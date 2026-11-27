"""Behavior checks for the local/CI verification entry point."""
from contextlib import redirect_stderr, redirect_stdout
import io
import os
from pathlib import Path
import subprocess
import re
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from check_all import ALL_GROUPS, GROUPS, run_checks
import check_all

LIFECYCLE_NATIVE={f'tools/{name}.py' for name in (
    'check_lifecycle_actions','check_lifecycle_swaps','check_futures_physical_delivery','check_futures_delivery_settings',
    'check_selected_physical_authority','check_physical_delivery_authority','check_physical_margin_qualification',
    'check_lifecycle_option_scheduling','check_lifecycle_option_expiries',
    'check_lifecycle_scheduling','check_lifecycle_scheduled_bookings',
    'check_gas_source_ancestors','check_gas_physical_known','check_gas_cashout_projection','check_gas_accepted_projection','check_gas_quantity_reconciliation',
    'check_gas_quantity_reconciliation_batches','check_gas_mixed_quantity_reconciliation','check_gas_lifecycle_agents','check_gas_mixed_quantity_browser','check_gas_owned_quantity_choices','check_physical_option_value','check_cash_accumulation','check_gas_principal_carrying',
    'check_risk_fit_authority','check_lifecycle_browser','check_retained_swap_schedule_reads')}
GAS_AMENDMENTS={f'tools/{name}.py' for name in (
    'check_gas_park_loan','check_gas_park_loan_upgrade','check_gas_park_loan_worker','check_gas_park_loan_browser',
    'check_gas_park_loan_balance_fee','check_gas_balance_fee_upgrade','check_gas_balance_fee_worker','check_gas_balance_fee_browser',
    'check_gas_fee_existing_origins','check_gas_fee_browser','check_gas_loan_quantity_browser',
    'check_gas_loan_quantity_reconciliation','check_gas_owned_fact_acknowledgements','check_gas_owned_title_acknowledgements','check_gas_read_authority','check_gas_operator_quantity_browser',
    'check_gas_operator_existing_origins','check_gas_operator_upgrade','check_gas_operator_native_upgrade')}
LIFECYCLE_PORTFOLIO={f'tools/{name}.py' for name in (
    'check_lifecycle_risk_jobs','check_lifecycle_risk_scope','check_lifecycle_final_scope',
    'check_lifecycle_projection_horizon','check_lifecycle_projection_horizon_upgrade','check_lifecycle_swap_worker','check_lifecycle_future_worker',
    'check_lifecycle_economic_browser','check_physical_futures_value','check_gas_projection_scope',
    'check_gas_operator_physical_value','check_gas_operator_ordinary_value','check_gas_known_cashout')}
OPTION_VALUE_APPS={'tools/check_lifecycle_valuation_browser.py'}
AGENT_SETTINGS_BROWSER={'tools/check_agent_operating_settings.py'}
STORAGE_FACILITY={f'tools/{name}.py' for name in ('check_storage_facility','check_storage_facility_browser')}
TRANSPORT_CAPACITY={f'tools/{name}.py' for name in ('check_gas_transport_capacity','check_gas_transport_capacity_upgrade','check_gas_transport_capacity_browser')}
TRANSPORT_ROUTE={f'tools/{name}.py' for name in ('check_gas_transport_route','check_gas_transport_route_upgrade','check_gas_transport_route_browser')}
OPERATING_FACILITIES={'tools/check_operating_facilities.py','tools/check_operating_schedule_storage.py','tools/check_operating_trades.py','tools/check_rec_inventory.py','tools/check_rec_forward_storage.py','tools/check_heat_rate_call_storage.py','tools/check_spread_mark_storage.py','tools/check_gas_storage_contract_storage.py'}
POINT_TRADER_MARKS={f'tools/check_point_trader_marks{suffix}.py' for suffix in ('', '_upgrade', '_browser')}


def gas_helpers(group, helpers):
    if group == 'all':
        return helpers
    if group == 'gas-native':
        return {helper for helper in helpers if not helper.endswith('_browser.py')}
    if group == 'gas-browser':
        return {helper for helper in helpers if helper.endswith('_browser.py')}
    return set()


class CheckAllTests(unittest.TestCase):
    def test_charge_preview_rejects_other_fixture_suites_before_startup(self):
        for flag in ('--gas-in-term-check', '--gas-delivery-quantity-check', '--gas-indexed-check'):
            with self.subTest(flag=flag):
                result = subprocess.run(
                    [sys.executable, 'tools/run_preview.py', '--pg-bin', '/unused-pg',
                     '--dotnet', '/unused-dotnet', '--check', '--gas-charges-check', flag],
                    cwd=ROOT, capture_output=True, text=True, check=False)
                self.assertEqual(result.returncode, 2, result.stderr)
                self.assertIn(f'--gas-charges-check and {flag} use different fixtures', result.stderr)

    def test_gas_jobs_separate_native_browser_and_preview_workloads(self):
        self.assertTrue({'gas', 'gas-native', 'gas-browser'}.issubset(GROUPS))
        workloads = {}
        for group in ('gas', 'gas-native', 'gas-browser'):
            commands = []
            run_checks(Path('/pg'), Path('/sdk'), Path('/browser'), group=group,
                       run=lambda command, **options: commands.append(command))
            workloads[group] = [command for command in commands
                                if command[0] == sys.executable and command[1].startswith('tools/')]
        previews = workloads['gas']
        self.assertEqual(len(previews), 12)
        self.assertTrue(all(command[1] == 'tools/run_preview.py' for command in previews))
        preview_flags = {
            '--gas-basis-demo-check', '--gas-operations-check', '--gas-billing-check',
            '--gas-daily-quantity-check', '--gas-amendments-check', '--gas-indexed-check',
            '--gas-in-term-check', '--gas-delivery-quantity-check', '--gas-pricing-check', '--gas-exercise-check', '--gas-charges-check', '--gas-imbalance-check', '--gas-swing-check'}
        self.assertEqual({part for command in previews for part in command if part in preview_flags},
                         preview_flags)
        exercise=[command for command in previews if '--gas-exercise-check' in command]
        self.assertEqual(len(exercise),1)
        self.assertTrue({'--gas-pricing-check','--browser-check'}.issubset(exercise[0]))
        swing, = [command for command in previews if '--gas-swing-check' in command]
        self.assertIn('--browser-check', swing)
        charge = [command for command in previews if '--gas-charges-check' in command]
        self.assertEqual(len(charge), 1)
        self.assertIn('--browser-check', charge[0])
        self.assertNotIn('--gas-exercise-check', charge[0])
        quantity=next(command for command in previews if '--gas-delivery-quantity-check' in command)
        self.assertIn('--browser-check',quantity)
        self.assertNotIn('--gas-composition-check',quantity)
        native = workloads['gas-native']
        self.assertEqual(len(native), 24)
        self.assertTrue(all(command[1] != 'tools/run_preview.py' and '--browser' not in command
                            for command in native))
        browsers = workloads['gas-browser']
        self.assertEqual(len(browsers), 9)
        self.assertTrue(all(command[1].endswith('_browser.py') and '--browser' in command
                            for command in browsers))
        self.assertEqual(sum('--provider-only' in command for command in browsers), 1)

    def test_point_trader_mark_native_upgrade_and_browser_are_required_once_with_selected_tools(self):
        helpers = POINT_TRADER_MARKS
        api = 'tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj'
        for group in ('all', *ALL_GROUPS):
            commands = []
            def run(command, **options):
                commands.append(command)
                found = helpers.intersection(command)
                if not found:
                    return
                helper = found.pop()
                self.assertTrue((ROOT / helper).is_file())
                self.assertEqual(command[:2], [sys.executable, helper])
                self.assertEqual(command[command.index('--pg-bin') + 1], '/stock pg')
                self.assertEqual(options['env']['DOTNET'], '/selected sdk')
                self.assertIn(['/selected sdk', 'restore', api, '--locked-mode'], commands)
                if group in ('gas-native', 'gas-browser'):
                    self.assertIn(['/selected sdk', 'build', api, '--no-restore'], commands)
                if helper.endswith('_browser.py'):
                    self.assertEqual(command[command.index('--dotnet') + 1], '/selected sdk')
                    self.assertEqual(command[command.index('--browser') + 1], '/selected browser')
                    self.assertIn('--no-build', command)
            with patch.dict(os.environ, {'DOTNET': '/unrelated sdk'}), redirect_stdout(io.StringIO()):
                run_checks(Path('/stock pg'), Path('/selected sdk'), Path('/selected browser'), group=group, run=run)
            selected = [part for command in commands for part in command if part in helpers]
            self.assertEqual(set(selected), gas_helpers(group, helpers))
            self.assertEqual(len(selected), len(gas_helpers(group, helpers)))

    def test_point_trader_mark_managed_contracts_are_required_once_in_managed(self):
        flags = {'--point-trader-mark-' + name for name in ('identity', 'inputs', 'commands', 'receipts')}
        api = 'tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj'
        for group in ('all', *ALL_GROUPS):
            selected = []
            def run(command, **options):
                found = flags.intersection(command)
                if found:
                    flag = found.pop()
                    self.assertEqual(command, ['/selected sdk', 'run', '--no-restore', '--project', api, '--', flag])
                    self.assertEqual(options['env']['DOTNET'], '/selected sdk')
                    selected.append(flag)
            with redirect_stdout(io.StringIO()):
                run_checks(Path('/pg'), Path('/selected sdk'), Path('/browser'), group=group, run=run)
            self.assertEqual(set(selected), flags if group in ('all', 'managed') else set())
            self.assertEqual(len(selected), len(flags) if group in ('all', 'managed') else 0)

    def test_delivery_workbench_requires_native_and_actual_application_qualification(self):
        helpers={'tools/check_delivery_workbench.py','tools/check_delivery_workbench_http.py'}
        flags={'--delivery-workbench','--delivery-nomination-batch'}
        for group in ALL_GROUPS:
            commands=[]
            def run(command,**options):
                commands.append(command)
                if 'tools/check_delivery_workbench_http.py' in command:
                    self.assertEqual(command[command.index('--dotnet')+1],'/selected sdk')
                    self.assertEqual(command[command.index('--browser')+1],'/selected browser')
                    self.assertIn('--no-build',command)
            with redirect_stdout(io.StringIO()):
                run_checks(Path('/stock pg'),Path('/selected sdk'),Path('/selected browser'),group=group,run=run)
            selected={part for command in commands for part in command if part in helpers}
            self.assertEqual(selected,helpers if group=='physical' else set())
            selected_flags={part for command in commands for part in command if part in flags}
            self.assertEqual(selected_flags,flags if group=='managed' else set())

    def test_financial_transport_requires_native_upgrade_worker_and_actual_browser(self):
        helpers = {f'tools/check_gas_transport_financial{suffix}.py'
                   for suffix in ('', '_upgrade', '_worker', '_browser', '_presence', '_presence_upgrade')}
        api = 'tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj'
        for group in ALL_GROUPS:
            commands = []
            def run(command, **options):
                commands.append(command)
                found = helpers.intersection(command)
                if not found:
                    return
                helper = found.pop()
                self.assertEqual(command[command.index('--pg-bin') + 1], '/stock pg')
                self.assertEqual(options['env']['DOTNET'], '/selected sdk')
                self.assertIn(['/selected sdk', 'restore', api, '--locked-mode'], commands)
                self.assertIn(['/selected sdk', 'build', api, '--no-restore'], commands)
                if helper.endswith('_browser.py'):
                    self.assertEqual(command[command.index('--dotnet') + 1], '/selected sdk')
                    self.assertEqual(command[command.index('--browser') + 1], '/selected browser')
                    self.assertIn('--no-build', command)
            with redirect_stdout(io.StringIO()):
                run_checks(Path('/stock pg'), Path('/selected sdk'), Path('/selected browser'),
                           group=group, run=run)
            selected = [part for command in commands for part in command if part in helpers]
            self.assertEqual(set(selected), gas_helpers(group, helpers))
            self.assertEqual(len(selected), len(gas_helpers(group, helpers)))

    def test_financial_transport_managed_contracts_run_once_in_managed(self):
        flags = {'--gas-transport-financial-' + name for name in
                 ('commands', 'source', 'ticket', 'positions', 'adapters', 'labels', 'setup')}
        for group in ALL_GROUPS:
            selected = []
            with redirect_stdout(io.StringIO()):
                run_checks(Path('/pg'), Path('/sdk'), Path('/browser'), group=group,
                           run=lambda command, **options: selected.extend(flags.intersection(command)))
            self.assertEqual(set(selected), flags if group == 'managed' else set())
            self.assertEqual(len(selected), len(flags) if group == 'managed' else 0)

    def test_chosen_route_native_upgrade_and_browser_are_required_with_the_selected_tools(self):
        for group in ALL_GROUPS:
            commands=[]
            def run(command,**options):
                commands.append(command)
                for helper in TRANSPORT_ROUTE.intersection(command):
                    self.assertEqual(command[command.index('--pg-bin')+1],'/stock pg')
                    self.assertEqual(options['env']['DOTNET'],'/selected sdk')
                    self.assertIn(['/selected sdk','build','tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj','--no-restore'],commands)
                    if helper.endswith('_browser.py'):
                        self.assertEqual(command[command.index('--dotnet')+1],'/selected sdk')
                        self.assertEqual(command[command.index('--browser')+1],'/selected browser')
                        self.assertIn('--no-build',command)
            with redirect_stdout(io.StringIO()):
                run_checks(Path('/stock pg'),Path('/selected sdk'),Path('/selected browser'),group=group,run=run)
            selected=[part for command in commands for part in command if part in TRANSPORT_ROUTE]
            self.assertEqual(set(selected),TRANSPORT_ROUTE if group=='physical' else set())
            self.assertEqual(len(selected),len(TRANSPORT_ROUTE) if group=='physical' else 0)

    def test_chosen_route_managed_contracts_are_required_once(self):
        flags={'--gas-transport-route-adapters','--gas-transport-route-source','--gas-transport-route-metadata'}
        for group in ALL_GROUPS:
            selected=[]
            with redirect_stdout(io.StringIO()):
                run_checks(Path('/pg'),Path('/sdk'),Path('/browser'),group=group,
                    run=lambda command,**options:selected.extend(flags.intersection(command)))
            self.assertEqual(set(selected),flags if group=='managed' else set())
            self.assertEqual(len(selected),len(flags) if group=='managed' else 0)

    def test_transport_capacity_native_upgrade_and_browser_require_physical_partition_and_built_api(self):
        api='tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj'
        for group in ALL_GROUPS:
            commands=[]
            def run(command,**options):
                commands.append(command)
                found=TRANSPORT_CAPACITY.intersection(command)
                if not found:return
                helper=found.pop()
                self.assertEqual(command[command.index('--pg-bin')+1],'/stock pg')
                self.assertEqual(options['env']['DOTNET'],'/selected sdk')
                self.assertIn(['/selected sdk','restore',api,'--locked-mode'],commands)
                self.assertIn(['/selected sdk','build',api,'--no-restore'],commands)
                if helper.endswith('_browser.py'):
                    self.assertEqual(command[command.index('--dotnet')+1],'/selected sdk')
                    self.assertEqual(command[command.index('--browser')+1],'/selected browser')
                    self.assertIn('--no-build',command)
            run_checks(Path('/stock pg'),Path('/selected sdk'),Path('/selected browser'),group=group,run=run)
            selected=[part for command in commands for part in command if part in TRANSPORT_CAPACITY]
            self.assertEqual(set(selected),TRANSPORT_CAPACITY if group=='physical' else set())
            self.assertEqual(len(selected),len(TRANSPORT_CAPACITY) if group=='physical' else 0)

    def test_transport_capacity_managed_source_metadata_and_business_context_run_once(self):
        flags={'--gas-transport-capacity-adapters','--gas-transport-capacity-source','--gas-transport-capacity-metadata','--operational-business-context'}
        for group in ALL_GROUPS:
            selected=[]
            run_checks(Path('/pg'),Path('/sdk'),Path('/browser'),group=group,
                run=lambda command,**options:selected.extend(flags.intersection(command)))
            self.assertEqual(set(selected),flags if group=='managed' else set())
            self.assertEqual(len(selected),len(flags) if group=='managed' else 0)

    def test_custody_native_upgrade_worker_and_both_browser_roles_are_required(self):
        helpers={f'tools/{name}.py' for name in (
            'check_gas_park_custody','check_gas_park_custody_upgrade',
            'check_gas_park_custody_worker','check_gas_park_custody_browser')}
        for group in ALL_GROUPS:
            selected=[]
            def run(command,**options):
                found=helpers.intersection(command)
                if not found:return
                helper=found.pop();selected.append(command)
                self.assertEqual(command[command.index('--pg-bin')+1],'/stock pg')
                self.assertEqual(options['env']['DOTNET'],'/selected sdk')
                if helper.endswith('_browser.py'):
                    self.assertEqual(command[command.index('--dotnet')+1],'/selected sdk')
                    self.assertEqual(command[command.index('--browser')+1],'/selected browser')
                    self.assertIn('--no-build',command)
            run_checks(Path('/stock pg'),Path('/selected sdk'),Path('/selected browser'),group=group,run=run)
            expected = gas_helpers(group, helpers)
            self.assertEqual(len(selected), len(expected) + int(group == 'gas-browser'))
            self.assertEqual({part for command in selected for part in command} & helpers, expected)
            self.assertEqual(sum('--provider-only' in command for command in selected), int(group == 'gas-browser'))

    def test_custody_managed_boundaries_run_once_in_managed_partition(self):
        flags={'--gas-park-custody','--gas-park-custody-ticket','--gas-park-custody-evidence',
            '--gas-park-custody-actions','--gas-park-custody-effects','--gas-park-custody-handler',
            '--gas-park-custody-setup','--gas-park-custody-overview','--ticket-status-routing',
            '--cash-payment-status','--gas-instant-ingress','--lifecycle-context-ingress'}
        for group in ALL_GROUPS:
            selected=[]
            run_checks(Path('/pg'),Path('/dotnet'),Path('/browser'),group=group,
                run=lambda command,**options:selected.extend(flags.intersection(command)))
            self.assertEqual(set(selected),flags if group=='managed' else set())
            self.assertEqual(len(selected),len(flags) if group=='managed' else 0)

    def test_migration_chain_has_unique_contiguous_versions(self):
        migrations = sorted((ROOT / 'db/migrations').glob('*.sql'))
        versions = [int(migration.name.split('_', 1)[0]) for migration in migrations]
        self.assertEqual(versions, list(range(1, len(migrations) + 1)),
                         'The ordered migration chain must have one file per version.')

    def test_structured_power_storage_and_http_run_in_physical_partition(self):
        for group in ALL_GROUPS:
            commands=[]
            run_checks(Path('/pg'),Path('/dotnet'),Path('/browser'),group=group,run=lambda command,**options:commands.append(command))
            selected=[command for command in commands if OPERATING_FACILITIES.intersection(command) or '--structured-power-check' in command]
            self.assertEqual(len(selected),9 if group=='physical' else 0)
            browser=[command for command in selected if '--structured-power-check' in command]
            if browser:
                self.assertIn('--browser-check',browser[0])
                self.assertEqual(browser[0][browser[0].index('--browser')+1],'/browser')

    def test_inventory_access_runs_once_in_physical_partition(self):
        for group in ALL_GROUPS:
            commands=[]
            run_checks(Path('/pg'),Path('/dotnet'),Path('/browser'),group=group,run=lambda command,**options:commands.append(command))
            self.assertEqual(sum('--inventory-access-check' in command for command in commands),int(group=='physical'))

    def test_assistant_prompt_and_demand_checks_are_required(self):
        for group in ALL_GROUPS:
            commands = []
            run_checks(Path('/pg'), Path('/dotnet'), Path('/browser'), group=group,
                       run=lambda command, **options: commands.append(command))
            self.assertEqual(sum('--assistant-prompts' in command for command in commands), int(group == 'managed'))
            workspace = [c for c in commands if '--assistant-workspace-check' in c]
            self.assertEqual(len(workspace), int(group == 'assistant'))
            if workspace:
                self.assertIn('--browser-check', workspace[0])
        preview = (ROOT / 'tools/run_preview.py').read_text()
        self.assertIn("migration.name in ('0339_assistant_installation_prompts.sql','0340_assistant_requested_context.sql')", preview)
        self.assertIn('check_assistant_prompt_upgrade(query,migration)', preview)

    def test_lightweight_groups_do_not_require_postgresql_installation(self):
        with tempfile.TemporaryDirectory(prefix='openetrm-ci-tools-') as temporary:
            root = Path(temporary)
            dotnet, browser = root / 'dotnet', root / 'browser'
            dotnet.touch()
            browser.touch()
            for group in ('repository', 'frontend', 'managed'):
                with self.subTest(group=group), \
                     patch.object(sys, 'argv', ['check_all.py', '--group', group, '--pg-bin', str(root / 'missing-pg'),
                                               '--dotnet', str(dotnet), '--browser', str(browser)]), \
                     patch.object(check_all.subprocess, 'check_output', return_value='v' + (ROOT / '.node-version').read_text().strip()), \
                     patch.object(check_all, 'run_checks') as selected:
                    self.assertEqual(check_all.main(), 0)
                    self.assertEqual(selected.call_args.kwargs['group'], group)
            for group in ('all', 'database', *GROUPS):
                with self.subTest(group=group), redirect_stderr(io.StringIO()), \
                     patch.object(sys, 'argv', ['check_all.py', '--group', group, '--pg-bin', str(root / 'missing-pg'),
                                               '--dotnet', str(dotnet), '--browser', str(browser)]), \
                     patch.object(check_all, 'run_checks') as selected:
                    with self.assertRaises(SystemExit) as failure:
                        check_all.main()
                    self.assertEqual(failure.exception.code, 2)
                    selected.assert_not_called()

    def test_balance_fee_native_upgrade_worker_and_browser_run_once_in_gas_jobs(self):
        expected = {f'tools/{name}.py' for name in (
            'check_gas_park_loan_balance_fee', 'check_gas_balance_fee_upgrade',
            'check_gas_balance_fee_worker', 'check_gas_balance_fee_browser')}
        for group in GROUPS:
            selected = []
            def run(command, **options):
                helpers = expected.intersection(command)
                if not helpers:
                    return
                helper = helpers.pop()
                selected.append(helper)
                self.assertEqual(command[command.index('--pg-bin') + 1], '/stock pg')
                self.assertEqual(options['env']['DOTNET'], '/selected sdk')
                if helper.endswith('_browser.py'):
                    self.assertEqual(command[command.index('--dotnet') + 1], '/selected sdk')
                    self.assertEqual(command[command.index('--browser') + 1], '/selected browser')
                    self.assertIn('--no-build', command)
            run_checks(Path('/stock pg'), Path('/selected sdk'), Path('/selected browser'),
                       group=group, run=run)
            self.assertEqual(set(selected), gas_helpers(group, expected))
            self.assertEqual(len(selected), len(gas_helpers(group, expected)))

    def test_park_loan_qualifiers_are_required_once_in_gas_jobs(self):
        expected = {'tools/check_gas_park_loan.py', 'tools/check_gas_park_loan_upgrade.py',
                    'tools/check_gas_park_loan_browser.py', 'tools/check_gas_park_loan_worker.py'}
        for group in GROUPS:
            commands = []
            run_checks(Path('/pg'), Path('/dotnet'), Path('/browser'), group=group,
                       run=lambda command, **options: commands.append(command))
            observed = [part for command in commands for part in command if part in expected]
            self.assertEqual(set(observed), gas_helpers(group, expected))
            self.assertEqual(len(observed), len(gas_helpers(group, expected)))

    def test_current_storage_native_managed_and_browser_run_once_in_physical(self):
        expected=STORAGE_FACILITY
        for group in GROUPS:
            selected=[]
            def run(command,**options):
                self.assertNotIn('tools/check_storage_facility_upgrade.py',command)
                helpers=expected.intersection(command)
                if not helpers:return
                helper=helpers.pop();selected.append(helper)
                self.assertEqual(command[command.index('--pg-bin')+1],'/stock pg')
                self.assertEqual(options['env']['DOTNET'],'/selected sdk')
                if helper.endswith('_browser.py'):
                    self.assertEqual(command[command.index('--dotnet')+1],'/selected sdk')
                    self.assertEqual(command[command.index('--browser')+1],'/selected browser')
                    self.assertIn('--no-build',command)
            run_checks(Path('/stock pg'),Path('/selected sdk'),Path('/selected browser'),group=group,run=run)
            self.assertEqual(set(selected),expected if group=='physical' else set())
            self.assertEqual(len(selected),len(expected) if group=='physical' else 0)

    def test_async_workbench_checks_are_required_in_workspace_partition(self):
        for group in ALL_GROUPS:
            commands = []
            run_checks(Path('/pg'), Path('/dotnet'), Path('/browser'), group=group,
                       run=lambda command, **options: commands.append(command))
            self.assertEqual(sum('--async-reads' in command for command in commands), int(group == 'managed'))
            selected = [c for c in commands if '--async-workbench-check' in c]
            self.assertEqual(len(selected), 1 if group == 'workspace' else 0)
            if selected:
                self.assertIn('--browser-check', selected[0])
                self.assertEqual(selected[0][selected[0].index('--browser')+1], '/browser')

    def test_large_preview_queues_have_separate_required_partitions(self):
        previews = {}
        for group in GROUPS:
            commands = []
            run_checks(Path('/pg'), Path('/dotnet'), Path('/browser'), group=group,
                       run=lambda command, **options: commands.append(command))
            previews[group] = [c for c in commands if 'tools/run_preview.py' in c]
        self.assertLessEqual(max(map(len, previews.values())), 12,
                             'Long preview queues must be partitioned before they exceed CI limits')
        for group, flags in (
            ('reference', ('--price-authoring-check', '--contract-dates-check', '--custom-curves-check')),
            ('physical', ('--crude-lifecycle-check', '--lng-cargo-check')),
            ('gas', ('--gas-billing-check', '--gas-in-term-check', '--gas-indexed-check')),
            ('risk', ('--risk-jobs-check', '--historical-risk-check', '--factor-history-check')),
        ):
            for flag in flags:
                self.assertEqual([name for name, commands in previews.items()
                                  if any(flag in command for command in commands)], [group], flag)

    def test_calendar_and_payment_contracts_and_populated_upgrade_cannot_be_omitted(self):
        commands=[]
        run_checks(Path('/stock postgres/bin'),Path('/managed sdk/dotnet'),Path('/browser executable'),run=lambda command,**options:commands.append(command))
        for flag in ('--calendar-policy','--payment-date-policy','--published-clock','--gas-ticket','--calendar-authoring'):
            self.assertEqual(sum(flag in command for command in commands),1)
        self.assertEqual(sum('tools/check_payment_day_upgrade.py' in command for command in commands),1)
        self.assertEqual(sum('tools/check_calendar_upgrade.py' in command for command in commands),1)
        self.assertEqual(sum('tools/check_side_calendar_policies.py' in command for command in commands),1)
        self.assertEqual(sum('--side-calendar-check' in command for command in commands),1)

    def setUp(self):
        self.enterContext(redirect_stdout(io.StringIO()))

    def test_gas_amendment_checks_run_once_with_selected_tools(self):
        expected=GAS_AMENDMENTS
        for group in GROUPS:
            selected=[]
            def run(command,**options):
                helpers=expected.intersection(command)
                if not helpers:return
                helper=helpers.pop();selected.append(helper)
                self.assertEqual(command[command.index('--pg-bin')+1],'/stock pg')
                self.assertEqual(options['env']['DOTNET'],'/selected sdk')
                if helper.endswith('browser.py'):
                    self.assertEqual(command[command.index('--dotnet')+1],'/selected sdk')
                    self.assertEqual(command[command.index('--browser')+1],'/selected browser')
                    self.assertIn('--no-build',command)
            run_checks(Path('/stock pg'),Path('/selected sdk'),Path('/selected browser'),group=group,run=run)
            self.assertEqual(set(selected),gas_helpers(group,expected))
            self.assertEqual(len(selected),len(gas_helpers(group,expected)))

    def test_gas_amendments_have_a_required_separate_partition(self):
        self.assertIn('gas',GROUPS)

    def test_operator_value_workers_use_selected_resources_once_in_portfolio(self):
        expected={'tools/check_gas_operator_physical_value.py','tools/check_gas_operator_ordinary_value.py','tools/check_gas_known_cashout.py'}
        for group in GROUPS:
            selected=[]
            def run(command,**options):
                helpers=expected.intersection(command)
                if not helpers:return
                selected.append(helpers.pop())
                self.assertEqual(command[command.index('--pg-bin')+1],'/stock pg')
                self.assertEqual(command[command.index('--dotnet')+1],'/selected sdk')
                self.assertEqual(options['env']['DOTNET'],'/selected sdk')
                self.assertIn('--no-build',command)
            run_checks(Path('/stock pg'),Path('/selected sdk'),Path('/selected browser'),group=group,run=run)
            self.assertEqual(set(selected),expected if group=='portfolio' else set())
            self.assertEqual(len(selected),len(expected) if group=='portfolio' else 0)

    def test_frontend_widget_checks_use_the_explicit_browser_on_every_platform(self):
        frontend = []

        def run(command, **options):
            if command == ['npm', 'test', '--prefix', 'web']:
                frontend.append(options['env'].get('CHROME_BIN'))

        with patch.dict(os.environ, {'CHROME_BIN': '/an unrelated browser'}):
            run_checks(Path('/stock postgres/bin'), Path('/managed sdk/dotnet'),
                       Path('/selected browser executable'), group='frontend', run=run)
        self.assertEqual(frontend, ['/selected browser executable'])

    def test_physical_gas_workflow_is_qualified_in_gas_partition(self):
        commands=[]
        run_checks(Path('/stock postgres/bin'),Path('/managed sdk/dotnet'),Path('/browser executable'),group='gas',run=lambda command,**options:commands.append(command))
        gas=[c for c in commands if '--gas-daily-quantity-check' in c]
        self.assertEqual(len(gas),1)
        demo=[c for c in commands if '--gas-basis-demo-check' in c]
        self.assertEqual(len(demo),1)
        self.assertIn('--browser-check',demo[0])
        for argument in ('--facility-quantity-check','--gas-composition-check','--gas-setup-check','--browser-check','--browser'):
            self.assertIn(argument,gas[0])
        amendments=[c for c in commands if '--gas-amendments-check' in c]
        self.assertEqual(len(amendments),1)
        self.assertIn('--browser-check',amendments[0])
        self.assertIn('--browser',amendments[0])
        indexed=[c for c in commands if '--gas-indexed-check' in c]
        self.assertEqual(len(indexed),1)
        self.assertIn('--browser-check',indexed[0])
        self.assertIn('--browser',indexed[0])

    def test_publication_and_contract_authoring_are_qualified_in_reference_partition(self):
        commands=[]
        run_checks(Path('/stock postgres/bin'),Path('/managed sdk/dotnet'),Path('/browser executable'),group='reference',run=lambda command,**options:commands.append(command))
        selected=[c for c in commands if '--price-authoring-check' in c]
        self.assertEqual(len(selected),1)
        self.assertIn('--browser-check',selected[0])
        self.assertEqual(selected[0][selected[0].index('--browser')+1],'/browser executable')
        database=[]
        run_checks(Path('/stock postgres/bin'),Path('/managed sdk/dotnet'),Path('/browser executable'),group='database',run=lambda command,**options:database.append(command))
        for helper in ('tools/check_price_publications.py','tools/check_contract_price_terms.py'):
            self.assertTrue(any(helper in command for command in database),helper)

    def test_lng_booking_upgrade_recovery_and_worker_checks_run_in_physical_partition(self):
        commands=[]
        run_checks(Path('/stock postgres/bin'),Path('/managed sdk/dotnet'),Path('/browser executable'),group='physical',run=lambda command,**options:commands.append(command))
        cargo=[c for c in commands if '--lng-cargo-check' in c]
        self.assertEqual(len(cargo),1)
        self.assertIn('--browser-check',cargo[0])
        self.assertEqual(cargo[0][cargo[0].index('--browser')+1],'/browser executable')

    def test_manual_gas_operations_and_billing_recovery_run_in_gas_partition(self):
        commands=[]
        run_checks(Path('/stock postgres/bin'),Path('/managed sdk/dotnet'),Path('/browser executable'),group='gas',run=lambda command,**options:commands.append(command))
        for flag in ('--gas-operations-check','--gas-billing-check'):
            selected=[command for command in commands if flag in command]
            self.assertEqual(len(selected),1)
            self.assertIn('--browser-check',selected[0])
            self.assertEqual(selected[0][selected[0].index('--browser')+1],'/browser executable')

    def test_physical_crude_lifecycle_and_recovery_are_qualified_in_physical_partition(self):
        commands=[]
        run_checks(Path('/stock postgres/bin'),Path('/managed sdk/dotnet'),Path('/browser executable'),group='physical',run=lambda command,**options:commands.append(command))
        crude=[c for c in commands if '--crude-lifecycle-check' in c]
        self.assertEqual(len(crude),1)
        self.assertIn('--browser-check',crude[0])
        self.assertIn('--browser',crude[0])

    def test_exercise_right_checks_run_once_in_managed_and_all(self):
        sdk = Path('/selected sdk/dotnet')
        market = 'tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj'
        expected = [str(sdk), 'run', '--no-restore', '--project', market, '--', '--exercise-rights']
        for group in ('all', *ALL_GROUPS):
            with self.subTest(group=group):
                commands = []
                run_checks(Path('/pg'), sdk, Path('/browser'), group=group,
                           run=lambda command, **options: commands.append(command))
                selected = [command for command in commands if '--exercise-rights' in command]
                self.assertEqual(selected, [expected] if group in ('managed', 'all') else [])

    def test_futures_option_checks_run_in_the_gate(self):
        commands = []
        run_checks(Path('/stock postgres/bin'), Path('/managed sdk/dotnet'), Path('/browser executable'),
                   run=lambda command, **options: commands.append(command))
        market = 'tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj'
        runs = [c for c in commands if '--project' in c and c[c.index('--project') + 1] == market]
        self.assertEqual([c[c.index('--project') + 2:] for c in runs],
                         [[], ['--', '--futures-options'], ['--', '--spread-options'], ['--', '--exercise-rights'],
                          ['--', '--gas-swing'], ['--', '--vol-surface'], ['--', '--calendar-policy'], ['--', '--payment-date-policy'],
                          ['--', '--hybrid-monte-carlo-risk'],
                          *[['--', flag] for flag in ('--power-flat', '--power-atc', '--power-product-averages',
                                                      '--power-interval-quantities', '--ppa-sides')]])
        restores = [c for c in commands if 'restore' in c and c[c.index('restore') + 1] == market]
        self.assertEqual(len(restores), 1)

    def test_native_margin_consumers_use_selected_sdk_in_lifecycle_partition(self):
        helpers={f'tools/check_futures_margin_{name}.py' for name in ('sources','adoption','projection_sources')}|{'tools/check_trading_business_date.py'}
        for group in ('lifecycle','trading','standard'):
            selected=[]
            def run(command,**options):
                if helpers.intersection(command):
                    self.assertEqual(options['env']['DOTNET'],'/selected sdk/dotnet')
                    selected.append(command)
            with patch.dict(os.environ,{'DOTNET':'/unrelated sdk/dotnet'}):
                run_checks(Path('/pg'),Path('/selected sdk/dotnet'),Path('/browser'),group=group,run=run)
            self.assertEqual({value for command in selected for value in command if value in helpers},helpers if group=='lifecycle' else set())
            self.assertEqual(len(selected),4 if group=='lifecycle' else 0)

    def test_native_lifecycle_consumers_run_once_in_lifecycle_with_the_selected_sdk(self):
        for group in ('lifecycle','trading','standard'):
            selected=[]
            def run(command,**options):
                if LIFECYCLE_NATIVE.intersection(command):
                    self.assertEqual(options['env']['DOTNET'],'/selected sdk/dotnet')
                    self.assertEqual(command[command.index('--pg-bin')+1],'/stock pg')
                    selected.append(command)
            with patch.dict(os.environ,{'DOTNET':'/unrelated sdk/dotnet'}):
                run_checks(Path('/stock pg'),Path('/selected sdk/dotnet'),Path('/browser'),group=group,run=run)
            actual=[value for command in selected for value in command if value in LIFECYCLE_NATIVE]
            self.assertEqual(set(actual),LIFECYCLE_NATIVE if group=='lifecycle' else set())
            self.assertEqual(len(actual),len(LIFECYCLE_NATIVE) if group=='lifecycle' else 0)

    def test_each_option_model_is_qualified_once_in_lifecycle_with_selected_tools(self):
        helper='tools/check_lifecycle_valuation_browser.py'
        for group in GROUPS:
            selected=[]
            def run(command,**options):
                if helper in command:
                    self.assertEqual(options['env']['DOTNET'],'/selected sdk')
                    selected.append(command)
            run_checks(Path('/stock pg'),Path('/selected sdk'),Path('/selected browser'),group=group,run=run)
            self.assertEqual(len(selected),2 if group=='lifecycle' else 0)
            if selected:
                self.assertEqual({command[command.index('--kind')+1] for command in selected},{'physical','cash'})
                for command in selected:
                    self.assertEqual(command[command.index('--pg-bin')+1],'/stock pg')
                    self.assertEqual(command[command.index('--dotnet')+1],'/selected sdk')
                    self.assertEqual(command[command.index('--browser')+1],'/selected browser')
                    self.assertIn('--no-build',command)

    def test_normal_agent_settings_and_gas_approval_run_once_in_assistant_with_selected_tools(self):
        for group in ('assistant','standard','trading'):
            commands=[]
            run_checks(Path('/stock pg'),Path('/selected sdk'),Path('/selected browser'),group=group,
                run=lambda command,**options:commands.append(command))
            selected=[command for command in commands if 'tools/check_agent_operating_settings.py' in command]
            self.assertEqual(len(selected),1 if group=='assistant' else 0)
            for command in selected:
                self.assertEqual(command[command.index('--pg-bin')+1],'/stock pg')
                self.assertEqual(command[command.index('--dotnet')+1],'/selected sdk')
                self.assertEqual(command[command.index('--browser')+1],'/selected browser')
                self.assertIn('--no-build',command)

    def test_economic_workers_and_real_browsers_keep_selected_tools_and_partitions(self):
        browsers={'tools/check_gas_mixed_quantity_browser.py':'lifecycle','tools/check_lifecycle_browser.py':'lifecycle','tools/check_lifecycle_economic_browser.py':'portfolio'}
        for group in ('portfolio','lifecycle','trading','standard'):
            selected=[]
            def run(command,**options):
                if LIFECYCLE_PORTFOLIO.intersection(command):selected.append(command)
                for helper,partition in browsers.items():
                    if helper not in command:continue
                    self.assertEqual(group,partition)
                    self.assertEqual(command[command.index('--dotnet')+1],'/selected sdk')
                    self.assertEqual(command[command.index('--browser')+1],'/selected browser')
                    self.assertIn('--no-build',command)
                    self.assertEqual(options['env']['OPENETRM_CHECK_BROWSER'],'/selected browser')
            with patch.dict(os.environ,{'OPENETRM_CHECK_BROWSER':'/unrelated browser'}):
                run_checks(Path('/pg'),Path('/selected sdk'),Path('/selected browser'),group=group,run=run)
            actual=[value for command in selected for value in command if value in LIFECYCLE_PORTFOLIO]
            self.assertEqual(set(actual),LIFECYCLE_PORTFOLIO if group=='portfolio' else set())
            self.assertEqual(len(actual),len(LIFECYCLE_PORTFOLIO) if group=='portfolio' else 0)

    def test_every_executable_check_project_is_restored_and_run(self):
        expected = {
            str(project.relative_to(ROOT))
            for project in (ROOT / 'tests').glob('*/*.csproj')
            if ET.parse(project).findtext('.//OutputType') == 'Exe'
        }
        commands = []

        def run(command, **options):
            self.assertEqual(options['cwd'], ROOT)
            self.assertTrue(options['check'])
            self.assertFalse(options.get('shell', False))
            commands.append(command)

        run_checks(Path('/stock postgres/bin'), Path('/managed sdk/dotnet'),
                   Path('/browser executable'), run=run)
        restored = {c[c.index('restore') + 1] for c in commands if 'restore' in c}
        executed = {c[c.index('--project') + 1] for c in commands if '--project' in c}
        self.assertEqual(restored, expected | {'src/OpenEtrm.Worker/OpenEtrm.Worker.csproj','tools/OpenEtrm.Seed/OpenEtrm.Seed.csproj'})
        self.assertEqual(executed, expected)
        schedule_commands = [c for c in commands if '--project' in c and 'Api.Checks' in c[c.index('--project') + 1]]
        self.assertEqual([c[-2:] for c in schedule_commands],
                         [['--', flag] for flag in ('--context-resolution', '--read-references', '--schedules', '--power-hedge', '--async-reads', '--assistant-prompts', '--published-clock', '--calendar-authoring', '--gas-ticket', '--curve-mark-editor', '--historical-risk', '--monte-carlo-risk', '--position-adjustment', '--close-risk', '--automation',
                           '--live-calculation', '--reference-intake', '--preview-data', '--side-observation-compatibility',
                       '--gas-park-custody','--gas-park-custody-ticket','--gas-park-custody-evidence',
                       '--gas-park-custody-actions','--gas-park-custody-effects','--gas-park-custody-handler',
                       '--gas-park-custody-setup','--gas-park-custody-overview','--ticket-status-routing',
                       '--cash-payment-status','--gas-instant-ingress','--lifecycle-context-ingress',
                           '--gas-transport-capacity-adapters','--gas-transport-capacity-source',
                           '--gas-transport-capacity-metadata','--operational-business-context',
                           '--gas-transport-route-adapters','--gas-transport-route-source','--gas-transport-route-metadata',
                           '--delivery-workbench','--delivery-nomination-batch',
                           '--gas-transport-financial-commands','--gas-transport-financial-source',
                           '--gas-transport-financial-ticket','--gas-transport-financial-positions',
                           '--gas-transport-financial-adapters','--gas-transport-financial-labels','--gas-transport-financial-setup',
                           '--point-trader-mark-identity','--point-trader-mark-inputs','--point-trader-mark-commands','--point-trader-mark-receipts',
                       '--lifecycle-actions','--economic-risk-contract','--lifecycle-schedule-contract','--gas-billing-operating','--gas-billing-enrollment','--gas-owned-setup',
                          '--shared-native-markets','--derivative-methods','--gas-accepted-order','--lifecycle-residual-currencies','--gas-operator-contract')])
        self.assertTrue(any('--historical-risk-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--monte-carlo-risk-check' in c and '--with-exchange' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--reference-import-check' in c for c in commands))
        self.assertTrue(any('--live-calculation-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('build' in c and 'src/OpenEtrm.Worker/OpenEtrm.Worker.csproj' in c for c in commands))
        self.assertTrue(any('tools/check_database_login_boundaries.py' in c and '--no-build' in c for c in commands))
        self.assertTrue(any('tools/check_automation.py' in c and '--dotnet' in c for c in commands))
        self.assertIn(['node','--test','tests/browser/owned-browser.test.mjs','tests/browser/gas-ticket.test.mjs','tests/browser/gas-swing.test.mjs'],commands)
        self.assertTrue(any('--draft-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--execution-check' in c for c in commands))
        self.assertTrue(any('--power-actual-check' in c for c in commands))
        self.assertTrue(any('--power-booked-roll-check' in c for c in commands))
        self.assertTrue(any('--power-context-check' in c for c in commands))
        self.assertTrue(any('--interval-series-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--interval-curves-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--ppa-check' in c for c in commands))
        self.assertTrue(any('--settlement-quarter-check' in c for c in commands))
        self.assertTrue(any('specs/settlement' in c for c in commands))
        self.assertTrue(any('--close-risk-check' in c for c in commands))
        self.assertTrue(any('--close-risk-lease-check' in c for c in commands))
        self.assertTrue(any('--ppa-bookings-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--contract-dates-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--swap-risk-holdings-check' in c for c in commands))
        self.assertTrue(any('--side-observation-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--pilot-close-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--custom-curves-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--connected-portfolio-check' in c for c in commands))
        self.assertTrue(any('--market-dates-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--valuation-status-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('--desk-fields-check' in c for c in commands))
        self.assertTrue(any('--jobs-check' in c and '--historical-pool-check' in c and '--browser-check' in c for c in commands))
        self.assertTrue(any('tools/check_reference_editing.py' in c for c in commands))
        self.assertTrue(any('tools/check_power_integrity.py' in c for c in commands))
        self.assertTrue(any('--reference-check' in c and '--browser-check' in c for c in commands))
        for command in commands:
            if '--pg-bin' in command:
                self.assertEqual(command[command.index('--pg-bin') + 1], '/stock postgres/bin')
        self.assertTrue(any('--browser-check' in command for command in commands))
        # Party race/revocation checks allocate their own disposable session pool.
        self.assertTrue(any('--party-check' in command and '--browser-check' in command for command in commands))
        # So does page-assistant administration: the standard run already spends the whole session pool.
        self.assertTrue(any('--assistant-check' in command and '--browser-check' in command for command in commands))
        self.assertTrue(any('--assistant-workspace-check' in command and '--browser-check' in command for command in commands))
        self.assertTrue(any('--unit-check' in command and '--browser-check' in command for command in commands))
        self.assertTrue(any('--exchange-check' in command and '--browser-check' in command for command in commands))
        self.assertTrue(any('--risk-jobs-check' in command and '--browser-check' in command for command in commands))
        self.assertTrue(any('--factor-history-check' in command and '--browser-check' in command for command in commands))
        self.assertTrue(any('--parametric-risk-check' in command and '--browser-check' in command for command in commands))
        self.assertTrue(any('mixed-gas' in command for command in commands))
        self.assertTrue(any('--hosted-check' in command and '--browser-check' in command for command in commands))

    def test_failed_browser_checks_stop_later_work_and_preserve_failure(self):
        commands = []

        def run(command, **options):
            commands.append(command)
            if '--browser-check' in command:
                raise subprocess.CalledProcessError(17, command)

        with self.assertRaises(subprocess.CalledProcessError) as failure:
            run_checks(Path('/postgres/bin'), Path('/sdk/dotnet'), Path('/browser'), run=run)
        self.assertEqual(failure.exception.returncode, 17)
        self.assertFalse(any('mixed-gas' in command for command in commands))

    def test_disabling_assertions_is_rejected_before_checks_start(self):
        with patch.dict(os.environ, {'PYTHONOPTIMIZE': '1'}):
            with self.assertRaisesRegex(ValueError, 'assertions'):
                run_checks(Path('/postgres/bin'), Path('/sdk/dotnet'), Path('/browser'),
                           run=lambda *args, **kwargs: self.fail('Started an unchecked run'))

    def test_ci_groups_cover_the_full_gate_and_prepare_each_fresh_checkout(self):
        def commands(group):
            result = []
            run_checks(Path('/pg'), Path('/dotnet'), Path('/browser'), group=group,
                       run=lambda command, **options: result.append(tuple(command)))
            return result

        complete = commands('all')
        groups = {name: commands(name) for name in GROUPS}
        partitions = {name: commands(name) for name in ALL_GROUPS}
        self.assertTrue(set(complete).issubset(set().union(*(set(value) for value in partitions.values()))))
        # Shared behavior commands run once across the entire matrix, while
        # every isolated integration job builds the API, worker and web assets.
        for command in complete:
            if 'restore' in command or 'build' in command or 'ci' in command:
                continue
            self.assertEqual(sum(value.count(command) for value in partitions.values()), 1, command)
        for name, selected in groups.items():
            with self.subTest(group=name):
                self.assertIn(('npm', 'run', 'build', '--prefix', 'web'), selected)
                for project in ('tests/OpenEtrm.Api.Checks/OpenEtrm.Api.Checks.csproj',
                                'src/OpenEtrm.Worker/OpenEtrm.Worker.csproj'):
                    self.assertIn(('/dotnet', 'restore', project, '--locked-mode'), selected)
                    self.assertIn(('/dotnet', 'build', project, '--no-restore'), selected)
                self.assertIn(('/dotnet', 'restore', 'tests/OpenEtrm.MarketData.Checks/OpenEtrm.MarketData.Checks.csproj', '--locked-mode'), selected)
                first_integration = next(i for i, c in enumerate(selected) if '--pg-bin' in c)
                self.assertTrue(all('restore' not in c and 'build' not in c for c in selected[first_integration:]))
                self.assertFalse(any('unittest' in c or 'test' in c or 'run' in c and '--project' in c for c in selected))
        margin={f'tools/check_futures_margin_{part}.py' for part in ('sources','adoption','projection_sources')}|{'tools/check_trading_business_date.py'}
        def shared(command):
            return 'tools/run_preview.py' not in command and not (margin|LIFECYCLE_NATIVE|GAS_AMENDMENTS|LIFECYCLE_PORTFOLIO|OPTION_VALUE_APPS|AGENT_SETTINGS_BROWSER|STORAGE_FACILITY|TRANSPORT_CAPACITY|TRANSPORT_ROUTE|OPERATING_FACILITIES|POINT_TRADER_MARKS).intersection(command)
        previews = {name: [c for c in value if 'tools/run_preview.py' in c]
                    for name, value in groups.items()}
        owned = {name: [c for c in value if not shared(c)] for name, value in groups.items()}
        every_owned = [c for value in owned.values() for c in value]
        self.assertEqual(len(every_owned), len(set(every_owned)), 'owned suites run in exactly one group')
        self.assertTrue(all(owned.values()), 'every CI job must run an owned qualification suite')

        def group_of(flag):
            return next(name for name, value in previews.items() if any(flag in c for c in value))

        # The longest workflows each take a job of their own so that the
        # slowest single check, not a queue of them, sets the wall-clock time.
        for name, flag in (('recovery', '--close-risk-lease-check'),
                           ('settlement', '--scheduled-swap-check'),
                           ('exchange', '--exchange-check')):
            self.assertEqual(len(previews[name]), 1, name)
            self.assertIn(flag, previews[name][0])
        self.assertEqual(len(previews['standard']), 3)
        self.assertEqual(group_of('--curve-marks-check'), 'standard')
        mark_check=next(command for command in previews['standard'] if '--curve-marks-check' in command)
        self.assertEqual(mark_check[-3:], ('--browser-check', '--browser', '/browser'))
        self.assertEqual(previews['standard'][0][-3:], ('--browser-check', '--browser', '/browser'))
        self.assertFalse(any(flag.endswith('-check') and flag not in ('--check', '--browser-check')
                             for flag in previews['standard'][0]))
        self.assertEqual(group_of('--exchange-check'), 'exchange')
        self.assertFalse(any('--exchange-check' in command for command in previews['workspace']))
        self.assertEqual(group_of('--close-risk-check'), 'ppa')
        self.assertEqual(group_of('--assistant-check'), 'assistant')
        self.assertEqual(group_of('--risk-jobs-check'), 'risk')
        self.assertEqual(group_of('--factor-history-check'), 'risk')
        self.assertEqual(group_of('--parametric-risk-check'), 'risk')
        self.assertEqual(group_of('--hosted-check'), 'workspace')
        for flag in ('--draft-check', '--factor-swap-check', '--index-fixing-check',
                     '--side-observation-check', '--demo-instruments-check', '--close-out-drafts-check', '--unified-trading-check'):
            self.assertEqual(group_of(flag), 'trading', flag)
        for flag in ('--swap-risk-holdings-check', '--pilot-close-check', '--connected-portfolio-check',
                     '--valuation-status-check', '--desk-fields-check'):
            self.assertEqual(group_of(flag), 'portfolio', flag)

    def test_branch_pushes_rely_on_their_pull_request_run(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        triggers = workflow.split('\non:\n', 1)[1].split('\n\n', 1)[0]
        self.assertEqual(triggers, '  push:\n    branches: [master]\n  pull_request:\n  workflow_dispatch:')

    def test_failed_or_interrupted_group_never_claims_completion(self):
        for group in GROUPS:
            for failure in (subprocess.CalledProcessError(17, ['required']), KeyboardInterrupt()):
                with self.subTest(group=group, failure=type(failure).__name__):
                    output = io.StringIO()
                    def run(command, **options):
                        if '--pg-bin' in command:
                            raise failure
                    with redirect_stdout(output), self.assertRaises(type(failure)):
                        run_checks(Path('/pg'), Path('/dotnet'), Path('/browser'), group=group, run=run)
                    self.assertNotIn('verification suites passed', output.getvalue())
                    self.assertNotIn(f'verification group {group} passed', output.getvalue())

    def test_unknown_group_is_rejected_before_running_anything(self):
        with self.assertRaisesRegex(ValueError, 'group'):
            run_checks(Path('/pg'), Path('/dotnet'), Path('/browser'), group='typo',
                       run=lambda *args, **kwargs: self.fail('Started an incomplete gate'))

    def test_hosted_gate_requires_every_group_and_rejects_non_success(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        self.assertIn('    needs: impact\n', workflow)
        self.assertIn('        group: ${{ fromJSON(needs.impact.outputs.groups) }}\n', workflow)
        aggregate = workflow.split('  checks:\n', 1)[1].split('  publish:\n', 1)[0]
        self.assertIn('    needs: [impact, verification]\n', aggregate)
        self.assertIn('    if: always()\n', aggregate)
        self.assertIn('IMPACT_RESULT: ${{ needs.impact.result }}', aggregate)
        self.assertIn('VERIFICATION_RESULT: ${{ needs.verification.result }}', aggregate)
        command = re.search(r'^        run: (.*)$', aggregate, re.MULTILINE).group(1)
        for impact in ('success', 'failure', 'cancelled', 'skipped', ''):
            for verification in ('success', 'failure', 'cancelled', 'skipped', ''):
                result = subprocess.run(['bash', '-c', command], env={**os.environ,
                    'IMPACT_RESULT': impact, 'VERIFICATION_RESULT': verification})
                self.assertEqual(result.returncode == 0, impact == verification == 'success')
        publish = workflow.split('  publish:\n', 1)[1]
        self.assertIn('    needs: [impact, checks]\n', publish)
        self.assertIn("needs.impact.outputs.full == 'true'", publish)


if __name__ == '__main__':
    unittest.main()
