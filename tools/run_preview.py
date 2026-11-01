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
from preview_diagnostics import print_failure_identifiers

ROOT = Path(__file__).resolve().parents[1]
# The sessions of the 64 a scope keeps that this process leaves free once the page-assistant checks have run: the
# browser checks' own and later suites'.
SESSION_MARGIN = 10


def main():
    from check_standalone import main as check_standalone
    check_standalone()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unified-trading-check',action='store_true',help='Verify the common trading workflow across futures, swaps, PPAs and gas')
    parser.add_argument('--with-gas-quantity-dates',action='store_true',help='Include synthetic January working frames for daily gas quantity exploration')
    parser.add_argument('--gas-quantity-benchmark',action='store_true',help='Measure owned daily gas quantity grids with authorized revision history')
    parser.add_argument('--gas-daily-quantity-check',action='store_true',help='Qualify daily trade-owned quantities, temporal selection and corrections')
    parser.add_argument('--gas-composition-check',action='store_true',help='Qualify multiple gas sides and explicit quantity links')
    parser.add_argument('--deal-ticket-check',action='store_true',help='Qualify the deal ticket schedule view of composed gas trades')
    parser.add_argument('--gas-setup-check',action='store_true',help='Qualify versioned gas setup authoring and trade pins')
    parser.add_argument('--physical-gas-check',action='store_true',help='Qualify physical gas preview, durable schedules and booking UI operations')
    parser.add_argument('--demo-instruments-check',action='store_true',help='Verify mixed synthetic PPA and heat-rate bookings, blotter and risk')
    parser.add_argument('--pg-bin', type=Path, required=True, help='Installed stock PG16+ binaries')
    parser.add_argument('--dotnet', type=Path, required=True, help='Installed .NET SDK matching global.json')
    parser.add_argument('--port', type=int, default=0, help='Loopback HTTP port; 0 chooses an unused port')
    parser.add_argument('--no-build', action='store_true', help='Use already-built assets')
    parser.add_argument('--check', action='store_true', help='Run HTTP integration checks, then clean up')
    parser.add_argument('--power-actual-check',action='store_true',help='Qualify exact collected-price interval settlement sources')
    parser.add_argument('--interval-series-check',action='store_true',help='Qualify audited interval series storage and API')
    parser.add_argument('--close-risk-lease-check',action='store_true',help='Qualify renewable risk ownership, real long completion and recovery faults')
    parser.add_argument('--close-risk-check',action='store_true',help='Qualify read-only existing-close PPA risk with actual fenced workers')
    parser.add_argument('--ppa-bookings-check',action='store_true',help='Qualify durable PPA bookings and bounded HTTP valuation')
    parser.add_argument('--ppa-http-benchmark',action='store_true',help='Also measure owned twenty-year hourly and ten-year quarter-hour PPA HTTP previews')
    parser.add_argument('--settlement-quarter-check',action='store_true',help='Qualify one full quarter-hour gross preview in a fresh owned context budget')
    parser.add_argument('--with-ppa',action='store_true',help='Load synthetic PPA references and pricing inputs for interactive exploration')
    parser.add_argument('--ppa-check',action='store_true',help='Qualify isolated PPA quantity, normalization and location-reference consumers')
    parser.add_argument('--interval-curves-check',action='store_true',help='Qualify interval curves, shaping and resolution: storage, API and, with --browser-check, the screen')
    parser.add_argument('--interval-series-benchmark',action='store_true',help='Measure twenty-year interval series workloads in an owned fixture and print a JSON report')
    parser.add_argument('--blotter-stream-benchmark',action='store_true',help='Time the streamed Blotter against trading/workspace in the seeded standard example, then with one twenty-year hourly PPA, and write a JSON report to --output')
    parser.add_argument('--output',type=Path,help='The JSON report file of --blotter-stream-benchmark, outside the repository')
    parser.add_argument('--usage-benchmark',action='store_true',help='Measure a year of page-assistant usage at a stated volume in an owned fixture and print a JSON report')
    parser.add_argument('--index-fixing-check',action='store_true',help='Qualify published monthly and partially fixed daily index observations')
    parser.add_argument('--contract-dates-check',action='store_true',help='Qualify the shared contract-date library')
    parser.add_argument('--pilot-close-check',action='store_true',help='Qualify explicitly generated pilot closes in an owned fixture')
    parser.add_argument('--swap-risk-holdings-check',action='store_true',help='Qualify stored swap risk materialization in an owned fixture')
    parser.add_argument('--close-out-drafts-check',action='store_true',help='Qualify Blotter close-out drafts of futures and swaps, settled periods and refusals in an owned fixture')
    parser.add_argument('--side-observation-check',action='store_true',help='Qualify exact side observation schedules through draft and retained booking operations')
    parser.add_argument('--live-calculation-check',action='store_true',help='Qualify committed live contexts and browser updates')
    parser.add_argument('--connected-portfolio-check',action='store_true',help='Qualify published custom curves in booked portfolio risk and jobs')
    parser.add_argument('--risk-jobs-check',action='store_true',help='Qualify nested risk and durable worker execution in a disposable database')
    parser.add_argument('--factor-swap-check',action='store_true',help='Qualify typed contractual factors and cross-commodity scheduled swaps in an owned fixture')
    parser.add_argument('--scheduled-swap-check',action='store_true',help='Qualify explicit recurring financial swap quantity schedules')
    parser.add_argument('--retained-schedule-check',action='store_true',help='Qualify staged graphs and shared Side reuse across reverse effective boundaries')
    parser.add_argument('--draft-check',action='store_true',help='Qualify temporary deal pricing and exposure')
    parser.add_argument('--hosted-check',action='store_true',help='Qualify local credential persistence in an owned database')
    parser.add_argument('--jobs-check',action='store_true',help='Qualify owned jobs and the shared infrastructure screen')
    parser.add_argument('--reference-import-check',action='store_true',help='Qualify private inactive reference intake in an owned database')
    parser.add_argument('--reference-demo-check',action='store_true',help='Qualify additive demo references and cross-version discounted swaps')
    parser.add_argument('--reference-check',action='store_true',help='Qualify reference metadata authoring and reviewed display activation')
    parser.add_argument('--booking-check',action='store_true',help='Qualify backend-owned immutable trade booking facts')
    parser.add_argument('--unit-check', action='store_true', help='Run amount/unit integration checks in the owned standard fixture')
    parser.add_argument('--execution-check',action='store_true',help='Qualify standard futures execution policies in an owned synthetic fixture')
    parser.add_argument('--custom-curves-check',action='store_true',help='Qualify custom curve definitions and calculations in an owned synthetic fixture')
    parser.add_argument('--market-read-check',action='store_true',help='Qualify per-row market authority in the owned standard fixture')
    parser.add_argument('--market-dates-check',action='store_true',help='Qualify scoped market-close availability and the calendar in an owned fixture')
    parser.add_argument('--valuation-status-check',action='store_true',help='Qualify Blotter reads and risk runs at closes that cannot value every seeded position')
    parser.add_argument('--desk-fields-check',action='store_true',help='Qualify the commodity, swap direction and mark minus price of Blotter rows, commodity pivots, and the desk\'s summary groupings and curve shapes, in the seeded standard example')
    parser.add_argument('--with-custom-curves',action='store_true',help='Load synthetic monthly and daily NG panels for the custom curve editor')
    parser.add_argument('--exchange-check',action='store_true',help='Qualify the independent exchange futures dataset')
    parser.add_argument('--with-exchange',action='store_true',help='Publish the independent exchange futures dataset in this newly owned database')
    parser.add_argument('--party-check', action='store_true', help='Run party-editor integration checks in an owned fixture')
    parser.add_argument('--identity-check', action='store_true', help='Run economic-key integration checks in the owned standard fixture, then clean up')
    parser.add_argument('--provider-adapters-check',action='store_true',help='Qualify additional provider routing and billing through HTTP Test in an owned fixture')
    parser.add_argument('--assistant-check',action='store_true',help='Qualify page-assistant provider, model and price administration in an owned fixture')
    parser.add_argument('--markets-desk-check',action='store_true',help='Run market and delivery desk browser checks in the owned standard fixture')
    parser.add_argument('--browser-check',action='store_true',help='Run checked-in browser scenarios in the newly owned standard fixture')
    parser.add_argument('--browser',type=Path,help='Installed Chromium-compatible executable; used only with --browser-check')
    parser.add_argument('--example', choices=['standard','mixed-gas'], default='standard', help='Explicit synthetic dataset')
    args = parser.parse_args()
    if args.markets_desk_check:
        if any(value for name,value in vars(args).items() if name.endswith(('_check','_benchmark')) and name not in ('markets_desk_check','browser_check')):
            parser.error('--markets-desk-check runs alone; omit other named checks and benchmarks')
        args.check=True
        args.browser_check=True
    if args.reference_import_check:
        args.check=True
        if args.example!='standard' or args.browser_check:
            parser.error('--reference-import-check requires the standard fixture without browser checks')
    if args.settlement_quarter_check:
        args.ppa_check = True
    if args.close_risk_lease_check:
        args.close_risk_check = True
    if args.close_risk_check:
        args.ppa_bookings_check = True
    if args.ppa_http_benchmark:
        args.ppa_bookings_check = True
    if args.unified_trading_check:
        args.demo_instruments_check=True;args.physical_gas_check=True
    if args.gas_quantity_benchmark: args.gas_daily_quantity_check=True
    if args.gas_setup_check or args.gas_composition_check or args.deal_ticket_check or args.gas_daily_quantity_check: args.physical_gas_check=True
    if args.demo_instruments_check: args.check=True
    if args.retained_schedule_check or args.physical_gas_check: args.check=True
    fixing_frames=args.with_gas_quantity_dates or args.gas_daily_quantity_check or args.factor_swap_check or args.scheduled_swap_check or args.index_fixing_check or args.swap_risk_holdings_check or args.side_observation_check or args.close_out_drafts_check
    if args.blotter_stream_benchmark and (args.output is None or args.output.resolve().is_relative_to(ROOT)):
        parser.error('--blotter-stream-benchmark requires an --output file outside the repository')
    if args.interval_series_benchmark or args.usage_benchmark or args.blotter_stream_benchmark:
        if args.example != 'standard' or args.interval_series_benchmark+args.usage_benchmark+args.blotter_stream_benchmark>1 or any(name.endswith('_check') and value for name,value in vars(args).items()):
            parser.error('Each --*-benchmark runs alone in the standard fixture, without another benchmark or any --*-check option')
        args.check = True
    if args.factor_swap_check or args.scheduled_swap_check or args.side_observation_check or args.ppa_bookings_check or args.ppa_check or args.live_calculation_check or args.connected_portfolio_check or args.risk_jobs_check or args.power_actual_check or args.interval_series_check or args.interval_curves_check or args.index_fixing_check or args.swap_risk_holdings_check or args.close_out_drafts_check or args.pilot_close_check or args.contract_dates_check or args.identity_check or args.unit_check or args.party_check or args.exchange_check or args.booking_check or args.draft_check or args.hosted_check or args.jobs_check or args.reference_check or args.execution_check or args.assistant_check or args.provider_adapters_check or args.custom_curves_check or args.market_dates_check or args.market_read_check or args.reference_demo_check or args.valuation_status_check or args.desk_fields_check:
        args.check = True
        if args.example != 'standard' or args.identity_check and args.browser_check:
            parser.error('--identity-check requires the standard fixture without --browser-check')
        if args.market_read_check and args.browser_check:
            parser.error('--market-read-check has no browser scenarios; omit --browser-check')
        if args.desk_fields_check and args.browser_check:
            parser.error('--desk-fields-check has no browser scenarios; omit --browser-check')
    if not 0 <= args.port <= 65535:
        parser.error('Port must be between 0 and 65535')
    if args.browser_check and (not args.check or args.example!='standard' or args.browser is None or not args.browser.is_file()):
        parser.error('--browser-check requires --check, --example standard and an installed --browser executable')
    if args.with_ppa and (args.example!='standard' or args.check):
        parser.error('--with-ppa is an interactive standard-fixture option; use --ppa-check for verification')
    if args.with_custom_curves and args.example!='standard':
        parser.error('--with-custom-curves requires the standard fixture')
    if not args.check and args.example=='standard': args.with_ppa=True
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
    risk_worker_process=None
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
            if (args.assistant_check or args.provider_adapters_check) and migration.name=='0105_model_identifier_policy.sql':
                from check_model_versions import check_identifier_policy_upgrade
                check_identifier_policy_upgrade(query,migration.read_text())
            if args.connected_portfolio_check and migration.name in ('0080_connected_recipe_weights.sql','0081_connected_risk_axes.sql'):
                from check_curve_cutover import run_checks as check_curve_cutover
                check_curve_cutover(query,[reference,market,security],manifest,migration.read_text(),migration.name[:4])
            if args.close_risk_check and migration.name=='0094_close_book_risk.sql':
                from check_close_risk_upgrade import run_checks as check_risk_upgrade
                check_risk_upgrade(query,[reference,market,security,trading_fixture(manifest['catalogId'])],manifest,migration.read_text())
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
            if args.assistant_check and migration.name=='0066_agent_administration.sql':
                from check_agent_administration import check_upgrade as check_agents_upgrade
                check_agents_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0068_agent_grants.sql':
                from check_agent_grants import check_upgrade as check_grants_upgrade
                check_grants_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0069_agent_turns.sql':
                from check_agent_turns import check_upgrade as check_turns_upgrade
                check_turns_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0071_admitted_asks.sql':
                from check_agent_turns import check_admitted_upgrade
                check_admitted_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0072_tool_steps.sql':
                from check_agent_turns import check_steps_upgrade
                check_steps_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0075_model_usage.sql':
                from check_model_usage import check_upgrade as check_usage_upgrade
                check_usage_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0078_turn_stream.sql':
                from check_agent_turns import check_stream_upgrade
                check_stream_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0079_workspace_grants.sql':
                from check_agent_grants import check_workspace_upgrade
                check_workspace_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0089_curve_agent_proposals.sql':
                from check_agent_turns import check_curve_proposals_upgrade
                check_curve_proposals_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0119_agent_job_reads.sql':
                from check_agent_turns import check_job_reads_upgrade
                check_job_reads_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0086_navigation_targets.sql':
                from check_agent_turns import check_navigation_upgrade
                check_navigation_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0087_hosted_turn_sessions.sql':
                from check_agent_turns import check_hosted_upgrade
                check_hosted_upgrade(query,migration.read_text())
            if args.check and args.example=='standard' and not args.exchange_check and migration.name=='0052_unambiguous_discount_defaults.sql':
                from check_discount_default_upgrade import run_checks as check_default_upgrade
                from swap_fixture import fixture as swap_fixture
                from preview_fixture import trading_fixture
                check_default_upgrade(query,[reference,market,swap_fixture(fixing_frames=fixing_frames)[0],security,trading_fixture(manifest['catalogId'],swap_defaults=True)],migration.read_text())
            if args.assistant_check and migration.name=='0120_agent_read_limit.sql':
                from check_agent_administration import check_read_limit_upgrade
                check_read_limit_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0121_agent_history_window.sql':
                from check_agent_administration import check_history_window_upgrade
                check_history_window_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0122_pin_turn_bound.sql':
                from check_agent_turns import check_pin_bound_upgrade
                check_pin_bound_upgrade(query,migration.read_text())
            if args.assistant_check and migration.name=='0128_agent_curve_drafts.sql':
                from check_agent_turns import check_curve_drafts_upgrade
                check_curve_drafts_upgrade(query,migration.read_text())
            if args.check and migration.name=='0116_remove_unused_cash_rounding.sql':
                from check_cash_rounding_retirement import run_checks as check_cash_retirement
                check_cash_retirement(query,migration.read_text())
            if args.physical_gas_check and migration.name=='0142_physical_gas_bookings.sql':
                from check_gas_upgrade import run_checks as check_gas_upgrade
                check_gas_upgrade(query,ROOT,[reference,market,security,trading_fixture(manifest['catalogId'])])
            query(migration.read_text())
        if args.check:
            from check_whole_term_swap_schema import run_checks as check_whole_term_swap_schema
            check_whole_term_swap_schema(query)
            from check_ppa_side_schedule_schema import run_checks as check_ppa_side_schedule_schema
            check_ppa_side_schedule_schema(query)
            from check_retained_side_schedules import run_checks as check_retained_side_schedules
            check_retained_side_schedules(query)
            from check_booked_side_programs import run_schema_checks as check_booked_program_schema
            check_booked_program_schema(query)
        if args.execution_check:
            from check_futures_execution import prepare as prepare_execution
            reference,market,manifest=prepare_execution(reference,market,manifest)
        if args.index_fixing_check:
            from check_index_fixings import prepare as prepare_index_fixings
            reference,market,manifest=prepare_index_fixings(reference,market,manifest)
        if args.interval_curves_check or args.ppa_check or args.ppa_bookings_check or args.with_ppa or args.demo_instruments_check or args.retained_schedule_check or args.blotter_stream_benchmark:
            from interval_curve_fixture import prepare as prepare_interval_curves
            reference,market,security,manifest=prepare_interval_curves(reference,market,security,manifest)
        if args.ppa_check or args.ppa_bookings_check or args.with_ppa or args.demo_instruments_check or args.retained_schedule_check or args.blotter_stream_benchmark:
            from ppa_fixture import prepare as prepare_ppa
            reference,market,security,manifest=prepare_ppa(reference,market,security,manifest)
            if not args.check:
                from seed_preview_positions import EXECUTIONS
                demo_instants={day+'T16:00:00' for day,*_ in EXECUTIONS}
                manifest['frames']=[frame for frame in manifest['frames'] if frame['at'][:19] in demo_instants or args.with_gas_quantity_dates and frame['at'][:10] in ('2021-01-01','2021-01-15','2021-01-31')]

        if args.ppa_bookings_check or args.retained_schedule_check or args.blotter_stream_benchmark:
            from ppa_bookings_fixture import prepare as prepare_ppa_bookings
            reference,market,security,manifest=prepare_ppa_bookings(reference,market,security,manifest)
            if args.close_risk_check:
                from close_risk_fixture import prepare as prepare_close_risk
                reference,market,security,manifest=prepare_close_risk(reference,market,security,manifest)
        if args.live_calculation_check or args.connected_portfolio_check or args.custom_curves_check or args.with_custom_curves or args.market_dates_check or args.valuation_status_check:
            from custom_curve_fixture import prepare as prepare_custom_curves
            reference,market,manifest=prepare_custom_curves(reference,market,manifest,daily_lifecycle=args.connected_portfolio_check)
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
            # The owner holds the six human roles by name, never the agent reader role.
            human_roles=f"DO $$BEGIN IF(SELECT string_agg(r.code,',' ORDER BY r.code) FROM security.role_grants g JOIN security.roles r ON r.id=g.role_id WHERE g.principal_id='{prepared_ids['owner']}' AND NOT g.revoked)<>'access_administrator,market_data_approver,market_data_editor,risk_analyst,trader,viewer' THEN RAISE EXCEPTION 'The hosted owner must hold exactly the human roles';END IF;END$$;"
            combined=(prepared/'seed.sql').read_text().replace('COMMIT;','')+calendar_sql.read_text().replace('BEGIN;','').replace('COMMIT;',"DO $$BEGIN IF NOT EXISTS(SELECT 1 FROM power_data.operating_days WHERE operating_date='2026-03-08' AND ends_at-starts_at=interval '23 hours') OR NOT EXISTS(SELECT 1 FROM power_data.operating_days WHERE operating_date='2026-11-01' AND ends_at-starts_at=interval '25 hours') THEN RAISE EXCEPTION 'Incorrect retained operating-day bounds';END IF;END$$;"+human_roles+"ROLLBACK;")
            query(combined)

            print('PASS fresh hosted seed validates on an empty stock database without synthetic users, observations or trades, and grants its owner the human roles',flush=True)
        query(reference)
        if args.power_actual_check:
            from check_power_actual_storage import prepare_market
            market=prepare_market(market,manifest)
        if args.interval_series_check:
            from check_interval_series_http import prepare_market as prepare_interval_series
            market=prepare_interval_series(market,manifest)
        if args.scheduled_swap_check:
            from check_scheduled_swaps import prepare as prepare_scheduled
            market=prepare_scheduled(market,manifest)
        if args.close_out_drafts_check:
            from check_close_out_drafts import prepare as prepare_close_out_drafts
            market=prepare_close_out_drafts(market,manifest)
        query(market)
        if args.example=='standard':
            from swap_fixture import fixture as swap_fixture
            # The valuation-status check reads the last custom-curve close, at which the daily bundle publishes.
            swap_market,swap_manifest=swap_fixture(fixing_frames=fixing_frames,observation_guards=args.side_observation_check or args.scheduled_swap_check,
                scheduled_periods=args.scheduled_swap_check,closes=['2020-12-31T16:00:00Z'] if args.valuation_status_check else (),
                effective_from='2020-12-16' if args.retained_schedule_check else '2020-01-01')
            if args.index_fixing_check:
                from check_swap_settlement_selection import prepare_daily
                swap_market=prepare_daily(swap_market,swap_manifest)
            query(swap_market)
            if args.connected_portfolio_check or args.valuation_status_check or args.with_custom_curves:
                # The primary custom-curve fixture visits closes beyond the four standard weekdays.
                # Its unrelated secondary bundle explicitly supplies no inputs there.
                times={f['at'] for f in swap_manifest['frames']}
                swap_manifest['frames'].extend({'at':f['at'],'observationIds':[],'curveContracts':{}}
                    for f in manifest['frames'] if f['at'] not in times)
            manifest['swapMarkets']=[swap_manifest]
            if args.factor_swap_check:
                from factor_swap_fixture import prepare as prepare_factor
                prepare_factor(manifest)
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
        if args.ppa_bookings_check or args.blotter_stream_benchmark:
            from ppa_bookings_fixture import trading as ppa_benchmark_books
            query(ppa_benchmark_books(manifest['catalogId']))
        if args.ppa_bookings_check:
            from ppa_revision_fixture import install as install_ppa_revisions
            install_ppa_revisions(query,manifest)
        if args.power_actual_check:
            from check_power_actual_storage import run_checks as check_power_actual_storage
            check_power_actual_storage(query,manifest)
        if args.interval_curves_check:
            from check_interval_curves_storage import run_checks as check_interval_curves_storage
            check_interval_curves_storage(query,manifest)
        # The close-risk fixture has a contemporary definition period; the
        # unchanged temporal baseline runs separately in --ppa-bookings-check.
        if args.ppa_bookings_check and not args.close_risk_check:
            from check_pre_retention_storage import ppa as check_ppa_raw_admission
            check_ppa_raw_admission(query,psql,env,ROOT,
                [reference,market,swap_market,security,trading_fixture(manifest['catalogId'],swap_defaults=True)],manifest)
        if args.ppa_check:
            from check_settlement_preview import prepare as prepare_settlement_preview
            manifest=prepare_settlement_preview(manifest)
            from check_ppa_storage import run_checks as check_ppa_storage
            check_ppa_storage(query,manifest)
            from check_meter_finality import run_checks as check_meter_finality
            check_meter_finality(query,manifest,psql,env,dotnet,temporary)
        if args.interval_series_check:
            from check_interval_series_storage import run_checks as check_interval_series_storage
            check_interval_series_storage(query,manifest,psql,env)
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
        if args.factor_swap_check:
            from check_factor_swap_upgrade import run_checks as check_factor_upgrade
            check_factor_upgrade(query,psql,env,ROOT)
        if args.check and args.example=='standard' and not args.exchange_check:
            from check_trade_revision_upgrade import run_checks as check_upgrade
            legacy_reference,legacy_market,legacy_security,_=fixture(ROOT,current_schema=False)
            check_upgrade(query,psql,env,ROOT,[legacy_reference,legacy_market,legacy_security,trading_fixture(manifest['catalogId'],annotations=False)],manifest)
        if args.jobs_check:
            from check_jobs_http import prepare as prepare_jobs
            prepare_jobs(query,manifest)
        if args.reference_demo_check:
            from reference_demo_fixture import install as install_reference_demo
            reference_demo=install_reference_demo(query,manifest)
            # One owned test date deliberately has no admitted demo input frame.
            reference_demo['omittedAt']=max(f['at'] for f in reference_demo['frames'] if not f['at'].startswith('2020-12-18'))
            manifest['swapMarkets'][-1]['frames']=[f for f in reference_demo['frames'] if f['at']!=reference_demo['omittedAt']]
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
        if args.assistant_check or args.provider_adapters_check:
            # Provider tests reach a loopback fake provider through the check endpoint, and every proxy
            # setting names a trap that no request may reach (docs/ai-assistant.md §5.2, §13).
            from fake_model_provider import FakeProvider,Trap
            fake_provider,proxy_trap=FakeProvider(),Trap()
            api_env={**app_env,'OPENETRM_PROVIDER_CHECK_ENDPOINT':fake_provider.url,'no_proxy':'','NO_PROXY':'',
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
        def prepare_risk_worker():
            from check_risk_jobs_db import provision_worker
            provision_worker(query,manifest)
            connection=temporary/'risk-worker-connection'
            connection.write_text(f'Host={temporary};Port=55439;Database=postgres;Username=preview_risk_worker;SSL Mode=Disable')
            connection.chmod(0o600)
            config=temporary/'risk-worker.json'
            config.write_text(json.dumps({'connectionFile':str(connection),'leaseSeconds':30,'pollSeconds':1,'timeoutSeconds':60}))
            config.chmod(0o600)
            return {**env,'OPENETRM_RISK_WORKER_CONFIG':str(config)}
        def risk_worker_runner():
            worker_env=prepare_risk_worker()
            def run():
                subprocess.run([str(dotnet),str(frozen_dll),'--risk-worker','once'],cwd=ROOT,env=worker_env,check=True,timeout=90)
            return run
        if (not args.check or args.live_calculation_check or args.unit_check or args.risk_jobs_check or args.valuation_status_check or args.desk_fields_check) and args.example=='standard':
            from seed_preview_positions import seed_positions
            seed_positions(url,run_id,temporary)
            from seed_preview_swaps import seed_swaps
            seed_swaps(url,run_id,temporary)
            print('Loaded eight futures and sixteen financial swap examples across four weekdays.',flush=True)
            if not args.check:
                from seed_demo_instruments import owned_client,seed_heat_rates
                seed_heat_rates(owned_client(url,run_id,temporary))
                print('Loaded eight synthetic heat-rate swaps with explicit side quantities and native-unit rates.',flush=True)

            if not args.check:
                from seed_schedule_examples import seed_schedule_examples
                seed_schedule_examples(url,run_id,temporary)
                print('Published the synthetic monthly gas date schedule; daily power contract dates are backend-loadable session candidates.',flush=True)
                from delivery_price_seed import seed as seed_delivery_prices
                seed_delivery_prices(query,manifest)
                print('Collected seven synthetic days of hourly and quarter-hour delivery prices, including the 2026-11-01 fall-back day.',flush=True)
        if args.with_exchange and not args.check:
            from seed_exchange_positions import seed_positions as seed_exchange
            seed_exchange(url,run_id,temporary)
            print('Loaded twelve source-listed monthly futures examples with original synthetic execution prices.',flush=True)
        if args.with_ppa:
            from seed_preview_ppa import seed_ppa
            seed_ppa(url,manifest)
            from seed_demo_instruments import owned_client,seed_ppas
            seed_ppas(owned_client(url,run_id,temporary),manifest)
            print('Loaded five synthetic physical, virtual and generation-linked PPA trades in the mixed book.',flush=True)
        print(f'PREVIEW_URL={url}',flush=True)
        print('Private synthetic database. Ctrl+C stops the app and removes its owned database.',flush=True)
        if args.check:
            if args.unified_trading_check:
                from check_demo_instruments import run_checks as check_demo
                run_demo_worker=risk_worker_runner()
                check_demo(url,manifest,query,run_worker=run_demo_worker,owned_root=temporary,run_id=run_id,psql=psql,environment=env)
                from check_physical_gas_preview import run_checks as check_single
                check_single(url)
                from check_gas_composition import run_checks as check_composed
                check_composed(url,query,manifest,run_worker=run_demo_worker)
                if args.browser_check:
                    target=temporary/'browser-target.json';target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    subprocess.run(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--unified-trading'],cwd=ROOT,env=env,check=True,timeout=240)
            elif args.physical_gas_check:
                from check_physical_gas_preview import run_checks,run_shape_checks
                gas_receipt=run_checks(url)
                run_shape_checks(query,manifest,gas_receipt)
                if args.gas_daily_quantity_check:
                    from check_gas_daily_quantities import run_checks as check_gas_daily_quantities
                    check_gas_daily_quantities(url,query,manifest)
                    if args.gas_quantity_benchmark:
                        from benchmark_gas_quantities import run as benchmark_gas_quantities
                        benchmark_gas_quantities(url,query,manifest,child.pid)
                if args.gas_composition_check:
                    from check_gas_composition import run_checks as check_gas_composition
                    check_gas_composition(url,query,manifest,run_worker=risk_worker_runner())
                if args.deal_ticket_check:
                    from check_deal_ticket import run_checks as check_deal_ticket
                    check_deal_ticket(url)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    subprocess.run(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--physical-gas'],cwd=ROOT,env=env,check=True,timeout=180)
                    if args.deal_ticket_check:
                        subprocess.run(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--deal-ticket'],cwd=ROOT,env=env,check=True,timeout=300)
                if args.gas_setup_check:
                    from check_gas_setup_editor import run_checks as check_gas_setup
                    check_gas_setup(url)
            elif args.retained_schedule_check:
                from check_retained_side_schedules import run_stage_checks,run_temporal_checks
                run_stage_checks(query,manifest)
                booked_program_instruments,before_programs=run_temporal_checks(url,query,manifest)
                from check_booked_side_programs import run_direct_read_checks,run_authority_checks,book_native_programs
                booked_program_instruments+=book_native_programs(url,manifest,query)
                booked_program_items=run_direct_read_checks(query,manifest,temporary,dotnet,app_env,booked_program_instruments,
                    ('fixed','floating','power','certificate','capacity'))
                run_authority_checks(query,manifest,booked_program_items[0],before_programs)
            elif args.demo_instruments_check:
                from check_demo_instruments import run_checks
                run_demo_worker=risk_worker_runner()
                run_checks(url,manifest,query,run_worker=run_demo_worker,owned_root=temporary,run_id=run_id,psql=psql,environment=env)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    subprocess.run(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--demo-instruments'],cwd=ROOT,env=env,check=True,timeout=240)
            elif args.live_calculation_check:
                from check_live_calculation import run_checks as check_live_calculation
                check_live_calculation(url,manifest,query)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--live-calculation'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=240):raise RuntimeError('Owned live calculation browser checks failed')
            elif args.connected_portfolio_check:
                from check_custom_projection_db import run_checks as check_custom_projection_db
                check_custom_projection_db(query,manifest)
                from check_connected_portfolio import run_checks as check_connected_portfolio
                worker_env=prepare_risk_worker()
                def run_worker():
                    subprocess.run([str(dotnet),str(frozen_dll),'--risk-worker','once'],cwd=ROOT,env=worker_env,check=True,timeout=90)
                check_connected_portfolio(url,manifest,query,run_worker)
                from check_published_curve_sources import run_checks as check_published_curve_sources
                check_published_curve_sources(url,manifest,query,run_worker)
                from check_curve_activation import run_checks as check_curve_activation
                check_curve_activation(url,manifest,query)
                from check_daily_custom_projection import run_checks as check_daily_custom_projection
                check_daily_custom_projection(url,manifest,query,run_worker)
                from check_curve_lookup import run_checks as check_curve_lookup
                check_curve_lookup(url,manifest,query,run_worker)
            elif args.risk_jobs_check:
                from preview_client import PreviewClient
                from check_risk_preview import run_checks as check_risk
                from check_risk_jobs import run_checks as check_risk_jobs
                from check_portfolio_risk import run_checks as check_portfolio_risk
                from check_portfolio_risk_authority import run_checks as check_portfolio_risk_authority
                from check_risk_jobs_db import run_checks as check_risk_jobs_db
                class Client(PreviewClient):
                    def __init__(self): super().__init__(url)
                def check(name,condition=True):
                    if not condition: raise AssertionError(name)
                    print('PASS '+name,flush=True)
                worker_env=prepare_risk_worker()
                def run_worker():
                    subprocess.run([str(dotnet),str(frozen_dll),'--risk-worker','once'],cwd=ROOT,env=worker_env,check=True,timeout=90)
                from check_curve_scenarios import run_checks as check_curve_scenarios
                check_curve_scenarios(url,run_worker)
                check_portfolio_risk_authority(Client,query,check,manifest,temporary)
                check_portfolio_risk(Client,check,run_worker)
                check_risk(Client,check)
                check_risk_jobs(Client,check,run_worker)
                check_risk_jobs_db(query,manifest,psql,env)
                from check_portfolio_risk import run_supplemental_scope
                run_supplemental_scope(Client,check,query,manifest,run_worker)
                from check_risk_run_definitions import run_now_checks
                run_now_checks(url,query,run_worker)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    risk_worker_process=subprocess.Popen([str(dotnet),str(frozen_dll),'--risk-worker','serve'],cwd=ROOT,env=worker_env,start_new_session=True)
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--risk'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=360):raise RuntimeError('Owned risk browser checks failed')
            elif args.interval_series_benchmark:
                from benchmark_interval_series import run_benchmark
                print(json.dumps(run_benchmark(url,query,dotnet,child.pid),indent=2),flush=True)
            elif args.blotter_stream_benchmark:
                from benchmark_blotter_stream import run_benchmark as run_blotter_stream_benchmark
                run_blotter_stream_benchmark(url,manifest,query,dotnet=dotnet,api_pid=child.pid,run_id=run_id,owned_root=temporary,output=args.output.resolve())
            elif args.usage_benchmark:
                from benchmark_usage import run_benchmark as run_usage_benchmark
                print(json.dumps(run_usage_benchmark(url,query,psql,manifest,app_env,dotnet,child.pid),indent=2),flush=True)
            elif args.close_risk_lease_check:
                from check_close_risk_leases import run_checks as check_close_risk_leases
                check_close_risk_leases(url,query,manifest,dotnet,temporary,app_env,psql,env)
            elif args.close_risk_check:
                from check_close_risk import run_checks as check_close_risk
                check_close_risk(url,query,manifest,dotnet,temporary,app_env,psql,env)
            elif args.ppa_bookings_check:
                from check_ppa_portfolio import run_checks as check_ppa_portfolio
                check_ppa_portfolio(url,query,manifest,dotnet,temporary,app_env)
                from check_ppa_bookings_http import run_checks as check_ppa_bookings_http
                check_ppa_bookings_http(url,query,manifest,benchmark=args.ppa_http_benchmark,api_pid=child.pid,dotnet=dotnet)
                from check_ppa_ui_http import run_checks as check_ppa_ui_http
                ppa_browser_fixture=check_ppa_ui_http(url,query,manifest)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id,'ppa':ppa_browser_fixture}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--ppa'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=240):raise RuntimeError('Owned PPA browser checks failed')
                    if evidence:=os.environ.get('OPENETRM_PPA_BROWSER_EVIDENCE'):
                        destination=Path(evidence).resolve();destination.mkdir(parents=True,exist_ok=True)
                        for screenshot in temporary.glob('ppa-*.png'):shutil.copy2(screenshot,destination/screenshot.name)

            elif args.ppa_check:
                from check_ppa_http import run_checks as check_ppa_http
                check_ppa_http(url,query,manifest)
                from check_ppa_break_even import run_checks as check_ppa_break_even
                check_ppa_break_even(url,query,manifest)
                from check_settlement_preview import run_checks as check_settlement_preview
                check_settlement_preview(url,query,manifest,dotnet,temporary,env,quarter_only=args.settlement_quarter_check)
            elif args.power_actual_check:
                from check_power_actual_http import run_checks as check_power_actual_http
                check_power_actual_http(url,query,manifest)
            elif args.interval_curves_check:
                from check_interval_curves_http import run_checks as check_interval_curves_http
                check_interval_curves_http(url,query,manifest)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--interval-curves'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=180):raise RuntimeError('Owned interval curves browser checks failed')
            elif args.interval_series_check:
                from check_interval_series_http import run_checks as check_interval_series_http
                check_interval_series_http(url,query,manifest)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--interval-series'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=180):raise RuntimeError('Owned interval series browser checks failed')
            elif args.side_observation_check:
                from check_side_observations import run_checks as check_side_observations
                check_side_observations(url,query,manifest,psql,app_env)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--side-observations'],cwd=ROOT,env=env,start_new_session=True)
                    # This combines 26 observation checks and the 171 swap checks
                    # whose standalone suite has a 300s budget. Linux reached
                    # 194/195 with continuous progress before the former 240s
                    # cutoff. Individual action and application deadlines stay unchanged.
                    if browser_process.wait(timeout=360):raise RuntimeError('Owned side observation browser checks failed')
            elif args.swap_risk_holdings_check:
                from check_swap_risk_holdings import run_checks as check_swap_risk_holdings
                check_swap_risk_holdings(url,query,manifest,dotnet,temporary,app_env,psql)
            elif args.close_out_drafts_check:
                from check_close_out_drafts import run_checks as check_close_out_drafts
                close_out_fixture=check_close_out_drafts(url,query,dotnet,temporary,app_env)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id,'closeOut':close_out_fixture}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--close-out'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=240):raise RuntimeError('Owned Blotter close-out browser checks failed')
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
            elif args.factor_swap_check:
                from check_factor_swaps import run_checks as check_factor_swaps
                worker_env=prepare_risk_worker()
                def run_worker():
                    subprocess.run([str(dotnet),str(frozen_dll),'--risk-worker','once'],cwd=ROOT,env=worker_env,check=True,timeout=90)
                check_factor_swaps(url,query,manifest,run_worker)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--factor-swaps'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=180):raise RuntimeError('Owned factor swap browser checks failed')
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
            elif args.reference_import_check:
                from check_reference_import import run_checks as check_reference_import
                check_reference_import(query,manifest,temporary,dotnet,env)
            elif args.reference_demo_check:
                from check_reference_demo import run_checks as check_reference_demo
                worker_env=prepare_risk_worker()
                def run_worker():
                    subprocess.run([str(dotnet),str(frozen_dll),'--risk-worker','once'],cwd=ROOT,env=worker_env,check=True,timeout=90)
                check_reference_demo(url,manifest,reference_demo,query,run_worker)
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
                check_hosted_http(temporary,manifest,query,dotnet,frozen_dll,app_env,args.browser if args.browser_check else None,url,run_id,psql=psql,psql_environment=env)
            elif args.scheduled_swap_check:
                from check_scheduled_swaps import run_checks as check_scheduled
                worker_env=prepare_risk_worker()
                def run_worker():
                    subprocess.run([str(dotnet),str(frozen_dll),'--risk-worker','once'],cwd=ROOT,env=worker_env,check=True,timeout=90)
                settlement_fixture=check_scheduled(url,query,manifest,run_worker=run_worker)
                from check_swap_settlement_agents import prepare as prepare_settlement_agent
                agent_fixture=temporary/'settlement-agent.json'
                agent_fixture.write_text(json.dumps(prepare_settlement_agent(query,settlement_fixture)))
                subprocess.run([str(dotnet),'run','--project','tests/OpenEtrm.Api.Checks','--','--swap-holding-markets',str(agent_fixture)],cwd=ROOT,env=app_env,check=True,timeout=120)
                subprocess.run([str(dotnet),'run','--project','tests/OpenEtrm.Api.Checks','--','--swap-settlement-agent',str(agent_fixture)],cwd=ROOT,env=app_env,check=True,timeout=120)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--scheduled-swaps'],cwd=ROOT,env={**env,'OPENETRM_BROWSER_ACTION_WAIT_MS':'30000'},start_new_session=True)
                    # Four owned browser suites: authoring, schedules, settlement and netting.
                    # Every action here re-reads scheduled swaps, so each may wait up to thirty seconds; keep an overall bound too.
                    if browser_process.wait(timeout=900):raise RuntimeError('Owned scheduled-swap browser checks failed')
            elif args.draft_check:
                from check_draft_pricing import run_checks as check_draft
                check_draft(url,query,manifest)
                from check_swap_composition import run_checks as check_composition
                check_composition(url,query,manifest)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--swaps'],cwd=ROOT,env=env,start_new_session=True)
                    # This suite has 169 checks. Two Linux runs made continuous
                    # progress through 149/150 before the former 180s whole-suite
                    # cutoff. Individual action and application deadlines stay unchanged.
                    if browser_process.wait(timeout=300):raise RuntimeError('Owned swap browser checks failed')
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
            elif args.valuation_status_check:
                from check_blotter_valuation_status import run_checks as check_valuation_status, run_risk_checks as check_valuation_status_risk, run_scheduled_checks as check_scheduled_valuation_status
                worker_env=prepare_risk_worker()
                def run_worker():
                    subprocess.run([str(dotnet),str(frozen_dll),'--risk-worker','once'],cwd=ROOT,env=worker_env,check=True,timeout=90)
                valuation_session=check_valuation_status(url)
                check_valuation_status_risk(valuation_session,run_worker)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--valuation-status'],cwd=ROOT,env=env,start_new_session=True)
                    if browser_process.wait(timeout=180):raise RuntimeError('Owned valuation status browser checks failed')
                # Last, as it books a scheduled swap beside the seeded positions the checks above count.
                check_scheduled_valuation_status(valuation_session)
            elif args.desk_fields_check:
                from check_blotter_desk_fields import run_checks as check_desk_fields
                check_desk_fields(url)
            elif args.provider_adapters_check:
                from preview_client import PreviewClient
                from check_additional_providers import run_checks as check_additional_providers
                admin=PreviewClient(url);admin.bootstrap();admin.call('profile',{'profile':'admin'})
                admin.call('admin/create',{'operationId':str(uuid4()),'login':'provider_adapters_admin','name':'Synthetic provider administrator',
                                          'kind':'human','roles':['access_administrator','viewer']})
                admin.call('profile',{'profile':'provider_adapters_admin'})
                check_additional_providers(query,manifest,fake_provider,proxy_trap,admin)
            elif args.assistant_check:
                # First, while nothing else can be writing to the application log.
                from check_request_refusals import run_checks as check_request_refusals
                # The preview holds at most 64 sessions, so the modules reuse the administrator's, a second of the
                # administrator's and the viewer's sessions instead of each opening its own.
                admin=check_request_refusals(url,temporary/'app.log')
                from check_model_providers import run_checks as check_model_providers
                viewer,again=check_model_providers(url,query,psql,manifest,temporary,app_env,dotnet,admin)
                from check_model_catalog import run_checks as check_model_catalog
                check_model_catalog(url,query,psql,manifest,app_env,admin,viewer,again)
                from check_model_qualification import run_checks as check_model_qualification
                check_model_qualification(query,psql,manifest,app_env,admin,viewer,again)
                from check_model_versions import run_checks as check_model_versions
                check_model_versions(query,psql,manifest,app_env,admin,viewer,again)
                from check_provider_test_http import run_checks as check_provider_test_http
                check_provider_test_http(url,query,psql,manifest,app_env,temporary/'app.log',fake_provider,proxy_trap,admin,viewer,again)
                from check_provider_tests import run_checks as check_provider_tests
                check_provider_tests(query,psql,manifest,app_env,admin)
                from check_agent_administration import run_checks as check_agent_administration
                check_agent_administration(query,psql,manifest,app_env,admin,viewer,again)
                from check_agent_grants import run_checks as check_agent_grants
                check_agent_grants(url,query,psql,manifest,app_env,admin,viewer)
                from check_agent_turns import run_checks as check_agent_turns
                check_agent_turns(url,query,psql,manifest,app_env,dotnet)
                from check_agent_turn_http import run_checks as check_agent_turn_http
                sessions=check_agent_turn_http(url,query,psql,manifest,app_env,temporary/'app.log',fake_provider,proxy_trap)
                from check_agent_tool_http import run_checks as check_agent_tool_http
                check_agent_tool_http(url,query,psql,manifest,app_env,temporary/'app.log',fake_provider,proxy_trap,*sessions,api_pid=child.pid)
                from check_turn_stream_http import run_checks as check_turn_stream_http
                check_turn_stream_http(url,query,psql,manifest,app_env,fake_provider,proxy_trap,*sessions)
                from check_model_usage import run_checks as check_model_usage
                check_model_usage(url,query,psql,manifest,app_env)
                from check_additional_providers import run_checks as check_additional_providers
                check_additional_providers(query,manifest,fake_provider,proxy_trap,admin)
                # A scope keeps at most 64 sessions for their 30 minutes (Program.cs). The browser checks below open their
                # own, as will later page-assistant suites, so the sessions this process has opened so far, in every suite
                # before this point, leave a stated margin for them.
                from preview_client import PreviewClient
                opened,cap,margin=PreviewClient.sessions['baseline'],64,SESSION_MARGIN
                if opened+margin>cap:raise RuntimeError(f'This process opened {opened} of the {cap} sessions a scope keeps by the end of the page-assistant checks, leaving fewer than {margin}.')
                print(f'PASS this process opened {opened} of the {cap} sessions a scope keeps by the end of the page-assistant checks, leaving {cap-opened}, at least {margin} for the browser and later suites',flush=True)
                if args.browser_check:
                    target=temporary/'browser-target.json'
                    # The panel's scenario scripts the fake provider's answers, and releases the ones it holds, through its control listener.
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id,'provider':fake_provider.control()}))
                    # Roles holding one of Settings › AI's permissions each, so the browser sees every tab offered by its own.
                    query("INSERT INTO security.roles(id,code,name) VALUES(gen_random_uuid(),'synthetic_agents_admin','Synthetic agents administrator'),"
                          "(gen_random_uuid(),'synthetic_models_admin','Synthetic models administrator'),(gen_random_uuid(),'synthetic_usage_reader','Synthetic usage reader');"
                          "INSERT INTO security.role_permissions SELECT id,'ManageAgents' FROM security.roles WHERE code='synthetic_agents_admin';"
                          "INSERT INTO security.role_permissions SELECT id,'ManageModelProviders' FROM security.roles WHERE code='synthetic_models_admin';"
                          "INSERT INTO security.role_permissions SELECT id,'ReadModelUsage' FROM security.roles WHERE code='synthetic_usage_reader';")
                    # Settings › AI's two tests and Usage's one reach the provider; the retry and the refused test must not.
                    from fake_model_provider import echo
                    reached=len(fake_provider.received);fake_provider.respond(echo(),echo(),echo())
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),'--assistant'],cwd=ROOT,env=env,start_new_session=True)
                    # Settings › AI, its Agents and Usage tabs and the assistant panel run in one browser process.
                    if browser_process.wait(timeout=600):raise RuntimeError('Owned page-assistant browser checks failed')
                    if len(fake_provider.received)!=reached+3+fake_provider.controlled or fake_provider.script:
                        raise RuntimeError('The browser tests did not reach the provider exactly once for each answer they scripted and three times besides')
                    print('PASS the browser tests reached the provider once for each answer the panel scripted and three times besides: a retry and a refused test sent nothing',flush=True)
            elif args.identity_check:
                from check_units_preview import run_checks as check_units
                check_units(url)
                from check_trading_reference_access import run_checks as check_reference_authority
                check_reference_authority(query,manifest,url)
                from check_economic_lookups import run_checks as check_identities
                check_identities(url,query,manifest)
                from check_trading_reference_allocation import run_checks as check_reference_allocation, run_http_checks as check_reference_allocation_http
                check_reference_allocation(query,manifest,psql,env)
                check_reference_allocation_http(url,query)
                from check_trading_history_boundary import run_checks as check_trading_history, run_http_checks as check_trading_history_http
                check_trading_history(query,manifest)
                check_trading_history_http(query,manifest,url,temporary)
            elif args.example=='mixed-gas':
                from check_mixed_preview import run_checks
                run_checks(url,json.loads(export.read_text()))
            else:
                from check_preview import run_checks
                from check_direct_reads import run_checks as check_direct_reads
                after_browser=[]
                workspace=run_checks(url,query,manifest,lambda views,trades:check_direct_reads(views,trades,dotnet,app_env,temporary),after_browser)
                from check_table_views import run_checks as check_table_views
                check_table_views(url,query)
                # Preview sessions are capped, so the account checks below share two browser sessions, each switching them to its own users.
                from preview_client import PreviewClient
                browsers=[PreviewClient(url) for _ in range(2)]
                for browser in browsers:browser.bootstrap()
                from check_dashboard_layout import run_checks as check_dashboard_layout
                check_dashboard_layout(url,query,browsers)
                from check_risk_run_definitions import run_checks as check_risk_run_definitions
                check_risk_run_definitions(url,query,browsers)
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
                    # The Delivery prices desk checks read the interactive preview's synthetic week; the HTTP checks above ran without it.
                    from delivery_price_seed import seed as seed_delivery_prices
                    seed_delivery_prices(query,manifest)
                    worker_env=prepare_risk_worker()
                    risk_worker_process=subprocess.Popen([str(dotnet),str(frozen_dll),'--risk-worker','serve'],cwd=ROOT,env=worker_env,start_new_session=True)
                    target=temporary/'browser-target.json'
                    target.write_text(json.dumps({'suite':'business_browser_v1','url':url,'runId':run_id}))
                    browser_process=subprocess.Popen(['node',str(ROOT/'tests/browser/run.mjs'),str(target),str(args.browser.resolve()),*(['--markets-desk'] if args.markets_desk_check else [])],cwd=ROOT,env=env,start_new_session=True)
                    # The desk's and the Blotter workbench's scenarios run in this suite; a CI runner needs about a quarter of an hour for them.
                    if browser_process.wait(timeout=1500):
                        raise RuntimeError('Owned browser checks failed')
                    if evidence:=os.environ.get('OPENETRM_MARKETS_BROWSER_EVIDENCE'):
                        destination=Path(evidence).resolve();destination.mkdir(parents=True,exist_ok=True)
                        for screenshot in temporary.glob('markets-*.png'):shutil.copy2(screenshot,destination/screenshot.name)
                # Checks that record durable closes would add samples to the browser suites' history, so they run after them.
                for later in after_browser:later()
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
                check_reference_authority(query,manifest,url,session)
                from check_economic_lookups import run_checks as check_identities
                check_identities(url,query,manifest,session)
                from check_trading_reference_allocation import run_checks as check_reference_allocation, run_http_checks as check_reference_allocation_http
                check_reference_allocation(query,manifest,psql,env)
                check_reference_allocation_http(url,query,session)
                from check_trading_history_boundary import run_checks as check_trading_history, run_http_checks as check_trading_history_http
                check_trading_history(query,manifest)
                check_trading_history_http(query,manifest,url,temporary,session)
                from check_pre_retention_storage import ordinary_swaps as check_swaps_storage
                check_swaps_storage(query,psql,env,ROOT,
                    [reference,market,swap_market,security,trading_fixture(manifest['catalogId'],swap_defaults=True)],manifest)
                from seed_preview_swaps import seed_swaps
                seeded=seed_swaps(url,run_id,temporary,session)
                assert len(seeded)==16 and seeded==seed_swaps(url,run_id,temporary,session)
                print('PASS sixteen synthetic swap examples seed idempotently across all four weekdays',flush=True)
                from check_seeded_preview import run_swap_book_checks
                run_swap_book_checks(session,seeded)
            if args.scheduled_swap_check:
                from check_whole_term_swap_schema import run_storage_checks as check_whole_term_swap_storage, check_rebooking_guard
                from check_pre_retention_storage import swaps as check_whole_term_swap_admission
                check_whole_term_swap_admission(query,psql,env,ROOT,
                    [reference,market,swap_market,security,trading_fixture(manifest['catalogId'],swap_defaults=True)],manifest)
                check_whole_term_swap_storage(query,manifest,require_retention=True)
                check_rebooking_guard(query,psql,env,ROOT)
        else:
            if args.example=='standard':
                worker_env=prepare_risk_worker()
                risk_worker_process=subprocess.Popen([str(dotnet),str(frozen_dll),'--risk-worker','serve'],cwd=ROOT,env=worker_env,start_new_session=True)
            child.wait()
    except KeyboardInterrupt:
        print('Stopping the owned preview.',flush=True)
        if args.check:
            raise
    except Exception:
        print_failure_identifiers(temporary/'app.log')
        raise
    finally:
        if risk_worker_process is not None:
            try:
                stop_owned_group(risk_worker_process)
            except Exception:
                browser_cleanup_ok=False
                print('Worker cleanup unconfirmed; retaining its private directory.',flush=True)
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
            raise RuntimeError('Owned worker/browser cleanup could not be confirmed; private files retained')


if __name__=='__main__':
    main()
