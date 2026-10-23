"""Exact PPA reference contracts in the launcher's isolated synthetic database."""
import json
from uuid import UUID, uuid4


def run_checks(query, manifest):
    catalog, revision, reference, ref_revision, tenant, workspace = [str(UUID(manifest[k])) for k in
        ('catalogId', 'revisionId', 'referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId')]
    passed = 0

    def check(name, condition):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS PPA storage: ' + name, flush=True)

    def reject(sql, expected):
        try:
            query('\\set VERBOSITY verbose\n' + sql)
        except RuntimeError as error:
            assert expected in str(error), str(error)
        else:
            raise AssertionError('Storage accepted an invalid PPA definition: ' + expected)

    def draft_header(draft):
        return f"INSERT INTO market_data.revisions VALUES('{catalog}','{draft}','{reference}','{ref_revision}','draft',0);"

    draft = str(uuid4())
    asset = (f"INSERT INTO market_data.generation_assets VALUES('{catalog}','{draft}','synthetic_asset','Synthetic asset',"
             f"'{reference}','{ref_revision}','test_hub_a','test_capacity_quantity','synthetic_eastern');")
    check('an asset explicitly binds a location, MW quantity and clock in its exact reference revision',
          query('BEGIN;' + draft_header(draft) + asset
                + f"SELECT code FROM market_data.generation_assets WHERE revision_id='{draft}';ROLLBACK;") == 'synthetic_asset')
    tables = ('generation_assets', 'generation_asset_versions', 'location_valuation_assignments', 'volume_sources')
    inventory = ('revision_periods', 'contracts', 'curves', 'series', 'curve_members', 'curve_inputs', 'constituent_mappings',
                 'rate_curves', 'rate_curve_points', 'contract_intervals', 'curve_blocks', 'shaped_curves', 'shaped_curve_blocks',
                 'curve_physical_price_sources', 'curve_actual_rules', 'shaped_curve_actual_rules', 'curve_expiry_rules',
                 'index_fixing_observations', *tables)

    def clone(draft, changes=None, omitted=None):
        changes, omitted = changes or {}, omitted or {}
        sql = draft_header(draft)
        for table in inventory:
            sql += (f"INSERT INTO market_data.{table} SELECT(jsonb_populate_record(NULL::market_data.{table},to_jsonb(r)||"
                    f"jsonb_build_object('revision_id','{draft}'){changes.get(table, '')})).* FROM market_data.{table} r "
                    f"WHERE(r.catalog_id,r.revision_id)=('{catalog}','{revision}'){omitted.get(table, '')};")
        return sql

    def seal(draft):
        return f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{draft}');"

    def altered(table, changes, expected='23514', prefix='', omitted=None, suffix=''):
        draft = str(uuid4())
        terms = {table: "||'" + json.dumps(changes).replace("'", "''") + "'::jsonb"} if table else {}
        reject('BEGIN;' + prefix + clone(draft, terms, omitted) + suffix.replace('{draft}', draft) + seal(draft) + 'ROLLBACK;', expected)

    def issue(profile='viewer'):
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")

    def runtime(token, sql):
        return f"SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');{sql}"

    for table in tables:
        rows = f"SELECT count(*) FROM market_data.{table} WHERE(catalog_id,revision_id)=('{catalog}','{revision}');"
        check(table + ' has an executable synthetic consumer', int(query(rows)) > 0)
    first_source = json.loads(query(f"SELECT row_to_json(r)::text FROM market_data.volume_sources r WHERE revision_id='{revision}' ORDER BY interval_minutes DESC,code LIMIT 1;"))
    draft = str(uuid4())
    check('an unchanged exact definition clone seals', query('BEGIN;' + clone(draft) + seal(draft)
          + f"SELECT state FROM market_data.revisions WHERE id='{draft}';ROLLBACK;") == 'sealed')

    draft = str(uuid4())
    check('the authorized sealing operation validates the exact PPA bundle',
          query('BEGIN;' + clone(draft) + runtime(issue('approver'),
                f"SELECT security.seal_market_revision('{catalog}','{draft}');SELECT state FROM market_data.revisions WHERE id='{draft}';")
                + 'ROLLBACK;') == 'sealed')

    for label, table, changes in [
            ('positive capacity basis', 'generation_asset_versions', {'capacity_basis': 0}),
            ('nonnegative accreditation', 'generation_asset_versions', {'accredited_capacity': -1}),
            ('explicit source stage', 'volume_sources', {'forecast_stage': 'unknown'}),
            ('explicit meter stage', 'volume_sources', {'metered_stage': 'unknown'}),
            ('source range rule', 'volume_sources', {'range_rule': 'unknown'}),
            ('supported source grid', 'volume_sources', {'interval_minutes': 30}),
            ('loss below one', 'volume_sources', {'loss': 1}),
            ('nonnegative loss', 'volume_sources', {'loss': -0.1}),
            ('availability upper bound', 'volume_sources', {'availability_mode': 'apply', 'availability': 1.1}),
            ('curtailment lower bound', 'volume_sources', {'curtailment_mode': 'apply', 'curtailment': -0.1}),
            ('applied availability value', 'volume_sources', {'availability_mode': 'apply', 'availability': None}),
            ('reflected availability without a second factor', 'volume_sources', {'availability_mode': 'reflected', 'availability': 0.9}),
            ('explicit adjustment mode', 'volume_sources', {'curtailment_mode': 'unknown'}),
            ('degradation below one', 'volume_sources', {'degradation_mode': 'apply', 'degradation': 1, 'degradation_basis': 'anniversary_step_v1', 'degradation_base_date': '2030-01-01'}),
            ('supported degradation basis', 'volume_sources', {'degradation_mode': 'apply', 'degradation': 0.005, 'degradation_basis': 'continuous_act365', 'degradation_base_date': '2030-01-01'}),
            ('unambiguous anniversary base', 'volume_sources', {'degradation_mode': 'apply', 'degradation': 0.005, 'degradation_basis': 'anniversary_step_v1', 'degradation_base_date': '2028-02-29'}),
            ('reflected degradation without a second factor', 'volume_sources', {'degradation_mode': 'reflected', 'degradation': 0.005}),
            ('explicit meter deadline rule', 'volume_sources', {'meter_due_rule': 'unknown'}),
            ('nonnegative meter offset', 'volume_sources', {'meter_due_offset_minutes': -1}),
            ('bounded meter offset', 'volume_sources', {'meter_due_offset_minutes': 129601})]:
        altered(table, changes)
        check('definitions require ' + label, True)
    for values in [{'availability_mode': 'apply', 'availability': 0, 'curtailment_mode': 'apply', 'curtailment': 1, 'meter_due_offset_minutes': 0},
                   {'meter_due_offset_minutes': 129600}]:
        draft = str(uuid4())
        terms = {'volume_sources': "||'" + json.dumps(values) + "'::jsonb"}
        check('explicit outage factors and meter-deadline endpoints are valid',
              query('BEGIN;' + clone(draft, terms) + seal(draft) + f"SELECT state FROM market_data.revisions WHERE id='{draft}';ROLLBACK;") == 'sealed')

    altered('generation_assets', {'capacity_quantity_code': 'test_power_quantity'}, 'ppa_reference_definition')
    check('asset capacity basis uses an exact MW power specification', True)
    for field in ('effective_from', 'effective_to'):
        draft = str(uuid4())
        boundary = 'min' if field == 'effective_from' else 'max'
        changes = {'generation_asset_versions': f"||CASE WHEN r.{field}=(SELECT {boundary}({field}) FROM market_data.generation_asset_versions WHERE revision_id='{revision}') THEN jsonb_build_object('{field}',r.{field}+interval '30 minutes') ELSE '{{}}'::jsonb END"}
        reject('BEGIN;' + clone(draft, changes) + seal(draft) + 'ROLLBACK;', 'ppa_reference_definition')
        check('asset version ' + field + ' is a declared operating-day boundary', True)
    for table, identity in [('generation_asset_versions', "'effective_from',r.effective_from+interval '1 minute'"),
                            ('location_valuation_assignments', "'code','synthetic_overlap'")]:
        suffix = (f"INSERT INTO market_data.{table} SELECT(jsonb_populate_record(NULL::market_data.{table},to_jsonb(r)||"
                  f"jsonb_build_object({identity}))).* FROM market_data.{table} r WHERE revision_id='{{draft}}' LIMIT 1;")
        altered(None, {}, 'ppa_reference_definition', suffix=suffix)
        check(table + ' refuses overlapping effective coverage', True)
    altered('generation_assets', {'reference_revision_id': str(uuid4())}, '23503')
    altered('volume_sources', {'clock_code': 'synthetic_central'}, '23503')
    altered('volume_sources', {'interval_minutes': 15}, '23503')
    check('assets and sources retain exact reference, clock and grid foreign keys', True)
    for label, field, value in [('forecast kind', 'forecast_series_id', first_source['metered_series_id']),
                                ('meter kind', 'metered_series_id', first_source['forecast_series_id'])]:
        draft = str(uuid4())
        term = "||CASE WHEN r.code='" + first_source['code'] + "' THEN '" + json.dumps({field: value}) + "'::jsonb ELSE '{}'::jsonb END"
        reject('BEGIN;' + clone(draft, {'volume_sources': term}) + seal(draft) + 'ROLLBACK;', 'ppa_reference_definition')
        check('volume sources enforce ' + label, True)
    bad_meter = str(uuid4())
    prefix = (f"INSERT INTO market_data.interval_series SELECT(jsonb_populate_record(NULL::market_data.interval_series,to_jsonb(r)||"
              f"jsonb_build_object('id','{bad_meter}','code','synthetic_bad_meter','quantity_code','test_gas_quantity'))).* "
              f"FROM market_data.interval_series r WHERE id='{first_source['metered_series_id']}';")
    draft = str(uuid4())
    term = f"||CASE WHEN r.code='{first_source['code']}' THEN jsonb_build_object('metered_series_id','{bad_meter}') ELSE '{{}}'::jsonb END"
    reject('BEGIN;' + prefix + clone(draft, {'volume_sources': term}) + seal(draft) + 'ROLLBACK;', 'ppa_reference_definition')
    check('metered energy has the asset commodity and MWh unit', True)

    # Each source/curve is a real executable baseline; removing a required definition must
    # refuse sealing, never silently downgrade an assignment to another price capability.
    for table in ('curve_actual_rules', 'shaped_curve_actual_rules'):
        altered(None, {}, 'ppa_reference_definition', omitted={table: ' AND false'})
        check('energy assignments require their ' + table, True)
    altered(None, {}, 'ppa_reference_definition', omitted={'index_fixing_observations': ' AND false'})
    check('capacity assignments require qualified exact period fixing schedules', True)

    # Curve eligibility is checked independently of the existence of an assignment row.
    capacity = manifest['ppa']['capacityCurve']
    draft = str(uuid4())
    changes = {'location_valuation_assignments': "||CASE WHEN r.attribute='certificate' THEN jsonb_build_object('curve_code',NULL,'shaped_curve_code',"
               + f"(SELECT shaped_curve_code FROM market_data.location_valuation_assignments WHERE revision_id='{revision}' AND shaped_curve_code IS NOT NULL LIMIT 1)) ELSE '{{}}'::jsonb END"}
    reject('BEGIN;' + clone(draft, changes) + seal(draft) + 'ROLLBACK;', 'ppa_reference_definition')
    check('certificate assignments reject shaped interval curves', True)
    draft = str(uuid4())
    changes = {'contracts': f"||CASE WHEN r.id IN(SELECT contract_id FROM market_data.curve_members WHERE revision_id='{revision}' AND curve_code='{capacity}') THEN jsonb_build_object('expiry_at',r.expiry_at-interval '1 second') ELSE '{{}}'::jsonb END"}
    reject('BEGIN;' + clone(draft, changes) + seal(draft) + 'ROLLBACK;', 'ppa_reference_definition')
    check('capacity contracts remain live through their exact fixing instant', True)
    boundary = f"market_data.ppa_day_boundary(at,'{reference}','{ref_revision}','synthetic_eastern')"
    check('operating-day proof accepts valid DST-day endpoints and refuses noon or unsupported years',
          query("SELECT bool_and(" + boundary + "=expected) FROM(VALUES('2031-11-02T04:00:00Z'::timestamptz,true),"
                "('2031-11-03T05:00:00Z'::timestamptz,true),('2031-11-02T16:00:00Z'::timestamptz,false),"
                "('1999-12-31T05:00:00Z'::timestamptz,false),('2100-01-02T05:00:00Z'::timestamptz,false))t(at,expected);") == 't')

    outside = str(UUID(query('BEGIN;' + runtime(issue(), f"SELECT security.capture_date_context('{catalog}','2019-01-01',NULL);") + 'COMMIT;')))
    for table in tables:
        rows = f"SELECT count(*) FROM market_data.{table} WHERE revision_id='{revision}';"
        expected = query(rows)
        check(table + ' is readable under current authority', query('BEGIN;' + runtime(issue(), rows) + 'ROLLBACK;') == expected)
        check(table + ' is hidden without a bound context', query('SET SESSION AUTHORIZATION preview_runtime;' + rows) == '0')
        check(table + ' retains restrictive visibility', query(f"BEGIN;CREATE POLICY synthetic_allow ON market_data.{table} FOR SELECT TO openetrm_runtime USING(true);"
              + 'SET LOCAL SESSION AUTHORIZATION preview_runtime;' + rows + 'ROLLBACK;') == '0')
        check(table + ' respects historical definition visibility', query('BEGIN;' + runtime(issue(),
              f"SELECT security.use_market_context('{catalog}','{outside}');" + rows) + 'ROLLBACK;') == '0')
        for scope, scope_catalog in [('market_catalog_scopes', catalog), ('reference_catalog_scopes', reference)]:
            token = issue()
            revoke = f"DELETE FROM security.{scope} WHERE catalog_id='{scope_catalog}' AND tenant_id='{tenant}' AND workspace_id='{workspace}';"
            check(table + ' follows current ' + scope + ' revocation', query('BEGIN;' + revoke + runtime(token, rows) + 'ROLLBACK;') == '0')
        for operation in ('UPDATE', 'DELETE'):
            statement = f"UPDATE market_data.{table} SET revision_id=revision_id WHERE revision_id='{revision}';" if operation == 'UPDATE' else f"DELETE FROM market_data.{table} WHERE revision_id='{revision}';"
            reject('BEGIN;' + statement + 'ROLLBACK;', '55000')
        draft = str(uuid4())
        copy = (f"INSERT INTO market_data.{table} SELECT(jsonb_populate_record(NULL::market_data.{table},to_jsonb(r)||"
                f"jsonb_build_object('revision_id','{draft}'))).* FROM market_data.{table} r WHERE revision_id='{revision}' LIMIT 1;")
        reject('BEGIN;' + draft_header(draft) + runtime(issue(), copy) + 'ROLLBACK;', '55000')
        check(table + ' is immutable and cannot be authored with read authority', True)
    selected_source = f"SELECT count(*) FROM market_data.volume_sources WHERE revision_id='{revision}' AND code='{first_source['code']}';"
    check('a hidden required series hides its volume-source binding', query("BEGIN;CREATE POLICY synthetic_hide ON market_data.interval_series AS RESTRICTIVE FOR SELECT TO openetrm_runtime "
          + f"USING(id<>'{first_source['forecast_series_id']}');" + runtime(issue(), selected_source) + 'ROLLBACK;') == '0')
    check('metadata helper owner is isolated and cannot bypass runtime row restrictions', query("""
      SELECT NOT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls
        AND pg_has_role(r.oid,'openetrm_runtime','MEMBER')
        AND NOT pg_has_role('openetrm_runtime',r.oid,'MEMBER')
        AND NOT EXISTS(SELECT 1 FROM pg_class WHERE relowner=r.oid AND relkind IN('r','p'))
      FROM pg_roles r WHERE rolname='openetrm_interval_metadata_reader';
    """) == 't')
    header = f"market_data.read_interval_series_header('{first_source['forecast_series_id']}')"
    hidden = ("CREATE POLICY synthetic_hide ON market_data.interval_series AS RESTRICTIVE FOR SELECT TO openetrm_runtime "
              + f"USING(id<>'{first_source['forecast_series_id']}');")
    check('a current header read also respects a hidden required series',
          query('BEGIN;' + hidden + runtime(issue(), f"SELECT {header}::text;") + 'ROLLBACK;') == 'null')
    retained = str(UUID(query('BEGIN;' + runtime(issue(),
        f"SELECT security.capture_date_context('{catalog}','2020-12-18',NULL);") + 'COMMIT;')))
    bind = f"SELECT security.use_market_context('{catalog}','{retained}');"
    probe = f"""SELECT json_build_object('binding',({selected_source[:-1]}),
      'header',{header},'raw',(SELECT count(*) FROM market_data.interval_series
        WHERE id='{first_source['forecast_series_id']}'))::text;"""
    visible = json.loads(query('BEGIN;' + runtime(issue(), bind + probe) + 'ROLLBACK;'))
    check('a bound read exposes only authorized immutable metadata and its exact source binding',
          visible['binding'] == 1 and visible['header']['id'] == first_source['forecast_series_id'] and visible['raw'] == 0)
    invisible = json.loads(query('BEGIN;' + hidden + runtime(issue(), bind + probe) + 'ROLLBACK;'))
    check('bound historical source and header reads retain arbitrary series restrictions',
          invisible == {'binding': 0, 'header': None, 'raw': 0})
    energy_assignments = f"SELECT count(*) FROM market_data.location_valuation_assignments WHERE revision_id='{revision}' AND attribute='energy';"
    check('hidden collected sources hide their energy assignments', query("BEGIN;CREATE POLICY synthetic_hide ON power_data.sources AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);"
          + runtime(issue(), energy_assignments) + 'ROLLBACK;') == '0')
    assignment = json.loads(query(f"SELECT row_to_json(r) FROM market_data.location_valuation_assignments r WHERE revision_id='{revision}' AND attribute='energy' AND curve_code IS NOT NULL LIMIT 1;"))
    draft = str(uuid4())
    assignment['revision_id'] = draft
    retained = json.dumps(assignment).replace("'", "''")
    insert = "INSERT INTO market_data.location_valuation_assignments SELECT(jsonb_populate_record(NULL::market_data.location_valuation_assignments,'" + retained + "'::jsonb)).*;"
    check('definition authors can bind a visible exact energy assignment',
          query('BEGIN;' + clone(draft, omitted={'location_valuation_assignments': ' AND false'})
                + runtime(issue('editor'), insert + f"SELECT count(*) FROM market_data.location_valuation_assignments WHERE revision_id='{draft}';") + 'ROLLBACK;') == '1')
    token = issue('editor')
    reject('BEGIN;' + clone(draft, omitted={'location_valuation_assignments': ' AND false'})
           + "CREATE POLICY synthetic_hide ON power_data.sources AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);"
           + runtime(token, insert) + 'ROLLBACK;', '42501')
    check('definition authors cannot bind an energy assignment to an invisible collected source', True)
    return passed
