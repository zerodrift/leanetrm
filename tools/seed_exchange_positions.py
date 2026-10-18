"""Populate only the explicitly owned exchange example through booking operations."""
import json
from decimal import Decimal
from pathlib import Path
from uuid import UUID,uuid5
from preview_client import PreviewClient

def seed_positions(url,run_id,owned_root):
    root=Path(owned_root).resolve()
    if not (root/'data/postmaster.pid').is_file() or (root/'.openetrm-preview').read_text()!=run_id:
        raise ValueError('The exchange example requires its live owner marker and database.')
    manifest=json.loads((root/'exchange-manifest.json').read_text())
    if manifest.get('identityIssuer')!='https://exchange-preview.invalid' or manifest.get('futuresConvention')!='contract_lot_execution_pnl_v2':
        raise ValueError('Choose the explicitly configured exchange example.')
    c=PreviewClient(url,'exchange');boot=c.bootstrap()
    if (boot['runId'],boot['catalogId'],boot['workspaceId'])!=(run_id,manifest['catalogId'],manifest['workspaceId']):
        raise ValueError('HTTP and owned exchange configuration disagree.')
    c.call('profile',{'profile':'trader'});w=c.call('workspace')
    frame=next(f for f in w['frames'] if f['at']=='2026-10-13T16:00:00+00:00' or f['at']=='2026-10-13T16:00:00Z')
    context={'snapshotId':frame['id'],'convention':manifest['futuresConvention'],'knowledgeMode':'current'}
    catalog=c.call('futures/catalog',context)
    selected={}
    for contract in sorted(catalog['contracts'],key=lambda v:v['deliveryFrom']):
        if contract['curve']!='test_calendar_day_gas':selected.setdefault(contract['curve'],contract)
    if len(selected)!=19:raise ValueError('The bounded exchange example requires nineteen verified families.')
    results=[]
    for n,(curve,contract) in enumerate(sorted(selected.items())):
        operation=str(uuid5(UUID('ed032a31-883e-447a-809a-66a4d583a124'),manifest['catalogId']+':'+curve))
        body={'operationId':operation,**context,'bookId':catalog['books'][n%len(catalog['books'])]['id'],
            'curve':curve,'contractId':contract['id'],'lots':'2' if n%2==0 else '-1',
            'executionPrice':str(Decimal(contract['mark'])-Decimal(contract['tick'])),
            'legalEntityId':catalog['legalEntities'][0]['id'],'legalEntityRevision':catalog['legalEntities'][0]['revision'],
            'counterpartyId':catalog['counterparties'][0]['id'],'counterpartyRevision':catalog['counterparties'][0]['revision']}
        results.append(c.call('futures/book',body))
    return results
