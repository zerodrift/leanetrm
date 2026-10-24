#!/usr/bin/env python3
"""Build a credential-free, data-only Docker release from already verified outputs."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile

from release_bundle import COMMIT, REPOSITORY, inspect_image, require, sha256


def prepare_context(root, api, worker, web, context):
    # Validate both executable outputs before any Docker build or publication.
    for source, assembly in [(api, 'OpenEtrm.Api'), (worker, 'OpenEtrm.Worker')]:
        require((source / (assembly + '.dll')).is_file() and
                (source / (assembly + '.deps.json')).is_file(), 'API or worker publish output is missing')
    context.mkdir()
    for name, source, assembly in [('api', api, 'OpenEtrm.Api'), ('worker', worker, 'OpenEtrm.Worker')]:
        target = context / name
        shutil.copytree(source, target)
        dependencies = json.loads((target / (assembly + '.deps.json')).read_text())['libraries']
        providers = [entry.removeprefix('Npgsql/') for entry in dependencies if entry.startswith('Npgsql/')]
        require(len(providers) == 1 and all(char.isdigit() or char == '.' for char in providers[0]), 'provider license version is ambiguous')
        notice = root / 'LICENSES' / ('Npgsql-' + providers[0] + '.txt')
        require(notice.is_file(), 'version-matched provider license is missing')
        shutil.copyfile(notice, target / 'Npgsql-LICENSE.txt')
        require((target / 'Npgsql.dll').is_file(), 'required provider binary missing')
    shutil.copytree(web, context / 'web')
    shutil.copyfile(root / 'deploy/Dockerfile', context / 'Dockerfile')
    shutil.copyfile(root / 'LICENSE', context / 'LICENSE')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--api', type=Path, required=True)
    parser.add_argument('--worker', type=Path, required=True)
    parser.add_argument('--web', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--run-id', type=int, required=True)
    parser.add_argument('--run-attempt', type=int, required=True)
    args = parser.parse_args()
    require(COMMIT.fullmatch(args.commit) and REPOSITORY.fullmatch(args.repository)
            and args.run_id > 0 and args.run_attempt > 0, 'invalid release source')
    require(args.output.parent.exists() and not args.output.exists(), 'output must be a new file')
    root = Path(__file__).resolve().parents[1]
    tag = 'openetrm-release:' + args.commit
    with tempfile.TemporaryDirectory(prefix='openetrm-package-') as temporary:
        context = Path(temporary) / 'context'
        prepare_context(root, args.api, args.worker, args.web, context)
        subprocess.run(['docker', 'build', '--platform', 'linux/amd64', '--label', 'org.opencontainers.image.revision=' + args.commit,
                        '--label', 'org.opencontainers.image.source=https://github.com/' + args.repository,
                        '-t', tag, str(context)], check=True)
        files = Path(temporary) / 'release'; files.mkdir()
        subprocess.run(['docker', 'save', '-o', str(files / 'image.tar'), tag], check=True)
        with tarfile.open(files / 'image.tar', 'r:') as archive:
            records = json.load(archive.extractfile('manifest.json'))
            config_hash = hashlib.sha256(archive.extractfile(records[0]['Config']).read()).hexdigest()
        inspect_image(files / 'image.tar', tag, config_hash, args.repository, args.commit)
        shutil.copyfile(root / 'deploy/compose.yaml', files / 'compose.yaml')
        shutil.copyfile(root / 'LICENSE', files / 'LICENSE')
        (files / 'migrations').mkdir()
        for migration in sorted((root / 'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql')):
            shutil.copyfile(migration, files / 'migrations' / migration.name)
        manifest = {'version': 1, 'repository': args.repository, 'commit': args.commit,
                    'runId': args.run_id, 'runAttempt': args.run_attempt, 'imageTag': tag,
                    'imageConfigSha256': config_hash,
                    'files': {str(path.relative_to(files)): sha256(path) for path in sorted(files.rglob('*')) if path.is_file()}}
        (files / 'release.json').write_text(json.dumps(manifest, sort_keys=True) + '\n')
        with tarfile.open(args.output, 'w:gz', format=tarfile.USTAR_FORMAT) as archive:
            for path in sorted(files.rglob('*')):
                if path.is_file():
                    info = archive.gettarinfo(str(path), str(path.relative_to(files)))
                    info.uid = info.gid = 0; info.uname = info.gname = ''; info.mtime = 0
                    with path.open('rb') as source:
                        archive.addfile(info, source)
    print('Release bundle:', args.output.name, sha256(args.output))


if __name__ == '__main__':
    main()
