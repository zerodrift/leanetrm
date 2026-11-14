"""Gas title, operator claims and surviving commodity loans in owned PostgreSQL."""
import json
from uuid import uuid4

from check_reference_editing import main
from preview_fixture import catalogue_fixture, trade_booking, trading_fixture


def checks(query, manifest, economic_worker=None):
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    query(trading_fixture(catalog))
    from rate_curve_fixture import discount_default
    publication=f"(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}')"
    reference=f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
    query(f"BEGIN;INSERT INTO trading.publications SELECT '{catalog}',max(publication_order)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';"
          +discount_default(catalog,str(uuid4()),manifest['revisionId'],reference,'usd','interest_rates',publication,'2020-01-01T00:00:00Z')+"COMMIT;")
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest, 'https://preview.invalid'))
    query("INSERT INTO security.role_permissions SELECT role_id,p.code FROM security.role_permissions r "
          "CROSS JOIN security.permissions p WHERE r.permission='AmendTrade' AND p.code IN"
          "('ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings','RunScenario') ON CONFLICT DO NOTHING;")

    def literal(value):
        return "'"+json.dumps(value).replace("'", "''")+"'::jsonb"

    def call(sql, profile='trader', rollback=False):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                      f"'https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query("SET SESSION AUTHORIZATION preview_runtime;BEGIN;"
                     f"SELECT security.use_context('{token}');{sql}"+('ROLLBACK;' if rollback else 'COMMIT;'))

    at = '2020-12-20T16:00:00Z'
    def capture():
        return call(f"SELECT security.capture_date_context('{catalog}','2020-12-20',NULL);")

    def command(account, action, body, policy=None, operation=None, profile='trader', context=None, rollback=False):
        policy_id, policy_revision = policy or (None, None)
        sql_uuid = lambda value: "NULL" if value is None else "'"+str(value)+"'"
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{operation or uuid4()}','{action}','{account}',"
                               f"{literal(body)},'{at}','{context or capture()}',{sql_uuid(policy_id)},{policy_revision or 'NULL'},false)::text;",profile,rollback))

    def rejected(work, code):
        try:
            work()
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Expected '+code)

    book = 'd1000000-0000-0000-0000-000000000001'
    entity = query(f"SELECT id FROM trading.legal_entities WHERE catalog_id='{catalog}' LIMIT 1;")
    party = query(f"SELECT id FROM trading.counterparties WHERE catalog_id='{catalog}' LIMIT 1;")
    terms = json.loads(query("SELECT jsonb_build_object('quantity',q.quantity_code,'currency',q.currency_code,'location',c.location_code)::text "
                             "FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)="
                             "(c.reference_catalog_id,c.reference_revision_id,c.quote_code) "
                             f"WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{manifest['revisionId']}','future');"))
    contract = '50000000-0000-0000-0000-000000000001'
    account_count=0
    def account(kind, selected_book=None, effective_at='2020-12-18T00:00:00Z'):
        nonlocal account_count
        account_count+=1
        identity = str(uuid4())
        command(identity,'account',dict(code=f'synthetic_{kind}_{account_count}',kind=kind,bookId=selected_book or book,legalEntityId=entity,
            legalEntityRevision=1,counterpartyId=party if kind=='operator_balance' else None,counterpartyRevision=1 if kind=='operator_balance' else None,
            referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],
            definitionRevisionId=manifest['revisionId'],markCurve='future',markContractId=contract,
            titlePolicy='explicit_accepted_title_v1',lossPolicy='separate_evidenced_loss_v1',effectiveAt=effective_at,**terms))
        return identity

    policies = {}
    def bind(identity, action, code):
        policy = str(uuid4())
        query(f"BEGIN;INSERT INTO trading.publications SELECT '{catalog}',coalesce(max(publication_order),0)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';"
              "INSERT INTO lifecycle.action_policies(catalog_id,id,revision,action,mode,from_at,until_at,implementation,actor_id,publication_order) "
              f"SELECT '{catalog}','{policy}',1,'{code}','manual','2020-01-01','2022-01-01','explicit_lifecycle_action_policy_v1',"
              f"(SELECT principal_id FROM security.identity_bindings WHERE subject='trader'),max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';COMMIT;")
        command(identity,'bind_policy',dict(action=code,policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))
        policies[(identity,action)] = (policy,1)

    evidence_count=0
    def evidence(identity, kind, qty, when='2020-12-19T12:00:00Z', source=None, revision=0):
        nonlocal evidence_count
        evidence_count+=1
        return command(identity,'evidence',dict(sourceKey=source or f'synthetic_{kind}_{evidence_count}',kind=kind,quantity=str(qty),occurredAt=when,
            evidence='Synthetic accepted operational evidence',expectedRevision=revision))['id']

    owned, operator = account('owned_inventory'), account('operator_balance')
    for action, code in [('receive_title','gas.owned.receive_title'),('reserve','gas.owned.reserve'),('release','gas.owned.release'),
                         ('dispatch','gas.owned.dispatch'),('loss','gas.owned.loss'),('borrow','gas.loan.borrow'),('repay','gas.loan.repay'),('fee_payment','gas.cash.record_payment')]:
        bind(owned,action,code)
    for action, code in [('operator_claim','gas.operator.record'),('operator_settle','gas.operator.settle')]:
        bind(operator,action,code)
    def apply(identity, action, body, **options):
        return command(identity,action,body,policies[(identity,action)],**options)
    missing_refusals=[]
    def expect_rolled_back_refusal(identity,action,body,code):
        try:
            apply(identity,action,body,rollback=True)
        except RuntimeError as error:
            assert code in str(error),str(error)
        else:
            missing_refusals.append(code)
    title = evidence(owned,'title_receipt',100)
    body = dict(evidenceId=title,quantity='100',titleAt='2020-12-19T12:00:00Z',availableAt='2020-12-19T13:00:00Z',
                originPolicy='opening_owned_stock_v1',carrying='nominal_mark_v1')
    operation = str(uuid4())
    title_context=capture()
    stock = apply(owned,'receive_title',body,operation=operation,context=title_context)
    assert apply(owned,'receive_title',body,operation=operation,context=title_context)==stock
    assert apply(owned,'receive_title',body)['id']==stock['id']
    assert apply(owned,'receive_title',{**body,'quantity':'100.0'})['id']==stock['id']
    rejected(lambda:apply(owned,'receive_title',{**body,'quantity':'99'},operation=operation),'gas_owned_conflict')
    before = capture()
    reserve = apply(owned,'reserve',dict(lotId=stock['id'],businessKey='delivery',quantity='60',effectiveAt='2020-12-19T13:00:00Z'))
    movement = evidence(owned,'physical_delivery',25,'2020-12-19T14:00:00Z')
    apply(owned,'dispatch',dict(lotId=stock['id'],evidenceId=movement,quantity='25',reservationId=reserve['id'],effectiveAt='2020-12-19T14:00:00Z'))
    rejected(lambda:apply(owned,'reserve',dict(lotId=stock['id'],businessKey='excess',quantity='41',effectiveAt='2020-12-19T13:30:00Z')),'gas_stock_reservation')
    def read(context=None):
        selected=context or capture()
        return json.loads(call(f"SELECT security.use_market_context('{catalog}','{selected}');SELECT security.use_trading_context('{catalog}','{at}');"
                               f"SELECT security.read_gas_owned_holdings('{catalog}','{selected}','{at}',ARRAY['{book}']::uuid[])::text;"))
    assert read(before)['positions'][0]['remaining_quantity']=='100'
    assert read()['positions'][0]['remaining_quantity']=='75'
    claim_evidence=evidence(operator,'operator_statement',10)
    claim=apply(operator,'operator_claim',dict(evidenceId=claim_evidence,quantity='10',effectiveAt='2020-12-19T12:00:00Z',
        dueAt='2020-12-21T12:00:00Z',carrying='nominal_mark_v1',settlementMode='return_quantity_v1',cashout=None))
    negative_receipt=evidence(owned,'title_receipt',-10,'2020-12-20T12:00:00Z')
    expect_rolled_back_refusal(operator,'operator_settle',dict(positionId=claim['id'],quantity='10',ownedAccountId=owned,lotId=None,reservationId=None,
        evidenceId=negative_receipt,effectiveAt='2020-12-20T12:00:00Z',availableAt='2020-12-20T12:00:00Z',sourceObservationId=None),'gas_owned_source')
    received=evidence(owned,'title_receipt',10,'2020-12-20T12:00:00Z')
    apply(operator,'operator_settle',dict(positionId=claim['id'],quantity='10',ownedAccountId=owned,lotId=None,reservationId=None,
        evidenceId=received,effectiveAt='2020-12-20T12:00:00Z',availableAt='2020-12-20T12:00:00Z',sourceObservationId=None))
    rows=read()['positions']
    assert not any(row['id']==claim['id'] for row in rows)
    assert sum(int(row['remaining_quantity']) for row in rows if row['kind']=='owned_stock')==85
    print('PASS native gas title, bounded physical availability, first outcomes, semantic no-ops, historical prefixes and operator-to-title conservation',flush=True)

    agreement=evidence(owned,'loan_agreement',100,'2020-12-20T10:00:00Z')
    received=evidence(owned,'title_receipt',100,'2020-12-20T10:00:00Z')
    borrowing=dict(agreementId=agreement,evidenceId=received,quantity='100',titleAt='2020-12-20T10:00:00Z',availableAt='2020-12-20T10:00:00Z',
        returnAt='2020-12-21T12:00:00Z',returnMarkCurve='future',returnMarkContractId=contract,carrying='currency_curve_v1',feeAmount='-2',
        feeDueAt='2020-12-21T12:00:00Z',lenderId=party,lenderRevision=1)
    loan=apply(owned,'borrow',borrowing)
    assert apply(owned,'borrow',borrowing)['id']==loan['id']
    sale=evidence(owned,'physical_delivery',100,'2020-12-20T11:00:00Z')
    apply(owned,'dispatch',dict(lotId=loan['stockId'],evidenceId=sale,quantity='100',reservationId=None,effectiveAt='2020-12-20T11:00:00Z'))
    positions=read()['positions']
    assert not any(p['id']==loan['stockId'] for p in positions)
    assert next(p['remaining_quantity'] for p in positions if p['id']==loan['returnId'])=='-100'
    returned=evidence(owned,'loan_return',30,'2020-12-20T12:30:00Z')
    apply(owned,'repay',dict(loanId=loan['id'],lotId=stock['id'],evidenceId=returned,quantity='30',reservationId=None,effectiveAt='2020-12-20T12:30:00Z'))
    positions=read()['positions']
    assert next(p['remaining_quantity'] for p in positions if p['id']==loan['returnId'])=='-70'
    fee=next(f for f in read()['fees'] if f['loan_id']==loan['id'])
    expect_rolled_back_refusal(owned,'fee_payment',dict(feeId=fee['id'],businessKey='premature_cash',expectedRevision=0,
        paidAt='2020-12-20T09:00:00Z',amount='-0.1',evidence='Synthetic payment before this fee existed'),'gas_owned_payment_origin')
    paid=dict(feeId=fee['id'],businessKey='synthetic_cash',expectedRevision=0,paidAt='2020-12-20T14:00:00Z',amount='-1',evidence='Synthetic bank evidence')
    first=apply(owned,'fee_payment',paid)
    assert apply(owned,'fee_payment',{**paid,'expectedRevision':1})['id']==first['id']
    rejected(lambda:apply(owned,'fee_payment',{**paid,'businessKey':'overpayment','amount':'-2'}),'gas_owned_payment_capacity')
    corrected=apply(owned,'fee_payment',{**paid,'expectedRevision':1,'amount':'-0.5'})
    assert corrected['revision']==2
    acknowledged=apply(owned,'fee_payment',{**paid,'expectedRevision':2,'amount':'-1'})
    assert acknowledged['revision']==1 and acknowledged['currentRevision']==2 and acknowledged['currentAmount']=='-0.5'
    assert acknowledged['semanticAcknowledgement'] and not acknowledged['changed']
    assert query(f"SELECT count(*) FROM trading.gas_owned_fee_payments WHERE(catalog_id,fee_id)=('{catalog}','{fee['id']}');")=='2'
    unused_source='synthetic_revision_ack'
    first_evidence=evidence(owned,'title_receipt',1,source=unused_source)
    evidence(owned,'title_receipt',2,source=unused_source,revision=1)
    acknowledged=command(owned,'evidence',dict(sourceKey=unused_source,kind='title_receipt',quantity='1',occurredAt='2020-12-19T12:00:00Z',
        evidence='Synthetic accepted operational evidence',expectedRevision=2))
    assert acknowledged['id']==first_evidence and acknowledged['currentRevision']==2 and acknowledged['currentQuantity']=='2' and not acknowledged['changed']
    old_policy=policies[(owned,'reserve')]
    bind(owned,'reserve','gas.owned.reserve')
    acknowledged=command(owned,'bind_policy',dict(action='gas.owned.reserve',policyId=old_policy[0],policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))
    assert acknowledged['revision']==1 and acknowledged['currentRevision']==2 and acknowledged['currentPolicyId']==policies[(owned,'reserve')][0] and not acknowledged['changed']
    risk=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='risk';")
    grant=str(uuid4())
    query(f"INSERT INTO security.role_grants SELECT '{grant}','{risk}',role_id,tenant_id,workspace_id,granted_from,granted_to,revoked "
          "FROM security.role_grants WHERE principal_id=(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader') LIMIT 1;")
    try:
        before_ack=query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
        acknowledged=command(owned,'receive_title',body,policies[(owned,'receive_title')],profile='risk')
        assert acknowledged['id']==stock['id'] and not acknowledged['changed']
        assert query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")==before_ack
        rejected(lambda:command(owned,'receive_title',body,policies[(owned,'receive_title')],operation=operation,context=title_context,profile='risk'),'gas_owned_conflict')
        rejected(lambda:command(owned,'receive_title',{**body,'quantity':'99'},policies[(owned,'receive_title')],operation=operation,context=title_context,profile='risk'),'gas_owned_conflict')
    finally:
        query(f"DELETE FROM security.role_grants WHERE id='{grant}';")
    print('PASS title-transferring loan stock may be consumed while return liability and separate partial/corrected fee payments survive',flush=True)

    observation=str(uuid4())
    query("INSERT INTO market_data.observations SELECT catalog_id,"+f"'{observation}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,"
          f"'synthetic_cashout_{observation}','1','daily_settlement','final','2020-12-19T15:00:00Z',clock_timestamp(),1.005,NULL "
          f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','70000000-0000-0000-0000-000000000001');")
    source=query(f"SELECT source_code FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{observation}');")
    statement=evidence(operator,'operator_statement',3,'2020-12-19T12:00:00Z')
    cash_claim=apply(operator,'operator_claim',dict(evidenceId=statement,quantity='3',effectiveAt='2020-12-19T12:00:00Z',dueAt='2020-12-21T12:00:00Z',
        carrying='currency_curve_v1',settlementMode='cashout_final_price_v1',cashout=dict(source=source,role='daily_settlement',observedAt='2020-12-19T15:00:00Z',
        cashDueAt='2020-12-21T12:00:00Z',rounding='payment_total_half_even_2dp_v1')))
    cash_evidence=evidence(operator,'cashout_determination',3,'2020-12-20T13:00:00Z',source='synthetic_cashout_evidence')
    current_cash_evidence=evidence(operator,'cashout_determination',4,'2020-12-20T13:00:00Z',source='synthetic_cashout_evidence',revision=1)
    settle=dict(positionId=cash_claim['id'],quantity='1',ownedAccountId=None,lotId=None,reservationId=None,evidenceId=cash_evidence,
        effectiveAt='2020-12-20T13:00:00Z',availableAt=None,sourceObservationId=observation)
    expect_rolled_back_refusal(operator,'operator_settle',settle,'gas_owned_evidence')
    settle['evidenceId']=current_cash_evidence
    first_cash=apply(operator,'operator_settle',settle)
    second_cash=apply(operator,'operator_settle',{**settle,'quantity':'2'})
    from decimal import Decimal
    fees={f['id']:Decimal(f['amount']) for f in read()['fees']}
    assert fees[first_cash['cashId']]==Decimal('1.00') and fees[second_cash['cashId']]==Decimal('2.02')
    assert not any(p['id']==cash_claim['id'] for p in read()['positions'])
    assert fees[first_cash['cashId']]+fees[second_cash['cashId']]==Decimal('3.02')
    print('PASS cashout consumes exact pinned final source and cumulative half-even totals; cash survives the extinguished gas claim',flush=True)
    from concurrent.futures import ThreadPoolExecutor
    shared_context=capture()
    concurrent=dict(lotId=stock['id'],businessKey='concurrent_same_fact',quantity='1',effectiveAt=at)
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes=list(pool.map(lambda _:apply(owned,'reserve',concurrent,context=shared_context),range(2)))
    assert outcomes[0]['id']==outcomes[1]['id'] and sum(int(o['changed']) for o in outcomes)==1
    shared_context=capture()
    def race(key):
        try:
            return apply(owned,'reserve',dict(lotId=stock['id'],businessKey=key,quantity='8',effectiveAt=at),context=shared_context)
        except RuntimeError as error:
            assert 'gas_owned_context' in str(error),str(error)
            return None
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes=list(pool.map(race,['competing_a','competing_b']))
    assert sum(o is not None for o in outcomes)==1
    rejected(lambda:command(owned,'reserve',concurrent,policies[(owned,'reserve')],profile='viewer'),'gas_owned_denied')
    trader=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    query(f"UPDATE security.role_grants SET revoked=true WHERE principal_id='{trader}';")
    try:
        rejected(lambda:apply(owned,'receive_title',body,operation=operation,context=title_context),'gas_owned_denied')
    finally:
        query(f"UPDATE security.role_grants SET revoked=false WHERE principal_id='{trader}';")
    print('PASS concurrent gas same-fact commands converge; competing source heads, unauthorized callers and revoked receipt replay refuse',flush=True)

    import time
    protected_context=capture()
    protected=dict(lotId=stock['id'],businessKey='expired_authority',quantity='0.1',effectiveAt=at)
    before_guard=query(f"SELECT count(*) FROM trading.gas_owned_reservations WHERE catalog_id='{catalog}';")
    query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '0.5 seconds' WHERE principal_id='{trader}';")
    try:
        with ThreadPoolExecutor(max_workers=1) as pool:
            blocker=pool.submit(query,"SET application_name='gas_owned_authority_check';BEGIN;SELECT pg_advisory_xact_lock(730849102);SELECT pg_sleep(1.5);COMMIT;")
            deadline=time.monotonic()+3
            while query("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='gas_owned_authority_check' AND wait_event='PgSleep');")!='t':
                assert time.monotonic()<deadline
                time.sleep(.01)
            rejected(lambda:apply(owned,'reserve',protected,context=protected_context),'gas_owned_denied')
            blocker.result()
    finally:
        query(f"UPDATE security.role_grants SET granted_to='9999-12-31' WHERE principal_id='{trader}';")
    protected_context=capture()
    query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '0.5 seconds' WHERE principal_id='{trader}';")
    try:
        policy_id,policy_revision=policies[(owned,'reserve')]
        rejected(lambda:call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','reserve','{owned}',{literal(protected)},'{at}',"
                             f"'{protected_context}','{policy_id}',{policy_revision},false);SELECT pg_sleep(1);"),'gas_owned_denied')
    finally:
        query(f"UPDATE security.role_grants SET granted_to='9999-12-31' WHERE principal_id='{trader}';")
    assert query(f"SELECT count(*) FROM trading.gas_owned_reservations WHERE catalog_id='{catalog}';")==before_guard
    print('PASS gas command authority expires after publication-lock waits and immediately before native commit, leaving no partial financial effect',flush=True)
    assert not missing_refusals,'Native producers accepted unsupported facts (rolled back): '+','.join(missing_refusals)
    import os
    from pathlib import Path
    import subprocess
    mark=Decimal(query(f"SELECT value FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','70000000-0000-0000-0000-000000000001');"))
    multiplier=Decimal(query("SELECT q.multiplier FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)="
        f"(c.reference_catalog_id,c.reference_revision_id,c.quote_code) WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{manifest['revisionId']}','future');"))
    # A real zero-value trade in another book proves explicit trade-only scope.
    filter_trade=json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','d1000000-0000-0000-0000-000000000002',"
        f"'{manifest['revisionId']}','future','{contract}','2020-12-19T15:00:00Z',NULL,1,{mark},'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(trade_booking())})::text;"))
    margin_policy=dict(id=str(uuid4()),definitionRevisionId=manifest['revisionId'],contractId=contract,settlementCurve='future',
        referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],currency=terms['currency'],
        fromAt='2020-01-01T00:00:00Z',untilAt='2021-01-01T00:00:00Z',opening='include_opening_instant',increment='0.01',tie='to_even',
        stage='payment_total',roundingScope='held_binding_difference_payment_total_v1',implementation='dated_futures_margin_policy_v1',
        occurrences=[dict(id=str(uuid4()),at='2020-12-28T15:30:00Z',dueAt='2020-12-29T12:00:00Z',phase='final')])
    call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf=at,policy=margin_policy))});",'editor')
    adoption_request=dict(operationId=str(uuid4()),tradeId=filter_trade['tradeId'],bindingId=filter_trade['bindingId'],
        expectedRevision=1,policyId=margin_policy['id'],policyRevision=1,contextId=capture(),asOf=at,adoption='native_execution_inception_v1')
    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(adoption_request)});")
    rates=json.loads(query("SELECT jsonb_agg(o.id ORDER BY t.ordinal)::text FROM market_data.rate_curves c JOIN market_data.rate_curve_tenors t "
        "ON(t.catalog_id,t.revision_id,t.curve_code)=(c.catalog_id,c.revision_id,c.code) JOIN market_data.observations o "
        "ON(o.catalog_id,o.definition_revision_id,o.series_id,o.source_code)=(c.catalog_id,c.revision_id,t.series_id,c.source_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{manifest['revisionId']}','interest_rates') "
        "AND o.role='parameter' AND o.publication_state='final' AND o.observed_at='2020-12-18T16:00:00Z';"))
    tenor_count=int(query(f"SELECT count(*) FROM market_data.rate_curve_tenors WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{manifest['revisionId']}','interest_rates');"))
    assert tenor_count>0 and len(rates)==tenor_count
    zero_rates=[]
    for original in rates:
        corrected=str(uuid4())
        query("INSERT INTO market_data.observations SELECT catalog_id,"+f"'{corrected}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,"
              f"source_code,source_event_id,'gas_owned_zero_v1',role,publication_state,observed_at,clock_timestamp(),0,id "
              f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{original}');")
        zero_rates.append(corrected)
    rates=zero_rates
    host=query('SHOW unix_socket_directories;')
    path=Path(host)/'gas-owned-managed.json'
    path.write_text(json.dumps(dict(manifest=manifest,host=host,contextId=capture(),at=at,accountId=owned,lotId=stock['id'],
        policyId=policies[(owned,'reserve')][0],markId='70000000-0000-0000-0000-000000000001',contractId=contract,bookId=book,
        expectedPv=str(Decimal('-11.5')*mark*multiplier+Decimal('1.52')*Decimal('.95')),rateIds=rates,filterTradeId=filter_trade['tradeId'],
        expectedWholeBookPv=str(Decimal('-15')*mark*multiplier+Decimal('1.52')))))
    before_managed=query(f"SELECT count(*) FROM trading.gas_owned_reservations WHERE catalog_id='{catalog}';")
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--gas-owned',str(path)],
        cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if result.returncode:
        raise RuntimeError(result.stdout+result.stderr)
    assert int(query(f"SELECT count(*) FROM trading.gas_owned_reservations WHERE catalog_id='{catalog}';"))==int(before_managed)+1
    print(result.stdout.strip(),flush=True)

    def configure(identity, action, body, operation=None, context=None, receipt=False, rollback=False):
        return json.loads(call(f"SELECT security.configure_gas_owned_projection('{catalog}','{operation or uuid4()}','{action}','{identity}',"
            f"{literal(body)},'{at}','{context or capture()}',{'true' if receipt else 'false'})::text;",rollback=rollback))

    old_mark_context=capture()
    next_mark=query(f"SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{manifest['revisionId']}','future') AND contract_id<>'{contract}' ORDER BY ordinal LIMIT 1;")
    assert next_mark
    mark_binding=dict(positionId=stock['id'],definitionRevisionId=manifest['revisionId'],markCurve='future',
        markContractId=next_mark,expectedRevision=0,effectiveAt='2020-12-20T16:00:00Z')
    mark_operation=str(uuid4());mark_context=capture()
    rebound=configure(owned,'mark_binding',mark_binding,mark_operation,mark_context)
    assert configure(owned,'mark_binding',mark_binding,mark_operation,mark_context)==rebound
    assert next(p for p in read()['positions'] if p['id']==stock['id'])['mark_contract_id']==mark_binding['markContractId']
    assert next(p for p in read(old_mark_context)['positions'] if p['id']==stock['id'])['mark_contract_id']==contract
    rejected(lambda:configure(owned,'mark_binding',{**mark_binding,'expectedRevision':1,'markContractId':contract},context=old_mark_context,rollback=True),'gas_owned_context')
    rejected(lambda:apply(owned,'reserve',dict(lotId=stock['id'],businessKey='stale_after_mark',quantity='1',effectiveAt=at),context=old_mark_context,rollback=True),'gas_owned_context')
    second=configure(owned,'mark_binding',{**mark_binding,'expectedRevision':1,'markContractId':contract})
    assert second['currentRevision']==2
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    older=configure(owned,'mark_binding',mark_binding)
    assert older['acknowledgedRevision']==1 and older['currentRevision']==2 and not older['changed']
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    assert next(p for p in read()['positions'] if p['id']==stock['id'])['mark_contract_id']==contract
    profile_body=dict(profileId=str(uuid4()),expectedRevision=0,implementation='gas_quantity_lifecycle_projection_v1',
        quantityDisposition='retain_quantity_obligations_v1',effectiveAt='2020-12-20T16:00:00Z',performances=[])
    before_profile=capture()
    saved_profile=configure(owned,'projection_profile',profile_body)
    rejected(lambda:configure(owned,'projection_profile',{**profile_body,'expectedRevision':1,'effectiveAt':'2020-12-20T15:59:00Z'},context=before_profile,rollback=True),'gas_owned_context')
    rejected(lambda:apply(owned,'reserve',dict(lotId=stock['id'],businessKey='stale_after_profile',quantity='1',effectiveAt=at),context=before_profile,rollback=True),'gas_owned_context')
    assert configure(owned,'projection_profile',profile_body)['acknowledgedRevision']==1
    profile_context=capture()
    retained_profile=json.loads(call(f"SELECT security.use_market_context('{catalog}','{profile_context}');SELECT security.use_trading_context('{catalog}','{at}');"
        f"SELECT security.read_gas_owned_projection_profile('{catalog}','{profile_body['profileId']}',1,'{profile_context}','{at}')::text;"))
    assert retained_profile['profile']['account_id']==owned and retained_profile['performances']==[] and saved_profile['changed']
    # A known future loss after this eight-hour horizon is retained evidence,
    # not a reason to reject the earlier projection or consume stock early.
    later_at='2020-12-23T16:00:00Z'
    def later_command(action, body, policy=None):
        selected=call(f"SELECT security.capture_date_context('{catalog}','2020-12-23',NULL);")
        policy_id,policy_revision=policy or (None,None)
        policy_sql="NULL" if policy_id is None else "'"+policy_id+"'"
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{owned}',{literal(body)},'{later_at}','{selected}',{policy_sql},{policy_revision or 'NULL'},false)::text;"))
    future_loss_lot=next(p['id'] for p in read()['positions'] if p['kind']=='owned_stock' and p['origin_policy']=='operator_balance_transfer_v1')
    future_loss=later_command('evidence',dict(sourceKey='synthetic_future_loss',kind='physical_loss',quantity='2',occurredAt='2020-12-22T12:00:00Z',
        evidence='Synthetic accepted future-effective loss',expectedRevision=0))
    later_command('loss',dict(lotId=future_loss_lot,evidenceId=future_loss['id'],quantity='2',reservationId=None,effectiveAt='2020-12-22T12:00:00Z'),policies[(owned,'loss')])
    from close_session_fixture import sql_session
    sql_session(query,manifest,'synthetic_gas_economic','test_business','16:00',['future'],[])
    # Independently authored synthetic native history; every date has one
    # complete correction head in the same declared series/source/role.
    for date, value in [('2020-12-15',mark-Decimal('0.1')),('2020-12-16',mark+Decimal('0.1'))]:
        observed=date+'T16:00:00Z'
        heads=json.loads(query(f"SELECT coalesce(jsonb_agg(o.id),'[]')::text FROM market_data.observations o JOIN market_data.observations anchor "
            "ON(anchor.catalog_id,anchor.id)=(o.catalog_id,'70000000-0000-0000-0000-000000000001'::uuid) "
            f"WHERE o.catalog_id='{catalog}' AND(o.definition_revision_id,o.series_id,o.source_code,o.role)=(anchor.definition_revision_id,anchor.series_id,anchor.source_code,anchor.role) "
            f"AND o.observed_at='{observed}' AND NOT EXISTS(SELECT 1 FROM market_data.observations n WHERE(n.catalog_id,n.corrects_id)=(o.catalog_id,o.id));"))
        assert len(heads)<=1
        sample_id=str(uuid4());source_row=heads[0] if heads else '70000000-0000-0000-0000-000000000001'
        source_event='source_event_id' if heads else "'synthetic_gas_history_"+date+"'"
        correction="'"+heads[0]+"'::uuid" if heads else 'NULL::uuid'
        query(f"INSERT INTO market_data.observations SELECT catalog_id,'{sample_id}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,"
            f"source_code,{source_event},'gas_economic_history_v1',role,publication_state,'{observed}',clock_timestamp(),{value},{correction} "
            f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{source_row}');")
    historical_tail=Decimal('3')*multiplier
    profile_context=capture()
    path.write_text(json.dumps(dict(manifest=manifest,host=host,contextId=profile_context,at=at,accountId=owned,
        profileId=profile_body['profileId'],bookId=book,markId='70000000-0000-0000-0000-000000000001',contractId=contract,rateIds=rates,
        economicSession='synthetic_gas_economic',expectedHistoricalVar=str(historical_tail))))
    profile_result=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--gas-owned-profile',str(path)],
        cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if profile_result.returncode:raise RuntimeError(profile_result.stdout+profile_result.stderr)
    print(profile_result.stdout.strip(),flush=True)
    if economic_worker is not None:economic_worker(query,manifest,path)

    # At the historical opening this separate book has no owned balances. The
    # source cutoff nevertheless knows a future title; risk must detect it and
    # retain the journal without activating that inventory at opening.
    future_book='d1000000-0000-0000-0000-000000000002'
    future_owned=account('owned_inventory',future_book,'2020-12-20T00:00:00Z');bind(future_owned,'receive_title','gas.owned.receive_title')
    future_evidence=evidence(future_owned,'title_receipt',7,'2020-12-20T12:00:00Z')
    future_stock=apply(future_owned,'receive_title',dict(evidenceId=future_evidence,quantity='7',titleAt='2020-12-20T12:00:00Z',
        availableAt='2020-12-20T13:00:00Z',originPolicy='opening_owned_stock_v1',carrying='nominal_mark_v1'))
    future_profile={**profile_body,'profileId':str(uuid4()),'effectiveAt':'2020-12-18T00:00:00Z'}
    configure(future_owned,'projection_profile',future_profile)
    opening='2020-12-19T16:00:00Z'
    known_context=call(f"SELECT security.capture_date_context('{catalog}','2020-12-19',NULL);")
    prefix=f"SELECT security.use_market_context('{catalog}','{known_context}');SELECT security.use_trading_context('{catalog}','{opening}');"
    opening_holdings=json.loads(call(prefix+f"SELECT security.read_gas_owned_holdings('{catalog}','{known_context}','{opening}',ARRAY['{future_book}']::uuid[])::text;"))
    assert opening_holdings['positions']==[]
    rejected(lambda:call(prefix+f"SELECT security.require_gas_owned_projection_scope('{catalog}','{known_context}','{opening}',ARRAY['{future_book}']::uuid[],false,clock_timestamp()+interval '30 seconds');"),'gas_owned_projection_policy_required')
    known=json.loads(call(prefix+f"SELECT security.read_gas_owned_known_history('{catalog}','{known_context}','{opening}',ARRAY['{future_book}']::uuid[])::text;"))
    assert known['asOf'].startswith('2020-12-19T16:00:00') and any(p['id']==future_stock['id'] and p['effective_at'].startswith('2020-12-20T12:00:00') for p in known['positions'])
    path.write_text(json.dumps(dict(manifest=manifest,host=host,contextId=known_context,at=opening,accountId=future_owned,
        profileId=future_profile['profileId'],positionId=future_stock['id'],bookId=future_book,markId='70000000-0000-0000-0000-000000000001',contractId=contract)))
    known_result=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--gas-owned-known',str(path)],
        cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if known_result.returncode:raise RuntimeError(known_result.stdout+known_result.stderr)
    print(known_result.stdout.strip(),flush=True)
    print('PASS gas retained mark revisions, first outcomes, older fact acknowledgement, saved profiles and knowledge-only future ownership scope',flush=True)


if __name__=='__main__':
    main(checks=checks)
