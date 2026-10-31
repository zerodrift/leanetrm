#!/usr/bin/env python3
"""Exercise service login profiles against isolated disposable PostgreSQL clusters."""
import argparse
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--no-build', action='store_true')
    args = parser.parse_args()
    bins, dotnet = args.pg_bin.resolve(), args.dotnet.resolve()
    environment = {k: v for k, v in os.environ.items() if not k.startswith('PG')}
    if not args.no_build:
        subprocess.run([str(dotnet), 'build', 'tests/OpenEtrm.Api.Checks', '-p:NuGetAudit=false', '-v', 'q'], cwd=ROOT, env=environment, check=True)
    for tracking in ('on', 'off'):
        root = Path(tempfile.mkdtemp(prefix='oetrm-boundary-', dir='/tmp')).resolve()
        safe_to_remove = True
        def run(command):
            result = subprocess.run([str(v) for v in command], cwd=ROOT, env=environment,
                                    capture_output=True, text=True, timeout=60)
            if result.returncode:
                raise RuntimeError(result.stdout + result.stderr)
            return result.stdout
        try:
            run([bins/'initdb', '-D', root/'data', '--auth-local=trust', '--auth-host=reject',
                 '--locale=C', '--encoding=UTF8', '--username=boundary_test_owner'])
            safe_to_remove = False
            run([bins/'pg_ctl', '-D', root/'data', '-l', root/'postgres.log', '-w', '-o',
                 shlex.join(['-k', str(root), '-h', '', '-p', '55439', '-c', f'track_commit_timestamp={tracking}']), 'start'])
            connection = root/'connection'
            connection.write_text(f'Host={root};Port=55439;Database=postgres;Username=boundary_test_owner;SSL Mode=Disable')
            connection.chmod(0o600)
            print(run([dotnet, ROOT/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0/OpenEtrm.Api.Checks.dll',
                       '--database-login-boundaries', connection, str(tracking == 'on').lower()]).strip())
        finally:
            if not safe_to_remove:
                run([bins/'pg_ctl', '-D', root/'data', '-w', '-m', 'fast', 'stop'])
                safe_to_remove = True
            if safe_to_remove:
                shutil.rmtree(root)


if __name__ == '__main__':
    main()
