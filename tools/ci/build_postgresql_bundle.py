#!/usr/bin/env python3
"""Producer for postgresql.Dockerfile; apt verifies signed indexes and package hashes."""
import gzip
import hashlib
import json
from pathlib import Path
import re
import shlex
import subprocess
import sys
import tarfile
from urllib.parse import unquote


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main(version):
    if not re.fullmatch(r'18\.[0-9]+-[A-Za-z0-9.+~]+', version):
        raise ValueError('Select an exact PostgreSQL 18 Debian package version')
    apt = ['apt-get', '-o', 'APT::Update::Error-Mode=any',
           '-o', 'Dir::Etc::sourcelist=/build/postgresql.sources',
           '-o', 'Dir::Etc::sourceparts=-', '-o', 'Dir::State::lists=/build/lists',
           '-o', 'Dir::State::status=/build/base-status', '-o', 'Dir::Cache::archives=/build/archives']
    subprocess.run([*apt, 'update'], check=True)
    selection = ['--no-install-recommends', '--download-only', 'install', '-y',
                 f'postgresql-18={version}', f'postgresql-client-18={version}', f'libpq5={version}']
    uris = subprocess.check_output([*apt, '--print-uris', *selection], text=True)
    subprocess.run([*apt, *selection], check=True)
    sources = {}
    for line in uris.splitlines():
        if line.startswith("'"):
            url, filename, size, *_ = shlex.split(line)
            sources[unquote(filename)] = (url, int(size))
    packages = []
    for path in sorted(Path('/build/archives').glob('*.deb')):
        url, size = sources[unquote(path.name)]
        # Apt's cache filename includes the Debian epoch, sometimes URL-encoded.
        normalized = path.with_name(unquote(path.name).replace(':', '_'))
        path.rename(normalized)
        path = normalized
        if size != path.stat().st_size:
            raise ValueError('Downloaded size differs from signed archive metadata')
        fields = subprocess.check_output(['dpkg-deb', '--show', '--showformat=${Package}\t${Version}\t${Architecture}\t${Source}', str(path)], text=True).split('\t')
        source = re.fullmatch(r'([a-z0-9.+-]+)(?: \(([^)]+)\))?', fields[3] or fields[0])
        if not source:
            raise ValueError('Invalid source package identity')
        packages.append(dict(file=path.name, url=url, size=size, sha256=digest(path),
                             package=fields[0], version=fields[1], architecture=fields[2],
                             source=source[1], source_version=source[2] or fields[1]))
    if len(packages) != len(sources):
        raise ValueError('Incomplete package acquisition')
    output = Path('/out')
    output.mkdir()
    filename = 'postgresql-' + version.split('-')[0] + '-ubuntu-24.04-amd64.tar.gz'
    bundle = output / filename
    with bundle.open('wb') as target, gzip.GzipFile(filename='', mode='wb', fileobj=target, mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode='w') as archive:
            for package in packages:
                path = Path('/build/archives') / package['file']
                info = tarfile.TarInfo(path.name)
                info.size, info.mode, info.mtime = path.stat().st_size, 0o644, 0
                with path.open('rb') as source:
                    archive.addfile(info, source)
    lock = dict(format=1, platform='ubuntu-24.04-amd64', postgres_version=version.split('-')[0],
                bundle=dict(file=filename, size=bundle.stat().st_size, sha256=digest(bundle)), packages=packages)
    # Mirror matching source packages together with the unmodified binary packages.
    # Apt authenticates source-file hashes through the same signed archive indexes.
    source_dir = Path('/build/sources')
    source_dir.mkdir()
    identities = sorted({p['source'] + '=' + p['source_version'] for p in packages})
    subprocess.run([*apt, '--download-only', '--only-source', 'source', *identities], cwd=source_dir, check=True)
    source_bundle = output / filename.replace('.tar.gz', '-sources.tar.gz')
    source_files = []
    with source_bundle.open('wb') as target, gzip.GzipFile(filename='', mode='wb', fileobj=target, mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode='w') as archive:
            for path in sorted(source_dir.iterdir()):
                if not path.is_file():
                    continue
                source_files.append(dict(file=path.name, size=path.stat().st_size, sha256=digest(path)))
                info = tarfile.TarInfo(path.name)
                info.size, info.mode, info.mtime = path.stat().st_size, 0o644, 0
                with path.open('rb') as source:
                    archive.addfile(info, source)
    lock['source_bundle'] = dict(file=source_bundle.name, size=source_bundle.stat().st_size, sha256=digest(source_bundle))
    (output / 'source-files.json').write_text(json.dumps(source_files, indent=2) + '\n')
    (output / 'postgresql.lock.json').write_text(json.dumps(lock, indent=2) + '\n')
    # Retain the signed repository receipts alongside the distributed unmodified debs.
    with tarfile.open(output / 'archive-signatures.tar.gz', 'w:gz') as archive:
        for path in sorted(Path('/build/lists').glob('*InRelease')):
            archive.add(path, arcname=path.name)
    print(f'Prepared {len(packages)} packages; bundle SHA-256 {lock["bundle"]["sha256"]}', flush=True)


if __name__ == '__main__':
    main(sys.argv[1])
