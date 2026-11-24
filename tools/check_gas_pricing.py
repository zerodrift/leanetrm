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


def run_checks(url,query,manifest,run_worker):
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
