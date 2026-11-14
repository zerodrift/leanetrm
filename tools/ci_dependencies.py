#!/usr/bin/env python3
"""Admit pinned PostgreSQL packages from a cache, Gitea bundle or public archives."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import pwd
import re
import shutil
import subprocess
import tarfile
import tempfile
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[1]


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def validate_file_record(record, suffix):
    if (not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.+~-]*' + re.escape(suffix), record['file'])
            or not re.fullmatch(r'[0-9a-f]{64}', record['sha256'])
            or type(record['size']) is not int or not 0 < record['size'] <= 512 * 1024 * 1024):
        raise ValueError('Invalid dependency file pin')


def validate_url(url):
    parsed = urlsplit(url)
    if (parsed.scheme not in ('https', 'http') or not parsed.hostname
            or parsed.username or parsed.password or parsed.fragment or parsed.query):
        raise ValueError('Dependency URL must be an HTTP(S) URL without credentials, query or fragment')


def validate_lock(lock):
    if (lock['format'] != 1 or lock['platform'] != 'ubuntu-24.04-amd64'
            or not re.fullmatch(r'18\.[0-9]+', lock['postgres_version'])
            or not 1 <= len(lock['packages']) <= 200):
        raise ValueError('Unsupported PostgreSQL dependency lock')
    validate_file_record(lock['bundle'], '.tar.gz')
    names = set()
    for package in lock['packages']:
        validate_file_record(package, '.deb')
        validate_url(package['url'])
        if (not re.fullmatch(r'[a-z0-9][a-z0-9+.-]+', package['package'])
                or not re.fullmatch(r'[0-9][A-Za-z0-9.+:~-]*', package['version'])
                or package['architecture'] not in ('amd64', 'all')):
            raise ValueError('Invalid Debian package identity')
        if not package['url'].startswith('https://') or package['file'] in names:
            raise ValueError('Public package URLs require HTTPS and unique filenames')
        names.add(package['file'])


def verify(path, record):
    if (path.is_symlink() or not path.is_file() or path.stat().st_size != record['size']
            or sha256(path) != record['sha256']):
        raise ValueError(f'Dependency integrity check failed: {record["file"]}')


def download(url, path):
    # These are public dependency bytes. No credentials, cookies or private configuration.
    validate_url(url)
    subprocess.run(['curl', '--fail', '--silent', '--show-error', '--location',
                    '--proto', '=https,http', '--proto-redir', '=https,http',
                    '--connect-timeout', '15', '--max-time', '300', '--retry', '3',
                    '--output', str(path), url], check=True)


def prepare_packages(lock, cache, bundle_url, *, download=download):
    """Validate every restored byte; commit only a completely verified acquisition."""
    validate_lock(lock)
    if bundle_url:
        validate_url(bundle_url)
    cache = Path(cache)
    cache.mkdir(parents=True, exist_ok=True)
    missing = []
    for package in lock['packages']:
        path = cache / package['file']
        if path.exists() or path.is_symlink():
            verify(path, package)
        else:
            missing.append(package)
    if not missing:
        print('PostgreSQL dependency cache hit: all package hashes verified.', flush=True)
    else:
        print('PostgreSQL dependency cache miss: ' + ('Gitea bundle.' if bundle_url else 'pinned public archives.'), flush=True)
        with tempfile.TemporaryDirectory(prefix='.acquire-', dir=cache) as directory:
            staging = Path(directory)
            if bundle_url:
                archive_path = staging / lock['bundle']['file']
                download(bundle_url, archive_path)
                verify(archive_path, lock['bundle'])
                with tarfile.open(archive_path, 'r:gz') as archive:
                    expected = {p['file']: p for p in lock['packages']}
                    seen = set()
                    for member in archive:
                        if (member.name not in expected or member.name in seen or not member.isfile()
                                or member.size != expected[member.name]['size']):
                            raise ValueError('Unexpected dependency archive member')
                        seen.add(member.name)
                        with archive.extractfile(member) as source, (staging / member.name).open('wb') as target:
                            shutil.copyfileobj(source, target)
                    if seen != set(expected):
                        raise ValueError('Incomplete dependency archive')
                # Validate all members, including ones already present in a partial cache.
                for package in lock['packages']:
                    verify(staging / package['file'], package)
            else:
                for package in missing:
                    download(package['url'], staging / package['file'])
                    verify(staging / package['file'], package)
            for package in missing:
                (staging / package['file']).replace(cache / package['file'])
    return [cache / p['file'] for p in lock['packages']]


def select_installation(lock, packages, *, run=subprocess.run):
    """Keep newer compatible runner libraries; never force a system downgrade."""
    selected = []
    for record, path in zip(lock['packages'], packages, strict=True):
        current = run(['dpkg-query', '-W', '-f=${db:Status-Status}\t${Version}', record['package']],
                      capture_output=True, text=True, check=False)
        status, _, version = current.stdout.partition('\t')
        if current.returncode != 0 or status != 'installed':
            selected.append(path)
            continue
        comparison = run(['dpkg', '--compare-versions', version, 'ge', record['version']], check=False)
        if comparison.returncode not in (0, 1):
            raise ValueError('Cannot compare installed package versions')
        if comparison.returncode == 1:
            selected.append(path)
        elif record['package'] in ('postgresql-18', 'postgresql-client-18') and version != record['version']:
            raise ValueError('A newer PostgreSQL server/client is installed; refresh the reviewed dependency lock')
    return selected


def install_packages(lock, packages):
    release = dict(line.split('=', 1) for line in Path('/etc/os-release').read_text().splitlines() if '=' in line)
    architecture = subprocess.check_output(['dpkg', '--print-architecture'], text=True).strip()
    if release.get('ID', '').strip('"') != 'ubuntu' or release.get('VERSION_ID', '').strip('"') != '24.04' or architecture != 'amd64':
        raise ValueError('The CI PostgreSQL bundle requires Ubuntu 24.04 amd64')
    # Apt resolves only local .debs and the installed system. It may not refresh or
    # fetch indexes/packages. Original package maintainer scripts run only after
    # all selected dependency bytes have passed verification.
    selected = select_installation(lock, packages)
    if selected:
        with tempfile.TemporaryDirectory(prefix='openetrm-offline-apt-') as directory:
            state = Path(directory)
            (state / 'sources.list').touch()
            (state / 'sourceparts').mkdir()
            (state / 'lists').mkdir()
            (state / 'archives').mkdir()
            # --no-download also disables apt's local-file acquisition. Populate
            # its canonical archive names so Pre-Depends can be installed offline.
            for record, path in zip(lock['packages'], packages, strict=True):
                name = f'{record["package"]}_{record["version"].replace(":", "%3a")}_{record["architecture"]}.deb'
                (state / 'archives' / name).symlink_to(path.resolve())
            subprocess.run(['sudo', 'env', 'DEBIAN_FRONTEND=noninteractive', 'apt-get',
                            '-o', f'Dir::Etc::sourcelist={state / "sources.list"}',
                            '-o', f'Dir::Etc::sourceparts={state / "sourceparts"}',
                            '-o', f'Dir::State::lists={state / "lists"}',
                            '-o', f'Dir::Cache::archives={state / "archives"}',
                            '-o', f'APT::Sandbox::User={pwd.getpwuid(os.getuid()).pw_name}',
                            '--no-download', '--no-remove', '--no-install-recommends', 'install', '-y',
                            *(str(path.resolve()) for path in selected)], check=True)
    version = subprocess.check_output(['/usr/lib/postgresql/18/bin/postgres', '--version'], text=True)
    if not version.startswith(f'postgres (PostgreSQL) {lock["postgres_version"]} '):
        raise ValueError('Installed PostgreSQL version differs from the dependency lock')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--lock', type=Path, default=ROOT / 'tools/ci/postgresql.lock.json')
    parser.add_argument('--cache', type=Path, required=True)
    parser.add_argument('--bundle-url', help='Exact public Gitea file URL; omitted means locked public package URLs')
    parser.add_argument('--verify-only', action='store_true', help='Acquire and verify without installation')
    args = parser.parse_args()
    lock = json.loads(args.lock.read_text())
    packages = prepare_packages(lock, args.cache, args.bundle_url)
    if not args.verify_only:
        install_packages(lock, packages)


if __name__ == '__main__':
    main()
