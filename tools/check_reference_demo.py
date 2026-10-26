"""Working synthetic reference rows through exact-version discounted swap pricing."""
from decimal import Decimal, localcontext, ROUND_HALF_EVEN
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url,manifest,demo,query,run_worker):
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'trader'})
    frame=initial_frame(client.call('workspace'),'2020-12-18')
    rates=client.call('reference/catalog?table=interest_rate_definitions')
    assert rates['referenceRevision']['revisionId']==demo['referenceRevisionId']
    assert [r['code'] for r in rates['rows']]==['demo_simple_rate']
    numeric=client.call('reference/catalog?table=value_specifications')
    assert [r['code'] for r in numeric['rows']]==['demo_decimal_rate']
    currencies=client.call('reference/catalog?table=currencies')
    assert currencies['referenceRevision']['revisionId']==manifest['referenceRevisionId']
    assert query("SELECT to_regclass('ref_data.cash_rounding_conventions') IS NULL;")=='t'
    assert client.call('reference/catalog?table=cash_rounding_conventions',expected=422)['code']=='reference_page'
    omitted=initial_frame(client.call('workspace/navigation'),demo['omittedAt'][:10])
    unavailable=client.call('swaps/catalog',{'snapshotId':omitted['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'})
    assert all(i['definitionRevisionId']!=demo['definitionRevisionId'] for i in unavailable['indices'])
    context={'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'}
    catalog0=client.call('swaps/catalog',context)
    assert {c['code'] for c in currencies['rows']}
    choices={c['definitionRevisionId']:c for c in catalog0['referenceChoices']}
    assert choices[manifest['revisionId']]['paymentCalendars'] and choices[demo['definitionRevisionId']]['paymentCalendars']
    assert all(c['reference']['revision']['revisionId']==demo['referenceRevisionId'] for c in choices[demo['definitionRevisionId']]['paymentCalendars'])
    index=next(i for i in catalog0['indices'] if i['definitionRevisionId']==demo['definitionRevisionId'] and i['curve']=='demo_forward')
    assert index['defaultDiscountCurve']=='demo_discount_curve'
    book=str(uuid4());query(f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic reference discounting',publication_order,effective_at FROM trading.books LIMIT 1;")
    delivery={'deliveryFrom':demo['deliveryFrom'],'deliveryTo':demo['deliveryTo']}
    payment={'frequency':'monthly','rule':'days_after_period_end','days':2,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}
    request={**context,**trade_booking(),'operationId':str(uuid4()),'bookId':book,'definitionRevisionId':demo['definitionRevisionId'],
        'commodity':index['commodity'],'quantity':'10','fixedCoefficient':'-1','discounting':True,
        'fixed':{'price':'40','currency':index['currencyCode'],**delivery,'paymentSchedule':payment},
        'floating':{'curve':'demo_forward','index':'demo_monthly_index','currency':index['currencyCode'],**delivery,'paymentSchedule':payment,'resetFrequency':'monthly','averagingMethod':'none'}}
    priced=client.call('swaps/preview',request)
    # Independent simple ACT/365 zero-rate oracle: ten units at 50 minus 40.
    from datetime import date,datetime
    days=(date.fromisoformat(demo['paymentDate'])-date.fromisoformat(frame['at'][:10])).days
    with localcontext() as ctx:
        ctx.prec=80;factor=(Decimal(365)/(Decimal(365)+Decimal('0.03')*days)).quantize(Decimal('1e-18'),rounding=ROUND_HALF_EVEN)
    assert Fraction(priced['valuation']['presentValue'])==Fraction(factor*100),priced
    booked=client.call('swaps/book',request)
    reopened=client.call('swaps/editor',{'context':context,'tradeId':booked['tradeId'],'revision':None})
    assert Fraction(reopened['valuation']['presentValue'])==Fraction(factor*100)
    assert query(f"SELECT s.reference_revision_id FROM trading.swap_trade_revisions r JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id) JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(i.catalog_id,i.floating_side_id) WHERE r.trade_id='{booked['tradeId']}';")==demo['referenceRevisionId']
    client.call('profile',{'profile':'risk'})
    scope={'context':{**context,'convention':'trading_workspace_v1'},'bookIds':[book],'portfolioIds':[]}
    catalog=client.call('portfolio/risk/catalog',scope)
    assert len(catalog['factors'])==1
    risk_request={**scope,'context':{**scope['context'],'knowledgeMode':'retained','contextId':catalog['context']['contextId']},'detail':'holdings',
        'convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],'pricingPolicy':catalog['pricingPolicy'],'stress':[],
        'bumps':[{'factor':catalog['factors'][0]['factor'],'size':'0.1','convention':catalog['bumpConvention']}]}
    report=client.call('portfolio/risk',risk_request)
    value=report['factors'][0]['positions'][0]['values']
    assert Fraction(value['base'])==Fraction(factor*100) and Fraction(value['delta'])==Fraction(factor*10)
    client.call('profile',{'profile':'editor'})
    price_pin=next(f for f in demo['frames'] if datetime.fromisoformat(f['at'].replace('Z','+00:00'))==datetime.fromisoformat(frame['at'].replace('Z','+00:00')))['observationIds'][0]
    corrected=client.call('corrections',{'snapshotId':frame['id'],'observationId':price_pin,'value':'51'})
    client.call('profile',{'profile':'risk'})
    scope['context']['snapshotId']=corrected['frame']['id']
    corrected_catalog=client.call('portfolio/risk/catalog',scope)
    corrected_request={**risk_request,**scope,'context':{**scope['context'],'knowledgeMode':'retained','contextId':corrected_catalog['context']['contextId']},
        'bumps':[{'factor':corrected_catalog['factors'][0]['factor'],'size':'0.1','convention':corrected_catalog['bumpConvention']}]}
    corrected_report=client.call('portfolio/risk',corrected_request)
    assert Fraction(corrected_report['factors'][0]['positions'][0]['values']['base'])==Fraction(factor*110)
    job=client.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':corrected_request})
    run_worker();completed=client.call('portfolio/risk/jobs/'+job['id'])
    from check_portfolio_risk import financial
    assert completed['state']=='completed' and financial(completed['result'])==financial(corrected_report)
    assert query("SELECT bool_and(convalidated) FROM pg_constraint WHERE contype='f' AND connamespace='ref_data'::regnamespace;")=='t'
    print('PASS reference demo: visible supported rows, separate original catalog, exact bundle references, selected rate mapping, independent discounted PV, retained booking and corrected-source worker parity',flush=True)
