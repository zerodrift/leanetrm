"""Native billing metadata work and pinned-result checks in an owned preview."""
import json
from pathlib import Path
import subprocess
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
    baseline = {mode: measure(mode) for mode in modes}

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
        for layout in ('fresh_growth', 'analyzed_growth'):
            if layout == 'analyzed_growth':
                for table in ('trading.gas_trade_revisions', 'trading.gas_component_terms', 'trading.gas_delivery_obligations',
                              'market_data.gas_valuation_assignments', 'market_data.curves',
                              'ref_data.quote_specifications', 'ref_data.currencies'):
                    query('VACUUM(ANALYZE) ' + table + ';')
                # Compare growth under the same maintenance state. ANALYZE of
                # other relations can independently change their native plans.
                baseline = {mode: measure(mode, baseline[mode]) for mode in modes}
            grow()
            if layout == 'analyzed_growth':
                query('VACUUM(ANALYZE) market_data.curves;')
            for mode in modes:
                measured = measure(mode, baseline[mode])
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
        clean()
