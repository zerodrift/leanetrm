"""Actual worker authority checks retain fresh decisions without duplicate work."""
from uuid import UUID
import json


def run_checks(query, claim):
    run = str(UUID(claim['id']))
    worker = str(UUID(claim['workerId']))
    fence = int(claim['fence'])
    profile=json.loads(query(f"SELECT jsonb_build_object('catalog',p.catalog_id,'close',p.close_id,'context',p.context_id) FROM automation.runs r JOIN automation.jobs j ON j.id=r.job_id JOIN automation.close_risk_profiles p ON p.id=j.close_risk_profile_id WHERE r.id='{run}';"))
    catalog,close,context=(str(UUID(profile[k])) for k in ('catalog','close','context'))
    stats = "SELECT 'CALLS|'||coalesce(sum(calls) FILTER(WHERE funcname='pilot_context_live'),0)||'|'||coalesce(sum(calls) FILTER(WHERE funcname='close_risk_authorized'),0) FROM pg_stat_xact_user_functions WHERE schemaname='automation';"
    for mode in ('force_custom_plan', 'force_generic_plan'):
        token = str(UUID(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_pilot_context('{run}',{fence},'{worker}');")))
        result = query("SET track_functions='all';BEGIN;"+f"SET LOCAL plan_cache_mode={mode};SET LOCAL SESSION AUTHORIZATION pilot_fixture_runtime;SELECT security.use_context('{token}');"+
                       stats+"SELECT 'ALLOWED|'||security.has_permission('ReadMarketData');"+stats+"SELECT 'ALLOWED|'||security.has_permission('ReadMarketData');"+stats+'ROLLBACK;').splitlines()
        calls = [list(map(int, row.split('|')[1:])) for row in result if row.startswith('CALLS|')]
        deltas = [[b-a for a,b in zip(start,end)] for start,end in zip(calls,calls[1:])]
        assert result.count('ALLOWED|true') == 2 and deltas == [[1,1],[1,1]], (mode, deltas, result)
        print(f'PASS close-risk authority: {mode} performs one fresh complete authorization per live-context check', flush=True)

        token = str(UUID(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_pilot_context('{run}',{fence},'{worker}');")))
        result=query("SET track_functions='all';BEGIN;"+f"SET LOCAL plan_cache_mode={mode};SET LOCAL SESSION AUTHORIZATION pilot_fixture_runtime;SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}','{context}');"+
                     stats+f"SELECT 'VISIBLE|'||security.market_close_visible('{catalog}','{close}');"+stats+'ROLLBACK;').splitlines()
        calls=[list(map(int,row.split('|')[1:])) for row in result if row.startswith('CALLS|')]
        authorizations=calls[1][1]-calls[0][1]
        assert 'VISIBLE|true' in result and 2<=authorizations<=12,(mode,authorizations,result)
        print(f'PASS close-risk visibility: {mode} uses {authorizations} fresh complete authority checks for the entire native pin set',flush=True)

        pins=int(query(f"SELECT count(*) FROM market_data.market_close_pins WHERE(catalog_id,close_id)=('{catalog}','{close}');"))
        assert pins>1,pins
        for bound in (False,True):
            counts=[]
            for size in (1,pins):
                token=str(UUID(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_pilot_context('{run}',{fence},'{worker}');")))
                # The small set is a rollback-only synthetic owner fixture; read
                # it with restored triggers through the actual runtime below.
                subset=(f"SET LOCAL session_replication_role=replica;DELETE FROM market_data.market_close_pins WHERE(catalog_id,close_id)=('{catalog}','{close}') AND observation_id<>(SELECT observation_id FROM market_data.market_close_pins WHERE(catalog_id,close_id)=('{catalog}','{close}') ORDER BY observation_id LIMIT 1);SET LOCAL session_replication_role=origin;" if size==1 else '')
                bind=f"SELECT security.use_market_context('{catalog}','{context}');" if bound else ''
                result=query("SET track_functions='all';BEGIN;"+subset+f"SET LOCAL plan_cache_mode={mode};SET LOCAL SESSION AUTHORIZATION pilot_fixture_runtime;SELECT security.use_context('{token}');"+bind+
                    stats+f"SELECT 'VISIBLE|'||security.market_close_visible('{catalog}','{close}');"+stats+
                    f"SELECT 'VISIBLE|'||security.market_close_visible('{catalog}','{close}');"+stats+'ROLLBACK;').splitlines()
                calls=[list(map(int,row.split('|')[1:])) for row in result if row.startswith('CALLS|')]
                deltas=[end[1]-start[1] for start,end in zip(calls,calls[1:])]
                assert result.count('VISIBLE|true')==2 and all(2<=n<=12 for n in deltas),(mode,size,bound,deltas,result)
                counts.extend(deltas)
            assert len(set(counts))==1,(mode,bound,counts)
            print(f"PASS close-risk visibility: {mode}, historical={bound}, repeats fresh {counts[0]}-check authority for 1 and {pins} pins",flush=True)

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
                           f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.has_permission('ReadMarketData');SELECT security.market_close_visible('{catalog}','{close}');RESET SESSION AUTHORIZATION;"+change+
                           f"SET LOCAL SESSION AUTHORIZATION pilot_fixture_runtime;SELECT security.has_permission('ReadMarketData');SELECT security.market_close_visible('{catalog}','{close}');ROLLBACK;").splitlines()
            assert result == ['t','t','f','f'], (mode,name,result)
        print(f'PASS close-risk authority: {mode} rechecks revoked authority, expiry, cancellation and replaced ownership in the same bound transaction', flush=True)

    from check_close_pin_visibility import run_checks as check_visibility
    check_visibility(query,claim,profile)
