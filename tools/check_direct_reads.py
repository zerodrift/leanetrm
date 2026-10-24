"""Page reads called without a session match their pages; driven by check_page_reads.py."""
import json
import subprocess
from pathlib import Path
from preview_client import initial_frame

# An author's pages also carry authoring rights, which differ between working and historical views.
PROFILE = 'editor'
TRADER = 'trader'


def page_view(c, name, context=None):
    """Record the selected view's page reads in an existing session, so no session is spent.

    A historical view refuses current-only TOU definitions; that refusal is its page read."""
    full = c.call('workspace')
    effective = context['effectiveOn'] if context else '2020-12-17'
    frame = initial_frame(full, effective)
    evaluations = {curve: c.call('evaluate', {'snapshotId': frame['id'], 'curve': curve})
                   for curve in ['local_power', 'interest_rates']}
    contract = evaluations['local_power']['points'][0]['contractId']

    def current_only(path, body=None):
        return c.call(path, body) if context is None else c.call(path, body, expected=422)['code']
    return {'name': name, 'context': context, 'frames': full['frames'],
            'initialFrameIds': full['initialFrameIds'], 'frameId': frame['id'],
            'workspace': full, 'catalog': c.call('workspace/catalog'),
            'curveNames': c.call('curve-names'),
            'references': c.call('reference/catalog?table=quantity_specifications'),
            'evaluations': evaluations,
            'history': {'curve': 'local_power', 'contractId': contract,
                        'response': c.call('history', {'snapshotId': frame['id'],
                                                       'curve': 'local_power',
                                                       'contractId': contract})},
            'schedules': c.call('date-schedules'),
            'touProducts': current_only('tou-products'),
            'powerCatalog': current_only('power/catalog', {'snapshotId': frame['id']}),
            'parties': {kind: c.call('parties/catalog', {'snapshotId': frame['id'], 'kind': kind})
                        for kind in ('legal_entity', 'counterparty')}}


def trade_view(c):
    """Record trading pages as a trader, who may read trades: the workspace over working data
    captured now, a re-read of that capture and a selected historical context, and the booking
    directory, a pivoted Blotter and trade detail under the re-read."""
    c.call('profile', {'profile': TRADER})
    full = c.call('workspace')
    frame = initial_frame(full, '2020-12-18')
    report = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [],
              'measures': [], 'pivotConvention': None}
    pivot = {**report, 'mode': 'pivot', 'groupBy': ['book', 'market', 'contract'],
             'measures': ['quantity'], 'pivotConvention': 'trading_blotter_totals_v1'}
    current = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    captured = c.call('trading/workspace', {'context': current, 'view': report})
    reread = {**current, 'knowledgeMode': 'retained', 'contextId': captured['catalog']['context']['contextId']}
    future = next(r for r in captured['blotter']['holdings'] if r['instrumentType'] != 'financial_swap')
    trade = {'context': {**reread, 'convention': full['futuresConvention']}, 'tradeId': future['tradeId'], 'revision': None}
    recorded = {'profile': TRADER, 'frames': full['frames'], 'frameId': frame['id'], 'report': report, 'pivot': pivot,
                'captured': captured, 'reread': c.call('trading/workspace', {'context': reread, 'view': report}),
                'directory': c.call('trading/workspace', {'context': reread, 'view': report, 'bookingCatalog': 'directory'}),
                'pivoted': c.call('trading/blotter', {'context': reread, 'view': pivot}),
                'trade': {'request': trade, 'response': c.call('futures/editor', trade)}}
    selected = c.call('market/context', {'effectiveOn': '2020-12-18', 'mode': 'current', 'selection': 'latest_correction'})['context']
    past = initial_frame(c.call('workspace'), '2020-12-18')
    retained = {**current, 'snapshotId': past['id'], 'knowledgeMode': 'retained', 'contextId': selected['contextId']}
    recorded['historical'] = {'context': selected, 'frame': past,
                              'response': c.call('trading/workspace', {'context': retained, 'view': report})}
    c.call('market/current', {})
    return recorded


def run_checks(views, trades, dotnet, environment, directory):
    fixture = Path(directory) / 'direct-reads.json'
    fixture.write_text(json.dumps({'profile': PROFILE, 'views': views, 'trades': trades}))
    subprocess.run([str(dotnet), 'run', '--project', 'tests/OpenEtrm.Api.Checks', '--',
                    '--direct-reads', str(fixture)],
                   cwd=Path(__file__).resolve().parents[1], env=environment, check=True,
                   timeout=120)
