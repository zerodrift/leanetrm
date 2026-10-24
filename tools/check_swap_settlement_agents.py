"""An explicitly delegated synthetic agent uses the same managed settlement operations."""
from uuid import uuid4
from preview_fixture import TENANT, WORKSPACE, identity


def prepare(query, fixture):
    agent, readonly_agent, delegation, readonly, revoked, delegator_role=(str(uuid4()) for _ in range(6))
    role='a0000000-0000-0000-0000-000000000008'
    trader=identity('c',5)
    query(f"""
    INSERT INTO security.principals VALUES('{agent}','agent',true),('{readonly_agent}','agent',true);
    INSERT INTO security.identity_bindings VALUES('https://preview.invalid','synthetic_settlement_agent','{agent}','agent_credential'),('https://preview.invalid','synthetic_settlement_readonly','{readonly_agent}','agent_credential');
    INSERT INTO security.workspace_memberships VALUES('{TENANT}','{WORKSPACE}','{agent}','synthetic_settlement_agent','Synthetic settlement agent',true,1),('{TENANT}','{WORKSPACE}','{readonly_agent}','synthetic_settlement_readonly','Synthetic settlement reader',true,1);
    INSERT INTO security.roles(id,code,name) VALUES('{delegator_role}','synthetic_settlement_delegator','Synthetic settlement delegator');
    INSERT INTO security.role_permissions VALUES('{delegator_role}','CreateDelegations');
    INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{agent}','{role}','{TENANT}','{WORKSPACE}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false);
    INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{readonly_agent}','{role}','{TENANT}','{WORKSPACE}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false);
    INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{trader}','{delegator_role}','{TENANT}','{WORKSPACE}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false);
    INSERT INTO security.agent_delegations SELECT id::uuid,CASE WHEN id='{readonly}' THEN '{readonly_agent}'::uuid ELSE '{agent}'::uuid END,'{trader}','{TENANT}','{WORKSPACE}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',id='{revoked}'
      FROM unnest(ARRAY['{delegation}','{readonly}','{revoked}']) id;
    INSERT INTO security.delegation_permissions SELECT id::uuid,permission FROM unnest(ARRAY['{delegation}','{readonly}','{revoked}']) id
      CROSS JOIN unnest(ARRAY['ReadTrades','ReadMarketData']) permission;
    INSERT INTO security.delegation_permissions VALUES('{delegation}','AmendTrade'),('{revoked}','AmendTrade');
    """)
    return {**fixture,'principal':'synthetic_settlement_agent','delegation':delegation,'readonlyDelegation':readonly,'revokedDelegation':revoked}
