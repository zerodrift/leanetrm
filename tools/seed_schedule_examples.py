"""Publish explicit synthetic date examples only in a verified owned preview."""
import json
from pathlib import Path
from preview_client import PreviewClient
from preview_fixture import CAT, REV, RCAT, RREV, WORKSPACE


def seed_schedule_examples(url, run_id, owned_root, client=None):
    root = Path(owned_root).resolve()
    if not (root/'data/postmaster.pid').is_file() or (root/'.openetrm-preview').read_text() != run_id:
        raise ValueError('Schedule examples require the matching live owned preview.')
    manifest = json.loads((root/'manifest.json').read_text())
    required = {'mode':'local-synthetic-preview','catalogId':CAT,'revisionId':REV,
                'referenceCatalogId':RCAT,'referenceRevisionId':RREV,'workspaceId':WORKSPACE}
    if any(manifest.get(key) != value for key,value in required.items()):
        raise ValueError('Schedule examples require the exact standard synthetic fixture.')
    client = client or PreviewClient(url)
    if client.call('health') != {'mode':'local-synthetic-preview','runId':run_id}:
        raise ValueError('Schedule example HTTP identity differs from the owned preview.')
    boot = client.bootstrap()
    if (boot['workspaceId'],boot['runId']) != (WORKSPACE,run_id):
        raise ValueError('Schedule example workspace differs from the owned preview.')
    client.call('profile', {'profile':'editor'})
    library = client.call('date-schedules')
    sample = library['examples']['monthly']
    if sample['definition']['symbol'] != 'oe.sample_gas_months':
        raise ValueError('The explicitly selected synthetic schedule template changed.')
    calendars = [c['reference'] for c in library['calendars'] if c['reference'] == {
        'catalogId':RCAT,'revisionId':RREV,'code':'test_business'}]
    if len(calendars) != 1:
        raise ValueError('The explicit synthetic business calendar is unavailable.')
    expected = {key:sample[key] for key in ['definition','selection','effectiveFrom','effectiveTo']}
    expected['calendar'] = calendars[0]
    existing = [v for v in library['versions'] if v['definition']['symbol'] == sample['definition']['symbol']]
    if existing:
        if len(existing) != 1 or existing[0]['state'] != 'published' or any(existing[0][key] != value for key,value in expected.items()):
            raise ValueError('Existing sample schedule differs or is incomplete; inspect it before continuing.')
        return existing[0]
    draft = client.call('date-schedules/save', {'id':None,'version':None,'generation':None,**expected})
    client.call('profile', {'profile':'approver'})
    published = client.call('date-schedules/publish', {key:draft[key] for key in ['id','version','generation']})
    if published['state'] != 'published' or any(published[key] != value for key,value in expected.items()):
        raise ValueError('The exact synthetic schedule publication was not confirmed.')
    return published
