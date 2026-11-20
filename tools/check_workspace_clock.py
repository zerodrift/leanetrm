"""Check statistical clock custody with an issuer and an independently bound runtime."""
from contextlib import contextmanager
import json
import subprocess
from uuid import UUID,uuid4

from assistant_context import Contexts


def capture_fixture_clock(query,manifest,source,as_of,calendar,clock):
    """Create real clock evidence for an explicitly selected synthetic source.

    The disposable database owner acts as the isolated issuer and runtime in
    sequence. run_checks below qualifies their real two-connection protocol.
    """
    catalog=manifest['catalogId']
    token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
        f"'https://preview.invalid','risk','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    literal=lambda value:"'"+json.dumps(value).replace("'","''")+"'::jsonb"
    arguments=f"'{catalog}','{source['id']}',{literal(calendar)},{literal(clock)}"
    from native_read_transport import select_owned_inputs
    selection=select_owned_inputs(catalog,source)
    return query(f"""SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');
        {selection}
        SELECT security.use_market_context('{catalog}','{source['id']}');
        SELECT security.use_trading_context('{catalog}','{as_of}','{source['effective_on']}'::date);
        SET SESSION AUTHORIZATION preview_authenticator;
        SELECT risk.attest_workspace_clock(pg_backend_pid(),pg_current_xact_id(),{arguments}) AS issued_clock_attestation \\gset
        SET SESSION AUTHORIZATION preview_runtime;SELECT risk.capture_workspace_clock({arguments},:'issued_clock_attestation'::uuid);COMMIT;""")


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
    example = query(f"""SELECT jsonb_build_array(h.input_business_on,h.context_id,h.calendar,h.clock)
        FROM risk.job_history j JOIN risk.history_clock_captures h
          ON (h.catalog_id,h.id)=(j.catalog_id,j.clock_capture_id)
        WHERE h.catalog_id='{catalog}' ORDER BY j.job_id LIMIT 1;""")
    assert example, 'Run the parametric risk fixture before the workspace clock check'
    day, saved_id, calendar, clock = json.loads(example)

    # Obtain a new certified cut in a separate transaction, exactly as the page does.
    issued = Contexts(query, manifest).runtime('risk', f"""SELECT row_to_json(c) FROM
        security.resolve_read_cut('{catalog}',DATE '{day}',NULL,false) c;""")
    cut = json.loads(issued)
    assert cut['id'] != saved_id
    identifier = str(UUID(cut['id']))
    bind = (f"SELECT security.bind_operation_read('{catalog}','{identifier}',DATE '{day}',"
            f"'{cut['known_at']}',{int(cut['known_through'])},'{int(cut['system_id'])}',{int(cut['timeline'])});")

    def literal(value):
        return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"

    def inputs(source, rules):
        return f"'{catalog}','{source}',{literal(calendar)},{literal(rules)}"

    exact = inputs(identifier, clock)
    changed = inputs(identifier, {**clock, 'tzdbVersion': 'synthetic_other_version'})
    def capture(attestation):return f"SELECT risk.capture_workspace_clock({exact},'{attestation}');"

    def refuse(arguments, expected, attestation):
        return f"""DO $test$ BEGIN PERFORM risk.capture_workspace_clock({arguments},'{attestation}');
            RAISE EXCEPTION 'Expected clock capture refusal';
            EXCEPTION WHEN insufficient_privilege THEN IF SQLERRM<>'{expected}' THEN RAISE; END IF;
            END $test$;"""

    def issue(address, arguments):
        pid, xid = address
        return str(UUID(query(f"""SET SESSION AUTHORIZATION preview_authenticator;SET statement_timeout='3s';
            SELECT risk.attest_workspace_clock({pid},'{xid}'::xid8,{arguments});""")))

    def pending(address):
        pid, xid = address
        return int(query(f"""SELECT count(*) FROM risk.pending_clock_attestations
            WHERE backend_pid={pid} AND transaction_id='{xid}'::xid8;"""))

    def source_counts():
        tables = [('risk.history_clock_captures', 'context_id')]
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
        first_token=issue(first, exact)
        runtime(refuse(exact, 'risk_capture_issuer',uuid4()))
        runtime(refuse(changed, 'risk_capture_issuer',first_token))
        receipt = str(UUID(runtime(capture(first_token))))
        repeated_token=issue(first, exact)
        assert repeated_token!=first_token
        assert str(UUID(runtime(capture(repeated_token))))==receipt,'Identical clock facts created another business receipt'
        runtime(refuse(exact,'risk_capture_issuer',first_token))
        assert runtime(f"SELECT id FROM security.operation_read_inputs WHERE catalog_id='{catalog}' AND id='{identifier}';") == identifier
        assert source_counts() == [0], 'A separate connection saw the owner inputs before their save committed'
        # Attempt a real job save without its required input frame.
        failure = runtime(f"""\\set ON_ERROR_STOP off
            SELECT risk.submit('{catalog}',gen_random_uuid(),'{saved_id}',NULL,NULL,'{{}}','{{}}');
            \\echo JOB_REFUSAL :SQLSTATE
            ROLLBACK;
            \\set ON_ERROR_STOP on""")
        assert 'JOB_REFUSAL P0001' in failure and 'temporal_inputs' in failure, failure
        assert source_counts() == [0] and pending(first) == 2

        second = begin()
        assert first[0] == second[0] and first[1] != second[1]
        runtime(refuse(exact, 'risk_capture_issuer',first_token))
        wrong_source_token=issue(second, inputs(saved_id, clock))
        assert pending(first) == 0, 'The issuer left behind the rolled-back transaction’s signature'
        runtime(refuse(exact, 'risk_capture_issuer',wrong_source_token))
        runtime('ROLLBACK;')

        expired = begin()
        expired_token=issue(expired, exact)
        # Expire only this synthetic request, inside a transaction that rolls back.
        runtime("""RESET SESSION AUTHORIZATION;
            UPDATE security.request_contexts SET expires_at=clock_timestamp()-interval '1 second'
             WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id();
            SET SESSION AUTHORIZATION preview_runtime;""")
        runtime(refuse(exact, 'risk_capture_input',expired_token))
        runtime('ROLLBACK;')
        assert source_counts() == [0]

        final = begin()
        final_token=issue(final, exact)
        assert pending(second) == pending(expired) == 0
        saved = str(UUID(runtime(capture(final_token))))
        runtime(refuse(exact, 'risk_capture_issuer',final_token))
        next_token=issue(final,exact)
        assert next_token!=final_token
        assert str(UUID(runtime(capture(next_token))))==saved
        runtime(refuse(exact,'risk_capture_issuer',next_token))
        runtime('SELECT security.finish_operation_read(); COMMIT;')

    assert source_counts() == [1] and pending(final) == 0
    assert query(f"""SELECT EXISTS(SELECT 1 FROM risk.history_clock_captures h
        WHERE (h.catalog_id,h.context_id,h.id)=('{catalog}','{identifier}','{saved}') AND h.calendar={literal(calendar)}
        AND h.clock={literal(clock)} AND (h.input_business_on,h.input_known_at,h.input_system_id,h.input_timeline)=
        (DATE '{day}','{cut['known_at']}'::timestamptz,'{cut['system_id']}',{int(cut['timeline'])}));""") == 't'
    assert query(f"SELECT count(*) FROM risk.history_clock_captures WHERE id='{receipt}';") == '0'
    print('PASS workspace clock requires the exact one-use issuer token, evidence, transaction, source and current authority; '
          'repeated same-transaction capture never waits or duplicates facts; rollback retains no inputs and tokens are reclaimed', flush=True)
