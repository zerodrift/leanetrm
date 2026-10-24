"""The hosted release must carry its separately launched worker and notices."""
from pathlib import Path
import json
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from package_release import prepare_context
from release_bundle import ReleaseError


class WorkerReleaseTests(unittest.TestCase):
    def test_api_and_worker_outputs_and_provider_notices_are_packaged(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name, assembly in [('api', 'OpenEtrm.Api'), ('worker', 'OpenEtrm.Worker')]:
                output = root / name
                output.mkdir()
                (output / (assembly + '.dll')).write_bytes(b'synthetic assembly')
                (output / (assembly + '.deps.json')).write_text(json.dumps({'libraries': {'Npgsql/10.0.3': {}}}))
                (output / 'Npgsql.dll').write_bytes(b'synthetic provider')
            (root / 'web').mkdir()
            (root / 'web/index.html').write_text('Synthetic interface')
            prepare_context(ROOT, root / 'api', root / 'worker', root / 'web', root / 'context')
            self.assertEqual((root / 'context/worker/OpenEtrm.Worker.dll').read_bytes(), b'synthetic assembly')
            for name in ['api', 'worker']:
                self.assertEqual((root / ('context/' + name + '/Npgsql-LICENSE.txt')).read_bytes(),
                                 (ROOT / 'LICENSES/Npgsql-10.0.3.txt').read_bytes())

    def test_missing_worker_is_rejected_before_building_an_image(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaises(ReleaseError):
                prepare_context(ROOT, root / 'api', root / 'missing-worker', root / 'web', root / 'context')


if __name__ == '__main__':
    unittest.main()
