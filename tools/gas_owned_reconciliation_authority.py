"""The new ordinary producer retains its original selected gas authority."""
import time
from concurrent.futures import ThreadPoolExecutor

from check_futures_margin_adoption import successor_grants


def checks(query,call,command,manifest,*,issuer='https://preview.invalid'):
    catalog=manifest['catalogId']
    before=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    def refused(statement):
        try:call(statement)
        except RuntimeError as error:assert 'gas_owned_denied' in str(error),str(error)
        else:raise AssertionError('Ordinary correction extended its original gas authority to a successor grant')
    # The successor already exists before admission. It makes current access
    # valid after the original term expires, and cannot extend that old term.
    with successor_grants(query,manifest,'trader','ReadGasOwnedHoldings',issuer=issuer) as application:
        with ThreadPoolExecutor(max_workers=1) as pool:
            blocked=pool.submit(query,f"SET application_name='{application}';BEGIN;SELECT pg_advisory_xact_lock(730849102);SELECT pg_sleep(4);COMMIT;")
            deadline=time.monotonic()+3
            while query(f"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='{application}' AND wait_event='PgSleep');")!='t':
                assert time.monotonic()<deadline,'Owned publication-lock probe did not begin'
                time.sleep(.01)
            refused(command)
            blocked.result()
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==before
    with successor_grants(query,manifest,'trader','ReadGasOwnedHoldings',issuer=issuer):
        refused(command+'SELECT pg_sleep(4);')
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==before
    print('PASS ordinary correction authority: original gas read term survives publication waits and deferred commit despite an active successor, with all financial rows rolled back',flush=True)
