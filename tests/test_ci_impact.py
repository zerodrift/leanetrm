"""Impact selection against synthetic changes and disposable Git history."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from check_all import ALL_GROUPS, GROUPS
from ci_impact import changed_paths, plan


class ImpactTests(unittest.TestCase):
    def groups(self, *paths, event='pull_request'):
        return plan(paths, event=event)['groups']

    def test_documentation_and_site_only_need_repository_checks(self):
        for path in ('README.md', 'docs/architecture.md', 'site/index.html'):
            self.assertEqual(self.groups(path), ['repository'])

    def test_web_unit_tests_do_not_run_database_or_managed_checks(self):
        self.assertEqual(self.groups('web/tests/amount-display.test.mjs'),
                         ['repository', 'frontend'])

    def test_managed_test_edits_select_the_managed_checks(self):
        self.assertEqual(self.groups('tests/OpenEtrm.Rates.Checks/Program.cs'),
                         ['repository', 'managed'])

    def test_managed_integration_helpers_select_every_consumer(self):
        for path in ('tests/OpenEtrm.Api.Checks/GasBookingChecks.cs',
                     'tests/OpenEtrm.MarketData.Checks/PpaAcceptanceFixture.cs',
                     'tests/OpenEtrm.MarketData.Checks/MixedResolutionFixture.cs'):
            self.assertEqual(self.groups(path), list(ALL_GROUPS))

    def test_browser_test_edits_select_their_consumers(self):
        self.assertEqual(self.groups('tests/browser/swaps.mjs'),
                         ['repository', 'frontend', 'standard', 'trading'])
        self.assertEqual(self.groups('tests/browser/assistant-panel.mjs'),
                         ['repository', 'assistant'])
        self.assertEqual(self.groups('tests/browser/scheduled-authoring.mjs'),
                         ['repository', 'settlement'])
        self.assertEqual(self.groups('tests/browser/risk.mjs'),
                         ['repository', 'frontend', 'standard', 'risk'])
        self.assertEqual(self.groups('tests/browser/risk-closes.mjs'),
                         ['repository', 'risk'])

    def test_combined_changes_union_consumers_in_stable_order(self):
        self.assertEqual(self.groups('tests/browser/ppa.mjs', 'web/tests/grid.test.mjs',
                                     'tests/browser/swaps.mjs', 'tests/browser/ppa.mjs'),
                         ['repository', 'frontend', 'standard', 'trading', 'ppa'])

    def test_web_runtime_checks_every_integration_consumer(self):
        selected = self.groups('web/src/amount-display.ts')
        self.assertEqual(selected, ['repository', 'frontend', 'database', *GROUPS])

    def test_unknown_and_cross_cutting_changes_run_everything(self):
        for path in ('src/OpenEtrm.Shared/Temporal.cs', 'db/migrations/0001_base.sql',
                     '.github/workflows/ci.yml', 'global.json', 'deploy/compose.yaml',
                     'tools/run_preview.py', 'tests/browser/new-scenario.mjs',
                     'docs/executable.py', 'new-component/file.txt', 'AGENTS.md',
                     'LICENSE', 'web/package-lock.json'):
            with self.subTest(path=path):
                self.assertEqual(self.groups(path), list(ALL_GROUPS))

    def test_release_and_manual_runs_cannot_use_partial_verification(self):
        for event in ('push', 'workflow_dispatch', 'unexpected'):
            self.assertEqual(self.groups('web/tests/grid.test.mjs', event=event), list(ALL_GROUPS))
        self.assertEqual(self.groups('docs/plan.md', event='push'), list(ALL_GROUPS))
        self.assertEqual(self.groups('docs/plan.md', event='workflow_dispatch'), list(ALL_GROUPS))
        self.assertTrue(plan(['docs/plan.md'], event='push')['full'])
        self.assertTrue(plan(['src/new.cs'], event='push')['full'])

    def test_all_gas_jobs_are_required_for_shared_changes_and_release(self):
        gas_jobs = {'gas', 'gas-native', 'gas-browser'}
        for event, path in (
            ('pull_request', 'src/OpenEtrm.Api/Program.cs'),
            ('pull_request', 'web/src/app.ts'),
            ('pull_request', 'tests/browser/gas-operations.mjs'),
            ('push', 'docs/plan.md'),
            ('workflow_dispatch', 'docs/plan.md'),
        ):
            with self.subTest(event=event, path=path):
                self.assertTrue(gas_jobs.issubset(self.groups(path, event=event)))

    def test_missing_or_empty_diff_runs_everything(self):
        for paths in (None, []):
            self.assertEqual(plan(paths, event='pull_request')['groups'], list(ALL_GROUPS))


class GitChangesTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='openetrm-impact-')
        self.addCleanup(temporary.cleanup)
        self.repo = Path(temporary.name)
        self.git('init', '-q', '--initial-branch=master')
        self.git('config', 'user.name', 'Synthetic CI Check')
        self.git('config', 'user.email', 'ci@example.invalid')
        self.git('config', 'commit.gpgsign', 'false')
        self.git('config', 'core.hooksPath', '/dev/null')
        self.write('README.md', 'synthetic baseline')
        self.commit()
        self.base = self.git('rev-parse', 'HEAD').strip()

    def git(self, *args):
        return subprocess.check_output(['git', *args], cwd=self.repo, text=True,
                                       stderr=subprocess.PIPE)

    def write(self, path, value):
        target = self.repo / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(value)

    def commit(self):
        self.git('add', '--all')
        self.git('commit', '-qm', 'Synthetic change')
        return self.git('rev-parse', 'HEAD').strip()

    def test_deletion_and_rename_include_old_and_new_paths(self):
        self.write('src/shared.cs', 'synthetic')
        before = self.commit()
        (self.repo / 'docs').mkdir()
        self.git('mv', 'src/shared.cs', 'docs/shared.md')
        after = self.commit()
        paths = changed_paths(self.repo, 'push', {'before': before, 'after': after})
        self.assertEqual(set(paths), {'src/shared.cs', 'docs/shared.md'})
        self.assertTrue(plan(paths, event='push')['full'])

    def test_pull_request_checks_merge_result_and_target_branch_changes(self):
        self.git('checkout', '-qb', 'topic')
        self.write('docs/topic.md', 'synthetic description')
        head = self.commit()
        self.git('checkout', '-q', 'master')
        self.write('src/shared.cs', 'synthetic target change')
        base = self.commit()
        self.git('merge', '--no-ff', '-qm', 'Synthetic merge', 'topic')
        paths = changed_paths(self.repo, 'pull_request',
                              {'pull_request': {'base': {'sha': base}, 'head': {'sha': head}}})
        self.assertEqual(set(paths), {'docs/topic.md', 'src/shared.cs'})

    def test_multicommit_push_and_unusual_filenames_are_complete(self):
        self.write('src/first.cs', 'synthetic')
        self.commit()
        self.write('docs/a\nstrange name.md', 'synthetic')
        after = self.commit()
        paths = changed_paths(self.repo, 'push', {'before': self.base, 'after': after})
        self.assertEqual(set(paths), {'src/first.cs', 'docs/a\nstrange name.md'})

    def test_unrelated_checkout_cannot_qualify_only_its_documentation(self):
        self.git('checkout', '-qb', 'topic')
        self.write('src/runtime.cs', 'synthetic topic runtime')
        head = self.commit()
        self.git('checkout', '-q', 'master')
        self.write('docs/unrelated.md', 'synthetic unrelated description')
        self.commit()
        paths = changed_paths(self.repo, 'pull_request',
                              {'pull_request': {'base': {'sha': self.base}, 'head': {'sha': head}}})
        self.assertIsNone(paths)

    def test_bad_missing_or_unavailable_revisions_fall_back_to_full(self):
        for revision in ('0' * 40, 'f' * 40, '--output=bad', '', None):
            with self.subTest(revision=revision):
                self.assertIsNone(changed_paths(self.repo, 'push',
                                               {'before': revision, 'after': self.base}))
        self.assertIsNone(changed_paths(self.repo, 'pull_request', {}))

    def test_cli_emits_nonempty_matrix_and_release_admission(self):
        self.write('docs/only.md', 'synthetic')
        after = self.commit()
        event_file = self.repo / 'event.json'
        for event, payload, groups, full in (
            ('push', {'before': self.base, 'after': after}, list(ALL_GROUPS), 'true'),
            ('pull_request', {'pull_request': {'base': {'sha': self.base}, 'head': {'sha': after}}},
             ['repository'], 'false'),
        ):
            with self.subTest(event=event):
                event_file.write_text(json.dumps(payload))
                output = self.repo / (event + '-output')
                result = subprocess.run([sys.executable, str(ROOT / 'tools/ci_impact.py')],
                                        cwd=self.repo, text=True, capture_output=True, check=True,
                                        env={**os.environ, 'GITHUB_EVENT_NAME': event,
                                             'GITHUB_EVENT_PATH': str(event_file), 'GITHUB_OUTPUT': str(output)})
                self.assertEqual(json.loads(result.stdout)['groups'], groups)
                self.assertIn('groups=' + json.dumps(groups, separators=(',', ':')) + '\n', output.read_text())
                self.assertIn('full=' + full + '\n', output.read_text())


if __name__ == '__main__':
    unittest.main()
