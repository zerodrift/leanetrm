"""Check statistical clock custody with an issuer and an independently bound runtime."""
from contextlib import contextmanager
import json
import subprocess
from uuid import UUID

from assistant_context import Contexts


@contextmanager
def runtime_session(psql, environment):
    # Keep this exact backend across rollback and retry so a PID-only proof fails the check.
    with subprocess.Popen(psql, env=environment, text=True, stdin=subprocess.PIPE,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT) as process:
        def run(sql):
            process.stdin.write(sql + '\n\\echo clock_check_done\n')
            process.stdin.flush()
            lines = []
            for line in process.stdout:
                if line.rstrip() == 'clock_check_done':
                    return '\n'.join(lines).strip()
                lines.append(line.rstrip())
            raise AssertionError('Clock fixture SQL failed\n' + '\n'.join(lines))

        try:
            run("SET statement_timeout='10s'; SET SESSION AUTHORIZATION preview_runtime;")
            yield run
        finally:
            process.stdin.close()


def run_checks(query, manifest, psql, environment):
    catalog, tenant, workspace = (str(UUID(manifest[k])) for k in ('catalogId', 'tenantId', 'workspaceId'))
    # Take the exact calendar and clock that a real statistical job already used in this preview.
    example = query(f"""SELECT jsonb_build_array(t.effective_on,t.id,h.calendar,h.clock)
        FROM risk.job_history j JOIN risk.history_clock_captures h
          ON (h.catalog_id,h.id)=(j.catalog_id,j.clock_capture_id)
        JOIN market_data.temporal_contexts t ON (t.catalog_id,t.id)=(h.catalog_id,h.context_id)
        WHERE h.catalog_id='{catalog}' ORDER BY j.job_id LIMIT 1;""")
    assert example, 'Run the parametric risk fixture before the workspace clock check'
    day, saved_id, calendar, clock = json.loads(example)

    # Obtain a new certified cut in a separate transaction, exactly as the page does.
    issued = Contexts(query, manifest).runtime('risk', f"""SELECT row_to_json(c) FROM
        security.workspace_read_cut('{catalog}',DATE '{day}',NULL,false) c;""")
    cut = json.loads(issued)
    assert cut['stored'] is False and cut['id'] != saved_id
    identifier = str(UUID(cut['id']))
    bind = (f"SELECT security.bind_workspace_read('{catalog}','{identifier}',DATE '{day}',"
            f"'{cut['known_at']}',{int(cut['known_through'])},'{int(cut['system_id'])}',{int(cut['timeline'])});")

    def literal(value):
        return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"

    def inputs(source, rules):
        return f"'{catalog}','{source}',{literal(calendar)},{literal(rules)}"

    exact = inputs(identifier, clock)
    changed = inputs(identifier, {**clock, 'tzdbVersion': 'synthetic_other_version'})
    capture = f'SELECT risk.capture_workspace_clock({exact});'

    def refuse(arguments, expected):
        return f"""DO $test$ BEGIN PERFORM risk.capture_workspace_clock({arguments});
            RAISE EXCEPTION 'Expected clock capture refusal';
            EXCEPTION WHEN insufficient_privilege THEN IF SQLERRM<>'{expected}' THEN RAISE; END IF;
            END $test$;"""

    def issue(address, arguments):
        pid, xid = address
        query(f"""SET SESSION AUTHORIZATION preview_authenticator;
            SELECT risk.attest_workspace_clock({pid},'{xid}'::xid8,{arguments});""")

    def pending(address):
        pid, xid = address
        return int(query(f"""SELECT count(*) FROM risk.pending_clock_attestations
            WHERE backend_pid={pid} AND transaction_id='{xid}'::xid8;"""))

    def source_counts():
        tables = [('market_data.temporal_contexts', 'id'), ('market_data.market_contexts', 'context_id'),
                  ('market_data.catalogue_contexts', 'context_id'), ('trading.contexts', 'context_id'),
                  ('risk.history_clock_captures', 'context_id')]
        counts = [f"(SELECT count(*) FROM {table} WHERE catalog_id='{catalog}' AND {key}='{identifier}')"
                  for table, key in tables]
        return [int(n) for n in query('SELECT ' + ','.join(counts)).split('|')]

    with runtime_session(psql, environment) as runtime:
        def begin():
            token = query(f"""SET SESSION AUTHORIZATION preview_authenticator;
                SELECT security.issue_context('https://preview.invalid','risk','human_interactive',
                    '{tenant}','{workspace}',NULL,'preview_runtime',60);""")
            response = runtime(f"BEGIN;SELECT security.use_context('{token}');{bind}"
                               'SELECT pg_backend_pid(),pg_current_xact_id();')
            return tuple(map(int, response.split('|')))

        first = begin()
        issue(first, exact)
        runtime(refuse(changed, 'risk_capture_issuer'))
        receipt = str(UUID(runtime(capture)))
        assert runtime(f"SELECT id FROM market_data.temporal_contexts WHERE catalog_id='{catalog}' AND id='{identifier}';") == identifier
        assert source_counts() == [0] * 5, 'A separate connection saw the source before its save committed'
        # Attempt a real job save without its required input frame.
        failure = runtime(f"""\\set ON_ERROR_STOP off
            SELECT risk.submit('{catalog}',gen_random_uuid(),'{saved_id}',NULL,NULL,'{{}}','{{}}');
            \\echo JOB_REFUSAL :SQLSTATE
            ROLLBACK;
            \\set ON_ERROR_STOP on""")
        assert 'JOB_REFUSAL P0001' in failure and 'temporal_inputs' in failure, failure
        assert source_counts() == [0] * 5 and pending(first) == 1

        second = begin()
        assert first[0] == second[0] and first[1] != second[1]
        runtime(refuse(exact, 'risk_capture_issuer'))
        issue(second, inputs(saved_id, clock))
        assert pending(first) == 0, 'The issuer left behind the rolled-back transaction’s signature'
        runtime(refuse(exact, 'risk_capture_issuer'))
        runtime('ROLLBACK;')

        expired = begin()
        issue(expired, exact)
        # Expire only this synthetic request, inside a transaction that rolls back.
        runtime("""RESET SESSION AUTHORIZATION;
            UPDATE security.request_contexts SET expires_at=clock_timestamp()-interval '1 second'
             WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id();
            SET SESSION AUTHORIZATION preview_runtime;""")
        runtime(refuse(exact, 'risk_capture_input'))
        runtime('ROLLBACK;')
        assert source_counts() == [0] * 5

        final = begin()
        issue(final, exact)
        assert pending(second) == pending(expired) == 0
        saved = str(UUID(runtime(capture)))
        runtime(refuse(exact, 'risk_capture_issuer'))
        runtime('SELECT security.finish_workspace_read(); COMMIT;')

    assert source_counts() == [1] * 5 and pending(final) == 0
    assert query(f"""SELECT EXISTS(SELECT 1 FROM risk.history_clock_captures h
        JOIN market_data.temporal_contexts t ON (t.catalog_id,t.id)=(h.catalog_id,h.context_id)
        WHERE (h.catalog_id,h.context_id,h.id)=('{catalog}','{identifier}','{saved}') AND h.calendar={literal(calendar)}
        AND h.clock={literal(clock)} AND (t.effective_on,t.known_at,t.known_through)=
        (DATE '{day}','{cut['known_at']}'::timestamptz,{int(cut['known_through'])}));""") == 't'
    assert query(f"SELECT count(*) FROM risk.history_clock_captures WHERE id='{receipt}';") == '0'
    print('PASS workspace clock requires the exact issuer evidence, transaction, source and current authority; '
          'a failed save retains no inputs and its signature is reclaimed', flush=True)
