"""Book original weekday examples only into a verified owned standard preview."""
from preview_fixture import trade_booking
import json
from pathlib import Path
from uuid import UUID, uuid5
from preview_client import PreviewClient
from preview_fixture import CAT, REV, WORKSPACE, identity

# Explicit synthetic executions, not prices inferred from a live feed or calendar.
EXECUTIONS = (
    ('2020-12-15', '2', '2.95', '-1', '3.05'),
    ('2020-12-16', '1', '2.975', '-2', '3.075'),
    ('2020-12-17', '3', '3', '-1', '3.1'),
    ('2020-12-18', '-1', '3.025', '2', '3.125'),
)
BOOKS = ('d1000000-0000-0000-0000-000000000001', 'd1000000-0000-0000-0000-000000000002')
NAMESPACE = UUID('e1000000-0000-0000-0000-000000000001')
CONVENTION = 'contract_lot_execution_pnl_v2'


def execution_frames(workspace):
    days = {row[0] for row in EXECUTIONS}
    frames = [frame for frame in workspace['frames']
              if frame['id'] in workspace['initialFrameIds'] and frame['at'][:10] in days and frame['at'][11:19] == '16:00:00']
    if sorted(frame['at'][:10] for frame in frames) != [row[0] for row in EXECUTIONS] or any(frame['at'][11:19] != '16:00:00' for frame in frames):
        raise ValueError('Weekday demo executions require the exact four supported original market frames.')
    return frames


def seed_positions(url, run_id, owned_root, client=None):
    root = Path(owned_root).resolve()
    if not (root/'data/postmaster.pid').is_file() or (root/'.openetrm-preview').read_text() != run_id:
        raise ValueError('Seeding requires the matching owner marker and live disposable database.')
    manifest = json.loads((root/'manifest.json').read_text())
    if (manifest.get('mode'), manifest.get('catalogId'), manifest.get('revisionId'), manifest.get('workspaceId'), manifest.get('example', {}).get('curve')) != ('local-synthetic-preview', CAT, REV, WORKSPACE, 'local_power'):
        raise ValueError('Only the explicitly supported standard synthetic example can be populated.')
    client = client or PreviewClient(url)
    if client.call('health') != {'mode':'local-synthetic-preview','runId':run_id}:
        raise ValueError('The HTTP application does not match the owned preview.')
    boot = client.bootstrap()
    if (boot['workspaceId'], boot['runId']) != (WORKSPACE, run_id):
        raise ValueError('The synthetic workspace does not match the owned preview.')
    client.call('profile', {'profile':'trader'})
    workspace = client.call('workspace')
    frames = execution_frames(workspace)
    contract = identity('5', 1)
    # Resolve every exact book/member before the first mutation. The business
    # command independently validates ticks, units, trading interval and authority.
    for frame in frames:
        catalog = client.call('futures/catalog', {'snapshotId':frame['id'],'convention':CONVENTION,'knowledgeMode':'current'})
        if not set(BOOKS).issubset({b['id'] for b in catalog['books']}) or not any(c['id'] == contract and c['curve'] == 'future' for c in catalog['contracts']):
            raise ValueError('Required synthetic books or dated contract are unavailable.')
    results = []
    for day, a_lots, a_price, b_lots, b_price in EXECUTIONS:
        frame = next(f for f in frames if f['at'][:10] == day)
        for book, lots, price in zip(BOOKS, (a_lots,b_lots), (a_price,b_price), strict=True):
            operation = uuid5(NAMESPACE, ':'.join(('weekday_demo_v1',CAT,REV,day,book)))
            results.append(client.call('futures/book', {'operationId':str(operation),'snapshotId':frame['id'],'convention':CONVENTION,
                'knowledgeMode':'current',**trade_booking(),'bookId':book,'curve':'future','contractId':contract,'lots':lots,'executionPrice':price}))
    return results
