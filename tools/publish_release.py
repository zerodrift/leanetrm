#!/usr/bin/env python3
"""Publish one immutable attested release; resume only an unpublished matching draft."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess

from release_bundle import COMMIT, REPOSITORY, require


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--attestation', type=Path, required=True)
    args = parser.parse_args()
    repository, commit = os.environ['GITHUB_REPOSITORY'], os.environ['GITHUB_SHA']
    require(REPOSITORY.fullmatch(repository) and COMMIT.fullmatch(commit)
            and os.environ['GITHUB_REF'] == 'refs/heads/master' and os.environ['GITHUB_EVENT_NAME'] == 'push', 'publication requires a master push')
    artifact = args.directory / 'openetrm-release.tar.gz'
    attestation = args.directory / 'openetrm-release.tar.gz.sigstore.json'
    require(artifact.is_file() and args.attestation.is_file(), 'attested release outputs are missing')
    shutil.copyfile(args.attestation, attestation)
    tag = 'deploy-' + commit
    result = subprocess.run(['gh', 'api', 'repos/' + repository + '/releases/tags/' + tag], text=True, capture_output=True)
    if result.returncode:
        require('HTTP 404' in result.stderr, 'could not resolve existing release')
        notes = args.directory / 'notes.txt'
        notes.write_text('Hosted application release for verified commit ' + commit + '.\n')
        subprocess.run(['gh', 'release', 'create', tag, '--repo', repository, '--target', commit, '--draft',
                        '--title', 'Hosted release ' + commit, '--notes-file', str(notes)], check=True)
    else:
        release = json.loads(result.stdout)
        require(release['tag_name'] == tag and release['target_commitish'] == commit
                and release['author']['login'] == 'github-actions[bot]', 'existing release ownership/source mismatch')
        require(set(asset['name'] for asset in release['assets']) <= {artifact.name, attestation.name}, 'unexpected existing release asset')
        if not release['draft']:
            require(set(asset['name'] for asset in release['assets']) == {artifact.name, attestation.name}, 'published release is incomplete')
            print('Existing immutable release retained.'); return
    subprocess.run(['gh', 'release', 'upload', tag, str(artifact), str(attestation), '--repo', repository, '--clobber'], check=True)
    subprocess.run(['gh', 'release', 'edit', tag, '--repo', repository, '--draft=false'], check=True)


if __name__ == '__main__':
    main()
