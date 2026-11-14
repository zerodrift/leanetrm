"""An authorized provider envelope must fit its encoded UTF-8 response budget."""
from pathlib import Path
from check_reference_editing import main
from check_gas_owned_reconciliation_writes import checks as baseline
from gas_owned_reconciliation_seal import upgrade_checks


def checks(query,manifest):
    baseline(query,manifest)
    catalog,tenant,workspace=(manifest[key] for key in ('catalogId','tenantId','workspaceId'))
    signature='security.read_gas_owned_operations(uuid,uuid,timestamptz,uuid)'
    original=query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    assert original.count('security.read_gas_owned_operations(')==1
    cloned=original.replace('security.read_gas_owned_operations(','security.synthetic_gas_choice_operations(',1)
    query(cloned)
    query('ALTER FUNCTION security.synthetic_gas_choice_operations(uuid,uuid,timestamptz,uuid) OWNER TO openetrm_security_owner;'
          'REVOKE ALL ON FUNCTION security.synthetic_gas_choice_operations(uuid,uuid,timestamptz,uuid) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;')
    try:
        # These unused, bounded documents alter only the read provider. Their
        # control-character escaping is valid JSON and changes no financial row.
        query("""CREATE OR REPLACE FUNCTION security.read_gas_owned_operations(catalog uuid,context uuid,as_of timestamptz,account uuid) RETURNS jsonb
          LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $fixture$
          DECLARE result jsonb;extra jsonb;BEGIN
           result=security.synthetic_gas_choice_operations(catalog,context,as_of,account);
           SELECT jsonb_agg((result->'evidence'->0)||jsonb_build_object('id',md5('synthetic_encoded_document_'||n)::uuid,
            'source_key','synthetic_encoded_document_'||n,'revision',1,'previous_id',NULL,'evidence',repeat(chr(1),2000))) INTO extra FROM generate_series(1,1500)n;
           RETURN jsonb_set(result,'{evidence}',(result->'evidence')||extra);
          END$fixture$;""")
        def call(sql):
            token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
            return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
        context=call(f"SELECT security.capture_date_context('{catalog}','2020-12-20',NULL);")
        account=query(f"SELECT account_id FROM trading.gas_owned_quantity_corrections WHERE catalog_id='{catalog}' ORDER BY publication_order LIMIT 1;")
        bound=f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','2020-12-20T16:00:00Z');"
        sizes=call(bound+f"WITH source AS(SELECT security.read_gas_owned_operations('{catalog}','{context}','2020-12-20T16:00:00Z','{account}') value) SELECT(pg_column_size(value)<16777216 AND octet_length(convert_to(value::text,'UTF8'))>16777216)::text FROM source;")
        assert sizes=='true','The synthetic provider must fit binary size while exceeding the encoded response budget.'
        try:call(bound+f"SELECT security.read_gas_owned_quantity_choices('{catalog}','{context}','2020-12-20T16:00:00Z','{account}')::text;")
        except RuntimeError as error:assert 'gas_owned_scope' in str(error),str(error)
        else:raise AssertionError('The ordinary choice reader accepted an encoded envelope larger than 16 MiB.')
    finally:
        query(original)
        query('DROP FUNCTION security.synthetic_gas_choice_operations(uuid,uuid,timestamptz,uuid);')
    print('PASS ordinary choice encoded envelope: escaped source documents exceed UTF-8 budget and refuse without truncation or financial mutation',flush=True)


if __name__=='__main__':main(checks=checks,before_migration=upgrade_checks)
