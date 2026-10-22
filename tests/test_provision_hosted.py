"""The page assistant's credential key files that hosted provisioning writes (docs/ai-assistant.md §5.2, §11)."""
import os
from pathlib import Path
import stat
import sys
import tempfile
import unittest
from unittest.mock import patch
from uuid import uuid4

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from provision_hosted import credential_keys, prepare  # noqa: E402

KEYS = ('credential.key', 'credential-retry.key')


class CredentialKeyChecks(unittest.TestCase):
    def assert_private_keys(self, directory):
        written = [(directory / name).read_bytes() for name in KEYS]
        self.assertEqual([len(key) for key in written], [32, 32])
        self.assertNotEqual(written[0], written[1])
        for name in KEYS:
            self.assertEqual(stat.S_IMODE((directory / name).stat().st_mode), 0o600)

    def test_a_new_installation_gets_both_key_files_with_its_other_private_files(self):
        with tempfile.TemporaryDirectory() as root:
            output = Path(root) / 'pilot'
            prepare('https://pilot.synthetic.invalid', str(uuid4()), '2026c', output)
            self.assert_private_keys(output)

    def test_an_existing_installation_gets_just_the_two_files_for_the_api_account(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            (directory / 'host.json').write_text('{}')
            credential_keys(directory, os.getuid(), os.getgid())
            self.assert_private_keys(directory)
            self.assertEqual({p.name for p in directory.iterdir()}, {'host.json', *KEYS})
            self.assertEqual({((directory / name).stat().st_uid, (directory / name).stat().st_gid) for name in KEYS}, {(os.getuid(), os.getgid())})

    def test_an_existing_key_file_is_never_replaced(self):
        for existing in KEYS:
            with self.subTest(existing=existing), tempfile.TemporaryDirectory() as root:
                directory = Path(root)
                (directory / existing).write_bytes(b'k' * 32)
                with self.assertRaises(FileExistsError):
                    credential_keys(directory, os.getuid(), os.getgid())
                self.assertEqual((directory / existing).read_bytes(), b'k' * 32)
                self.assertEqual([p.name for p in directory.iterdir()], [existing])

    def test_a_failure_while_writing_leaves_no_key_file(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            with patch.object(os, 'fsync', side_effect=OSError('synthetic disk failure')), self.assertRaises(OSError):
                credential_keys(directory, os.getuid(), os.getgid())
            self.assertEqual(list(directory.iterdir()), [])


if __name__ == '__main__':
    unittest.main()
