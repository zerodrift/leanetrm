"""A repeatable pipeline-gas example, only inside its matching disposable preview."""
from copy import deepcopy
from pathlib import Path
from uuid import UUID, uuid5
import json
from preview_client import initial_frame
from preview_fixture import trade_booking
from seed_demo_instruments import owned_client
from seed_preview_positions import BOOKS

NAMESPACE = UUID('1833468b-b5d8-45f0-856b-3c036b69b20c')

def history_frames(query, manifest):
    """Expose the generated dated observations through ordinary curve-history reads."""
    from preview_fixture import CAT, REV
    rows=json.loads(query(f"SELECT coalesce(jsonb_agg(x ORDER BY x.at),'[]') FROM(SELECT observed_at at,jsonb_agg(id ORDER BY series_id) AS \"observationIds\" FROM market_data.observations WHERE catalog_id='{CAT}' AND definition_revision_id='{REV}' AND source_code='fixture' AND source_event_id LIKE 'close/%' GROUP BY observed_at)x;"))
    if len(rows)!=246:raise ValueError('The gas basis demo requires its complete synthetic weekday history.')
    contracts=manifest['frames'][0]['curveContracts']
    manifest['frames']=[dict(at=row['at'],observationIds=row['observationIds'],curveContracts=deepcopy(contracts)) for row in rows[-96:]]+manifest['frames']


def seed(url, run_id, owned_root):
    client = owned_client(url, run_id, owned_root)
    workspace = client.call('workspace')
    before = dict(snapshotId=initial_frame(workspace, '2020-12-18')['id'], convention='physical_gas_fixed_quantity_v4', knowledgeMode='current')
    after = {**before, 'snapshotId': next(f['id'] for f in workspace['frames'] if f['at'].startswith('2021-01-15T08:00:00'))}
    catalog = client.call('gas/catalog', before)
    point = next(p for p in catalog['locations'] if p['deliveryPoint'] == 'synthetic_gas_point_a')
    terms = {**before, **trade_booking(), 'bookId': catalog['books'][0]['id'], 'definitionRevisionId': point['definitionRevisionId'],
             'gasSetupId': catalog['gasSetupId'], 'discounting': False, 'profile': 'fixed_gas_composition_v1',
             'physicalSides': [dict(code='gas', pipeline=point['pipeline'], zone=point['zone'], deliveryPoint=point['deliveryPoint'], product=point['product'],
                                    direction='receive', deliveryFrom='2021-01-01', deliveryTo='2021-01-02', quantityBasis='per_operating_day', quantity='100', link=None)],
             'cashSides': [dict(code='payment', role='consideration', direction='pay', source='gas', stage='delivery', price='3', tariffCode=None, paymentAt='2021-02-02T00:00:00Z')]}
    books=(BOOKS[0],BOOKS[1],BOOKS[0])
    parties=('d5000000-0000-0000-0000-000000000001','d5000000-0000-0000-0000-000000000002','d5000000-0000-0000-0000-000000000002')
    if not set(books)<={b['id'] for b in catalog['books']} or not set(parties)<={p['id'] for p in catalog['counterparties'] if p['revision']==1}:
        raise ValueError('The gas demo requires its exact two synthetic books and counterparties.')
    trades, obligations = [], []
    for n in range(3):
        body = {**deepcopy(terms), 'bookId':books[n], 'counterpartyId':parties[n], 'operationId': str(uuid5(NAMESPACE, f'trade/{n}'))}
        booked = client.call('gas/composition/status', dict(operationId=body['operationId']))
        if not booked.get('tradeId'):
            client.call('gas/composition/preview', body)
            booked = client.call('gas/composition/book', body)
        trades.append(booked['tradeId'])
        row = client.call('gas/quantities/read', dict(context=after, tradeId=booked['tradeId'], selectionPolicy='operational_projection_v1', includeHistory=False))['rows'][0]
        obligations.append(row['obligationId'])
        for kind,amount in [('nominated','90'),('confirmed','85'),('scheduled','80')]:
            operation=str(uuid5(NAMESPACE,f'quantity/{n}/{kind}'))
            outcome=client.call('gas/quantities/status',dict(operationId=operation))
            if not outcome.get('revisionId'):
                client.call('gas/quantities/record',dict(context=before,tradeId=booked['tradeId'],expectedRevision=1,operationId=operation,
                    obligationId=row['obligationId'],kind=kind,status='provisional',day='2021-01-01',quantity=amount,
                    evidence=f'Synthetic pipeline {kind} statement',expectedPreviousId=None))
    context = dict(effectiveOn='2021-01-15', knowledgeMode='current', retained=None, convention='facility_business_date_end_utc_v1')
    directory = client.call('facilities/quantities/directory', dict(context=context))
    owner = directory['ownerId']
    def record(name, command, hour='08'):
        request = dict(context=context, operationId=str(uuid5(NAMESPACE, name)), effectiveAt=f'2021-01-15T{hour}:00:00Z', ownerId=owner, command=command)
        return client.call('facilities/quantities/record', request)['outcome']
    created = record('ledger', dict(kind='create', obligationId=obligations[0], day='2021-01-01', facility=point['deliveryPoint']))
    ledger = created['ledgerId']
    source = record('reported', dict(kind='source', ledgerId=ledger, expectedPreviousId=None, quantity='100', evidence='Synthetic pipeline flow statement'))
    record('limit', dict(kind='limit', ledgerId=ledger, expectedPreviousId=None, quantity='90', evidence='Synthetic operating maximum'))
    plan = record('plan', dict(kind='allocate', ledgerId=ledger, expectedPreviousId=None, sourceRevisionId=source['revisionId'], evidence='Synthetic allocation statement',
                              allocations=[dict(obligationId=o, tradeRevision=1, quantity=q) for o, q in zip(obligations, ['60', '25', '10'])]))
    result = dict(trades=trades, obligations=obligations, ledgerId=ledger, sourceId=source['revisionId'], planId=plan['revisionId'],
                  context=context, bookingContext=before, quantityContext=after, benchmark='base_forward', basis='gas_basis', local='local_gas',
                  curvesOn='2020-12-18', operationsOn='2021-01-15', historyFrom='2020-01-06', historyTo='2020-12-18')
    (Path(owned_root)/'gas-basis-demo.json').write_text(json.dumps(result, indent=2)+'\n')
    return result
