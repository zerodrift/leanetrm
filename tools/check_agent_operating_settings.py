"""Normal Agent Settings publishes a narrow gas role before an explicit delegated financial operation."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
from uuid import uuid4

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from check_reference_editing import main as native_main
from check_gas_owned_reconciliation import checks as source_checks
from check_gas_lifecycle_agents import fixture
from owned_lifecycle_browser import run as browser_run,build_assets
from agent_operating_role_upgrade import checks as upgrade_checks


def checks(query,manifest,dotnet,browser,children):
    source=source_checks(query,manifest)
    catalog,tenant,workspace=(manifest[key] for key in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer'];role='a0000000-0000-0000-0000-000000000010'
    trader=query(f"SELECT principal_id FROM security.identity_bindings WHERE(issuer,subject)=('{issuer}','trader');")
    delegator_role=str(uuid4())
    query(f"INSERT INTO security.roles(id,code,name) VALUES('{delegator_role}','synthetic_explicit_gas_delegator','Synthetic gas delegator');"
        "DELETE FROM security.role_permissions p USING security.roles r WHERE p.role_id=r.id AND r.code='trader' "
        "AND p.permission IN('ReadMarketData','ReadTrades','ReadGasOwnedHoldings','RecordGasOwnedHoldings');"
        f"INSERT INTO security.role_permissions SELECT '{delegator_role}',p FROM unnest(ARRAY['ReadMarketData','ReadTrades','ReadGasOwnedHoldings','RecordGasOwnedHoldings'])p;"
        f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{trader}','{delegator_role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false);")
    # Generic workspace tools need only UseAssistantWorkspace on the allowed Trader role;
    # the actual human's separately assigned gas permissions authorize the saved operating role.
    stamp=uuid4().hex[:12]
    setup=dict(manager='synthetic_role_manager_'+stamp,reader='synthetic_role_reader_'+stamp,name='Synthetic gas operating '+stamp)
    browser_run(query,manifest,dotnet,browser,children,setup,'tests/browser/agent-operating-roles.mjs',worker_enabled=False,timeout=240,
        artifacts=['agent-operating-role-desktop.png','agent-operating-role-mobile.png'])
    host=Path(query('SHOW unix_socket_directories;'))
    configured=json.loads((host/'agent-operating-role-result.json').read_text())
    agent_id=configured['agentId'];agent=query(f"SELECT principal_id FROM assistant.agents WHERE id='{agent_id}';")
    actual=json.loads(query(f"SELECT jsonb_agg(r.code ORDER BY r.code) FROM security.role_grants g JOIN security.roles r ON r.id=g.role_id "
        f"WHERE(g.principal_id,g.tenant_id,g.workspace_id)=('{agent}','{tenant}','{workspace}') AND NOT g.revoked AND g.granted_from<=clock_timestamp() AND clock_timestamp()<g.granted_to;"))
    assert actual==['agent_reader','gas_lifecycle_operator'],actual
    versions=json.loads(query(f"SELECT jsonb_agg(coalesce(r.code,'none') ORDER BY v.version) FROM assistant.agent_versions v "
        f"LEFT JOIN security.roles r ON r.id=v.operating_role_id WHERE v.agent_id='{agent_id}';"))
    assert versions==['gas_lifecycle_operator','none','gas_lifecycle_operator'],versions
    assert query(f"SELECT count(*) FROM security.agent_delegations WHERE agent_id='{agent}' AND NOT revoked;")=='1'
    readonly_agent,readonly,foreign,foreign_workspace=(str(uuid4()) for _ in range(4))
    revoked=configured['retiredGasDelegation']
    assert query(f"SELECT revoked FROM security.agent_delegations WHERE id='{revoked}';")=='t'
    query(f"INSERT INTO security.principals VALUES('{readonly_agent}','agent',true);"
        f"INSERT INTO security.identity_bindings VALUES('{issuer}','synthetic_configured_gas_reader','{readonly_agent}','agent_credential');"
        f"INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{readonly_agent}','synthetic_configured_gas_reader','Synthetic configured reader',true,1);"
        f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{readonly_agent}','{role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false);"
        f"INSERT INTO security.workspaces VALUES('{tenant}','{foreign_workspace}','synthetic_foreign_configured_gas');"
        f"INSERT INTO security.agent_delegations SELECT id::uuid,CASE WHEN id='{readonly}' THEN '{readonly_agent}'::uuid ELSE '{agent}'::uuid END,"
        f"'{trader}','{tenant}',CASE WHEN id='{foreign}' THEN '{foreign_workspace}'::uuid ELSE '{workspace}'::uuid END,"
        f"clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false FROM unnest(ARRAY['{readonly}','{foreign}'])id;"
        f"INSERT INTO security.delegation_permissions SELECT id::uuid,p FROM unnest(ARRAY['{readonly}','{foreign}'])id "
        "CROSS JOIN unnest(ARRAY['ReadMarketData','ReadTrades','ReadGasOwnedHoldings'])p;"
        f"INSERT INTO security.delegation_permissions VALUES('{foreign}','RecordGasOwnedHoldings');")
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{issuer}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return call(f"SELECT security.capture_date_context('{catalog}','2020-12-20',NULL);")
    policy=str(uuid4());account=source['account'];at=source['at']
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action='gas.owned.reconcile_quantity',mode='review',fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
    call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','bind_policy','{account}',{literal(dict(action='gas.owned.reconcile_quantity',policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))},'{at}','{capture()}',NULL,NULL,false);")
    packet=host/'configured-gas-lifecycle-agent.json';operation=str(uuid4())
    packet.write_text(json.dumps(dict(manifest=manifest,host=str(host),at=at,contextId=capture(),otherContextId=source['before'],accountId=account,
        operationId=operation,policyId=policy,selection=source['body'],principal=agent,readonlyPrincipal='synthetic_configured_gas_reader',
        delegation=configured['delegation'],readonlyDelegation=readonly,revokedDelegation=revoked,foreignDelegation=foreign)));packet.chmod(0o600)
    root=Path(__file__).resolve().parents[1]
    environment={key:value for key,value in os.environ.items() if not key.startswith(('PG','OPENETRM_'))}
    for name,login in (('OPENETRM_PREVIEW_RUNTIME','preview_runtime'),('OPENETRM_PREVIEW_AUTHENTICATOR','preview_authenticator')):
        environment[name]=f'Host={host};Port=55439;Database=postgres;Username={login};SSL Mode=Disable;Timeout=5;Command Timeout=5;Maximum Pool Size=2'
    command=[str(dotnet),str(root/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0/OpenEtrm.Api.Checks.dll'),'--configured-gas-lifecycle-agent',str(packet)]
    subprocess.run(command,cwd=root,env=environment,check=True)
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    retained=json.loads(packet.read_text())
    query(f"UPDATE security.role_grants SET revoked=true WHERE(principal_id,role_id,tenant_id,workspace_id)=('{agent}','{role}','{tenant}','{workspace}');")
    retained['deniedReplay']='agent operating-role revocation';packet.write_text(json.dumps(retained));subprocess.run(command,cwd=root,env=environment,check=True)
    query(f"UPDATE security.role_grants SET revoked=false WHERE(principal_id,role_id,tenant_id,workspace_id)=('{agent}','{role}','{tenant}','{workspace}');"
        f"DELETE FROM security.role_permissions WHERE role_id='{delegator_role}' AND permission='RecordGasOwnedHoldings';")
    retained['deniedReplay']='delegator gas permission';packet.write_text(json.dumps(retained));subprocess.run(command,cwd=root,env=environment,check=True)
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    print('PASS normal Agent Settings config/save/publish, frozen grants, explicit delegator intersection and actual manual gas review/execute/retained authority',flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin',type=Path,required=True);parser.add_argument('--dotnet',type=Path,required=True);parser.add_argument('--browser',type=Path,required=True)
    parser.add_argument('--no-build',action='store_true',help='Use the exact assets built before the owned qualifier.')
    args=parser.parse_args();os.environ['DOTNET']=str(args.dotnet.resolve());children=[]
    if not args.no_build:build_assets(args.dotnet.resolve())
    native_main(checks=lambda q,m:checks(q,m,args.dotnet.resolve(),args.browser.resolve(),children),fixture_factory=fixture,before_migration=upgrade_checks,
        arguments=['--pg-bin',str(args.pg_bin.resolve())],cleanup_ready=lambda:not children)
