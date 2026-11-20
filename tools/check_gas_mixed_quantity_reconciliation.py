"""An owned title correction and its physical dispatch funding share one commit."""
import json
import os
import subprocess
from decimal import Decimal
from pathlib import Path
from uuid import uuid4

from check_reference_editing import main
from check_gas_quantity_reconciliation import checks as quantity_checks, fixture
from gas_quantity_bundle_seal import upgrade_checks
from gas_quantity_fixture import principal_plan


def checks(query, manifest, *, run_correction=None):
    quantity_checks(query, manifest)
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    at = '2026-11-06T16:00:00Z'
    def literal(value):
        return "'"+json.dumps(value).replace("'", "''")+"'::jsonb"

    def call(sql,rollback=False):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                      f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}"+('ROLLBACK;' if rollback else 'COMMIT;'))

    def capture():
        return call(f"SELECT security.resolve_operation_context('{catalog}','2026-11-06',NULL);")

    physical = json.loads(query("SELECT to_jsonb(l)||jsonb_build_object('accountId',p.account_id) "
                               "FROM settlement_data.futures_delivery_obligations l JOIN trading.gas_owned_positions p "
                               "ON(p.catalog_id,p.id)=(l.catalog_id,l.position_id) "
                               f"WHERE l.catalog_id='{catalog}' AND p.quantity<0;"))
    account, position = physical['accountId'], physical['position_id']
    def gas(action, body, policy=None):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{account}',{literal(body)},"
                              f"'{at}','{capture()}',"+(f"'{policy}',1" if policy else 'NULL,NULL')+",false)::text;"))

    def bind(action):
        policy = str(uuid4())
        call(f"SELECT lifecycle.save_policy('{catalog}',{literal(dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,
             action=action,mode='manual',fromAt='2026-01-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
        gas('bind_policy',dict(action=action,policyId=policy,policyRevision=1,effectiveAt='2026-11-06T00:00:00Z'))
        return policy

    receive = bind('gas.owned.receive_title')
    ordinary_policy = bind('gas.owned.reconcile_quantity')
    def evidence(key, quantity, when, kind='title_receipt', revision=0):
        return gas('evidence',dict(sourceKey=key,kind=kind,quantity=quantity,occurredAt=when,
                                   evidence='Synthetic mixed quantity reconciliation evidence',expectedRevision=revision))['id']

    def stock(quantity, when):
        key = str(uuid4())
        source = evidence(key,quantity,when)
        accepted = gas('receive_title',dict(evidenceId=source,quantity=quantity,titleAt=when,availableAt=when,
                                           originPolicy='opening_owned_stock_v1',carrying='nominal_mark_v1'),receive)
        return accepted['id'], key

    opening, source_key = stock('1','2026-11-06T13:15:00Z')
    replacement, _ = stock('0.25','2026-11-06T13:20:00Z')
    performed_at = '2026-11-06T14:30:00Z'
    performance = str(uuid4())
    proof = evidence(str(uuid4()),'0.25',performed_at,'physical_delivery')
    terms = json.loads(query("SELECT to_jsonb(t) FROM trading.gas_futures_delivery_terms t "
                             f"WHERE(t.catalog_id,t.profile_id,t.profile_revision)=('{catalog}','{physical['profile_id']}',{physical['profile_revision']});"))
    proposal = principal_plan(query,catalog,position,added=dict(id=performance,business_reference='synthetic_mixed_dispatch',
                                                              performed_at=performed_at,quantity='0.25'))
    body = dict(positionId=position,businessReference='synthetic_mixed_dispatch',expectedVersion=proposal['expectedVersion'],
                quantity='0.25',performedAt=performed_at,titleEvidenceId=None,availableAt=None,
                supply=[dict(lotId=opening,evidenceId=proof,reservationId=None,quantity='0.25')])
    call(f"SELECT security.record_gas_futures_delivery('{catalog}','{performance}','{position}','performance',{literal(body)},"
         f"'{at}','{capture()}','{terms['action_policy_id']}',{terms['action_policy_revision']},false,{literal(proposal)});")
    corrected = evidence(source_key,'0.125','2026-11-06T13:15:00Z',revision=1)
    ordinary = dict(positions=[dict(positionId=opening,expectedRevision=1,quantity='0.125',retracted=False,evidenceId=corrected,
                                   sourceAllocation='preserve_unallocated_source_quantity_v1')],consumptions=[],
                    evidence='Synthetic explicit correction of title funding an accepted dispatch')
    supplies = json.loads(query("SELECT jsonb_agg(to_jsonb(s)||jsonb_build_object('quantity',c.quantity)) "
                                 "FROM trading.gas_futures_performance_quantity_supplies s JOIN trading.gas_owned_consumption_quantities c "
                                 "ON(c.catalog_id,c.consumption_id,c.revision)=(s.catalog_id,s.consumption_id,s.consumption_revision) "
                                 f"WHERE(s.catalog_id,s.performance_id,s.performance_revision)=('{catalog}','{performance}',1);"))
    assert len(supplies)==1 and Decimal(str(supplies[0]['quantity']))==Decimal('.25')
    supplied = supplies[0]
    plan = principal_plan(query,catalog,position)
    correction = dict(positionId=position,performanceId=performance,expectedRevision=1,expectedPrincipalVersion=plan['expectedVersion'],
                      quantity='0.25',retracted=False,titleEvidenceId=None,sourceAllocation='preserve_unallocated_source_quantity_v1',
                      evidence='Synthetic explicit split of original and replacement stock',supply=[
                          dict(consumptionId=supplied['consumption_id'],expectedRevision=supplied['consumption_revision'],lotId=opening,
                               quantity='0.125',retracted=False,evidenceId=proof,reservationId=None),
                          dict(consumptionId=None,expectedRevision=0,lotId=replacement,quantity='0.125',retracted=False,evidenceId=proof,reservationId=None)])
    physical_body = dict(corrections=[correction])
    continuation = dict(implementation='gas_futures_quantity_batch_continuation_v1',positions=[plan])
    physical_policy = query("SELECT policy_id FROM trading.gas_owned_action_bindings "
                            f"WHERE(catalog_id,account_id,action)=('{catalog}','{account}','gas.futures.reconcile_quantity') ORDER BY revision DESC LIMIT 1;")
    context = capture()
    for statement, code in [(
        f"SELECT security.reconcile_gas_owned_quantities('{catalog}','{uuid4()}','{account}',{literal(ordinary)},'{at}','{context}','{ordinary_policy}',1,false);", 'gas_stock_capacity'),(
        f"SELECT security.reconcile_gas_futures_delivery_batch('{catalog}','{uuid4()}','{account}',{literal(physical_body)},'{at}','{context}','{physical_policy}',1,false,{literal(continuation)});", 'gas_owned_reconciliation')]:
        try:
            call(statement)
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('A sequential half-correction must refuse '+code)
    from gas_quantity_bundle_seal import source_check
    source_check(query,call,capture,catalog,account,ordinary,correction,at,ordinary_policy,physical_policy,continuation,literal)
    context=capture()
    total = query(f"SELECT trim_scale(sum(amount)) FROM trading.gas_futures_delivery_cash WHERE(catalog_id,position_id)=('{catalog}','{position}');")
    payments = query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY id),'[]')::text) FROM trading.gas_futures_delivery_payments p WHERE catalog_id='{catalog}';")
    operation = str(uuid4())
    request = f"SELECT security.reconcile_gas_quantity_bundle('{catalog}','{operation}','{account}',{literal(dict(ordinary=ordinary,physical=physical_body,physicalAccountId=account))},'{at}','{context}','{ordinary_policy}',1,'{physical_policy}',1,false,{literal(continuation)})::text;"
    # Naming an unrelated unchanged title cannot authorize the pending title
    # source that actually funds this dispatch.
    replacement_source=query(f"SELECT evidence_id FROM trading.gas_owned_position_quantities WHERE(catalog_id,position_id,revision)=('{catalog}','{replacement}',1);")
    unrelated={**ordinary,'positions':[dict(positionId=replacement,expectedRevision=1,quantity='0.25',retracted=False,evidenceId=replacement_source,
        sourceAllocation='preserve_unallocated_source_quantity_v1')]}
    incomplete=request.replace(operation,str(uuid4())).replace(literal(dict(ordinary=ordinary,physical=physical_body,physicalAccountId=account)),
        literal(dict(ordinary=unrelated,physical=physical_body,physicalAccountId=account)))
    try:call(incomplete)
    except RuntimeError as error:assert 'gas_owned_reconciliation' in str(error),str(error)
    else:raise AssertionError('An unrelated acknowledged title bypassed the exact pending-source closure')
    from gas_quantity_bundle_seal import checks as seal_checks
    seal_checks(query,call,request,catalog)
    from gas_owned_reconciliation_authority import checks as authority_checks
    authority_checks(query,call,request,manifest,issuer=manifest['identityIssuer'])
    before=int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';"))
    host=query('SHOW unix_socket_directories;');path=Path(host)/'gas-mixed-correction.json'
    packet=dict(manifest=manifest,host=host,at=at,contextId=context,operationId=operation,accountId=account,policyId=ordinary_policy,
        selection=dict(ordinary=ordinary,physical=physical_body,physicalAccountId=account,physicalPolicyId=physical_policy,physicalPolicyRevision=1))
    if run_correction is None:
        path.write_text(json.dumps(packet))
        subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--project','tests/OpenEtrm.Api.Checks','--no-build','--no-restore','--',
                        '--gas-mixed-correction',str(path)],check=True,cwd=Path(__file__).resolve().parents[1])
    else:
        operation,selected_context,before=run_correction(query,manifest,packet,call=call)
        request=request.replace(packet['operationId'],operation).replace(context,selected_context)
    # The managed producer owns its exact persisted continuation representation.
    # Receipt recovery deliberately excludes a recomputed continuation while the
    # independent literal totals above continue to constrain native finance.
    receipt_request=request.replace(',false,'+literal(continuation),',true,NULL')
    result = json.loads(call(receipt_request))['outcome']
    assert result['changed'] and result['operationId']==operation
    assert int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';"))==before+1
    assert {result[part]['publicationOrder'] for part in ('ordinary','physical')}=={result['publicationOrder']}
    assert query(f"SELECT count(*) FROM trading.gas_quantity_bundles WHERE(catalog_id,operation_id)=('{catalog}','{operation}');")=='1'
    assert json.loads(call(receipt_request))['outcome']==result
    assert query(f"SELECT trim_scale(sum(amount)) FROM trading.gas_futures_delivery_cash WHERE(catalog_id,position_id)=('{catalog}','{position}');")==total
    assert query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY id),'[]')::text) FROM trading.gas_futures_delivery_payments p WHERE catalog_id='{catalog}';")==payments
    assert query(f"SELECT quantity FROM trading.gas_owned_positions_current WHERE(catalog_id,id)=('{catalog}','{opening}');")=='0.125'
    uses = json.loads(query(f"SELECT jsonb_object_agg(lot_id,quantity) FROM trading.gas_owned_consumptions_current WHERE catalog_id='{catalog}' AND lot_id IN('{opening}','{replacement}') AND quantity>0;"))
    assert {key:Decimal(str(value)) for key,value in uses.items()}=={opening:Decimal('.125'),replacement:Decimal('.125')}
    from gas_quantity_bundle_seal import acknowledgement_checks
    acknowledgement_checks(query,call,request.replace(operation,str(uuid4())),catalog)
    print('PASS mixed ordinary and physical quantity correction: explicit original/replacement stock, both sequential halves refuse, unchanged signed principal and bank payments, one complete first outcome',flush=True)


if __name__=='__main__':
    main(checks=checks,fixture_factory=fixture,before_migration=upgrade_checks)
