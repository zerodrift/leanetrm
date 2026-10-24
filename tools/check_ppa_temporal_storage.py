"""Independent certified-cutoff checks for the bounded PPA readers."""
import json
from uuid import uuid4


def run_checks(query, manifest):
    catalog, definition, reference, ref_revision, tenant, workspace = [manifest[k] for k in
        ('catalogId', 'revisionId', 'referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId')]
    valuation = '2020-12-15T16:00:00Z'
    passed = 0

    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS PPA temporal storage: ' + name, flush=True)

    def runtime(sql):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                      f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
        return 'BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;' + f"SELECT security.use_context('{token}');" + sql

    def reject(sql, code):
        try:
            query(runtime(sql) + 'ROLLBACK;')
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Accepted invalid PPA temporal read: ' + code)

    def capture(day='2020-12-15'):
        return query(runtime(f"SELECT security.capture_date_context('{catalog}','{day}',NULL);") + 'COMMIT;')

    context = capture()

    def guard(pins, selected=context, at=valuation):
        ids = ','.join("'" + p + "'::uuid" for p in pins)
        return (f"SELECT security.ppa_require_market('{catalog}','{definition}','{reference}','{ref_revision}',"
                f"'{selected}','{at}',ARRAY[{ids}]::uuid[]);")

    query(runtime(guard(manifest['frames'][0]['observationIds'])) + 'ROLLBACK;')
    check('complete original frame pins admit under their exact certified context')
    reject(guard([], at='2020-12-16T16:00:00Z'), 'knowledge_coverage')
    check('a certified context cannot be reused for a different effective date')
    outside = capture('2023-01-01')
    reject(guard([], outside, '2023-01-01T16:00:00Z'), 'ppa_definition_revision')
    check('publication does not authorize a definition outside its exact effective period')

    late = str(uuid4())
    old = manifest['intervalCurves']['observations'][0]
    query(f"INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||"
          f"jsonb_build_object('id','{late}','source_event_id','synthetic_temporal_{late}','corrects_id',NULL,"
          "'received_at',clock_timestamp(),'value',100))).* FROM market_data.observations o "
          f"WHERE o.id='{old}';")
    later_context = capture()
    reject(guard([late]), 'ppa_observation_knowledge')
    query(runtime(guard([late], later_context)) + 'ROLLBACK;')
    check('a later committed observation refuses the old cutoff and admits only a later eligible cutoff')
    reject(guard([str(uuid4())]), 'ppa_observation_knowledge')
    check('an absent selected pin cannot fall back to another observation')

    def discount(selected):
        return (f"SELECT security.ppa_discount_default('{catalog}','{reference}','{ref_revision}','usd',"
                f"'{definition}','{valuation}','{selected}');")

    old_mapping = json.loads(query(runtime(discount(context)) + 'ROLLBACK;'))
    later_mapping = str(uuid4())
    # The alternate identity uses the same exact curve and policy. A committed
    # later publication still cannot replace the mapping at the retained cutoff.
    insert_default = (f"INSERT INTO trading.publications SELECT '{catalog}',max(publication_order)+1,pg_current_xact_id() "
        f"FROM trading.publications WHERE catalog_id='{catalog}';"
        "INSERT INTO trading.currency_discount_defaults SELECT(jsonb_populate_record(NULL::trading.currency_discount_defaults,to_jsonb(d)||"
        f"jsonb_build_object('id','{later_mapping}','effective_at','2020-12-15T15:00:00Z','publication_order',"
        f"(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}')))).* "
        f"FROM trading.currency_discount_defaults d WHERE d.id='{old_mapping['id']}';")
    query('BEGIN;' + insert_default + 'COMMIT;')
    actual = json.loads(query(runtime(discount(context)) + 'ROLLBACK;'))
    check('explicit trading knowledge preserves the selected default against a later publication', actual == old_mapping)
    current_mapping = json.loads(query(runtime(discount(capture())) + 'ROLLBACK;'))
    check('a later certified cutoff selects the explicitly later mapping identity', current_mapping['id'] == later_mapping)

    def tou(requested):
        encoded = json.dumps(requested).replace("'", "''")
        return (f"SELECT security.ppa_tou_versions('{catalog}','{definition}','{reference}','{ref_revision}',"
                f"'{context}','{valuation}','{encoded}'::jsonb);")

    proven = query(runtime(tou([{'id': manifest['intervalCurves']['pjm'], 'version': 1}])) + 'ROLLBACK;')
    check('an eligible sealed bundle proves its immutable exact published TOU version',
          any(json.loads(row)['product_id'] == manifest['intervalCurves']['pjm'] for row in proven.splitlines()))
    reject(tou([{'id': manifest['intervalCurves']['nyiso'], 'version': 1}]), 'ppa_tou_knowledge')
    check('an unproven direct-profile TOU refuses without audit-time inference')
    check('unrelated unproven TOU versions do not poison the selected bundle',
          query(runtime(tou([])) + 'ROLLBACK;') == proven)
    for table, scoped_catalog in [('market_catalog_scopes', catalog), ('reference_catalog_scopes', reference)]:
        revoke = (f"DELETE FROM security.{table} WHERE catalog_id='{scoped_catalog}' "
                  f"AND tenant_id='{tenant}' AND workspace_id='{workspace}';")
        try:
            query('BEGIN;' + revoke + runtime(guard([])).removeprefix('BEGIN;') + 'ROLLBACK;')
        except RuntimeError as error:
            assert '42501' in str(error) or 'No current' in str(error), str(error)
        else:
            raise AssertionError('Revoked authority admitted explicit temporal PPA pins')
        check('current ' + table + ' authority precedes retained pin admission')
    return passed
