"""HTTP checks against a newly owned synthetic preview; invoked by run_preview.py."""
from preview_client import PreviewClient

def run_checks(url,query,manifest,direct_reads,after_browser):
    passed=0
    class Client(PreviewClient):
        def __init__(self):
            super().__init__(url)
    def check(name,condition=True):
        nonlocal passed
        if not condition:
            raise AssertionError(name)
        passed+=1
        print('PASS '+name,flush=True)
    c=Client();boot=c.bootstrap()
    check('synthetic preview boundary is explicit',boot['mode']=='local-synthetic-preview' and boot['profile'] is None)
    startup=c.call('bootstrap?includeFrames=false&includeAccess=true')
    check('anonymous bootstrap explicitly has no selected-profile access projection',startup['access'] is None and startup['profile'] is None)
    compact=c.call('bootstrap?includeFrames=false')
    check('identity bootstrap can omit unused frame payload without changing session metadata',compact['frames']==[] and
          all(compact[key]==boot[key] for key in ['mode','runId','profiles','profile','customProfiles','catalogId','tenantId','workspaceId']) and
          bool(compact['csrfToken']) and bool(boot['frames']))
    check('full bootstrap remains compatible and compact reads preserve exact retained frames',
          c.call('bootstrap?includeFrames=true')['frames']==boot['frames']==c.call('bootstrap')['frames'])
    c.call('bootstrap?includeFrames=unknown',expected=400);check('invalid bootstrap frame selection is rejected')
    c.call('workspace',expected=403);check('no identity does not read workspace')
    c.call('workspace/catalog',expected=403);check('navigation catalog requires current identity')
    c.call('reference/catalog?table=currencies',expected=403);check('reference pages require current identity')
    c.call('profile',{'profile':'viewer'},headers={'X-Preview-CSRF':'invalid'},expected=400);check('CSRF token enforced')
    c.call('profile',{'profile':'viewer'},headers={'Origin':'https://untrusted.invalid'},expected=403);check('foreign origin rejected')
    c.call('health',headers={'Host':'untrusted.invalid'},expected=403);check('host boundary enforced')
    c.call('profile',{'profile':'administrator'},expected=403);check('client cannot invent test identity')
    c.call('profile',{'profile':'viewer'})
    from check_bootstrap_access import run_checks as check_bootstrap_access
    check_bootstrap_access(c,check,query)
    from check_market_navigation import run_checks as check_market_navigation
    check_market_navigation(c,check,query,manifest)
    from check_first_pricing import run_checks as check_first_pricing
    check_first_pricing(c,check)
    workspace=c.call('workspace')
    check('database timestamp transport stays UTC under a non-UTC API environment',all(m.get('event_at') is None or m['event_at'].endswith('+00:00') for m in workspace['references']['futures_milestones']))
    check('viewer reads real catalog and exact input frames',len(workspace['definitions']['curves'])==10 and len(workspace['frames'])==4)
    compact=c.call('bootstrap?includeFrames=false')
    check('compact identity bootstrap retains the selected profile and authorized workspace pins',compact['profile']=='viewer' and
          c.call('workspace/catalog')['frames']==workspace['frames'])
    named=workspace['definitions']['curves']+workspace['definitions']['rate_curves']
    directory=c.call('curve-names')
    check('the curve-name directory serves stored names for every catalog curve, including other bundles',{v['code'] for v in named}<={d['code'] for d in directory} and {'daily_gas','daily_power'}<={d['code'] for d in directory} and all(d['name'] for d in directory))
    check('every commodity and rate curve carries its stored display name',all(isinstance(v.get('name'),str) and v['name'].strip()==v['name']!='' for v in named) and len({v['name'] for v in named})==len(named))
    check('speculative reference catalogs are absent',not any(t in workspace['references'] for t in ['day_count_conventions','compounding_conventions','fx_rate_definitions','heat_rate_definitions']))
    point=next(r for r in workspace['references']['locations'] if r['code']=='test_facility_a')
    check('physical location exposes exact coordinate strings',point['latitude']=='12.345678' and point['longitude']=='-45.678912')
    check('pipeline and region types are explicit reference links',all(any(r['code']=='test_'+kind for r in workspace['references']['location_types']) for kind in ['pipeline','node','hub','region','terminal','delivery_point']))
    current=next(f for f in workspace['frames'] if f['at'].startswith('2020-12-18'))
    old=current['id'];obs='70000000-0000-0000-0000-000000000001'
    check('all selected observations supplied',len(current['observationIds'])==40)
    check('financial values transported as strings',all(isinstance(o['value'],str) for o in workspace['observations']))
    check('reference inspector includes the used interest specification, venues and indexes',all(k in workspace['references'] for k in ['exchanges','interest_rate_definitions','price_index_definitions','futures_products']))
    check('reference financial values transported as strings',all(isinstance(q['trading_tick'],str) for q in workspace['references']['quote_specifications']))
    for curve in workspace['definitions']['curves']:
        result=c.call('evaluate',{'snapshotId':old,'curve':curve['code']})
        check('curve builds six exact dated constituents: '+curve['code'],len(result['points'])==6 and all(isinstance(p['value'],str) for p in result['points']))
    base=c.call('evaluate',{'snapshotId':old,'curve':'local_power'})
    check('five-layer dependency value is exact',base['points'][0]['value']=='29.1')
    from decimal import Decimal
    trace=base['trace']; by_id={n['id']:n for n in trace}
    check('trace has one node per actual evaluated constituent',len(trace)==base['evaluated']==54 and len(by_id)==len(trace))
    for node in trace:
        assert all(i['id'] in by_id for i in node['inputs'])
        if node['rule']!='direct':
            inputs={i['slot']:by_id[i['id']] for i in node['inputs']}
            factor=Decimal(node['multiplier']['value']) if node['rule']=='scale_add' else Decimal(1)
            assert Decimal(node['value'])==factor*Decimal(inputs['base']['value'])+Decimal(inputs['spread']['value'])
    check('every trace equation reconciles to server price strings')
    check('each selected contract has its exact root trace',all(any(n['curve']==base['curve'] and n['contractId']==p['contractId'] and n['value']==p['value'] and n['lineage']==p['lineage'] for n in trace) for p in base['points']))
    check('trace multiplier retains exact selected observation',all(n['multiplier']['value']=='7' and n['multiplier']['observationId'] in current['observationIds'] for n in trace if n['rule']=='scale_add'))
    c.call('evaluate',{'snapshotId':old,'curve':'absent'},expected=422);check('missing curve raises, never returns empty success')
    body={'snapshotId':old,'observationId':obs,'value':'3.1'}
    c.call('corrections',body,expected=403);check('viewer write rejected by authorized operation')
    c.call('profile',{'profile':'editor'})
    c.call('corrections',{**body,'value':'3.0000000000000000001'},expected=422);check('excess precision rejected')
    c.call('corrections',{**body,'value':"3; SELECT 1"},expected=422);check('non-decimal input rejected')
    c.call('corrections',{**body,'value':None},expected=422);check('missing decimal rejected explicitly')
    c.call('corrections',{**body,'value':'3.1001'},expected=422);check('daily settlement clearing grid enforced')
    changed=c.call('corrections',body)
    new=changed['frame']['id']
    result=c.call('evaluate',{'snapshotId':new,'curve':'local_power'})
    check('one gas correction propagates through all dependent layers',result['points'][0]['value']=='29.8' and result['points'][1:]==base['points'][1:])
    independent=c.call('evaluate',{'snapshotId':new,'curve':'local_power_all_in'})
    check('independent all-in mark retains its own value',independent['points'][0]['value']=='29.1')
    check('all-in trace has no inferred base relationship',len(independent['trace'])==6 and all(n['inputs']==[] and n['multiplier'] is None for n in independent['trace']))
    replay=c.call('evaluate',{'snapshotId':old,'curve':'local_power'})
    check('original snapshot replays unchanged',replay==base)
    history=c.call('history',{'revisionPolicy':'exact_snapshot_pins_v1','snapshotId':new,'curve':'local_power','contractId':result['points'][0]['contractId']})
    check('contract history uses exact dated pins and chosen revision',len(history['points'])==4 and history['points'][-1]['value']=='29.8')
    c.call('corrections',body,expected=409);check('stale revision cannot fork correction chain')
    selected=c.call('select-observation',{'snapshotId':new,'observationId':obs})
    replay=c.call('evaluate',{'snapshotId':selected['frame']['id'],'curve':'local_power'})
    check('prior revision can be explicitly selected',replay['points'][0]['value']=='29.1')
    c.call('profile',{'profile':'viewer'})
    c.call('corrections',{'snapshotId':new,'observationId':changed['observationId'],'value':'3.2'},expected=403);check('identity switch applies current permissions')
    other=Client();other.bootstrap();other.call('profile',{'profile':'viewer'})
    other.call('evaluate',{'snapshotId':new,'curve':'local_power'},expected=422);check('snapshot selection is isolated by browser session')
    # Recovery never relies on receiving the mutation's returned frame or ID.
    recovery=other.call('workspace')
    successor=next(o for o in recovery['observations'] if o['corrects_id']==obs)
    recovery_frame=next(f for f in recovery['frames'] if f['at'].startswith('2020-12-18'))
    recovered=other.call('select-observation',{'snapshotId':recovery_frame['id'],'observationId':successor['id']})
    recovered_value=other.call('evaluate',{'snapshotId':recovered['frame']['id'],'curve':'local_power'})
    check('committed correction recovers without mutation response or retry',recovered_value['points'][0]['value']=='29.8')
    rates=c.call('evaluate',{'snapshotId':old,'curve':'interest_rates'})
    check('rate convention labels come with the evaluated rate result',rates['convention']=={'dayCount':'Actual/365 Fixed','compounding':'Simple compounding'})
    check('interest curve has real dated rate samples, not commodity contracts',rates['kind']=='rates' and len(rates['points'])==3 and all('seriesId' in p and 'contractId' not in p for p in rates['points']))
    check('rate policy is explicit and convention implementation is local',rates['numericalPolicy']=='simple_actual365fixed_rational_to_even_18_v1' and rates['points'][-1]['actualDays']==365)
    check('annual decimal rates display as explicit percentages',[p['value'] for p in rates['points']]==['2.5','3','3.5'])
    check('exact rational calculation computes simple actual365 discount factor',rates['points'][-1]['displayDiscountFactor']=='0.966184')
    c.call('profile',{'profile':'editor'})
    rate_id=rates['points'][-1]['lineage'][0]
    c.call('corrections',{'snapshotId':old,'observationId':rate_id,'value':'2'},expected=422)
    check('rate sample domain is enforced')
    corrected_rate=c.call('corrections',{'snapshotId':old,'observationId':rate_id,'value':'0.04'})
    rate_frame=corrected_rate['frame']['id']
    revised_rates=c.call('evaluate',{'snapshotId':rate_frame,'curve':'interest_rates'})
    check('rate correction changes its discount factor only',revised_rates['points'][-1]['displayDiscountFactor']=='0.961538' and revised_rates['points'][:2]==rates['points'][:2])
    check('original interest inputs replay unchanged',c.call('evaluate',{'snapshotId':old,'curve':'interest_rates'})==rates)
    rate_history=c.call('history',{'revisionPolicy':'exact_snapshot_pins_v1','snapshotId':rate_frame,'curve':'interest_rates','seriesId':rates['points'][-1]['seriesId']})
    check('rate sample histories retain exact dated identity',len(rate_history['points'])==4 and rate_history['points'][-1]['value']=='4')
    c.call('history',{'revisionPolicy':'exact_snapshot_pins_v1','snapshotId':old,'curve':'interest_rates','contractId':base['points'][0]['contractId']},expected=422)
    check('interest samples cannot be requested as fictional commodity contracts')
    check('rate correction does not alter commodity prices',c.call('evaluate',{'snapshotId':rate_frame,'curve':'local_power'})['points'][0]['value']=='29.1')
    check('only successful corrections appended',len(c.call('workspace')['observations'])==len(workspace['observations'])+2)
    from check_schedule_preview import run_checks as check_schedules
    check_schedules(Client,check)
    from check_schedule_publication import run_checks as check_publication
    check_publication(Client,check)
    from check_temporal_preview import run_checks as check_temporal
    check_temporal(Client,check)
    from check_market_temporal import run_checks as check_market_temporal
    check_market_temporal(Client,check,after_browser)
    from check_admin_preview import run_checks as check_admin
    check_admin(Client,check)
    from check_power_preview import run_checks as check_power
    check_power(Client,check)
    from check_empty_trading_directory import run_checks as check_empty_directory
    check_empty_directory(c,check,query,manifest)
    from check_futures_preview import run_checks as check_futures
    check_futures(Client,check)
    from check_trading_directory import run_checks as check_trading_directory
    check_trading_directory(c,check,query,manifest)
    from check_trade_valuation_scope import run_checks as check_trade_valuation_scope
    check_trade_valuation_scope(c,check,query)
    from check_trading_pin_reads import run_checks as check_trading_pin_reads
    check_trading_pin_reads(c,check,query,manifest)
    from check_trading_compression import run_checks as check_trading_compression
    check_trading_compression(c,check,query,manifest)
    from check_risk_preview import run_checks as check_risk
    check_risk(Client,check)
    from check_tou_products import run_checks as check_tou_products
    check_tou_products(Client,check)
    from check_page_reads import run_checks as check_page_reads
    check_page_reads(c,check,direct_reads)
    from check_reference_reads import run_checks as check_reference_reads,run_scope_checks
    check_reference_reads(c,check)
    run_scope_checks(c,check,query,manifest)
    print(f'{passed} preview HTTP checks passed.',flush=True)
    return workspace


if __name__=='__main__':
    raise SystemExit('Use tools/run_preview.py --check so checks only touch a newly owned fixture.')
