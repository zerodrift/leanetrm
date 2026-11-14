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
    ('tests/OpenEtrm.Rates.Checks/OpenEtrm.Rates.Checks.csproj', ()),
    ('tests/OpenEtrm.Shared.Checks/OpenEtrm.Shared.Checks.csproj', ()),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--context-resolution',)),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--schedules',)),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--historical-risk',)),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--monte-carlo-risk',)),
    ('tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj', ('--hybrid-monte-carlo-risk',)),
)


GROUPS = ('workspace', 'standard', 'assistant', 'trading', 'portfolio', 'settlement', 'ppa', 'recovery')


def run_checks(pg_bin, dotnet, browser, *, group='all', run=subprocess.run):
    if group not in ('all', *GROUPS):
        raise ValueError(f'Unknown verification group: {group}')
    if not __debug__ or os.environ.get('PYTHONOPTIMIZE') not in (None, '', '0'):
        raise ValueError('Python assertions must remain enabled for verification.')
    environment = {**os.environ, 'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1',
                   'CHROME_BIN': str(browser), 'OPENETRM_CHECK_BROWSER': str(browser), 'DOTNET': str(dotnet)}

    def check(label, command, *, suite=None):
        if group != 'all' and suite not in (None, group):
            return
        print(f'CHECK {label}', flush=True)
        run(command, cwd=ROOT, env=environment, check=True)

    check('verification runner', [sys.executable, '-m', 'unittest', 'discover',
                                  '-s', 'tests', '-p', 'test_*.py'])
    check('independent settlement specification', [sys.executable, '-m', 'unittest', 'discover', '-s', 'specs/settlement', '-v'])
    check('standalone dependencies', [sys.executable, 'tools/check_standalone.py'])
    check('owned process cleanup', [sys.executable, 'tools/check_owned_process.py'])
    check('owned browser readiness', ['node', '--test', 'tests/browser/owned-browser.test.mjs', 'tests/browser/gas-ticket.test.mjs'])
    check('locked frontend install', ['npm', 'ci', '--prefix', 'web', '--ignore-scripts'])
    check('frontend build and checks', ['npm', 'test', '--prefix', 'web'])
    for project in dict.fromkeys(project for project, _ in CHECK_PROJECTS):
        check(f'locked restore: {Path(project).stem}', [str(dotnet), 'restore', project, '--locked-mode'])
    for project, arguments in CHECK_PROJECTS:
        command = [str(dotnet), 'run', '--no-restore', '--project', project]
        check(' '.join((Path(project).stem, *arguments)), [*command, '--', *arguments] if arguments else command)
    for argument in ('--power-flat','--power-atc','--power-product-averages','--power-interval-quantities','--ppa-sides'):
        check('native power '+argument,[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj','--',argument])
    check('power worker admission',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--close-risk'])
    check('independent Monte Carlo generator and quantile intervals',[sys.executable,'tools/check_monte_carlo_numerics.py','--dotnet',str(dotnet)])
    worker_project='src/OpenEtrm.Worker/OpenEtrm.Worker.csproj'
    check('locked worker restore',[str(dotnet),'restore',worker_project,'--locked-mode'])
    check('independent worker build',[str(dotnet),'build',worker_project,'--no-restore'])
    check('isolated database login boundaries',[sys.executable,'tools/check_database_login_boundaries.py',
          '--pg-bin',str(pg_bin),'--dotnet',str(dotnet),'--no-build'])
    check('managed automation contracts',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--automation'])
    check('managed live context contracts',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--live-calculation'])
    check('managed private reference intake',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--reference-intake'])
    check('managed preview data',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--preview-data'])
    check('managed side observation compatibility',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--side-observation-compatibility'])
    check('owned automation database',[sys.executable,'tools/check_automation.py','--pg-bin',str(pg_bin),'--dotnet',str(dotnet)])
    check('owned operating settings database',[sys.executable,'tools/check_operating_settings.py','--pg-bin',str(pg_bin)])
    check('native volatility-fit admission expiry',[sys.executable,'tools/check_vol_surface_admission_expiry.py','--pg-bin',str(pg_bin)])
    check('owned reference metadata database',[sys.executable,'tools/check_reference_editing.py','--pg-bin',str(pg_bin)])
    check('recorded cash upgrade over existing option bookings',[sys.executable,'tools/check_recorded_cash_upgrade.py','--pg-bin',str(pg_bin)])
    for part in ('sources','adoption','journal'):
        check('owned futures margin '+part,[sys.executable,f'tools/check_futures_margin_{part}.py','--pg-bin',str(pg_bin)],suite='trading')
    check('explicit trading business dates and command source admission',[sys.executable,'tools/check_trading_business_date.py','--pg-bin',str(pg_bin)],suite='trading')
    for helper,label in (
        ('check_lifecycle_actions','shared lifecycle actions and native option history'),
        ('check_lifecycle_swaps','shared swap settlement, netting and exact task sources'),
        ('check_futures_physical_delivery','physical futures final margin and delivery obligation'),
        ('check_selected_physical_authority','selected physical action authority'),
        ('check_physical_delivery_authority','physical delivery original authority'),
        ('check_physical_margin_qualification','new physical futures require qualified delivery terms'),
        ('check_lifecycle_option_scheduling','actual option expiry scheduling and linked futures'),
        ('check_lifecycle_option_expiries','historical option expiry and immutable instruction review'),
        ('check_lifecycle_scheduling','lifecycle scheduler source admission and replay'),
        ('check_lifecycle_scheduled_bookings','scheduled native bookings retain exact lifecycle policies'),
        ('check_gas_source_ancestors','applied gas corrections, pending financial writes and captured source ancestry'),
        ('check_gas_physical_known','captured physical gas delivery dependencies'),
        ('check_gas_cashout_projection','cashout profiles, native valuation and retained economic workers'),
        ('check_gas_accepted_projection','accepted physical gas movements, exact remainders and native economic workers'),
        ('check_gas_quantity_reconciliation','native quantity corrections, exact source allocations and retained principal refunds'),
        ('check_gas_quantity_reconciliation_batches','atomic linked gas corrections, exact review metadata and complete native principal prefixes'),
        ('check_cash_accumulation','explicit cash funding choices, native source custody and economic worker replay'),
        ('check_gas_principal_carrying','saved delivery principal carrying and exact residual curve demand'),
        ('check_risk_fit_authority','volatility fitting actor and delegated worker authority'),
        ('check_retained_swap_schedule_reads','retained swap hydration and original worker read admission')):
        check(label,[sys.executable,f'tools/{helper}.py','--pg-bin',str(pg_bin)],suite='trading')
    for helper,label in (
        ('check_lifecycle_risk_jobs','economic lifecycle custody and native worker scope'),
        ('check_lifecycle_risk_scope','consumer-specific gas authority in risk jobs'),
        ('check_lifecycle_final_scope','native lifecycle final-result scope'),
        ('check_lifecycle_projection_horizon','economic horizon and retained projection profiles'),
        ('check_lifecycle_swap_worker','economic swap replay in separate workers'),
        ('check_lifecycle_future_worker','economic futures replay in separate workers'),
        ('check_physical_futures_value','full native physical futures value and exact linked-source economic workers')):
        check(label,[sys.executable,f'tools/{helper}.py','--pg-bin',str(pg_bin)],suite='portfolio')
    for helper,suite in (('check_lifecycle_browser','trading'),('check_lifecycle_economic_browser','portfolio')):
        check(helper,[sys.executable,f'tools/{helper}.py','--pg-bin',str(pg_bin),'--dotnet',str(dotnet),
            '--browser',str(browser),'--no-build'],suite=suite)
    for flag in ('--lifecycle-actions','--economic-risk-contract','--lifecycle-schedule-contract','--gas-owned-setup',
        '--shared-native-markets','--derivative-methods','--gas-accepted-order','--lifecycle-residual-currencies'):
        check('managed lifecycle '+flag,[str(dotnet),'run','--no-restore','--project',
            'tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--',flag])
    check('exact temporal sources, retained close upgrade and claimed worker replay',
          [sys.executable,'tools/check_context_resolution.py','--upgrade-existing-close','--pg-bin',str(pg_bin),'--dotnet',str(dotnet)])
    check('owned recorded-close observation batches',[sys.executable,'tools/check_observation_batch_reads.py','--pg-bin',str(pg_bin)])
    check('owned publication definitions and final releases',[sys.executable,'tools/check_price_publications.py','--pg-bin',str(pg_bin)])
    check('owned contract pricing terms',[sys.executable,'tools/check_contract_price_terms.py','--pg-bin',str(pg_bin)])
    check('owned delivery catalog integrity',[sys.executable,'tools/check_power_integrity.py','--pg-bin',str(pg_bin)])
    check('stock database schema', [sys.executable, 'tools/check_reference_schema.py',
                                   '--pg-bin', str(pg_bin), '--market', '--security'])
    preview = [sys.executable, 'tools/run_preview.py', '--pg-bin', str(pg_bin),
               '--dotnet', str(dotnet), '--no-build', '--check']
    def preview_check(label, command, *, suite='workspace'):
        check(label, command, suite=suite)

    preview_check('integrated live calculation and browser',[*preview,'--live-calculation-check','--browser-check','--browser',str(browser)])
    preview_check('private inactive reference intake',[*preview,'--reference-import-check'])
    preview_check('standard preview and browser', [*preview, '--browser-check', '--browser', str(browser)], suite='standard')
    preview_check('context read demand and grouped header', [*preview,'--context-read-check','--browser-check','--browser',str(browser)])
    preview_check('isolated party maintenance and power browser', [*preview, '--party-check', '--browser-check', '--browser', str(browser)])
    preview_check('isolated page-assistant administration and browser', [*preview, '--assistant-check', '--browser-check', '--browser', str(browser)], suite='assistant')
    preview_check('physical assistant context and native approvals', [*preview, '--physical-assistant-check', '--browser-check', '--browser', str(browser)], suite='assistant')
    preview_check('assistant page operations, approvals and guidance', [*preview, '--assistant-workspace-check', '--browser-check', '--browser', str(browser)], suite='assistant')
    preview_check('isolated units and deal precision browser', [*preview, '--unit-check', '--browser-check', '--browser', str(browser)])
    preview_check('independent swap sides, spreads and reviewed par pricing', [*preview, '--draft-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('shared booking workflow and mixed gas portfolio', [*preview, '--unified-trading-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('physical crude booking, portfolio risk, delivery cash and record corrections', [*preview, '--crude-lifecycle-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('maintained crude setup, original pricing bindings and recovery', [*preview, '--crude-setup-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('gas basis history and final facility inputs', [*preview, '--gas-basis-demo-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('manual gas scheduling, imbalance and request recovery', [*preview, '--gas-operations-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('final daily gas billing, corrections and request recovery', [*preview, '--gas-billing-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('physical gas booking, facility allocation, setup and daily quantities', [*preview, '--facility-quantity-check', '--gas-daily-quantity-check', '--gas-composition-check', '--gas-setup-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('future gas amendments, immutable revisions and ticket recovery', [*preview, '--gas-amendments-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('daily indexed gas payments, shared Side inspection and retained risk', [*preview, '--gas-indexed-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('in-term gas admission, recorded payments and the owner-only in-term load', [*preview, '--gas-in-term-check'], suite='trading')
    preview_check('physical LNG cargo, Incoterms, upgrade, recovery and retained worker valuation', [*preview, '--lng-cargo-check', '--browser-check', '--browser', str(browser)], suite='trading')
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
    preview_check('shared contract-date library', [*preview, '--contract-dates-check', '--browser-check', '--browser', str(browser)])
    preview_check('stored mixed portfolio valuation and swap risk', [*preview, '--swap-risk-holdings-check'], suite='portfolio')
    preview_check('generated pilot close, actual worker and immutable report', [*preview, '--pilot-close-check', '--browser-check', '--browser', str(browser)], suite='portfolio')
    preview_check('connected published portfolio and retained workers', [*preview, '--connected-portfolio-check'], suite='portfolio')
    preview_check('custom curve graphs and editor', [*preview, '--custom-curves-check', '--browser-check', '--browser', str(browser)])
    preview_check('contract observation curves', [*preview, '--contract-observation-curve-check', '--browser-check', '--browser', str(browser)])
    preview_check('publication ingestion and contract pricing authoring', [*preview, '--price-authoring-check', '--browser-check', '--browser', str(browser)])
    preview_check('market close calendar', [*preview, '--market-dates-check', '--browser-check', '--browser', str(browser)])
    preview_check('live outputs and frozen dependencies', [*preview, '--curve-eligibility-check'])
    preview_check('close sessions and bounded observation reads', [*preview, '--regional-closes-check'])
    preview_check('positions a close cannot value', [*preview, '--valuation-status-check', '--browser-check', '--browser', str(browser)], suite='portfolio')
    preview_check('Blotter commodity, swap direction, mark minus price, summaries and curve shapes', [*preview, '--desk-fields-check'], suite='portfolio')
    preview_check('isolated exchange futures, option reference storage, option booking and browser', [*preview, '--exchange-check', '--browser-check', '--browser', str(browser)], suite='standard')
    preview_check('isolated hosted authentication and browser', [*preview, '--hosted-check', '--browser-check', '--browser', str(browser)])
    preview_check('isolated jobs and infrastructure browser', [*preview, '--jobs-check', '--browser-check', '--browser', str(browser)])
    preview_check('additive reference demo and exact-version discounting',[*preview,'--reference-demo-check'])
    preview_check('isolated reference metadata editor',[*preview,'--reference-check','--browser-check','--browser',str(browser)])
    preview_check('isolated nested risk and durable workers', [*preview, '--risk-jobs-check', '--browser-check', '--browser', str(browser)], suite='assistant')
    preview_check('Monte Carlo full-PV and hybrid VaR/ES, explicit consent, custody and graphical reports', [*preview, '--with-exchange', '--monte-carlo-risk-check', '--browser-check', '--browser', str(browser)], suite='assistant')
    preview_check('historical full-PV VaR/ES, custody and graphical reports', [*preview, '--historical-risk-check', '--browser-check', '--browser', str(browser)], suite='assistant')
    preview_check('parametric VaR/ES, retained history custody and graphical reports', [*preview, '--parametric-risk-check', '--browser-check', '--browser', str(browser)], suite='assistant')
    preview_check('aligned held-factor history and graphical diagnostics', [*preview, '--factor-history-check', '--browser-check', '--browser', str(browser)], suite='assistant')
    preview_check('mixed-period preview', [*preview, '--example', 'mixed-gas'])
    message = ('All synthetic verification suites passed.' if group == 'all'
               else f'Synthetic verification group {group} passed.')
    print(message, flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin', type=Path, required=True, help='Installed stock PostgreSQL binaries')
    parser.add_argument('--dotnet', type=Path, required=True, help='Installed SDK matching global.json')
    parser.add_argument('--browser', type=Path, required=True, help='Installed Chromium-compatible executable')
    parser.add_argument('--group', choices=('all', *GROUPS), default='all',
                        help='CI partition; each group repeats all shared preparation and checks')
    args = parser.parse_args()
    pg_bin, dotnet, browser = (p.resolve() for p in (args.pg_bin, args.dotnet, args.browser))
    for binary in (dotnet, browser, *(pg_bin / name for name in ('initdb', 'pg_ctl', 'psql'))):
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
