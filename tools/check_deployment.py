#!/usr/bin/env python3
"""Validate rendered Compose boundaries without starting containers or reading private settings."""
import argparse
import json
import os
from pathlib import Path
import subprocess


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--compose',type=Path,default=Path(__file__).resolve().parents[1]/'deploy/compose.yaml')
    args=parser.parse_args()
    environment={**os.environ,'STATE_ROOT':'/var/tmp/openetrm-synthetic-state','PRIVATE_ROOT':'/var/tmp/openetrm-synthetic-private','API_IMAGE':'openetrm-api:synthetic','GATEWAY_ADDRESS':'192.0.2.10','GATEWAY_PORT':'18080','DEPLOYMENT_PROJECT':'openetrm-synthetic','DATABASE_CONTAINER':'openetrm-synthetic-database','API_CONTAINER':'openetrm-synthetic-api'}
    rendered=json.loads(subprocess.check_output(['docker','compose','--env-file','/dev/null','-f',str(args.compose.resolve()),'--profile','collection','config','--format','json'],env=environment,text=True))
    services=rendered['services']
    for name in ['api','collector']:
        service=services[name]
        if service.get('tmpfs')!=['/tmp:size=64m,mode=1777'] or not service.get('read_only') or (name=='collector' and service.get('ports')):
            raise ValueError(f'{name} requires one bounded temporary mount, a read-only image, and collector ports must remain unpublished.')
        if set(service.get('cap_drop',[]))!={'ALL'}:raise ValueError('Application capabilities must be dropped.')
    if services['database'].get('ports') or not rendered['networks']['database'].get('internal'):
        raise ValueError('The database must remain on its private network without published ports.')
    ports=services['api'].get('ports',[])
    if len(ports)!=1 or ports[0].get('host_ip')!='192.0.2.10' or ports[0].get('target')!=8080 or str(ports[0].get('published'))!='18080':
        raise ValueError('Bind only the API listener to the explicitly supplied private gateway address.')
    if set(services['api']['networks'])!={'database','web_ingress'} or 'web_ingress' in services['collector']['networks']:
        raise ValueError('Only the API may join its gateway ingress bridge.')
    print('PASS rendered deployment: valid private mounts, isolated database and collector, read-only API, one explicit gateway binding and no database/collector host ports')


if __name__=='__main__':main()
