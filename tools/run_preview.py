#!/usr/bin/env python3
"""Run a disposable, loopback-only synthetic workbench. Never uses an existing DB."""
import argparse
import json
import os
from pathlib import Path
import shlex
import shutil
import signal
import socket
import subprocess
import tempfile
import time
from urllib.request import urlopen
from uuid import uuid4
from preview_fixture import fixture
from owned_process import stop_owned_group

ROOT = Path(__file__).resolve().parents[1]


def main():
    from check_standalone import main as check_standalone
    check_standalone()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin', type=Path, required=True, help='Installed stock PG16+ binaries')
    parser.add_argument('--dotnet', type=Path, required=True, help='Installed .NET SDK matching global.json')
    parser.add_argument('--port', type=int, default=0, help='Loopback HTTP port; 0 chooses an unused port')
    parser.add_argument('--no-build', action='store_true', help='Use already-built assets')
    parser.add_argument('--check', action='store_true', help='Run HTTP integration checks, then clean up')
    parser.add_argument('--draft-check',action='store_true',help='Qualify temporary deal pricing and exposure')
    parser.add_argument('--booking-check',action='store_true',help='Qualify backend-owned immutable trade booking facts')
    parser.add_argument('--unit-check', action='store_true', help='Run amount/unit integration checks in the owned standard fixture')
    parser.add_argument('--exchange-check',action='store_true',help='Qualify the independent exchange futures dataset')
    parser.add_argument('--with-exchange',action='store_true',help='Publish the independent exchange futures dataset in this newly owned database')
    parser.add_argument('--party-check', action='store_true', help='Run party-editor integration checks in an owned fixture')
    parser.add_argument('--identity-check', action='store_true', help='Run economic-key integration checks in the owned standard fixture, then clean up')
    parser.add_argument('--browser-check',action='store_true',help='Run checked-in browser scenarios in the newly owned standard fixture')
    parser.add_argument('--browser',type=Path,help='Installed Chromium-compatible executable; used only with --browser-check')
    parser.add_argument('--example', choices=['standard','mixed-gas'], default='standard', help='Explicit synthetic dataset')
    args = parser.parse_args()
    if args.identity_check or args.unit_check or args.party_check or args.exchange_check or args.booking_check or args.draft_check:
        args.check = True
        if args.example != 'standard' or args.identity_check and args.browser_check:
            parser.error('--identity-check requires the standard fixture without --browser-check')
    if not 0 <= args.port <= 65535:
        parser.error('Port must be between 0 and 65535')
    if args.browser_check and (not args.check or args.example!='standard' or args.browser is None or not args.browser.is_file()):
        parser.error('--browser-check requires --check, --example standard and an installed --browser executable')
    bins = {n: args.pg_bin.resolve()/n for n in ('initdb','pg_ctl','psql')}
    dotnet = args.dotnet.resolve()
    for binary in [dotnet,*bins.values()]:
        if not binary.is_file():
            parser.error(f'Missing installed binary: {binary}')
    env = {k:v for k,v in os.environ.items() if not k.startswith(('PG','OPENETRM_PREVIEW_','ASPNETCORE_','DOTNET_'))}
    env.update({'PGTZ':'UTC','DOTNET_CLI_TELEMETRY_OPTOUT':'1','DOTNET_NOLOGO':'1'})
    if not args.no_build:
        subprocess.run(['npm','ci','--prefix','web','--ignore-scripts'],cwd=ROOT,env=env,check=True,timeout=120)
        subprocess.run(['npm','run','build','--prefix','web'],cwd=ROOT,env=env,check=True,timeout=60)
        subprocess.run([str(dotnet),'restore','src/OpenEtrm.Api','--locked-mode'],cwd=ROOT,env=env,check=True,timeout=120)
        subprocess.run([str(dotnet),'build','src/OpenEtrm.Api','--no-restore'],cwd=ROOT,env=env,check=True,timeout=120)
    dll=ROOT/'src/OpenEtrm.Api/bin/Debug/net10.0/OpenEtrm.Api.dll'
    if not dll.is_file() or not (ROOT/'web/dist/index.html').is_file():
        parser.error('Build the API and web assets before using --no-build')
    # Use a short socket path on systems with a small Unix-socket path limit.
    temporary=Path(tempfile.mkdtemp(prefix='openetrm-preview-',dir='/tmp')).resolve()
    os.chmod(temporary,0o700)
    data=temporary/'data'
    run_id=str(uuid4())
    (temporary/'.openetrm-preview').write_text(run_id)
    started=False
    startup_attempted=False
    safe_to_remove=True
    child=None
    browser_process=None
    browser_cleanup_ok=True
    log=None
    def run(command,**kwargs):
        return subprocess.run([str(v) for v in command],env=env,text=True,capture_output=True,timeout=30,**kwargs)
    def stop_signal(signum,frame):
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM,stop_signal)
    try:
        initialized=run([bins['initdb'],'-D',data,'--auth-local=trust','--auth-host=reject','--encoding=UTF8','--locale=C','--no-instructions','--username=preview_owner'])
        if initialized.returncode:
            raise RuntimeError(initialized.stderr)
        startup_attempted=True
        safe_to_remove=False
        started_result=run([bins['pg_ctl'],'-D',data,'-w','-t','15','-l',temporary/'postgres.log','-o',shlex.join(['-k',str(temporary),'-h','','-p','55439','-c','track_commit_timestamp=on']),'start'])
        if started_result.returncode:
            raise RuntimeError(started_result.stderr)
        started=True
        psql=[bins['psql'],'-X','-h',temporary,'-p','55439','-U','preview_owner','-d','postgres','-qAt','-v','ON_ERROR_STOP=1']
        def query(sql):
            result=run(psql,input=sql)
            if result.returncode:
                raise RuntimeError(result.stderr)
            return result.stdout.strip()
        if Path(query('SHOW data_directory;')).resolve()!=data.resolve():
            raise RuntimeError('Refusing to operate on a database outside the owned disposable cluster')
        if args.example=='mixed-gas':
            export=temporary/'mixed-inputs.json'
            subprocess.run([str(dotnet),'run','--project','tests/OpenEtrm.MarketData.Checks','--',
                            '--export-mixed-resolution',str(export)],cwd=ROOT,env=env,check=True,timeout=60)
            from mixed_preview_fixture import fixture as mixed_fixture
            reference,market,security,manifest=mixed_fixture(export)
        else:
            reference,market,security,manifest=fixture(ROOT)
        for migration in sorted((ROOT/'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql')):
            query(migration.read_text())
        query(reference)
        query(market)
        if args.example=='standard':
            from swap_fixture import fixture as swap_fixture
            swap_market,swap_manifest=swap_fixture();query(swap_market)
            manifest['swapMarkets']=[swap_manifest]
        query(security)
        if args.exchange_check or args.with_exchange:
            from exchange_fixture import fixture as exchange_fixture
            er,em,es,et,exchange_manifest=exchange_fixture(ROOT)
            query(er);query(em);query(es);query(et)
            (temporary/'exchange-manifest.json').write_text(json.dumps(exchange_manifest))
        from preview_fixture import trading_fixture
        query(trading_fixture(manifest['catalogId'],swap_defaults=args.example=='standard'))
        if args.unit_check:
            manifest['unitCatalogPreviousKnownAt']=query("SELECT to_char(clock_timestamp() AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"');")
            unit_reference='20000000-0000-0000-0000-000000000004'
            query(f"INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{manifest['referenceCatalogId']}','{unit_reference}','unit_catalog_v1');")
            for table in ('dimensions','units','unit_conversions','commodities','measurement_bases','quantity_specifications'):
                query(f"INSERT INTO ref_data.{table} SELECT (jsonb_populate_record(NULL::ref_data.{table},to_jsonb(t)||jsonb_build_object('revision_id','{unit_reference}'))).* FROM ref_data.{table} t WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}';")
            query(f"INSERT INTO ref_data.units VALUES('{manifest['referenceCatalogId']}','{unit_reference}','catalog_only_kwh','Catalog kWh','energy'); INSERT INTO ref_data.unit_conversions VALUES('{manifest['referenceCatalogId']}','{unit_reference}','catalog_only_kwh','kilowatt_hour','energy',1,1); INSERT INTO ref_data.quantity_specifications VALUES('{manifest['referenceCatalogId']}','{unit_reference}','test_power_catalog_only','test_power','test_power_energy','catalog_only_kwh','energy'); UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{manifest['referenceCatalogId']}' AND id='{unit_reference}';")
            unit_bundle='60000000-0000-0000-0000-000000000004'
            query(f"BEGIN; INSERT INTO market_data.revisions VALUES ('{manifest['catalogId']}','{unit_bundle}','{manifest['referenceCatalogId']}','{unit_reference}','draft',0); INSERT INTO market_data.revision_periods VALUES ('{manifest['catalogId']}','{unit_bundle}','2020-01-01','2022-01-01'); UPDATE market_data.revisions SET state='sealed' WHERE catalog_id='{manifest['catalogId']}' AND id='{unit_bundle}'; COMMIT;")
            manifest['unitCatalog']={'referenceRevisionId':unit_reference,'definitionRevisionId':unit_bundle}
        if args.check and args.example=='standard' and not args.exchange_check:
            from check_trade_revision_upgrade import run_checks as check_upgrade
            legacy_reference,legacy_market,_,_=fixture(ROOT,current_schema=False)
            check_upgrade(query,psql,env,ROOT,[legacy_reference,legacy_market,security,trading_fixture(manifest['catalogId'],annotations=False)],manifest)
        (temporary/'manifest.json').write_text(json.dumps(manifest))
        # Freeze assets for this owned instance; a later web build must not change
        # another running preview's presentation while it is being used.
        shutil.copytree(ROOT/'web/dist',temporary/'web')
        port=args.port
        if port==0:
            with socket.socket() as reservation:
                reservation.bind(('127.0.0.1',0))
                port=reservation.getsockname()[1]
        url=f'http://127.0.0.1:{port}'
        app_env={**env,'OPENETRM_PREVIEW_ROOT':str(temporary),'OPENETRM_PREVIEW_PORT':str(port),
                 'OPENETRM_PREVIEW_WEB':str(temporary/'web'),
                 'OPENETRM_PREVIEW_RUNTIME':f'Host={temporary};Port=55439;Database=postgres;Username=preview_runtime;SSL Mode=Disable',
                 'OPENETRM_PREVIEW_AUTHENTICATOR':f'Host={temporary};Port=55439;Database=postgres;Username=preview_authenticator;SSL Mode=Disable'}
        # Exercise transport independently of the launcher's UTC psql sessions.
        if args.check:
            app_env['PGTZ']='America/Chicago'
        shutil.copytree(dll.parent,temporary/'api')
        frozen_dll=temporary/'api'/dll.name
        log=(temporary/'app.log').open('w')
        child=subprocess.Popen([str(dotnet),str(frozen_dll),'--local-preview'],cwd=ROOT,env=app_env,stdout=log,stderr=subprocess.STDOUT)
        deadline=time.monotonic()+20
        while time.monotonic()<deadline:
            if child.poll() is not None:
                raise RuntimeError('Preview failed to start:\n'+(temporary/'app.log').read_text())
            try:
                with urlopen(url+'/api/health',timeout=.5) as response:
                    health=json.load(response)
                if health.get('runId')!=run_id:
                    raise RuntimeError('HTTP port belongs to a different process; refusing to reuse it')
                break
            except OSError:
                time.sleep(.1)
        else:
            raise RuntimeError('Preview did not become ready:\n'+(temporary/'app.log').read_text())
        if (not args.check or args.unit_check) and args.example=='standard':
            from seed_preview_positions import seed_positions
            seed_positions(url,run_id,temporary)
            from seed_preview_swaps import seed_swaps
            seed_swaps(url,run_id,temporary)
            print('Loaded eight futures and sixteen financial swap examples across four weekdays.',flush=True)
        if args.with_exchange and not args.check:
            from seed_exchange_positions import seed_positions as seed_exchange
            seed_exchange(url,run_id,temporary)
            print('Loaded nineteen exchange-aligned futures examples with original synthetic execution prices.',flush=True)
        print(f'PREVIEW_URL={url}',flush=True)
        print('Private synthetic database. Ctrl+C stops the app and removes its owned database.',flush=True)
        if args.check:
            if args.draft_check:
                from check_draft_pricing import run_checks as check_draft
                check_draft(url,query,manifest)
            elif args.booking_check:
                from check_definition_publication import run_checks as check_definition_publication
                check_definition_publication(query,manifest)
                from check_booking_facts import run_checks as check_booking_facts
                check_booking_facts(url,query,manifest)
            elif args.exchange_check:
                from check_exchange_preview import run_checks as check_exchange
                check_exchange(url,query,exchange_manifest)
                from seed_exchange_positions import seed_positions as seed_exchange
                seeded=seed_exchange(url,run_id,temporary)
                assert len(seeded)==19 and seeded==seed_exchange(url,run_id,temporary)
                print('PASS exchange examples populate nineteen real product families through idempotent authorized bookings.',flush=True)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--exchange'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=120):raise RuntimeError('Owned exchange browser checks failed')
            elif args.party_check:
                from check_party_editors import run_checks as check_parties
                check_parties(url)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--parties-power'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=120):raise RuntimeError('Owned party/power browser checks failed')
            elif args.unit_check:
                from check_units_preview import run_checks as check_units
                check_units(url,manifest.get('unitCatalogPreviousKnownAt'))
                from check_deal_formats import run_checks as check_formats
                check_formats(url)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--units'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=60):raise RuntimeError('Owned unit browser checks failed')
            elif args.identity_check:
                from check_units_preview import run_checks as check_units
                check_units(url)
                from check_trading_reference_access import run_checks as check_reference_authority
                check_reference_authority(query,manifest)
                from check_economic_lookups import run_checks as check_identities
                check_identities(url,query,manifest)
            elif args.example=='mixed-gas':
                from check_mixed_preview import run_checks
                run_checks(url,json.loads(export.read_text()))
            else:
                from check_preview import run_checks
                workspace=run_checks(url)
                from check_table_views import run_checks as check_table_views
                check_table_views(url)
                from check_schedule_storage import run_checks as check_schedule_storage
                check_schedule_storage(query,psql,env,manifest)
                from check_temporal_storage import run_checks as check_temporal_storage
                check_temporal_storage(query,psql,env,manifest,url)
                from check_futures_storage import run_checks as check_futures_storage
                check_futures_storage(query,manifest,psql,env)
                from check_futures_amendments import run_checks as check_amendments
                check_amendments(url,query,manifest,psql,env)
                adapter_fixture=temporary/'adapter-fixture.json'
                adapter_fixture.write_text(json.dumps(workspace))
                subprocess.run([str(dotnet),'run','--project','tests/OpenEtrm.Api.Checks','--',str(adapter_fixture)],cwd=ROOT,env=env,check=True,timeout=60)
                from check_seeded_preview import run_checks as check_seeded_preview
                check_seeded_preview(url,run_id,temporary)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve())],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=360):
                        raise RuntimeError('Owned browser checks failed')
                from check_swaps_preview import run_checks as check_swaps
                check_swaps(url)
                from check_swap_payment_rules import run_checks as check_swap_payment_rules
                check_swap_payment_rules(url)
                from check_booking_facts import run_checks as check_booking_facts
                check_booking_facts(url,query,manifest)
                from check_draft_pricing import run_checks as check_draft
                seed_client=check_draft(url,query,manifest)
                from check_definition_publication import run_checks as check_definition_publication
                check_definition_publication(query,manifest)
                from check_trade_annotations import run_checks as check_trade_annotations
                check_trade_annotations(url)
                from check_trade_parties import run_checks as check_trade_parties
                check_trade_parties(url,query,manifest)
                from check_units_preview import run_checks as check_units
                check_units(url)
                from check_trading_reference_access import run_checks as check_reference_authority
                check_reference_authority(query,manifest)
                from check_economic_lookups import run_checks as check_identities
                check_identities(url,query,manifest)
                from check_swaps_storage import run_checks as check_swaps_storage
                check_swaps_storage(query,manifest,psql,env)
                from seed_preview_swaps import seed_swaps
                seeded=seed_swaps(url,run_id,temporary,seed_client)
                assert len(seeded)==16 and seeded==seed_swaps(url,run_id,temporary,seed_client)
                print('PASS sixteen synthetic swap examples seed idempotently across all four weekdays',flush=True)
        else:
            child.wait()
    except KeyboardInterrupt:
        print('Stopping the owned preview.',flush=True)
    finally:
        # Stop the entire owned browser group, even if Node timed out or exited
        # before Chromium. Never remove a profile while a live child still uses it.
        if browser_process is not None:
            try:
                stop_owned_group(browser_process)
            except Exception:
                browser_cleanup_ok=False
                print('Browser cleanup unconfirmed; retaining its private directory.',flush=True)
        # Only the process started above and its exact database directory are touched.
        if child is not None and child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=10)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait(timeout=5)
        if log is not None:
            log.close()
        if startup_attempted:
            status=run([bins['pg_ctl'],'-D',data,'status'])
            if status.returncode==0:
                stopped=run([bins['pg_ctl'],'-D',data,'-m','immediate','-w','-t','15','stop'])
                if stopped.returncode:
                    raise RuntimeError('Failed to stop owned preview; retained '+str(temporary))
                status=run([bins['pg_ctl'],'-D',data,'status'])
            if status.returncode!=3:
                raise RuntimeError('Cannot establish that owned preview stopped; retained '+str(temporary))
            # A failed startup can leave a late child; retain its directory.
            safe_to_remove=started and browser_cleanup_ok
        if safe_to_remove:
            shutil.rmtree(temporary)
            print('Removed owned disposable preview database.',flush=True)
        else:
            print(f'Startup/cleanup incomplete; retained private directory: {temporary}',flush=True)
        if not browser_cleanup_ok:
            raise RuntimeError('Owned browser cleanup could not be confirmed; private files retained')


if __name__=='__main__':
    main()
