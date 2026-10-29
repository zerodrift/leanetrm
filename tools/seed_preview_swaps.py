"""Create explicit synthetic monthly/daily swaps through owned preview operations."""
from preview_fixture import trade_booking
import json
from pathlib import Path
from uuid import UUID, uuid5
from preview_client import PreviewClient
from seed_preview_positions import BOOKS, execution_frames
from preview_fixture import CAT, REV, WORKSPACE
from swap_fixture import REVISION

# The fixed side's membership coefficient: -1 pays fixed and receives floating.
FIXED_COEFFICIENT = '-1'


def seed_swaps(url, run_id, owned_root, client=None):
    root = Path(owned_root).resolve()
    if not (root/'data/postmaster.pid').is_file() or (root/'.openetrm-preview').read_text() != run_id:
        raise ValueError('Swap examples require the matching owned synthetic database.')
    manifest = json.loads((root/'manifest.json').read_text())
    if (manifest.get('mode'), manifest.get('catalogId'), manifest.get('revisionId'), manifest.get('workspaceId')) != ('local-synthetic-preview', CAT, REV, WORKSPACE):
        raise ValueError('Swap examples require the supported standard synthetic workspace.')
    client = client or PreviewClient(url)
    if client.call('health') != {'mode': 'local-synthetic-preview', 'runId': run_id}:
        raise ValueError('The application does not match the owner marker.')
    client.bootstrap(); client.call('profile', {'profile': 'trader'})
    workspace = client.call('workspace')
    frames = execution_frames(workspace)
    # Book B contains mixed holdings; Book A provides a separate futures-only scope.
    book = BOOKS[1]
    choices = [('test_gas', REV, 'monthly_index_price', '3', True),
               ('test_power', REV, 'monthly_index_price', '40', True),
               ('test_gas', REVISION, 'calendar_day_arithmetic', '3', False),
               ('test_power', REVISION, 'calendar_day_arithmetic', '40', False)]
    commands = []
    for frame in sorted(frames, key=lambda f: f['at']):
        context = {'snapshotId': frame['id'], 'convention': 'independent_side_financial_swap_v2', 'knowledgeMode': 'current'}
        catalog = client.call('swaps/catalog', context)
        for commodity, revision, averaging, strike, discounting in choices:
            eligible = sorted((i for i in catalog['indices'] if i['commodity'] == commodity and i['definitionRevisionId'] == revision and averaging in i['averaging']), key=lambda i: i['curve'])
            if not eligible or book not in [b['id'] for b in catalog['books']]:
                raise ValueError('Required exact synthetic index, curve or book is unavailable.')
            index = eligible[0]
            if not any(p['deliveryFrom'] == '2021-01-01' and p['deliveryTo'] == '2021-02-01' for p in index['contracts']):
                raise ValueError('The exact January delivery interval is unavailable.')
            operation = str(uuid5(UUID('e1000000-0000-0000-0000-000000000002'), ':'.join(('swap_schedule_examples_v2', frame['at'][:10], commodity, averaging))))
            commands.append({**context, **trade_booking(), 'operationId': operation, 'definitionRevisionId': revision, 'commodity':commodity, 'bookId': book,
                'quantity': '1000000' if averaging == 'monthly_index_price' else '100', 'fixedCoefficient': FIXED_COEFFICIENT, 'discounting': discounting,
                'fixed': {'price': strike, 'currency': index['currencyCode'], 'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01', 'paymentSchedule':{'frequency':'monthly','rule':'day_of_following_month','days':None,'dayOfMonth':18,'dayBasis':None,'shortMonth':'reject','adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}},
                'floating': {'spread': '0', 'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'curve': index['curve'], 'index': index['index'], 'currency': index['currencyCode'], 'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01', 'paymentSchedule':{'frequency':'monthly','rule':'days_after_period_end','days':46,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}, 'resetFrequency':'monthly' if averaging=='monthly_index_price' else 'daily','averagingMethod':'none' if averaging=='monthly_index_price' else 'arithmetic'}})
    # Resolve all commands and their exact schedules before the first mutation.
    for body in commands:
        status = client.call('swaps/status', {'operationId': body['operationId']})
        if not status.get('tradeId'):
            client.call('swaps/preview', body)
    return [client.call('swaps/book', body) for body in commands]
