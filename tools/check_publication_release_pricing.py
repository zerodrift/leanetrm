"""Release-aware source inspection through the owned synthetic preview's real API."""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import json
from uuid import UUID, uuid4, uuid5

from preview_client import PreviewClient



def prepare_market(market, manifest):
    """Independent synthetic physical forwards with explicit last-trade expiries."""
    marker = 'UPDATE market_data.revisions SET'
    assert market.count(marker) == 1
    catalog, revision = manifest['catalogId'], manifest['revisionId']
    expiries = ('2020-12-31T12:00:00Z', '2021-01-31T12:00:00Z', '2021-02-28T12:00:00Z',
                '2021-03-31T12:00:00Z', '2021-04-30T12:00:00Z', '2021-05-31T12:00:00Z')
    pairs = [(f'50000000-0000-0000-0000-{month*5+2:012d}',
              str(uuid5(UUID(catalog), f'synthetic-release-forward:{month}')), expiry)
             for month, expiry in enumerate(expiries)]
    values = ','.join(f"('{old}'::uuid,'{new}'::uuid,'{at}'::timestamptz)" for old, new, at in pairs)
    scope = f"(c.catalog_id,c.revision_id)=('{catalog}','{revision}')"
    sql = f"""
CREATE TEMP TABLE release_test_members(old_id uuid,new_id uuid,expiry timestamptz) ON COMMIT DROP;
INSERT INTO release_test_members VALUES {values};
INSERT INTO market_data.contracts SELECT(jsonb_populate_record(NULL::market_data.contracts,to_jsonb(c)||jsonb_build_object('id',m.new_id,'expiry_at',m.expiry))).*
 FROM market_data.contracts c JOIN release_test_members m ON m.old_id=c.id WHERE {scope};
INSERT INTO market_data.curves SELECT(jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||jsonb_build_object('code','publication_release_forward','name','Synthetic explicit-expiry publication forwards'))).*
 FROM market_data.curves c WHERE {scope} AND code='base_forward';
INSERT INTO market_data.curve_members SELECT catalog_id,revision_id,'publication_release_forward',m.new_id,c.ordinal
 FROM market_data.curve_members c JOIN release_test_members m ON m.old_id=c.contract_id WHERE {scope} AND curve_code='base_forward';
INSERT INTO market_data.curve_inputs SELECT(jsonb_populate_record(NULL::market_data.curve_inputs,to_jsonb(c)||jsonb_build_object('curve_code','publication_release_forward'))).*
 FROM market_data.curve_inputs c WHERE {scope} AND curve_code='base_forward';
INSERT INTO market_data.constituent_mappings SELECT(jsonb_populate_record(NULL::market_data.constituent_mappings,to_jsonb(c)||jsonb_build_object('curve_code','publication_release_forward','target_contract_id',m.new_id))).*
 FROM market_data.constituent_mappings c JOIN release_test_members m ON m.old_id=c.target_contract_id WHERE {scope} AND curve_code='base_forward';
"""
    # Qualifying frames retain their original marks; the new source explicitly
    # consumes those marks through the cloned constituent relationships.
    for frame in manifest['frames']:
        if 'base_forward' in frame['curveContracts']:
            frame['curveContracts']['publication_release_forward'] = [new for _, new, _ in pairs]
    return market.replace(marker, sql + marker, 1)


def run_checks(url, query, manifest):
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'editor'})
    workspace = client.call('workspace')
    initial = set(workspace['initialFrameIds'])
    frames = [f for f in workspace['frames'] if f['id'] in initial]
    live = next(f for f in frames if f['at'].startswith('2020-12-18T16:00:00'))
    fixed = next(f for f in frames if f['at'].startswith('2021-01-15T08:00:00'))
    later = next(f for f in frames if f['at'].startswith('2021-01-31T08:00:00'))
    catalog = client.call('contract-price-terms?snapshotId=' + live['id'])
    assert 'published_release_v1' in catalog['settlementSources']
    native = next(s for s in client.call('custom-curves')['sources'] if s['code'] == 'publication_release_forward')
    target = native['contracts'][0]['id']
    def node(id, kind, parameters=None, inputs=None, **rest):
        return dict(id=id, kind=kind, parameters=parameters or {}, inputs=inputs or {}, **rest)
    definition = dict(code='http_release_pricing', name='Synthetic release-aware source inspection', output='out', nodes=[
        node('src', 'source', {'curve':'publication_release_forward'}),
        node('map', 'weighted_contracts', {'aggregation':'signed_sum_v1'}, {'source_a':'src'},
             contractWeights=[dict(outputPosition=1, sourcePort='source_a', contractId=target, weight='1')]),
        node('dates', 'dates', {'mode':'context', 'sampling':'context_utc_time', 'corrections':'exact_context_frame_history_latest_visible'}),
        node('sample', 'sample', inputs={'selection':'map', 'dates':'dates'}), node('out', 'output', inputs={'values':'sample'})])
    curve = client.call('custom-curves/save', dict(requestId=str(uuid4()), snapshotId=live['id'], definition=definition, layout={}))
    choices = client.call('contract-price-terms?snapshotId=' + live['id'])
    source = next(v for v in choices['curves'] if (v['id'], v['version']) == (curve['id'], curve['version']))['sources'][0]
    reports = client.call('price-publications?snapshotId=' + live['id'])
    index = next(i for i in reports['indices'] if i['key']['code'] == 'test_price' and i['quote']['code'] == source['quote'])
    publication = dict(key=dict(catalogId=reports['catalogId'], id=str(uuid4()), version=1), references=reports['references'],
        publisher=dict(key=dict(catalogId=reports['catalogId'], id=str(uuid4()), version=1), name='Synthetic release check publisher'),
        name='Synthetic release check monthly', commodities=[index['commodity']],
        indices=[{k:index[k] for k in ('key', 'commodity', 'location', 'quote', 'unit')} | dict(symbol='SYNTH-RELEASE', statistic='monthly_index')],
        calendar=reports['calendars'][0]['reference'], clock=reports['clocks'][0]['reference'], implementation='gas_monthly_explicit_v1')
    publication = client.call('price-publications/save', dict(requestId=str(uuid4()), snapshotId=live['id'], expectedVersion=0, definition=publication))
    terms = dict(nodeId=source['nodeId'], scope='curve', contractId=None, mapping='same_delivery_period_v1', mappings=[],
        settlement='published_release_v1', publicationId=publication['key']['id'], publicationVersion=1, index='test_price',
        observationRule='single_assessment_v1', pendingPolicy='require_final_v1', projectionPolicy=None)
    curve = client.call('contract-price-terms/save', dict(requestId=str(uuid4()), snapshotId=live['id'], curveId=curve['id'],
        version=curve['version'], generation=curve['generation'], terms=terms))

    def status(frame):
        return sorted(client.call(f"contract-price-terms/status?snapshotId={frame['id']}&curveId={curve['id']}&version={curve['version']}")['rows'], key=lambda row: row['deliveryFrom'])

    assert all(r['state'] == 'publication_schedule_missing' and r['price'] is None for r in status(live))
    occurrence_ids = []
    for member in source['contracts']:
        expiry = datetime.fromisoformat(member['expiry'].replace('Z', '+00:00'))
        stamp = lambda at: at.astimezone(timezone.utc).isoformat().replace('+00:00', 'Z')
        occurrence = dict(publication=publication['key'], assessmentFrom=stamp(expiry - timedelta(days=3)), assessmentTo=stamp(expiry),
            assessmentDate=member['deliveryFrom'], periods=[{'from':member['deliveryFrom'], 'to':member['deliveryTo']}],
            expectedReleaseAt=member['deliveryFrom'] + 'T16:00:00Z')
        saved = client.call('price-publications/occurrences/save', dict(requestId=str(uuid4()), snapshotId=live['id'], id=str(uuid4()), occurrence=occurrence))
        occurrence_ids.append(saved['id'])
    live_rows = status(live)
    assert all(r['state'] == 'projected' and r['price'] is not None and r['fixingPrice'] is None for r in live_rows), live_rows
    pending = status(fixed)
    assert pending[0]['state'] == 'awaiting_publication' and pending[0]['price'] is None and pending[0]['fixingPrice'] is None, pending
    assert any(r['state'] == 'projected' and r['price'] is not None for r in pending[1:]), pending
    refused = client.call('custom-curves/preview', dict(snapshotId=fixed['id'], definition=curve['definition']), expected=422)
    assert refused['code'] == 'awaiting_publication', refused
    first = source['contracts'][0]
    fixing_source = query(f"SELECT source_code FROM market_data.index_fixing_observations WHERE catalog_id='{manifest['catalogId']}' "
        f"AND revision_id='{manifest['revisionId']}' AND index_code='test_price' AND observation_from='{first['deliveryFrom']}' AND observation_to='{first['deliveryTo']}';")
    ingest = dict(requestId=str(uuid4()), snapshotId=fixed['id'], occurrenceId=occurrence_ids[0], indexCode='test_price', sourceCode=fixing_source,
        sourceEventId='synthetic-release-check-' + str(uuid4()), sourceRevisionId='initial', publishedAt='2021-01-02T12:00:00Z', value='3.29', correctsReleaseId=None)
    close_pins = lambda: query("SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.close_id,p.observation_id,p.input_use)::text,'[]')) FROM market_data.market_close_pins p;")
    original_close_pins = close_pins()
    received = client.call('price-publications/ingest', ingest)
    actual = status(fixed)[0]
    assert actual['state'] == 'final' and actual['fixingPrice'] == '3.29' and actual['price'] == '3.29', actual
    assert actual['releaseId'] == received['releaseId'] and actual['selectedAsDependency'] and actual['pricingError'] is None, actual
    priced = client.call('custom-curves/preview', dict(snapshotId=fixed['id'], definition=curve['definition']))
    assert priced['rows'][0][1] == '3.29' and priced['dependencies'] == [] and all(i['fixedValue'] for i in priced['inputs']), priced
    legacy_definition = deepcopy(curve['definition'])
    next(node for node in legacy_definition['nodes'] if node['kind'] == 'source')['contractTerms']['default']['settlement'] = 'published_index_v1'
    legacy = client.call('custom-curves/preview', dict(snapshotId=fixed['id'], definition=legacy_definition))
    assert legacy['rows'][0][1] == '3.5', legacy
    calendar_request = dict(curve=None, timeZone='UTC', session=fixed['close']['session'], definition=curve['definition'])
    calendar = client.call('market/dates', calendar_request)
    assert fixed['at'][:10] in {row['date'] for row in calendar['dates']}, calendar
    latest = client.call('market/dates', calendar_request | dict(maximumDates=1))
    assert latest['dates'] == calendar['dates'][-1:], (calendar, latest)
    retained = client.call('custom-curves/preview', dict(snapshotId=later['id'], definition=curve['definition']))
    assert retained['rows'][0][1] == '3.29', retained
    correction = client.call('price-publications/ingest', ingest | dict(requestId=str(uuid4()), sourceRevisionId='correction',
        publishedAt='2021-01-20T12:00:00Z', value='3.31', correctsReleaseId=received['releaseId']))
    unchanged = status(fixed)[0]
    assert unchanged['releaseId'] == received['releaseId'] and unchanged['fixingPrice'] == '3.29' and unchanged['price'] == '3.29', unchanged
    assert query(f"SELECT observed_at FROM market_data.observations WHERE id='{correction['observationIds'][0]}';").startswith('2021-01-02 12:00:00')

    # The direct reader also applies its supplied knowledge cutoff without a
    # bound historical context; a later certification cannot leak into this read.
    target = json.dumps([dict(publicationId=publication['key']['id'], version=1, index='test_price',
        **{'from':first['deliveryFrom'], 'to':first['deliveryTo']})]).replace("'", "''")
    knowledge = query(f"SELECT k.known_from FROM market_data.price_publication_releases r JOIN trading.knowledge_receipts k "
        f"ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order) WHERE r.id='{received['releaseId']}';")
    token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context(" +
        f"'https://preview.invalid','viewer','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
    raw = query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');"
        f"SELECT market_data.read_contract_publication_prices('{manifest['catalogId']}','{manifest['revisionId']}','{target}'::jsonb,"
        f"'2021-01-15T08:00:00Z','{knowledge}'::timestamptz-interval '1 microsecond');COMMIT;")
    assert json.loads(raw)[0]['occurrences'][0]['releases'] == []
    updated_rows = status(later)
    assert any(row['state'] == 'projected' and row['price'] is None and row['pricingError'] for row in updated_rows[1:]), updated_rows
    updated = updated_rows[0]
    assert updated['price'] == '3.31' and updated['releaseId'] == correction['releaseId'] and updated['selectedAsDependency'], updated
    client.call('market/context', dict(effectiveOn='2021-01-31', knownAt=retained['context']['knownAt'], mode='at', selection='latest_correction'))
    historical_frame = next(f for f in client.call('workspace/navigation')['frames'] if f['at'].startswith('2021-01-31T08:00:00'))
    historical = client.call('custom-curves/preview', dict(snapshotId=historical_frame['id'], definition=curve['definition']))
    assert historical['rows'] == retained['rows'] and historical['inputs'] == retained['inputs'], historical
    client.call('market/current', {})
    assert close_pins() == original_close_pins, 'Publication selection rewrote recorded close pins'
    client.call('profile', {'profile':'viewer'})
    refused = client.call(f"contract-price-terms/status?snapshotId={fixed['id']}&curveId={curve['id']}&version={curve['version']}", expected=422)
    assert refused['code'] == 'custom_curve_definition', refused
    client.call('price-publications/ingest', ingest, expected=403)
    client.call('profile', {'profile':'approver'})
    curve = client.call('custom-curves/publish', dict(requestId=str(uuid4()), id=curve['id'], version=curve['version'], generation=curve['generation']))
    client.call('profile', {'profile':'viewer'})
    assert status(fixed)[0]['fixingPrice'] == '3.29'
    client.call('price-publications/ingest', ingest, expected=403)
    print('PASS release-aware HTTP: exact schedules, live native prices, explicit pending rows, strict valuation refusal, normalized final evidence, separate frozen publication dependencies and unchanged recorded closes, publish/knowledge cutoffs and viewer authority', flush=True)
