"""Native managed point reads in an owned synthetic database; broad-read fault always restored."""
import json
from pathlib import Path
import subprocess
from uuid import UUID


def run_checks(query, manifest, dotnet, environment, owned_root):
    root=Path(owned_root).resolve()
    assert (root/'.openetrm-preview').is_file()
    catalog,revision,tenant,workspace=(str(UUID(manifest[k])) for k in ('catalogId','revisionId','tenantId','workspaceId'))
    pins=json.loads(query(f"SELECT json_agg(observation_id ORDER BY observation_id) FROM market_data.market_close_pins WHERE(catalog_id,close_id)=('{catalog}',(SELECT id FROM market_data.market_closes WHERE catalog_id='{catalog}' AND session_code='test_utc_close' AND business_date='2020-12-18' ORDER BY publication_order DESC LIMIT 1));"))
    context=query(f"SELECT id FROM market_data.temporal_contexts WHERE catalog_id='{catalog}' AND effective_on='2020-12-18' ORDER BY known_at,id LIMIT 1;")
    assert context and len(pins)>1
    selected="ARRAY["+','.join("'"+str(UUID(p))+"'::uuid" for p in pins)+"]"
    original=query("SELECT pg_get_functiondef('security.visible_observations()'::regprocedure);")
    legacy=(Path(__file__).resolve().parents[1]/'db/migrations/0202_regional_closes.sql').read_text()
    legacy='CREATE OR REPLACE FUNCTION security.visible_observations()'+legacy.split('CREATE OR REPLACE FUNCTION security.visible_observations()',1)[1].split('END$$;',1)[0]+'END$$;'
    samples=[]
    try:
        query(legacy)
        for retained,session in ((None,'test_utc_close'),(context,'test_utc_close'),(context,'test_late_close')):
            token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','viewer','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
            bind='' if retained is None else f"SELECT security.use_market_context('{catalog}','{retained}');"
            expected=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{bind}SELECT security.use_close_session('{catalog}','{session}');SELECT coalesce(json_agg(to_jsonb(t)||jsonb_build_object('value',t.value::text) ORDER BY t.observed_at,t.received_at,t.id),'[]')::text FROM market_data.observations t WHERE catalog_id='{catalog}' AND definition_revision_id='{revision}' AND id=ANY({selected});ROLLBACK;")
            samples.append(dict(contextId=retained,session=session,pins=pins,expectedJson=expected))
    finally:
        query(original)
    fixture=root/'exact-pin-reads.json';fixture.write_text(json.dumps(samples))
    def managed():
        try:
            query("CREATE OR REPLACE FUNCTION security.visible_observations() RETURNS TABLE(catalog uuid,revision uuid,observation uuid) LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$BEGIN RAISE EXCEPTION 'synthetic_broad_observation_scan';END$$;")
            result=subprocess.run([str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--exact-pin-reads',str(fixture)],cwd=Path(__file__).resolve().parents[1],env=environment,capture_output=True,text=True,timeout=120)
            assert result.returncode==0,result.stdout+result.stderr
        finally:
            query(original)
        print(result.stdout.strip(),flush=True)
    managed()
    stats = "SELECT 'CLOSE_CALLS|'||coalesce(sum(calls),0) FROM pg_stat_xact_user_functions WHERE schemaname='security' AND funcname='market_close_visible';"
    for mode in ('force_custom_plan','force_generic_plan'):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','viewer','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
        output=query(f"""SET track_functions='all';BEGIN;SET LOCAL plan_cache_mode={mode};SET LOCAL SESSION AUTHORIZATION preview_runtime;
          SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_close_session('{catalog}','test_utc_close');
          {stats}SELECT 'PIN_ROWS|'||count(*) FROM market_data.observations WHERE catalog_id='{catalog}' AND definition_revision_id='{revision}' AND id=ANY({selected});
          {stats}ROLLBACK;""").splitlines()
        calls=[int(line.split('|')[1]) for line in output if line.startswith('CLOSE_CALLS|')]
        rows=int(next(line.split('|')[1] for line in output if line.startswith('PIN_ROWS|')))
        assert rows>1 and calls[1]-calls[0]==1,dict(mode=mode,rows=rows,closeVisibilityCalls=calls[1]-calls[0])
        print(f'PASS observation read: {mode}, {rows} exact pins share one close visibility decision',flush=True)
    from check_exact_pin_authority import run_checks as authority
    authority(query,manifest,context,pins,legacy,original)
    managed()
    print('PASS later unrelated history does not add a broad scan to native exact-pin retrieval',flush=True)
