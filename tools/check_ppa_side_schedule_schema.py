"""Side-owned PPA geometry contracts on the launcher's disposable database."""
from copy import deepcopy
import json


def encoded(value):
    return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"


def run_checks(query):
    assert query("SELECT to_regclass('trading.ppa_side_schedules') IS NOT NULL;") == 't', 'PPA sides must retain their own concrete delivery shape'
    assert query("SELECT to_regclass('trading.ppa_side_normalization_assets') IS NOT NULL;") == 't', 'A reusable side must own its normalization asset set independently of its instrument'
    assert query("""SELECT count(*) FROM pg_constraint WHERE conrelid='trading.ppa_side_normalization_assets'::regclass
                  AND contype='f' AND confrelid='market_data.generation_assets'::regclass;""") == '1', 'Normalization requires exact asset-version foreign keys'
    assert query("""SELECT count(*) FROM information_schema.columns WHERE table_schema='trading' AND table_name='ppa_sides'
                  AND column_name IN('normalization_basis_code','normalization_policy','settlement_mode','settlement_target_index_code','settlement_index_code','settlement_projection_policy');""") == '6', 'Side normalization and period settlement choices must be retained explicitly'
    assert query("""
        SELECT count(*) FROM pg_constraint WHERE conrelid='trading.ppa_sides'::regclass
         AND confrelid='trading.ppa_side_schedules'::regclass AND contype='f';
    """) == '1', 'A PPA side must pin one exact schedule definition'
    assert query("""
        SELECT count(*) FROM pg_constraint WHERE conrelid='trading.ppa_side_schedules'::regclass AND contype='f'
         AND confrelid IN('ref_data.clock_rules'::regclass,'ref_data.calendars'::regclass,'market_data.tou_product_versions'::regclass);
    """) == '3', 'Schedule clocks, calendars and masks require exact-version relationships'
    assert query("SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='trading.ppa_side_schedules'::regclass;") == 't'
    source = {'kind': 'energy', 'quantity': {'kind': 'profile', 'hours': {'membership': 'peak',
              'touProduct': '10000000-0000-0000-0000-000000000001', 'touVersion': 1, 'calendar': 'synthetic_holidays'}}}
    payment = {'kind': 'fixed', 'quantity': {'kind': 'slots', 'slots': [{'role': 'energy_delivery', 'ordinal': 0}]}}
    spec = {'definitionRevisionId': '20000000-0000-0000-0000-000000000001',
            'referenceCatalogId': '30000000-0000-0000-0000-000000000001',
            'referenceRevisionId': '40000000-0000-0000-0000-000000000001',
            'delivery': {'clock': 'synthetic_clock', 'intervalMinutes': 60, 'from': '2031-11-01', 'to': '2031-12-01'},
            'memberships': [{'role': 'energy_delivery', 'ordinal': 0, 'side': source}]}

    def shape(document, side):
        return json.loads(query(f'SELECT security.ppa_schedule_document({encoded(document)},{encoded(side)});'))

    selected = shape(spec, payment)
    assert selected['membership'] == 'peak' and selected['calendar'] == 'synthetic_holidays'
    changed = deepcopy(spec)
    changed['memberships'][0]['side']['quantity']['hours'] = {'membership': 'all', 'touProduct': None, 'touVersion': None, 'calendar': 'synthetic_holidays'}
    assert shape(changed, payment) != selected, 'A payment slot bound to another mask must have another schedule definition'
    capacity = deepcopy(spec)
    capacity['memberships'][0]['side'] = {'kind': 'capacity', 'quantity': {'kind': 'accredited'}}
    capacity['memberships'][0]['role'] = 'capacity_delivery'
    capacity_payment = deepcopy(payment)
    capacity_payment['quantity']['slots'][0]['role'] = 'capacity_delivery'
    assert shape(capacity, capacity_payment)['cadence'] == 'operating_months', 'Capacity payments inherit the explicit monthly row shape'

    def reject(document, side):
        try:
            shape(document, side)
        except RuntimeError as error:
            assert 'ppa_side_schedule' in str(error), str(error)
        else:
            raise AssertionError('Missing or ambiguous slot schedule was accepted')

    reject({**spec, 'memberships': []}, payment)
    reject({**spec, 'memberships': spec['memberships'] * 2}, payment)
    cyclic = deepcopy(spec)
    cyclic['memberships'][0]['side'] = payment
    reject(cyclic, payment)
    mismatched = deepcopy(spec)
    extra = deepcopy(mismatched['memberships'][0])
    extra['ordinal'] = 1
    extra['side']['quantity']['hours']['membership'] = 'off'
    mismatched['memberships'].append(extra)
    multiple = deepcopy(payment)
    multiple['quantity']['slots'].append({'role': 'energy_delivery', 'ordinal': 1})
    reject(mismatched, multiple)
    assert query("""
        SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
        CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
        WHERE n.nspname='security' AND p.proname IN('ppa_schedule_document','retain_ppa_schedule')
         AND a.grantee=0 AND a.privilege_type='EXECUTE';
    """) == '0', 'Only authorized retention may resolve stored schedule terms'
    print('PASS PPA schedule schema: own shape, exact references, slot masks, monthly capacity and ambiguous-source refusals', flush=True)


def run_storage_checks(query, request, save, catalog, instrument):
    from interval_curve_fixture import tou_product
    from uuid import uuid4
    rows = json.loads(query("SELECT jsonb_agg(jsonb_build_object('role',m.role,'side',s.id,'schedule',s.schedule_id)) "
                      f"FROM trading.ppa_memberships m JOIN trading.ppa_sides s ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id) WHERE(m.catalog_id,m.instrument_id)=('{catalog}','{instrument}');"))
    assert len({r['schedule'] for r in rows}) == 1, 'A linked payment must own the same concrete geometry as its delivery'
    original_payment = next(r for r in rows if r['role'] == 'strike_payment')
    peak = deepcopy(request)
    book=str(uuid4())
    query(f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic schedule geometry checks',publication_order,effective_at FROM trading.books WHERE(catalog_id,id)=('{catalog}','{request['bookId']}');")
    peak['bookId']=book
    peak['terms']['memberships'][0]['side']['quantity']['hours'].update(membership='peak', touProduct=tou_product('pjm'), touVersion=1)
    result = save(peak)
    other = result['bindings'][0]['instrumentId']
    retained = json.loads(query("SELECT jsonb_build_object('side',s.id,'schedule',s.schedule_id) "
                         f"FROM trading.ppa_memberships m JOIN trading.ppa_sides s ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id) WHERE(m.catalog_id,m.instrument_id,m.role)=('{catalog}','{other}','strike_payment');"))
    assert retained['side'] != original_payment['side'] and retained['schedule'] != original_payment['schedule'], 'Different bound delivery masks cannot deduplicate a payment side'
    replay = save(deepcopy(peak))
    assert replay['bindings'][0]['instrumentId'] == other, 'Equivalent resolved masks must retain the exact existing instrument'
    print('PASS PPA schedule retention: bound mask distinguishes shared payment identity and exact retries reuse it', flush=True)
