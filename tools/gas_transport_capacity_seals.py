"""Independent owner-definer attacks against capacity source and member seals."""
import json
from copy import deepcopy
from uuid import uuid4

from gas_transport_capacity_fixture import identity, literal


def seals(f, rejected):
    catalog = f.catalog
    def attack(statement, code):
        from native_read_transport import register_captured_input
        inputs=''.join(register_captured_input(f.query,identifier) for identifier in f.query.read_sources)
        # Only the owned disposable database receives this future-definer probe.
        f.query("CREATE FUNCTION trading.synthetic_transport_definer() RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $probe$BEGIN "
            + statement + " END;$probe$;ALTER FUNCTION trading.synthetic_transport_definer() OWNER TO openetrm_security_owner;"
            "REVOKE ALL ON FUNCTION trading.synthetic_transport_definer() FROM PUBLIC;GRANT EXECUTE ON FUNCTION trading.synthetic_transport_definer() TO openetrm_runtime;")
        try:
            rejected(lambda: f.call(inputs+'SELECT trading.synthetic_transport_definer();'), code)
        finally:
            f.query('DROP FUNCTION trading.synthetic_transport_definer();')

    body = f.definition('synthetic_definer_seals')
    version = str(uuid4())
    f.record(body, operation=version)
    facts = f.read(body['entitlementId'])
    operation, chosen = f.plan(facts,'synthetic_sealed_plan',20)
    accepted = f.record(chosen,f.hourly_witness(facts,chosen['claims']),operation=operation)
    current = f.read(body['entitlementId'])
    source = f.capture()
    from native_read_transport import captured_input
    cut=captured_input(f.query,source)
    inputs=dict(source_business_on=cut['effective_on'],source_known_at=cut['known_at'],source_system_id=cut['system_id'],source_timeline=cut['timeline'])
    fresh = str(uuid4())
    publication = f"(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}')"
    attack(f"INSERT INTO trading.publications SELECT '{catalog}',max(publication_order)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';"
        "INSERT INTO trading.gas_transport_capacity_plans SELECT(jsonb_populate_record(NULL::trading.gas_transport_capacity_plans,to_jsonb(p)||"
        f"{literal(dict(id=fresh,business_key='synthetic_unattested_plan',revision=1,previous_id=None,source_context_id=source,source_generation=current['stateVersion'],**inputs))}"
        f"||jsonb_build_object('publication_order',{publication}))).* FROM trading.gas_transport_capacity_plans p WHERE(p.catalog_id,p.id)=('{catalog}','{operation}');",
        'gas_transport_capacity_integrity')

    forged = str(uuid4())
    request = deepcopy(chosen)
    request['claims'][0].update(claimId=identity('gas_transport_claim_v1',forged),receiptQuantity='999')
    attack("INSERT INTO trading.gas_transport_capacity_commands SELECT(jsonb_populate_record(NULL::trading.gas_transport_capacity_commands,to_jsonb(c)||"
        f"jsonb_build_object('operation_id','{forged}','outcome',jsonb_set(c.outcome,'{{changed}}','false'),'request',jsonb_set(c.request,'{{body}}',{literal(request)}),"
        f"'fact',jsonb_build_object('body',trading.gas_transport_capacity_fact_key({literal(request)}),'effectiveAt',c.request->'effectiveAt')))).* "
        f"FROM trading.gas_transport_capacity_commands c WHERE(c.catalog_id,c.operation_id)=('{catalog}','{operation}');",'gas_transport_capacity_integrity')
    # A genuine outcome cannot be attributed to a later, unrelated source cut.
    forged = str(uuid4())
    request = deepcopy(chosen)
    request['claims'][0]['claimId'] = identity('gas_transport_claim_v1',forged)
    attack("INSERT INTO trading.gas_transport_capacity_commands SELECT(jsonb_populate_record(NULL::trading.gas_transport_capacity_commands,to_jsonb(c)||"
        f"{literal(inputs)}||jsonb_build_object('operation_id','{forged}','outcome',jsonb_set(c.outcome,'{{changed}}','false'),'source_context_id','{source}',"
        f"'request',jsonb_build_object('body',{literal(request)},'contextId','{source}','effectiveAt',c.request->'effectiveAt')))).* "
        f"FROM trading.gas_transport_capacity_commands c WHERE(c.catalog_id,c.operation_id)=('{catalog}','{operation}');",'gas_transport_capacity_integrity')

    operation, replacement = f.plan(current,'synthetic_sealed_plan',10)
    witness = f.hourly_witness(current,replacement['claims'])
    extra = identity('gas_transport_claim_v1',operation,1)
    attack(f"PERFORM security.record_gas_transport_capacity('{catalog}','{operation}',{literal(replacement)},'{f.effective}',{literal(witness)},'{source}');"
        "INSERT INTO trading.gas_transport_capacity_claims SELECT(jsonb_populate_record(NULL::trading.gas_transport_capacity_claims,to_jsonb(c)||"
        f"{literal(dict(id=extra))})).* FROM trading.gas_transport_capacity_claims c WHERE(c.catalog_id,c.plan_id)=('{catalog}','{operation}');",
        'gas_transport_capacity_integrity')
    assert f.read(body['entitlementId'])['stateVersion'] == current['stateVersion']
    assert f.read(body['entitlementId'])['claims'] == current['claims']
    print('PASS future-definer no-receipt, altered terms, unrelated source lineage and same-transaction post-producer child append all refuse without changing claims.',flush=True)

    def raw_empty(operation, command, assessment, revision, publication):
        normalized=f"trading.gas_transport_capacity_fact({literal(command)})"
        effective=f"trading.gas_transport_capacity_instant('{f.effective}'::timestamptz)"
        actor="(SELECT actor_id FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user)"
        outcome=dict(entitlementId=body['entitlementId'],kind='plan',changed=True,revisionId=operation,versionId=version,revision=revision)
        return f"INSERT INTO trading.gas_transport_capacity_plans VALUES('{catalog}','{body['entitlementId']}','{operation}','{command['businessKey']}',{revision},"\
            f"'{command['expectedPreviousId']}','{version}','{command['admissionPolicy']}','{command['evidence']}','{source}',{command['expectedStateVersion']},{literal(assessment)},{actor},{publication},'{f.effective}');"\
            f"INSERT INTO trading.gas_transport_capacity_commands VALUES('{catalog}','{operation}',{actor},'{body['entitlementId']}',"\
            f"jsonb_build_object('body',{normalized},'effectiveAt',{effective},'contextId','{source}'),jsonb_build_object('body',trading.gas_transport_capacity_fact_key({normalized}),'effectiveAt',{effective}),"\
            f"{literal(outcome)}||jsonb_build_object('stateVersion',{publication}),clock_timestamp()+interval '30 seconds','{source}');"

    # A sibling revision cannot claim a predecessor absent from the reviewed cut,
    # even when both try to reuse the same otherwise legitimate publication.
    first_operation, first = f.plan(current,'synthetic_sealed_plan',0,empty=True)
    second_operation=str(uuid4())
    second={**first,'expectedPreviousId':first_operation,'evidence':'Synthetic same-publication sibling'}
    idle=f.hourly_witness(current,[])
    attack(f"PERFORM security.record_gas_transport_capacity('{catalog}','{first_operation}',{literal(first)},'{f.effective}',{literal(idle)},'{source}');"+
        raw_empty(second_operation,second,idle,3,publication),'gas_transport_capacity_integrity')
    assert f.read(body['entitlementId'])['stateVersion']==current['stateVersion']
    print('PASS same-publication sibling plan revision cannot reuse an uncertified predecessor/source generation.',flush=True)
    # Emptying a plan once is a real revision. A new publication of that exact
    # fact must acknowledge it, even if an incomplete producer tries changed=true.
    empty_operation, empty = f.plan(current,'synthetic_sealed_plan',0,empty=True)
    emptied=f.record(empty,idle,operation=empty_operation)
    after_empty=f.read(body['entitlementId']);source=f.capture()
    duplicate_operation, duplicate=f.plan(after_empty,'synthetic_sealed_plan',0,empty=True)
    attack(f"INSERT INTO trading.publications SELECT '{catalog}',max(publication_order)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';"+
        raw_empty(duplicate_operation,duplicate,idle,3,publication),'gas_transport_capacity_integrity')
    assert f.read(body['entitlementId'])['stateVersion']==after_empty['stateVersion']
    assert f.record(duplicate,operation=duplicate_operation)=={**emptied,'changed':False}
    print('PASS a changed=true duplicate fact cannot publish a second revision; the public semantic retry acknowledges its first empty-plan outcome.',flush=True)
