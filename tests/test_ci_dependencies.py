"""Dependency cache admission must be identical on cold and warm runners."""
import hashlib
import io
import json
from pathlib import Path
import sys
import subprocess
import tarfile
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from ci_dependencies import prepare_packages, select_installation


def digest(data):
    return hashlib.sha256(data).hexdigest()


class DependencyCacheTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(self.enterContext(tempfile.TemporaryDirectory()))
        self.cache = self.root / 'cache'
        self.payloads = {'server.deb': b'synthetic server', 'library.deb': b'synthetic library'}
        self.lock = {'format': 1, 'platform': 'ubuntu-24.04-amd64', 'postgres_version': '18.6',
                     'packages': [{'file': name, 'sha256': digest(data), 'size': len(data),
                                   'package': name[:-4], 'version': '1.0', 'architecture': 'amd64',
                                   'url': 'https://archive.example/' + name}
                                  for name, data in self.payloads.items()]}
        self.archive = self.make_archive(self.payloads)
        self.downloads = []

    def make_archive(self, members):
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode='w:gz') as archive:
            for name, data in members.items():
                member = tarfile.TarInfo(name)
                member.size = len(data)
                archive.addfile(member, io.BytesIO(data))
        data = stream.getvalue()
        self.lock['bundle'] = {'file': 'postgresql.tar.gz', 'size': len(data), 'sha256': digest(data)}
        return data

    def download(self, url, path):
        self.downloads.append(url)
        path.write_bytes(self.archive if url.endswith('.tar.gz') else self.payloads[url.rsplit('/', 1)[1]])

    def test_mirror_miss_then_offline_hit_returns_identical_verified_packages(self):
        cold = prepare_packages(self.lock, self.cache, 'https://packages.example/bundle.tar.gz', download=self.download)
        self.assertEqual(self.downloads, ['https://packages.example/bundle.tar.gz'])
        self.assertEqual({p.name: p.read_bytes() for p in cold}, self.payloads)
        warm = prepare_packages(self.lock, self.cache, 'https://packages.example/bundle.tar.gz',
                                download=lambda *_: self.fail('cache hit attempted a download'))
        self.assertEqual(cold, warm)

    def test_public_miss_then_offline_hit_uses_only_locked_package_urls(self):
        cold = prepare_packages(self.lock, self.cache, None, download=self.download)
        self.assertCountEqual(self.downloads, [p['url'] for p in self.lock['packages']])
        self.assertEqual({p.name: p.read_bytes() for p in cold}, self.payloads)
        self.assertEqual(cold, prepare_packages(self.lock, self.cache, None,
                         download=lambda *_: self.fail('cache hit attempted a download')))

    def test_cached_tampering_is_rejected_without_download(self):
        paths = prepare_packages(self.lock, self.cache, None, download=self.download)
        paths[0].write_bytes(b'changed')
        with self.assertRaises(ValueError):
            prepare_packages(self.lock, self.cache, None,
                             download=lambda *_: self.fail('corruption must not cause silent replacement'))

    def test_corrupt_download_never_enters_cache(self):
        for mirror in (None, 'https://packages.example/bundle.tar.gz'):
            with self.subTest(mirror=mirror), self.assertRaises(ValueError):
                prepare_packages(self.lock, self.cache, mirror,
                                 download=lambda _, path: path.write_bytes(b'corrupted'))
        self.assertEqual(list(self.cache.glob('*.deb')), [])

    def test_mirror_failure_does_not_silently_change_sources(self):
        def unavailable(url, path):
            self.downloads.append(url)
            raise OSError('unavailable')
        with self.assertRaises(OSError):
            prepare_packages(self.lock, self.cache, 'https://packages.example/bundle.tar.gz', download=unavailable)
        self.assertEqual(self.downloads, ['https://packages.example/bundle.tar.gz'])

    def test_missing_one_package_can_be_repaired_without_trusting_other_files(self):
        paths = prepare_packages(self.lock, self.cache, None, download=self.download)
        paths[0].unlink()
        self.downloads.clear()
        prepare_packages(self.lock, self.cache, None, download=self.download)
        self.assertEqual(len(self.downloads), 1)

    def test_bundle_rejects_unexpected_paths_and_missing_members(self):
        for members in ({'../escape.deb': b'escape', **self.payloads},
                        {'extra.deb': b'extra', **self.payloads}, {'server.deb': b'synthetic server'}):
            with self.subTest(members=list(members)):
                self.archive = self.make_archive(members)
                with self.assertRaises(ValueError):
                    prepare_packages(self.lock, self.cache, 'https://packages.example/bundle.tar.gz', download=self.download)
                self.assertEqual(list(self.cache.glob('*.deb')), [])

    def test_lock_rejects_unsafe_names_duplicates_and_untrusted_schemes(self):
        for change in ({'file': '../escape.deb'}, {'file': 'library.deb'},
                       {'url': 'file:///etc/passwd'}, {'sha256': ''}, {'size': -1}):
            lock = json.loads(json.dumps(self.lock))
            lock['packages'][0].update(change)
            with self.subTest(change=change), self.assertRaises(ValueError):
                prepare_packages(lock, self.cache, None, download=self.download)
        self.assertEqual(self.downloads, [])

    def test_matching_archive_hash_does_not_bypass_individual_package_hashes(self):
        self.archive = self.make_archive({**self.payloads, 'server.deb': b'changed server!!'})
        with self.assertRaises(ValueError):
            prepare_packages(self.lock, self.cache, 'https://packages.example/bundle.tar.gz', download=self.download)
        self.assertEqual(list(self.cache.glob('*.deb')), [])

    def test_bundle_rejects_symlinks_and_duplicate_members(self):
        for kind in ('link', 'duplicate'):
            stream = io.BytesIO()
            with tarfile.open(fileobj=stream, mode='w:gz') as archive:
                for name, data in self.payloads.items():
                    member = tarfile.TarInfo(name)
                    member.size = len(data)
                    if kind == 'link':
                        member.type, member.linkname = tarfile.SYMTYPE, '/etc/passwd'
                    archive.addfile(member, io.BytesIO(data))
                    if kind == 'duplicate':
                        archive.addfile(member, io.BytesIO(data))
            self.archive = stream.getvalue()
            self.lock['bundle'].update(size=len(self.archive), sha256=digest(self.archive))
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                prepare_packages(self.lock, self.cache, 'https://packages.example/bundle.tar.gz', download=self.download)
            self.assertEqual(list(self.cache.glob('*.deb')), [])

    def test_cache_symlink_is_rejected_even_if_target_has_valid_bytes(self):
        self.cache.mkdir()
        outside = self.root / 'outside'
        outside.write_bytes(self.payloads['server.deb'])
        (self.cache / 'server.deb').symlink_to(outside)
        with self.assertRaises(ValueError):
            prepare_packages(self.lock, self.cache, None, download=self.download)
        self.assertEqual(self.downloads, [])


class WorkflowCacheTests(unittest.TestCase):
    def test_verification_and_publication_both_use_explicit_lockfile_caches(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        for section in ('verification', 'publish'):
            job = workflow.split(f'  {section}:\n', 1)[1].split('\n  checks:\n', 1)[0]
            self.assertIn('          cache: true\n', job)
            for pattern in ('global.json', 'src/**/packages.lock.json', 'tests/**/packages.lock.json'):
                self.assertIn('            ' + pattern + '\n', job)
            self.assertIn('          cache: npm\n          cache-dependency-path: web/package-lock.json', job)
        self.assertNotIn('node_modules\n', workflow)

    def test_postgresql_install_is_unconditional_and_cache_uses_the_complete_lock(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        step = workflow.split('      - name: Verify and install PostgreSQL 18\n')[1].split('      - name: ')[0]
        self.assertNotIn('if:', step)
        self.assertIn('python3 tools/ci_dependencies.py', step)
        self.assertIn("hashFiles('tools/ci/postgresql.lock.json')", workflow)
        self.assertNotIn('restore-keys:', workflow)
        self.assertNotIn('ACCC4CF8.asc', workflow)

    def test_producer_uses_checked_in_key_and_signature_verification(self):
        key = (ROOT / 'tools/ci/pgdg.asc').read_bytes()
        self.assertEqual(digest(key), '0144068502a1eddd2a0280ede10ef607d1ec592ce819940991203941564e8e76')
        recipe = (ROOT / 'tools/ci/postgresql.Dockerfile').read_text()
        self.assertIn('B97B0AFCAA1A47F044F244A07FCC7D46ACCC4CF8', recipe)
        self.assertNotIn('curl', recipe)
        self.assertNotIn('--allow-unauthenticated', recipe)


class InstalledPackageTests(unittest.TestCase):
    def test_missing_and_older_packages_are_installed_but_newer_libraries_are_retained(self):
        packages = [{'package': name, 'version': '2'} for name in ('absent', 'older', 'same', 'libpq5')]
        paths = [Path('/cache') / (p['package'] + '.deb') for p in packages]
        installed = {'older': '1', 'same': '2', 'libpq5': '3'}
        def run(command, **kwargs):
            if command[0] == 'dpkg-query':
                version = installed.get(command[-1])
                return subprocess.CompletedProcess(command, 0 if version else 1,
                                                   'installed\t' + version if version else '')
            self.assertEqual(command[:2], ['dpkg', '--compare-versions'])
            self.assertEqual(command[3], 'ge')
            return subprocess.CompletedProcess(command, 0 if int(command[2]) >= int(command[4]) else 1)
        self.assertEqual(select_installation({'packages': packages}, paths, run=run), paths[:2])

    def test_incompatible_newer_server_is_refused_before_installation(self):
        packages = [{'package': 'postgresql-18', 'version': '18.6-1'}]
        def run(command, **kwargs):
            if command[0] == 'dpkg-query':
                return subprocess.CompletedProcess(command, 0, 'installed\t18.7-1')
            return subprocess.CompletedProcess(command, 0)
        with self.assertRaises(ValueError):
            select_installation({'packages': packages}, [Path('/cache/server.deb')], run=run)


if __name__ == '__main__':
    unittest.main()
