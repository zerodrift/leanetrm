"""Adversarial close visibility in an owned synthetic database, with rollback-only faults."""
import json
from uuid import UUID,uuid4


def run_checks(query, claim, profile):
    run,worker=str(UUID(claim['id'])),str(UUID(claim['workerId']))
    fence=int(claim['fence'])
    catalog,close,context=(str(UUID(profile[k])) for k in ('catalog','close','context'))
    selected=json.loads(query(f"SELECT jsonb_build_object('observation',p.observation_id,'definition',p.definition_revision_id,'event',e.event_order) FROM market_data.market_close_pins p JOIN market_data.knowledge_events e ON(e.catalog_id,e.observation_id)=(p.catalog_id,p.observation_id) WHERE(p.catalog_id,p.close_id)=('{catalog}','{close}') ORDER BY e.event_order DESC LIMIT 1;"))
    observation,definition=str(UUID(selected['observation'])),str(UUID(selected['definition']))
    event=int(selected['event'])
    instant=f"(SELECT known_at FROM market_data.temporal_contexts WHERE(catalog_id,id)=('{catalog}','{context}'))"
    next_day=f"(SELECT ((effective_on+1)::timestamp AT TIME ZONE 'UTC') FROM market_data.temporal_contexts WHERE(catalog_id,id)=('{catalog}','{context}'))"
    receipt=f"catalog_id='{catalog}' AND event_order={event}"
    faults={
        'missing one pin event':f"DELETE FROM market_data.knowledge_events WHERE {receipt};",
        'missing one pin receipt':f"DELETE FROM market_data.market_knowledge_receipts WHERE {receipt};",
        'different publication transaction':f"UPDATE market_data.market_knowledge_receipts SET publication_xid=pg_current_xact_id() WHERE {receipt};",
        'pin outside certified prefix':f"UPDATE market_data.market_contexts SET known_through={event-1} WHERE(catalog_id,context_id)=('{catalog}','{context}');",
        'later-known pin':f"UPDATE market_data.market_knowledge_receipts SET known_from={instant}+interval '1 second' WHERE {receipt};",
        'observation at next-day equality':f"UPDATE market_data.observations SET observed_at={next_day},received_at=greatest(received_at,{next_day}) WHERE(catalog_id,id)=('{catalog}','{observation}');",
        'ineffective definition':f"UPDATE market_data.revision_periods SET effective_to=(SELECT effective_on FROM market_data.temporal_contexts WHERE(catalog_id,id)=('{catalog}','{context}')) WHERE(catalog_id,revision_id)=('{catalog}','{definition}');",
        'later-known close':f"UPDATE market_data.market_close_receipts SET known_from={instant}+interval '1 second' WHERE(catalog_id,publication_order) IN(SELECT catalog_id,publication_order FROM market_data.market_closes WHERE(catalog_id,id)=('{catalog}','{close}'));",
        'wrong bound catalog':"UPDATE security.request_contexts SET market_catalog_id='ffffffff-ffff-ffff-ffff-ffffffffffff' WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned();",
    }
    identity=query(f"SELECT format('%L,%L,%L,%L::uuid,%L::uuid',i.issuer,i.subject,i.login_kind,j.tenant_id,j.workspace_id) FROM automation.runs r JOIN automation.jobs j ON j.id=r.job_id JOIN security.identity_bindings i ON i.principal_id=j.owner_id WHERE r.id='{run}' AND i.login_kind='human_interactive';")
    def issue(login):
        if login=='pilot_fixture_runtime':
            token=query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_pilot_context('{run}',{fence},'{worker}');")
        else:
            token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context({identity},NULL,'preview_runtime',60);")
        return str(UUID(token))
    for login in ('pilot_fixture_runtime','preview_runtime'):
        for name,change in faults.items():
            token=issue(login)
            # Fault injection belongs only to this disposable owner's transaction.
            # Restore triggers before reading as the actual runtime; always roll back.
            result=query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION {login};SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}','{context}');"
                f"SELECT security.market_close_visible('{catalog}','{close}');RESET SESSION AUTHORIZATION;SET LOCAL session_replication_role=replica;"+change+
                f"SET LOCAL session_replication_role=origin;SET LOCAL SESSION AUTHORIZATION {login};SELECT security.market_close_visible('{catalog}','{close}');ROLLBACK;").splitlines()
            assert result==['t','f'],(login,name,result)
        print(f'PASS close pin visibility: {login} refuses whole-close missing, mismatched, later-known and out-of-effective-time evidence',flush=True)

        token=issue(login)
        result=query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION {login};SELECT security.use_context('{token}');RESET SESSION AUTHORIZATION;SET LOCAL session_replication_role=replica;DELETE FROM market_data.market_knowledge_receipts WHERE {receipt};SET LOCAL session_replication_role=origin;SET LOCAL SESSION AUTHORIZATION {login};SELECT security.market_close_visible('{catalog}','{close}');ROLLBACK;")
        assert result=='t',(login,'current mode must not acquire historical receipt requirements',result)
        correction=str(uuid4());token=issue(login)
        result=query(f"BEGIN;INSERT INTO market_data.observations SELECT (jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object('id','{correction}','source_revision_id','synthetic_later_correction','corrects_id','{observation}','received_at',clock_timestamp(),'value','99'))).* FROM market_data.observations o WHERE(o.catalog_id,o.id)=('{catalog}','{observation}');SET LOCAL SESSION AUTHORIZATION {login};SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}','{context}');SELECT security.market_fact_visible('{catalog}','{definition}','{correction}');SELECT security.market_close_visible('{catalog}','{close}');ROLLBACK;").splitlines()
        assert result==['f','t'],(login,'later correction must not replace an original pin',result)
        print(f'PASS close pin visibility: {login} retains current-mode reads and original close pins after a later unselected correction',flush=True)
