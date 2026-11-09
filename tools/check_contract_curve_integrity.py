"""Native sealing checks through ordinary Editor writes and Approver admission authority."""
from uuid import uuid4


def run_checks(query, manifest):
    cat, original, rcat, rrev, tenant, workspace = [manifest[key] for key in
        ('catalogId', 'revisionId', 'referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId')]

    def runtime(sql, actor='editor'):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                      f"'https://preview.invalid','{actor}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
        return query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');{sql}COMMIT;")

    def draft(*, wrong_window=False, reversed_positions=False):
        revision = str(uuid4())
        selection = ("futures_series_code BETWEEN 'test_gas_202103' AND 'test_gas_202105'" if wrong_window else
                     f"id IN(SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{cat}','{original}','rolling_gas'))")
        runtime(f"""
INSERT INTO market_data.revisions VALUES('{cat}','{revision}','{rcat}','{rrev}','draft',0);
INSERT INTO market_data.contracts SELECT(jsonb_populate_record(NULL::market_data.contracts,to_jsonb(k)||jsonb_build_object('revision_id','{revision}'))).*
 FROM market_data.contracts k WHERE(catalog_id,revision_id)=('{cat}','{original}') AND {selection};
INSERT INTO market_data.series SELECT(jsonb_populate_record(NULL::market_data.series,to_jsonb(s)||jsonb_build_object('revision_id','{revision}'))).*
 FROM market_data.series s WHERE(catalog_id,revision_id)=('{cat}','{original}')
 AND contract_id IN(SELECT id FROM market_data.contracts WHERE(catalog_id,revision_id)=('{cat}','{revision}'));
INSERT INTO market_data.curves SELECT(jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||jsonb_build_object('revision_id','{revision}'))).*
 FROM market_data.curves c WHERE(catalog_id,revision_id,code)=('{cat}','{original}','rolling_gas');
INSERT INTO market_data.curve_members SELECT catalog_id,revision_id,'rolling_gas',id,
 (row_number() OVER(ORDER BY delivery_from {'DESC' if reversed_positions else 'ASC'})-1)::int
 FROM market_data.contracts WHERE(catalog_id,revision_id)=('{cat}','{revision}');
INSERT INTO market_data.revision_periods VALUES('{cat}','{revision}','2020-01-01','2022-01-01');
""")
        return revision

    def admissions(revision):
        # Identical absolute-list occurrences may already have been verified on different days. Reuse their evidence.
        for predicate, day in (('a.ordinal=0', '2021-01-14'), ('a.ordinal>0', '2021-01-15')):
            context = runtime(f"SELECT security.capture_date_context('{cat}','{day}',NULL);")
            runtime(f"""INSERT INTO market_data.contract_date_admissions
 (catalog_id,id,revision_id,contract_id,mapping_id,mapping_version,ordinal,context_id,actor_id)
 SELECT a.catalog_id,gen_random_uuid(),'{revision}',a.contract_id,a.mapping_id,a.mapping_version,a.ordinal,'{context}',
 security.custom_curve_actor(a.catalog_id,'WriteMarketDefinitions')
 FROM market_data.contract_date_admissions a JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=('{cat}','{original}','rolling_gas')
 JOIN market_data.curve_definition_versions v ON(v.catalog_id,v.definition_id,v.version)=(c.catalog_id,c.definition_id,c.definition_version)
 WHERE(a.catalog_id,a.revision_id,a.mapping_id,a.mapping_version)=(c.catalog_id,c.revision_id,v.mapping_definition_id,v.mapping_version)
 AND {predicate};""")

    def refused(revision):
        runtime(f"""DO $$BEGIN BEGIN
 PERFORM security.seal_market_revision('{cat}','{revision}');
 RAISE EXCEPTION 'A pinned curve without exact positional evidence was sealed';
 EXCEPTION WHEN check_violation THEN
  IF SQLERRM<>'Pinned contract curves require exact date admissions at every mapping position' THEN RAISE;END IF;
 END;END$$;""", 'approver')
        assert runtime(f"SELECT state FROM market_data.revisions WHERE(catalog_id,id)=('{cat}','{revision}');") == 'draft'

    wrong = draft(wrong_window=True)
    assert runtime(f"SELECT count(*) FROM market_data.contract_date_admissions WHERE(catalog_id,revision_id)=('{cat}','{wrong}');") == '0'
    refused(wrong)
    reordered = draft(reversed_positions=True)
    admissions(reordered)
    refused(reordered)
    valid = draft()
    admissions(valid)
    runtime(f"SELECT security.seal_market_revision('{cat}','{valid}');", 'approver')
    assert runtime(f"SELECT state FROM market_data.revisions WHERE(catalog_id,id)=('{cat}','{valid}');") == 'sealed'
    print('PASS pinned contract curves: runtime wrong-window and reordered-member sealing refuse; exact verified positions seal with reusable evidence')
