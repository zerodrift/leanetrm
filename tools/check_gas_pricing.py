"""Independent admission and valuation oracles for synthetic gas payment entries."""
from copy import deepcopy
from fractions import Fraction
import json
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking
from synthetic_side_calendars import side_calendar_policy


def request(c):
    workspace=c.call('workspace')
    context=dict(snapshotId=initial_frame(workspace,'2020-12-18')['id'],convention='physical_gas_fixed_quantity_v4',knowledgeMode='current')
    catalog=c.call('gas/catalog',context)
    point=next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_quantity_point')
    calendar=next(x['reference'] for x in catalog['sideCalendars'] if x['code']=='test_utc_every_day')
    physical=dict(code='gas',pipeline=point['pipeline'],zone=point['zone'],deliveryPoint=point['deliveryPoint'],product=point['product'],direction='receive',
                  deliveryFrom='2021-01-13',deliveryTo='2021-01-16',quantityBasis='per_operating_day',quantity='10000',link=None,quantityPolicy='delivery_schedule_v1',
                  calendarPolicy=side_calendar_policy(calendar,delivery=True,observation=False,payment=False))
    def cash(code,index,convention,direction,part,spread='0'):
        return dict(code=code,role='indexed_consideration',source='gas',direction=direction,stage='delivery',share='1',part=part,price=None,tariffCode=None,paymentAt=None,
                    indexed=dict(index=index,observationConvention=convention,paymentConvention='next_month_day_utc_v1',paymentDay=10,spread=spread,forecastCurve=None),
                    calendarPolicy=side_calendar_policy(calendar,delivery=True,observation=True,payment=True))
    month='synthetic_entry_monthly';daily='synthetic_entry_daily'
    band=lambda lower,upper:dict(kind='band',lower=lower,upper=upper)
    short=dict(kind='shortfall',lower='10000',upper=None)
    body={**context,**trade_booking(), 'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'definitionRevisionId':point['definitionRevisionId'],
          'gasSetupId':catalog['gasSetupId'],'discounting':False,'profile':'indexed_gas_composition_v1','physicalSides':[physical],
          'cashSides':[cash('base',month,'named_index_monthly_v1','pay',band('0','8000')),
                       cash('above',daily,'named_index_daily_v1','pay',band('8000',None),'0.05'),
                       cash('short_daily',daily,'named_index_daily_v1','receive',short),cash('short_monthly',month,'named_index_monthly_v1','pay',short)]}
    return context,body


def run_checks(url,query,manifest,run_worker,admin_load):
    c=PreviewClient(url);c.bootstrap();c.call('profile',dict(profile='trader'))
    context,body=request(c)
    booked=c.call('gas/composition/book',body)
    assert c.call('gas/composition/book',body)==booked
    terms=json.loads(query(f"SELECT jsonb_agg(jsonb_build_array(slot,share_n::text,share_d::text,part_kind,part_lower_n::text,part_lower_d::text,part_upper_n::text,part_upper_d::text) ORDER BY slot) FROM trading.gas_component_terms WHERE instrument_id='{booked['instrumentId']}';"))
    assert terms==[[0,None,None,None,None,None,None,None],[1,'1','1','band','0','1','4','5'],[2,'1','1','band','4','5',None,None],[3,'1','1','shortfall','1','1',None,None],[4,'1','1','shortfall','1','1',None,None]],terms
    bad=[]
    eight=deepcopy(body);eight['cashSides']=[{**eight['cashSides'][0],'code':f'pay_{i}'} for i in range(8)];bad.append(eight)
    linked=deepcopy(body);linked['physicalSides'][0].pop('quantityPolicy');linked['physicalSides'].append({**linked['physicalSides'][0],'code':'linked','quantity':None,'link':dict(source='gas',formula='gross_less_fraction',fraction='0.02')});bad.append(linked)
    early=deepcopy(body);early['cashSides'][0]['deliveryFrom']='2021-01-12';bad.append(early)
    equal=deepcopy(body);equal['cashSides'][0]['part']=dict(kind='band',lower='8000',upper='8000');bad.append(equal)
    missing=deepcopy(body);missing['cashSides'][0].pop('share');bad.append(missing)
    for invalid in bad:
        invalid['operationId']=str(uuid4())
        assert c.call('gas/composition/book',invalid,expected=422)['code']=='gas_composition_input'
    for convention,index,code in [('daily_published_index_v1','synthetic_entry_daily','gas_index_terms'),('named_index_daily_v1','synthetic_missing_index','gas_index_coverage')]:
        invalid=deepcopy(body);invalid['operationId']=str(uuid4());invalid['cashSides'][0]['indexed'].update(observationConvention=convention,index=index)
        assert c.call('gas/composition/book',invalid,expected=422)['code']==code
    # An exact period-total fraction and a shorter fixed payment must survive native admission.
    plain=deepcopy(body);plain.update(operationId=str(uuid4()),profile='fixed_gas_composition_v1')
    plain['physicalSides'][0]['quantityBasis']='period_total_equal_days'
    plain['cashSides']=[dict(code='fixed',role='consideration',direction='pay',source='gas',stage='delivery',share='1',price='3',tariffCode=None,paymentAt='2021-02-10T00:00:00Z',
                           deliveryFrom='2021-01-14',deliveryTo='2021-01-16',calendarPolicy=side_calendar_policy(body['cashSides'][0]['calendarPolicy']['payment']['calendar'],delivery=True,observation=False,payment=True))]
    partial=c.call('gas/composition/book',plain)
    assert Fraction(c.call('gas/composition/editor',dict(context=context,tradeId=partial['tradeId']))['valuation']['sides'][1]['presentValue'])==-20000
    route_checks(c,context,body,booked,run_worker,query,admin_load)
    native_admission(query,manifest,body)
    billing_checks(c,query,manifest,body,booked,plain)
    print('PASS gas pricing admission: reduced shares and parts, monthly index, replay, independent refusals and exact partial period-total payment',flush=True)
    return dict(tradeId=booked['tradeId'],instrumentId=booked['instrumentId'],snapshotId=context['snapshotId'])


def upgrade_checks(query,migration):
    from assistant_context import migration_body
    body=migration_body(migration.read_text())
    signatures=['security.write_gas_composition(uuid,jsonb,timestamptz,boolean)',
                'trading.require_gas_index_cash_shape(uuid,uuid,trading.gas_component_terms,market_data.gas_valuation_assignments,jsonb)',
                'trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz,jsonb)',
                'security.record_gas_billing(uuid,jsonb,timestamptz,uuid,jsonb)']
    for signature in signatures:
        for change in [f'GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;',f"ALTER FUNCTION {signature} SET work_mem='64kB';",
                       f'ALTER FUNCTION {signature} OWNER TO CURRENT_USER;',
                       f"DO $drift$ DECLARE s text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(s,'AS $function$','AS $function$ /* synthetic drift */');END$drift$;"]:
            try:query(f'BEGIN;{change}{body}ROLLBACK;')
            except RuntimeError as error:assert 'gas_pricing_upgrade: unexpected predecessor' in str(error),str(error)
            else:raise AssertionError('Changed pricing predecessor accepted: '+signature)
    query(f'BEGIN;SET LOCAL search_path=security,public;{body}ROLLBACK;')
    print('PASS gas pricing upgrade: four predecessor definitions, owners, grants and configuration are pinned; caller search path is independent',flush=True)


def native_admission(query,manifest,body):
    from check_booked_side_programs import runtime_query
    cat=manifest['catalogId']
    mutations=[('share',None),('share','0'),('part',dict(kind='band',lower='8000',upper='8000')),('deliveryFrom','2021-01-12')]
    for key,value in mutations:
        invalid=deepcopy(body);operation=str(uuid4());invalid['operationId']=operation
        if value is None:invalid['cashSides'][0].pop(key)
        else:invalid['cashSides'][0][key]=value
        wire=json.dumps(invalid).replace("'","''")
        prefix=f"""INSERT INTO trading.side_schedule_requests SELECT(jsonb_populate_record(NULL::trading.side_schedule_requests,
          to_jsonb(s)||jsonb_build_object('request_id','{operation}','command_hash',encode(sha256(convert_to('{wire}'::jsonb::text,'UTF8')),'hex')))).*
          FROM trading.side_schedule_requests s WHERE(catalog_id,request_id)=('{cat}','{body['operationId']}');"""
        # bytea JSON uses PostgreSQL's explicit hex representation.
        prefix=prefix.replace("'command_hash',encode(","'command_hash','\\x'||encode(")
        for table in ['side_schedule_request_items','side_schedule_request_seals']:
            prefix+=f"INSERT INTO trading.{table} SELECT(jsonb_populate_record(NULL::trading.{table},to_jsonb(s)||jsonb_build_object('request_id','{operation}'))).* FROM trading.{table} s WHERE(catalog_id,request_id)=('{cat}','{body['operationId']}');"
        runtime_query(query,manifest,f"DO $test$ BEGIN BEGIN PERFORM security.book_gas_composition('{cat}','{wire}'::jsonb,'2020-12-18T16:00:00Z');RAISE EXCEPTION 'malformed_entry_accepted';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'gas_composition_input' THEN RAISE;END IF;END;END$test$;",prefix=prefix,commit=False)
    print('PASS native entry admission independently refuses missing/zero share, equal band levels and dates outside the source',flush=True)


def billing_checks(c,query,manifest,body,booked,plain):
    from check_booked_side_programs import runtime_query
    after={**{k:body[k] for k in ['snapshotId','convention','knowledgeMode']},'snapshotId':next(f['id'] for f in c.call('workspace')['frames'] if f['at'].startswith('2021-01-15T08:'))}
    preview=dict(context=after,tradeId=booked['tradeId'],expectedRevision=1,cashSlot=1,day='2021-01-13',selectionPolicy='final_allocated_billing_v1',profile='gas_final_daily_fixed_cash_v1',decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1')
    assert c.call('gas/billing/preview',preview,expected=422)['code']=='gas_billing_input'
    fixed=deepcopy(plain);fixed['operationId']=str(uuid4());fixed['physicalSides'][0]['quantityBasis']='per_operating_day'
    for key in ['deliveryFrom','deliveryTo']:fixed['cashSides'][0].pop(key)
    held=c.call('gas/composition/book',fixed)
    quantities=c.call('gas/quantities/read',dict(context=after,tradeId=held['tradeId'],selectionPolicy='operational_projection_v1'))
    source=c.call('gas/quantities/record',dict(context=after,tradeId=held['tradeId'],expectedRevision=1,operationId=str(uuid4()),obligationId=quantities['rows'][0]['obligationId'],kind='allocated',status='final',day='2021-01-13',quantity='9000',evidence='Synthetic final entry allocation',expectedPreviousId=None))
    accepted=c.call('gas/billing/preview',{**preview,'tradeId':held['tradeId']})
    assert Fraction(accepted['amount'])==-27000
    rejected=dict(preview=preview,operationId=str(uuid4()),previewHash=accepted['previewHash'],sourceId=accepted['sourceId'],expectedPreviousId=None,correctionDueAt=None,reason=None,evidence='Synthetic unsupported entry billing')
    calc=dict(sourceId=accepted['sourceId'],amount='-27000',numerator='-27000',denominator='1')
    wire=json.dumps(rejected).replace("'","''");calculation=json.dumps(calc).replace("'","''")
    runtime_query(query,manifest,f"DO $test$ BEGIN BEGIN PERFORM security.record_gas_billing('{manifest['catalogId']}','{wire}'::jsonb,'2021-01-15T08:00:00Z','{accepted['context']['contextId']}','{calculation}'::jsonb);RAISE EXCEPTION 'unsupported_entry_billed';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'gas_billing_profile' THEN RAISE;END IF;END;END$test$;")
    print('PASS API and native billing refuse pricing entries while full-term, whole fixed cash remains supported',flush=True)


def route_checks(c,context,body,booked,run_worker,query,admin_load):
    from check_portfolio_risk import VIEW,financial
    trade=booked['tradeId']
    shared={**context,'convention':'trading_workspace_v1'}
    def editor(selected=None):return c.call('gas/composition/editor',dict(context=selected or context,tradeId=trade))['valuation']
    def values(value):return [Fraction(s['presentValue']) for s in value['sides']]
    def risk(expected,queued=True):
        c.call('profile',dict(profile='risk'))
        scope=dict(context=shared,bookIds=[],portfolioIds=[],tradeIds=[trade])
        catalog=c.call('portfolio/risk/catalog',scope)
        request={**scope,'context':{**shared,'knowledgeMode':'retained','contextId':catalog['context']['contextId']},'detail':'holdings',
                 'convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],'pricingPolicy':catalog['pricingPolicy'],'stress':[],
                 'bumps':[dict(factor=f['factor'],size='0.1',convention=catalog['bumpConvention']) for f in catalog['factors'][:16]]}
        result=c.call('portfolio/risk',request)
        assert result['factors']
        for factor in result['factors']:
            p=factor['positions'][0]
            assert Fraction(p['values']['base'])==sum(expected),p
            assert [Fraction(e['base']) for e in p['entries']]==expected,p
        if queued:
            job=c.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=request));run_worker()
            done=c.call('portfolio/risk/jobs/'+job['id'])
            assert done['state']=='completed' and financial(done['result'])==financial(result),done
        c.call('profile',dict(profile='trader'))
    def routes(expected,preview=True):
        value=editor();assert values(value)==expected,value
        assert Fraction(value['presentValue'])==sum(expected)
        row=next(h for h in c.call('trading/blotter',dict(context=shared,view=VIEW))['holdings'] if h['tradeId']==trade)
        assert Fraction(row['presentValue'])==sum(expected)
        assert [Fraction(s['presentValue']) for s in row['sides']]==expected,row
        if preview:
            held={**body,'previewTradeId':trade}
            assert values(c.call('gas/composition/preview',held))==expected
            changed=deepcopy(held);changed['cashSides'][0]['share']='1.1'
            assert c.call('gas/composition/preview',changed,expected=422)['code']=='gas_composition_input'
            assert c.call('gas/composition/book',held,expected=422)['code']=='gas_composition_input'
        risk(expected)
        return value
    baseline=[93000,-72000,-19500,0,0]
    assert values(c.call('gas/composition/preview',body))==baseline
    pinned=c.call('gas/quantities/read',dict(context=context,tradeId=trade,selectionPolicy='operational_projection_v1'))['context']
    routes(baseline)
    quantities=c.call('gas/quantities/read',dict(context=context,tradeId=trade,selectionPolicy='operational_projection_v1'))
    obligation=quantities['rows'][0]['obligationId']
    def record(day,kind,quantity,status='provisional'):
        return c.call('gas/quantities/record',dict(context=context,tradeId=trade,expectedRevision=1,operationId=str(uuid4()),obligationId=obligation,
            kind=kind,status=status,day=day,quantity=quantity,evidence='Synthetic pricing entry selection',expectedPreviousId=None))
    record('2021-01-14','scheduled','9000');record('2021-01-15','allocated','7000')
    value=routes([80600,-69000,-9750,12800,-12000])
    day=next(r for r in value['schedule']['rows'] if r['key']=='2021-01-14')
    cell=day['cells']['above.quantity']
    assert Fraction(cell['value'])==-1000 and cell['quantityPart']=='band_v1',cell
    assert Fraction(cell['sourceQuantity'])==9000 and cell['sources']==['scheduled'],cell
    c.select_inputs(pinned)
    assert values(editor({**context,'knowledgeMode':'retained','contextId':pinned['contextId']}))==baseline
    historical_preview={**body,'previewTradeId':trade,'previewContextId':pinned['contextId'],'knowledgeMode':'retained'}
    assert values(c.call('gas/composition/preview',historical_preview))==baseline
    c.call('profile',dict(profile='viewer'))
    c.call('gas/composition/preview',historical_preview,expected=403)
    c.call('profile',dict(profile='trader'))
    print('PASS pricing Example A: every entry and total in held preview, editor, Blotter and queued risk; source labels and earlier cutoff',flush=True)

    original=deepcopy(body);before=context
    body=deepcopy(original);body['operationId']=str(uuid4());body['physicalSides'][0].pop('quantityPolicy')
    held=c.call('gas/composition/book',body);trade=held['tradeId']
    routes(baseline)
    body=deepcopy(original);body['operationId']=str(uuid4());body['cashSides']=[{**body['cashSides'][2],'code':'shared','part':None,'share':'1.05','deliveryTo':'2021-01-14'}]
    held=c.call('gas/composition/book',body);trade=held['tradeId']
    shared_value=routes([93000,33600])
    shared_day=next(r for r in shared_value['schedule']['rows'] if r['key']=='2021-01-13')
    assert Fraction(shared_day['cells']['shared.quantity']['value'])==10500
    body=deepcopy(original);body['operationId']=str(uuid4());body['cashSides']=[{**body['cashSides'][0],'deliveryFrom':'2021-01-14'}]
    held=c.call('gas/composition/book',body);trade=held['tradeId']
    obligation=c.call('gas/quantities/read',dict(context=context,tradeId=trade,selectionPolicy='operational_projection_v1'))['rows'][0]['obligationId']
    assert values(editor())[1]==-48000
    record('2021-01-13','scheduled','500')
    routes([63550,-48000])
    print('PASS pricing routes: contract-only parts, exact share and source records outside payment dates',flush=True)

    # The dedicated administrator loads a started trade; ordinary booking stays closed.
    from gas_pricing_fixture import ADMINISTRATOR
    query("CREATE ROLE preview_gas_in_term_loader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS INHERIT IN ROLE openetrm_runtime,openetrm_gas_in_term_loader;")
    context={**before,'snapshotId':next(f['id'] for f in c.call('workspace')['frames'] if f['at'].startswith('2021-01-15T08:'))}
    shared={**context,'convention':'trading_workspace_v1'}
    body={**original,**context,'operationId':str(uuid4())}
    assert c.call('gas/composition/book',body,expected=422)['code']=='gas_delivery_lifecycle'
    loaded=admin_load(dict(administrator=ADMINISTRATOR[0],composition=body))
    assert loaded.returncode==0,loaded.stderr
    held=json.loads(loaded.stdout);trade=held['tradeId']
    repeated=admin_load(dict(administrator=ADMINISTRATOR[0],composition=body))
    assert repeated.returncode==0 and json.loads(repeated.stdout)==held,repeated.stderr
    obligation=c.call('gas/quantities/read',dict(context=context,tradeId=trade,selectionPolicy='operational_projection_v1'))['rows'][0]['obligationId']
    record('2021-01-13','allocated','9000','final');record('2021-01-14','allocated','7000','final')
    b=routes([31000,-69000,-9850,12600,-12000])
    assert [Fraction(b['measures'][k]) for k in ['realizedUnsettled','unrealized','total']]==[-47750,500,-47250],b['measures']
    print('PASS pricing Example B: administrator load and replay, published monthly/daily prices, accrued and unrealized entry values on every route',flush=True)
