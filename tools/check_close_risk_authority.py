"""Actual worker authority checks retain fresh decisions without duplicate work."""
from uuid import UUID


def run_checks(query, claim):
    run = str(UUID(claim['id']))
    worker = str(UUID(claim['workerId']))
    fence = int(claim['fence'])
    stats = "SELECT 'CALLS|'||coalesce(sum(calls) FILTER(WHERE funcname='pilot_context_live'),0)||'|'||coalesce(sum(calls) FILTER(WHERE funcname='close_risk_authorized'),0) FROM pg_stat_xact_user_functions WHERE schemaname='automation';"
    for mode in ('force_custom_plan', 'force_generic_plan'):
        token = str(UUID(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_pilot_context('{run}',{fence},'{worker}');")))
        result = query("SET track_functions='all';BEGIN;"+f"SET LOCAL plan_cache_mode={mode};SET LOCAL SESSION AUTHORIZATION pilot_fixture_runtime;SELECT security.use_context('{token}');"+
                       stats+"SELECT 'ALLOWED|'||security.has_permission('ReadMarketData');"+stats+"SELECT 'ALLOWED|'||security.has_permission('ReadMarketData');"+stats+'ROLLBACK;').splitlines()
        calls = [list(map(int, row.split('|')[1:])) for row in result if row.startswith('CALLS|')]
        deltas = [[b-a for a,b in zip(start,end)] for start,end in zip(calls,calls[1:])]
        assert result.count('ALLOWED|true') == 2 and deltas == [[1,1],[1,1]], (mode, deltas, result)
        print(f'PASS close-risk authority: {mode} performs one fresh complete authorization per live-context check', flush=True)

        owner = f"(SELECT owner_id FROM automation.jobs WHERE id=(SELECT job_id FROM automation.runs WHERE id='{run}'))"
        service = f"(SELECT principal_id FROM automation.workers WHERE login=(SELECT worker_login FROM automation.runs WHERE id='{run}'))"
        changes = {
            'owner grant revoked': f"UPDATE security.role_grants SET revoked=true WHERE principal_id={owner};",
            'service grant revoked': f"UPDATE security.role_grants SET revoked=true WHERE principal_id={service};",
            'owner disabled': f"UPDATE security.principals SET enabled=false WHERE id={owner};",
            'service disabled': f"UPDATE security.principals SET enabled=false WHERE id={service};",
            'scenario permission removed': "DELETE FROM security.role_permissions WHERE permission='RunScenario';",
            'lease expired': f"UPDATE automation.runs SET lease_until=clock_timestamp()-interval '1 second' WHERE id='{run}';",
            'overall deadline expired': f"UPDATE automation.runs SET deadline_at=clock_timestamp()-interval '1 second' WHERE id='{run}';",
            'ownership replaced': f"UPDATE automation.runs SET fence=fence+1 WHERE id='{run}';",
            'cancelled': f"UPDATE automation.runs SET state='cancellation_requested' WHERE id='{run}';",
        }
        for name,change in changes.items():
            token = str(UUID(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_pilot_context('{run}',{fence},'{worker}');")))
            result = query(f"BEGIN;SET LOCAL plan_cache_mode={mode};SET LOCAL SESSION AUTHORIZATION pilot_fixture_runtime;SELECT security.use_context('{token}');"
                           "SELECT security.has_permission('ReadMarketData');RESET SESSION AUTHORIZATION;"+change+
                           "SET LOCAL SESSION AUTHORIZATION pilot_fixture_runtime;SELECT security.has_permission('ReadMarketData');ROLLBACK;").splitlines()
            assert result == ['t','f'], (mode,name,result)
        print(f'PASS close-risk authority: {mode} rechecks revoked authority, expiry, cancellation and replaced ownership in the same bound transaction', flush=True)
