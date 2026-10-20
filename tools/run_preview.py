#!/usr/bin/env python3
"""Run a disposable, loopback-only synthetic workbench. Never uses an existing DB."""
import argparse
import json
import os
from pathlib import Path
import secrets
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
    parser.add_argument('--power-actual-check',action='store_true',help='Qualify exact collected-price interval settlement sources')
    parser.add_argument('--index-fixing-check',action='store_true',help='Qualify published monthly and partially fixed daily index observations')
    parser.add_argument('--contract-dates-check',action='store_true',help='Qualify the shared contract-date library')
    parser.add_argument('--pilot-close-check',action='store_true',help='Qualify explicitly generated pilot closes in an owned fixture')
    parser.add_argument('--swap-risk-holdings-check',action='store_true',help='Qualify stored swap risk materialization in an owned fixture')
    parser.add_argument('--draft-check',action='store_true',help='Qualify temporary deal pricing and exposure')
    parser.add_argument('--hosted-check',action='store_true',help='Qualify local credential persistence in an owned database')
    parser.add_argument('--jobs-check',action='store_true',help='Qualify owned jobs and the shared infrastructure screen')
    parser.add_argument('--reference-check',action='store_true',help='Qualify reference metadata authoring and reviewed display activation')
    parser.add_argument('--booking-check',action='store_true',help='Qualify backend-owned immutable trade booking facts')
    parser.add_argument('--unit-check', action='store_true', help='Run amount/unit integration checks in the owned standard fixture')
    parser.add_argument('--execution-check',action='store_true',help='Qualify standard futures execution policies in an owned synthetic fixture')
    parser.add_argument('--custom-curves-check',action='store_true',help='Qualify custom curve definitions and calculations in an owned synthetic fixture')
    parser.add_argument('--market-read-check',action='store_true',help='Qualify per-row market authority in the owned standard fixture')
    parser.add_argument('--market-dates-check',action='store_true',help='Qualify scoped market-close availability and the calendar in an owned fixture')
    parser.add_argument('--with-custom-curves',action='store_true',help='Load synthetic monthly and daily NG panels for the custom curve editor')
    parser.add_argument('--exchange-check',action='store_true',help='Qualify the independent exchange futures dataset')
    parser.add_argument('--with-exchange',action='store_true',help='Publish the independent exchange futures dataset in this newly owned database')
    parser.add_argument('--party-check', action='store_true', help='Run party-editor integration checks in an owned fixture')
    parser.add_argument('--identity-check', action='store_true', help='Run economic-key integration checks in the owned standard fixture, then clean up')
    parser.add_argument('--assistant-check',action='store_true',help='Qualify page-assistant provider, model and price administration in an owned fixture')
    parser.add_argument('--browser-check',action='store_true',help='Run checked-in browser scenarios in the newly owned standard fixture')
    parser.add_argument('--browser',type=Path,help='Installed Chromium-compatible executable; used only with --browser-check')
    parser.add_argument('--example', choices=['standard','mixed-gas'], default='standard', help='Explicit synthetic dataset')
    args = parser.parse_args()
    fixing_frames=args.index_fixing_check or args.swap_risk_holdings_check
    if args.power_actual_check or args.index_fixing_check or args.swap_risk_holdings_check or args.pilot_close_check or args.contract_dates_check or args.identity_check or args.unit_check or args.party_check or args.exchange_check or args.booking_check or args.draft_check or args.hosted_check or args.jobs_check or args.reference_check or args.execution_check or args.assistant_check or args.custom_curves_check or args.market_dates_check or args.market_read_check:
        args.check = True
        if args.example != 'standard' or args.identity_check and args.browser_check:
            parser.error('--identity-check requires the standard fixture without --browser-check')
        if args.market_read_check and args.browser_check:
            parser.error('--market-read-check has no browser scenarios; omit --browser-check')
    if not 0 <= args.port <= 65535:
        parser.error('Port must be between 0 and 65535')
    if args.browser_check and (not args.check or args.example!='standard' or args.browser is None or not args.browser.is_file()):
        parser.error('--browser-check requires --check, --example standard and an installed --browser executable')
    if args.with_custom_curves and args.example!='standard':
        parser.error('--with-custom-curves requires the standard fixture')
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
        # Model-provider keys are sealed under two private keys made once per disposable database
        # and passed to every API start (docs/ai-assistant.md §5.2); nothing is generated at API start.
        credential_keys={}
        for variable,name in (('OPENETRM_PREVIEW_CREDENTIAL_KEY','credential.key'),('OPENETRM_PREVIEW_CREDENTIAL_RETRY_KEY','credential-retry.key')):
            with os.fdopen(os.open(temporary/name,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600),'wb') as key_file:
                key_file.write(secrets.token_bytes(32))
            credential_keys[variable]=str(temporary/name)
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
            reference,market,security,manifest=fixture(ROOT,fixing_frames=fixing_frames)
        for migration in sorted((ROOT/'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql')):
            if args.execution_check and migration.name=='0040_futures_execution_policies.sql':
                from check_futures_policy_upgrade import run_checks as check_policy_upgrade
                legacy_reference,legacy_market,_,legacy_manifest=fixture(ROOT)
                check_policy_upgrade(query,legacy_reference,legacy_market,legacy_manifest,migration.read_text())
            if args.assistant_check and migration.name=='0051_model_catalog.sql':
                from check_model_catalog import check_upgrade as check_catalog_upgrade
                check_catalog_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0053_model_cache_read_rule.sql':
                from check_model_catalog import check_rule_upgrade
                check_rule_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0058_provider_qualification.sql':
                from check_model_qualification import check_upgrade as check_qualification_upgrade
                check_qualification_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0059_model_display_name_rule.sql':
                from check_model_catalog import check_name_rule_upgrade
                check_name_rule_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0060_model_versions.sql':
                from check_model_versions import check_upgrade as check_versions_upgrade
                check_versions_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0061_directory_efforts.sql':
                from check_model_versions import check_directory_upgrade
                check_directory_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0062_provider_tests.sql':
                from check_provider_tests import check_upgrade as check_tests_upgrade
                check_tests_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0064_directory_tests.sql':
                from check_provider_tests import check_directory_upgrade as check_tests_directory_upgrade
                check_tests_directory_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0065_worker_start_time.sql':
                from check_provider_tests import check_start_upgrade
                check_start_upgrade(query,migration.read_text())
            if args.check and args.example=='standard' and not args.exchange_check and migration.name=='0052_unambiguous_discount_defaults.sql':
                from check_discount_default_upgrade import run_checks as check_default_upgrade
                from swap_fixture import fixture as swap_fixture
                from preview_fixture import trading_fixture
                check_default_upgrade(query,[reference,market,swap_fixture(fixing_frames=fixing_frames)[0],security,trading_fixture(manifest['catalogId'],swap_defaults=True)],migration.read_text())
            query(migration.read_text())
        if args.execution_check:
            from check_futures_execution import prepare as prepare_execution
            reference,market,manifest=prepare_execution(reference,market,manifest)
        if args.custom_curves_check or args.with_custom_curves or args.market_dates_check:
            from custom_curve_fixture import prepare as prepare_custom_curves
            reference,market,manifest=prepare_custom_curves(reference,market,manifest)
        if args.hosted_check:
            from provision_hosted import prepare as prepare_hosted
            prepared=temporary/'fresh-pilot-check'
            from preview_fixture import installed_tzdb
            source_id,job_id=str(uuid4()),str(uuid4())
            collection={'sources':[{'id':source_id,'name':'Synthetic PJM source','provider':'PJM','dataset':'da_hrl_lmps','market':'DA','point':'51288','minutes':60,'unit':'MWh','currency':'USD','timeZone':'America/New_York'}],
                        'jobs':[{'id':job_id,'provider':'PJM','name':'Synthetic paused collection','sourceIds':[source_id],'enabled':True,'scheduleEnabled':False,'cron':'0 8 * * *','scheduleZone':'UTC','fromDay':-7,'throughDay':-1}], 'requestsPerMinute':{'PJM':4}}
            prepared_ids=prepare_hosted('https://pilot.synthetic.invalid',str(uuid4()),installed_tzdb(),prepared,collection)
            calendar_request=prepared/'calendar.json';calendar_sql=prepared/'calendar.sql'
            calendar_request.write_text(json.dumps({'referenceCatalogId':prepared_ids['reference'],'referenceRevisionId':prepared_ids['referenceRevision'],'from':'2026-03-08','through':'2026-11-02','clocks':[{'code':'pjm','kind':'iana','fixedOffsetSeconds':None,'ianaZone':'America/New_York','tzdbVersion':installed_tzdb()}]}))
            subprocess.run([str(dotnet),str(dll),'--render-power-days',str(calendar_request),str(calendar_sql)],cwd=ROOT,env=env,check=True,timeout=30)
            combined=(prepared/'seed.sql').read_text().replace('COMMIT;','')+calendar_sql.read_text().replace('BEGIN;','').replace('COMMIT;',"DO $$BEGIN IF NOT EXISTS(SELECT 1 FROM power_data.operating_days WHERE operating_date='2026-03-08' AND ends_at-starts_at=interval '23 hours') OR NOT EXISTS(SELECT 1 FROM power_data.operating_days WHERE operating_date='2026-11-01' AND ends_at-starts_at=interval '25 hours') THEN RAISE EXCEPTION 'Incorrect retained operating-day bounds';END IF;END$$;ROLLBACK;")
            query(combined)

            print('PASS fresh hosted seed validates on an empty stock database without synthetic users, observations or trades',flush=True)
        query(reference)
        if args.power_actual_check:
            from check_power_actual_storage import prepare_market
            market=prepare_market(market,manifest)
        query(market)
        if args.example=='standard':
            from swap_fixture import fixture as swap_fixture
            swap_market,swap_manifest=swap_fixture(fixing_frames=fixing_frames);query(swap_market)
            manifest['swapMarkets']=[swap_manifest]
        query(security)
        if args.hosted_check:
            from check_hosted_auth import run_checks as check_hosted_auth
            check_hosted_auth(query, manifest)
            from check_power_import import run_checks as check_power_import
            check_power_import(query,manifest,dotnet,temporary,env)
        if args.exchange_check or args.with_exchange:
            from exchange_fixture import fixture as exchange_fixture
            er,em,es,et,exchange_manifest=exchange_fixture(ROOT)
            query(er);query(em);query(es);query(et)
            (temporary/'exchange-manifest.json').write_text(json.dumps(exchange_manifest))
        from preview_fixture import trading_fixture
        query(trading_fixture(manifest['catalogId'],swap_defaults=args.example=='standard'))
        if args.power_actual_check:
            from check_power_actual_storage import run_checks as check_power_actual_storage
            check_power_actual_storage(query,manifest)
        if args.index_fixing_check:
            from check_index_fixing_upgrade import run_checks as check_index_fixing_upgrade
            check_index_fixing_upgrade(query,psql,env,ROOT)
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
        if args.jobs_check:
            from check_jobs_http import prepare as prepare_jobs
            prepare_jobs(query,manifest)
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
                 'OPENETRM_PREVIEW_AUTHENTICATOR':f'Host={temporary};Port=55439;Database=postgres;Username=preview_authenticator;SSL Mode=Disable',
                 **credential_keys}
        # Exercise transport independently of the launcher's UTC psql sessions.
        if args.check:
            app_env['PGTZ']='America/Chicago'
        shutil.copytree(dll.parent,temporary/'api')
        frozen_dll=temporary/'api'/dll.name
        log=(temporary/'app.log').open('w')
        api_env=app_env
        if args.assistant_check:
            # Provider tests reach a loopback fake provider through the preview's check endpoint, and every proxy
            # setting names a trap that no request may reach (docs/ai-assistant.md §5.2, §13).
            from fake_model_provider import FakeProvider,Trap
            fake_provider,proxy_trap=FakeProvider(),Trap()
            api_env={**app_env,'OPENETRM_PREVIEW_PROVIDER_ENDPOINT':fake_provider.url,'no_proxy':'','NO_PROXY':'',
                     **{name:proxy_trap.url for name in ('http_proxy','https_proxy','all_proxy','HTTP_PROXY','HTTPS_PROXY','ALL_PROXY')}}
        child=subprocess.Popen([str(dotnet),str(frozen_dll),'--local-preview'],cwd=ROOT,env=api_env,stdout=log,stderr=subprocess.STDOUT)
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
            if not args.check:
                from seed_schedule_examples import seed_schedule_examples
                seed_schedule_examples(url,run_id,temporary)
                print('Published the synthetic monthly gas date schedule; daily power contract dates are backend-loadable session candidates.',flush=True)
        if args.with_exchange and not args.check:
            from seed_exchange_positions import seed_positions as seed_exchange
            seed_exchange(url,run_id,temporary)
            print('Loaded twelve source-listed monthly futures examples with original synthetic execution prices.',flush=True)
        print(f'PREVIEW_URL={url}',flush=True)
        print('Private synthetic database. Ctrl+C stops the app and removes its owned database.',flush=True)
        if args.check:
            if args.power_actual_check:
                from check_power_actual_http import run_checks as check_power_actual_http
                check_power_actual_http(url,query,manifest)
            elif args.swap_risk_holdings_check:
                from check_swap_risk_holdings import run_checks as check_swap_risk_holdings
                check_swap_risk_holdings(url,query,manifest,dotnet,temporary,app_env,psql)
            elif args.pilot_close_check:
                from check_pilot_close import run_checks as check_pilot_close
                check_pilot_close(url,query,manifest,psql,app_env)
                from check_pilot_worker import run_checks as check_pilot_worker
                pilot_run=check_pilot_worker(url,query,manifest,dotnet,temporary,app_env,psql)
                from check_pilot_reports import run_checks as check_pilot_reports
                check_pilot_reports(url,query,manifest,pilot_run,owner_profile='approver',psql=psql,environment=app_env)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--pilot-close'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=180):raise RuntimeError('Owned pilot close browser checks failed')
            elif args.market_dates_check:
                from check_market_dates import run_checks as check_market_dates
                check_market_dates(url,query,manifest)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--market-dates'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=240):raise RuntimeError('Owned market date browser checks failed')
            elif args.contract_dates_check:
                from check_contract_dates import run_checks as check_contract_dates
                check_contract_dates(url,query,manifest,psql,env)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--contract-dates'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=180):raise RuntimeError('Owned shared contract-date browser checks failed')
            elif args.custom_curves_check:
                from check_custom_curves import run_checks as check_custom_curves
                check_custom_curves(url,query,manifest)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--custom-curves'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=180):raise RuntimeError('Owned custom curve browser checks failed')
            elif args.execution_check:
                from check_futures_execution import run_checks as check_execution
                check_execution(url,query,manifest)
            elif args.reference_check:
                from check_reference_display_http import run_checks as check_reference_display
                check_reference_display(url,query)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--reference-editor'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=180):raise RuntimeError('Owned reference-editor browser checks failed')
            elif args.jobs_check:
                from check_jobs_http import run_checks as check_jobs
                check_jobs(url,query)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--jobs'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=120):
                        raise RuntimeError('Owned jobs browser checks failed')
            elif args.hosted_check:
                from check_hosted_http import run_checks as check_hosted_http
                check_hosted_http(temporary,manifest,query,dotnet,frozen_dll,app_env,args.browser if args.browser_check else None,url,run_id)
            elif args.draft_check:
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
                assert len(seeded)==12 and seeded==seed_exchange(url,run_id,temporary)
                print('PASS exchange examples populate twelve source-listed monthly product families through idempotent authorized bookings.',flush=True)
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
            elif args.index_fixing_check:
                from check_index_fixings import run_checks as check_index_fixings
                check_index_fixings(url,query,manifest)
            elif args.market_read_check:
                from check_market_read_authority import run_checks as check_market_authority
                check_market_authority(query,manifest)
            elif args.assistant_check:
                from check_model_providers import run_checks as check_model_providers
                check_model_providers(url,query,psql,manifest,temporary,app_env,dotnet)
                from check_model_catalog import run_checks as check_model_catalog
                check_model_catalog(url,query,psql,manifest,app_env)
                from check_model_qualification import run_checks as check_model_qualification
                check_model_qualification(url,query,psql,manifest,app_env)
                from check_model_versions import run_checks as check_model_versions
                check_model_versions(url,query,psql,manifest,app_env)
                from check_provider_test_http import run_checks as check_provider_test_http
                check_provider_test_http(url,query,psql,manifest,app_env,temporary/'app.log',fake_provider,proxy_trap)
                from check_provider_tests import run_checks as check_provider_tests
                check_provider_tests(url,query,psql,manifest,app_env)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    # The browser's two tests reach the provider; its retry and refused test must not.
                    from fake_model_provider import echo
                    reached=len(fake_provider.received);fake_provider.respond(echo(),echo())
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--assistant'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=180):raise RuntimeError('Owned page-assistant settings browser checks failed')
                    if len(fake_provider.received)!=reached+2:raise RuntimeError('The browser tests did not reach the provider exactly twice')
                    print('PASS the browser tests reached the provider exactly twice: a retry and a refused test sent nothing',flush=True)
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
                from check_direct_reads import run_checks as check_direct_reads
                workspace=run_checks(url,query,manifest,lambda views,trades:check_direct_reads(views,trades,dotnet,app_env,temporary))
                from check_table_views import run_checks as check_table_views
                check_table_views(url)
                from check_schedule_storage import run_checks as check_schedule_storage
                check_schedule_storage(query,psql,env,manifest)
                from check_temporal_storage import run_checks as check_temporal_storage
                check_temporal_storage(query,psql,env,manifest,url)
                from check_reference_policy_storage import run_checks as check_reference_policy_storage
                check_reference_policy_storage(query,manifest)
                from check_market_read_authority import run_checks as check_market_authority
                check_market_authority(query,manifest)
                from check_futures_storage import run_checks as check_futures_storage
                check_futures_storage(query,manifest,psql,env)
                from check_futures_amendments import run_checks as check_amendments
                check_amendments(url,query,manifest,psql,env)
                adapter_fixture=temporary/'adapter-fixture.json'
                adapter_fixture.write_text(json.dumps(workspace))
                subprocess.run([str(dotnet),'run','--project','tests/OpenEtrm.Api.Checks','--',str(adapter_fixture)],cwd=ROOT,env=env,check=True,timeout=60)
                from check_seeded_preview import run_checks as check_seeded_preview
                check_seeded_preview(url,run_id,temporary)
                from check_reference_display_http import run_checks as check_reference_display
                check_reference_display(url,query)
                from check_schedule_examples import run_checks as check_schedule_examples
                check_schedule_examples(url,run_id,temporary)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve())],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=360):
                        raise RuntimeError('Owned browser checks failed')
                # Preview sessions are capped, so the checks below share this session instead of each opening one.
                from check_swaps_preview import run_checks as check_swaps
                session=check_swaps(url)
                from check_trading_pin_reads import run_supplemental_checks as check_supplemental_pins
                def check_pin(name,condition=True):
                    if not condition:raise AssertionError(name)
                    print('PASS '+name,flush=True)
                check_supplemental_pins(session,check_pin,query,manifest)
                from check_empty_trading_directory import run_nonempty_checks
                run_nonempty_checks(session,query,manifest)
                from check_swap_payment_rules import run_checks as check_swap_payment_rules
                check_swap_payment_rules(url,session)
                from check_booking_facts import run_checks as check_booking_facts
                check_booking_facts(url,query,manifest,session)
                from check_draft_pricing import run_checks as check_draft
                check_draft(url,query,manifest,session)
                from check_definition_publication import run_checks as check_definition_publication
                check_definition_publication(query,manifest)
                from check_trade_annotations import run_checks as check_trade_annotations
                check_trade_annotations(url,session)
                from check_trade_parties import run_checks as check_trade_parties
                check_trade_parties(url,query,manifest,session)
                from check_units_preview import run_checks as check_units
                check_units(url,client=session)
                from check_trading_reference_access import run_checks as check_reference_authority
                check_reference_authority(query,manifest)
                from check_economic_lookups import run_checks as check_identities
                check_identities(url,query,manifest,session)
                from check_swaps_storage import run_checks as check_swaps_storage
                check_swaps_storage(query,manifest,psql,env)
                from seed_preview_swaps import seed_swaps
                seeded=seed_swaps(url,run_id,temporary,session)
                assert len(seeded)==16 and seeded==seed_swaps(url,run_id,temporary,session)
                print('PASS sixteen synthetic swap examples seed idempotently across all four weekdays',flush=True)
                from check_seeded_preview import run_swap_book_checks
                run_swap_book_checks(session,seeded)
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
