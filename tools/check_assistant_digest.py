"""Managed page-digest contracts; uses the repository's installed pinned .NET SDK."""
import argparse
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def run_checks(dotnet):
    subprocess.run([str(dotnet), 'run', '--project', 'tests/OpenEtrm.Api.Checks', '--', '--assistant-workspace'],
                   cwd=ROOT, check=True, timeout=120)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path, required=True)
    run_checks(parser.parse_args().dotnet)
