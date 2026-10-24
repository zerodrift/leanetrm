#!/usr/bin/env python3
"""Check the standalone dependency boundary without contacting a package registry."""
import json
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[1]


def main():
    projects=list((ROOT/'src').glob('**/*.csproj'))+list((ROOT/'tests').glob('**/*.csproj'))
    for project in projects:
        tree=ET.parse(project)
        for reference in tree.iter('ProjectReference'):
            target=(project.parent/reference.attrib['Include']).resolve()
            if not target.is_relative_to(ROOT) or not target.is_file():
                raise SystemExit(f'Project reference leaves this repository: {project.relative_to(ROOT)}')
        for package in tree.iter('PackageReference'):
            if (package.get('Include'),package.get('Version'))!=('Npgsql','10.0.3'):
                raise SystemExit(f'Review the new package boundary: {project.relative_to(ROOT)}')
        if list(tree.iter('Import')) or list(tree.iter('Exec')):
            raise SystemExit(f'Review the external build boundary: {project.relative_to(ROOT)}')
    web=json.loads((ROOT/'web/package.json').read_text())
    if web.get('dependencies',{})!={'echarts':'6.1.0','tabulator-tables':'6.6.1'} or web.get('devDependencies')!={'typescript':'7.0.2','esbuild':'0.28.2'}:
        raise SystemExit('Review the new browser dependency boundary.')
    if list(ROOT.glob('**/Cargo.toml')) or list(ROOT.glob('**/base.lock.json')):
        raise SystemExit('Unexpected native or external platform dependency manifest.')
    print(f'PASS standalone boundary: {len(projects)} managed projects; public database driver, pinned TypeScript/browser bundler and presentation components.')


if __name__=='__main__':
    main()
