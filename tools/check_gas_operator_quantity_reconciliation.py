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


def checks(query,manifest):
    # Preserve the original title, loan, cashout and ordinary valuation oracles
    # before introducing any correction to their source-known counterparts.
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
    def capture(day='2020-12-20'):return call(f"SELECT security.capture_date_context('{catalog}','{day}',NULL);")
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
    from gas_operator_source_authority import checks as source_authority
    source_authority(query,call,read,manifest,claim['id'],capture())


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
