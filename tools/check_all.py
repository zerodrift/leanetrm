#!/usr/bin/env python3
"""Run the synthetic verification suites with explicitly supplied local tools."""
import argparse
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
CHECK_PROJECTS = (
    ('tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj', ()),
    ('tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj', ('--futures-options',)),
    ('tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj', ('--spread-options',)),
    ('tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj', ('--exercise-rights',)),
    ('tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj', ('--vol-surface',)),
    ('tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj', ('--calendar-policy',)),
    ('tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj', ('--payment-date-policy',)),
    ('tests/OpenEtrm.Rates.Checks/OpenEtrm.Rates.Checks.csproj', ()),
    ('tests/OpenEtrm.Shared.Checks/OpenEtrm.Shared.Checks.csproj', ()),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--context-resolution',)),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--read-references',)),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--schedules',)),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--power-hedge',)),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--async-reads',)),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--assistant-prompts',)),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--published-clock',)),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--calendar-authoring',)),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--gas-ticket',)),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--historical-risk',)),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--monte-carlo-risk',)),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--position-adjustment',)),
    ('tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj', ('--hybrid-monte-carlo-risk',)),
)


GROUPS = ('workspace', 'reference', 'standard', 'exchange', 'assistant', 'risk',
          'trading', 'lifecycle', 'physical', 'gas', 'gas-native', 'gas-browser',
          'portfolio', 'settlement', 'ppa', 'recovery')
SHARED_GROUPS = ('repository', 'frontend', 'managed', 'database')
ALL_GROUPS = (*SHARED_GROUPS, *GROUPS)


def run_checks(pg_bin, dotnet, browser, *, group='all', run=subprocess.run):
    if group not in ('all', *ALL_GROUPS):
        raise ValueError(f'Unknown verification group: {group}')
    if not __debug__ or os.environ.get('PYTHONOPTIMIZE') not in (None, '', '0'):
        raise ValueError('Python assertions must remain enabled for verification.')
    environment = {**os.environ, 'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1',
                   'CHROME_BIN': str(browser), 'OPENETRM_CHECK_BROWSER': str(browser), 'DOTNET': str(dotnet)}

    def check(label, command, *, suite='repository'):
        if group != 'all' and suite != group:
            return
        print(f'CHECK {label}', flush=True)
        run(command, cwd=ROOT, env=environment, check=True)

    check('verification runner', [sys.executable, '-m', 'unittest', 'discover',
                                  '-s', 'tests', '-p', 'test_*.py'])
    check('independent settlement specification', [sys.executable, '-m', 'unittest', 'discover', '-s', 'specs/settlement', '-v'])
    check('standalone dependencies', [sys.executable, 'tools/check_standalone.py'])
    if group in ('all','reference','trading'):
        regional_group='reference' if group=='all' else group
        check('locked regional seed restore',[str(dotnet),'restore','tools/OpenEtrm.Seed/OpenEtrm.Seed.csproj','--locked-mode'],suite=regional_group)
        check('regional seed build',[str(dotnet),'build','tools/OpenEtrm.Seed/OpenEtrm.Seed.csproj','--no-restore'],suite=regional_group)
    check('owned process cleanup', [sys.executable, 'tools/check_owned_process.py'])
    check('owned browser readiness', ['node', '--test', 'tests/browser/owned-browser.test.mjs', 'tests/browser/gas-ticket.test.mjs'])
    # Each integration checkout builds its own assets; shared behavior checks
    # execute only in their named groups.
    if group in ('database', *GROUPS):
        check('locked frontend install', ['npm', 'ci', '--prefix', 'web', '--ignore-scripts'], suite=group)
        check('frontend build', ['npm', 'run', 'build', '--prefix', 'web'], suite=group)
        for project in ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj',
                        'src/OpenEtrm.Worker/OpenEtrm.Worker.csproj',
                        'tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj'):
            check(f'locked integration restore: {Path(project).stem}',
                  [str(dotnet), 'restore', project, '--locked-mode'], suite=group)
            check(f'integration build: {Path(project).stem}',
                  [str(dotnet), 'build', project, '--no-restore'], suite=group)
    check('locked frontend install', ['npm', 'ci', '--prefix', 'web', '--ignore-scripts'], suite='frontend')
    check('frontend build and checks', ['npm', 'test', '--prefix', 'web'], suite='frontend')
    for project in dict.fromkeys(project for project, _ in CHECK_PROJECTS):
        check(f'locked restore: {Path(project).stem}', [str(dotnet), 'restore', project, '--locked-mode'], suite='managed')
    for project, arguments in CHECK_PROJECTS:
        command = [str(dotnet), 'run', '--no-restore', '--project', project]
        check(' '.join((Path(project).stem, *arguments)), [*command, '--', *arguments] if arguments else command, suite='managed')
    for argument in ('--power-flat','--power-atc','--power-product-averages','--power-interval-quantities','--ppa-sides'):
        check('native power '+argument,[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj','--',argument], suite='managed')
    check('power worker admission',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--close-risk'], suite='managed')
    check('independent Monte Carlo generator and quantile intervals',[sys.executable,'tools/check_monte_carlo_numerics.py','--dotnet',str(dotnet)], suite='managed')
    worker_project='src/OpenEtrm.Worker/OpenEtrm.Worker.csproj'
    check('locked worker restore',[str(dotnet),'restore',worker_project,'--locked-mode'], suite='managed')
    check('independent worker build',[str(dotnet),'build',worker_project,'--no-restore'], suite='managed')
    check('isolated database login boundaries',[sys.executable,'tools/check_database_login_boundaries.py',
          '--pg-bin',str(pg_bin),'--dotnet',str(dotnet),'--no-build'], suite='database')
    check('managed automation contracts',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--automation'], suite='managed')
    check('managed live context contracts',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--live-calculation'], suite='managed')
    check('managed private reference intake',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--reference-intake'], suite='managed')
    check('managed preview data',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--preview-data'], suite='managed')
    check('managed side observation compatibility',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--side-observation-compatibility'], suite='managed')
    check('owned automation database',[sys.executable,'tools/check_automation.py','--pg-bin',str(pg_bin),'--dotnet',str(dotnet)], suite='database')
    check('owned operating settings database',[sys.executable,'tools/check_operating_settings.py','--pg-bin',str(pg_bin)], suite='database')
    check('owned operating facility versions',[sys.executable,'tools/check_operating_facilities.py','--pg-bin',str(pg_bin)],suite='physical')
    for helper,label in (
        ('check_operating_schedule_storage','operating schedule grammar and predecessor contracts'),
        ('check_operating_trades','native operating-right admission'),
        ('check_rec_inventory','evidenced certificate ownership and conservation'),
        ('check_rec_forward_storage','native certificate forward admission'),
        ('check_heat_rate_call_storage','native heat-rate call option admission'),
        ('check_spread_mark_storage','native spread-option mark publication'),
        ('check_bilateral_desk','bilateral agreement and credit admission'),
        ('check_facility_desk','commercial facility pricing and operating limits'),
        ('check_gas_storage_contract_storage','native gas storage contract booking and plans')):
        check(label,[sys.executable,f'tools/{helper}.py','--pg-bin',str(pg_bin)],suite='physical')
    check('native volatility-fit admission expiry',[sys.executable,'tools/check_vol_surface_admission_expiry.py','--pg-bin',str(pg_bin)], suite='database')
    check('owned reference metadata database',[sys.executable,'tools/check_reference_editing.py','--pg-bin',str(pg_bin)], suite='database')
    check('populated payment-day program upgrade',[sys.executable,'tools/check_payment_day_upgrade.py','--pg-bin',str(pg_bin)], suite='database')
    check('calendar migration helper contracts',[sys.executable,'tools/check_calendar_upgrade.py','--pg-bin',str(pg_bin)], suite='database')
    check('native Side calendar bindings',[sys.executable,'tools/check_side_calendar_policies.py','--pg-bin',str(pg_bin)], suite='database')
    check('recorded cash upgrade over existing option bookings',[sys.executable,'tools/check_recorded_cash_upgrade.py','--pg-bin',str(pg_bin)], suite='database')
    # The projection-source qualifier also runs the complete native journal.
    for part in ('sources','adoption','projection_sources'):
        check('owned futures margin '+part,[sys.executable,f'tools/check_futures_margin_{part}.py','--pg-bin',str(pg_bin)],suite='lifecycle')
    check('explicit trading business dates and command source admission',[sys.executable,'tools/check_trading_business_date.py','--pg-bin',str(pg_bin)],suite='lifecycle')
    for helper,label in (
        ('check_lifecycle_actions','shared lifecycle actions and native option history'),
        ('check_lifecycle_swaps','shared swap settlement, netting and exact task sources'),
        ('check_futures_physical_delivery','physical futures final margin and delivery obligation'),
        ('check_futures_delivery_settings','bounded retained physical delivery settings'),
        ('check_selected_physical_authority','selected physical action authority'),
        ('check_physical_delivery_authority','physical delivery original authority'),
        ('check_physical_margin_qualification','new physical futures require qualified delivery terms'),
        ('check_lifecycle_option_scheduling','actual option expiry scheduling and linked futures'),
        ('check_lifecycle_option_expiries','historical option expiry and immutable instruction review'),
        ('check_european_option','European option style, value beside an American series, refused early exercise and expiry'),
        ('check_lifecycle_scheduling','lifecycle scheduler source admission and replay'),
        ('check_lifecycle_scheduled_bookings','scheduled native bookings retain exact lifecycle policies'),
        ('check_gas_source_ancestors','applied gas corrections, pending financial writes and captured source ancestry'),
        ('check_gas_physical_known','captured physical gas delivery dependencies'),
        ('check_gas_cashout_projection','cashout profiles, native valuation and retained economic workers'),
        ('check_gas_accepted_projection','accepted physical gas movements, exact remainders and native economic workers'),
        ('check_gas_quantity_reconciliation','native quantity corrections, exact source allocations and retained principal refunds'),
        ('check_gas_quantity_reconciliation_batches','atomic linked gas corrections, exact review metadata and complete native principal prefixes'),
        ('check_gas_owned_quantity_choices','ordinary gas corrections, exact member choices and encoded source budgets'),
        ('check_gas_mixed_quantity_reconciliation','atomic ordinary and physical quantity corrections, exact source closure and sealed first outcomes'),
        ('check_gas_lifecycle_agents','opt-in gas lifecycle role, delegated review and current actor authority'),
        ('check_physical_option_value','selected native physical option value, source admission and retained workers'),
        ('check_cash_accumulation','explicit cash funding choices, native source custody and economic worker replay'),
        ('check_gas_principal_carrying','saved delivery principal carrying and exact residual curve demand'),
        ('check_risk_fit_authority','volatility fitting actor and delegated worker authority'),
        ('check_retained_swap_schedule_reads','retained swap hydration and original worker read admission')):
        check(label,[sys.executable,f'tools/{helper}.py','--pg-bin',str(pg_bin)],suite='lifecycle')
    for helper,label in (
        ('check_lifecycle_risk_jobs','economic lifecycle custody and native worker scope'),
        ('check_lifecycle_risk_scope','consumer-specific gas authority in risk jobs'),
        ('check_lifecycle_final_scope','native lifecycle final-result scope'),
        ('check_lifecycle_projection_horizon','economic horizon and retained projection profiles'),
        ('check_lifecycle_projection_horizon_upgrade','historical populated horizon migration'),
        ('check_lifecycle_swap_worker','economic swap replay in separate workers'),
        ('check_lifecycle_future_worker','economic futures replay in separate workers'),
        ('check_gas_projection_scope','original gas projection scope authority, fixed source tuples and actual economic workers'),
        ('check_physical_futures_value','full native physical futures value and exact linked-source economic workers')):
        check(label,[sys.executable,f'tools/{helper}.py','--pg-bin',str(pg_bin)],suite='portfolio')
    for helper,label in (
        ('check_gas_park_loan','booked Park and Loan, native ownership and independent whole-book price scenarios'),
        ('check_gas_park_loan_upgrade','populated Park and Loan schema upgrade preserves existing native owners and cash'),
        ('check_gas_park_loan_worker','retained Park and Loan price workers preserve exact scope, source and original authority'),
        ('check_gas_park_loan_balance_fee','outstanding-balance fee accrual, independent cash and whole-book price scenarios'),
        ('check_gas_balance_fee_upgrade','populated fixed-fee upgrade retains exact owners and rejects predecessor drift'),
        ('check_gas_balance_fee_worker','balance-time fee PRICE workers and explicit economic-continuation refusals'),
        ('check_gas_park_custody','retained-title custody, explicit reservations, independent cash and native price scenarios'),
        ('check_gas_park_custody_upgrade','populated custody upgrade retains predecessor owners and rejects boundary drift'),
        ('check_gas_park_custody_worker','retained custody PRICE workers preserve exact source, scope and original authority'),
        ('check_gas_transport_financial','booked Transport rights, independent fee cash and explicit lifecycle execution'),
        ('check_gas_transport_financial_upgrade','populated financial Transport upgrade preserves operating sources and rejects boundary drift'),
        ('check_gas_transport_financial_presence','scoped Transport absence preserves independent clocks and original read authority'),
        ('check_gas_transport_financial_presence_upgrade','populated Transport presence upgrade preserves native facts and helper contracts'),
        ('check_gas_transport_financial_worker','retained Transport PRICE workers preserve exact state, sources and original authority'),
        ('check_point_trader_marks','ordinary direct point publication, exact source authority and explicit financial adoption'),
        ('check_point_trader_marks_upgrade','populated point publisher upgrade preserves existing facts and unrelated helper privileges'),
        ('check_gas_fee_existing_origins','fixed loan fee upgrade preserves exact existing financial origins and bank history'),
        ('check_gas_loan_quantity_reconciliation','loan withdrawal, exact source authority and cash-only maturity'),
        ('check_gas_owned_fact_acknowledgements','original gas commands acknowledge accepted financial snapshots without new effects'),
        ('check_gas_owned_title_acknowledgements','original title and loan commands retain exact accepted facts and valid source contexts'),
        ('check_gas_read_authority','original gas read authority survives the complete final helper boundary'),
        ('check_gas_operator_existing_origins','operator upgrades preserve exact financial origins and original bank history'),
        ('check_gas_operator_upgrade','operator source upgrades retain exact predecessor execution metadata and privileges'),
        ('check_gas_operator_native_upgrade','native operator continuation retains exact dependencies, function identities and privileges')):
        check(label,[sys.executable,f'tools/{helper}.py','--pg-bin',str(pg_bin)],suite='gas-native')
    for helper,label in (
        ('check_gas_operator_physical_value','corrected operator stock and debt through complete economic workers'),
        ('check_gas_operator_ordinary_value','ordinary and zero operator corrections retain exact owners through economic workers'),
        ('check_gas_known_cashout','source-known future cashouts retain exact commodity and independent cash owners')):
        check(label,[sys.executable,f'tools/{helper}.py','--pg-bin',str(pg_bin),'--dotnet',str(dotnet),'--no-build'],suite='portfolio')
    for helper,suite in (('check_gas_park_loan_browser','gas-browser'),('check_gas_balance_fee_browser','gas-browser'),('check_gas_park_custody_browser','gas-browser'),('check_gas_transport_financial_browser','gas-browser'),('check_point_trader_marks_browser','gas-browser'),('check_gas_mixed_quantity_browser','lifecycle'),('check_lifecycle_browser','lifecycle'),('check_gas_fee_browser','gas-browser'),
                         ('check_gas_loan_quantity_browser','gas-browser'),('check_gas_operator_quantity_browser','gas-browser'),('check_lifecycle_economic_browser','portfolio'),
                         ('check_agent_operating_settings','assistant')):
        check(helper,[sys.executable,f'tools/{helper}.py','--pg-bin',str(pg_bin),'--dotnet',str(dotnet),
            '--browser',str(browser),'--no-build'],suite=suite)
    check('foreign custody operations without owned-gas or bank-payment authority',
        [sys.executable,'tools/check_gas_park_custody_browser.py','--pg-bin',str(pg_bin),'--dotnet',str(dotnet),
         '--browser',str(browser),'--no-build','--provider-only'],suite='gas-browser')
    for flag in ('--gas-park-custody','--gas-park-custody-ticket','--gas-park-custody-evidence',
        '--gas-park-custody-actions','--gas-park-custody-effects','--gas-park-custody-handler',
        '--gas-park-custody-setup','--gas-park-custody-overview','--ticket-status-routing',
        '--cash-payment-status','--gas-instant-ingress','--lifecycle-context-ingress'):
        check('managed custody '+flag,[str(dotnet),'run','--no-restore','--project',
            'tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--',flag],suite='managed')
    for flag in ('--gas-transport-capacity-adapters','--gas-transport-capacity-source',
        '--gas-transport-capacity-metadata','--operational-business-context'):
        check('managed Transport capacity '+flag,[str(dotnet),'run','--no-restore','--project',
            'tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--',flag],suite='managed')
    for flag in ('--gas-transport-route-adapters','--gas-transport-route-source','--gas-transport-route-metadata'):
        check('managed chosen Transport route '+flag,[str(dotnet),'run','--no-restore','--project',
            'tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--',flag],suite='managed')
    for flag in ('--delivery-workbench','--delivery-nomination-batch'):
        check('managed delivery workbench '+flag,[str(dotnet),'run','--no-restore','--project',
            'tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--',flag],suite='managed')
    for name in ('commands','source','ticket','positions','adapters','labels','setup'):
        flag='--gas-transport-financial-'+name
        check('managed financial Transport '+flag,[str(dotnet),'run','--no-restore','--project',
            'tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--',flag],suite='managed')
    for name in ('identity','inputs','commands','receipts'):
        flag='--point-trader-mark-'+name
        check('managed ordinary point publication '+flag,[str(dotnet),'run','--no-restore','--project',
            'tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--',flag],suite='managed')
    check('fresh storage native and managed source allocation',
        [sys.executable,'tools/check_storage_facility.py','--pg-bin',str(pg_bin)],suite='physical')
    check('physical facility authoring and measured activity in the application',
        [sys.executable,'tools/check_storage_facility_browser.py','--pg-bin',str(pg_bin),'--dotnet',str(dotnet),
         '--browser',str(browser),'--no-build'],suite='physical')
    for helper in ('check_gas_transport_capacity','check_gas_transport_capacity_upgrade'):
        check(helper,[sys.executable,f'tools/{helper}.py','--pg-bin',str(pg_bin)],suite='physical')
    check('commercial Transport capacity operations in the application',
        [sys.executable,'tools/check_gas_transport_capacity_browser.py','--pg-bin',str(pg_bin),'--dotnet',str(dotnet),
         '--browser',str(browser),'--no-build'],suite='physical')
    for helper in ('check_gas_transport_route','check_gas_transport_route_upgrade'):
        check(helper,[sys.executable,f'tools/{helper}.py','--pg-bin',str(pg_bin)],suite='physical')
    check('retained chosen Transport routes in the application',
        [sys.executable,'tools/check_gas_transport_route_browser.py','--pg-bin',str(pg_bin),'--dotnet',str(dotnet),
         '--browser',str(browser),'--no-build'],suite='physical')
    check('native delivery fulfillment relationships',
        [sys.executable,'tools/check_delivery_workbench.py','--pg-bin',str(pg_bin)],suite='physical')
    check('shared gas and crude delivery workbench in the application',
        [sys.executable,'tools/check_delivery_workbench_http.py','--pg-bin',str(pg_bin),'--dotnet',str(dotnet),
         '--browser',str(browser),'--no-build'],suite='physical')
    for kind in ('physical','cash'):
        check('native '+kind+' option models in the application',[sys.executable,'tools/check_lifecycle_valuation_browser.py',
            '--pg-bin',str(pg_bin),'--dotnet',str(dotnet),'--browser',str(browser),'--kind',kind,'--no-build'],suite='lifecycle')
    for flag in ('--lifecycle-actions','--economic-risk-contract','--lifecycle-schedule-contract','--gas-owned-setup',
        '--shared-native-markets','--derivative-methods','--gas-accepted-order','--lifecycle-residual-currencies','--gas-operator-contract'):
        check('managed lifecycle '+flag,[str(dotnet),'run','--no-restore','--project',
            'tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--',flag], suite='managed')
    check('exact runtime temporal sources and claimed worker replay',
          [sys.executable,'tools/check_context_resolution.py','--pg-bin',str(pg_bin),'--dotnet',str(dotnet)], suite='database')
    check('historical populated recorded-close migration',
          [sys.executable,'tools/check_context_resolution.py','--upgrade-existing-close','--pg-bin',str(pg_bin),'--dotnet',str(dotnet)], suite='database')
    check('owned recorded-close observation batches',[sys.executable,'tools/check_observation_batch_reads.py','--pg-bin',str(pg_bin)], suite='database')
    check('owned publication definitions and final releases',[sys.executable,'tools/check_price_publications.py','--pg-bin',str(pg_bin)], suite='database')
    check('owned contract pricing terms',[sys.executable,'tools/check_contract_price_terms.py','--pg-bin',str(pg_bin)], suite='database')
    check('owned delivery catalog integrity',[sys.executable,'tools/check_power_integrity.py','--pg-bin',str(pg_bin)], suite='database')
    check('stock database schema', [sys.executable, 'tools/check_reference_schema.py',
                                   '--pg-bin', str(pg_bin), '--market', '--security'], suite='database')
    preview = [sys.executable, 'tools/run_preview.py', '--pg-bin', str(pg_bin),
               '--dotnet', str(dotnet), '--no-build', '--check']
    def preview_check(label, command, *, suite='workspace'):
        check(label, command, suite=suite)

    preview_check('structured power facilities, trading, REC inventory and browser',[*preview,'--structured-power-check','--browser-check','--browser',str(browser)],suite='physical')
    preview_check('integrated live calculation and browser',[*preview,'--live-calculation-check','--browser-check','--browser',str(browser)])
    preview_check('private inactive reference intake',[*preview,'--reference-import-check'], suite='reference')
    preview_check('standard preview and browser', [*preview, '--browser-check', '--browser', str(browser)], suite='standard')
    preview_check('runtime context removal, exact replay and worker reconstruction', [*preview,'--ephemeral-context-check','--browser-check','--browser',str(browser)],suite='standard')
    preview_check('context read demand and grouped header', [*preview,'--context-read-check','--browser-check','--browser',str(browser)])
    preview_check('asynchronous workbench reads', [*preview,'--async-workbench-check','--browser-check','--browser',str(browser)])
    preview_check('isolated party maintenance and power browser', [*preview, '--party-check', '--browser-check', '--browser', str(browser)])
    preview_check('isolated page-assistant administration and browser', [*preview, '--assistant-check', '--browser-check', '--browser', str(browser)], suite='assistant')
    preview_check('physical assistant context and native approvals', [*preview, '--physical-assistant-check', '--browser-check', '--browser', str(browser)], suite='assistant')
    preview_check('assistant page operations, approvals and guidance', [*preview, '--assistant-workspace-check', '--browser-check', '--browser', str(browser)], suite='assistant')
    preview_check('isolated units and deal precision browser', [*preview, '--unit-check', '--browser-check', '--browser', str(browser)])
    preview_check('independent swap sides, spreads and reviewed par pricing', [*preview, '--draft-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('explicit Side calendars, native booking and retained replay',[*preview,'--side-calendar-check'],suite='trading')
    preview_check('shared booking workflow and mixed gas portfolio', [*preview, '--unified-trading-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('physical crude booking, portfolio risk, delivery cash and record corrections', [*preview, '--crude-lifecycle-check', '--browser-check', '--browser', str(browser)], suite='physical')
    preview_check('maintained crude setup, original pricing bindings and recovery', [*preview, '--crude-setup-check', '--browser-check', '--browser', str(browser)], suite='physical')
    preview_check('gas basis history and final facility inputs', [*preview, '--gas-basis-demo-check', '--browser-check', '--browser', str(browser)], suite='gas')
    preview_check('manual gas scheduling, imbalance and request recovery', [*preview, '--gas-operations-check', '--browser-check', '--browser', str(browser)], suite='gas')
    preview_check('final daily gas billing, corrections and request recovery', [*preview, '--gas-billing-check', '--browser-check', '--browser', str(browser)], suite='gas')
    preview_check('physical gas booking, facility allocation, setup and daily quantities', [*preview, '--facility-quantity-check', '--gas-daily-quantity-check', '--gas-composition-check', '--gas-setup-check', '--browser-check', '--browser', str(browser)], suite='gas')
    preview_check('gas payment entries, named index pricing and exercise rights', [*preview, '--gas-exercise-check', '--gas-pricing-check', '--browser-check', '--browser', str(browser)], suite='gas')
    preview_check('selected gas delivery quantities, temporal evidence and retained risk', [*preview, '--gas-delivery-quantity-check', '--browser-check', '--browser', str(browser)], suite='gas')
    preview_check('future gas amendments, immutable revisions and ticket recovery', [*preview, '--gas-amendments-check', '--browser-check', '--browser', str(browser)], suite='gas')
    preview_check('daily indexed gas payments, shared Side inspection and retained risk', [*preview, '--gas-indexed-check', '--browser-check', '--browser', str(browser)], suite='gas')
    preview_check('in-term gas admission, recorded payments and the owner-only in-term load', [*preview, '--gas-in-term-check'], suite='gas')
    preview_check('physical LNG cargo, Incoterms, upgrade, recovery and retained worker valuation', [*preview, '--lng-cargo-check', '--browser-check', '--browser', str(browser)], suite='physical')
    preview_check('retained graph admission and reverse-boundary independent Side reuse', [*preview, '--retained-schedule-check'], suite='trading')
    preview_check('mixed PPA and heat-rate trades, exact worker custody and browser', [*preview, '--demo-instruments-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('scheduled swap settlement, delegated operations and lifecycle browser', [*preview, '--scheduled-swap-check', '--browser-check', '--browser', str(browser)], suite='settlement')
    preview_check('typed factor units, cross-commodity lifecycle and risk', [*preview, '--factor-swap-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('published index fixing price and risk', [*preview, '--index-fixing-check'], suite='trading')
    preview_check('Blotter close-out drafts, settled periods, refusals and browser', [*preview, '--close-out-drafts-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('side observation authoring, storage and valued schedules', [*preview, '--side-observation-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('mapped collected power interval settlement', [*preview, '--power-actual-check','--browser-check','--browser',str(browser)])
    preview_check('audited interval series', [*preview, '--interval-series-check', '--browser-check', '--browser', str(browser)])
    preview_check('interval curves, block shaping and resolution', [*preview, '--interval-curves-check', '--browser-check', '--browser', str(browser)])
    preview_check('physical power product curves, saved profiles, captures and corrections',[*preview,'--power-booked-roll-check'],suite='ppa')
    preview_check('power recorded sources and historical close admission',[*preview,'--power-context-check'],suite='ppa')
    preview_check('PPA reference and quantity sources', [*preview, '--ppa-check'], suite='ppa')
    preview_check('quarter-hour fixed and indexed settlement preview', [*preview, '--settlement-quarter-check'], suite='ppa')
    preview_check('Durable PPA bookings, HTTP oracles, revision admission and browser', [*preview, '--ppa-bookings-check', '--browser-check', '--browser', str(browser)], suite='ppa')
    preview_check('read-only existing-close risk and fenced workers', [*preview, '--close-risk-check'], suite='ppa')
    preview_check('renewable existing-close completion and recovery', [*preview, '--close-risk-lease-check'], suite='recovery')
    preview_check('standard futures execution policies', [*preview, '--execution-check'])
    preview_check('shared contract-date library', [*preview, '--contract-dates-check', '--browser-check', '--browser', str(browser)], suite='reference')
    preview_check('stored mixed portfolio valuation and swap risk', [*preview, '--swap-risk-holdings-check'], suite='portfolio')
    preview_check('generated pilot close, actual worker and immutable report', [*preview, '--pilot-close-check', '--browser-check', '--browser', str(browser)], suite='portfolio')
    preview_check('connected published portfolio and retained workers', [*preview, '--connected-portfolio-check'], suite='portfolio')
    preview_check('custom curve graphs and editor', [*preview, '--custom-curves-check', '--browser-check', '--browser', str(browser)], suite='reference')
    preview_check('contract observation curves', [*preview, '--contract-observation-curve-check', '--browser-check', '--browser', str(browser)], suite='reference')
    preview_check('publication ingestion and contract pricing authoring', [*preview, '--price-authoring-check', '--browser-check', '--browser', str(browser)], suite='reference')
    preview_check('market close calendar', [*preview, '--market-dates-check', '--browser-check', '--browser', str(browser)], suite='reference')
    preview_check('market calendar read scaling and authority', [*preview, '--market-dates-reads-check'], suite='reference')
    preview_check('live outputs and frozen dependencies', [*preview, '--curve-eligibility-check'], suite='reference')
    preview_check('close sessions and bounded observation reads', [*preview, '--regional-closes-check'], suite='reference')
    preview_check('positions a close cannot value', [*preview, '--valuation-status-check', '--browser-check', '--browser', str(browser)], suite='portfolio')
    preview_check('Blotter commodity, swap direction, mark minus price, summaries and curve shapes', [*preview, '--desk-fields-check'], suite='portfolio')
    preview_check('isolated exchange futures, option reference storage, option booking and browser', [*preview, '--exchange-check', '--browser-check', '--browser', str(browser)], suite='exchange')
    preview_check('isolated hosted authentication and browser', [*preview, '--hosted-check', '--browser-check', '--browser', str(browser)])
    preview_check('isolated jobs and infrastructure browser', [*preview, '--jobs-check', '--historical-pool-check', '--browser-check', '--browser', str(browser)])
    preview_check('additive reference demo and exact-version discounting',[*preview,'--reference-demo-check'], suite='reference')
    preview_check('regional closes and independent currency pricing seed',[*preview,'--regional-seed-check'],suite='reference')
    check('interactive regional and date seed startup',[sys.executable,'tools/run_preview.py','--pg-bin',str(pg_bin),
        '--dotnet',str(dotnet),'--no-build','--regional-seed-startup-check'],suite='trading')
    preview_check('isolated reference metadata editor',[*preview,'--reference-check','--browser-check','--browser',str(browser)], suite='reference')
    preview_check('isolated nested risk and durable workers', [*preview, '--risk-jobs-check', '--browser-check', '--browser', str(browser)], suite='risk')
    preview_check('Monte Carlo full-PV and hybrid VaR/ES, explicit consent, custody and graphical reports', [*preview, '--with-exchange', '--monte-carlo-risk-check', '--browser-check', '--browser', str(browser)], suite='risk')
    preview_check('historical full-PV VaR/ES, custody and graphical reports', [*preview, '--historical-risk-check', '--browser-check', '--browser', str(browser)], suite='risk')
    preview_check('parametric VaR/ES, retained history custody and graphical reports', [*preview, '--parametric-risk-check', '--browser-check', '--browser', str(browser)], suite='risk')
    preview_check('aligned held-factor history and graphical diagnostics', [*preview, '--factor-history-check', '--browser-check', '--browser', str(browser)], suite='risk')
    preview_check('mixed-period preview', [*preview, '--example', 'mixed-gas'])
    message = ('All synthetic verification suites passed.' if group == 'all'
               else f'Synthetic verification group {group} passed.')
    print(message, flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin', type=Path, required=True, help='Installed stock PostgreSQL binaries')
    parser.add_argument('--dotnet', type=Path, required=True, help='Installed SDK matching global.json')
    parser.add_argument('--browser', type=Path, required=True, help='Installed Chromium-compatible executable')
    parser.add_argument('--group', choices=('all', *ALL_GROUPS), default='all',
                        help='CI partition; integration groups build assets without repeating shared checks')
    args = parser.parse_args()
    pg_bin, dotnet, browser = (p.resolve() for p in (args.pg_bin, args.dotnet, args.browser))
    binaries = [dotnet, browser]
    if args.group in ('all', 'database', *GROUPS):
        binaries.extend(pg_bin / name for name in ('initdb', 'pg_ctl', 'psql'))
    for binary in binaries:
        if not binary.is_file():
            parser.error(f'Missing installed binary: {binary}')
    expected_node = 'v' + (ROOT / '.node-version').read_text().strip()
    actual_node = subprocess.check_output(['node', '--version'], cwd=ROOT, text=True).strip()
    if actual_node != expected_node:
        parser.error(f'Use Node {expected_node} from .node-version; found {actual_node}')
    try:
        run_checks(pg_bin, dotnet, browser, group=args.group)
    except ValueError as error:
        parser.error(str(error))
    except subprocess.CalledProcessError as error:
        print(f'Verification stopped after exit status {error.returncode}.', file=sys.stderr)
        return error.returncode
    return 0


if __name__ == '__main__':
    sys.exit(main())
