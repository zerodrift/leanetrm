"""Prepare an explicit private seed plan from authorized catalog inspection; never apply it."""
import argparse
import json
import os
from pathlib import Path
from uuid import UUID
from henry_hub_curve_repair import prepare_commands


def unique(pairs):
    result={}
    for key,value in pairs:
        if key in result:raise ValueError('Duplicate JSON property: '+key)
        result[key]=value
    return result


def private_json(path):
    path=Path(path)
    if not path.is_absolute() or path.is_symlink() or not path.is_file() or path.stat().st_mode & 0o077 or path.stat().st_size>16*1024*1024:
        raise ValueError('Use an absolute private regular input file, at most16MiB.')
    return json.loads(path.read_text(),object_pairs_hook=unique)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for option in ('catalogs','calendar','source-bindings','administrator','snapshot-id','market-revision-id','curve-code','original-version','output'):
        parser.add_argument('--'+option,required=True)
    args=parser.parse_args()
    for value in (args.snapshot_id,args.market_revision_id):
        if str(UUID(value))!=value or UUID(value).int==0:raise ValueError('Select canonical nonempty exact identities.')
    catalogs=private_json(args.catalogs)
    if set(catalogs)!={'publicationCatalog','curveCatalog'}:raise ValueError('Use the exact authorized inspector output.')
    mapping=private_json(args.source_bindings)
    if set(mapping)!={'publications','sources'}:raise ValueError('Supply exact publication and source bindings.')
    result=prepare_commands(catalogs['publicationCatalog'],catalogs['curveCatalog'],args.snapshot_id,args.market_revision_id,
        mapping['publications'],private_json(args.calendar),administrator=args.administrator,
        curve_code=args.curve_code,original_version=int(args.original_version),source_bindings=mapping['sources'])
    encoded=(json.dumps(result,indent=2)+'\n').encode()
    output=Path(args.output)
    if not output.is_absolute() or len(encoded)>8*1024*1024 or len(result['commands'])>1024:
        raise ValueError('Choose an absolute new output path and a bounded command plan.')
    with os.fdopen(os.open(output,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600),'wb') as target:target.write(encoded)
    print(json.dumps(dict(prepared=True,commands=len(result['commands']),output=str(output))))


if __name__=='__main__':main()
