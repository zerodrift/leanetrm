"""Interval-curve storage checks in the launcher's owned synthetic database."""
import json
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
                + copy('shaped_curve_blocks', 'x', f"x.shaped_curve_code IN({codes(shapes)}) AND x.block_curve_code IN({codes(selected)})"))

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
    count_shaped = f"SELECT count(*) FROM market_data.shaped_curve_blocks WHERE anchor_role='period' AND(catalog_id,revision_id)=('{catalog}','{revision}');"
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
    # New source contracts are tested using independent draft copies. Existing marked-source
    # checks remain in check_power_actual_storage.py without changed expectations.
    def shaped_rule(draft, source=curves['source'], reference_version=ref_revision):
        return (f"INSERT INTO market_data.shaped_curve_actual_rules VALUES('{catalog}','{draft}','{shaped}',"
                f"'{reference}','{reference_version}','{source}','interval_end',60,'interval_actual_due_v1');")

    draft = str(uuid4())
    check('a compatible shaped collected source seals', sealed(clone(draft, blocks, [shaped]) + shaped_rule(draft), draft))
    for label, old, replacement in [('due anchor', "'interval_end'", "'unknown'"), ('due offset', ',60,', ',-1,'),
                                    ('due version', "'interval_actual_due_v1'", "'unknown'")]:
        draft = str(uuid4())
        reject('BEGIN;' + clone(draft, blocks, [shaped])
               + shaped_rule(draft).replace(old, replacement) + 'ROLLBACK;', '23514')
        check('shaped actuals reject unsupported ' + label, True)
    for label, changes, refusal in [
            ('quantity', {'quantity_code': 'test_gas_quantity'}, 'interval_curve_definition'),
            ('location', {'location_code': 'test_node_a'}, 'interval_curve_definition'),
            ('PJM market zone', {'clock_code': 'synthetic_central'}, 'interval_curve_definition'),
            ('IANA clock', {'clock_code': 'synthetic_fixed_est'}, 'interval_curve_definition'),
            ('ERCOT market zone', {'provider': 'ERCOT', 'dataset': 'np4-190-cd/dam_stlmnt_pnt_prices'}, 'interval_curve_definition'),
            ('provider cadence', {'minutes': 15}, '23514'),
            ('unsupported provider', {'provider': 'synthetic_provider'}, '23514')]:
        draft, source = str(uuid4()), str(uuid4())
        payload = json.dumps({'id': source, 'point': 'synthetic_' + source, **changes})
        changed = (f"INSERT INTO power_data.sources SELECT(jsonb_populate_record(NULL::power_data.sources,"
                   f"to_jsonb(s)||'{payload}'::jsonb)).* FROM power_data.sources s WHERE id='{curves['source']}';")
        reject('BEGIN;' + clone(draft, blocks, [shaped]) + changed + shaped_rule(draft, source) + seal(draft) + 'ROLLBACK;', refusal)
        check('shaped sources refuse incompatible ' + label, True)
    draft = str(uuid4())
    reject('BEGIN;' + clone(draft, blocks, [shaped]) + shaped_rule(draft, reference_version=str(uuid4())) + 'ROLLBACK;', '23503')
    check('shaped sources retain exact reference-version foreign keys', True)

    # Exercise every representable C9 quote restriction against a fully independent reference
    # revision. FK-inconsistent commodity/quantity combinations are rejected by their exact keys.
    # The same predicate is exercised above through shaped seals and by the unchanged 0063 suite.
    def compatibility_fixture(table='', item='', changes=None):
        version, draft, source = str(uuid4()), str(uuid4()), str(uuid4())
        changes = changes or {}
        payload = json.dumps(changes).replace("'", "''")
        index_changes = {target: changes[field] for field, target in [('multiplier', 'quote_multiplier'), ('unit_code', 'quote_unit_code')] if field in changes}
        index_payload = json.dumps(index_changes)
        minutes = changes.get('interval_minutes', 60)
        source_payload = payload if table == 'power_data.sources' else '{}'
        extra_currency = (f"INSERT INTO ref_data.currencies SELECT(jsonb_populate_record(NULL::ref_data.currencies,to_jsonb(r)||"
                          f"jsonb_build_object('code','synthetic_other_currency','display_code','XXX'))).* FROM ref_data.currencies r "
                          f"WHERE(catalog_id,revision_id,code)=('{reference}','{version}','usd');") if table == 'power_data.sources' else ''
        sql = f"""INSERT INTO ref_data.revisions(catalog_id,id,revision_key)
          VALUES('{reference}','{version}','synthetic_{version}');
          DO $copy$ DECLARE t text;BEGIN
           FOREACH t IN ARRAY security.reference_display_tables() LOOP
            EXECUTE format('INSERT INTO ref_data.%1$I SELECT(jsonb_populate_record(NULL::ref_data.%1$I,
             to_jsonb(r)||jsonb_build_object(''revision_id'',$3)||CASE WHEN $4 AND to_jsonb(r)->>''code''=$5
             THEN $6 ELSE ''{{}}''::jsonb END||CASE WHEN $7 AND to_jsonb(r)->>''code''=''test_power_price''
             THEN $8 ELSE ''{{}}''::jsonb END)).* FROM ref_data.%1$I r WHERE catalog_id=$1 AND revision_id=$2',t)
             USING '{reference}'::uuid,'{ref_revision}'::uuid,'{version}'::uuid,t='{table}','{item}','{payload}'::jsonb,t='price_index_definitions','{index_payload}'::jsonb;
           END LOOP;
          END $copy$;
          {extra_currency}
          INSERT INTO market_data.revisions VALUES('{catalog}','{draft}','{reference}','{version}','draft',0);
          INSERT INTO market_data.curves SELECT(jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||
           jsonb_build_object('revision_id','{draft}','reference_revision_id','{version}'))).*
           FROM market_data.curves c WHERE(catalog_id,revision_id,code)=('{catalog}','{revision}','{peak}');
          INSERT INTO power_data.sources SELECT(jsonb_populate_record(NULL::power_data.sources,to_jsonb(s)||
           jsonb_build_object('id','{source}','point','synthetic_{source}','reference_revision_id','{version}')||'{source_payload}'::jsonb)).*
           FROM power_data.sources s WHERE id='{curves['source']}';"""
        sql += f"UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{reference}','{version}');"
        predicate = f"SELECT market_data.collected_source_fits('{catalog}','{draft}','{peak}','{reference}','{version}','{source}');"
        shape_series = str(uuid4())
        consume = (f"INSERT INTO market_data.revision_periods SELECT catalog_id,'{draft}',effective_from,effective_to FROM market_data.revision_periods "
                   f"WHERE(catalog_id,revision_id)=('{catalog}','{revision}');")
        for table, condition in [('contracts', f"r.id IN({members([peak])})"),
                                 ('series', f"r.contract_id IN({members([peak])})"),
                                 ('curve_members', f"r.curve_code='{peak}'")]:
            consume += (f"INSERT INTO market_data.{table} SELECT(jsonb_populate_record(NULL::market_data.{table},to_jsonb(r)||"
                        f"jsonb_build_object('revision_id','{draft}','reference_revision_id','{version}'))).* FROM market_data.{table} r "
                        f"WHERE(r.catalog_id,r.revision_id)=('{catalog}','{revision}') AND {condition};")
        consume += (f"INSERT INTO market_data.interval_series SELECT(jsonb_populate_record(NULL::market_data.interval_series,to_jsonb(r)||"
                    f"jsonb_build_object('id','{shape_series}','code','synthetic_{shape_series.replace('-', '')}',"
                    f"'reference_revision_id','{version}','interval_minutes',{minutes}))).* FROM market_data.interval_series r WHERE id='{curves['shapeSeries']['additive']}';"
                    f"INSERT INTO market_data.shaped_curves SELECT(jsonb_populate_record(NULL::market_data.shaped_curves,to_jsonb(r)||"
                    f"jsonb_build_object('revision_id','{draft}','shape_series_id','{shape_series}'))).* FROM market_data.shaped_curves r "
                    f"WHERE(catalog_id,revision_id,code)=('{catalog}','{revision}','{shaped}');"
                    f"INSERT INTO market_data.curve_blocks VALUES('{catalog}','{draft}','{peak}','all',NULL,NULL);"
                    f"INSERT INTO market_data.shaped_curve_blocks VALUES('{catalog}','{draft}','{shaped}','{peak}','period');"
                    + shaped_rule(draft, source, version))
        return sql, predicate, consume, draft

    sql, predicate, consume, draft = compatibility_fixture()
    check('the shared compatibility predicate accepts an exact synthetic cloned reference', query('BEGIN;' + sql + predicate + 'ROLLBACK;') == 't')
    check('an independently cloned compatible shaped source seals', sealed(sql + consume, draft))
    for label, table, item, changes in [
            ('USD currency', 'currencies', 'usd', {'display_code': 'TST'}),
            ('matching source currency', 'power_data.sources', '', {'currency_code': 'synthetic_other_currency'}),
            ('outright role', 'quote_specifications', 'test_power_outright', {'kind': 'basis'}),
            ('unit quote multiplier', 'quote_specifications', 'test_power_outright', {'multiplier': 2}),
            ('matching interval cadence', 'delivery_profiles', 'test_hourly_eastern', {'interval_minutes': 15}),
            ('MWh energy unit', 'quantity_specifications', 'test_power_quantity', {'unit_code': 'mmbtu_it'}),
            ('energy dimension', 'quantity_specifications', 'test_power_quantity',
             {'dimension_code': 'power', 'unit_code': 'megawatt', 'basis_code': 'test_capacity'})]:
        sql, predicate, consume, draft = compatibility_fixture(table, item, changes)
        check('source compatibility preserves the declared ' + label + ' contract',
              query('BEGIN;' + sql + predicate + 'ROLLBACK;') == ('t' if table == 'delivery_profiles' else 'f'))
        reject('BEGIN;' + sql + consume + seal(draft) + 'ROLLBACK;', 'A shaped actual rule requires a compatible collected source')
        check('shaped sealing requires ' + label, True)
    sql, predicate, consume, draft = compatibility_fixture('quantity_specifications', 'test_power_quantity', {'commodity_code': 'test_gas'})
    reject('BEGIN;' + sql + predicate + 'ROLLBACK;', '23503')
    check('exact quantity commodity relationships cannot bypass source compatibility', True)

    draft, source = str(uuid4()), str(uuid4())
    same_statement = (f"WITH source AS(INSERT INTO power_data.sources SELECT(jsonb_populate_record(NULL::power_data.sources,to_jsonb(s)||"
                      f"jsonb_build_object('id','{source}','point','synthetic_{source}'))).* FROM power_data.sources s "
                      f"WHERE id='{curves['source']}' RETURNING id) INSERT INTO market_data.curve_physical_price_sources "
                      f"SELECT '{catalog}','{draft}','{peak}','{reference}','{ref_revision}',id FROM source;")
    check('marked mappings see compatible sources inserted in the same SQL statement',
          sealed(clone(draft, blocks, [shaped]) + same_statement, draft))

    def expiry_rule(draft, curve=peak):
        return f"INSERT INTO market_data.curve_expiry_rules VALUES('{catalog}','{draft}','{curve}','freeze_final_settlement_v1');"

    draft = str(uuid4())
    check('a shaped period block may explicitly freeze eligible final settlements',
          sealed(clone(draft, blocks, [shaped]) + expiry_rule(draft), draft))
    for label, selected, shapes, target in [('marked interval', (marked,), [], marked),
                                            ('unconsumed block', blocks, [], peak)]:
        draft = str(uuid4())
        reject('BEGIN;' + clone(draft, selected, shapes) + expiry_rule(draft, target) + seal(draft) + 'ROLLBACK;',
               'interval_curve_definition')
        check('expiry policies refuse a ' + label, True)

    # A balance block is another explicit curve, with its own complete pricing period.
    # Copy the period definitions, then give each balance contract an independent identity.
    def balance(draft, day_shift=9, end_shift=0, membership='peak', product=None, curve_changes=None):
        product = product or curves['pjm']
        balance_code = 'synthetic_balance'
        curve_changes = curve_changes or {}
        change_payload = json.dumps(curve_changes)
        quote = curve_changes.get('quote_code', 'test_power_outright')
        product_fields = 'NULL,NULL' if membership == 'all' else f"'{product}',1"
        return (f"INSERT INTO market_data.curves SELECT(jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||"
                f"jsonb_build_object('code','{balance_code}','name','Synthetic balance')||'{change_payload}'::jsonb)).* FROM market_data.curves c "
                f"WHERE(catalog_id,revision_id,code)=('{catalog}','{draft}','{peak}');"
                f"INSERT INTO market_data.curve_blocks VALUES('{catalog}','{draft}','{balance_code}','{membership}',{product_fields});"
                f"INSERT INTO market_data.shaped_curve_blocks VALUES('{catalog}','{draft}','{shaped}','{balance_code}','balance');"
                f"INSERT INTO market_data.contracts SELECT(jsonb_populate_record(NULL::market_data.contracts,to_jsonb(k)||"
                f"jsonb_build_object('id',md5(k.id::text||'balance')::uuid,'delivery_from',k.delivery_from+{day_shift},"
                f"'delivery_to',k.delivery_to+{end_shift}))).* FROM market_data.contracts k "
                f"JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k.catalog_id,k.revision_id,k.id) "
                f"WHERE(m.catalog_id,m.revision_id,m.curve_code)=('{catalog}','{draft}','{peak}');"
                f"INSERT INTO market_data.series SELECT(jsonb_populate_record(NULL::market_data.series,to_jsonb(s)||"
                f"jsonb_build_object('id',md5(s.id::text||'balance')::uuid,'code',s.code||'_balance',"
                f"'contract_id',md5(s.contract_id::text||'balance')::uuid,'quote_code','{quote}'))).* FROM market_data.series s "
                f"JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(s.catalog_id,s.revision_id,s.contract_id) "
                f"WHERE(m.catalog_id,m.revision_id,m.curve_code)=('{catalog}','{draft}','{peak}');"
                f"INSERT INTO market_data.curve_members SELECT catalog_id,revision_id,'{balance_code}',"
                f"md5(contract_id::text||'balance')::uuid,ordinal FROM market_data.curve_members "
                f"WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{draft}','{peak}');")

    draft = str(uuid4())
    check('a balance block with explicit contained days and matching product seals',
          sealed(clone(draft, blocks, [shaped]) + balance(draft) + expiry_rule(draft, 'synthetic_balance'), draft))
    for label, arguments in [('start outside its period', {'day_shift': -1}),
                             ('end before its period end', {'end_shift': -1}),
                             ('end after its period end', {'end_shift': 1}),
                             ('another product', {'product': curves['nyiso']})]:
        draft = str(uuid4())
        reject('BEGIN;' + clone(draft, blocks, [shaped]) + balance(draft, **arguments) + seal(draft) + 'ROLLBACK;',
               'interval_curve_definition')
        check('balance sealing refuses ' + label, True)

    sql, predicate, consume, draft = compatibility_fixture()
    check('an independently cloned all-hours balance seals', sealed(sql + consume + balance(draft, membership='all'), draft))

    # Keep each changed balance internally valid so the shared block-contract rule, rather
    # than a foreign key or its own quote/series relationship, rejects the mismatch.
    for label, changes in [('quote', {'quote_code': 'test_power_basis'}),
                           ('profile', {'profile_code': 'test_uniform'}),
                           ('commodity', {'commodity_code': 'test_gas', 'quote_code': 'test_gas_outright', 'settlement_target_code': 'test_index_target'}),
                           ('location', {'location_code': 'test_hub_b'})]:
        sql, predicate, consume, draft = compatibility_fixture()
        if label == 'location':
            extra = (f"INSERT INTO ref_data.commodity_locations SELECT reference_catalog_id,reference_revision_id,'test_power','test_hub_b' "
                     f"FROM market_data.revisions WHERE id='{draft}';")
            sql = sql.replace("UPDATE ref_data.revisions SET state='sealed'", extra + "UPDATE ref_data.revisions SET state='sealed'")
        reject('BEGIN;' + sql + consume + balance(draft, membership='all', curve_changes=changes) + seal(draft) + 'ROLLBACK;',
               'The blocks of a shaped curve share their quote, commodity, location and delivery profile')
        check('balance sealing refuses mismatched ' + label, True)

    draft = str(uuid4())
    reject('BEGIN;' + clone(draft, blocks, [shaped]) + expiry_rule(draft).replace('freeze_final_settlement_v1', 'unknown') + 'ROLLBACK;', '23514')
    check('expiry policies require the named supported version', True)
    draft = str(uuid4())
    reject('BEGIN;' + clone(draft, blocks, [shaped]) + balance(draft).replace("'balance');", "'unknown');") + 'ROLLBACK;', '23514')
    check('anchor bindings require an explicit supported role', True)
    for table, insert in [('shaped_curve_actual_rules', shaped_rule), ('curve_expiry_rules', expiry_rule)]:
        draft = str(uuid4())
        reject('BEGIN;' + clone(draft, blocks, [shaped]) + runtime(issue(), insert(draft)) + 'ROLLBACK;', '55000')
        check('read authority cannot author ' + table, True)

    # Exercise the runtime policies without leaving additional revisions in the shared fixture.
    draft = str(uuid4())
    setup = 'BEGIN;' + clone(draft, blocks, [shaped]) + shaped_rule(draft) + expiry_rule(draft) + seal(draft)
    for table in ('shaped_curve_actual_rules', 'curve_expiry_rules'):
        rows = f"SELECT count(*) FROM market_data.{table} WHERE revision_id='{draft}';"
        check(table + ' is visible to a current authorized reader', query(setup + runtime(issue(), rows) + 'ROLLBACK;') == '1')
        check(table + ' is hidden from an unbound runtime', query(setup + 'SET LOCAL SESSION AUTHORIZATION preview_runtime;' + rows + 'ROLLBACK;') == '0')
        check(table + ' keeps mandatory visibility under permissive policies',
              query(setup + f"CREATE POLICY synthetic_allow ON market_data.{table} FOR SELECT TO openetrm_runtime USING(true);"
                    + 'SET LOCAL SESSION AUTHORIZATION preview_runtime;' + rows + 'ROLLBACK;') == '0')
        check(table + ' respects historical visibility', query(setup + runtime(issue(),
              f"SELECT security.use_market_context('{catalog}','{outside}');" + rows) + 'ROLLBACK;') == '0')
        for scope, scope_catalog in [('market_catalog_scopes', catalog), ('reference_catalog_scopes', reference)]:
            token = issue()
            revoke = f"DELETE FROM security.{scope} WHERE catalog_id='{scope_catalog}' AND tenant_id='{tenant}' AND workspace_id='{workspace}';"
            check(table + ' follows current ' + scope + ' revocation', query(setup + revoke + runtime(token, rows) + 'ROLLBACK;') == '0')
        for statement in (f"UPDATE market_data.{table} SET rule_version=rule_version WHERE revision_id='{draft}';",
                          f"DELETE FROM market_data.{table} WHERE revision_id='{draft}';"):
            reject(setup + statement + 'ROLLBACK;', '55000')
        check(table + ' remains immutable after sealing', True)
    return passed
