"""Independent durable PPA contracts against the launcher's disposable database.

These commands deliberately bypass managed compilation. Expected coverage and term
documents are authored here, rather than obtained from storage reconstruction.
"""
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
import json
import time
from uuid import UUID, uuid4
from preview_fixture import trade_booking


def run_checks(query, manifest):
    catalog, definition, reference, ref_revision, tenant, workspace = [str(UUID(manifest[k])) for k in
        ('catalogId', 'revisionId', 'referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId')]
    passed = 0

    def check(name, condition):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS PPA bookings storage: ' + name, flush=True)

    def encoded(value):
        return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"

    def issue(profile='trader', lifetime=120):
        return query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                     f"'https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',{lifetime});")

    def runtime(sql, token=None):
        return f"SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token or issue()}');" + sql

    def command(payload, operation=None):
        return f"SELECT security.save_ppa_booking('{catalog}','{operation or uuid4()}',{encoded(payload)});"

    def save(payload, operation=None):
        return json.loads(query('BEGIN;' + runtime(command(payload, operation)) + 'COMMIT;'))

    def reject(sql, code):
        try:
            query('\\set VERBOSITY verbose\n' + sql)
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Storage accepted invalid PPA command: ' + code)

    payment = {'frequency': 'monthly', 'rule': 'days_after_period_end', 'days': 18,
               'dayOfMonth': None, 'dayBasis': 'calendar_days', 'shortMonth': None,
               'adjustment': 'unadjusted', 'calendar': None, 'implementation': 'monthly_payment_date_rules_v1'}
    empty = {'kind': 'slots', 'sources': [], 'stage': None, 'capacity': None, 'energy': None,
             'hours': None, 'slots': [{'role': 'energy_delivery', 'ordinal': 0}], 'assets': []}
    profile = {**deepcopy(empty), 'kind': 'profile', 'slots': [], 'capacity': 'test_capacity_quantity',
               'energy': 'test_power_quantity', 'hours': {'membership': 'all', 'touProduct': None,
               'touVersion': None, 'calendar': 'synthetic_eastern_holidays'}}
    delivery_side = {'kind': 'energy', 'quantity': profile, 'paymentRule': payment,
                     'location': 'test_hub_a', 'fixedPrice': None, 'indexPrice': None, 'attribute': None}
    fixed = {'kind': 'flat', 'quantity': 'test_power_quantity', 'multiplier': '1', 'price': '45.125',
             'rate': None, 'baseDate': None, 'basis': None, 'steps': []}
    payment_side = {'kind': 'fixed', 'quantity': empty, 'paymentRule': payment,
                    'location': None, 'fixedPrice': fixed, 'indexPrice': None, 'attribute': None}
    terms = {'definitionRevisionId': definition, 'referenceCatalogId': reference,
             'referenceRevisionId': ref_revision, 'delivery': {'clock': 'synthetic_eastern',
             'intervalMinutes': 60, 'from': '2031-01-01', 'to': '2032-01-01'},
             'currency': 'usd', 'unitQuantity': 'test_capacity_quantity', 'discounting': False,
             'implementation': 'ppa_instrument_v2', 'normalizationAssets': [], 'memberships': [
                 {'role': 'energy_delivery', 'ordinal': 0, 'coefficient': '1', 'side': delivery_side},
                 {'role': 'strike_payment', 'ordinal': 0, 'coefficient': '-1', 'side': payment_side}]}
    book = str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{catalog}','{book}','d0000000-0000-0000-0000-000000000001',"
          "'Synthetic storage checks',1,'2020-01-01T00:00:00Z');")
    request = {'snapshotId': str(uuid4()), 'action': 'booked', 'tradeId': None,
               'expectedRevision': None, 'bookId': book,
               'valuationAt': '2020-12-15T16:00:00Z', 'effectiveAt': '2020-12-15T16:00:00Z',
               'boundaryAt': None, 'coverageFrom': '2031-01-01T05:00:00Z', 'coverageTo': '2032-01-01T05:00:00Z',
               'quantity': '80', 'reason': 'Synthetic durable PPA check', 'memo': '',
               'booking': trade_booking(), 'terms': terms}
    operation = str(uuid4())
    # Independent sessions start from the same operation identity and command.
    with ThreadPoolExecutor(max_workers=2) as pool:
        first, replay = list(pool.map(lambda _: save(request, operation), range(2)))
    trade = first['tradeId']
    check('concurrent identical commands commit one trade, revision and binding', first == replay and
          query(f"SELECT count(*) FROM trading.ppa_trade_revisions WHERE operation_id='{operation}';") == '1' and
          first['revision'] == 1 and len(first['bindings']) == 1)
    instrument = first['bindings'][0]['instrumentId']
    document_sql = f"SELECT security.read_ppa_terms('{catalog}','{instrument}');"
    retained = json.loads(query('BEGIN;' + runtime(document_sql) + 'ROLLBACK;'))
    check('typed storage reconstructs the independently authored exact terms', retained['terms'] == terms and
          {(x['role'], x['ordinal']) for x in retained['sideIds']} == {('energy_delivery', 0), ('strike_payment', 0)})
    from check_ppa_side_schedule_schema import run_storage_checks as check_schedule_retention
    check_schedule_retention(query, request, save, catalog, instrument)
    before_amendment = query('BEGIN;' + runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-15',NULL);") + 'COMMIT;')

    def historical(context, sql):
        return runtime(f"SELECT security.use_market_context('{catalog}','{context}');"
                       f"SELECT security.use_trading_context('{catalog}','2020-12-15T16:00:00Z');" + sql)

    scope = f"SELECT revision FROM security.ppa_scope_heads('{catalog}','trades',ARRAY['{trade}'::uuid]);"
    reject('BEGIN;' + runtime(command({**request, 'quantity': '81'}, operation)) + 'ROLLBACK;', 'ppa_conflict')
    check('a committed operation identity refuses changed economics', True)
    equivalent = deepcopy(request)
    equivalent['quantity'] = '80.000'
    equivalent['terms']['memberships'][1]['side']['fixedPrice']['price'] = '45.12500'
    equivalent['terms']['memberships'][0]['coefficient'] = '1.00'
    sibling = save(equivalent)
    check('numeric spelling does not duplicate exact reusable instruments or sides',
          sibling['bindings'][0]['instrumentId'] == instrument)
    later_first = deepcopy(request)
    later_first['valuationAt'] = later_first['effectiveAt'] = '2020-12-18T16:00:00Z'
    later_first['terms']['memberships'][1]['side']['fixedPrice']['price'] = '71'
    earlier_second = {**deepcopy(later_first), 'valuationAt': '2020-12-14T16:00:00Z', 'effectiveAt': '2020-12-14T16:00:00Z'}
    later_operation, earlier_operation = str(uuid4()), str(uuid4())
    availability = (command(later_first, later_operation) + command(earlier_second, earlier_operation) + 'RESET SESSION AUTHORIZATION;' +
                    f"SELECT i.effective_at<='2020-12-14T16:00:00Z'::timestamptz FROM trading.ppa_instruments i "
                    f"JOIN trading.ppa_revision_bindings b ON(b.catalog_id,b.instrument_id)=(i.catalog_id,i.instrument_id) "
                    f"JOIN trading.ppa_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(b.catalog_id,b.trade_id,b.revision) "
                    f"WHERE r.operation_id='{earlier_operation}';")
    availability_result = query('BEGIN;' + runtime(availability) + 'ROLLBACK;').splitlines()
    check('later-first term reuse remains visible at an admitted earlier effective context',
          len(availability_result) == 3 and availability_result[2] == 't' and
          json.loads(availability_result[0])['bindings'][0]['instrumentId'] == json.loads(availability_result[1])['bindings'][0]['instrumentId'])

    def roundtrip(label, variant):
        payload = {**request, 'terms': variant}
        sql = (f"WITH saved AS(SELECT security.save_ppa_booking('{catalog}','{uuid4()}',{encoded(payload)}) r) "
               f"SELECT security.read_ppa_terms('{catalog}',(r#>>'{{bindings,0,instrumentId}}')::uuid)->'terms' FROM saved;")
        check(label + ' survives an independent typed-storage round trip',
              json.loads(query('BEGIN;' + runtime(sql) + 'ROLLBACK;')) == variant)

    duplicate = deepcopy(terms)
    charge = deepcopy(duplicate['memberships'][1]); charge['role'] = 'fixed_charge'
    duplicate['memberships'].append(charge)
    roundtrip('identical strike and charge keep independent explicit memberships', duplicate)
    wrong_units = deepcopy(request)
    wrong_units['terms']['memberships'][0]['side']['quantity']['capacity'] = 'test_power_quantity'
    reject('BEGIN;' + runtime(command(wrong_units)) + 'ROLLBACK;', 'ppa_quantity_unit')
    check('storage refuses incompatible profile capacity units before retention', True)
    later_bad_month = deepcopy(request)
    later_bad_month['terms']['delivery'].update({'from':'2031-02-01','to':'2031-05-01'})
    later_bad_month.update(coverageFrom='2031-02-01T05:00:00Z',coverageTo='2031-05-01T04:00:00Z')
    for member in later_bad_month['terms']['memberships']:
        member['side']['paymentRule'].update(rule='day_of_following_month',days=None,dayOfMonth=31,dayBasis=None,shortMonth='reject')
    reject('BEGIN;' + runtime(command(later_bad_month)) + 'ROLLBACK;', 'short_month')
    check('every original payment month is validated before durable retention', True)

    for kind, changes in [
            ('escalated', {'rate': '0.025', 'baseDate': '2030-01-01', 'basis': 'anniversary_step_v1'}),
            ('stepped', {'price': None, 'steps': [{'from': '2030-01-01', 'price': '40'}, {'from': '2031-07-01', 'price': '50'}]})]:
        variant = deepcopy(terms)
        variant['memberships'][1]['side']['fixedPrice'].update(kind=kind, **changes)
        roundtrip(kind + ' fixed strike', variant)
        if kind == 'stepped':
            reversed_steps = deepcopy(variant)
            reversed_steps['memberships'][1]['side']['fixedPrice']['steps'].reverse()
            compare = (f"SELECT security.save_ppa_booking('{catalog}','{uuid4()}',{encoded({**request, 'terms': variant})})#>>'{{bindings,0,instrumentId}}';"
                       f"SELECT security.save_ppa_booking('{catalog}','{uuid4()}',{encoded({**request, 'terms': reversed_steps})})#>>'{{bindings,0,instrumentId}}';")
            identities = query('BEGIN;' + runtime(compare) + 'ROLLBACK;').splitlines()
            check('dated strike steps reuse their canonical domain order', len(identities) == 2 and identities[0] == identities[1])
    for curve in (manifest['intervalCurves']['marked'], manifest['intervalCurves']['shaped']):
        variant = deepcopy(terms)
        side = variant['memberships'][1]['side']
        variant['memberships'][1]['role'] = 'indexed_payment'
        side.update(kind='indexed', fixedPrice=None, indexPrice={'curve': curve, 'quantity': 'test_power_quantity',
                    'multiplier': '1', 'indexMultiplier': '0.95', 'settlement': None, 'adder': '-1.25'})
        roundtrip('explicit index curve ' + curve, variant)
    with_certificate = deepcopy(terms)
    with_certificate['normalizationAssets'] = list(manifest['ppa']['assets'])
    volume = {**deepcopy(empty), 'kind': 'volume', 'slots': [], 'stage': 'generated',
              'sources': [f'{a}_60' for a in manifest['ppa']['assets']]}
    certificate = {'kind': 'certificate', 'quantity': volume, 'paymentRule': payment, 'location': None,
                   'fixedPrice': None, 'indexPrice': None, 'attribute': {'quantity': 'test_rec_quantity',
                   'conversion': '1', 'eligibility': 'asset_generation_at_stage_v1',
                   'holding': 'held_at_current_vintage_mark_v1', 'disposition': 'none_recorded_v1', 'rule': None}}
    with_certificate['memberships'].append({'role': 'certificate_delivery', 'ordinal': 0, 'coefficient': '1', 'side': certificate})
    roundtrip('certificate holdings and generated source-stage policies', with_certificate)
    normalized_operation=str(uuid4())
    normalization_probe=(command({**request,'terms':with_certificate},normalized_operation)+'RESET SESSION AUTHORIZATION;'+
        "SELECT bool_and(s.normalization_policy='sum_capacity_in_force_v1' AND s.normalization_basis_code='test_capacity_quantity' "
        f"AND s.id<>(SELECT m.side_id FROM trading.ppa_memberships m WHERE(m.catalog_id,m.instrument_id,m.role)=('{catalog}','{instrument}','strike_payment')) "
        f"AND (SELECT count(*) FROM trading.ppa_side_normalization_assets n WHERE(n.catalog_id,n.side_id)=(s.catalog_id,s.id))={len(with_certificate['normalizationAssets'])}) "
        "FROM trading.ppa_trade_revisions r JOIN trading.ppa_revision_bindings b ON(b.catalog_id,b.trade_id,b.revision)=(r.catalog_id,r.trade_id,r.revision) "
        "JOIN trading.ppa_memberships m ON(m.catalog_id,m.instrument_id)=(b.catalog_id,b.instrument_id) "
        "JOIN trading.ppa_sides s ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id) "
        f"WHERE r.operation_id='{normalized_operation}' AND m.role='strike_payment';")
    check('a slot payment owns its complete normalizer and cannot reuse a differently normalized side',
          query('BEGIN;'+runtime(normalization_probe)+'ROLLBACK;').splitlines()[-1]=='t')
    capacity = deepcopy(terms)
    capacity['normalizationAssets'] = list(manifest['ppa']['assets'])
    capacity['memberships'][0].update(role='capacity_delivery')
    capacity['memberships'][0]['side'].update(kind='capacity', location=None,
        quantity={**deepcopy(empty), 'kind': 'accredited', 'slots': [], 'assets': list(manifest['ppa']['assets'])},
        attribute={'quantity': 'test_ppa_capacity_days_quantity', 'conversion': None, 'eligibility': None,
                   'holding': None, 'disposition': None, 'rule': 'mw_day_per_operating_day_v1'})
    capacity['memberships'][1].update(role='capacity_payment')
    capacity['memberships'][1]['side']['quantity']['slots'][0]['role'] = 'capacity_delivery'
    capacity['memberships'][1]['side']['fixedPrice']['quantity'] = 'test_ppa_capacity_days_quantity'
    roundtrip('accredited capacity and its exact quantity binding', capacity)
    reject('BEGIN;' + runtime(command({**request, 'terms': capacity, 'coverageFrom': '2031-01-02T05:00:00Z'})) + 'ROLLBACK;', 'ppa_segment_boundary')
    check('capacity coverage refuses a partial operating month', True)
    for path in ('price', 'multiplier'):
        malformed = deepcopy(request)
        malformed['terms']['memberships'][1]['side']['fixedPrice'][path] = None
        reject('BEGIN;' + runtime(command(malformed)) + 'ROLLBACK;', 'ppa_input' if path == 'multiplier' else 'ppa_price_rule')
    malformed = deepcopy(request)
    malformed['terms']['memberships'][1]['role'] = 'indexed_payment'
    malformed['terms']['memberships'][1]['side'].update(kind='indexed', fixedPrice=None,
        indexPrice={'curve': manifest['intervalCurves']['marked'], 'quantity': 'test_power_quantity',
                    'multiplier': '1', 'indexMultiplier': '1', 'settlement': None, 'adder': None})
    reject('BEGIN;' + runtime(command(malformed)) + 'ROLLBACK;', 'ppa_price_rule')
    malformed = deepcopy(request)
    two_strikes = [deepcopy(terms['memberships'][1]), deepcopy(terms['memberships'][1])]
    for ordinal, member in enumerate(two_strikes):
        member['ordinal'] = ordinal
        member['side']['quantity'] = deepcopy(profile)
    two_strikes[1]['side']['fixedPrice']['price'] = '46'
    malformed['terms']['memberships'] = two_strikes
    reject('BEGIN;' + runtime(command(malformed)) + 'ROLLBACK;', 'ppa_composition')
    check('storage refuses missing explicit prices and unsupported compositions', True)

    def amendment(head, boundary='2031-07-01T04:00:00Z', **patch):
        return {**deepcopy(request), 'action': 'amended', 'tradeId': trade, 'expectedRevision': head,
                'booking': None, 'boundaryAt': boundary, 'coverageFrom': boundary, 'quantity': '60', **patch}

    original = query(f"SELECT row_to_json(b)::text FROM trading.ppa_revision_bindings b WHERE trade_id='{trade}' AND revision=1;")
    for label, pin, value, expected in [
            ('definition', 'definitionRevisionId', str(uuid4()), 'ppa_definition_revision'),
            ('reference', 'referenceRevisionId', str(uuid4()), 'ppa_reference_revision')]:
        changed = amendment(1)
        changed['terms'][pin] = value
        reject('BEGIN;' + runtime(command(changed)) + 'ROLLBACK;', expected)
        check(label + ' mismatch is refused atomically even for full future replacement',
              query(f"SELECT max(revision) FROM trading.ppa_trade_revisions WHERE trade_id='{trade}';") == '1' and
              query(f"SELECT row_to_json(b)::text FROM trading.ppa_revision_bindings b WHERE trade_id='{trade}' AND revision=1;") == original)
    change_operation = str(uuid4())
    amended = save(amendment(1), change_operation)
    bindings = amended['bindings']
    check('forward amendment retains original prefix and appends an adjacent quantity tail',
          amended['revision'] == 2 and len(bindings) == 2 and
          [x['quantity'] for x in bindings] == ['80', '60'] and
          bindings[0]['bindingId'] == first['bindings'][0]['bindingId'] and
          bindings[0]['coverageTo'] == bindings[1]['coverageFrom'] and
          all(x['instrumentId'] == instrument for x in bindings))
    check('amendment preserves original terms, identities and revision-one coverage',
          query(f"SELECT row_to_json(b)::text FROM trading.ppa_revision_bindings b WHERE trade_id='{trade}' AND revision=1;") == original and
          json.loads(query('BEGIN;' + runtime(document_sql) + 'ROLLBACK;')) == retained)
    reject('BEGIN;' + runtime(command(amendment(1))) + 'ROLLBACK;', 'ppa_revision_conflict')
    check('stale expected revision cannot fork history', True)
    # Two competing successors deliberately share expected head two.
    def compete(amount):
        try:
            return save(amendment(2, '2031-10-01T04:00:00Z', quantity=amount))
        except RuntimeError as error:
            assert 'ppa_revision_conflict' in str(error), str(error)
            return None
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes = list(pool.map(compete, ('40', '50')))
    winner = next(x for x in outcomes if x is not None)
    check('concurrent amendments admit exactly one successor without partial bindings',
          sum(x is not None for x in outcomes) == 1 and winner['revision'] == 3 and len(winner['bindings']) == 3)
    check('replay after later amendments returns the original retained outcome',
          save(amendment(1), change_operation) == amended)
    check('certified earlier knowledge retains the first exact trade revision',
          query('BEGIN;' + historical(before_amendment, scope) + 'ROLLBACK;') == '1')
    check('an added permissive policy cannot bypass the retained knowledge cutoff',
          query('BEGIN;CREATE POLICY synthetic_allow ON trading.ppa_trade_revisions FOR SELECT TO openetrm_runtime USING(true);'
                + historical(before_amendment, f"SELECT max(revision) FROM trading.ppa_trade_revisions WHERE trade_id='{trade}';")
                + 'ROLLBACK;') == '1')
    reject('BEGIN;' + historical(before_amendment, command(amendment(3))) + 'ROLLBACK;', 'historical_read_only')
    check('historical context cannot author a successor', True)
    termination = amendment(3, '2031-11-01T04:00:00Z', action='terminated', terms=None,
                            quantity=None, coverageFrom=None, coverageTo=None)
    terminated = save(termination)
    check('termination clips originating delivery without rewriting previous revisions',
          terminated['kind'] == 'terminated' and terminated['revision'] == 4 and
          len(terminated['bindings']) == 3 and terminated['bindings'][-1]['coverageTo'].startswith('2031-11-01T04:00:00') and
          query(f"SELECT count(*) FROM trading.ppa_revision_bindings WHERE trade_id='{trade}' AND revision=3;") == '3')
    cancel = {**termination, 'tradeId': sibling['tradeId'], 'expectedRevision': 1,
              'boundaryAt': request['coverageFrom']}
    cancelled = save(cancel)
    check('termination exactly at delivery start retains an explicit empty binding set', cancelled['bindings'] == [])
    after_termination = query('BEGIN;' + runtime(f"SELECT security.capture_date_context('{catalog}','2020-12-15',NULL);") + 'COMMIT;')
    complete_scope = f"SELECT count(*) FROM security.ppa_scope_heads('{catalog}','books',ARRAY['{book}'::uuid]);"
    check('complete scope admission retains terminated and empty-binding trade heads',
          query('BEGIN;' + historical(after_termination, complete_scope) + 'ROLLBACK;') == '2')
    reject('BEGIN;' + historical(after_termination,
           f"SELECT count(*) FROM security.ppa_scope_heads('{catalog}','trades',ARRAY['{trade}'::uuid,'{uuid4()}'::uuid]);")
           + 'ROLLBACK;', '42501')
    check('an incomplete explicit trade selection cannot return a successful subset', True)

    # Every table retains immutable rows and fresh authority rather than granting
    # replay or direct reads a special exemption.
    tables = ('ppa_sides', 'ppa_side_slots', 'ppa_instruments', 'ppa_memberships',
              'ppa_trade_origins', 'ppa_trade_revisions', 'ppa_revision_bindings')
    for table in tables:
        count = f"SELECT count(*) FROM trading.{table} WHERE catalog_id='{catalog}';"
        check(table + ' hides rows without a bound context', query('SET SESSION AUTHORIZATION preview_runtime;' + count) == '0')
        for mutation in (f'UPDATE trading.{table} SET catalog_id=catalog_id;', f'DELETE FROM trading.{table};'):
            reject('BEGIN;' + mutation + 'ROLLBACK;', '55000')
    check('durable terms, revisions and coverage reject update and delete', True)
    for table, scoped_catalog in [('market_catalog_scopes', catalog), ('reference_catalog_scopes', reference)]:
        revoke = (f"DELETE FROM security.{table} WHERE catalog_id='{scoped_catalog}' "
                  f"AND tenant_id='{tenant}' AND workspace_id='{workspace}';")
        reject('BEGIN;' + revoke + runtime(command(request, operation)) + 'ROLLBACK;', '42501')
        reject('BEGIN;' + revoke + runtime(document_sql) + 'ROLLBACK;', '42501')
        reject('BEGIN;' + revoke + historical(before_amendment, scope) + 'ROLLBACK;', '42501')
        check('current ' + table + ' revocation rejects replay and retained-term reads', True)
    reject('BEGIN;' + runtime(command(request), issue('viewer')) + 'ROLLBACK;', '42501')
    check('read authority cannot invoke the booking operation', True)
    with ThreadPoolExecutor(max_workers=1) as pool:
        locked = pool.submit(query, 'BEGIN;SELECT pg_advisory_xact_lock(730849102);SELECT pg_sleep(2);COMMIT;')
        deadline = time.monotonic() + 1
        while query("SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND objid=730849102 AND granted;") != '1':
            assert time.monotonic() < deadline, 'Synthetic publication gate was not acquired'
            time.sleep(0.01)
        reject('BEGIN;' + runtime(command(request, operation), issue(lifetime=1)) + 'ROLLBACK;', '42501')
        locked.result()
    check('authority expiring at the publication gate refuses an idempotent replay', True)
    check('binding identities are protected by exact bundle foreign keys',
          query("SELECT count(*) FROM pg_constraint WHERE conrelid='trading.ppa_revision_bindings'::regclass "
                "AND contype='f' AND array_length(conkey,1) IN(5,6);") == '2')
    return passed
