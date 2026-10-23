"""Exact published synthetic fixings: full, partial, missing, wrong and unpublished actual observations."""
from datetime import datetime, timezone
from fractions import Fraction as F
import json
from uuid import UUID, uuid4
from preview_client import PreviewClient
from preview_fixture import CAT, R, RCAT, REV, RREV, identity, trade_booking
from swap_fixture import REVISION
from check_trading_pin_reads import legacy_trading_reads, trading_context, trading_workspace

CONVENTION='independent_side_financial_swap_v2'
RULE={'frequency':'monthly','rule':'days_after_period_end','days':46,'dayOfMonth':None,
      'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,
      'implementation':'monthly_payment_date_rules_v1'}
JANUARY,FEBRUARY=('2021-01-01','2021-02-01'),('2021-02-01','2021-03-01')
BOOK='d1000000-0000-0000-0000-000000000001'
# The fixture's published January monthly gas actual, then this check's own observations.
JANUARY_GAS=identity('7',5000000)
FEBRUARY_GAS,OTHER_SOURCE,TRADER_MARK,PRELIMINARY=(identity('7',8000000+n) for n in range(1,5))
# The separate daily bundle's January 1 and 2 gas actuals, and its January 1 morning mark for day 1.
DAILY_DAY_1,DAILY_DAY_2,DAILY_OTHER_FACT=identity('7',7002001),identity('7',7002002),identity('7',6002001)


def prepare(reference, market, manifest):
    """Add frames that differ from fixture frames only in their monthly gas actuals."""
    marker='UPDATE ref_data.revisions SET'
    assert marker in market
    source=(f"INSERT INTO ref_data.sources (catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review) "
            f"VALUES ({R},'fixture_alternate','synthetic','synthetic:alternate-fixing-source-v1','2021-01-01 00:00:00+00','Project-authored synthetic fixture','redistributable');")
    now=datetime.now(timezone.utc).isoformat()
    def observation(oid,series,value,observed='2020-12-31 12:00:00+00',source='fixture',role='index_fixing',state='final'):
        return (f"INSERT INTO market_data.observations VALUES ('{CAT}','{oid}','{REV}','{series}',{R},'{source}','check_{oid[-7:]}','1',"
                f"'{role}','{state}','{observed}','{now}',{value},NULL);")
    january,february=identity('9',9200),identity('9',9201)
    rows=[observation(FEBRUARY_GAS,february,'3.2','2021-01-31 12:00:00+00'),
          observation(OTHER_SOURCE,january,'3.5',source='fixture_alternate'),
          observation(TRADER_MARK,january,'3.5',role='trader_mark'),
          observation(PRELIMINARY,january,'3.5',state='preliminary')]
    market=market.replace(marker,source+'\n'+marker,1)
    market=market.rsplit('COMMIT;',1)[0]+'\n'.join(rows)+'\nCOMMIT;\n'
    frames={f['at']:f for f in manifest['frames']}
    def frame(at,base,remove=(),add=()):
        pins=[o for o in frames[base]['observationIds'] if o not in remove]+list(add)
        manifest['frames'].append({**frames[base],'at':at,'observationIds':pins})
    for at,wrong in [('2021-01-15T10:00:00Z',OTHER_SOURCE),('2021-01-15T11:00:00Z',TRADER_MARK),('2021-01-15T12:00:00Z',PRELIMINARY)]:
        frame(at,'2021-01-15T08:00:00Z',[JANUARY_GAS],[wrong])
    frame('2021-01-15T13:00:00Z','2021-01-15T08:00:00Z',[JANUARY_GAS])
    # February publishes at 2021-01-31T12:00Z and pays on 2021-04-15.
    for at in ['2021-01-31T12:00:00Z','2021-02-01T08:00:00Z','2021-04-15T00:00:00Z']:
        frame(at,'2021-01-31T08:00:00Z',add=[FEBRUARY_GAS])
    frame('2021-02-01T09:00:00Z','2021-01-31T08:00:00Z',[JANUARY_GAS],[FEBRUARY_GAS])
    return reference,market,manifest


def written(query):
    return query("SELECT count(*)::text||':'||(SELECT count(*) FROM trading.publications)::text FROM trading.trades;")


def swap_index(c, frame, curve):
    """Catalog choices for one curve, read at a frame that has every swap bundle."""
    catalog=c.call('swaps/catalog',{'snapshotId':frame['id'],'convention':CONVENTION,'knowledgeMode':'current'})
    index=next(i for i in catalog['indices'] if i['curve']==curve)
    assert index['fixingConvention']=='published_index_observations_v1'
    return index,catalog['books'][0]['id']


def draft(frame, index, curve, strike, period=JANUARY, quantity='100'):
    """Receive the index and pay the strike over one month, undiscounted, at one exact frame."""
    daily=curve.startswith('daily_')
    return {'context':{'snapshotId':frame['id'],'convention':CONVENTION,'knowledgeMode':'current'},
            'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity'],
            'quantity':{'value':quantity,'specification':index['quantityChoices'][0]['specification']},
            'fixedCoefficient':'-1','discounting':False,
            'fixed':{'price':strike,'currency':index['currencyCode'],'deliveryFrom':period[0],'deliveryTo':period[1],'paymentSchedule':RULE},
            'floating':{'curve':curve,'index':index['index'],'currency':index['currencyCode'],
                        'deliveryFrom':period[0],'deliveryTo':period[1],'paymentSchedule':RULE,
                        'resetFrequency':'daily' if daily else 'monthly','averagingMethod':'arithmetic' if daily else 'none'}}


def booking(terms, book):
    """The same terms as a booking command with a fresh operation identity."""
    return {**terms['context'],**trade_booking(),'operationId':str(uuid4()),'bookId':book,
            'definitionRevisionId':terms['definitionRevisionId'],'commodity':terms['commodity'],
            'quantity':terms['quantity']['value'],'fixedCoefficient':'-1','discounting':False,
            'fixed':terms['fixed'],'floating':terms['floating']}


def issue(query, manifest, profile='trader'):
    return query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                 f"'https://preview.invalid','{profile}','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")


def runtime(token, sql):
    """Run SQL as the runtime role under one current context, then roll it back."""
    return f"BEGIN;SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');{sql}ROLLBACK;"


def reject(sql, message):
    return (f"DO $$ BEGIN PERFORM {sql.removeprefix('SELECT ')} RAISE EXCEPTION 'Expected rejection'; "
            f"EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'{message}' THEN RAISE; END IF; END $$;")


def writer(at, pins):
    """The authorized booking function called directly for the January monthly gas swap."""
    rule=json.dumps(RULE);pinned=','.join(f"'{p}'" for p in pins)
    return (f"SELECT security.save_indexed_swap_with_parties('{CAT}','{uuid4()}',NULL,NULL,'{BOOK}','{REV}','{RCAT}','{RREV}',"
            f"'base_forward','test_price','usd','usd','2021-01-01','2021-02-01','2021-03-18T00:00:00Z','2021-01-01','2021-02-01',"
            f"'2021-03-18T00:00:00Z','monthly_index_price',3,100,-1,false,'{at}Z',NULL,'{at}Z','Initial booking','{CONVENTION}',"
            f"'{rule}'::jsonb,'{rule}'::jsonb,'test_gas',NULL,NULL,NULL,'{json.dumps(trade_booking())}'::jsonb,ARRAY[{pinned}]::uuid[])::text;")


def run_checks(url, query, manifest):
    run_rls_checks(query,manifest)
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    workspace=c.call('workspace')
    frames={f['at'][:19]:f for f in workspace['frames'] if f['id'] in set(workspace['initialFrameIds'])}
    before=written(query)

    for day,actual_count in [('01',1),('15',15),('31',31)]:
        frame=frames[f'2021-01-{day}T08:00:00']
        # The complete monthly index is known even though some delivery remains.
        monthly=draft(frame,swap_index(c,frame,'base_forward')[0],'base_forward','3')
        priced=c.call('swaps/price',monthly)
        resets=priced['valuation']['sides'][1]['payments'][0]['resets']
        assert F(priced['valuation']['presentValue'])==50
        assert len(resets)==1 and resets[0]['fixingState']=='Actual' and resets[0]['price']=='3.5'
        retained={**monthly,'context':{**monthly['context'],'knowledgeMode':'retained','contextId':priced['context']['contextId']}}
        risk=c.call('swaps/draft-risk',{'draft':retained,'bumpPolicy':priced['bumpPolicy'],'numericalPolicy':priced['numericalPolicy']})
        assert risk['factors']==[] and risk['scenarioCount']==1
        for curve,strike,pv_per_actual,base_pv in [('daily_gas','3',1,16),('daily_power','40',10,160)]:
            body=draft(frame,swap_index(c,frame,curve)[0],curve,strike)
            priced=c.call('swaps/price',body)
            resets=priced['valuation']['sides'][1]['payments'][0]['resets']
            assert F(priced['valuation']['presentValue'])==base_pv+pv_per_actual*actual_count
            assert len(resets)==31 and sum(r['fixingState']=='Actual' for r in resets)==actual_count
            assert [r['from'] for r in resets]==[f'2021-01-{d:02d}' for d in range(1,32)]
            assert all(r['weight']=='1' and r['lineage'] for r in resets)
            retained={**body,'context':{**body['context'],'knowledgeMode':'retained','contextId':priced['context']['contextId']}}
            request={'draft':retained,'bumpPolicy':priced['bumpPolicy'],'numericalPolicy':priced['numericalPolicy']}
            risk=c.call('swaps/draft-risk',request)
            assert len(risk['factors'])==31-actual_count and risk['scenarioCount']==1+2*(31-actual_count)
            assert all(F(f['delta'])==F(100,31) and F(f['gamma'])==0 for f in risk['factors'])
            assert c.call('swaps/draft-risk',request)['factors']==risk['factors']
            c.call('swaps/price',{**body,'floating':{**body['floating'],'firstFixingAt':'2020-12-31T12:00:00Z'}},expected=422)
        print(f'PASS published index fixings: January {day}; one monthly actual, {actual_count}/31 daily actuals, exact PV and only unresolved forward risk',flush=True)
    # The omitted actual still exists in the database. Exact snapshot pins must
    # prevent the loader finding it through an unrestricted observation read.
    partial=frames['2021-01-15T09:00:00']
    error=c.call('swaps/price',draft(partial,swap_index(c,partial,'daily_gas')[0],'daily_gas','3'),expected=422)
    assert error['code']=='index_fixing_missing'
    assert before==written(query)
    print('PASS missing due fixing cannot be projected or recovered outside exact snapshot pins; temporary price/risk writes no trades',flush=True)
    # Check-owned frames have no daily swap bundle, so catalog choices come from a fixture frame.
    gas,book=swap_index(c,frames['2021-01-15T08:00:00'],'base_forward')
    run_oracle_checks(c,frames,gas)
    run_input_checks(c,query,manifest,frames,gas,book)
    run_window_checks(c,query,frames,gas,book)
    run_correction_checks(query,manifest,c,frames)


def run_oracle_checks(c, frames, gas):
    """10,000 MMBtu receiving a 3.20 published index and paying 3.00 nets 10,000 × 0.20 = 2,000 USD."""
    assert (gas['currency'],gas['unit'])==('USD','MMBtu (IT)')
    terms=draft(frames['2021-02-01T08:00:00'],gas,'base_forward','3',FEBRUARY,'10000')
    value=c.call('swaps/price',terms)['valuation']
    fixed,floating=value['sides']
    (reset,)=floating['payments'][0]['resets']
    # Sides keep unit gross amounts; the trade quantity scales the completed instrument once.
    assert (fixed['kind'],fixed['coefficient'],F(fixed['payments'][0]['cashAmount']))==('fixed','-1',3)
    assert (floating['kind'],floating['coefficient'],F(floating['payments'][0]['cashAmount']))==('floating','1',F('3.2'))
    assert (reset['fixingState'],reset['price'])==('Actual','3.2') and [o['id'] for o in reset['lineage']]==[FEBRUARY_GAS]
    assert all((p['paymentDate'],p['discountFactor'])==('2021-04-15','1') for side in value['sides'] for p in side['payments'])
    assert F(value['presentValue'])==2000 and value['quantity']=='10000'
    due=c.call('swaps/price',draft(frames['2021-04-15T00:00:00'],gas,'base_forward','3',FEBRUARY,'10000'),expected=422)
    assert due['code']=='side_payment_due',due
    print('PASS acceptance oracle: 10,000 MMBtu receive a published 3.20 USD/MMBtu index and pay 3.00 for 2,000 USD undiscounted before fees; at the payment date valuation requires lifecycle state',flush=True)


def run_input_checks(c, query, manifest, frames, gas, book):
    """A due monthly actual from another source, role, state or period, or none at all, cannot price or book."""
    before=written(query)
    cases=[('2021-01-15T10:00:00',[OTHER_SOURCE],'index_fixing_selection'),
           ('2021-01-15T11:00:00',[TRADER_MARK],'index_fixing_missing'),
           ('2021-01-15T12:00:00',[PRELIMINARY],'frozen_fact'),
           ('2021-01-15T13:00:00',[],'index_fixing_missing'),
           ('2021-02-01T09:00:00',[FEBRUARY_GAS],'index_fixing_missing')]
    for at,_,code in cases:
        terms=draft(frames[at],gas,'base_forward','3')
        for path,body in [('swaps/price',terms),('swaps/book',booking(terms,book))]:
            error=c.call(path,body,expected=422)
            assert error['code']==code,(path,at,error)
    assert written(query)==before
    print('PASS wrong actuals through the API: another source, a trader mark, a preliminary value, no actual or only another period cannot price or book',flush=True)
    # The database writer selects the actual itself instead of trusting its managed caller.
    for at,pins,_ in cases:
        query(runtime(issue(query,manifest),reject(writer(at,pins),'index_fixing_missing')))
    # The same call with the exact January actual is admitted, so the rejections are not malformed calls.
    accepted=json.loads(query(runtime(issue(query,manifest),writer('2021-01-15T10:00:00',[JANUARY_GAS]))))
    assert accepted['tradeId'],accepted
    assert written(query)==before
    print('PASS wrong actuals through the database writer: each pin set rejects independently, while the exact January actual is admitted',flush=True)


def run_window_checks(c, query, frames, gas, book):
    """February's base forward needs a live future that expires on January 28; the February index publishes on January 31 at 12:00."""
    before=written(query)
    window=draft(frames['2021-01-31T08:00:00'],gas,'base_forward','3',FEBRUARY)
    assert c.call('swaps/price',window,expected=422)['code']=='source_expired'
    assert c.call('swaps/book',booking(window,book),expected=422)['code']=='source_expired'
    assert written(query)==before
    published=c.call('swaps/price',draft(frames['2021-01-31T12:00:00'],gas,'base_forward','3',FEBRUARY))['valuation']
    (reset,)=published['sides'][1]['payments'][0]['resets']
    assert (reset['fixingState'],reset['price'])==('Actual','3.2') and F(published['presentValue'])==20
    print('PASS expired projection before publication: no price or booking until the actual publishes; at publication the pinned actual prices',flush=True)


def record_correction(query, manifest, identifier, previous, amount):
    """A correction of a daily-bundle actual recorded outside this session's snapshots."""
    query(f"""INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value,corrects_id)
      SELECT catalog_id,'{identifier}'::uuid,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,'{identifier}',role,publication_state,observed_at,clock_timestamp(),value+({amount}),'{previous}'::uuid
      FROM market_data.observations WHERE catalog_id='{UUID(manifest['catalogId'])}' AND definition_revision_id='{REVISION}' AND id='{previous}';""")


def run_correction_checks(query, manifest, c, frames):
    """D172: every bundle prices its snapshot's exact pins, so a correction reaches pricing only through a new snapshot."""
    seed=frames['2021-01-15T08:00:00']
    gas,book=swap_index(c,seed,'base_forward')
    terms={'monthly':draft(seed,gas,'base_forward','3'),'daily':draft(seed,swap_index(c,seed,'daily_gas')[0],'daily_gas','3')}

    def priced(frame, **context):
        """PV, the first two reset prices and their lineage for both swaps at one frame and context."""
        result={}
        for name,body in terms.items():
            value=c.call('swaps/price',{**body,'context':{**body['context'],'snapshotId':frame['id'],**context}})
            resets=value['valuation']['sides'][1]['payments'][0]['resets'][:2]
            result[name]=(F(value['valuation']['presentValue']),[r['price'] for r in resets])
            result[name+' lineage']={o['id'] for r in resets for o in r['lineage']}
            result[name+' context']=value['context']
        return result
    def values(result):
        return {name:result[name] for name in terms}

    # The daily bundle's first two gas actuals are 3.01+0.31 and 3.02+0.31; 15 of 31 days are published.
    original={'monthly':(50,['3.5']),'daily':(31,['3.32','3.33'])}
    first=priced(seed)
    assert values(first)==original
    commands={name:booking(body,book) for name,body in terms.items()}
    booked={name:c.call('swaps/book',command) for name,command in commands.items()}
    pair={**commands['daily'],'operationId':str(uuid4()),'fixed':None,'fixedCoefficient':None,'swapType':'financial_floating_floating','firstCoefficient':'-1',
        'firstFloating':{**commands['daily']['floating'],'spread':'0.1'},'floating':{**commands['daily']['floating'],'spread':'0.2'}}
    pair_booked=c.call('swaps/book',pair)
    original_evidence=query(f"SELECT (request->'sideFixings')::text FROM trading.swap_trade_revisions WHERE operation_id='{pair['operationId']}';")
    assert all(len(side['evidence']['inputs'])==15 and DAILY_DAY_1 in side['evidence']['inputs'] for side in json.loads(original_evidence))
    # A daily-bundle correction needs the same current import authority as a main-bundle one.
    c.call('corrections',{'snapshotId':seed['id'],'observationId':DAILY_DAY_1,'value':'3.63'},expected=403)
    c.call('profile',{'profile':'editor'})
    monthly=c.call('corrections',{'snapshotId':seed['id'],'observationId':JANUARY_GAS,'value':'3.6'})
    daily=c.call('corrections',{'snapshotId':seed['id'],'observationId':DAILY_DAY_1,'value':'3.63'})
    # It writes only an observation the snapshot pins: not another daily fact, and nothing
    # from a frame whose instant declares no daily bundle frame.
    for frame,other in [(seed,DAILY_OTHER_FACT),(frames['2021-01-15T10:00:00'],DAILY_DAY_1)]:
        error=c.call('corrections',{'snapshotId':frame['id'],'observationId':other,'value':'4'},expected=422)
        assert error['code']=='snapshot_input',error
    # A correction recorded outside the session reaches a snapshot only when it is explicitly selected.
    imported=str(uuid4());record_correction(query,manifest,imported,DAILY_DAY_2,'.62')
    count=len(c.call('workspace')['frames'])
    for other in [OTHER_SOURCE,DAILY_OTHER_FACT]:
        error=c.call('select-observation',{'snapshotId':seed['id'],'observationId':other},expected=422)
        assert error['code']=='selection_mismatch',error
    assert len(c.call('workspace')['frames'])==count
    selected=c.call('select-observation',{'snapshotId':seed['id'],'observationId':imported})
    c.call('profile',{'profile':'trader'})
    # Each new snapshot names its separate-bundle selection; the others name none.
    assert daily['observationId'] in daily['frame']['swapMarketSelections'][REVISION]
    assert imported in selected['frame']['swapMarketSelections'][REVISION]
    assert 'swapMarketSelections' not in seed and 'swapMarketSelections' not in monthly['frame']
    # One actual of 31 corrected by 0.31 moves the daily average by 0.01, or 1 USD on 100 units.
    expected=[(seed,original,set()),
              (monthly['frame'],{**original,'monthly':(60,['3.6'])},{monthly['observationId']}),
              (daily['frame'],{**original,'daily':(32,['3.63','3.33'])},{daily['observationId']}),
              (selected['frame'],{**original,'daily':(33,['3.32','3.95'])},{imported})]
    for frame,result,corrections in expected:
        assert frame['at']==seed['at']
        value=priced(frame)
        assert values(value)==result,(frame['name'],values(value))
        assert corrections<=value['monthly lineage']|value['daily lineage']
        assert frame is seed or frame['id']!=seed['id']
    print('PASS corrections in both bundles: the original snapshot keeps its exact actuals; each correction or explicit revision selection prices only through its own new snapshot',flush=True)
    # The scoped trading read consumes each snapshot's daily-bundle selection exactly as the full reads do.
    futures=c.call('workspace')['futuresConvention']
    for frame,pv in [(seed,31),(daily['frame'],32),(selected['frame'],33)]:
        cut=trading_context(frame,trading_workspace(c,trading_context(frame))['catalog']['context'])
        scoped=trading_workspace(c,cut)
        assert scoped==legacy_trading_reads(c,cut,futures),frame['name']
        assert F(next(h for h in scoped['blotter']['holdings'] if h['tradeId']==booked['daily']['tradeId'])['presentValue'])==pv
    print('PASS the stored daily swap values 31, 32 and 33 USD at the original, corrected and selected snapshots, identically through scoped and full trading reads',flush=True)
    for name in terms:
        retained=priced(seed,knowledgeMode='retained',contextId=first[name+' context']['contextId'])
        assert retained[name]==original[name]
        assert c.call('swaps/book',commands[name])==booked[name]
        assert c.call('swaps/status',{'operationId':commands[name]['operationId']})==booked[name]
    # The same command through a snapshot whose actual was corrected keeps its original outcome.
    moved={**commands['daily'],'snapshotId':daily['frame']['id']}
    assert c.call('swaps/book',moved)==booked['daily']
    assert c.call('swaps/status',{'operationId':moved['operationId']})==booked['daily']
    c.call('swaps/book',{**commands['daily'],'quantity':'101'},expected=409)
    c.call('profile',{'profile':'risk'})
    c.call('swaps/book',commands['daily'],expected=403)
    c.call('profile',{'profile':'trader'})
    print('PASS after corrections: a pre-correction retained context and identical booking retries, also through the corrected snapshot, keep their original values; changed terms conflict and write authority remains required',flush=True)
    # A new booking from the corrected snapshot records its corrected actual, and the
    # stored trade values at each snapshot from that snapshot's own pins.
    placed=booking({**terms['daily'],'context':{**terms['daily']['context'],'snapshotId':daily['frame']['id']}},book)
    trade=c.call('swaps/book',placed)['tradeId']
    inputs=json.loads(query(f"SELECT (request->'fixingInputs')::text FROM trading.swap_trade_revisions WHERE operation_id='{placed['operationId']}';"))
    assert len(inputs)==15 and daily['observationId'] in inputs and DAILY_DAY_1 not in inputs,inputs
    for frame,pv in [(daily['frame'],32),(seed,31)]:
        editor=c.call('swaps/editor',{'context':{**terms['daily']['context'],'snapshotId':frame['id']},'tradeId':trade,'revision':None})
        assert F(editor['valuation']['presentValue'])==pv,(frame['name'],editor['valuation']['presentValue'])
    print('PASS a booking from a snapshot with a selected daily correction stores that actual, and the trade revalues from each snapshot\'s own pins',flush=True)
    assert c.call('swaps/book',{**pair,'snapshotId':daily['frame']['id']})==pair_booked
    assert query(f"SELECT (request->'sideFixings')::text FROM trading.swap_trade_revisions WHERE operation_id='{pair['operationId']}';")==original_evidence
    fresh_pair={**pair,'operationId':str(uuid4()),'snapshotId':daily['frame']['id']}
    c.call('swaps/book',fresh_pair)
    updated=json.loads(query(f"SELECT (request->'sideFixings')::text FROM trading.swap_trade_revisions WHERE operation_id='{fresh_pair['operationId']}';"))
    assert all(daily['observationId'] in side['evidence']['inputs'] and DAILY_DAY_1 not in side['evidence']['inputs'] for side in updated)
    c.call('swaps/amend',{**commands['daily'],'operationId':str(uuid4()),'snapshotId':daily['frame']['id'],'tradeId':pair_booked['tradeId'],'expectedRevision':1,'effectiveAt':daily['frame']['at'],'reason':'Synthetic rejected fixed-side substitution'},expected=422)
    print('PASS composed fixing receipts: both slots retain original actuals on corrected replay; a fresh operation records corrected pins and direct subtype conversion rejects',flush=True)
    after=priced(seed)['monthly context']['knownAt']
    aliases=set()
    for known,result in [(first['monthly context']['knownAt'],original),
                         (after,{'monthly':(60,['3.6']),'daily':(34,['3.63','3.95'])})]:
        c.call('market/context',{'effectiveOn':'2021-01-15','knownAt':known,'mode':'at','selection':'latest_correction'})
        workspace=c.call('workspace')
        (alias,)=[f for f in workspace['frames'] if f['at']==seed['at']]
        historical={'knowledgeMode':'retained','contextId':workspace['context']['contextId']}
        assert values(priced(alias,**historical))==result==values(priced(alias,**historical))
        for path,body in [('corrections',{'snapshotId':alias['id'],'observationId':DAILY_DAY_1,'value':'4'}),
                          ('select-observation',{'snapshotId':alias['id'],'observationId':imported})]:
            assert c.call(path,body,expected=422)['code']=='historical_read_only'
        aliases.add(alias['id'])
        c.call('market/current',{})
    assert len(aliases)==2 and seed['id'] not in aliases
    print('PASS certified contexts: the latest correction known at each cutoff resolves both bundles into the context\'s own snapshot, repeatably and read-only',flush=True)
    c.call('profile',{'profile':'approver'})
    refused=c.call('market/closes/register',{'requestId':str(uuid4()),'snapshotId':daily['frame']['id'],'name':'Selected daily correction'},expected=422)
    assert refused['code']=='close_swap_market_pins',refused
    c.call('profile',{'profile':'trader'})
    print('PASS a close cannot be registered from a snapshot whose separate swap-bundle selections it cannot record',flush=True)


def run_rls_checks(query,manifest):
    catalog,ref,tenant,workspace=(manifest[k] for k in ('catalogId','referenceCatalogId','tenantId','workspaceId'))
    query("CREATE POLICY synthetic_extra_read ON market_data.index_fixing_observations FOR SELECT TO openetrm_runtime USING(true);")
    try:
        assert query("SET SESSION AUTHORIZATION preview_runtime;SELECT count(*) FROM market_data.index_fixing_observations;")=='0'
        assert int(query(runtime(issue(query,manifest),'SELECT count(*) FROM market_data.index_fixing_observations;')))>0
        for table,condition in [('market_catalog_scopes',f"catalog_id='{catalog}'"),
                                ('reference_catalog_scopes',f"catalog_id='{ref}' AND tenant_id='{tenant}' AND workspace_id='{workspace}'")]:
            token=issue(query,manifest)
            assert query(f"BEGIN;DELETE FROM security.{table} WHERE {condition};SET SESSION AUTHORIZATION preview_runtime;"
                         f"SELECT security.use_context('{token}');SELECT count(*) FROM market_data.index_fixing_observations;ROLLBACK;")=='0'
    finally:
        query("DROP POLICY synthetic_extra_read ON market_data.index_fixing_observations;")
    print('PASS fixing schedules retain current identity, market scope and reference scope under an additional permissive policy',flush=True)
