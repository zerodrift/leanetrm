"""Interval-curve storage checks in the launcher's owned synthetic database."""
from uuid import UUID, uuid4


def run_checks(query, manifest):
    catalog, revision, reference, ref_revision, tenant, workspace = [str(UUID(manifest[k])) for k in
        ('catalogId', 'revisionId', 'referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId')]
    curves = manifest['intervalCurves']
    marked, peak, off, shaped = curves['marked'], curves['peak'], curves['off'], curves['shaped']
    first = curves['contracts'][0]
    passed = 0

    def check(name, condition):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS interval curves storage: ' + name, flush=True)

    def reject(sql, expected):
        try:
            query('\\set VERBOSITY verbose\n' + sql)
        except RuntimeError as error:
            assert expected in str(error), str(error)
        else:
            raise AssertionError('Storage accepted an invalid interval curve operation: ' + expected)

    def issue(profile='viewer'):
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")

    def runtime(token, sql):
        return f"SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');{sql}"

    def codes(values):
        return ','.join(f"'{v}'" for v in values) or 'NULL'

    def members(selected):
        return f"SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id)=('{catalog}','{revision}') AND curve_code IN({codes(selected)})"

    count = f"SELECT count(*) FROM market_data.contract_intervals WHERE(catalog_id,revision_id)=('{catalog}','{revision}') AND contract_id IN({members([marked])});"

    def clone(draft, selected=(marked,), shapes=(), terms=None, where=None):
        """A draft copy of the selected curves and shaped curves; jsonb terms alter, and where terms omit, copied rows by table."""
        terms, where = terms or {}, where or {}

        def copy(table, alias, condition):
            return (f"INSERT INTO market_data.{table} SELECT(jsonb_populate_record(NULL::market_data.{table},"
                    f"to_jsonb({alias})||jsonb_build_object('revision_id','{draft}'){terms.get(table, '')})).* FROM market_data.{table} {alias} "
                    f"WHERE {alias}.catalog_id='{catalog}' AND {alias}.revision_id='{revision}' AND {condition}{where.get(table, '')};")
        contracts = members(selected)
        return (f"INSERT INTO market_data.revisions VALUES('{catalog}','{draft}','{reference}','{ref_revision}','draft',0);"
                + copy('revision_periods', 'p', 'true')
                + copy('curves', 'c', f"c.code IN({codes(selected)})")
                + copy('contracts', 'k', f"k.id IN({contracts})")
                + copy('contract_intervals', 'i', f"i.contract_id IN({contracts})")
                + copy('series', 's', f"s.contract_id IN({contracts})")
                + copy('curve_members', 'm', f"m.curve_code IN({codes(selected)})")
                + copy('curve_physical_price_sources', 'g', f"g.curve_code IN({codes(selected)})")
                + copy('curve_actual_rules', 'a', f"a.curve_code IN({codes(selected)})")
                + copy('curve_blocks', 'b', f"b.curve_code IN({codes(selected)})")
                + copy('shaped_curves', 'h', f"h.code IN({codes(shapes)})")
                + copy('shaped_curve_blocks', 'x', f"x.shaped_curve_code IN({codes(shapes)})"))

    def seal(draft):
        return f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{draft}');"

    def sealed(sql, draft):
        return query('BEGIN;' + sql + seal(draft) + f"SELECT state FROM market_data.revisions WHERE id='{draft}';ROLLBACK;") == 'sealed'

    check('the example bundle seals with 97 interval contracts', query(count) == '97')
    check('a viewer sees every interval of the marked curve', query('BEGIN;' + runtime(issue(), count) + 'ROLLBACK;') == '97')
    outside = str(UUID(query('BEGIN;' + runtime(issue(), f"SELECT security.capture_date_context('{catalog}','2019-01-01',NULL);") + 'COMMIT;')))
    check('an unavailable historical context sees no intervals',
          query('BEGIN;' + runtime(issue(), f"SELECT security.use_market_context('{catalog}','{outside}');" + count) + 'ROLLBACK;') == '0')
    interval_row = f"SELECT * FROM market_data.contract_intervals WHERE(catalog_id,revision_id,contract_id)=('{catalog}','{revision}','{first}')"
    reject('BEGIN;' + runtime(issue('editor'), f"INSERT INTO market_data.contract_intervals SELECT * FROM({interval_row})q;") + 'ROLLBACK;', '55000')
    for sql in [f"UPDATE market_data.contract_intervals SET occurrence=occurrence WHERE revision_id='{revision}';",
                f"DELETE FROM market_data.contract_intervals WHERE revision_id='{revision}';",
                f"UPDATE market_data.curve_blocks SET tou_version=tou_version WHERE revision_id='{revision}';",
                f"DELETE FROM market_data.shaped_curve_blocks WHERE revision_id='{revision}';"]:
        reject('BEGIN;' + sql + 'ROLLBACK;', '55000')
    check('sealed intervals, blocks and shaped curves cannot be added, changed or removed', True)

    draft = str(uuid4())
    check('an unaltered draft copy of the marked curve seals', sealed(clone(draft), draft))
    # The definition guard locks its parent revision first; read authority cannot obtain that row.
    reject('BEGIN;' + f"INSERT INTO market_data.revisions VALUES('{catalog}','{draft}','{reference}','{ref_revision}','draft',0);"
           + runtime(issue(), f"INSERT INTO market_data.contract_intervals SELECT(jsonb_populate_record(NULL::market_data.contract_intervals,to_jsonb(q)||jsonb_build_object('revision_id','{draft}'))).* FROM({interval_row})q;")
           + 'ROLLBACK;', '55000')
    check('a viewer cannot author intervals on a draft', True)
    shorter = "||CASE WHEN i.contract_id='{0}' THEN jsonb_build_object('ends_at',i.starts_at+interval '30 minutes') ELSE '{{}}'::jsonb END".format(first)
    shorter_expiry = "||CASE WHEN k.id='{0}' THEN jsonb_build_object('expiry_at',(SELECT starts_at+interval '30 minutes' FROM market_data.contract_intervals x WHERE x.contract_id=k.id AND x.revision_id='{1}')) ELSE '{{}}'::jsonb END".format(first, revision)
    for name, terms, where in [
            ('an interval contract expiring after its interval', {'contracts': f"||CASE WHEN k.id='{first}' THEN jsonb_build_object('expiry_at',k.expiry_at+interval '1 hour') ELSE '{{}}'::jsonb END"}, {}),
            ('an interval contract spanning two days', {'contracts': f"||CASE WHEN k.id='{first}' THEN jsonb_build_object('delivery_to',k.delivery_to+1) ELSE '{{}}'::jsonb END"}, {}),
            ('a 30-minute interval on the hourly profile', {'contracts': shorter_expiry, 'contract_intervals': shorter}, {}),
            ('a curve mixing interval and date-only constituents', {}, {'contract_intervals': f" AND i.contract_id<>'{first}'"})]:
        bad = str(uuid4())
        reject('BEGIN;' + clone(bad, terms=terms, where=where) + seal(bad) + 'ROLLBACK;', 'interval_curve_definition')
        check('sealing refuses ' + name, True)

    blocks = (peak, off)
    count_shaped = f"SELECT count(*) FROM market_data.shaped_curve_blocks WHERE(catalog_id,revision_id)=('{catalog}','{revision}');"
    check('the example bundle seals two shaped curves over peak and off-peak blocks and one over all hours', query(count_shaped) == '5')
    check('a viewer sees the blocks and shaped curves', query('BEGIN;' + runtime(issue(), count_shaped) + 'ROLLBACK;') == '5')
    draft = str(uuid4())
    check('an unaltered draft copy of the shaped curve seals', sealed(clone(draft, blocks, [shaped]), draft))

    def tou(product, version=1):
        return f"||jsonb_build_object('tou_product_id','{product}','tou_version',{version})"
    draft_version = (f"INSERT INTO market_data.tou_product_versions(catalog_id,product_id,version,generation,state,name,clock_catalog_id,clock_revision_id,"
                     f"clock_code,peak_start_hour,peak_end_hour,peak_saturday) VALUES('{catalog}','{curves['pjm']}',2,1,'draft','Synthetic draft peak',"
                     f"'{reference}','{ref_revision}','synthetic_eastern',7,23,false);")
    composition = 'one all-hours block, or the peak and off-peak blocks of one TOU product version'
    binding = 'bound to a published TOU product version'
    for name, prefix, selected, shapes, terms, where, extra, detail in [
            ('a block bound to a draft TOU version', draft_version, blocks, [shaped], {'curve_blocks': tou(curves['pjm'], 2)}, {}, '', binding),
            ('a block on a TOU product of another clock', '', blocks, [shaped], {'curve_blocks': tou(curves['spp'])}, {}, '', binding),
            ('a shaped curve with only a peak block', '', blocks, [shaped], {}, {'shaped_curve_blocks': f" AND x.block_curve_code<>'{off}'"}, '', composition),
            ('peak and off-peak blocks of two TOU products', '', blocks, [shaped],
             {'curve_blocks': f"||CASE WHEN b.curve_code='{off}' THEN jsonb_build_object('tou_product_id','{curves['nyiso']}') ELSE '{{}}'::jsonb END"}, {}, '', composition),
            ('a shaped curve code that is also a curve code', '', blocks, [shaped],
             {'shaped_curves': f"||jsonb_build_object('code','{peak}')", 'shaped_curve_blocks': f"||jsonb_build_object('shaped_curve_code','{peak}')"}, {}, '',
             'must differ from every curve code'),
            ('a multiplicative curve on an additive shape series', '', blocks, [shaped], {'shaped_curves': "||jsonb_build_object('method','multiplicative')"}, {}, '',
             'series is an additive shape'),
            ('a block over interval constituents', '', (marked,), [], {}, {}, lambda d: f"INSERT INTO market_data.curve_blocks VALUES('{catalog}','{d}','{marked}','all',NULL,NULL);",
             'direct curve of date-only constituents')]:
        bad = str(uuid4())
        added = extra(bad) if extra else ''
        reject('BEGIN;' + prefix + clone(bad, selected, shapes, terms, where) + added + seal(bad) + 'ROLLBACK;', detail)
        check('sealing refuses ' + name, True)

    rules = f"SELECT count(*) FROM market_data.curve_actual_rules WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{revision}','{marked}');"
    check('the example bundle seals the marked curve with its due rule', query(rules) == '1')
    due = 'An actual-due rule binds an interval curve whose collected-price source shares its clock and interval length'
    bound = (f"INSERT INTO market_data.curve_physical_price_sources SELECT catalog_id,'{{0}}','{peak}',reference_catalog_id,reference_revision_id,source_id "
             f"FROM market_data.curve_physical_price_sources WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{revision}','{marked}');"
             f"INSERT INTO market_data.curve_actual_rules VALUES('{catalog}','{{0}}','{peak}','interval_end',60,'interval_actual_due_v1');")
    bad = str(uuid4())
    reject('BEGIN;' + clone(bad, (marked, peak)) + bound.format(bad) + seal(bad) + 'ROLLBACK;', due)
    check('sealing refuses a due rule on a date-only curve', True)
    draft = str(uuid4())
    reject('BEGIN;' + clone(draft, terms={'curve_actual_rules': "||jsonb_build_object('rule_version','interval_actual_due_v2')"}) + 'ROLLBACK;', '23514')
    check('a due rule names the supported rule version', True)
    return passed
