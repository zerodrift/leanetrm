"""Gas title, operator claims and surviving commodity loans in owned PostgreSQL."""
import json
from uuid import uuid4

from check_reference_editing import main
from preview_fixture import catalogue_fixture, trading_fixture


def checks(query, manifest):
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    query(trading_fixture(catalog))
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest, 'https://preview.invalid'))
    query("INSERT INTO security.role_permissions SELECT role_id,p.code FROM security.role_permissions r "
          "CROSS JOIN security.permissions p WHERE r.permission='AmendTrade' AND p.code IN"
          "('ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings') ON CONFLICT DO NOTHING;")

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
    def account(kind):
        identity = str(uuid4())
        command(identity,'account',dict(code='synthetic_'+identity.replace('-',''),kind=kind,bookId=book,legalEntityId=entity,
            legalEntityRevision=1,counterpartyId=party if kind=='operator_balance' else None,counterpartyRevision=1 if kind=='operator_balance' else None,
            referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],
            definitionRevisionId=manifest['revisionId'],markCurve='future',markContractId=contract,
            titlePolicy='explicit_accepted_title_v1',lossPolicy='separate_evidenced_loss_v1',effectiveAt='2020-12-18T00:00:00Z',**terms))
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

    def evidence(identity, kind, qty, when='2020-12-19T12:00:00Z', source=None, revision=0):
        return command(identity,'evidence',dict(sourceKey=source or str(uuid4()),kind=kind,quantity=str(qty),occurredAt=when,
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
    host=query('SHOW unix_socket_directories;')
    path=Path(host)/'gas-owned-managed.json'
    path.write_text(json.dumps(dict(manifest=manifest,host=host,contextId=capture(),at=at,accountId=owned,lotId=stock['id'],
        policyId=policies[(owned,'reserve')][0],markId='70000000-0000-0000-0000-000000000001',contractId=contract,bookId=book,
        expectedPv=str(Decimal('-11.5')*mark*multiplier+Decimal('1.52')*Decimal('.95')))))
    before_managed=query(f"SELECT count(*) FROM trading.gas_owned_reservations WHERE catalog_id='{catalog}';")
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--gas-owned',str(path)],
        cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if result.returncode:
        raise RuntimeError(result.stdout+result.stderr)
    assert int(query(f"SELECT count(*) FROM trading.gas_owned_reservations WHERE catalog_id='{catalog}';"))==int(before_managed)+1
    print(result.stdout.strip(),flush=True)


if __name__=='__main__':
    main(checks=checks)
