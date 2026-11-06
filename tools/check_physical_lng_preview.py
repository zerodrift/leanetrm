"""Native LNG entry checks against a newly owned stock PostgreSQL fixture."""
from decimal import Decimal
from uuid import uuid4

from check_lng_cargo_schema import physical_selection
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url, query, manifest):
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile':'trader'})
    frame = initial_frame(client.call('workspace'), '2020-12-18')
    context = {'snapshotId':frame['id'], 'convention':'physical_lng_cargo_v1', 'knowledgeMode':'current'}
    references = client.call('lng/references', context)
    assert {p['code'] for p in references['products']} == {'synthetic_lng_product','synthetic_lng_unpriced'}
    profile = references['profiles'][0]
    body = {**context, **trade_booking(), 'operationId':str(uuid4()), 'bookId':references['books'][0]['id'],
            'profile':profile['code'], 'quantityBasis':profile['quantityBasis'], 'tolerance':profile['tolerance'], 'discounting':profile['discounting'],
            'physical':physical_selection(manifest), 'loadFrom':'2021-01-04T00:00:00Z', 'loadTo':'2021-01-05T00:00:00Z',
            'dischargeFrom':'2021-01-14T00:00:00Z', 'dischargeTo':'2021-01-15T00:00:00Z', 'paymentAt':'2021-01-25T00:00:00Z',
            'quantity':'980', 'quantityCode':'synthetic_lng_mass', 'quantityUnit':'metric_tonne', 'conversionCode':'synthetic_lng_factor',
            'incoterms':{'edition':'incoterms_2020', 'rule':'incoterms_2020_cif', 'namedPlace':'synthetic_lng_named_port'},
            'direction':'receive', 'considerationDirection':'pay', 'fixedPrice':'10'}
    resolve = {**context, 'physical':body['physical'], 'dischargeFrom':body['dischargeFrom'], 'dischargeTo':body['dischargeTo']}
    mapping = client.call('lng/resolve', resolve)
    unit = next(u['symbol'] for u in references['units'] if u['code'] == 'mmbtu_it')
    assert mapping['curve'] == 'synthetic_lng_forward' and mapping['energyUnit'] == unit, mapping
    missing = client.call('lng/resolve', {**resolve, 'physical':{**body['physical'],'product':'synthetic_lng_unpriced'}}, expected=422)
    assert missing['code'] == 'lng_price_mapping_missing', missing
    client.call('lng/preview', {**body, 'curve':'synthetic_lng_forward'}, expected=400)
    client.call('lng/preview', {k:v for k,v in body.items() if k!='conversionCode'}, expected=400)
    for change in [{'profile':None}, {'quantityBasis':None}, {'tolerance':None}, {'discounting':None},
                   {'direction':None}, {'considerationDirection':None}, {'conversionCode':None}, {'quantity':'0'},
                   {'quantityUnit':'mmbtu_it'}, {'loadTo':body['dischargeTo']}, {'paymentAt':body['loadTo']},
                   {'loadFrom':'2021-01-04T01:00:00+01:00'}, {'dischargeTo':'2021-02-01T00:00:00.000001Z'},
                   {'paymentAt':'2021-01-25T00:00:00.0000001Z'},
                   {'incoterms':{**body['incoterms'],'rule':'incoterms_2010_cif'}}]:
        client.call('lng/preview', {**body, **change}, expected=422)
    value = client.call('lng/preview', body)
    assert Decimal(value['presentValue']) == 49000, value
    assert value['originalQuantity'] == '980' and value['energyQuantity'] == '49000'
    assert [Decimal(s['presentValue']) for s in value['sides']] == [539000, -490000]
    energy = {**body, 'quantity':'49000', 'quantityCode':'synthetic_lng_energy', 'quantityUnit':'mmbtu_it', 'conversionCode':None}
    assert Decimal(client.call('lng/preview', energy)['presentValue']) == 49000
    assert Decimal(client.call('lng/preview', {**body, 'incoterms':{**body['incoterms'],'rule':'incoterms_2020_fob'}})['presentValue']) == 49000
    booked = client.call('lng/book', body)
    assert client.call('lng/book', body) == booked
    assert client.call('lng/status', {'operationId':body['operationId']}) == booked
    client.call('lng/book', {**body, 'quantity':'981'}, expected=409)
    assert query(f"SELECT count(*) FROM trading.lng_trade_revisions WHERE catalog_id='{manifest['catalogId']}';") == '1'
    loaded = client.call('lng/editor', {'context':context, 'tradeId':booked['tradeId']})
    assert loaded['terms']['incoterms'] == body['incoterms']
    assert loaded['terms']['conversionCode'] == body['conversionCode']
    assert Decimal(loaded['valuation']['presentValue']) == 49000
    listed = next(row for row in client.call('lng/directory', context)['trades'] if row['tradeId'] == booked['tradeId'])
    assert listed['tradeReference'].startswith('TR-'), listed
    from check_physical_operations import contract_delivery_checks
    contract_delivery_checks(client,'trading/lng',dict(context={**context,'convention':'trading_workspace_v1'},tradeId=booked['tradeId'],bindingId=booked['instrumentId'],revision=1,instrumentType='physical_lng'),'49000',body['loadFrom'],body['dischargeTo'],'lng_actuals',query,manifest)
    historical = {**context, 'knowledgeMode':'retained', 'contextId':value['context']['contextId']}
    assert not any(row['tradeId'] == booked['tradeId'] for row in client.call('lng/directory', historical)['trades'])
    client.call('lng/editor', {'context':historical, 'tradeId':booked['tradeId']}, expected=422)
    client.call('profile', {'profile':'risk'})
    assert not client.call('lng/references', context)['canBook']
    client.call('lng/book', {**body, 'operationId':str(uuid4())}, expected=403)
    client.call('lng/editor', {'context':context, 'tradeId':booked['tradeId']})
    client.call('profile', {'profile':'viewer'})
    client.call('lng/references', context, expected=403)
    client.call('lng/editor', {'context':context, 'tradeId':booked['tradeId']}, expected=403)
    print('PASS native LNG: separate exact references, derived source, explicit units/conversion and Incoterms, normalized PV, booking replay and retained inspection', flush=True)
    return booked
