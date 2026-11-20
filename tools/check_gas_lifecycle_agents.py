"""Opt-in delegated gas operations retain the ordinary source and write contract."""
import json
import os
import shutil
import subprocess
from pathlib import Path
from uuid import uuid4
from check_reference_editing import main
from check_gas_owned_reconciliation import checks as source_checks
from gas_lifecycle_agent_upgrade import checks as upgrade_checks
from preview_fixture import fixture as base_fixture


def fixture(root):
    reference,market,security,manifest=base_fixture(root)
    manifest['identityIssuer']='https://preview.invalid'
    return reference,market,security,manifest


def checks(query,manifest):
    metadata=json.loads(query("""SELECT jsonb_agg(jsonb_build_object('signature',p.oid::regprocedure::text,'sourceHash',md5(p.prosrc),
      'language',l.lanname,'owner',r.rolname,'securityDefiner',p.prosecdef,'config',p.proconfig,'volatility',p.provolatile,
      'parallel',p.proparallel,'strict',p.proisstrict,'leakproof',p.proleakproof,'kind',p.prokind,'returnsSet',p.proretset,
      'variadic',p.provariadic,'argNames',p.proargnames,'allArgTypes',p.proallargtypes,'argModes',p.proargmodes,
      'argDefaultsCount',p.pronargdefaults,'argDefaults',p.proargdefaults::text,'acl',p.proacl::text) ORDER BY p.oid::regprocedure::text)
      FROM pg_proc p JOIN pg_language l ON l.oid=p.prolang JOIN pg_roles r ON r.oid=p.proowner
      WHERE p.oid=ANY(ARRAY['security.guard_agent_roles()'::regprocedure,'security.administration_directory()'::regprocedure,
        'security.apply_administration(jsonb)'::regprocedure]);"""))
    host=Path(query('SHOW unix_socket_directories;'))
    (host/'gas-agent-predecessors.json').write_text(json.dumps(metadata,indent=2))
    print('Owned gas agent predecessor metadata: '+json.dumps(metadata,separators=(',',':')),flush=True)
    actual=json.loads(query("SELECT coalesce(jsonb_agg(p.permission ORDER BY p.permission),'[]') FROM security.roles r "
        "JOIN security.role_permissions p ON p.role_id=r.id WHERE r.code='gas_lifecycle_operator';"))
    assert actual==['ReadGasOwnedHoldings','ReadMarketData','ReadTrades','RecordGasOwnedHoldings'],\
        'The opt-in assignable agent role for the four existing gas permissions is missing or broadened.'
    assert query("SELECT count(*) FROM security.role_grants g JOIN security.roles r ON r.id=g.role_id WHERE r.code='gas_lifecycle_operator';")=='0',\
        'The opt-in role must ship without any assigned principal.'
    source=source_checks(query,manifest)
    catalog,tenant,workspace=(manifest[key] for key in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer'];role='a0000000-0000-0000-0000-000000000010'
    agent,readonly_agent,delegation,readonly,revoked,foreign,foreign_workspace,delegator_role,service=(str(uuid4()) for _ in range(9))
    trader=query(f"SELECT principal_id FROM security.identity_bindings WHERE(issuer,subject)=('{issuer}','trader');")
    query(f"""
      INSERT INTO security.principals VALUES('{agent}','agent',true),('{readonly_agent}','agent',true),('{service}','service',true);
      INSERT INTO security.identity_bindings VALUES('{issuer}','synthetic_gas_lifecycle_agent','{agent}','agent_credential'),
        ('{issuer}','synthetic_gas_lifecycle_reader','{readonly_agent}','agent_credential');
      INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{agent}','synthetic_gas_lifecycle_agent','Synthetic gas agent',true,1),
        ('{tenant}','{workspace}','{readonly_agent}','synthetic_gas_lifecycle_reader','Synthetic gas reader',true,1),
        ('{tenant}','{workspace}','{service}','synthetic_gas_lifecycle_service','Synthetic gas service',true,1);
      INSERT INTO security.roles(id,code,name) VALUES('{delegator_role}','synthetic_gas_delegator','Synthetic gas delegator');
      INSERT INTO security.role_permissions VALUES('{delegator_role}','CreateDelegations');
      INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{trader}','{delegator_role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false),
        (gen_random_uuid(),'{agent}','{role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false),
        (gen_random_uuid(),'{readonly_agent}','{role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false);
      INSERT INTO security.workspaces VALUES('{tenant}','{foreign_workspace}','synthetic_foreign_gas_agents');
      INSERT INTO security.agent_delegations SELECT id::uuid,CASE WHEN id='{readonly}' THEN '{readonly_agent}'::uuid ELSE '{agent}'::uuid END,
        '{trader}','{tenant}',CASE WHEN id='{foreign}' THEN '{foreign_workspace}'::uuid ELSE '{workspace}'::uuid END,
        clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',id='{revoked}'
        FROM unnest(ARRAY['{delegation}','{readonly}','{revoked}','{foreign}'])id;
      INSERT INTO security.delegation_permissions SELECT id::uuid,permission FROM unnest(ARRAY['{delegation}','{readonly}','{revoked}','{foreign}'])id
        CROSS JOIN unnest(ARRAY['ReadTrades','ReadMarketData','ReadGasOwnedHoldings'])permission;
      INSERT INTO security.delegation_permissions SELECT id::uuid,'RecordGasOwnedHoldings' FROM unnest(ARRAY['{delegation}','{revoked}','{foreign}'])id;
      """)
    for principal,selected_role in ((trader,role),(service,role),(agent,'a0000000-0000-0000-0000-000000000002')):
        try:query(f"BEGIN;INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{principal}','{selected_role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false);ROLLBACK;")
        except RuntimeError as error:assert 'agent_managed' in str(error),str(error)
        else:raise AssertionError('The native role boundary accepted a human/service gas operator or an ordinary role on an agent.')
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{issuer}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return call(f"SELECT security.resolve_operation_context('{catalog}','2020-12-20',NULL);")
    policy=str(uuid4());account=source['account'];at=source['at']
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action='gas.owned.reconcile_quantity',mode='review',fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
    call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','bind_policy','{account}',{literal(dict(action='gas.owned.reconcile_quantity',policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))},'{at}','{capture()}',NULL,NULL,false);")
    packet=host/'gas-lifecycle-agent.json';operation=str(uuid4())
    packet.write_text(json.dumps(dict(manifest=manifest,host=str(host),at=at,contextId=capture(),otherContextId=source['before'],accountId=account,
        operationId=operation,policyId=policy,selection=source['body'],principal='synthetic_gas_lifecycle_agent',readonlyPrincipal='synthetic_gas_lifecycle_reader',
        delegation=delegation,readonlyDelegation=readonly,revokedDelegation=revoked,foreignDelegation=foreign)));packet.chmod(0o600)
    root=Path(__file__).resolve().parents[1]
    (host/'manifest.json').write_text(json.dumps(manifest));(host/'manifest.json').chmod(0o600)
    shutil.copyfile(root/'deploy/process-limits.example.json',host/'process-limits.json')
    environment={key:value for key,value in os.environ.items() if not key.startswith(('PG','OPENETRM_'))}
    environment['OPENETRM_NATIVE_CHECK_INPUTS']=os.environ['OPENETRM_NATIVE_CHECK_INPUTS']
    for name,login in (('OPENETRM_PREVIEW_RUNTIME','preview_runtime'),('OPENETRM_PREVIEW_AUTHENTICATOR','preview_authenticator')):
        environment[name]=f'Host={host};Port=55439;Database=postgres;Username={login};SSL Mode=Disable;Timeout=5;Command Timeout=5;Maximum Pool Size=2'
    command=[os.environ.get('DOTNET','dotnet'),str(root/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0/OpenEtrm.Api.Checks.dll'),'--gas-lifecycle-agent',str(packet)]
    subprocess.run(command,cwd=root,env=environment,check=True)
    assert query(f"SELECT count(*) FROM trading.gas_owned_quantity_corrections WHERE(catalog_id,operation_id)=('{catalog}','{operation}');")=='1'
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    retained=json.loads(packet.read_text())
    query(f"UPDATE security.role_grants SET revoked=true WHERE(principal_id,role_id,tenant_id,workspace_id)=('{agent}','{role}','{tenant}','{workspace}');")
    retained['deniedReplay']='agent-role revocation';packet.write_text(json.dumps(retained))
    subprocess.run(command,cwd=root,env=environment,check=True)
    query(f"UPDATE security.role_grants SET revoked=false WHERE(principal_id,role_id,tenant_id,workspace_id)=('{agent}','{role}','{tenant}','{workspace}');"
        f"DELETE FROM security.role_permissions p USING security.role_grants g WHERE p.role_id=g.role_id AND g.principal_id='{trader}' AND p.permission='RecordGasOwnedHoldings';")
    retained['deniedReplay']='delegator permission';packet.write_text(json.dumps(retained))
    subprocess.run(command,cwd=root,env=environment,check=True)
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication


if __name__=='__main__':main(checks=checks,before_migration=upgrade_checks,fixture_factory=fixture)
