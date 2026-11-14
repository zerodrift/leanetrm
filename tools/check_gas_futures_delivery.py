"""Actual physical performance and reviewed cumulative principal in owned PG."""
import json
from decimal import Decimal
from uuid import uuid4

from check_reference_editing import main
from check_futures_physical_delivery import fixture, checks as physical_checks
from check_lifecycle_actions import upgrade_checks as common_upgrade_checks


def upgrade_checks(query,migration):
    common_upgrade_checks(query,migration)
    if migration.name=='0273_gas_futures_delivery_principal.sql':
        groups=[('futures_delivery_holdings_upgrade','security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])')]
    elif migration.name=='0274_gas_futures_delivery_commands.sql':
        groups=[('futures_delivery_principal_upgrade','settlement_data.determine_futures_margin(uuid,jsonb,jsonb)'),
            ('futures_delivery_principal_upgrade','settlement_data.determine_futures_margin_native(uuid,jsonb,jsonb,timestamptz)'),
            ('futures_delivery_principal_upgrade','settlement_data.finish_futures_margin_run(uuid,uuid,jsonb,timestamptz)'),
            ('futures_delivery_shared_upgrade','lifecycle.record_native_action(uuid,jsonb,jsonb,timestamptz)'),
            ('futures_delivery_shared_upgrade','lifecycle.finish_command(uuid,uuid,text,jsonb,text,timestamptz)')]
    else:return
    from assistant_context import check_guard
    for guard,signature in groups:
        changes=[(change,signature) for change in (
            f"UPDATE pg_proc SET proargmodes=array_fill('i'::\"char\",ARRAY[{signature.count(',')+1}]) WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET provariadic='uuid'::regtype WHERE oid='{signature}'::regprocedure;",
            f"ALTER FUNCTION {signature} SET work_mem='64kB';",f"ALTER FUNCTION {signature} STRICT;",
            f"ALTER FUNCTION {signature} LEAKPROOF;",f"ALTER FUNCTION {signature} PARALLEL SAFE;",
            f"ALTER FUNCTION {signature} STABLE;",f"ALTER FUNCTION {signature} SECURITY INVOKER;",
            f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
            f"GRANT EXECUTE ON FUNCTION {signature} TO openetrm_runtime WITH GRANT OPTION;",
            f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
            f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic drift */ ');END$$;")]
        check_guard(query,migration.read_text(),migration.name[:4],guard,changes)


def checks(query, manifest, *, carrying='currency_curve_v1'):
    physical_checks(query, manifest,carrying=carrying)
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    issuer = manifest['identityIssuer']
    def literal(value): return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"
    def call(sql):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{issuer}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture(day='2026-11-06'): return call(f"SELECT security.capture_date_context('{catalog}','{day}',NULL);")
    def refuse(sql, code):
        try: call(sql)
        except RuntimeError as error: assert code in str(error), str(error)
        else: raise AssertionError('Expected ' + code)
    link=json.loads(query(f"SELECT to_jsonb(x) FROM settlement_data.futures_delivery_obligations x WHERE catalog_id='{catalog}';"))
    holding=link['position_id']
    terms=json.loads(query(f"SELECT to_jsonb(x) FROM trading.gas_futures_delivery_terms x WHERE(catalog_id,profile_id,profile_revision)="
        f"('{catalog}','{link['profile_id']}',{link['profile_revision']});"))
    account=terms['account_id']
    at='2026-11-06T16:00:00Z'
    def gas(action,body):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{account}',{literal(body)},'{at}','{capture()}',NULL,NULL,false)::text;"))
    gas('bind_policy',dict(action='gas.futures.delivery',policyId=terms['action_policy_id'],policyRevision=terms['action_policy_revision'],effectiveAt='2026-10-01T00:00:00Z'))
    payment_policy=dict(operationId=str(uuid4()),policyId=str(uuid4()),expectedRevision=0,action='gas.cash.record_payment',mode='manual',
        fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(payment_policy)});")
    gas('bind_policy',dict(action='gas.cash.record_payment',policyId=payment_policy['policyId'],policyRevision=1,effectiveAt='2026-10-01T00:00:00Z'))
    def evidence(when):
        return gas('evidence',dict(sourceKey=str(uuid4()),kind='title_receipt',quantity='0.25',occurredAt=when,
            evidence='Synthetic observed physical title',expectedRevision=0))['id']
    def final():
        return json.loads(query(f"SELECT to_jsonb(d)||jsonb_build_object('price',o.value,'at',to_char(p.at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"')) FROM settlement_data.futures_margin_determinations d "
            "JOIN market_data.observations o ON(o.catalog_id,o.id)=(d.catalog_id,d.source_observation_id) "
            "JOIN settlement_data.futures_margin_policy_occurrences p ON(p.catalog_id,p.policy_id,p.policy_revision,p.id)=(d.catalog_id,d.policy_id,d.policy_revision,d.occurrence_id) "
            f"WHERE(d.catalog_id,d.trade_id,d.binding_id,d.occurrence_id)=('{catalog}','{link['trade_id']}','{link['binding_id']}','{link['final_occurrence_id']}') ORDER BY d.run_revision DESC LIMIT 1;"))
    def ids():
        return json.loads(query(f"SELECT coalesce(jsonb_object_agg(performance_id,id),'{{}}') FROM trading.gas_futures_delivery_principal_determinations "
            f"WHERE(catalog_id,position_id,run_revision)=('{catalog}','{holding}',(SELECT max(revision) FROM trading.gas_futures_delivery_principal_runs WHERE(catalog_id,position_id)=('{catalog}','{holding}')));"))
    def step(identity,quantity,exact,total,contribution,adjustment,previous):
        return dict(performanceId=identity,previousDeterminationId=previous.get(identity),cumulativeQuantity=quantity,exactTotal=exact,
            roundedTotal=total,contribution=contribution,adjustment=adjustment)
    def continuation(version,steps,observation=None):
        return dict(implementation='gas_futures_delivery_continuation_v1',positionId=holding,expectedVersion=version,
            finalOccurrenceId=link['final_occurrence_id'],sourceObservationId=observation or final()['source_observation_id'],steps=steps)
    def record(operation,body,plan,action='performance',context=None):
        policy=terms['action_policy_id'] if action=='performance' else payment_policy['policyId']
        return f"SELECT security.record_gas_futures_delivery('{catalog}','{operation}','{holding}','{action}',{literal(body)},'{at}','{context or capture()}',"+\
            f"'{policy}',1,false,{literal(plan) if plan is not None else 'NULL'})::text;"
    first,second,middle=(str(uuid4()) for _ in range(3))
    first_at,second_at,middle_at='2026-11-03T12:00:00Z','2026-11-05T12:00:00Z','2026-11-04T12:00:00Z'
    def body(reference,when,title,version):
        return dict(positionId=holding,businessReference=reference,expectedVersion=version,quantity='0.25',performedAt=when,
            titleEvidenceId=title,availableAt=when,supply=[])
    first_body=body('synthetic_delivery_first',first_at,evidence(first_at),0)
    first_plan=continuation(0,[step(first,'0.25','-1.5','-1.5','-1.5','-1.5',{})])
    command=record(first,first_body,first_plan)
    accepted=json.loads(call(command));assert accepted['principalVersion']==1 and accepted['id']==first
    assert json.loads(call(command))==accepted
    refuse(record(first,first_body,{**first_plan,'expectedVersion':1}),'gas_owned_conflict')
    previous=ids()
    second_body=body('synthetic_delivery_last',second_at,evidence(second_at),1)
    second_plan=continuation(1,[step(first,'0.25','-1.5','-1.5','-1.5','0',previous),step(second,'0.5','-3','-3','-1.5','-1.5',previous)])
    assert json.loads(call(record(second,second_body,second_plan)))['principalVersion']==2
    assert query(f"SELECT count(*) FROM trading.gas_futures_delivery_performances WHERE catalog_id='{catalog}';")=='2'

    def correct(price,version,plan_values):
        old=final();observation=str(uuid4());occurrence=old['occurrence_id'];context_day=str(old['at'])[:10]
        query(f"INSERT INTO market_data.observations SELECT catalog_id,'{observation}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,"
            f"source_event_id,(source_revision_id::integer+1)::text,role,publication_state,observed_at,clock_timestamp(),{price},id FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{old['source_observation_id']}');")
        size=Decimal(query(f"SELECT quantity FROM trading.gas_owned_positions WHERE(catalog_id,id)=('{catalog}','{holding}');"))
        due='2026-11-06T12:00:00.000000Z'
        request=dict(operationId=str(uuid4()),tradeId=link['trade_id'],bindingId=link['binding_id'],expectedRevision=1,expectedMarginRevision=version,
            contextId=capture(context_day),asOf=old['at'],throughOccurrence=occurrence,implementation='native_futures_margin_journal_v1',
            correctionDue=[dict(occurrenceId=occurrence,dueAt=due)],previewHash='0'*64)
        vm=dict(occurrenceId=occurrence,previousDeterminationId=old['id'],sourceObservationId=observation,previousBasis='3',
            exactVariation=str((Decimal(price)-3)*size),roundedTotal=str((Decimal(price)-3)*size),adjustment=str((Decimal(price)-Decimal(str(old['price'])))*size),adjustmentDue=due,noChange=False)
        prior=ids();plan=continuation(version-1,[step(identity,*values,prior) for identity,values in plan_values],observation)
        sql=f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(request)},{literal([vm])},{literal(plan)})::text;"
        accepted=json.loads(call(sql));assert accepted['currentPrincipalVersion']==version and accepted['changed']==1
        assert json.loads(call(sql))==accepted
        refuse(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(request)},{literal([vm])},{literal({**plan,'expectedVersion':0})})::text;",'futures_delivery_command_conflict')
        return request,vm,plan,accepted
    # Final correction's effective instant precedes BOTH known performances.
    first_correction=correct('6.01',3,[(first,('0.25','-1.5025','-1.5','-1.5','0')),(second,('0.5','-3.005','-3','-1.5','0'))])
    assert query(f"SELECT count(*) FROM trading.gas_futures_delivery_cash WHERE catalog_id='{catalog}';")=='2', 'Zero cash must still retain the new accepted principal basis'
    second_correction=correct('6.03',4,[(first,('0.25','-1.5075','-1.51','-1.51','-0.01')),(second,('0.5','-3.015','-3.02','-1.51','-0.01'))])
    # An older accepted composite fact may be acknowledged after a later
    # price, but every retained principal operand must be truthful.
    import copy
    old_request,old_vm,old_plan,_=first_correction
    before_ack=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    def acknowledge(plan):
        request={**old_request,'operationId':str(uuid4())}
        return f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(request)},{literal([old_vm])},{literal(plan)})::text;"
    acknowledged=json.loads(call(acknowledge(old_plan)))
    assert acknowledged['acknowledgedPrincipalVersion']==3 and acknowledged['currentPrincipalVersion']==4
    unchanged=copy.deepcopy(old_plan)
    accepted_ids=json.loads(query(f"SELECT jsonb_object_agg(performance_id,id) FROM trading.gas_futures_delivery_principal_determinations "
        f"WHERE(catalog_id,position_id,run_revision)=('{catalog}','{holding}',3);"))
    for row in unchanged['steps']:row.update(previousDeterminationId=accepted_ids[row['performanceId']],adjustment='0')
    acknowledged=json.loads(call(acknowledge(unchanged)))
    assert acknowledged['acknowledgedPrincipalVersion']==3 and acknowledged['currentPrincipalVersion']==4
    missing_ack_guards=[]
    for name,change in [('exact total',lambda p:p['steps'][0].update(exactTotal='999')),
        ('adjustment',lambda p:p['steps'][0].update(adjustment='1')),
        ('causal prior',lambda p:p['steps'][0].update(previousDeterminationId=str(uuid4()))),
        ('missing causal prior',lambda p:p['steps'][0].update(previousDeterminationId=None)),
        ('prefix order',lambda p:p['steps'].reverse()),('missing prefix',lambda p:p.update(steps=[]))]:
        malformed=copy.deepcopy(old_plan);change(malformed)
        try:call(acknowledge(malformed))
        except RuntimeError as error:assert 'futures_delivery_acknowledgement' in str(error),str(error)
        else:missing_ack_guards.append(name)
    try:call(acknowledge(None))
    except RuntimeError as error:assert 'futures_delivery_continuation_required' in str(error),str(error)
    else:missing_ack_guards.append('missing continuation')
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==before_ack
    previous=ids();middle_body=body('synthetic_delivery_middle',middle_at,evidence(middle_at),4)
    middle_plan=continuation(4,[step(first,'0.25','-1.5075','-1.51','-1.51','0',previous),
        step(middle,'0.5','-3.015','-3.02','-1.51','-1.51',previous),step(second,'0.75','-4.5225','-4.52','-1.50','0.01',previous)])
    assert json.loads(call(record(middle,middle_body,middle_plan)))['principalVersion']==5
    assert query(f"SELECT trim_scale(sum(amount)) FROM trading.gas_futures_delivery_cash WHERE catalog_id='{catalog}';")=='-4.52'
    assert query(f"SELECT count(*) FROM trading.gas_owned_lots WHERE catalog_id='{catalog}';")=='3', 'Final corrections must never create duplicate title'
    current=capture()
    book=query(f"SELECT book_id FROM trading.gas_owned_accounts WHERE(catalog_id,id)=('{catalog}','{account}');")
    view=json.loads(call(f"SELECT security.use_market_context('{catalog}','{current}');SELECT security.use_trading_context('{catalog}','{at}');"
        f"SELECT security.read_gas_owned_holdings('{catalog}','{current}','{at}',ARRAY['{book}'::uuid])::text;"))
    expected=Decimal(query(f"SELECT quantity FROM trading.gas_owned_positions WHERE(catalog_id,id)=('{catalog}','{holding}');"))-Decimal('0.75')
    residual=next(p for p in view['positions'] if p['id']==holding)
    assert Decimal(str(residual['remaining_quantity']))==expected and len(view['deliveryCash'])==6
    cash=next(c for c in view['deliveryCash'] if Decimal(str(c['amount']))==Decimal('-1.5'))
    pay=dict(positionId=holding,cashId=cash['id'],businessReference='synthetic_principal_payment',expectedVersion=0,
        paidAt='2026-11-02T12:00:00Z',amount='-0.5',evidence='Synthetic recorded cash')
    refuse(record(str(uuid4()),pay,None,'payment'),'futures_delivery_payment_origin')
    pay['paidAt']='2026-11-06T12:00:00Z'
    paid=json.loads(call(record(str(uuid4()),pay,None,'payment')));assert paid['currentRevision']==1
    revised={**pay,'expectedVersion':1,'amount':'-0.75'}
    assert json.loads(call(record(str(uuid4()),revised,None,'payment')))['currentRevision']==2
    ack=json.loads(call(record(str(uuid4()),{**pay,'expectedVersion':2},None,'payment')))
    assert not ack['changed'] and ack['acknowledgedRevision']==1 and ack['currentRevision']==2
    from pathlib import Path
    import os
    import subprocess
    managed_at='2026-11-06T13:00:00Z'
    managed_evidence=evidence(managed_at)
    managed_context=capture()
    host=query('SHOW unix_socket_directories;')
    packet=Path(host)/'gas-delivery-action-check.json'
    packet.write_text(json.dumps(dict(manifest=manifest,host=host,positionId=holding,contextId=managed_context,at=at,
        performedAt=managed_at,evidenceId=managed_evidence,policyId=terms['action_policy_id'])))
    packet.chmod(0o600)
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks',
        '--','--gas-futures-delivery',str(packet)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(),flush=True)
    assert query(f"SELECT count(*) FROM trading.gas_owned_lots WHERE catalog_id='{catalog}';")=='4'
    assert query(f"SELECT trim_scale(sum(amount)) FROM trading.gas_futures_delivery_cash WHERE catalog_id='{catalog}';")=='-6.03'
    # The same product adapter must discharge a short through identified
    # owned supply, including two pieces from one account in one publication.
    account_row=json.loads(query(f"SELECT to_jsonb(a) FROM trading.gas_owned_accounts a WHERE(catalog_id,id)=('{catalog}','{account}');"))
    party=query(f"SELECT id FROM trading.counterparties WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    booking=dict(legalEntityId=account_row['legal_entity_id'],legalEntityRevision=account_row['legal_entity_revision'],counterpartyId=party,counterpartyRevision=1)
    profile=json.loads(query(f"SELECT to_jsonb(p) FROM settlement_data.futures_delivery_profiles p WHERE(catalog_id,id,revision)=('{catalog}','{link['profile_id']}',{link['profile_revision']});"))
    booked=json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{account_row['book_id']}','{manifest['revisionId']}',"
        f"'nymex_ng','{profile['contract_id']}','2026-10-16T14:00:00Z',NULL,-2,3,'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(booking)})::text;"))
    adoption=dict(operationId=str(uuid4()),tradeId=booked['tradeId'],bindingId=booked['bindingId'],expectedRevision=1,policyId=link['policy_id'],policyRevision=link['policy_revision'],
        contextId=capture('2026-10-16'),asOf='2026-10-16T16:00:00.000000Z',adoption='native_execution_inception_v1')
    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(adoption)});")
    source=final();size=-Decimal(query(f"SELECT quantity FROM trading.gas_owned_positions WHERE(catalog_id,id)=('{catalog}','{holding}');"))
    variation=(Decimal(str(source['price']))-3)*size
    request=dict(operationId=str(uuid4()),tradeId=booked['tradeId'],bindingId=booked['bindingId'],expectedRevision=1,expectedMarginRevision=1,
        contextId=capture(source['at'][:10]),asOf=source['at'],throughOccurrence=link['final_occurrence_id'],implementation='native_futures_margin_journal_v1',correctionDue=[],previewHash='0'*64)
    vm=dict(occurrenceId=link['final_occurrence_id'],previousDeterminationId=None,sourceObservationId=source['source_observation_id'],previousBasis='3',
        exactVariation=str(variation),roundedTotal=str(variation),adjustment=str(variation),adjustmentDue=source['at'],noChange=False)
    plan=dict(implementation='gas_futures_delivery_continuation_v1',positionId=None,expectedVersion=0,finalOccurrenceId=link['final_occurrence_id'],sourceObservationId=source['source_observation_id'],steps=[])
    accepted=json.loads(call(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(request)},{literal([vm])},{literal(plan)})::text;"))
    short=accepted['deliveryPositionId']
    lots=json.loads(query(f"SELECT jsonb_agg(id) FROM(SELECT l.id FROM trading.gas_owned_lots l WHERE(catalog_id,account_id)=('{catalog}','{account}') ORDER BY l.id LIMIT 2)s;"))
    dispatch_at='2026-11-06T14:00:00Z'
    def dispatch_evidence(quantity):
        return gas('evidence',dict(sourceKey=str(uuid4()),kind='physical_delivery',quantity=quantity,occurredAt=dispatch_at,
            evidence='Synthetic observed physical dispatch',expectedRevision=0))['id']
    oversized=dispatch_evidence('0.5');pieces=[dispatch_evidence('0.25') for _ in lots]
    def dispatch(supply):
        identity=str(uuid4())
        body=dict(positionId=short,businessReference='synthetic_identified_short_dispatch',expectedVersion=0,quantity='0.5',performedAt=dispatch_at,
            titleEvidenceId=None,availableAt=None,supply=supply)
        plan=dict(implementation='gas_futures_delivery_continuation_v1',positionId=short,expectedVersion=0,finalOccurrenceId=link['final_occurrence_id'],sourceObservationId=source['source_observation_id'],
            steps=[dict(performanceId=identity,previousDeterminationId=None,cumulativeQuantity='-0.5',exactTotal='3.015',roundedTotal='3.02',contribution='3.02',adjustment='3.02')])
        return f"SELECT security.record_gas_futures_delivery('{catalog}','{identity}','{short}','performance',{literal(body)},'{at}','{capture()}',"+\
            f"'{terms['action_policy_id']}',1,false,{literal(plan)})::text;"
    refuse(dispatch([dict(lotId=lots[0],evidenceId=oversized,reservationId=None,quantity='0.5')]),'gas_stock_capacity')
    result=json.loads(call(dispatch([dict(lotId=lot,evidenceId=proof,reservationId=None,quantity='0.25') for lot,proof in zip(lots,pieces)])))
    assert result['principalVersion']==1 and result['stockId'] is None
    assert query(f"SELECT trim_scale(sum(amount)) FROM trading.gas_futures_delivery_cash WHERE(catalog_id,position_id)=('{catalog}','{short}');")=='3.02'
    assert query(f"SELECT count(*) FROM trading.gas_futures_delivery_splits WHERE(catalog_id,performance_id)=('{catalog}','{result['id']}');")=='2'
    print('PASS short physical delivery: explicit multiple owned lots, no negative stock or procurement, positive cumulative principal and atomic source admission',flush=True)
    assert not missing_ack_guards, 'Accepted forged principal acknowledgement: '+', '.join(missing_ack_guards)
    print('PASS physical performance: separately held title, complete future-known principal, zero-cash final rebasing, backdated prefixes, first outcomes, standalone cash and payment origin',flush=True)


if __name__=='__main__': main(checks=checks,fixture_factory=fixture,before_migration=upgrade_checks)
