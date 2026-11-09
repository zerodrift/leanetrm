"""A display rename must remain usable with a populated futures reference catalog."""
import json
from uuid import uuid4
from check_reference_editing import main


def upgrade_checks(query, migration):
    function = 'ref_data.require_close_reproduces_milestones()'
    identity = f"'{function}'::regprocedure"
    attributes = f"SELECT (to_jsonb(p)-ARRAY['oid','prosrc'])::text FROM pg_proc p WHERE p.oid={identity};"
    before = json.loads(query(attributes))
    definition = query(f'SELECT pg_get_functiondef({identity});')
    for mutation in [definition.replace('RETURN NEW;', 'RETURN OLD;'),
                     f'ALTER FUNCTION {function} SET enable_seqscan=off;',
                     'ALTER FUNCTION ref_data.require_close_reproduces_milestones() OWNER TO openetrm_runtime;',
                     'REVOKE EXECUTE ON FUNCTION ref_data.require_close_reproduces_milestones() FROM PUBLIC;']:
        try:
            query('BEGIN;'+mutation+';'+migration.read_text())
        except RuntimeError as error:
            assert 'reference_close_validation_predecessor' in str(error), str(error)
        else:
            raise AssertionError('The migration accepted a changed predecessor')
        assert json.loads(query(attributes)) == before
        assert query(f'SELECT pg_get_functiondef({identity});') == definition
    # Qualify the real upgrade inside a rollback, including privileges and function
    # settings that CREATE OR REPLACE must retain. The runner then applies it once.
    assert json.loads(query(migration.read_text().rsplit('COMMIT;', 1)[0]+attributes+'ROLLBACK;')) == before
    print('PASS close-validation upgrade: drift refused; owner, grants and function settings retained', flush=True)


def run_checks(query, manifest, *, contracts=2000, products=1, extra_milestones=False, deadline_seconds=5):
    catalog, original, tenant, workspace = [manifest[k] for k in
        ('referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId')]
    source, operation = str(uuid4()), str(uuid4())
    tables = json.loads(query('SELECT to_json(security.reference_display_tables())::text;'))
    query(f"INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{catalog}','{source}','synthetic_display_scale');")
    for table in tables:
        query(f"""INSERT INTO ref_data.{table}
 SELECT copied.* FROM ref_data.{table} r CROSS JOIN LATERAL
 jsonb_populate_record(NULL::ref_data.{table},to_jsonb(r)||jsonb_build_object('revision_id','{source}')) copied
 WHERE r.catalog_id='{catalog}' AND r.revision_id='{original}';""")
    if products > 1:
        query(f"""INSERT INTO ref_data.futures_products
 SELECT copied.* FROM ref_data.futures_products r CROSS JOIN generate_series(0,{products-1}) n
 CROSS JOIN LATERAL jsonb_populate_record(NULL::ref_data.futures_products,to_jsonb(r)||jsonb_build_object('code','scale_product_'||n)) copied
 WHERE r.catalog_id='{catalog}' AND r.revision_id='{source}' AND r.code='test_gas_future';""")
    product = f"'scale_product_'||(n%{products})" if products > 1 else "'test_gas_future'"
    query(f"""
INSERT INTO ref_data.futures_series(catalog_id,revision_id,code,product_code,series_label,definition_state,delivery_from,delivery_to)
 SELECT '{catalog}','{source}','scale_'||n,{product},'Synthetic day '||n,'synthetic',
 DATE '2021-01-01'+n,DATE '2021-01-02'+n FROM generate_series(1,{contracts}) n;
""")
    if extra_milestones:
        query(f"""INSERT INTO ref_data.futures_milestones(catalog_id,revision_id,series_code,milestone,value_state)
 SELECT '{catalog}','{source}','scale_'||n,m,'unknown' FROM generate_series(1,{contracts}) n
 CROSS JOIN unnest(ARRAY['first_trading','first_notice','last_notice','final_settlement','delivery_start','delivery_end']) m;""")
    query(f"""
INSERT INTO ref_data.futures_milestones(catalog_id,revision_id,series_code,milestone,value_state,business_date,event_at,clock_code,source_code)
 SELECT '{catalog}','{source}','scale_'||n,'last_trading','known',DATE '2020-12-31'+n,
 (DATE '2020-12-31'+n+TIME '15:30') AT TIME ZONE 'UTC','test_utc','fixture' FROM generate_series(1,{contracts}) n;
""")
    close = f"""INSERT INTO ref_data.trading_close_times(catalog_id,revision_id,code,version,milestone,futures_product_code,local_time,clock_code,source_code)
 VALUES('{catalog}','{source}','scale_close',1,'last_trading','test_gas_future','15:30','test_utc','fixture');"""
    # One inconsistent instant among the populated product's otherwise matching
    # milestones must still refuse configuration; the failed block records nothing.
    query(f"""DO $$BEGIN
 INSERT INTO ref_data.futures_series(catalog_id,revision_id,code,product_code,series_label)
 VALUES('{catalog}','{source}','scale_bad','test_gas_future','Synthetic inconsistent milestone');
 INSERT INTO ref_data.futures_milestones(catalog_id,revision_id,series_code,milestone,value_state,business_date,event_at,clock_code,source_code)
 VALUES('{catalog}','{source}','scale_bad','last_trading','known','2025-01-02','2025-01-02 15:31Z','test_utc','fixture');
 {close}
 RAISE EXCEPTION 'Expected close_time_mismatch';
EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'close_time_mismatch' THEN RAISE;END IF;END$$;""")
    configured = f"""INSERT INTO ref_data.trading_close_times(catalog_id,revision_id,code,version,milestone,futures_product_code,local_time,clock_code,source_code)
 SELECT '{catalog}','{source}','scale_close_'||n,1,'last_trading','scale_product_'||n,'15:30','test_utc','fixture'
 FROM generate_series(0,{products-1}) n;""" if products > 1 else close
    query(f"""{configured}
UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{catalog}' AND id='{source}';
ANALYZE ref_data.futures_series; ANALYZE ref_data.futures_milestones;
""")
    # Exercise the route's save + apply transaction under the same actor, rather
    # than committing an unapplied draft in between.
    query("INSERT INTO security.role_permissions SELECT id,'SealReferenceData' FROM security.roles WHERE code='market_data_editor';")
    try:
        def commit():
            token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
            return json.loads(query(f"""SET SESSION AUTHORIZATION preview_runtime;BEGIN;
SELECT security.use_context('{token}'); SET LOCAL statement_timeout='{deadline_seconds}s';
DO $$BEGIN PERFORM security.save_reference_display_edit('{catalog}','{source}','{source}','{operation}','commodities','test_gas','{{"name":"Natural gas"}}');END$$;
SELECT security.apply_reference_display('{catalog}','{source}','{operation}','{operation}',0)::text;
COMMIT;"""))
        assert commit() == {'displayRevisionId': operation, 'version': 1}
        assert commit() == {'displayRevisionId': operation, 'version': 1}, 'The retained request must replay its first outcome'
        assert query(f"SELECT count(*) FROM presentation.reference_display_versions WHERE catalog_id='{catalog}' AND source_revision_id='{source}';") == '1'
    finally:
        query("DELETE FROM security.role_permissions WHERE permission='SealReferenceData' AND role_id=(SELECT id FROM security.roles WHERE code='market_data_editor');")
    for table in tables:
        change = "CASE WHEN r.code='test_gas' THEN jsonb_build_object('name','Natural gas') ELSE '{}'::jsonb END" if table == 'commodities' else "'{}'::jsonb"
        assert query(f"""SELECT
 (SELECT jsonb_agg(value ORDER BY value::text COLLATE "C") FROM
  (SELECT (to_jsonb(r)-'revision_id')||{change} value FROM ref_data.{table} r WHERE catalog_id='{catalog}' AND revision_id='{source}') expected)
 IS NOT DISTINCT FROM
 (SELECT jsonb_agg(value ORDER BY value::text COLLATE "C") FROM
  (SELECT to_jsonb(r)-'revision_id' value FROM ref_data.{table} r WHERE catalog_id='{catalog}' AND revision_id='{operation}') actual);""") == 't', table+' changed beyond the requested label'
    print(f'PASS display rename: {contracts} added daily contracts, {products} close rules, save/apply and exact retry within the {deadline_seconds}-second statement deadline; only the requested label changes', flush=True)


if __name__ == '__main__':
    main(checks=run_checks)
