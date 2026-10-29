"""Independent synthetic draft break-even checks through the authorized API."""
from copy import deepcopy
from fractions import Fraction as F
from uuid import uuid4
from exact_values import fraction
from preview_client import PreviewClient
from check_ppa_bookings_http import profile_terms


def run_checks(url, query, manifest):
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'trader'});client.bootstrap()
    frame=next(f for f in client.call('ppa/frames') if not f.get('ppaBundle'))['id']
    terms=profile_terms(manifest)
    editor=PreviewClient(url);editor.bootstrap();editor.call('profile',{'profile':'editor'});editor.bootstrap()
    series=manifest['intervalCurves']['shapeSeries']['additive']
    revision=int(query(f"SELECT coalesce(max(revision),0) FROM market_data.interval_series_revisions WHERE series_id='{series}';"))
    editor.call('interval-series/revise',dict(requestId=str(uuid4()),seriesId=series,expectedRevision=revision,reason='Synthetic break-even shape',segments=[dict(start='2031-11-01T04:00:00Z',values=['0']*721)]))
    known=client.call('interval-series')['knownAt']
    body=dict(snapshotId=frame,terms=terms,quantity='80',coverageFrom='2031-11-02T04:00:00Z',coverageTo='2031-11-03T05:00:00Z',
        knownAt=known,pendingActuals='require_actuals_v1',selectedSide=dict(role='strike_payment',ordinal=0),measure='CompletePv',quoteDecimalPlaces=2,quoteRounding='half_even_v1')
    before=query("SELECT count(*) FROM trading.ppa_trade_revisions;")
    value=client.call('ppa/break-even',body)
    assert fraction(value['price'])==F(165,4) and fraction(value['sensitivity'])==-2000
    assert fraction(value['inputValue'])==2500 and fraction(value['quoteResidual'])==0
    assert query("SELECT count(*) FROM trading.ppa_trade_revisions;")==before
    print('PASS break-even HTTP: independent physical DST root, signed sensitivity and no trade mutation',flush=True)
    count=1
    def check(name, condition=True):
        nonlocal count
        assert condition,name
        count+=1;print('PASS break-even HTTP: '+name,flush=True)
    def solve(expected=200,**changes):return client.call('ppa/break-even',{**body,**changes},expected=expected)
    check('model label, explicit scope and quote metadata',value['label']=='Model break-even price' and value['scope']=='unbooked_draft' and value['quote']=='41.25' and value['quoteIsExact'] and value['exactPriceEnterable'] and value['assumptions']['commercialRiskPremium'] is False)
    periods=value['profiles']['periods'];physical=next(p for p in periods if p['kind']=='Delivery');payment=next(p for p in periods if p['kind']=='Payment')
    check('server monthly DST quantity, physical valuation and contractual cash stay separate',fraction(physical['quantity'])==2000 and fraction(physical['amount'])==82500 and fraction(payment['amount'])==-80000 and fraction(physical['effectivePrice'])==F(165,4) and fraction(payment['effectivePrice'])==40)
    check('server strike-value curve marks exact root with zero selected value',next(p for p in value['profiles']['valueCurve'] if p['label']=='Model break-even')['value']['numerator']=='0')
    check('both basis valuations consume request work',value['work']['unitValuations']==2 and value['work']['rowEvaluations']==100)
    receipt=value['receipt'];check('complete request, membership and source pins',receipt['request']==body and len(receipt['basis'])==2 and all(len(b['memberships'])==2 for b in receipt['basis']) and receipt['inputs']['knowledgeContext'] and receipt['inputs']['observations'] and receipt['inputs']['shapes'])
    check('catalog advertises existing operation deadline',client.call('ppa/catalog',{'snapshotId':frame})['breakEven']['limits']['deadlineSeconds']==20)
    virtual=deepcopy(terms);profile=deepcopy(virtual['memberships'][0]['side']['quantity'])
    virtual['memberships'][0].update(role='indexed_payment')
    virtual['memberships'][0]['side'].update(kind='indexed',location=None,indexPrice=dict(curve=manifest['intervalCurves']['shaped'],quantity='test_power_quantity',multiplier='1',indexMultiplier='1',settlement=None,adder='1'))
    virtual['memberships'][1]['side']['quantity']=profile
    v=solve(terms=virtual);check('virtual fixed-for-floating independent root',fraction(v['price'])==41 and fraction(v['inputValue'])==2000)
    inverse=deepcopy(virtual)
    for m in inverse['memberships']:m['coefficient']=str(-int(m['coefficient']))
    iv=solve(terms=inverse);check('reversed direction preserves root and reverses sensitivity',fraction(iv['price'])==41 and fraction(iv['sensitivity'])==2000)
    fractional=deepcopy(virtual);fractional['memberships'][1]['side']['fixedPrice']['multiplier']='3'
    fv=solve(terms=fractional);check('fractional root, quote and independently calculated residual',fraction(fv['price'])==F(41,3) and fv['quote']=='13.67' and fraction(fv['quoteResidual'])==-20 and not fv['exactPriceEnterable'] and not fv['quoteIsExact'])
    quoted=deepcopy(fractional);quoted['memberships'][1]['side']['fixedPrice']['price']=fv['quote']
    preview=dict(snapshotId=frame,definitionRevisionId=terms['definitionRevisionId'],referenceCatalogId=terms['referenceCatalogId'],referenceRevisionId=terms['referenceRevisionId'],scope='draft',selectedIds=[],explicitSubset=False,terms=quoted,quantity='80',coverageFrom=body['coverageFrom'],coverageTo=body['coverageTo'],knownAt=known,pendingActuals='require_actuals_v1',detailMonths=[])
    check('rounded quote residual agrees with ordinary valuation engine',fraction(client.call('ppa/preview',preview)['valueExcludingOverdue'])==-20)
    check('explicit different quantity scales slope only',fraction(solve(quantity='40')['sensitivity'])==-1000 and fraction(solve(quantity='40')['price'])==F(165,4))
    for name in ('quantity','coverageFrom','coverageTo','knownAt','measure','selectedSide','quoteDecimalPlaces','quoteRounding'):
        omitted={k:v for k,v in body.items() if k!=name};check('omitted '+name+' refuses',client.call('ppa/break-even',omitted,expected=400)['code']=='request_format')
    omitted=deepcopy(body);del omitted['terms']['discounting']
    check('discount choice cannot be omitted',client.call('ppa/break-even',omitted,expected=400)['code']=='request_format')
    check('a trade identity is not accepted by draft pricing',solve(expected=400,tradeId=str(uuid4()))['code']=='request_format')
    check('unknown selected measure refuses',solve(expected=422,measure='all_cash')['code']=='ppa_break_even_measure')
    check('unknown selected side refuses',solve(expected=422,selectedSide=dict(role='strike_payment',ordinal=1))['code']=='ppa_break_even_side')
    stepped=deepcopy(terms);stepped['memberships'][1]['side']['fixedPrice'].update(kind='stepped',price=None,steps=[])
    stepped['memberships'][1]['side']['fixedPrice']['steps']=[{'from':'2031-11-01','price':'40'}]
    check('selected stepped strike refuses',solve(expected=422,terms=stepped)['code']=='ppa_break_even_side')
    over=deepcopy(terms);over['delivery']['to']='2062-12-01'
    check('request work limit precedes valuation',solve(expected=422,terms=over)['code']=='ppa_size')
    wrong=deepcopy(terms);wrong['definitionRevisionId']=str(uuid4())
    check('incompatible revision refuses',solve(expected=422,terms=wrong).get('code') is not None)
    discount=deepcopy(terms);discount['discounting']=True
    check('missing required discount inputs refuse',solve(expected=422,terms=discount).get('code') is not None)
    # A later source correction is separate from the unchanged draft terms.
    revision+=1
    editor.call('interval-series/revise',dict(requestId=str(uuid4()),seriesId=series,expectedRevision=revision,reason='Synthetic shape correction',segments=[dict(start='2031-11-02T05:00:00Z',values=['3'])]))
    newer=client.call('interval-series')['knownAt'];corrected=solve(knownAt=newer)
    check('later-known shape correction changes only the later context',fraction(corrected['price'])!=fraction(value['price']) and fraction(solve(knownAt=known)['price'])==F(165,4) and corrected['receipt']['inputs']['knownAt']!=value['receipt']['inputs']['knownAt'])
    editor.call('interval-series/revise',dict(requestId=str(uuid4()),seriesId=series,expectedRevision=revision+1,reason='Synthetic missing shape',segments=[dict(start='2031-11-02T05:00:00Z',values=[None])]))
    missing=client.call('interval-series')['knownAt']
    check('missing price coverage refuses rather than solving a subset',solve(expected=422,knownAt=missing).get('code') is not None)
    query("DELETE FROM security.role_permissions WHERE permission='ReadTrades' AND role_id=(SELECT id FROM security.roles WHERE code='trader');")
    try:check('current authority loss refuses old pinned calculation',solve(expected=403)['code']=='access_denied')
    finally:query("INSERT INTO security.role_permissions SELECT id,'ReadTrades' FROM security.roles WHERE code='trader';")
    check('draft pricing and corrections create no booking or amendment',query("SELECT count(*) FROM trading.ppa_trade_revisions;")==before)
    print(f'{count} break-even HTTP checks passed',flush=True)
