"""Exact curve/source storage checks in the launcher's owned synthetic database."""
import json
from uuid import UUID, uuid4


def prepare_market(market, manifest):
    """Add explicit physical-price sources while the original model is still draft."""
    catalog, revision, reference, ref_revision = [str(UUID(manifest[k])) for k in
        ('catalogId', 'revisionId', 'referenceCatalogId', 'referenceRevisionId')]
    sources = {
        'pjm': {'curve': 'local_power_all_in', 'sourceId': str(uuid4()), 'clock': 'synthetic_eastern'},
        'ercot': {'curve': 'hub_power', 'sourceId': str(uuid4()), 'clock': 'synthetic_central'},
    }
    statements = []
    for provider, dataset, timing, minutes, key in (
            ('PJM', 'da_hrl_lmps', 'DA', 60, 'pjm'),
            ('ERCOT', 'np6-905-cd/spp_node_zone_hub', 'RT', 15, 'ercot')):
        source = sources[key]
        statements.append(f"""
          INSERT INTO power_data.sources
            (id,catalog_id,name,provider,dataset,market,point,minutes,reference_catalog_id,reference_revision_id,
             location_code,clock_code,currency_code,quantity_code)
          SELECT '{source['sourceId']}',c.catalog_id,'Synthetic {provider} settlement source','{provider}',
            '{dataset}','{timing}','synthetic_{key}',{minutes},c.reference_catalog_id,c.reference_revision_id,
            c.location_code,'{source['clock']}',q.currency_code,q.quantity_code
          FROM market_data.curves c JOIN ref_data.quote_specifications q
            ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
          WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{revision}','{source['curve']}');
          INSERT INTO market_data.curve_physical_price_sources
            (catalog_id,revision_id,curve_code,reference_catalog_id,reference_revision_id,source_id)
          VALUES('{catalog}','{revision}','{source['curve']}','{reference}','{ref_revision}','{source['sourceId']}');
        """)
    marker = 'UPDATE market_data.revisions SET'
    if market.count(marker) != 1:
        raise ValueError('The synthetic fixture must have exactly one model sealing statement')
    manifest['powerActual'] = sources
    return market.replace(marker, '\n'.join(statements) + '\n' + marker, 1)


def run_checks(query, manifest):
    catalog, revision, reference, ref_revision, tenant, workspace = [str(UUID(manifest[k])) for k in
        ('catalogId', 'revisionId', 'referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId')]
    selected = manifest['powerActual']['pjm']
    source, curve = str(UUID(selected['sourceId'])), selected['curve']
    passed = 0

    def check(name, condition):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS power actual storage: ' + name, flush=True)

    def reject(sql, state):
        try:
            query('\\set VERBOSITY verbose\n' + sql)
        except RuntimeError as error:
            assert state in str(error), str(error)
        else:
            raise AssertionError('Storage accepted invalid physical source terms')

    def issue(profile='viewer'):
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")

    def runtime(token, sql):
        return f"SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');{sql}"

    count = f"SELECT count(*) FROM market_data.curve_physical_price_sources WHERE catalog_id='{catalog}' AND revision_id='{revision}';"
    check('price history has a leading source and interval range index', query("""
      SELECT i.indisvalid AND i.indisready AND
        ARRAY(SELECT a.attname::text FROM unnest(i.indkey) WITH ORDINALITY k(attnum,ordinal)
          JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum ORDER BY k.ordinal)
        =ARRAY['source_id','starts_at']
      FROM pg_index i WHERE i.indexrelid='power_data.prices_source_interval'::regclass;
    """) == 't')
    check('the authorized history reader bounds both interval endpoints', query("""
      SELECT position('p.starts_at<last_at' IN pg_get_functiondef(
        'power_data.read_prices(uuid,timestamptz,timestamptz,timestamptz)'::regprocedure))>0;
    """) == 't')
    # The tiny correctness fixture need not make the planner prefer an index.
    # Disable sequential scans only to prove the available bounded access path.
    plan = json.loads(query(f"""BEGIN;SET LOCAL enable_seqscan=off;
      EXPLAIN(FORMAT JSON) SELECT * FROM power_data.prices
      WHERE source_id='{source}' AND starts_at>='2021-03-01T00:00:00Z'
        AND starts_at<'2021-03-02T00:00:00Z' AND ends_at<='2021-03-02T00:00:00Z';ROLLBACK;"""))
    def bounded_index(node):
        condition = node.get('Index Cond', '')
        return (node.get('Index Name') == 'prices_source_interval' and
                condition.count('starts_at') == 2 and
                all(term in condition for term in ('source_id =', ' >= ', ' < '))) or any(
                    bounded_index(child) for child in node.get('Plans', []))
    check('selected source/day can use a two-sided index range', bounded_index(plan[0]['Plan']))
    check('both explicit sources survive validated sealing', query(count) == '2')
    check('unbound runtime cannot read mappings', query('SET SESSION AUTHORIZATION preview_runtime;' + count) == '0')
    check('authorized current reader sees both sources', query('BEGIN;' + runtime(issue(), count) + 'ROLLBACK;') == '2')
    check('mandatory visibility survives an additional permissive policy', query("BEGIN;CREATE POLICY synthetic_allow ON market_data.curve_physical_price_sources FOR SELECT TO openetrm_runtime USING(true);SET LOCAL SESSION AUTHORIZATION preview_runtime;" + count + 'ROLLBACK;') == '0')
    outside = str(UUID(query('BEGIN;' + runtime(issue(), f"SELECT security.capture_date_context('{catalog}','2019-01-01',NULL);") + 'COMMIT;')))
    check('an unavailable historical model never exposes its mapping', query('BEGIN;' + runtime(issue(), f"SELECT security.use_market_context('{catalog}','{outside}');" + count) + 'ROLLBACK;') == '0')
    for table, key in [('market_catalog_scopes', catalog), ('reference_catalog_scopes', reference)]:
        token = issue()
        check('current ' + table + ' revocation hides mappings', query(f"BEGIN;DELETE FROM security.{table} WHERE catalog_id='{key}' AND tenant_id='{tenant}' AND workspace_id='{workspace}';" + runtime(token, count) + 'ROLLBACK;') == '0')

    draft = str(uuid4())
    setup = f"""INSERT INTO market_data.revisions VALUES('{catalog}','{draft}','{reference}','{ref_revision}','draft',0);
      INSERT INTO market_data.curves SELECT(jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||jsonb_build_object('revision_id','{draft}'))).*
      FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{revision}','{curve}');"""
    def insert(source_id=source, ref_id=ref_revision, curve_code=curve):
        return f"INSERT INTO market_data.curve_physical_price_sources VALUES('{catalog}','{draft}','{curve_code}','{reference}','{ref_id}','{source_id}');"

    check('current editor can author an exact source on a draft curve', query('BEGIN;' + setup + runtime(issue('editor'), insert() + f"SELECT count(*) FROM market_data.curve_physical_price_sources WHERE revision_id='{draft}';") + 'ROLLBACK;') == '1')
    # The immutable-definition trigger locks its parent revision. A read-only
    # principal cannot obtain that write-qualified row, so the existing guard
    # rejects before the mapping's INSERT policy is evaluated.
    reject('BEGIN;' + setup + runtime(issue(), insert()) + 'ROLLBACK;', '55000')
    print('PASS power actual storage: read authority cannot author physical sources', flush=True)
    passed += 1
    for field, value in [('quantity_code', 'test_gas_quantity'), ('location_code', 'test_node_a'), ('clock_code', 'synthetic_central')]:
        bad = str(uuid4())
        altered = f"INSERT INTO power_data.sources SELECT(jsonb_populate_record(NULL::power_data.sources,to_jsonb(s)||jsonb_build_object('id','{bad}','point','synthetic_{bad}','{field}','{value}'))).* FROM power_data.sources s WHERE id='{source}';"
        reject('BEGIN;' + setup + altered + insert(bad) + 'ROLLBACK;', 'physical_source_definition')
        print('PASS power actual storage: incompatible ' + field + ' is rejected', flush=True)
        passed += 1
    reject('BEGIN;' + setup + insert(ref_id=str(uuid4())) + 'ROLLBACK;', '23503')
    reject('BEGIN;' + setup + insert(curve_code='absent_curve') + 'ROLLBACK;', '23503')
    reject('BEGIN;' + setup + insert() + insert() + 'ROLLBACK;', '23505')
    print('PASS power actual storage: exact bundle/curve foreign keys and one-source identity are enforced', flush=True)
    passed += 1
    for sql in [f"UPDATE market_data.curve_physical_price_sources SET source_id=source_id WHERE revision_id='{revision}';",
                f"DELETE FROM market_data.curve_physical_price_sources WHERE revision_id='{revision}';",
                f"INSERT INTO market_data.curve_physical_price_sources VALUES('{catalog}','{revision}','local_power','{reference}','{ref_revision}','{source}');"]:
        reject('BEGIN;' + sql + 'ROLLBACK;', '55000')
    print('PASS power actual storage: published mappings cannot be replaced or added after sealing', flush=True)
    passed += 1
    return passed
