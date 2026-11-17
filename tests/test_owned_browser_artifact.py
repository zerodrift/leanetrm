import contextlib
import io
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import check_storage_facility_browser as storage_browser


class StorageBrowserArtifactTests(unittest.TestCase):
    def setUp(self):
        environment = patch.dict('os.environ')
        environment.start()
        self.addCleanup(environment.stop)

    def arguments(self, directory):
        for name in ['psql', 'dotnet', 'browser']:
            (directory / name).touch()
        return ['check_storage_facility_browser.py', '--pg-bin', str(directory),
                '--dotnet', str(directory / 'dotnet'), '--browser', str(directory / 'browser'), '--no-build']

    def test_incomplete_explicit_artifact_refuses_before_database_provision(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            artifact = directory / 'api'
            artifact.mkdir()
            (artifact / 'OpenEtrm.Api.dll').touch()
            error = io.StringIO()
            with patch.object(sys, 'argv', self.arguments(directory) + ['--api-bin', str(artifact)]), \
                    patch.object(storage_browser, 'native_main') as provision, \
                    patch.object(storage_browser, 'build_assets') as build, contextlib.redirect_stderr(error):
                with self.assertRaises(SystemExit) as refused:
                    storage_browser.main()
            self.assertEqual(refused.exception.code, 2)
            self.assertIn('A coherent current-tree API artifact is required:', error.getvalue())
            provision.assert_not_called()
            build.assert_not_called()

    def test_ordinary_no_build_invocation_keeps_its_existing_provisioning_path(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            with patch.object(sys, 'argv', self.arguments(directory)), \
                    patch.object(storage_browser, 'native_main') as provision, \
                    patch.object(storage_browser, 'build_assets') as build:
                storage_browser.main()
            provision.assert_called_once()
            self.assertEqual(provision.call_args.kwargs['arguments'], ['--pg-bin', str(directory.resolve())])
            build.assert_not_called()

    def test_complete_explicit_artifact_is_admitted_without_building_or_replacing_source_bins(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            artifact = directory / 'frozen-api'
            artifact.mkdir()
            for name in ['OpenEtrm.Api.dll', 'OpenEtrm.Api.deps.json', 'OpenEtrm.Api.runtimeconfig.json']:
                (artifact / name).touch()
            with patch.object(sys, 'argv', self.arguments(directory) + ['--api-bin', str(artifact)]), \
                    patch.object(storage_browser, 'native_main') as provision, \
                    patch.object(storage_browser, 'build_assets') as build:
                storage_browser.main()
            provision.assert_called_once()
            build.assert_not_called()


if __name__ == '__main__':
    unittest.main()
