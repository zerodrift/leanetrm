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
    ('tests/OpenEtrm.Rates.Checks/OpenEtrm.Rates.Checks.csproj', ()),
    ('tests/OpenEtrm.Shared.Checks/OpenEtrm.Shared.Checks.csproj', ()),
    ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj', ('--schedules',)),
)


GROUPS = ('workspace', 'trading', 'ppa', 'recovery')


def run_checks(pg_bin, dotnet, browser, *, group='all', run=subprocess.run):
    if group not in ('all', *GROUPS):
        raise ValueError(f'Unknown verification group: {group}')
    if not __debug__ or os.environ.get('PYTHONOPTIMIZE') not in (None, '', '0'):
        raise ValueError('Python assertions must remain enabled for verification.')
    environment = {**os.environ, 'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1'}

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
    check('owned browser readiness', ['node', '--test', 'tests/browser/owned-browser.test.mjs'])
    check('locked frontend install', ['npm', 'ci', '--prefix', 'web', '--ignore-scripts'])
    check('frontend build and checks', ['npm', 'test', '--prefix', 'web'])
    for project, _ in CHECK_PROJECTS:
        check(f'locked restore: {Path(project).stem}', [str(dotnet), 'restore', project, '--locked-mode'])
    for project, arguments in CHECK_PROJECTS:
        command = [str(dotnet), 'run', '--no-restore', '--project', project]
        check(Path(project).stem, [*command, '--', *arguments] if arguments else command)
    worker_project='src/OpenEtrm.Worker/OpenEtrm.Worker.csproj'
    check('locked worker restore',[str(dotnet),'restore',worker_project,'--locked-mode'])
    check('independent worker build',[str(dotnet),'build',worker_project,'--no-restore'])
    check('managed automation contracts',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--automation'])
    check('managed live context contracts',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--live-calculation'])
    check('managed private reference intake',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--reference-intake'])
    check('managed preview data',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--preview-data'])
    check('managed side observation compatibility',[str(dotnet),'run','--no-restore','--project','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--','--side-observation-compatibility'])
    check('owned automation database',[sys.executable,'tools/check_automation.py','--pg-bin',str(pg_bin),'--dotnet',str(dotnet)])
    check('owned reference metadata database',[sys.executable,'tools/check_reference_editing.py','--pg-bin',str(pg_bin)])
    check('owned delivery catalog integrity',[sys.executable,'tools/check_power_integrity.py','--pg-bin',str(pg_bin)])
    check('stock database schema', [sys.executable, 'tools/check_reference_schema.py',
                                   '--pg-bin', str(pg_bin), '--market', '--security'])
    preview = [sys.executable, 'tools/run_preview.py', '--pg-bin', str(pg_bin),
               '--dotnet', str(dotnet), '--no-build', '--check']
    def preview_check(label, command, *, suite='workspace'):
        check(label, command, suite=suite)

    preview_check('integrated live calculation and browser',[*preview,'--live-calculation-check','--browser-check','--browser',str(browser)])
    preview_check('private inactive reference intake',[*preview,'--reference-import-check'])
    preview_check('standard preview and browser', [*preview, '--browser-check', '--browser', str(browser)])
    preview_check('isolated party maintenance and power browser', [*preview, '--party-check', '--browser-check', '--browser', str(browser)])
    preview_check('isolated page-assistant administration and browser', [*preview, '--assistant-check', '--browser-check', '--browser', str(browser)])
    preview_check('isolated units and deal precision browser', [*preview, '--unit-check', '--browser-check', '--browser', str(browser)])
    preview_check('independent swap sides, spreads and reviewed par pricing', [*preview, '--draft-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('scheduled swap settlement, delegated operations and lifecycle browser', [*preview, '--scheduled-swap-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('published index fixing price and risk', [*preview, '--index-fixing-check'], suite='trading')
    preview_check('side observation authoring, storage and valued schedules', [*preview, '--side-observation-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('mapped collected power interval settlement', [*preview, '--power-actual-check'])
    preview_check('audited interval series', [*preview, '--interval-series-check', '--browser-check', '--browser', str(browser)])
    preview_check('interval curves, block shaping and resolution', [*preview, '--interval-curves-check', '--browser-check', '--browser', str(browser)])
    preview_check('PPA reference and quantity sources', [*preview, '--ppa-check'], suite='ppa')
    preview_check('quarter-hour fixed and indexed settlement preview', [*preview, '--settlement-quarter-check'], suite='ppa')
    preview_check('Durable PPA bookings, HTTP oracles, revision admission and browser', [*preview, '--ppa-bookings-check', '--browser-check', '--browser', str(browser)], suite='ppa')
    preview_check('read-only existing-close risk and fenced workers', [*preview, '--close-risk-check'], suite='ppa')
    preview_check('renewable existing-close completion and recovery', [*preview, '--close-risk-lease-check'], suite='recovery')
    preview_check('standard futures execution policies', [*preview, '--execution-check'])
    preview_check('shared contract-date library', [*preview, '--contract-dates-check', '--browser-check', '--browser', str(browser)])
    preview_check('stored mixed portfolio valuation and swap risk', [*preview, '--swap-risk-holdings-check'], suite='trading')
    preview_check('generated pilot close, actual worker and immutable report', [*preview, '--pilot-close-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('connected published portfolio and retained workers', [*preview, '--connected-portfolio-check'], suite='trading')
    preview_check('custom curve graphs and editor', [*preview, '--custom-curves-check', '--browser-check', '--browser', str(browser)])
    preview_check('market close calendar', [*preview, '--market-dates-check', '--browser-check', '--browser', str(browser)])
    preview_check('positions a close cannot value', [*preview, '--valuation-status-check', '--browser-check', '--browser', str(browser)], suite='trading')
    preview_check('Blotter commodity, swap direction, mark minus price, summaries and curve shapes', [*preview, '--desk-fields-check'], suite='trading')
    preview_check('isolated exchange futures browser', [*preview, '--exchange-check', '--browser-check', '--browser', str(browser)])
    preview_check('isolated hosted authentication and browser', [*preview, '--hosted-check', '--browser-check', '--browser', str(browser)])
    preview_check('isolated jobs and infrastructure browser', [*preview, '--jobs-check', '--browser-check', '--browser', str(browser)])
    preview_check('isolated reference metadata editor',[*preview,'--reference-check','--browser-check','--browser',str(browser)])
    preview_check('isolated nested risk and durable workers', [*preview, '--risk-jobs-check', '--browser-check', '--browser', str(browser)])
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
