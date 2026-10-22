"""Independent monthly gross preview acceptance through the authorized HTTP API."""
import json
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import trade_booking
from check_ppa_bookings_http import profile_terms

POLICIES = dict(contract='ppa_monthly_gross_fixed_v1', evidence='verified_meter_head_v1',
    rounding='cash_2dp_half_even_per_occurrence_v1', approval='two_principals_exact_action_24h_v1',
    correction='restate_rounded_occurrence_v1', adjustmentDue='original_due_for_adjustment_v1')


def run_checks(url, query, manifest, dotnet, temporary, environment, *, quarter_only=False):
    catalog=manifest['catalogId']; tenant=manifest['tenantId']; workspace=manifest['workspaceId']
    def runtime(sql,profile='trader'):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
        return query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');"+sql+'COMMIT;')
    def encoded(value): return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    terms=profile_terms(manifest)
    terms['delivery'].update(**{'from':'2020-11-01','to':'2020-12-01'})
    asset='test_asset_c' if quarter_only else 'test_asset_b'
    zone='America/Chicago' if quarter_only else 'America/New_York'
    clock='synthetic_central' if quarter_only else 'synthetic_eastern'
    terms['normalizationAssets']=[asset]
    terms['delivery']['clock']=clock
    cadence=15 if quarter_only else 60
    terms['delivery']['intervalMinutes']=cadence
    terms['memberships'][0]['side']['quantity'].update(kind='volume',sources=[f'{asset}_{cadence}'],stage='generated',capacity=None,energy=None,hours=None)
    payload=dict(action='booked',snapshotId=str(uuid4()),tradeId=None,expectedRevision=None,
        bookId='d1000000-0000-0000-0000-000000000001',valuationAt='2020-10-01T00:00:00Z',effectiveAt='2020-10-01T00:00:00Z',boundaryAt=None,
        coverageFrom='2020-11-01T04:00:00Z',coverageTo='2020-12-01T05:00:00Z',quantity='80',reason='Synthetic settlement preview',memo='',booking=trade_booking(),terms=terms)
    if quarter_only:payload.update(coverageFrom='2020-11-01T05:00:00Z',coverageTo='2020-12-01T06:00:00Z')
    booked=json.loads(runtime(f"SELECT security.save_ppa_booking('{catalog}','{uuid4()}',{encoded(payload)});"))
    binding=booked['bindings'][0]
    envelope=json.loads(runtime(f"SELECT security.read_ppa_terms('{catalog}','{binding['instrumentId']}');"))
    side=next(s for s in envelope['sideIds'] if s['role']=='strike_payment')
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'trader'});client.bootstrap()
    frame=next(f for f in client.call('ppa/frames') if f['at'].startswith('2020-12-15'))['id']
    client.bootstrap()
    mapping=dict(bindingId=binding['bindingId'],originRevision=1,instrumentId=binding['instrumentId'],sideId=side['sideId'],role='strike_payment',ordinal=0)
    body=dict(snapshotId=frame,tradeId=booked['tradeId'],expectedRevision=1,original=mapping,mappings=[mapping],policies=POLICIES)
    client.call('settlement/payment-lineages/plan',body,expected=403)
    print('PASS settlement preview: no inferred proposal permission grant',flush=True)
    # No migration role grants: synthetic qualification grants only the proposed action explicitly.
    query("INSERT INTO security.role_permissions SELECT r.id,p.code FROM security.roles r CROSS JOIN security.permissions p WHERE r.code='trader' AND p.code='ProposeSettlementDeterminations' ON CONFLICT DO NOTHING;")
    planned=client.call('settlement/payment-lineages/plan',body)
    assert planned['lineage']['trade_id']==booked['tradeId']
    print('PASS settlement preview: explicit immutable original payment mapping',flush=True)
    from fractions import Fraction as F
    from datetime import timedelta
    import subprocess, sys, time
    from pathlib import Path
    from exact_values import fraction
    from preview_fixture import installed_tzdb
    sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'specs/settlement'))
    from reference_model import native_month, gross_cash, round_cash, ROUND, APPROVAL, Family, Binding, Reading, Finality, plan as oracle_plan, preview as oracle_preview
    count=2
    def check(name,condition=True):
        nonlocal count
        assert condition,name
        count+=1;print('PASS settlement preview: '+name,flush=True)
    def cutoff():return client.call('interval-series')['knownAt']
    lineage=planned['lineage']['id'];before_meters=cutoff()
    def preview(expected=200,known=None,**changes):
        request=dict(snapshotId=frame,lineageId=lineage,expectedRevision=1,month='2020-11-01',knownAt=known or cutoff());request.update(changes)
        return client.call('settlement/ppa/preview',request,expected=expected)
    calendar_request=temporary/'settlement-calendar.json';calendar_sql=temporary/'settlement-calendar.sql'
    calendar_request.write_text(json.dumps(dict(referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],
        **{'from':'2020-11-01' if quarter_only else '2020-11-02','through':'2020-11-30'},clocks=[dict(code=clock,kind='iana',fixedOffsetSeconds=None,ianaZone=zone,tzdbVersion=installed_tzdb())])))
    subprocess.run([str(dotnet),'src/OpenEtrm.Api/bin/Debug/net10.0/OpenEtrm.Api.dll','--render-power-days',str(calendar_request),str(calendar_sql)],env=environment,check=True,timeout=30)
    # The earlier finality suite may already have retained the first day.
    query(calendar_sql.read_text())
    check('missing full-month finality returns no partial total',preview(expected=422)['code']=='meter_finality_missing')
    query("INSERT INTO security.role_permissions SELECT id,'VerifySettlementMeters' FROM security.roles WHERE code='market_data_editor' ON CONFLICT DO NOTHING;")
    editor=PreviewClient(url);editor.bootstrap();editor.call('profile',{'profile':'editor'});editor.bootstrap()
    source=f'{asset}_{cadence}';series=manifest['ppa']['series'][source]['meter'];grid=native_month(2020,11,zone,cadence)
    def revise(values,start=None):
        start=start or payload['coverageFrom']
        rev=int(query(f"SELECT coalesce(max(revision),0) FROM market_data.interval_series_revisions WHERE series_id='{series}';"))
        return editor.call('interval-series/revise',dict(requestId=str(uuid4()),seriesId=series,expectedRevision=rev,reason='Synthetic final meter',segments=[dict(start=start,values=values)]))['revision']
    def publish(revision,indices=None,state='final',at=None):
        statements=[]
        previous_heads=json.loads(query(f"SELECT coalesce(jsonb_object_agg(starts_at::text,id),'{{}}') FROM (SELECT DISTINCT ON(starts_at) starts_at,id FROM settlement_data.meter_finality_heads WHERE catalog_id='{catalog}' AND source_code='{source}' ORDER BY starts_at,sequence DESC) h;"))
        from datetime import datetime
        previous_heads={datetime.fromisoformat(k):v for k,v in previous_heads.items()}
        for n in indices if indices is not None else range(len(grid)):
            first,last=grid[n];start=first.isoformat();end=last.isoformat()
            previous=previous_heads.get(first)
            pred="'"+previous+"'" if previous else 'NULL'
            statements.append(f"SELECT settlement_data.publish_meter_finality('{catalog}','{manifest['revisionId']}','{source}','generated','{start}','{end}',{revision},'{state}','{at or end}',{pred},'{uuid4()}','Synthetic preview finality');")
        for statement in statements: runtime(statement,'editor')
    rev=revise(['12.5' if quarter_only else '50']*len(grid));publish(rev)
    started=time.monotonic();base=preview();elapsed=time.monotonic()-started;old=cutoff()
    from check_indexed_settlement_preview import run_checks as indexed_checks
    indexed_checks(client,query,manifest,runtime,encoded,payload,frame,grid,cutoff,revise,publish,cadence)
    if quarter_only:
        oracle=sum(gross_cash((F('12.5'),),(F(100),),F(80),F(40),-1) for _ in grid)
        check('full native quarter-hour DST HTTP preview uses MWh once',len(base['components'])==2884 and fraction(base['exactCash'])==oracle==-1153600)
        check('quarter-hour round-once evidence is complete',fraction(base['roundedCash'])==round_cash(oracle,ROUND) and len(base['proposalPins']['meterHeads'])==2884)
        print(f'MEASURE settlement preview: 2884 cells HTTP {elapsed:.6f} seconds, {len(json.dumps(base).encode())} decoded JSON bytes',flush=True)
        print(f'{count} settlement preview quarter-hour checks passed',flush=True)
        return
    oracle=sum(gross_cash((F(50),),(F(100),),F(80),F(40),-1) for _ in grid)
    check('complete DST month matches independent specification',len(grid)==721 and len(base['components'])==721 and fraction(base['exactCash'])==oracle==-1153600)
    from datetime import datetime,timezone
    as_of=datetime(2020,12,15,16,tzinfo=timezone.utc);due=datetime(2020,12,10,tzinfo=timezone.utc)
    family=Family(catalog,booked['tradeId'],lineage,'original-payment','USD','America/New_York',due,('synthetic-payer','synthetic-receiver'),-1,((binding['bindingId'],side['sideId']),))
    ob=Binding(binding['bindingId'],side['sideId'],grid[0][0],grid[-1][1],F(80),F(40),due,'USD',family.parties,-1,(('asset/source/generated',F(100),'capacity-v1'),))
    op=oracle_plan(family,2020,11,60,{1:(ob,)},1,grid[0][0],grid[-1][1],as_of)
    readings=tuple(Reading(('asset/source/generated',a,b),'1',F(50),b) for a,b in grid)
    heads=tuple(Finality(r.target,'synthetic-head-'+str(i),1,None,'1','final',r.target[2],r.known) for i,r in enumerate(readings))
    independent=oracle_preview(family,2020,11,'synthetic-book',1,op,readings,heads,as_of,as_of,ROUND,APPROVAL)
    check('complete executable specification planner and preview agree',independent.exact==fraction(base['exactCash']) and independent.rounded==fraction(base['roundedCash']) and len(independent.rows)==len(base['components']))
    check('round once and retain due/policy/component pins',fraction(base['roundedCash'])==round_cash(oracle,ROUND) and fraction(base['roundingDifference'])==0 and base['due']['at'].startswith('2020-12-10T00:00:00') and len(base['proposalPins']['meterHeads'])==721 and base['cashState']=='unaccepted_preview')
    check('exact cash evidence uses string decimals',base['components'][0]['strike']['unit']['multiplier']=='1' and base['proposalPins']['lineage']['direction']=='-1' and isinstance(base['proposalPins']['selectedBindings'][0]['quantity'],str))
    print(f'MEASURE settlement preview: 721 cells HTTP {elapsed:.6f} seconds, {len(json.dumps(base).encode())} decoded JSON bytes',flush=True)
    repeated=[r for r in base['components'] if r['interval']['operatingDate']=='2020-11-01' and r['interval']['localHour']==1] if 'localHour' in base['components'][0]['interval'] else []
    check('all native UTC contribution keys remain distinct',len({(r['key']['start'],r['key']['end']) for r in base['components']})==721)
    check('earlier certified cutoff excludes later verification',preview(expected=422,known=before_meters)['code']=='meter_finality_missing')
    rev=revise(['51']);check('new raw correction does not substitute for published finality',fraction(preview()['exactCash'])==oracle)
    publish(rev,[0]);one=preview();check('single native interval correction is -32 only',fraction(one['roundedCash'])==-1153632 and fraction(one['roundedCash'])-fraction(base['roundedCash'])==-32)
    check('earlier knowledge retains exact original published heads',fraction(preview(known=old)['roundedCash'])==oracle)
    rev=revise(['51']*721);publish(rev);all_changed=preview()
    check('all 721 intervals correction is distinct -23072 scenario',fraction(all_changed['roundedCash'])==-1176672 and fraction(all_changed['roundedCash'])-fraction(base['roundedCash'])==-23072)
    publish(rev,[0],state='withdrawn');check('withdrawal never falls back to older final',preview(expected=422)['code']=='settlement_finality')
    publish(rev,[0],state='suspended');check('suspension refuses complete occurrence',preview(expected=422)['code']=='settlement_finality')
    rev=revise(['0']);publish(rev,[0]);zero=preview();check('explicit zero remains a contribution with its denominator',fraction(zero['components'][0]['quantity'])==0 and fraction(zero['components'][0]['normalization']['capacityBasis'])==100 and len(zero['components'])==721)
    check('expected trade head mismatch refuses whole preview',preview(expected=422,expectedRevision=2)['code']=='settlement_trade_head')
    check('unknown request fields cannot supply a caller component manifest',preview(expected=400,components=[])['code']=='request_format')
    replay=client.call('settlement/payment-lineages/plan',body);check('repeated plan retains canonical lineage',replay['lineage']['id']==lineage)
    from copy import deepcopy
    def save(change):
        return json.loads(runtime(f"SELECT security.save_ppa_booking('{catalog}','{uuid4()}',{encoded(change)});"))
    def mapping_for(b,revision):
        e=json.loads(runtime(f"SELECT security.read_ppa_terms('{catalog}','{b['instrumentId']}');"))
        side=next(s for s in e['sideIds'] if s['role']=='strike_payment')
        return dict(bindingId=b['bindingId'],originRevision=revision,instrumentId=b['instrumentId'],sideId=side['sideId'],role='strike_payment',ordinal=0)
    amendment=deepcopy(payload);amendment.update(action='amended',tradeId=booked['tradeId'],expectedRevision=1,boundaryAt=grid[2][0].isoformat(),coverageFrom=grid[2][0].isoformat(),quantity='60')
    amendment['terms']['memberships'][1]['side']['fixedPrice']['price']='50'
    changed=save(amendment);tail=next(b for b in changed['bindings'] if b['bindingId']!=binding['bindingId'])
    check('unmapped amendment refuses entire occurrence',preview(expected=422,expectedRevision=2)['code']=='settlement_manifest')
    body2={**body,'expectedRevision':2,'mappings':[mapping,mapping_for(tail,2)]}
    assert client.call('settlement/payment-lineages/plan',body2)['lineage']['id']==lineage
    amended=preview(expectedRevision=2)
    expected=sum(gross_cash((F(0 if i==0 else 51),),(F(100),),F(80 if i<2 else 60),F(40 if i<2 else 50),-1) for i in range(721))
    check('one certified amended head retains prefix and replacement original terms',fraction(amended['exactCash'])==expected and {c['originalMw'] for c in amended['components']}=={'80','60'} and len(amended['components'])==721)
    check('old certified trade and evidence remain replayable',fraction(preview(known=old)['exactCash'])==oracle)
    termination={**amendment,'action':'terminated','expectedRevision':2,'boundaryAt':grid[4][0].isoformat(),'coverageFrom':None,'coverageTo':None,'quantity':None,'terms':None}
    save(termination);ended=preview(expectedRevision=3)
    expected_end=sum(gross_cash((F(0 if i==0 else 51),),(F(100),),F(80 if i<2 else 60),F(40 if i<2 else 50),-1) for i in range(4))
    check('termination preserves originated four-cell manifest and original due date',len(ended['components'])==4 and fraction(ended['exactCash'])==expected_end and ended['due']['at']==base['due']['at'])
    check('canonical occurrence identity survives amendment and termination',ended['occurrence']==base['occurrence']==amended['occurrence'])
    # A new lineage first planned after a later amendment must still allow the
    # earlier-business November head under later knowledge of the assignment.
    historical_payload=deepcopy(payload);historical_payload['terms']['delivery']['to']='2021-01-01';historical_payload['coverageTo']='2021-01-01T05:00:00Z'
    hbook=save(historical_payload);hbinding=hbook['bindings'][0];horigin=mapping_for(hbinding,1)
    ha=deepcopy(historical_payload);ha.update(action='amended',tradeId=hbook['tradeId'],expectedRevision=1,valuationAt='2020-12-16T00:00:00Z',effectiveAt='2020-12-16T00:00:00Z',boundaryAt='2020-12-20T05:00:00Z',coverageFrom='2020-12-20T05:00:00Z',quantity='60')
    ha['terms']['memberships'][1]['side']['fixedPrice']['price']='50';hamended=save(ha)
    htail=next(b for b in hamended['bindings'] if b['bindingId']!=hbinding['bindingId'])
    later_frame=next(f for f in client.call('ppa/frames') if f['at'].startswith('2020-12-18'))['id']
    hp=client.call('settlement/payment-lineages/plan',dict(snapshotId=later_frame,tradeId=hbook['tradeId'],expectedRevision=2,original=horigin,mappings=[horigin,mapping_for(htail,2)],policies=POLICIES))
    earlier=preview(lineageId=hp['lineage']['id'])
    check('late planned mapping retains originating business time',earlier['proposalPins']['tradeHead']['revision']==1 and len(earlier['components'])==721)
    from concurrent.futures import ThreadPoolExecutor
    from threading import Barrier
    racebook=save(payload);racebinding=racebook['bindings'][0];raceorigin=mapping_for(racebinding,1);barrier=Barrier(2)
    def race(_):
        barrier.wait(timeout=10)
        return json.loads(runtime(f"SELECT settlement_data.plan_payment_lineage('{catalog}','{racebook['tradeId']}',1,security.capture_date_context('{catalog}','2020-12-15',NULL),'2020-12-15T16:00:00Z',{encoded(raceorigin)},{encoded([raceorigin])},{encoded(POLICIES)});"))
    with ThreadPoolExecutor(max_workers=2) as pool: raced=list(pool.map(race,range(2)))
    check('independent database sessions assign one canonical lineage',raced[0]['lineage']['id']==raced[1]['lineage']['id'] and query(f"SELECT count(*) FROM settlement_data.payment_lineages WHERE catalog_id='{catalog}' AND trade_id='{racebook['tradeId']}';")=='1')
    bad=dict(snapshotId=later_frame,tradeId=hbook['tradeId'],expectedRevision=2,original=horigin,mappings=[horigin,horigin],policies=POLICIES)
    check('duplicated or extra mapping cannot narrow manifest',client.call('settlement/payment-lineages/plan',bad,expected=422)['code']=='settlement_manifest')
    bad={**body,'mappings':[mapping]*33}
    check('binding-work limit refuses before planning',client.call('settlement/payment-lineages/plan',bad,expected=422)['code']=='settlement_manifest')
    for incompatible in ('due','direction'):
        fresh=save(payload);first_map=mapping_for(fresh['bindings'][0],1)
        change=deepcopy(amendment);change['tradeId']=fresh['tradeId'];change['expectedRevision']=1
        if incompatible=='due':change['terms']['memberships'][1]['side']['paymentRule']['dayOfMonth']=11
        else:change['terms']['memberships'][1]['coefficient']='1'
        altered=save(change);last_binding=next(b for b in altered['bindings'] if b['bindingId']!=fresh['bindings'][0]['bindingId'])
        command=dict(snapshotId=frame,tradeId=fresh['tradeId'],expectedRevision=2,original=first_map,mappings=[first_map,mapping_for(last_binding,2)],policies=POLICIES)
        check('incompatible '+incompatible+' refuses atomic lineage planning',client.call('settlement/payment-lineages/plan',command,expected=422)['code']=='settlement_incompatible' and query(f"SELECT count(*) FROM settlement_data.payment_lineages WHERE catalog_id='{catalog}' AND trade_id='{fresh['tradeId']}';")=='0')
    for table,selected_catalog in [('market_catalog_scopes',catalog),('reference_catalog_scopes',manifest['referenceCatalogId'])]:
        where=f"catalog_id='{selected_catalog}' AND tenant_id='{tenant}' AND workspace_id='{workspace}'"
        saved=query(f"SELECT row_to_json(s)::text FROM security.{table} s WHERE {where};");retained=cutoff()
        try:
            query(f"DELETE FROM security.{table} WHERE {where};")
            check('current '+table+' access is required despite old pins',preview(expected=403,known=retained,expectedRevision=3)['code']=='access_denied')
        finally:query(f"INSERT INTO security.{table} SELECT(json_populate_record(NULL::security.{table},{encoded(json.loads(saved))}::json)).*;")
    publication_count=query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
    client.call('settlement/payment-lineages/plan',dict(snapshotId=later_frame,tradeId=hbook['tradeId'],expectedRevision=2,original=horigin,mappings=[horigin,mapping_for(htail,2)],policies=POLICIES))
    check('idempotent planning creates no empty publication',query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")==publication_count)
    query("DELETE FROM security.role_permissions WHERE permission='ProposeSettlementDeterminations' AND role_id=(SELECT id FROM security.roles WHERE code='trader');")
    check('current proposal authority loss refuses retained preview',preview(expected=403,expectedRevision=3)['code']=='access_denied')
    print(f'{count} settlement preview checks passed',flush=True)
