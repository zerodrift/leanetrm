"""Additional explicitly synthetic instruments, booked through ordinary operations."""
from copy import deepcopy
from pathlib import Path
from uuid import UUID, uuid5
import json
from preview_client import PreviewClient
from preview_fixture import CAT, REV, WORKSPACE, trade_booking
from seed_preview_positions import BOOKS, execution_frames
from ppa_demo_terms import profile_terms

NAMESPACE = UUID('e1000000-0000-0000-0000-000000000003')


def owned_client(url, run_id, owned_root):
    root = Path(owned_root).resolve()
    if not (root / 'data/postmaster.pid').is_file() or (root / '.openetrm-preview').read_text() != run_id:
        raise ValueError('Demo trades require the matching owned synthetic database.')
    manifest = json.loads((root / 'manifest.json').read_text())
    if (manifest.get('mode'), manifest.get('catalogId'), manifest.get('revisionId'), manifest.get('workspaceId')) != ('local-synthetic-preview', CAT, REV, WORKSPACE):
        raise ValueError('Demo trades require the standard synthetic workspace.')
    c = PreviewClient(url)
    if c.call('health') != {'mode': 'local-synthetic-preview', 'runId': run_id}:
        raise ValueError('The application does not match the owned database.')
    c.bootstrap()
    c.call('profile', {'profile': 'trader'})
    return c


def seed_heat_rates(c):
    frames = sorted(execution_frames(c.call('workspace')), key=lambda f: f['at'])
    commands = []
    payment = dict(frequency='monthly', rule='day_of_following_month', days=None, dayOfMonth=18,
                   dayBasis=None, shortMonth='reject', adjustment='unadjusted', calendar=None, implementation='monthly_payment_date_rules_v1')
    for frame in frames:
        context = dict(snapshotId=frame['id'], convention='independent_side_financial_swap_v2', knowledgeMode='current')
        catalog = c.call('swaps/catalog', context)
        gas = next(i for i in catalog['indices'] if i['definitionRevisionId'] == REV and i['curve'] == 'base_forward')
        power = next(i for i in catalog['indices'] if i['definitionRevisionId'] == REV and i['curve'] == 'local_power_all_in')
        for sample, amount, rate, coefficient in [('receive_power', '1000', '7', '1'), ('pay_power', '250', '8', '-1')]:
            def side(index, quantity):
                return dict(basis='period_total', quantity=next(q['specification'] for q in index['quantityChoices'] if q['specification']['code'] == index['quantityCode']), amount=None,
                            allocation=None, priceApplication='period_average_times_quantity_v1', power=None,
                            periods=[dict(amount=quantity, fixed=None, floating=dict(settlement=dict(mode='curve_target',index=None,projectionPolicy=None),curve=index['curve'], index=index['index'], currency=index['currencyCode'],
                                deliveryFrom='2021-01-01', deliveryTo='2021-02-01', paymentSchedule=payment, resetFrequency='monthly', averagingMethod='none', spread='0'))])
            # The managed factor contract derives the second-side schedule exactly.
            first, second = side(power, amount), side(gas, None)
            commands.append({**context, **trade_booking(), 'operationId': str(uuid5(NAMESPACE, frame['at'] + ':' + sample)), 'bookId': BOOKS[1], 'executedAt': None,
                'quantity': None, 'fixedCoefficient': None, 'discounting': False, 'fixed': None, 'floating': None, 'definitionRevisionId': REV,
                'commodity': power['commodity'], 'swapType': 'scheduled_financial_swap', 'firstCoefficient': coefficient,
                'scheduled': dict(implementation=catalog['factorOptions']['swapImplementation'], first=first, second={**second, 'basis': catalog['factorOptions']['quantityBasis']},
                    factor=dict(denominatorQuantity=first['quantity'], numeratorQuantity=second['quantity'], value=rate, implementation=catalog['factorOptions']['factorImplementation'], unitConversionImplementation=catalog['factorOptions']['unitConversionImplementation']))})
    for body in commands:
        if not c.call('swaps/status', {'operationId': body['operationId']}).get('tradeId'):
            c.call('swaps/preview', body)
    return [c.call('swaps/book', body) for body in commands]


def seed_ppas(c, manifest):
    frame = sorted(execution_frames(c.call('workspace')), key=lambda f: f['at'])[0]
    physical = profile_terms(manifest)
    virtual = deepcopy(physical)
    virtual['memberships'][0] = dict(role='indexed_payment', ordinal=0, coefficient='1', side=dict(kind='indexed', quantity=deepcopy(physical['memberships'][0]['side']['quantity']),
        paymentRule=physical['memberships'][0]['side']['paymentRule'], location=None, fixedPrice=None,
        indexPrice=dict(curve=manifest['intervalCurves']['shaped'], quantity='test_power_quantity', multiplier='1', indexMultiplier='1',settlement=None, adder='0'), attribute=None))
    virtual['memberships'][1]['side']['quantity'] = deepcopy(physical['memberships'][0]['side']['quantity'])
    alternate = profile_terms(manifest, '45')
    for membership in alternate['memberships']:
        membership['coefficient'] = '-1' if membership['coefficient'] == '1' else '1'
    volume = deepcopy(physical)
    volume['normalizationAssets'] = [manifest['ppa']['assets'][0]]
    volume['memberships'][0]['side']['quantity'] = dict(kind='volume', sources=[manifest['ppa']['assets'][0] + '_60'], stage='generated', capacity=None, energy=None, hours=None, slots=[], assets=[])
    commands = []
    for name, quantity, terms in [('physical_80', '80', physical), ('physical_20', '20', physical), ('virtual_30', '30', virtual), ('physical_pay_50', '50', alternate), ('generation_70', '70', volume)]:
        commands.append(dict(operationId=str(uuid5(NAMESPACE, name)), snapshotId=frame['id'], tradeId=None, expectedRevision=None, bookId=BOOKS[1], boundaryAt=None,
            coverageFrom='2031-11-01T04:00:00Z', coverageTo='2031-11-04T05:00:00Z', quantity=quantity, reason='Synthetic demo booking', memo='Synthetic ' + name.replace('_', ' '), booking=trade_booking(), terms=terms))
    for body in commands:
        if not c.call('ppa/status', {'operationId': body['operationId']}).get('tradeId'):
            c.call('ppa/preview', dict(snapshotId=body['snapshotId'], definitionRevisionId=REV, referenceCatalogId=manifest['referenceCatalogId'], referenceRevisionId=manifest['referenceRevisionId'],
                scope='draft', selectedIds=[], explicitSubset=False, terms=body['terms'], quantity=body['quantity'], coverageFrom=body['coverageFrom'], coverageTo=body['coverageTo'],
                knownAt=c.call('ppa/catalog', {'snapshotId': frame['id']})['knownAt'], pendingActuals='estimate_until_actual_v1', detailMonths=[]))
    return [c.call('ppa/book', body) for body in commands]
