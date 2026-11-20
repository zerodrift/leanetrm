"""Upgrade accepted operator settlements without rewriting their financial history.

This native 0309 probe stays before the runtime reset boundary. The separate
current-schema qualifier retains the complete managed and economic baseline.
"""
from datetime import datetime
from decimal import Decimal, ROUND_HALF_EVEN, localcontext
import json
from pathlib import Path
from uuid import uuid4

from check_gas_fee_existing_origins import _state as gas_state, _tables as gas_tables
from check_reference_editing import main as native_main
from preview_fixture import fixture as synthetic_fixture, trading_fixture, catalogue_fixture, trade_booking
from seed_fixture_market import load_fixture


def _literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"


def _operations(query,manifest,at='2020-12-20T16:00:00Z'):
    catalog,tenant,workspace=(manifest[key] for key in ('catalogId','tenantId','workspaceId'))
    def call(statement):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{statement}COMMIT;")
    def record(account,action,body,policy=None,revision=None):
        context=call(f"SELECT security.capture_date_context('{catalog}','{at[:10]}',NULL);")
        operation=str(uuid4())
        result=json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{operation}','{action}','{account}',{_literal(body)},'{at}','{context}',"
            +(f"'{policy}',{revision}" if policy else 'NULL,NULL')+",false)::text;"))
        return operation,result
    return call,record


def _seed_base(query,manifest):
    catalog,tenant,workspace=(manifest[key] for key in ('catalogId','tenantId','workspaceId'))
    query(trading_fixture(catalog))
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest,manifest['identityIssuer']))
    query("INSERT INTO security.role_permissions SELECT role_id,p FROM security.role_permissions CROSS JOIN "
        "unnest(ARRAY['ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings'])p WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
    parties=trade_booking();call,record=_operations(query,manifest)
    terms=json.loads(query("SELECT jsonb_build_object('quantity',q.quantity_code,'currency',q.currency_code,'location',c.location_code) "
        "FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)="
        "(c.reference_catalog_id,c.reference_revision_id,c.quote_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{manifest['revisionId']}','future');"))
    accounts={};policies={}
    for kind,code,actions in [('owned_inventory','synthetic_owned_inventory_1',['gas.owned.receive_title','gas.owned.loss']),
            ('operator_balance','synthetic_operator_balance_2',['gas.operator.record','gas.operator.settle','gas.cash.record_payment'])]:
        account=str(uuid4());accounts[kind]=account
        record(account,'account',dict(code=code,kind=kind,bookId='d1000000-0000-0000-0000-000000000001',
            legalEntityId=parties['legalEntityId'],legalEntityRevision=parties['legalEntityRevision'],
            counterpartyId=parties['counterpartyId'] if kind=='operator_balance' else None,
            counterpartyRevision=parties['counterpartyRevision'] if kind=='operator_balance' else None,
            referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],definitionRevisionId=manifest['revisionId'],
            markCurve='future',markContractId='50000000-0000-0000-0000-000000000001',titlePolicy='explicit_accepted_title_v1',
            lossPolicy='separate_evidenced_loss_v1',effectiveAt='2020-12-18T00:00:00Z',**terms))
        for action in actions:
            policy=str(uuid4());policies[action]=policy
            call(f"SELECT lifecycle.save_policy('{catalog}',{_literal(dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action=action,
                mode='manual',fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
            record(account,'bind_policy',dict(action=action,policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))
    owned,operator=(accounts[key] for key in ('owned_inventory','operator_balance'))
    def evidence(account,key,kind,quantity,when):
        return record(account,'evidence',dict(sourceKey=key,kind=kind,quantity=quantity,occurredAt=when,expectedRevision=0,
            evidence='Synthetic native operator origin'))[1]['id']
    statement=evidence(operator,'synthetic_origin_return','operator_statement','10','2020-12-19T12:00:00Z')
    _,claim=record(operator,'operator_claim',dict(evidenceId=statement,quantity='10',effectiveAt='2020-12-19T12:00:00Z',dueAt='2020-12-21T12:00:00Z',
        carrying='nominal_mark_v1',settlementMode='return_quantity_v1',cashout=None),policies['gas.operator.record'],1)
    receipt=evidence(owned,'synthetic_origin_receipt','title_receipt','10','2020-12-20T12:00:00Z')
    record(operator,'operator_settle',dict(positionId=claim['id'],quantity='10',ownedAccountId=owned,lotId=None,reservationId=None,evidenceId=receipt,
        effectiveAt='2020-12-20T12:00:00Z',availableAt='2020-12-20T12:00:00Z',sourceObservationId=None),policies['gas.operator.settle'],1)
    observation=str(uuid4())
    query("INSERT INTO market_data.observations SELECT catalog_id,"+f"'{observation}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,"
        f"'synthetic_origin_cashout','1','daily_settlement','final','2020-12-19T15:00:00Z',clock_timestamp(),1.005,NULL "
        f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','70000000-0000-0000-0000-000000000001');")
    source=query(f"SELECT source_code FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{observation}');")
    statement=evidence(operator,'synthetic_origin_cash','operator_statement','3','2020-12-19T12:00:00Z')
    _,claim=record(operator,'operator_claim',dict(evidenceId=statement,quantity='3',effectiveAt='2020-12-19T12:00:00Z',dueAt='2020-12-21T12:00:00Z',
        carrying='nominal_mark_v1',settlementMode='cashout_final_price_v1',cashout=dict(source=source,role='daily_settlement',observedAt='2020-12-19T15:00:00Z',
            cashDueAt='2020-12-21T12:00:00Z',rounding='payment_total_half_even_2dp_v1')),policies['gas.operator.record'],1)
    cashout=evidence(operator,'synthetic_origin_determination','cashout_determination','3','2020-12-20T13:00:00Z')
    first=None
    for quantity in ['1','2']:
        _,settlement=record(operator,'operator_settle',dict(positionId=claim['id'],quantity=quantity,ownedAccountId=None,lotId=None,reservationId=None,
            evidenceId=cashout,effectiveAt='2020-12-20T13:00:00Z',availableAt=None,sourceObservationId=observation),policies['gas.operator.settle'],1)
        first=first or settlement
    payment=dict(feeId=first['cashId'],businessKey='synthetic_origin_bank',expectedRevision=0,paidAt='2020-12-20T14:00:00Z',amount='0.25',evidence='Synthetic bank receipt')
    record(operator,'fee_payment',payment,policies['gas.cash.record_payment'],1)
    record(operator,'fee_payment',payment|dict(expectedRevision=1,amount='0.5'),policies['gas.cash.record_payment'],1)


def _seed_counterparts(query,manifest):
    catalog=manifest['catalogId'];call,record=_operations(query,manifest)
    owned=json.loads(query(f"SELECT to_jsonb(a) FROM trading.gas_owned_accounts a WHERE(catalog_id,code)=('{catalog}','synthetic_owned_inventory_1');"))
    base=json.loads(query(f"SELECT to_jsonb(a) FROM trading.gas_owned_accounts a WHERE(catalog_id,code)=('{catalog}','synthetic_operator_balance_2');"))
    operator=str(uuid4())
    record(operator,'account',dict(code='synthetic_operator_upgrade_debt',kind='operator_balance',bookId=base['book_id'],
        legalEntityId=base['legal_entity_id'],legalEntityRevision=base['legal_entity_revision'],counterpartyId=base['counterparty_id'],counterpartyRevision=base['counterparty_revision'],
        referenceCatalogId=base['reference_catalog_id'],referenceRevisionId=base['reference_revision_id'],quantity=base['quantity_code'],location=base['location_code'],
        definitionRevisionId=base['definition_revision_id'],markCurve=base['mark_curve_code'],markContractId=base['mark_contract_id'],currency=base['currency_code'],
        titlePolicy=base['title_policy'],lossPolicy=base['loss_policy'],effectiveAt='2020-12-18T00:00:00Z'))
    policies={}
    for action in ('gas.operator.record','gas.operator.settle'):
        policy=str(uuid4());policies[action]=policy
        call(f"SELECT lifecycle.save_policy('{catalog}',{_literal(dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action=action,mode='manual',fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
        record(operator,'bind_policy',dict(action=action,policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))
    def evidence(account,key,kind,quantity,when):
        return record(account,'evidence',dict(sourceKey=key,kind=kind,quantity=quantity,occurredAt=when,expectedRevision=0,
            evidence='Synthetic independently accepted operator upgrade counterpart'))[1]['id']
    title=evidence(owned['id'],'synthetic_operator_upgrade_funding','title_receipt','3','2020-12-19T12:00:00Z')
    policy=json.loads(query(f"SELECT to_jsonb(p) FROM trading.gas_owned_action_bindings p WHERE(catalog_id,account_id,action)=('{catalog}','{owned['id']}','gas.owned.receive_title') ORDER BY revision DESC LIMIT 1;"))
    _,stock=record(owned['id'],'receive_title',dict(evidenceId=title,quantity='3',titleAt='2020-12-19T12:00:00Z',availableAt='2020-12-19T12:00:00Z',
        originPolicy='opening_owned_stock_v1',carrying='nominal_mark_v1'),policy['policy_id'],policy['policy_revision'])
    statement=evidence(operator,'synthetic_operator_upgrade_statement','operator_statement','-3','2020-12-19T12:00:00Z')
    _,claim=record(operator,'operator_claim',dict(evidenceId=statement,quantity='-3',effectiveAt='2020-12-19T12:00:00Z',dueAt='2020-12-21T12:00:00Z',
        carrying='nominal_mark_v1',settlementMode='return_quantity_v1',cashout=None),policies['gas.operator.record'],1)
    delivery=evidence(owned['id'],'synthetic_operator_upgrade_delivery','physical_delivery','-2','2020-12-20T13:00:00Z')
    _,settled=record(operator,'operator_settle',dict(positionId=claim['id'],quantity='2',ownedAccountId=owned['id'],lotId=stock['id'],reservationId=None,
        evidenceId=delivery,effectiveAt='2020-12-20T13:00:00Z',availableAt=None,sourceObservationId=None),policies['gas.operator.settle'],1)
    supplied=json.loads(query(f"SELECT to_jsonb(s) FROM trading.gas_owned_claim_settlements s WHERE(catalog_id,id)=('{catalog}','{settled['id']}');"))
    assert settled['changed'] and supplied['consumption_id'] and supplied['cash_id'] is None
    original=json.loads(query("SELECT to_jsonb(c) FROM trading.gas_owned_commands c JOIN trading.gas_owned_claim_settlements s "
        "ON(s.catalog_id,s.id)=(c.catalog_id,(c.outcome->>'id')::uuid) "
        f"WHERE c.catalog_id='{catalog}' AND c.request->>'action'='operator_settle' AND c.outcome->>'changed'='true' "
        "AND s.source_observation_id IS NOT NULL ORDER BY s.publication_order LIMIT 1;"))
    request=original['request']
    cash_claim=json.loads(query("SELECT c.request->'body' FROM trading.gas_owned_commands c "
        f"WHERE(c.catalog_id,c.request->>'action',c.outcome->>'id')=('{catalog}','operator_claim','{request['body']['positionId']}') AND c.outcome->>'changed'='true';"))
    negative_statement=evidence(operator,'synthetic_operator_upgrade_cash_statement','operator_statement','-3','2020-12-19T12:00:00Z')
    _,negative_cash=record(operator,'operator_claim',cash_claim|dict(evidenceId=negative_statement,quantity='-3'),policies['gas.operator.record'],1)
    _,later_record=_operations(query,manifest,'2020-12-22T16:00:00Z')
    for quantity,when,writer in [('1','2020-12-20T13:00:00Z',record),('2','2020-12-21T13:00:00Z',later_record)]:
        _,cash_document=writer(operator,'evidence',dict(sourceKey='synthetic_operator_upgrade_cash_'+quantity,kind='cashout_determination',quantity=quantity,
            occurredAt=when,evidence='Synthetic independently accepted signed cashout prefix',expectedRevision=0))
        _,cash=writer(operator,'operator_settle',request['body']|dict(positionId=negative_cash['id'],quantity=quantity,evidenceId=cash_document['id'],effectiveAt=when),policies['gas.operator.settle'],1)
        assert cash['changed'] and cash['cashId']
        assert query(f"SELECT consumption_id IS NULL AND receipt_lot_id IS NULL FROM trading.gas_owned_claim_settlements WHERE(catalog_id,id)=('{catalog}','{cash['id']}');")=='t'
    acknowledgement,ack=record(original['account_id'],'operator_settle',request['body'],request['policyId'],request['policyRevision'])
    assert ack['id']==original['outcome']['id'] and not ack['changed'] and ack['semanticAcknowledgement']
    return acknowledgement


def _origins(query,catalog,identities,acknowledgement):
    selected=','.join("'"+identity+"'::uuid" for identity in identities)
    rows=json.loads(query("SELECT jsonb_agg(jsonb_build_object('native',to_jsonb(s),'head',to_jsonb(h),'command',to_jsonb(c),'owner',to_jsonb(p),"
        "'account',(SELECT to_jsonb(a) FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(p.catalog_id,p.account_id)),"
        "'cashout',(SELECT to_jsonb(t) FROM trading.gas_owned_cashout_terms t WHERE(t.catalog_id,t.position_id)=(p.catalog_id,p.id)),"
        "'receipt',(SELECT to_jsonb(q) FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id,q.revision)=(h.catalog_id,h.receipt_lot_id,h.receipt_revision)),"
        "'consumption',(SELECT to_jsonb(q) FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id,q.revision)=(h.catalog_id,h.consumption_id,h.consumption_revision)),"
        "'cash',(SELECT to_jsonb(f) FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.id)=(s.catalog_id,s.cash_id)),"
        "'value',(SELECT value::text FROM market_data.observations o WHERE(o.catalog_id,o.id)=(s.catalog_id,s.source_observation_id)),"
        "'multiplier',(SELECT quote.multiplier::text FROM trading.gas_owned_accounts a JOIN market_data.curves curve "
        "ON(curve.catalog_id,curve.revision_id,curve.code)=(p.catalog_id,p.definition_revision_id,p.mark_curve_code) JOIN ref_data.quote_specifications quote "
        "ON(quote.catalog_id,quote.revision_id,quote.code)=(a.reference_catalog_id,a.reference_revision_id,curve.quote_code) WHERE(a.catalog_id,a.id)=(p.catalog_id,p.account_id))) "
        "ORDER BY s.position_id,s.publication_order) FROM trading.gas_owned_claim_settlements s JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(s.catalog_id,s.position_id) "
        "LEFT JOIN trading.gas_operator_settlement_quantities h ON(h.catalog_id,h.settlement_id,h.revision)=(s.catalog_id,s.id,1) "
        "LEFT JOIN trading.gas_owned_commands c ON(c.catalog_id,c.request_id)=(h.catalog_id,h.origin_request_id) "
        f"WHERE s.catalog_id='{catalog}' AND s.id IN({selected});"),parse_float=Decimal)
    assert len(rows)==len(identities)
    prior={};modes=set();cash_signs=set()
    with localcontext() as arithmetic:
        arithmetic.prec=80
        for row in rows:
            native,head,command,owner=(row[key] for key in ('native','head','command','owner'))
            assert head is not None and command is not None
            assert head['revision']==1 and head['previous_revision'] is None and head['operation_id'] is None
            assert head['source_allocation']=='initial_native_source_enrollment_v1'
            assert head['settlement_id']==native['id'] and head['claim_id']==native['position_id']
            assert head['actor_id']==native['actor_id']==command['actor_id'] and head['publication_order']==native['publication_order']
            assert command['request']['action']=='operator_settle' and command['outcome']['changed'] and not command['outcome']['semanticAcknowledgement']
            assert command['outcome']['id']==native['id'] and command['request_id']!=acknowledgement
            assert command['request']['body']['positionId']==native['position_id'] and command['request']['body']['evidenceId']==native['evidence_id']
            assert datetime.fromisoformat(command['request']['body']['effectiveAt'])==datetime.fromisoformat(native['effective_at'])
            assert head['quantity']==native['quantity']==Decimal(command['request']['body']['quantity']) and head['evidence_id']==native['evidence_id']
            assert head['source_observation_id']==native['source_observation_id']==command['request']['body']['sourceObservationId']
            previous=prior.get(native['position_id']);cumulative=(previous['cumulative'] if previous else Decimal(0))+native['quantity']
            assert head['previous_settlement_id']==(previous['id'] if previous else None)
            assert head['previous_settlement_revision']==(1 if previous else None) and head['cumulative_quantity']==cumulative
            if native['source_observation_id'] is None:
                assert all(head[key] is None for key in ('price','exact_total','rounded_total','cash_amount'))
                counterpart=row['receipt'] if native['receipt_lot_id'] else row['consumption']
                assert counterpart is not None and counterpart['revision']==1 and counterpart['publication_order']==native['publication_order']
                assert counterpart['quantity']==native['quantity']
                assert head['receipt_lot_id']==native['receipt_lot_id'] and head['consumption_id']==native['consumption_id']
                modes.add('receipt' if native['receipt_lot_id'] else 'consumption');rounded=None
            else:
                price=Decimal(row['value'])*Decimal(row['multiplier'])
                exact=(Decimal(1) if owner['quantity']>0 else Decimal(-1))*cumulative*price
                rounded=exact.quantize(Decimal('.01'),rounding=ROUND_HALF_EVEN)
                contribution=rounded-(previous['rounded'] if previous else Decimal(0))
                assert head['price']==price and head['exact_total']==exact and head['rounded_total']==rounded and head['cash_amount']==contribution
                if contribution==0:assert row['cash'] is None
                else:
                    cash=row['cash'];account=row['account']
                    assert cash['id']==native['cash_id'] and cash['amount']==contribution
                    assert cash['publication_order']==native['publication_order'] and cash['actor_id']==native['actor_id']
                    assert cash['effective_at']==native['effective_at'] and cash['due_at']==row['cashout']['cash_due_at']==owner['due_at']
                    assert cash['position_id']==native['position_id'] and cash['loan_id'] is None
                    assert (cash['reference_catalog_id'],cash['reference_revision_id'],cash['currency_code'])==(
                        account['reference_catalog_id'],account['reference_revision_id'],account['currency_code'])
                assert head['receipt_lot_id'] is None and head['consumption_id'] is None
                modes.add('cash');cash_signs.add(1 if owner['quantity']>0 else -1)
            prior[native['position_id']]=dict(id=native['id'],cumulative=cumulative,rounded=rounded)
    assert modes=={'receipt','consumption','cash'},modes
    assert cash_signs=={-1,1},cash_signs
    assert any(datetime.fromisoformat(row['native']['effective_at'])>datetime.fromisoformat('2020-12-20T16:00:00Z') for row in rows)
    return query(f"SELECT jsonb_agg(to_jsonb(q) ORDER BY settlement_id) FROM trading.gas_operator_settlement_quantities q WHERE catalog_id='{catalog}' AND revision=1 AND settlement_id IN({selected});")


def _future_loss(query,manifest):
    catalog=manifest['catalogId'];_,record=_operations(query,manifest,'2020-12-23T16:00:00Z')
    lot=json.loads(query(f"SELECT to_jsonb(p) FROM trading.gas_owned_positions p WHERE(catalog_id,origin_policy)=('{catalog}','operator_balance_transfer_v1');"))
    policy=json.loads(query(f"SELECT to_jsonb(p) FROM trading.gas_owned_action_bindings p WHERE(catalog_id,account_id,action)=('{catalog}','{lot['account_id']}','gas.owned.loss') ORDER BY revision DESC LIMIT 1;"))
    _,document=record(lot['account_id'],'evidence',dict(sourceKey='synthetic_future_loss',kind='physical_loss',quantity='2',occurredAt='2020-12-22T12:00:00Z',
        evidence='Synthetic accepted future-effective loss',expectedRevision=0))
    _,result=record(lot['account_id'],'loss',dict(lotId=lot['id'],evidenceId=document['id'],quantity='2',reservationId=None,effectiveAt='2020-12-22T12:00:00Z'),policy['policy_id'],policy['policy_revision'])
    assert result['changed']


def main():
    prepared={};root=Path(__file__).resolve().parents[1];target='0309_gas_operator_quantity_reconciliation.sql'
    def verify(query):
        catalog=prepared['manifest']['catalogId']
        assert gas_state(query,catalog,prepared['tables'])==prepared['state'],'Operator migration rewrote old financial, bank, publication or receipt rows.'
        assert query("SELECT to_regclass('trading.gas_operator_settlement_quantities') IS NOT NULL;")=='t'
        assert query(f"SELECT count(*) FROM trading.gas_operator_settlement_quantities WHERE catalog_id='{catalog}';")==str(len(prepared['identities']))
        prepared['origins']=_origins(query,catalog,prepared['identities'],prepared['acknowledgement']);prepared['verified']=True
        print('PASS existing operator upgrade: every old gas/publication/knowledge row unchanged; exact original actor, publication, receipt/consumption and cumulative cash prefix; acknowledgements do not enroll',flush=True)
    def before_migration(query,migration):
        if prepared and not prepared.get('verified') and migration.name>target:verify(query)
        if migration.name!=target:return
        assert not prepared and query("SELECT to_regclass('trading.gas_operator_settlement_quantities') IS NULL;")=='t'
        reference,market,security,manifest=synthetic_fixture(root);manifest['identityIssuer']='https://preview.invalid'
        load_fixture(query,reference,market,security,manifest)
        _seed_base(query,manifest)
        acknowledgement=_seed_counterparts(query,manifest);tables=gas_tables(query)
        identities=json.loads(query(f"SELECT jsonb_agg(id ORDER BY id) FROM trading.gas_owned_claim_settlements WHERE catalog_id='{manifest['catalogId']}';"))
        assert len(identities)==6
        prepared.update(manifest=manifest,tables=tables,state=gas_state(query,manifest['catalogId'],tables),identities=identities,acknowledgement=acknowledgement)
        print('PASS predecessor operator fixture: accepted receipt, identified debt dispatch and both signed rounded cash prefixes plus a distinct acknowledgement before0309',flush=True)
    def loaded_fixture(_):
        assert prepared,'The operator predecessor must be seeded before0309.'
        return '','','',prepared['manifest']
    def checks(query,manifest):
        assert prepared and manifest is prepared['manifest']
        if not prepared.get('verified'):verify(query)
        _future_loss(query,manifest)
        prior=json.loads(query(f"SELECT to_jsonb(c) FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.request_id)=('{manifest['catalogId']}','{prepared['acknowledgement']}');"))
        request=prior['request'];_,record=_operations(query,manifest)
        _,ack=record(prior['account_id'],'operator_settle',request['body'],request['policyId'],request['policyRevision'])
        assert ack['id']==prior['outcome']['id'] and not ack['changed'] and ack['semanticAcknowledgement']
        assert _origins(query,manifest['catalogId'],prepared['identities'],prepared['acknowledgement'])==prepared['origins']
        print('PASS native operator upgrade retains immutable q1 origins through a new loss and a post-upgrade acknowledgement without enrolling new settlements',flush=True)
    native_main(checks=checks,before_migration=before_migration,fixture_factory=loaded_fixture,through_migration='0309')


if __name__=='__main__':main()
