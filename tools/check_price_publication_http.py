"""Publication authoring and normalized final ingestion over the owned preview API."""
from copy import deepcopy
import json
from uuid import uuid4

from preview_client import PreviewClient


def prepare_market(market, manifest):
    """One compatible quote without a fixing binding, before the fixture is sealed."""
    marker = 'UPDATE ref_data.revisions SET'
    assert market.count(marker) == 1
    catalog, revision = manifest['referenceCatalogId'], manifest['referenceRevisionId']
    alias = ("INSERT INTO ref_data.quote_specifications (catalog_id,revision_id,code,commodity_code,kind,quantity_code,currency_code,multiplier,trading_tick,clearing_increment,minimum,maximum) "
             "SELECT catalog_id,revision_id,'synthetic_publication_quote_alias',commodity_code,kind,quantity_code,currency_code,multiplier,trading_tick,clearing_increment,minimum,maximum "
             f"FROM ref_data.quote_specifications WHERE catalog_id='{catalog}' AND revision_id='{revision}' AND code='test_gas_outright';\n")
    return market.replace(marker, alias + marker, 1)


def run_checks(url, query, manifest):
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'viewer'})
    frame = next(f for f in client.call('workspace')['frames'] if f['at'].startswith('2021-01-15T08:00:00'))
    snapshot = frame['id']
    catalog = client.call('price-publications?snapshotId=' + snapshot)
    assert not catalog['canEdit'] and not catalog['canIngest']
    client.call('profile', {'profile': 'editor'})
    catalog = client.call('price-publications?snapshotId=' + snapshot)
    assert catalog['canEdit'] and catalog['canIngest']
    fixing_quote = query("SELECT quote_code FROM market_data.index_fixing_observations WHERE "
                         f"catalog_id='{manifest['catalogId']}' AND revision_id='{manifest['revisionId']}' "
                         "AND index_code='test_price' AND observation_from='2021-01-01' AND observation_to='2021-02-01';")
    index = next(i for i in catalog['indices'] if i['key']['code'] == 'test_price' and i['quote']['code'] == fixing_quote)
    definition = dict(key=dict(catalogId=catalog['catalogId'], id=str(uuid4()), version=1), references=catalog['references'],
                      publisher=dict(key=dict(catalogId=catalog['catalogId'], id=str(uuid4()), version=1), name='Synthetic API publisher'),
                      name='Synthetic API monthly report', commodities=[index['commodity']],
                      indices=[{k:v for k,v in index.items() if k != 'name'} | dict(symbol='SYNTH_API_GAS', statistic='monthly_index')],
                      calendar=catalog['calendars'][0]['reference'], clock=catalog['clocks'][0]['reference'], implementation='gas_monthly_explicit_v1')
    request = dict(requestId=str(uuid4()),snapshotId=snapshot,expectedVersion=0,definition=definition)
    saved = client.call('price-publications/save',request)
    assert saved == definition, (saved,definition)
    assert client.call('price-publications/save',request) == saved
    assert client.call('price-publications/save',request | {'requestId':str(uuid4())}) == saved
    bad = deepcopy(request);bad['requestId']=str(uuid4());bad['definition']['indices'][0]['location']['code']='unknown_location'
    assert client.call('price-publications/save',bad,expected=422)['code']=='publication_index'
    client.call('profile', {'profile':'viewer'})
    client.call('price-publications/save',request,expected=403)
    client.call('profile', {'profile':'editor'})

    occurrence = dict(publication=saved['key'],assessmentFrom='2020-12-28T12:00:00Z',assessmentTo='2020-12-30T20:00:00Z',
                      assessmentDate='2021-01-01',periods=[{'from':'2021-01-01','to':'2021-02-01'}],expectedReleaseAt='2021-01-01T20:00:00Z')
    preview = client.call('price-publications/preview',dict(snapshotId=snapshot,definition=saved,occurrence=occurrence))
    assert preview['coveredPeriods']==occurrence['periods']
    binding = preview['bindings'][0]['periods'][0]
    assert binding['available'] and binding['fixing']['fixingAt'] != preview['expectedReleaseAt']
    alias = next(i for i in catalog['indices'] if i['key']['code'] == 'test_price' and i['quote']['code'] == 'synthetic_publication_quote_alias')
    alternative = deepcopy(saved)
    alternative['indices'][0]['quote'] = alias['quote']
    alternative['indices'][0]['unit'] = alias['unit']
    unavailable = client.call('price-publications/preview', dict(snapshotId=snapshot,definition=alternative,occurrence=occurrence))['bindings'][0]['periods'][0]
    assert not unavailable['available'] and unavailable['fixing'] is None, 'A compatible quote has no fixing unless the exact quote is bound: ' + repr(unavailable)
    occurrence_request = dict(requestId=str(uuid4()),snapshotId=snapshot,id=str(uuid4()),occurrence=occurrence)
    scheduled = client.call('price-publications/occurrences/save',occurrence_request)
    assert scheduled['id']==occurrence_request['id'] and scheduled['occurrence']['periods']==occurrence['periods']
    assert client.call('price-publications/occurrences/save',occurrence_request)==scheduled
    reloaded = client.call('price-publications?snapshotId='+snapshot)
    assert any(o['id']==scheduled['id'] for o in reloaded['occurrences'])

    ingest = dict(requestId=str(uuid4()),snapshotId=snapshot,occurrenceId=scheduled['id'],indexCode=index['key']['code'],
                  sourceCode=binding['fixing']['source']['code'],sourceEventId='synthetic-http-'+str(uuid4()),sourceRevisionId='initial',
                  publishedAt='2020-12-31T12:05:00Z',value='3.27',correctsReleaseId=None)
    malformed=ingest|dict(publishedAt='2020-12-31T12:05:00.Z')
    assert client.call('price-publications/ingest',malformed,expected=400)['code']=='request_format'
    received = client.call('price-publications/ingest',ingest)
    assert len(received['observationIds'])==1 and received['stage']=='final' and received['value']=='3.27'
    assert client.call('price-publications/ingest',ingest)==received
    assert client.call('price-publications/ingest',ingest | {'requestId':str(uuid4())})==received
    correction = ingest | dict(requestId=str(uuid4()),sourceRevisionId='correction',publishedAt='2021-01-02T12:05:00Z',value='3.28',correctsReleaseId=received['releaseId'])
    corrected = client.call('price-publications/ingest',correction)
    assert corrected['correctsReleaseId']==received['releaseId'] and corrected['value']=='3.28'
    stored = json.loads(query("SELECT row_to_json(x) FROM(SELECT role,publication_state,value::text,corrects_id,observed_at FROM market_data.observations "
                              f"WHERE id='{corrected['observationIds'][0]}')x;"))
    assert stored['role']=='index_fixing' and stored['publication_state']=='final' and stored['value']=='3.28'
    assert stored['corrects_id']==received['observationIds'][0] and stored['observed_at'].startswith('2020-12-31T12:05:00')
    assert query(f"SELECT value::text FROM market_data.observations WHERE id='{received['observationIds'][0]}';")=='3.27'
    receipts = client.call('price-publications?snapshotId='+snapshot)['releases']
    assert {r['releaseId'] for r in receipts} >= {received['releaseId'],corrected['releaseId']}
    client.call('profile', {'profile':'viewer'})
    client.call('price-publications/ingest',ingest,expected=403)
    print('PASS publication HTTP: permission-aware catalog, immutable save/replay, exact schedule preview, saved occurrence, final ingestion and immutable correction',flush=True)
