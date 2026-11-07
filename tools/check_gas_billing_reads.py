"""Native billing metadata work and pinned-result checks in an owned preview."""
import json
from pathlib import Path
import subprocess
import time
from uuid import UUID, uuid4


def run_checks(query, manifest, owner, profile, request, dotnet, environment, owned_root):
    root = Path(owned_root).resolve()
    assert (root / '.openetrm-preview').is_file()
    assert Path(query('SHOW data_directory;')).resolve() == root / 'data'
    repository = Path(__file__).resolve().parents[1]
    catalog = str(UUID(manifest['catalogId']))
    frame = next(f for f in owner.call('workspace/navigation')['frames'] if f['id'] == request['context']['snapshotId'])
    fixture = root / 'gas-billing-read-work.json'
    prefix = 'synthetic_billing_work_' + uuid4().hex
    original = query("SELECT md5(coalesce(jsonb_agg(to_jsonb(c) ORDER BY catalog_id,revision_id,code),'[]')::text) FROM market_data.curves c;")
    settings_sql = """SELECT json_agg(row_to_json(t)) FROM(
      SELECT format('%I.%I',n.nspname,c.relname) relation,
        ARRAY(SELECT o FROM unnest(c.reloptions)o ORDER BY o) options
      FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
      WHERE c.relkind='r' AND n.nspname IN('security','trading','market_data','ref_data') ORDER BY n.nspname,c.relname)t;"""
    settings = json.loads(query(settings_sql))
    restore_settings = []
    for table in settings:
        option = next((o for o in table['options'] if o.startswith('autovacuum_enabled=')), None)
        assert option in (None, 'autovacuum_enabled=true', 'autovacuum_enabled=false')
        restore_settings.append('ALTER TABLE ' + table['relation'] + (' RESET(autovacuum_enabled);' if option is None else ' SET(' + option + ');'))

    def statistics():
        # Counter comparisons require unchanged planner inputs outside the one
        # deliberately enlarged relation, including underlying RLS dependencies.
        return query("""SELECT md5(jsonb_agg(to_jsonb(t) ORDER BY oid)::text) FROM(
          SELECT c.oid,c.reltuples,c.relpages,c.relallvisible,
            (SELECT jsonb_agg(to_jsonb(s) ORDER BY staattnum,stainherit) FROM pg_statistic s WHERE s.starelid=c.oid) statistics
          FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
          WHERE c.relkind='r' AND n.nspname IN('security','trading','market_data','ref_data')
          AND c.oid<>'market_data.curves'::regclass)t;""")

    def measure(mode, baseline=None):
        body = dict(profile=profile, frame=frame, request=request, planMode=mode)
        if baseline:
            body.update(context=baseline['context'], expected=baseline['data'])
        fixture.write_text(json.dumps(body))
        result = subprocess.run([str(dotnet), 'run', '--no-restore', '--project', 'tests/OpenEtrm.Api.Checks', '--',
                                 '--gas-billing-read-work', str(fixture)], cwd=repository, env=environment,
                                capture_output=True, text=True, timeout=120)
        assert result.returncode == 0, result.stdout + result.stderr
        return json.loads(Path(str(fixture) + '.result.json').read_text())

    modes = ('force_custom_plan', 'force_generic_plan')

    def grow():
        # Add only unrelated synthetic definition rows, never rewrite a source.
        # The owned fault bypasses immutable-publication triggers, not read RLS.
        query(f"""BEGIN;SET LOCAL session_replication_role=replica;
          INSERT INTO market_data.curves SELECT(jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||
            jsonb_build_object('code','{prefix}_'||n,'name','Synthetic unrelated billing curve '||n))).*
          FROM(SELECT * FROM market_data.curves WHERE catalog_id='{catalog}' ORDER BY revision_id,code LIMIT 1)c
          CROSS JOIN generate_series(1,512)n;COMMIT;""")
    def clean():
        query(f"BEGIN;SET LOCAL session_replication_role=replica;DELETE FROM market_data.curves WHERE catalog_id='{catalog}' AND starts_with(code,'{prefix}_');COMMIT;")
        assert query("SELECT md5(coalesce(jsonb_agg(to_jsonb(c) ORDER BY catalog_id,revision_id,code),'[]')::text) FROM market_data.curves c;") == original

    try:
        # Freeze only this owned fixture's table maintenance, not read authority
        # or its five-second deadline. Restore each original option in finally.
        query('BEGIN;' + ''.join('ALTER TABLE ' + t['relation'] + ' SET(autovacuum_enabled=false);' for t in settings) + 'COMMIT;')
        deadline = time.monotonic() + 15
        while query("SELECT count(*) FROM pg_stat_activity WHERE backend_type='autovacuum worker' AND datname=current_database();") != '0':
            assert time.monotonic() < deadline, 'Owned table maintenance did not finish'
            time.sleep(.1)
        baseline = {mode: measure(mode) for mode in modes}
        for layout in ('fresh_growth', 'analyzed_growth'):
            if layout == 'analyzed_growth':
                query('VACUUM(ANALYZE);')
                # Compare growth under the same maintenance state. ANALYZE of
                # other relations can independently change their native plans.
                baseline = {mode: measure(mode, baseline[mode]) for mode in modes}
            held_statistics = statistics()
            grow()
            if layout == 'analyzed_growth':
                query('VACUUM(ANALYZE) market_data.curves;')
            for mode in modes:
                measured = measure(mode, baseline[mode])
                assert statistics() == held_statistics, 'Non-curve planner statistics changed during the billing work comparison'
                before, after = baseline[mode]['tables'], measured['tables']
                # Stale small-table estimates may still choose one raw curve scan.
                # Unrelated rows must not fan out the authorized metadata chain.
                key = 'market_data.curves'
                assert after.get(key, 0) <= before.get(key, 0) + 512, (layout, mode, before, after)
                before_bound = baseline[mode]['tuples'] - before.get(key, 0)
                after_bound = measured['tuples'] - after.get(key, 0)
                assert after_bound <= before_bound + 128, (layout, mode, before, after)
                for table, tuples in after.items():
                    if table != key:
                        assert tuples <= before.get(table, 0) + 128, (layout, mode, table, before.get(table, 0), tuples)
                print(f"PASS managed billing metadata: {layout}/{mode}, same certified result and order; "
                      f"curve tuples {before.get(key, 0)}/{after.get(key, 0)}, "
                      f"all metadata tuples {baseline[mode]['tuples']}/{measured['tuples']}", flush=True)
            clean()
    finally:
        try:
            clean()
        finally:
            query('BEGIN;' + ''.join(restore_settings) + 'COMMIT;')
            assert json.loads(query(settings_sql)) == settings, 'Owned table maintenance settings were not restored'
