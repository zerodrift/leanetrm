"""Settings upgrades preserve prior facts and refuse unreviewed helper drift."""
import re
from uuid import uuid4
from assistant_context import check_guard, migration_body


def run_checks(query, migration):
    signatures=re.findall(r"\('((?:assistant|security)\.[^']+)','[0-9a-f]{64}',ARRAY",migration.read_text())
    check_guard(query,migration.read_text(),migration.name[:4],'operating_policy_upgrade',tuple(
        change for signature in signatures for change in (
            (f"ALTER FUNCTION {signature} SET work_mem='64kB';",signature),
            (f'GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;',signature))))
    tenant,workspace,human,principal,provider,model,agent,delegation,conversation,turn=(str(uuid4()) for _ in range(10))
    session=uuid4().hex+uuid4().hex
    initialize=f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');" if migration.name.endswith('_access_policy_consumers.sql') else ''
    columns=['model_id','version','provider_id','vendor','mapping','output_limit','reasoning_efforts','accepted_served','recorded_by','recorded_at','identifier_policy']
    values=[f"'{model}'",'1',f"'{provider}'","'xai'","'xai_responses_v1'",'1024',"ARRAY['low']","ARRAY[]::text[]",f"'{human}'","'2020-01-01'","'fixed'"]
    for fact in ('output_limit','reasoning','identifier_stability','accepted_served','tools','streaming'):
        columns += [fact+'_source',fact+'_checked_on'];values += ["'https://example.invalid/synthetic-upgrade'","'2020-01-01'"]
    seed=f"""
INSERT INTO security.tenants VALUES('{tenant}','synthetic_settings_upgrade');
INSERT INTO security.workspaces VALUES('{tenant}','{workspace}','synthetic_settings_upgrade');
INSERT INTO security.principals VALUES('{human}','human',true),('{principal}','agent',true);
INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{human}','synthetic_human','Synthetic human',true,1),
 ('{tenant}','{workspace}','{principal}','synthetic_agent','Synthetic agent',true,1);
{initialize}
INSERT INTO assistant.providers(id,tenant_id,workspace_id,vendor,version) VALUES('{provider}','{tenant}','{workspace}','xai',1);
INSERT INTO assistant.models VALUES('{model}','{tenant}','{workspace}','{provider}','model-synthetic-upgrade','Synthetic upgrade model',1);
INSERT INTO assistant.model_versions({','.join(columns)}) VALUES({','.join(values)});
INSERT INTO assistant.agents VALUES('{agent}','{tenant}','{workspace}','{principal}','Synthetic upgrade agent',1,NULL,NULL);
INSERT INTO assistant.agent_versions(agent_id,version,description,instructions,model_id,model_version,reasoning,caching,tools,
 max_tool_steps,max_input_tokens,max_output_tokens,max_elapsed_seconds,max_retries,grant_days,saved_by,saved_at,max_read_seconds,history_turns)
 VALUES('{agent}',1,'Synthetic description','Synthetic instructions','{model}',1,'low',false,ARRAY['read_current_page'],1,1000,128,30,0,28,'{human}','2020-01-01',1,0);
INSERT INTO security.agent_delegations VALUES('{delegation}','{principal}','{human}','{tenant}','{workspace}','2020-01-01','2020-02-01',true);
INSERT INTO assistant.grant_audit VALUES('{tenant}','{workspace}','{uuid4()}','{delegation}','granted','{human}','{agent}',1,'{model}',1,ARRAY[]::text[],'2020-02-01','{human}','2020-01-01');
INSERT INTO assistant.conversations VALUES('{conversation}','{tenant}','{workspace}','{human}','api','{agent}','2020-01-01',0);
INSERT INTO assistant.turns VALUES('{turn}','{conversation}','{tenant}','{workspace}',1,'{human}','{uuid4()}','{agent}',1,false,'{delegation}',
 'Synthetic prior question','{{"page":"jobs","selection":{{}}}}','{uuid4()}','operational',NULL,NULL,NULL,'2020-01-01',NULL,'2020-01-01',
 ARRAY[]::uuid[],'turns_v11','completed',NULL,NULL,'Synthetic prior answer',false,1,'{'0'*64}','{session}','Synthetic human',
 '2020-01-01 00:30Z','2020-01-01','2020-01-01',NULL,NULL,'2020-01-01 00:01Z');
INSERT INTO security.hosted_accounts(principal_id,issuer,tenant_id,workspace_id,login,password_hash,credential_version)
 VALUES('{human}','https://example.invalid','{tenant}','{workspace}','synthetic_upgrade','synthetic_hash',1);
INSERT INTO security.hosted_sessions VALUES('{session}','{human}',1,'2020-01-01','2020-01-01 00:30Z',NULL);
"""
    state="jsonb_build_object("+','.join(f"'{table}',(SELECT jsonb_agg(to_jsonb(x)) FROM {table} x)" for table in (
        'assistant.turns','assistant.agent_versions','assistant.grant_audit','security.agent_delegations','security.hosted_sessions'))+")"
    no_fake_lineage=(f"IF EXISTS(SELECT 1 FROM assistant.turn_operating_pins WHERE turn_id='{turn}') THEN RAISE EXCEPTION 'Invented prior turn policy';END IF;"
        if migration.name.endswith('_operating_policies.sql') else f"IF EXISTS(SELECT 1 FROM security.hosted_session_policy_pins WHERE token_digest='{session}') THEN RAISE EXCEPTION 'Invented prior session policy';END IF;")
    query('BEGIN;'+seed+f'CREATE TEMP TABLE prior_settings_facts AS SELECT {state} value;'+migration_body(migration.read_text())+
        f"DO $$BEGIN IF {state} IS DISTINCT FROM(SELECT value FROM prior_settings_facts) THEN RAISE EXCEPTION 'Settings upgrade changed prior facts';END IF;{no_fake_lineage}END$$;ROLLBACK;")
    print('PASS '+migration.name[:4]+' preserves complete prior turns, agent versions, grants and session identity/expiry without invented policy lineage',flush=True)


def before_migration(query,migration):
    if migration.name.endswith(('_operating_policies.sql','_access_policy_consumers.sql')):run_checks(query,migration)
