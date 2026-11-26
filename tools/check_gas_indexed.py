"""Independent daily indexed payment booking, retained PV and generic risk oracles."""
from copy import deepcopy
from fractions import Fraction
import json
from uuid import uuid4
from preview_client import PreviewClient,initial_frame
from preview_fixture import trade_booking
from synthetic_side_calendars import side_calendar_policy


def run_checks(url,query,manifest,run_worker):
    c=PreviewClient(url);c.bootstrap();c.call('profile',dict(profile='trader'))
    frame=initial_frame(c.call('workspace'),'2020-12-18')
    context=dict(snapshotId=frame['id'],convention='physical_gas_fixed_quantity_v4',knowledgeMode='current')
    cat=c.call('gas/catalog',context)
    p=next(p for p in cat['locations'] if p['deliveryPoint']=='synthetic_gas_point_indexed')
    body={**context,**trade_booking(),'operationId':str(uuid4()),'bookId':cat['books'][0]['id'],'definitionRevisionId':p['definitionRevisionId'],
          'gasSetupId':cat['gasSetupId'],'discounting':False,'profile':'indexed_gas_composition_v1',
          'physicalSides':[dict(code='gas',pipeline=p['pipeline'],zone=p['zone'],deliveryPoint=p['deliveryPoint'],product=p['product'],direction='receive',
              deliveryFrom='2021-01-01',deliveryTo='2021-03-01',quantityBasis='per_operating_day',quantity='100',link=None)],
          'cashSides':[dict(code='payment',role='indexed_consideration',share='1',direction='pay',source='gas',stage='delivery',price=None,tariffCode=None,paymentAt=None,
              indexed=dict(index='synthetic_daily_index',observationConvention='named_index_daily_v1',paymentConvention='next_month_day_utc_v1',paymentDay=10,spread='0',forecastCurve=None))]}
    calendar=next(row['reference'] for row in cat['sideCalendars'] if row['code']=='test_utc_every_day')
    body['physicalSides'][0]['calendarPolicy']=side_calendar_policy(calendar,delivery=True,observation=False,payment=False)
    body['cashSides'][0]['calendarPolicy']=side_calendar_policy(calendar,delivery=True,observation=True,payment=True)
    # Asset uses two monthly quotes; each payment day uses its own varying quote.
    asset=100*(31*Fraction('3.2')+28*Fraction('3.3'))
    payable=100*sum(Fraction(3)+Fraction(n,1000) for n in range(59))
    preview=c.call('gas/composition/preview',body)
    assert [Fraction(s['presentValue']) for s in preview['sides']]==[asset,-payable],preview
    assert Fraction(preview['presentValue'])==asset-payable==Fraction('1288.9')
    saved=c.call('gas/composition/book',body)
    assert c.call('gas/composition/book',body)==saved
    overview=c.call('trading/gas',dict(context={**context,'convention':'trading_workspace_v1'},tradeId=saved['tradeId'],bindingId=saved['instrumentId'],revision=1,instrumentType='physical_gas'))
    index=overview['indexedCash'][0]
    assert (index['index_code'],index['observation_convention'],index['payment_convention'],index['payment_day'],index['spread'],index['projection_id'])==('synthetic_daily_index','named_index_daily_v1','next_month_day_utc_v1','10','0',None),index
    assert next(m for m in overview['components'] if m['role']=='indexed_consideration')['payment_at'] is None
    ticket=c.call('tickets/trade',dict(context=context,tradeId=saved['tradeId'],bindingId=saved['instrumentId'],revision=1))
    assert [s['kind'] for s in ticket['draft']['sides']]==['physical','floating']
    assert len([r for r in ticket['schedule']['rows'] if r['level']=='day'])==59
    payment=ticket['draft']['sides'][1]['values']
    assert payment['floating_spread']=='0' and payment['floating_payment_day']=='10' and payment['floating_forecast_curve']=='assigned'
    assert Fraction(c.call('tickets/preview',dict(context=context,draft=ticket['draft']))['presentValue'])==asset-payable
    # Entry hints remain visible, editable and separate from pricing validation.
    def lookup(side,field,draft=None):return c.call('tickets/lookup',dict(context=context,draft=draft or ticket['draft'],side=side,field=field))
    blank=deepcopy(ticket['draft']);blank['sides'][0]['values'].pop('quantity')
    assert lookup('gas','quantity',blank)['value']=='100'
    blank['sides'][0]['values'].pop('delivery_from');blank['sides'][0]['values'].pop('delivery_to')
    assert lookup('gas','delivery_from',blank)['value']=='2021-01-01'
    assert lookup('gas','delivery_to',blank)['value']=='2021-02-01'
    assert lookup('gas','quantity')['value']=='100'
    # A manually chosen spread changes only the contractual cash amount.
    spread=deepcopy(body);spread['cashSides'][0]['indexed']['spread']='0.02'
    assert Fraction(c.call('gas/composition/preview',spread)['presentValue'])==asset-payable-118
    for label,change in [('missing projection',lambda x:x['cashSides'][0]['indexed'].pop('forecastCurve')),
                         ('unknown nested policy',lambda x:x['cashSides'][0]['indexed'].update(fallback='current')),
                         ('invalid payment day',lambda x:x['cashSides'][0]['indexed'].update(paymentDay=29)),
                         ('fixed price with index',lambda x:x['cashSides'][0].update(price='3')),
                         ('wrong index',lambda x:x['cashSides'][0]['indexed'].update(index='test_price_b')),
                         ('unbounded spread',lambda x:x['cashSides'][0]['indexed'].update(spread='1000000000000')),
                         ('missing cash array',lambda x:x.update(cashSides=None))]:
        bad=deepcopy(body);change(bad)
        try:c.call('gas/composition/preview',bad,expected=400 if label in ['missing projection','unknown nested policy','missing cash array'] else 422)
        except AssertionError as error:raise AssertionError(label) from error
    check_native(query,manifest,saved,body)
    shared={**context,'convention':'trading_workspace_v1'}
    c.call('profile',dict(profile='risk'))
    scope=dict(context=shared,bookIds=[],portfolioIds=[],tradeIds=[saved['tradeId']])
    catalog=c.call('portfolio/risk/catalog',scope)
    assert not catalog['excludedPositions'] and len(catalog['factors'])==61
    request={**scope,'context':{**shared,'knowledgeMode':'retained','contextId':catalog['context']['contextId']},'detail':'holdings',
             'convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],'pricingPolicy':catalog['pricingPolicy'],'stress':[],
             'bumps':[]}
    from check_portfolio_risk import financial
    factors=[]
    identity=lambda factor:json.dumps(factor,sort_keys=True)
    # Normal request-size bounds apply equally to tests and UI clients.
    for start in range(0,len(catalog['factors']),16):
        batch={**request,'bumps':[dict(factor=f['factor'],size='0.01',convention=catalog['bumpConvention']) for f in catalog['factors'][start:start+16]]}
        result=c.call('portfolio/risk',batch)
        assert not result['excludedPositions'] and result['work']['positionCount']==1,result
        assert len(result['factors'])==len(batch['bumps'])
        assert {identity(f['factor']['factor']) for f in result['factors']}=={identity(b['factor']) for b in batch['bumps']}
        assert all(len(f['positions'])==1 and f['positions'][0]['tradeId']==saved['tradeId'] for f in result['factors'])
        job=c.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=batch));run_worker()
        done=c.call('portfolio/risk/jobs/'+job['id'])
        assert done['state']=='completed',done
        assert financial(done['result'])==financial(result)
        factors.extend(result['factors'])
    assert len(factors)==len({identity(f['factor']['factor']) for f in factors})==61
    for factor in factors:
        f=factor['factor'];values=factor['positions'][0]['values']
        assert Fraction(values['base'])==asset-payable and Fraction(values['gamma'])==0
        start=f['deliveryFrom'];end=f['deliveryTo']
        expected=(3100 if start=='2021-01-01' else 2800) if end in ['2021-02-01','2021-03-01'] and start.endswith('-01') else -100
        assert Fraction(values['delta'])==expected,(f,values)
    assert query('BEGIN;SET LOCAL SESSION AUTHORIZATION preview_risk_worker;SELECT count(*) FROM trading.gas_index_cash_terms;ROLLBACK;')=='0'
    c.call('profile',dict(profile='trader'))
    changed=deepcopy(ticket['draft']);changed['sides'][1]['values'].update(floating_spread='0.02',floating_payment_day='11')
    amendment=dict(context=context,draft=changed,tradeId=saved['tradeId'],bindingId=saved['instrumentId'],expectedRevision=1,
                   definitionRevisionId=body['definitionRevisionId'],reason='Synthetic indexed payment correction',policy='future_gas_composition_replace_v1',operationId=str(uuid4()))
    assert Fraction(c.call('tickets/amend-preview',amendment)['presentValue'])==asset-payable-118
    revised=c.call('tickets/amend',amendment)
    assert revised['revision']==2 and c.call('tickets/amend',amendment)==revised
    reopened=c.call('tickets/trade',dict(context=context,tradeId=saved['tradeId'],bindingId=revised['instrumentId'],revision=2))
    assert reopened['draft']==changed
    no_op={**amendment,'operationId':str(uuid4()),'bindingId':revised['instrumentId'],'expectedRevision':2}
    unchanged=c.call('tickets/amend',no_op)
    assert unchanged['unchanged'] and unchanged['revision']==2 and c.call('tickets/amend',no_op)==unchanged
    assert c.call('tickets/status',dict(operationId=amendment['operationId']))==revised
    print('PASS daily indexed consideration: 59 distinct day prices, monthly asset quotes, explicit spread, retained ticket/booking, native shape refusals and 61-factor black-box risk/worker parity',flush=True)
    check_location_purchase_and_sale(c,query,context,cat,p,calendar,ticket['draft'])
    return revised


def check_location_purchase_and_sale(c,query,context,catalog,sale_point,calendar,indexed_ticket):
    selection=dict(agreements='one_agreement',purchasePrice='fixed',salePrice='indexed')
    draft,=c.call('tickets/gas-locations/start',dict(context=context,**selection))
    draft['trade']=indexed_ticket['trade']
    purchase_point=next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    sides={s['code']:s for s in draft['sides']}
    for code,point,quantity in [('purchase',purchase_point,'100'),('sale',sale_point,'97')]:
        sides[code]['values'].update(pipeline=point['pipeline'],zone=point['zone'],delivery_point=point['deliveryPoint'],
            product=point['product'],delivery_from='2021-01-01',delivery_to='2021-01-03',quantity_basis='per_operating_day',quantity=quantity)
        sides[code]['calendarPolicy']=side_calendar_policy(calendar,delivery=True,observation=False,payment=False)
    sides['purchase_payment']['values'].update(cash_share='1',cash_part='whole',cash_start='2021-01-01',cash_end='2021-01-03',cash_stage='delivery',cash_price='2.75',cash_payment_at='2021-02-02T00:00:00Z')
    sides['purchase_payment']['calendarPolicy']=side_calendar_policy(calendar,delivery=True,observation=False,payment=True)
    sides['sale_payment']['values']={**indexed_ticket['sides'][1]['values'],**sides['sale_payment']['values']}
    sides['sale_payment']['values'].update(floating_start='2021-01-01',floating_end='2021-01-03')
    sides['sale_payment']['calendarPolicy']=side_calendar_policy(calendar,delivery=True,observation=True,payment=True)
    preview=c.call('tickets/preview',dict(context=context,draft=draft))
    values={s['code']:Fraction(s['presentValue']) for s in preview['sides']}
    assert values['purchase_payment']==-200*Fraction('2.75') and values['sale_payment']==97*(3+Fraction('3.001')),values
    leg=dict(draft=draft,definitionRevisionId=preview['source']['definitionRevisionId'])
    receipt=c.call('tickets/gas-locations/book',dict(context=context,operationId=str(uuid4()),tickets=[leg],**selection))
    trade=receipt['purchaseTradeId']
    assert receipt['saleTradeId']==trade
    instrument=query(f"SELECT instrument_id FROM trading.gas_trade_revisions WHERE trade_id='{trade}' AND revision=1")
    reopened=c.call('tickets/trade',dict(context=context,tradeId=trade,bindingId=instrument,revision=1))
    retained={s['code']:s['values'] for s in reopened['draft']['sides']}
    assert retained['purchase']['quantity']=='100' and retained['sale']['quantity']=='97'
    assert (retained['purchase_payment']['cash_source'],retained['purchase_payment']['cash_price'])==('purchase','2.75')
    assert (retained['sale_payment']['floating_source'],retained['sale_payment']['floating_index'])==('sale','synthetic_daily_index')
    print('PASS two-location booking values fixed purchase and indexed sale on their own daily quantities and reopens both payment terms',flush=True)


def check_native(query,manifest,saved,body):
    catalog=manifest['catalogId'];instrument=saved['instrumentId']
    schedule=query(f"SELECT schedule_id FROM trading.instrument_side_schedules WHERE(catalog_id,instrument_id,slot)=('{catalog}','{instrument}',1);")
    terms=json.dumps(body['cashSides'][0]['indexed']).replace("'","''")
    call=f"PERFORM trading.require_gas_index_cash_shape('{catalog}','{schedule}',(SELECT t FROM trading.gas_component_terms t WHERE(t.catalog_id,t.instrument_id,t.slot)=('{catalog}','{instrument}',0)),(SELECT a FROM market_data.gas_valuation_assignments a WHERE(a.catalog_id,a.code)=('{catalog}','synthetic_index_assignment')),'{terms}'::jsonb);"
    query('DO $test$ BEGIN '+call+'END$test$;')
    where=f"WHERE(catalog_id,schedule_id)=('{catalog}','{schedule}')"
    for mutation,code in [
        (f"UPDATE trading.side_schedule_rows SET parameters='{{\"numerator\":\"2\",\"denominator\":\"1\"}}' {where} AND local_key='payment/0/quantity';",'gas_index_quantity'),
        (f"UPDATE trading.side_schedule_rows SET value_at=value_at+interval '1 day' {where} AND payment_ordinal=0;",'gas_schedule_completion'),
        (f"UPDATE trading.side_schedule_rows SET parameters='{{\"numerator\":\"1\",\"denominator\":\"1\"}}' {where} AND local_key='payment/0/observation/0/adder';",'gas_index_schedule'),
        (f"UPDATE market_data.index_fixing_observations SET fixing_at='2021-03-10T00:00:00Z' WHERE(catalog_id,revision_id,index_code,observation_from,observation_to)=('{catalog}','{manifest['revisionId']}','synthetic_daily_index','2021-01-01','2021-01-02');",'gas_index_payment')]:
        query("BEGIN;ALTER TABLE trading.side_schedule_rows DISABLE TRIGGER ALL;ALTER TABLE market_data.index_fixing_observations DISABLE TRIGGER ALL;"+mutation+
              "DO $test$ BEGIN BEGIN "+call+"RAISE EXCEPTION 'malformed_index_accepted';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'"+code+"' THEN RAISE;END IF;END;END$test$;ROLLBACK;")
    assert query("SELECT has_table_privilege('openetrm_runtime','trading.gas_index_cash_terms','INSERT'),has_function_privilege('openetrm_runtime','trading.require_gas_index_cash_shape(uuid,uuid,trading.gas_component_terms,market_data.gas_valuation_assignments,jsonb)','EXECUTE');")=='f|f'
