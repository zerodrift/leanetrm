#!/usr/bin/env python3
"""Strict data-only release envelope shared by the publisher and installed deployer."""
import gzip
import hashlib
import json
from pathlib import Path
import re
import tarfile

MAXIMUM_BYTES = 2 * 1024 ** 3
ENTRYPOINT = ['dotnet', '/opt/openetrm/api/OpenEtrm.Api.dll', '--hosted-pilot']
FILE_NAME = re.compile(r'(?:release\.json|compose\.yaml|LICENSE|image\.tar|migrations/[0-9]{4}_[a-z0-9_]+\.sql)\Z')
SHA256 = re.compile(r'[a-f0-9]{64}\Z')
COMMIT = re.compile(r'[a-f0-9]{40}\Z')
REPOSITORY = re.compile(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\Z')


class ReleaseError(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise ReleaseError(message)


def sha256(path):
    result = hashlib.sha256()
    with Path(path).open('rb') as source:
        for part in iter(lambda: source.read(1024 * 1024), b''):
            result.update(part)
    return result.hexdigest()


def read_exact(source, count):
    parts = []
    while count:
        part = source.read(min(count, 1024 * 1024))
        require(bool(part), 'truncated release archive')
        parts.append(part)
        count -= len(part)
    return b''.join(parts)


def unpack(artifact, destination, repository, commit, run_id, run_attempt, maximum_bytes=MAXIMUM_BYTES):
    """Read USTAR headers before bodies; reject metadata extensions and all non-files."""
    destination = Path(destination)
    destination.mkdir(mode=0o700, parents=True, exist_ok=False)
    names, total = set(), 0
    with gzip.open(artifact, 'rb') as source:
        while True:
            header = read_exact(source, 512)
            if header == bytes(512):
                require(read_exact(source, 512) == bytes(512), 'invalid archive terminator')
                remainder = source.read(10241)
                require(len(remainder) <= 10240 and not any(remainder), 'trailing archive content')
                break
            try:
                entry = tarfile.TarInfo.frombuf(header, 'utf-8', 'strict')
            except (tarfile.TarError, UnicodeError, ValueError) as error:
                raise ReleaseError('invalid archive header') from error
            require(entry.type in (tarfile.REGTYPE, tarfile.AREGTYPE) and not entry.linkname
                    and not entry.sparse, 'archive permits regular files only')
            require(FILE_NAME.fullmatch(entry.name) and entry.name not in names, 'invalid or duplicate archive filename')
            names.add(entry.name)
            total += entry.size + 512
            limit = maximum_bytes if entry.name == 'image.tar' else min(maximum_bytes, 1024 * 1024)
            require(0 <= entry.size <= limit and total <= maximum_bytes and len(names) <= 147, 'release expansion limit exceeded')
            target = destination / entry.name
            target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
            with target.open('xb') as output:
                remaining = entry.size
                while remaining:
                    part = read_exact(source, min(remaining, 1024 * 1024))
                    output.write(part)
                    remaining -= len(part)
            padding = read_exact(source, (-entry.size) % 512)
            require(not any(padding), 'nonzero archive padding')
    require('release.json' in names, 'missing release manifest')
    try:
        manifest = json.loads((destination / 'release.json').read_text())
    except (ValueError, UnicodeError) as error:
        raise ReleaseError('invalid release manifest') from error
    require(set(manifest) == {'version', 'repository', 'commit', 'runId', 'runAttempt', 'imageTag', 'imageConfigSha256', 'files'}, 'unsupported release manifest fields')
    require(manifest['version'] == 1 and manifest['repository'] == repository and manifest['commit'] == commit
            and type(manifest['runId']) is int and manifest['runId'] > 0
            and type(manifest['runAttempt']) is int and manifest['runAttempt'] > 0
            and (run_id is None or manifest['runId'] == run_id)
            and (run_attempt is None or manifest['runAttempt'] == run_attempt), 'release source or run mismatch')
    require(COMMIT.fullmatch(commit) and REPOSITORY.fullmatch(repository)
            and manifest['imageTag'] == 'openetrm-release:' + commit
            and SHA256.fullmatch(manifest['imageConfigSha256']), 'invalid release identity')
    files = manifest['files']
    require(isinstance(files, dict) and names == {'release.json', *files}
            and {'image.tar', 'compose.yaml', 'LICENSE'} <= files.keys(), 'release file inventory mismatch')
    require(any(name.startswith('migrations/') for name in files), 'missing migration baseline')
    for name, expected in files.items():
        require(isinstance(expected, str) and SHA256.fullmatch(expected) and sha256(destination / name) == expected, 'release file hash mismatch')
    return manifest


def require_baseline(manifest, baseline, compose_sha256):
    migrations = {name.removeprefix('migrations/'): value for name, value in manifest['files'].items() if name.startswith('migrations/')}
    require(migrations == {item['name']: item['sha256'] for item in baseline}, 'migration review required; automatic schema upgrades are disabled')
    require(manifest['files']['compose.yaml'] == compose_sha256, 'deployment configuration review required')


def inspect_image(path, tag, expected_config, repository, commit):
    """Inspect a verified Docker save archive without extracting its layer paths."""
    with tarfile.open(path, 'r:') as archive:
        names, total, members = set(), 0, {}
        for entry in archive:
            require(entry.name not in names and len(names) < 8192, 'duplicate or excessive image archive members')
            names.add(entry.name)
            total += entry.size
            require(total <= MAXIMUM_BYTES and entry.size >= 0, 'image archive size limit exceeded')
            require(entry.isfile() or entry.isdir(), 'image archive contains non-file member')
            members[entry.name] = entry
        try:
            metadata = members['manifest.json']
            require(metadata.isfile() and metadata.size <= 1024 * 1024, 'image manifest size limit exceeded')
            records = json.load(archive.extractfile(metadata))
            require(len(records) == 1 and records[0]['RepoTags'] == [tag], 'image archive tag mismatch')
            config_entry = members[records[0]['Config']]
            require(config_entry.isfile() and config_entry.size <= 1024 * 1024, 'image configuration size limit exceeded')
            raw = archive.extractfile(config_entry).read()
            require(hashlib.sha256(raw).hexdigest() == expected_config, 'image configuration digest mismatch')
            config = json.loads(raw)
            runtime = config['config']
            require(config['architecture'] == 'amd64' and config['os'] == 'linux'
                    and runtime['User'] == '1654:1654' and runtime['Entrypoint'] == ENTRYPOINT
                    and not runtime.get('Cmd') and not runtime.get('Volumes')
                    and runtime['Labels']['org.opencontainers.image.revision'] == commit
                    and runtime['Labels']['org.opencontainers.image.source'] == 'https://github.com/' + repository,
                    'image runtime contract mismatch')
        except (KeyError, ValueError, TypeError, IndexError) as error:
            raise ReleaseError('invalid image archive metadata') from error
    return config
