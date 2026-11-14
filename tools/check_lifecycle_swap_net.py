"""Qualified gross/net swap actions reuse the existing native settlement ledger."""
import json
from uuid import uuid4


def checks(query, manifest, call, managed, booking, occurrence, determine_policy, snapshot):
    catalog=manifest['catalogId']
    def lit(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    booked=managed(dict(snapshotId=snapshot,booking={**booking,'operationId':str(uuid4())}))['LIFECYCLE_SWAP_BOOKED']
    trade=booked['tradeId']
    binding=query(f"SELECT binding_id FROM trading.swap_trade_revisions WHERE(catalog_id,trade_id,revision)=('{catalog}','{trade}',1);")
    for ordinal in (0,1):
        profile={**occurrence,'operationId':str(uuid4()),'profileId':str(uuid4()),'tradeId':trade,'membershipOrdinal':ordinal}
        call(f"SELECT lifecycle.save_swap_profile('{catalog}',{lit(profile)});")
    source=call(f"SELECT security.capture_date_context('{catalog}','2021-01-15',NULL);")
    packet=dict(snapshotId=snapshot,frameAt='2021-01-15T08:00:00Z',
        context=dict(contextId=source,asOf='2021-01-15T08:00:00Z',businessDate='2021-01-15',snapshotId=snapshot),
        target=dict(handler='financial_swap',scopeId=trade,bindingId=binding))
    for ordinal in (0,1):
        managed({**packet,'membershipOrdinal':ordinal})
        packet['context']={**packet['context'],'contextId':call(f"SELECT security.capture_date_context('{catalog}','2021-01-15',NULL);")}
    policy={**determine_policy,'operationId':str(uuid4()),'policyId':str(uuid4()),'action':'swap.net'}
    call(f"SELECT lifecycle.save_policy('{catalog}',{lit(policy)});")
    profile=dict(operationId=str(uuid4()),profileId=str(uuid4()),tradeId=trade,expectedRevision=0,originRevision=1,action='swap.net',
        policyId=policy['policyId'],policyRevision=1,referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],
        currency='usd',decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1',duePolicy='swap_due_nominal_v1',
        recognitionPolicy='swap_completed_period_trading_pnl_v1',correctionPolicy='swap_explicit_correction_due_v1',netPolicy='explicit_same_due_swap_net_v1')
    saved=json.loads(call(f"SELECT lifecycle.save_swap_net_profile('{catalog}',{lit(profile)})::text;"))
    assert saved['revision']==1
    print('PASS configured native swap net profile retains exact trade, currency and execution policy',flush=True)
    assert json.loads(call(f"SELECT lifecycle.save_swap_net_profile('{catalog}',{lit(profile)})::text;"))==saved
    publications=query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
    same=json.loads(call(f"SELECT lifecycle.save_swap_net_profile('{catalog}',{lit({**profile,'operationId':str(uuid4())})})::text;"))
    assert same['revision']==1 and not same['changed']
    assert query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")==publications
    currency=dict(revision=dict(catalogId=manifest['referenceCatalogId'],revisionId=manifest['referenceRevisionId']),code='usd')
    ids=query(f"SELECT d.id FROM settlement_data.swap_determinations d JOIN settlement_data.swap_occurrences o ON(o.catalog_id,o.id)=(d.catalog_id,d.occurrence_id) WHERE(d.catalog_id,d.trade_id)=('{catalog}','{trade}') ORDER BY o.membership_ordinal;").splitlines()
    def balances():return query(f"SELECT string_agg((d.contribution-coalesce((SELECT sum(a.amount) FROM settlement_data.swap_payment_allocations a WHERE(a.catalog_id,a.determination_id)=(d.catalog_id,d.id)),0))::text,',' ORDER BY o.membership_ordinal) FROM settlement_data.swap_determinations d JOIN settlement_data.swap_occurrences o ON(o.catalog_id,o.id)=(d.catalog_id,d.occurrence_id) WHERE(d.catalog_id,d.trade_id)=('{catalog}','{trade}');")
    # Independent ledger oracle: fixed -3 x100 and retained fixture fixing 3.5 x100.
    from fractions import Fraction as F
    assert list(map(F,balances().split(',')))==[-300,350]
    reviewed_requests=[]
    def record(version,amount,values,reference,reverses=None,expected_error=None):
        selected={**packet,'action':'swap.net','target':{**packet['target'],'handler':'financial_swap_net'},
            'context':{**packet['context'],'contextId':call(f"SELECT security.capture_date_context('{catalog}','2021-01-15',NULL);")}}
        request=dict(context=dict(snapshotId=snapshot,convention='independent_side_financial_swap_v2',knowledgeMode='current',contextId=None),
            tradeId=trade,expectedRevision=1,expectedVersion=version,currency=currency,amount=amount,
            allocations=[dict(determinationId=i,amount=value) for i,value in zip(ids,values)],policy='explicit_same_due_swap_net_v1',reversesId=reverses)
        selected['net']=dict(preview=request,effectiveAt='2021-01-15T08:00:00Z',reference=reference)
        if expected_error:selected['expectedError']=expected_error
        result=managed(selected)
        if expected_error:return None
        reviewed_requests.append(result['LIFECYCLE_SWAP_REVIEWED'])
        return result['LIFECYCLE_SWAP_OUTCOME']
    first=record(2,'50',['-300','350'],'Synthetic net settlement')
    assert list(map(F,balances().split(',')))==[0,0]
    record(3,None,[],'Synthetic full net reversal',first['paymentId'])
    assert list(map(F,balances().split(',')))==[-300,350]
    record(4,'20',['-100','120'],'Synthetic partial net settlement')
    assert list(map(F,balances().split(',')))==[-200,230]
    zero=record(5,'0',['-200','200'],'Synthetic noncash discharge')
    assert zero['recordKind']=='zero_net_discharge_v1' and list(map(F,balances().split(',')))==[0,30]
    record(6,None,[],'Synthetic noncash discharge reversal',zero['paymentId'])
    assert list(map(F,balances().split(',')))==[-200,230]
    assert query(f"SELECT count(*) FROM settlement_data.swap_cash_payments WHERE trade_id='{trade}';")=='5'
    assert query(f"SELECT count(*) FROM settlement_data.swap_payment_allocations WHERE trade_id='{trade}';")=='10'
    print('PASS shared net lifecycle: gross settlement, exact reversal, partial cash, zero discharge, first-outcome retry and unchanged financial sequence on acknowledgements',flush=True)
    for cash,lines,code in [('31',['-200','230'],'settlement_net_allocation'),('29',['-201','230'],'settlement_net_allocation'),('231',['1','230'],'settlement_net_allocation')]:
        record(7,cash,lines,'Synthetic refused net',expected_error=code)
    # Independent native admission: a forged calculation cannot hide behind
    # an otherwise acknowledged old financial snapshot or its unchanged hash.
    from copy import deepcopy
    first_request=deepcopy(reviewed_requests[0]);first_request['operationId']=str(uuid4())
    source=first_request['context']
    first_request['context']=dict(contextId=source['contextId'],asOf='2021-01-15T08:00:00.000000Z',businessDate='2021-01-15')
    calculation=dict(contextId=source['contextId'],amount='50',allocations=[dict(determination=i,amount=value) for i,value in zip(ids,['-300','350'])])
    def native(body,derived):
        return call(f"SELECT security.admit_trading_business_source('{catalog}','{source['contextId']}','2021-01-15T08:00:00Z','2021-01-15');"
            f"SELECT lifecycle.record_swap_net_action('{catalog}',{lit(body)},lifecycle.capture_action_authority('{catalog}','swap.net',{lit(body)}),{lit(derived)})::text;")
    accepted=json.loads(native(first_request,calculation).splitlines()[-1]);assert accepted['paymentId']==first['paymentId'] and accepted['currentVersion']==7
    for body,derived,code in [(dict(first_request,operationId=str(uuid4())),dict(calculation,amount='51'),'lifecycle_native_calculation'),
        (dict(first_request,operationId=str(uuid4())),dict(calculation,allocations=[dict(determination=ids[0],amount='-301'),dict(determination=ids[1],amount='351')]),'lifecycle_native_calculation')]:
        try:native(body,derived)
        except RuntimeError as error:assert code in str(error),str(error)
        else:raise AssertionError('Expected '+code)
    assert query(f"SELECT count(*) FROM settlement_data.swap_cash_payments WHERE trade_id='{trade}';")=='5'
    assert query(f"SELECT count(*) FROM settlement_data.swap_payment_allocations WHERE trade_id='{trade}';")=='10'
    # A settings acknowledgement of A after B never makes A current again.
    alternative={**policy,'operationId':str(uuid4()),'policyId':str(uuid4()),'mode':'manual'}
    call(f"SELECT lifecycle.save_policy('{catalog}',{lit(alternative)});")
    changed={**profile,'operationId':str(uuid4()),'expectedRevision':1,'policyId':alternative['policyId']}
    assert json.loads(call(f"SELECT lifecycle.save_swap_net_profile('{catalog}',{lit(changed)})::text;"))['revision']==2
    prior=json.loads(call(f"SELECT lifecycle.save_swap_net_profile('{catalog}',{lit({**profile,'operationId':str(uuid4()),'expectedRevision':2})})::text;"))
    assert prior['acknowledgedRevision']==1 and prior['currentRevision']==2 and not prior['changed'] and prior['semanticAcknowledgement']
    print('PASS net source and acknowledgement boundaries: exact native gross lines, old snapshot after later cash, explicit settings A/B/A and no new allocations',flush=True)
