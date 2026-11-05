"""An actual storage wait must not preserve an expired adoption replay authority."""
from concurrent.futures import ThreadPoolExecutor
import time


def check(query, manifest, operation):
    catalog=manifest['catalogId']
    def token():
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',2);")
    # Status has authorized its command before waiting on the immutable adoption.
    # An empty plan gives it no later per-trade check to accidentally rely on.
    with ThreadPoolExecutor(max_workers=2) as pool:
        holder=pool.submit(query,"SET application_name='synthetic_adoption_expiry_holder';BEGIN;LOCK TABLE trading.facility_quantity_adoptions IN ACCESS EXCLUSIVE MODE;SELECT pg_sleep(4);COMMIT;")
        deadline=time.monotonic()+5
        while query("SELECT count(*) FROM pg_stat_activity WHERE application_name='synthetic_adoption_expiry_holder' AND wait_event='PgSleep';")!='1':
            assert time.monotonic()<deadline;time.sleep(.02)
        issued=token()
        def waiting():
            try:
                query(f"SET application_name='synthetic_adoption_expiry_waiter';SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{issued}');SELECT security.facility_quantity_status('{catalog}','{operation}');COMMIT;")
            except RuntimeError as error:
                return str(error)
            raise AssertionError('An expired request recovered an adoption outcome after a storage wait')
        waiter=pool.submit(waiting)
        deadline=time.monotonic()+2
        while query("SELECT count(*) FROM pg_stat_activity WHERE application_name='synthetic_adoption_expiry_waiter' AND wait_event_type='Lock';")!='1':
            assert time.monotonic()<deadline and not waiter.done();time.sleep(.02)
        holder.result(timeout=8)
        error=waiter.result(timeout=8)
        assert any(code in error for code in ['facility_quantity_denied','access_denied','context_expired']), error
    print('PASS final allocation status: authority expiring during an observed storage lock cannot recover an empty-plan receipt',flush=True)
