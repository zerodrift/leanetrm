"""Recorded operator entitlement and settlement counterparts on owned PostgreSQL."""
import argparse
import json
from decimal import Decimal
import os
from pathlib import Path
import subprocess
from uuid import uuid4

from check_reference_editing import main
from check_gas_owned_holdings import checks as owned_checks


def checks(query,manifest,after_writer=None,*,after_counterparts=None,baseline_already_run=False):
    # Preserve the original title, loan, cashout and ordinary valuation oracles
    # before introducing any correction to their source-known counterparts.
    if not baseline_already_run:
        owned_checks(query,manifest,form_source_counterexamples=False)
    catalog,tenant,workspace=(manifest[key] for key in ('catalogId','tenantId','workspaceId'))
    accounts=json.loads(query("SELECT jsonb_agg(to_jsonb(a))::text FROM trading.gas_owned_accounts a "
        f"WHERE(a.catalog_id,a.code)=('{catalog}','synthetic_operator_balance_2');"))
    assert len(accounts)==1
    account=accounts[0]
    claims=json.loads(query("SELECT jsonb_agg(to_jsonb(p))::text FROM trading.gas_owned_positions p "
        f"WHERE(p.catalog_id,p.account_id,p.kind,p.settlement_mode)=('{catalog}','{account['id']}','operator_claim','cashout_final_price_v1');"))
    assert len(claims)==1 and Decimal(str(claims[0]['quantity']))==3
    claim=claims[0]
    original=json.loads(query("SELECT jsonb_agg(to_jsonb(s) ORDER BY s.publication_order)::text FROM trading.gas_owned_claim_settlements s "
        f"WHERE(s.catalog_id,s.position_id)=('{catalog}','{claim['id']}');"))
    assert [Decimal(str(row['quantity'])) for row in original]==[Decimal(1),Decimal(2)]

    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    at='2020-12-20T16:00:00Z'
    def capture(day='2020-12-20'):return call(f"SELECT security.resolve_operation_context('{catalog}','{day}',NULL);")
    def read(position,context):return json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');"
        f"SELECT security.use_trading_context('{catalog}','{at}','2020-12-20'::date);"
        f"SELECT security.read_gas_operator_quantity_reconciliation('{catalog}','{position}','{context}','{at}')::text;"))
    context=capture();source=read(claim['id'],context)
    assert source['implementation']=='gas_operator_quantity_reconciliation_source_v1'
    assert source['contextId']==context and source['claim']['id']==claim['id']
    assert source['claim']['remainingQuantity']=='0'
    assert source['cashoutTerms']['position_id']==claim['id'] and source['cashoutTerms']['rounding']=='payment_total_half_even_2dp_v1'
    assert [row['id'] for row in source['settlements']]==[row['id'] for row in original]
    assert [Decimal(row['quantity']) for row in source['settlements']]==[Decimal(1),Decimal(2)]
    assert sorted(Decimal(row['amount']) for row in source['cash'])==[Decimal('1.00'),Decimal('2.02')]
    assert {row['sourceObservationId'] for row in source['settlements']}=={row['source_observation_id'] for row in original}
    print('PASS operator correction source: fully settled claim retains original ordered settlements, cumulative rounded cash and exact accepted source',flush=True)

    def record(action,body,policy_action=None,target=account['id']):
        selected=capture('2020-12-22')
        policy=None if policy_action is None else json.loads(query("SELECT jsonb_build_object('id',policy_id,'revision',policy_revision)::text "
            f"FROM trading.gas_owned_action_bindings WHERE(catalog_id,account_id,action,revision)=('{catalog}','{target}','{policy_action}',1);"))
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{target}',{literal(body)},"
            f"'2020-12-22T16:00:00Z','{selected}',"+(f"'{policy['id']}',{policy['revision']}" if policy else 'NULL,NULL')+",false)::text;"))
    def evidence(key,kind,quantity,when,target=account['id']):return record('evidence',dict(sourceKey=key,kind=kind,quantity=quantity,occurredAt=when,
        evidence='Synthetic independently accepted operator source',expectedRevision=0),target=target)['id']
    statement=evidence('synthetic_operator_known_future_statement','operator_statement','3','2020-12-19T12:00:00Z')
    terms=json.loads(query(f"SELECT to_jsonb(t) FROM trading.gas_owned_cashout_terms t WHERE(catalog_id,position_id)=('{catalog}','{claim['id']}');"))
    future_claim=record('operator_claim',dict(evidenceId=statement,quantity='3',effectiveAt='2020-12-19T12:00:00Z',dueAt='2020-12-24T12:00:00Z',
        carrying='currency_curve_v1',settlementMode='cashout_final_price_v1',cashout=dict(source=terms['source_code'],role=terms['role'],
        observedAt='2020-12-19T15:00:00Z',cashDueAt='2020-12-24T12:00:00Z',rounding=terms['rounding'])),'gas.operator.record')
    future_settlements=[]
    for suffix,quantity,when in [('current','1','2020-12-20T13:00:00Z'),('future','2','2020-12-21T13:00:00Z')]:
        document=evidence('synthetic_operator_known_'+suffix,'cashout_determination',quantity,when)
        accepted=record('operator_settle',dict(positionId=future_claim['id'],quantity=quantity,ownedAccountId=None,lotId=None,reservationId=None,
            evidenceId=document,effectiveAt=when,availableAt=None,sourceObservationId=original[0]['source_observation_id']),'gas.operator.settle')
        future_settlements.append(accepted['id'])
    known=read(future_claim['id'],capture())
    assert known['claim']['remainingQuantity']=='2'
    assert [row['id'] for row in known['settlements']]==future_settlements
    assert known['settlements'][1]['effectiveAt'].startswith('2020-12-21T13:00:00')
    assert sum(Decimal(row['amount']) for row in known['cash'])==Decimal('3.02')
    historical=read(claim['id'],context)
    assert [row['id'] for row in historical['settlements']]==[row['id'] for row in original]
    try:read(future_claim['id'],context)
    except RuntimeError as error:assert 'gas_operator_quantity_source' in str(error),str(error)
    else:raise AssertionError('A claim outside the retained knowledge prefix became a successful current-source read.')
    print('PASS operator source clocks: future-effective accepted settlement and cash remain known; original effective dates and earlier captured prefix stay exact',flush=True)

    positive=json.loads(query("SELECT to_jsonb(p) FROM trading.gas_owned_positions p "
        f"WHERE(p.catalog_id,p.account_id,p.kind,p.settlement_mode)=('{catalog}','{account['id']}','operator_claim','return_quantity_v1');"))
    title=read(positive['id'],capture())
    assert title['claim']['remainingQuantity']=='0' and len(title['settlements'])==1
    receipt=title['settlements'][0]['receiptLotId']
    assert receipt and title['settlements'][0]['consumptionId'] is None
    assert any(row['id']==receipt and Decimal(row['quantity'])==10 for row in title['positions'])
    future_uses=json.loads(query("SELECT jsonb_agg(to_jsonb(c)) FROM trading.gas_owned_consumptions c "
        "JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(c.catalog_id,c.evidence_id) "
        f"WHERE(c.catalog_id,c.lot_id,c.kind,e.source_key)=('{catalog}','{receipt}','loss','synthetic_future_loss');"))
    assert len(future_uses)==1 and Decimal(str(future_uses[0]['quantity']))==2
    assert future_uses[0]['effective_at'].startswith('2020-12-22T12:00:00')
    assert any(row['id']==future_uses[0]['id'] and row['lotId']==receipt and row['kind']=='loss' and Decimal(row['quantity'])==2 and
        row['effectiveAt']==future_uses[0]['effective_at'] for row in title['consumptions'])
    assert title['cash']==[]

    owned=json.loads(query("SELECT to_jsonb(a) FROM trading.gas_owned_accounts a "
        f"WHERE(a.catalog_id,a.code)=('{catalog}','synthetic_owned_inventory_1');"))
    receipt_document=evidence('synthetic_operator_debt_stock','title_receipt','3','2020-12-19T12:00:00Z',owned['id'])
    stock=record('receive_title',dict(evidenceId=receipt_document,quantity='3',titleAt='2020-12-19T12:00:00Z',availableAt='2020-12-19T12:00:00Z',
        originPolicy='opening_owned_stock_v1',carrying='nominal_mark_v1'),'gas.owned.receive_title',owned['id'])
    debt_document=evidence('synthetic_operator_debt_statement','operator_statement','-3','2020-12-19T12:00:00Z')
    debt=record('operator_claim',dict(evidenceId=debt_document,quantity='-3',effectiveAt='2020-12-19T12:00:00Z',dueAt='2020-12-24T12:00:00Z',
        carrying='nominal_mark_v1',settlementMode='return_quantity_v1',cashout=None),'gas.operator.record')
    delivery_document=evidence('synthetic_operator_debt_delivery','physical_delivery','-2','2020-12-20T13:00:00Z',owned['id'])
    delivered=record('operator_settle',dict(positionId=debt['id'],quantity='2',ownedAccountId=owned['id'],lotId=stock['id'],reservationId=None,
        evidenceId=delivery_document,effectiveAt='2020-12-20T13:00:00Z',availableAt=None,sourceObservationId=None),'gas.operator.settle')
    debt_source=read(debt['id'],capture())
    assert debt_source['claim']['remainingQuantity']=='-1' and len(debt_source['settlements'])==1
    selected=debt_source['settlements'][0]
    assert selected['id']==delivered['id'] and selected['receiptLotId'] is None and selected['consumptionId']
    assert any(row['id']==selected['consumptionId'] and row['lotId']==stock['id'] and Decimal(row['quantity'])==2 for row in debt_source['consumptions'])
    assert any(row['id']==stock['id'] and Decimal(row['quantity'])==3 for row in debt_source['positions'])
    assert debt_source['cash']==[]
    print('PASS operator quantity counterparts: positive title retains exact receipt and future known use; negative debt retains exact signed-source delivery and funding lot without invented cash',flush=True)
    assert all(row.get('editable') is False for row in debt_source['consumptions'] if row['id']==selected['consumptionId']), \
        'The linked operator debt use must remain review-only in ordinary-use choices.'
    assert next(row for row in title['consumptions'] if row['id']==future_uses[0]['id']).get('editable') is False, \
        'A known future use is retained for capacity but is not yet an ordinary editable choice.'
    later_context=capture('2020-12-23');later_at='2020-12-23T16:00:00Z'
    later_title=json.loads(call(f"SELECT security.use_market_context('{catalog}','{later_context}');SELECT security.use_trading_context('{catalog}','{later_at}','2020-12-23'::date);"
        f"SELECT security.read_gas_operator_quantity_reconciliation('{catalog}','{positive['id']}','{later_context}','{later_at}')::text;"))
    assert {row['id'] for row in later_title['consumptions']}=={row['id'] for row in title['consumptions']}
    assert next(row for row in later_title['consumptions'] if row['id']==future_uses[0]['id']).get('editable') is True, \
        'The exact now-effective independent loss must become an editable ordinary choice.'
    print('PASS operator use choices: exact native ordinary eligibility at the selected clock; future/linked facts retained for review and capacity',flush=True)
    from gas_operator_source_authority import checks as source_authority
    source_authority(query,call,read,manifest,claim['id'],capture())
    # Cashout documents allocate quantity through settlements, whereas physical
    # title/return documents already allocate through their receipt/consumption.
    # The source contract must disclose both without counting a physical pair twice.
    allocations=read(claim['id'],capture())['allocations']
    allocated={row['id'] for row in allocations if row['allocation_kind']=='cashout_settlement'}
    assert allocated=={row['id'] for row in original},'The correction source omitted cashout allocations of its raw determination evidence.'
    assert all(row['allocation_kind']!='cashout_settlement' for row in title['allocations'])
    other_statement=evidence('synthetic_operator_shared_statement','operator_statement','1','2020-12-19T12:00:00Z')
    other_claim=record('operator_claim',dict(evidenceId=other_statement,quantity='1',effectiveAt='2020-12-19T12:00:00Z',dueAt='2020-12-24T12:00:00Z',
        carrying='currency_curve_v1',settlementMode='cashout_final_price_v1',cashout=dict(source=terms['source_code'],role=terms['role'],
        observedAt='2020-12-19T15:00:00Z',cashDueAt='2020-12-24T12:00:00Z',rounding=terms['rounding'])),'gas.operator.record')
    assert original[0]['evidence_id']==original[1]['evidence_id']
    other_settlement=record('operator_settle',dict(positionId=other_claim['id'],quantity='1',ownedAccountId=None,lotId=None,reservationId=None,
        evidenceId=original[0]['evidence_id'],effectiveAt='2020-12-20T13:00:00Z',availableAt=None,
        sourceObservationId=original[0]['source_observation_id']),'gas.operator.settle')
    shared=read(claim['id'],capture())
    assert {row['id'] for row in shared['allocations'] if row['allocation_kind']=='cashout_settlement'}==allocated|{other_settlement['id']}
    assert [row['id'] for row in shared['settlements']]==[row['id'] for row in original] and len(shared['cash'])==2
    print('PASS operator raw-source allocation scope: all shared cashout settlements disclosed; title/consumption counterpart counted once and unrelated cash excluded',flush=True)
    policy=str(uuid4())
    call(f"SELECT lifecycle.save_policy('{catalog}',"+literal(dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,
        action='gas.operator.reconcile_quantity',mode='manual',fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',
        implementation='explicit_lifecycle_action_policy_v1'))+");")
    record('bind_policy',dict(action='gas.operator.reconcile_quantity',policyId=policy,policyRevision=1,effectiveAt='2020-12-20T15:59:00Z'))
    preserve='preserve_unallocated_source_quantity_v1'
    def member(row,quantity):return dict(settlementId=row['id'],expectedRevision=1,quantity=quantity,evidenceId=row['evidence_id'],
        sourceObservationId=row['source_observation_id'],expectedReceiptRevision=None,expectedConsumptionRevision=None,
        lotId=None,reservationId=None,sourceAllocation=preserve)
    body=dict(implementation='gas_operator_quantity_reconciliation_v1',rootClaimId=claim['id'],claims=[
        dict(claimId=claim['id'],expectedRevision=1,quantity='3',evidenceId=claim['evidence_id'],sourceAllocation=preserve,
            settlements=[member(original[0],'0.75'),member(original[1],'2.25')]),
        dict(claimId=other_claim['id'],expectedRevision=1,quantity='1',evidenceId=other_statement,sourceAllocation=preserve,
            settlements=[dict(settlementId=other_settlement['id'],expectedRevision=1,quantity='1',evidenceId=original[0]['evidence_id'],
                sourceObservationId=original[0]['source_observation_id'],expectedReceiptRevision=None,expectedConsumptionRevision=None,
                lotId=None,reservationId=None,sourceAllocation=preserve)])],consumptions=[],
        cashout=dict(ordering='replay_original_settlement_publication_prefix_v1',adjustmentDueAt='2020-12-25T12:00:00Z'),
        evidence='Synthetic explicit shared operator allocation and retained original publication order')
    current=capture()
    original_cash=query("SELECT jsonb_agg(to_jsonb(f) ORDER BY f.id) FROM trading.gas_owned_fees f "
        f"WHERE f.catalog_id='{catalog}' AND f.position_id IN('{claim['id']}','{other_claim['id']}');")
    bank=query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.id),'[]')::text) FROM trading.gas_owned_fee_payments p WHERE p.catalog_id='{catalog}';")
    original_members=query("SELECT jsonb_agg(to_jsonb(s) ORDER BY s.id) FROM trading.gas_owned_claim_settlements s "
        f"WHERE s.catalog_id='{catalog}' AND s.position_id IN('{claim['id']}','{other_claim['id']}');")
    publication=int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';"))
    operation=str(uuid4())
    def amend(selected_body,selected_operation,selected_context,receipt_only=False):
        return json.loads(call(f"SELECT security.reconcile_gas_operator_quantities('{catalog}','{selected_operation}','{account['id']}',{literal(selected_body)},'{at}','{selected_context}','{policy}',1,{'true' if receipt_only else 'false'})::text;"))
    outcome=amend(body,operation,current)
    assert outcome['operationId']==operation and outcome['rootClaimId']==claim['id'] and outcome['changed'] and not outcome['semanticAcknowledgement']
    assert {row['claimId'] for row in outcome['claims']}=={claim['id'],other_claim['id']}
    assert sorted(Decimal(row['amount']) for row in outcome['cashChanges'])==[Decimal('-.25'),Decimal('.25')]
    heads=json.loads(query("SELECT jsonb_agg(to_jsonb(q) ORDER BY q.settlement_id) FROM(SELECT DISTINCT ON(settlement_id)* "
        f"FROM trading.gas_operator_settlement_quantities WHERE catalog_id='{catalog}' AND settlement_id IN('{original[0]['id']}','{original[1]['id']}','{other_settlement['id']}') "
        "ORDER BY settlement_id,revision DESC)q;"))
    versions={row['settlement_id']:row for row in heads}
    assert versions[original[0]['id']]['revision']==versions[original[1]['id']]['revision']==2
    assert Decimal(str(versions[original[0]['id']]['quantity']))==Decimal('.75') and Decimal(str(versions[original[0]['id']]['cash_amount']))==Decimal('.75')
    assert Decimal(str(versions[original[1]['id']]['quantity']))==Decimal('2.25') and Decimal(str(versions[original[1]['id']]['cash_amount']))==Decimal('2.27')
    assert versions[other_settlement['id']]['revision']==1 and Decimal(str(versions[other_settlement['id']]['cash_amount']))==Decimal('1')
    adjustments=json.loads(query(f"SELECT jsonb_agg(to_jsonb(f)) FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.publication_order)=('{catalog}',{publication+1});"))
    assert len(adjustments)==2 and sorted(Decimal(str(row['amount'])) for row in adjustments)==[Decimal('-.25'),Decimal('.25')]
    assert all(row['position_id']==claim['id'] and row['loan_id'] is None and row['due_at'].startswith('2020-12-25T12:00:00') and
        row['effective_at'].startswith('2020-12-20T13:00:00') for row in adjustments)
    original_ids=[row['cash_id'] for row in original]+[other_settlement['cashId']]
    assert query("SELECT jsonb_agg(to_jsonb(f) ORDER BY f.id) FROM trading.gas_owned_fees f "
        f"WHERE f.catalog_id='{catalog}' AND f.id IN({','.join(repr(value) for value in original_ids)});")==original_cash
    assert query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.id),'[]')::text) FROM trading.gas_owned_fee_payments p WHERE p.catalog_id='{catalog}';")==bank
    assert query("SELECT jsonb_agg(to_jsonb(s) ORDER BY s.id) FROM trading.gas_owned_claim_settlements s "
        f"WHERE s.catalog_id='{catalog}' AND s.position_id IN('{claim['id']}','{other_claim['id']}');")==original_members
    assert int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';"))==publication+1
    print('PASS operator cashout correction: explicit .75/2.25 original-publication prefix gives .75/2.27, signed dated -.25/+.25 only; unchanged peer, original cash, bank and native settlement identities',flush=True)
    packet=dict(accountId=account['id'],policyId=policy,policyRevision=1,at=at,rootClaimId=claim['id'],otherClaimId=other_claim['id'],
        body=body,operationId=operation,contextId=current,outcome=outcome,originalObservationId=original[0]['source_observation_id'],cashoutTerms=terms,
        counterpartCases=[dict(claimId=positive['id']),dict(claimId=debt['id'])])
    from gas_operator_cashout_cases import checks as cashout_checks
    packet['cashoutCases']=cashout_checks(query,manifest,call,capture,read,record,amend,packet)
    # An acknowledged cash fact cannot silently discard an unsupported stock
    # instruction. This is a real COMMIT attempt against an existing cash fact.
    unsupported=json.loads(json.dumps(body))
    use=title['consumptions'][0]
    unsupported['consumptions']=[dict(consumptionId=use['id'],expectedRevision=use['expectedRevision'],lotId=use['lotId'],quantity=use['quantity'],
        retracted=False,evidenceId=use['evidenceId'],reservationId=use['reservation_id'],sourceAllocation=preserve)]
    for selection in [unsupported,{**unsupported,'consumptions':[{**unsupported['consumptions'][0],'consumptionId':str(uuid4())}]}]:
        try:amend(selection,str(uuid4()),capture())
        except RuntimeError as error:assert 'gas_operator_quantity_closure' in str(error),str(error)
        else:raise AssertionError('The bounded cashout writer silently acknowledged and discarded an unsupported stock-use selection.')
    print('PASS bounded operator cashout admission: existing cash facts cannot acknowledge and discard selected ordinary stock uses or unknown use IDs',flush=True)
    if after_writer is not None:
        after_writer(query,manifest,call,capture,read,record,amend,packet)
    from gas_operator_remaining_proofs import checks as remaining_proofs
    remaining_proofs(query,manifest,call,capture,read,record,amend,packet)
    from gas_operator_seal_cases import checks as seal_checks
    seal_checks(query,manifest,call,capture,read,record,amend,packet)
    from gas_operator_counterpart_cases import checks as counterpart_checks
    packet['counterpartCases']=counterpart_checks(query,manifest,call,capture,read,record,amend,packet)
    if after_counterparts is not None:
        after_counterparts(query,manifest,call,capture,read,record,amend,packet)
    from gas_operator_ordinary_use_cases import checks as ordinary_checks
    ordinary_checks(query,manifest,call,capture,read,record,amend,packet)
    from gas_operator_native_continuation_cases import checks as native_continuation
    native_continuation(query,manifest,call,capture,read,record,amend,packet)
    return packet


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin',type=Path,required=True)
    parser.add_argument('--no-build',action='store_true',help='Use already built API checks for an immutable qualification run.')
    arguments=parser.parse_args()
    root=Path(__file__).resolve().parents[1]
    if not arguments.no_build:
        subprocess.run([os.environ.get('DOTNET','dotnet'),'build','tests/OpenEtrm.Api.Checks','--no-restore','--disable-build-servers',
            '-m:1','-p:UseSharedCompilation=false','-nodeReuse:false'],cwd=root,check=True,timeout=120)
    main(checks=checks,arguments=['--pg-bin',str(arguments.pg_bin)])
