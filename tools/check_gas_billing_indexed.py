"""Final daily billing of synthetic named-index gas payments through the native operations.

Expected amounts are worked by hand from final quantity x share x (fixing + spread) x direction: the daily index
fixes 13 January at 3.30 and the monthly index fixes January at 3.00 (gas_pricing_fixture).
"""
import json
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import identity
from check_booked_side_programs import runtime_query
from check_gas_pricing import request
from check_physical_operations import january_delivery_frame
from native_read_transport import select_context_inputs

INDEXED = 'gas_final_daily_indexed_cash_v1'
DAILY_FIXING = identity('7', 94600 * 100 + 90)
NEXT_DAY_FIXING = identity('7', 94601 * 100 + 90)
MONTHLY_FIXING = identity('7', 94610 * 100 + 90)


def run_checks(url, query, manifest):
    c = PreviewClient(url); c.bootstrap(); c.call('profile', dict(profile='trader'))
    context, body = request(c)
    after = {**context, 'snapshotId': january_delivery_frame(c.call('workspace'))}
    monthly, daily = body['cashSides'][0], body['cashSides'][1]
    body['operationId'] = str(uuid4())
    body['physicalSides'][0]['quantity'] = '100'
    body['cashSides'] = [
        {**daily, 'code': 'daily', 'part': None},
        {**monthly, 'code': 'shared', 'part': None, 'share': '0.4'},
        {**monthly, 'code': 'band', 'part': dict(kind='band', lower='30', upper='70'),
         'indexed': {**monthly['indexed'], 'spread': '-0.25'}},
        {**daily, 'code': 'receive', 'part': None, 'direction': 'receive', 'indexed': {**daily['indexed'], 'spread': '0'}}]
    trade = c.call('gas/composition/book', body)['tradeId']
    obligation = c.call('gas/quantities/read', dict(context=after, tradeId=trade, selectionPolicy='operational_projection_v1'))['rows'][0]['obligationId']

    def final(day, value, previous=None):
        return c.call('gas/quantities/record', dict(context=after, tradeId=trade, expectedRevision=1, operationId=str(uuid4()),
            obligationId=obligation, day=day, quantity=value, kind='allocated', status='final', expectedPreviousId=previous,
            evidence='Synthetic final allocation'))

    def preview(slot=1, day='2021-01-13', view=after, profile=INDEXED):
        return dict(context=view, tradeId=trade, expectedRevision=1, cashSlot=slot, day=day, selectionPolicy='final_allocated_billing_v1',
                    profile=profile, decimalPlaces=2, roundingPolicy='currency_minor_units_to_even_v1')

    def accept(reviewed, value, *, previous=None, reason=None, due=None, **changes):
        return {**dict(preview=reviewed, operationId=str(uuid4()), previewHash=value['previewHash'], sourceId=value['sourceId'],
                       fixingObservationId=value['fixing']['observationId'], expectedPreviousId=previous, correctionDueAt=due,
                       reason=reason, evidence='Synthetic indexed billing review'), **changes}

    claims = {(row['cashSlot'], row['day']): row for row in c.call('gas/billing/read', dict(context=after, tradeId=trade))['claims']}
    claim = claims[1, '2021-01-13']
    assert claim['supported'] and claim['profiles'] == [INDEXED] and claim['refusal'] is None, claim
    assert claim['index'] == dict(code='synthetic_entry_daily', convention='named_index_daily_v1', spread='0.05'), claim
    assert c.call('gas/billing/preview', preview(), expected=422)
    allocation = final('2021-01-13', '98')

    # A fixed profile cannot bill an indexed payment, and a day whose gas day has not ended is not billable.
    for profile in ('gas_final_daily_fixed_cash_v1', 'gas_final_daily_entry_cash_v1'):
        assert c.call('gas/billing/preview', preview(profile=profile), expected=422)['code'] == 'gas_billing_profile'
    assert c.call('gas/billing/preview', preview(day='2021-01-15'), expected=422)

    first = c.call('gas/billing/preview', preview())
    assert (first['amount'], first['contribution'], first['previousId']) == ('-328.3', '-328.3', None), first
    assert first['fixing'] == {**first['fixing'], 'observationId': DAILY_FIXING, 'value': '3.3', 'spread': '0.05', 'unitPrice': '3.35',
                               'correctsId': None, 'index': 'synthetic_entry_daily', 'policy': 'final_fixing_head_v1'}, first
    assert first['contractualDueAt'][:10] == '2021-02-10' and not first['alreadyDetermined']
    assert c.call('gas/billing/preview', preview(2))['amount'] == '-117.6'
    assert c.call('gas/billing/preview', preview(3))['amount'] == '-110'
    receive = c.call('gas/billing/preview', preview(4))
    assert receive['amount'] == '323.4'

    # The database works the amount out itself and selects the fixing itself. Calling the writer directly with a forged
    # amount, or with another day's fixing as the reviewed observation, records nothing.
    honest = dict(sourceId=first['sourceId'], amount='-328.3', numerator='-3283', denominator='10', fixingObservationId=DAILY_FIXING)
    for calculation, named, refusal in [({**honest, 'amount': '-328.2'}, DAILY_FIXING, 'gas_billing_amount'),
                                        ({**honest, 'numerator': '-3282'}, DAILY_FIXING, 'gas_billing_amount'),
                                        ({**honest, 'fixingObservationId': NEXT_DAY_FIXING}, NEXT_DAY_FIXING, 'gas_billing_fixing')]:
        wire = json.dumps(accept(preview(), first, fixingObservationId=named)).replace("'", "''")
        runtime_query(query, manifest, select_context_inputs(manifest['catalogId'], first['context']) + f"""
            DO $probe$ BEGIN BEGIN
              PERFORM security.record_gas_billing('{manifest['catalogId']}','{wire}',
                '2021-01-15T08:00:00Z','{first['context']['contextId']}','{json.dumps(calculation)}');
              RAISE EXCEPTION 'forged_indexed_billing_accepted';
            EXCEPTION WHEN raise_exception THEN
              IF SQLERRM<>'{refusal}' THEN RAISE;END IF;
            END;END$probe$;""")

    # The reviewed fixing is part of the request: absent, or another observation, is not the reviewed billing.
    unnamed = accept(preview(), first); unnamed.pop('fixingObservationId')
    assert c.call('gas/billing/determine', unnamed, expected=422)['code'] == 'gas_billing_input'
    c.call('gas/billing/determine', accept(preview(), first, fixingObservationId=str(uuid4())), expected=409)
    request_one = accept(preview(), first)
    original = c.call('gas/billing/determine', request_one)
    assert (original['amount'], original['contribution']) == ('-328.3', '-328.3')
    assert c.call('gas/billing/determine', request_one) == original
    assert c.call('gas/billing/determine', {**request_one, 'operationId': str(uuid4())})['determinationId'] == original['determinationId']
    history = c.call('gas/billing/read', dict(context=after, tradeId=trade))
    assert [row['fixing_observation_id'] for row in history['determinations']] == [DAILY_FIXING]
    # Another authorized actor reviewing the same accepted quantity and fixing is given the existing determination.
    other = PreviewClient(url); other.bootstrap(); other.call('profile', dict(profile='trader'))
    elsewhere = {**after, 'snapshotId': january_delivery_frame(other.call('workspace'))}
    theirs = other.call('gas/billing/preview', preview(view=elsewhere))
    assert theirs['alreadyDetermined'] and theirs['fixing']['observationId'] == DAILY_FIXING
    assert other.call('gas/billing/determine', accept(preview(view=elsewhere), theirs))['determinationId'] == original['determinationId']
    # A monthly index gives the day the month's fixing; its share entry is determined like any other.
    shared = c.call('gas/billing/preview', preview(2))
    assert (shared['fixing']['observationId'], shared['fixing']['value'], shared['fixing']['unitPrice']) == (MONTHLY_FIXING, '3', '3')
    assert c.call('gas/billing/determine', accept(preview(2), shared))['amount'] == '-117.6'
    retained = {**after, 'knowledgeMode': 'retained', 'contextId': history['context']['contextId']}
    assert c.call('gas/billing/preview', preview())['alreadyDetermined']

    # The publisher corrects the fixing to 3.31. The fixing is selected by knowledge, not by the snapshot's pins: every
    # current view sees the correction, and nothing is restated until it is reviewed with its own cause.
    publisher = PreviewClient(url); publisher.bootstrap(); publisher.call('profile', dict(profile='editor'))
    published = publisher.call('corrections', dict(snapshotId=january_delivery_frame(publisher.call('workspace')), observationId=DAILY_FIXING, value='3.31'))
    corrected = published['observationId']
    second = c.call('gas/billing/preview', preview())
    assert (second['amount'], second['contribution'], second['previousId']) == ('-329.28', '-0.98', original['determinationId']), second
    assert (second['fixing']['observationId'], second['fixing']['correctsId'], second['fixing']['value']) == (corrected, DAILY_FIXING, '3.31')
    assert not second['alreadyDetermined']
    # A context that does not yet know the correction still selects the superseded fixing. The writer records a new
    # determination only on a fixing that has no correction at all, so that stale review cannot be recorded directly.
    stale = dict(sourceId=receive['sourceId'], amount='323.4', numerator='3234', denominator='10', fixingObservationId=DAILY_FIXING)
    wire = json.dumps(accept(preview(4), receive)).replace("'", "''")
    runtime_query(query, manifest, select_context_inputs(manifest['catalogId'], receive['context']) + f"""
        DO $probe$ BEGIN BEGIN
          PERFORM security.record_gas_billing('{manifest['catalogId']}','{wire}',
            '2021-01-15T08:00:00Z','{receive['context']['contextId']}','{json.dumps(stale)}');
          RAISE EXCEPTION 'superseded_fixing_recorded';
        EXCEPTION WHEN raise_exception THEN
          IF SQLERRM<>'gas_billing_context' THEN RAISE;END IF;
        END;END$probe$;""")
    fix = accept(preview(), second, previous=original['determinationId'], reason='index_fixing_correction', due='2021-03-10T00:00:00Z')
    c.call('gas/billing/determine', {**fix, 'operationId': str(uuid4()), 'fixingObservationId': DAILY_FIXING}, expected=409)
    for wrong in ('quantity_correction', 'late_actuals', 'allocation_correction'):
        assert c.call('gas/billing/determine', {**fix, 'operationId': str(uuid4()), 'reason': wrong}, expected=422)['code'] == 'gas_billing_correction'
    c.call('gas/billing/determine', {**fix, 'operationId': str(uuid4()), 'reason': None, 'correctionDueAt': None}, expected=422)
    refixed = c.call('gas/billing/determine', fix)
    assert (refixed['amount'], refixed['contribution'], refixed['previousId']) == ('-329.28', '-0.98', original['determinationId'])
    assert c.call('gas/billing/determine', fix) == refixed and c.call('gas/billing/determine', request_one) == original

    # A corrected final quantity on the same fixing takes a quantity cause, never the fixing cause.
    final('2021-01-13', '100', allocation['revisionId'])
    third = c.call('gas/billing/preview', preview())
    assert (third['amount'], third['contribution'], third['previousId']) == ('-336', '-6.72', refixed['determinationId']), third
    requantify = accept(preview(), third, previous=refixed['determinationId'], reason='quantity_correction', due='2021-03-11T00:00:00Z')
    assert c.call('gas/billing/determine', {**requantify, 'operationId': str(uuid4()), 'reason': 'index_fixing_correction'}, expected=422)['code'] == 'gas_billing_correction'
    last = c.call('gas/billing/determine', requantify)
    assert (last['amount'], last['contribution']) == ('-336', '-6.72')

    rows = query(f"SELECT string_agg(ordinal||':'||trim_scale(rounded_total)||':'||trim_scale(contribution)||':'||coalesce(reason,'original')||':'||fixing_observation_id,',' ORDER BY ordinal) "
                 f"FROM trading.gas_cash_determinations WHERE trade_id='{trade}' AND cash_slot=1;")
    assert rows == (f"1:-328.3:-328.3:original:{DAILY_FIXING},2:-329.28:-0.98:index_fixing_correction:{corrected},"
                    f"3:-336:-6.72:quantity_correction:{corrected}"), rows

    # Scheduled discovery bills no indexed payment: under either fixed profile every day of this trade is reported as
    # terms the scope does not bill, and the scope cannot select the indexed profile.
    operating = dict(convention='gas_processing_instant_utc_v1', businessDate='2021-01-15', evaluationAt='2021-01-15T08:00:00Z',
                     knowledgeMode='current', retained=None)
    for profile in ('gas_final_daily_fixed_cash_v1', 'gas_final_daily_entry_cash_v1'):
        work = dict(context=operating, bookIds=[body['bookId']], **{'from': '2021-01-13', 'to': '2021-01-15'},
                    selectionPolicy='final_allocated_billing_v1', profile=profile, after=None, limit=8)
        items = []
        for _ in range(64):
            page = c.call('gas/billing/worklist', work)
            items.extend(row for row in page['items'] if row['selection']['tradeId'] == trade)
            if not page['hasMore']:
                break
            work.update(after=page['next'], context={**operating, 'knowledgeMode': 'retained', 'retained': page['context']})
        assert len(items) == 8 and {(row['state'], row['amount'], row['sourceId']) for row in items} == {('unsupported_terms', None, None)}, items
        assert {row['reason'] for row in items} <= {'gas_billing_profile', 'gas_billing_input'}, items
    c.call('gas/billing/worklist', {**work, 'context': operating, 'after': None, 'profile': INDEXED}, expected=422)
    selection = {key: value for key, value in preview().items() if key != 'context'}
    assert c.call('gas/billing/operating-preview', dict(context=operating, selection=selection), expected=422)['code'] == 'gas_billing_profile'

    # A correction is recorded when its input changed even though its cash did not: a zero final quantity billed at 3.10,
    # then corrected twice by the publisher. The head is the last correction, and its contribution is zero.
    final('2021-01-14', '0')
    nothing = c.call('gas/billing/preview', preview(day='2021-01-14'))
    assert (nothing['amount'], nothing['fixing']['observationId'], nothing['fixing']['value']) == ('0', NEXT_DAY_FIXING, '3.1'), nothing
    zero = c.call('gas/billing/determine', accept(preview(day='2021-01-14'), nothing))
    once = publisher.call('corrections', dict(snapshotId=published['frame']['id'], observationId=NEXT_DAY_FIXING, value='3.11'))
    twice = publisher.call('corrections', dict(snapshotId=once['frame']['id'], observationId=once['observationId'], value='3.12'))
    again = c.call('gas/billing/preview', preview(day='2021-01-14'))
    assert (again['amount'], again['contribution'], again['alreadyDetermined']) == ('0', '0', False), again
    assert (again['fixing']['observationId'], again['fixing']['correctsId'], again['fixing']['value']) == (twice['observationId'], once['observationId'], '3.12')
    unchanged = c.call('gas/billing/determine', accept(preview(day='2021-01-14'), again, previous=zero['determinationId'],
                                                        reason='index_fixing_correction', due='2021-03-12T00:00:00Z'))
    assert (unchanged['amount'], unchanged['contribution'], unchanged['previousId']) == ('0', '0', zero['determinationId'])
    assert unchanged['determinationId'] != zero['determinationId']

    # A monthly bill expects exactly the payment days billing can determine: four indexed payments over three gas days,
    # of which three payment days are determined.
    (billing_month,) = c.call('gas/bills/read', dict(context=after, tradeId=trade))['months']
    assert (billing_month['state'], len(billing_month['missing'])) == ('incomplete', 9), billing_month
    assert not {(1, '2021-01-13'), (2, '2021-01-13'), (1, '2021-01-14')} & {(m['cashSlot'], m['day']) for m in billing_month['missing']}

    # The earlier knowledge view keeps the original fixing, quantity and single determination.
    earlier = c.call('gas/billing/read', dict(context=retained, tradeId=trade))
    assert [row['id'] for row in earlier['determinations']] == [original['determinationId']]
    seen = c.call('gas/billing/preview', preview(view=retained))
    assert (seen['amount'], seen['fixing']['observationId'], seen['alreadyDetermined']) == ('-328.3', DAILY_FIXING, True), seen
    print('PASS indexed gas billing: final fixing plus spread, share and band on a monthly index, reviewed fixing and quantity corrections '
          'with their own causes, immutable originals and the earlier knowledge view', flush=True)


def point_checks(c, context, trade):
    """A payment on point publications bills from the fixing its location's dated periods retained for the day.

    The in-term fixture receives 100 a day and pays the location's daily index, fixed at 3.19 for 4 November 2026,
    with no spread, on the tenth of the next month. December, on the location's monthly period, has not started.
    """
    read = c.call('gas/billing/read', dict(context=context, tradeId=trade))
    claims = {row['day']: row for row in read['claims']}
    claim = claims['2026-11-04']
    assert claim['supported'] and claim['completed'] and claim['profiles'] == [INDEXED] and claim['refusal'] is None, claim
    assert claim['index'] == dict(code=None, convention='point_publications_v1', spread='0'), claim
    assert claims['2026-12-15']['supported'] and not claims['2026-12-15']['completed']
    obligation = c.call('gas/quantities/read', dict(context=context, tradeId=trade, selectionPolicy='operational_projection_v1'))['rows'][0]['obligationId']
    c.call('gas/quantities/record', dict(context=context, tradeId=trade, expectedRevision=read['expectedRevision'], operationId=str(uuid4()),
        obligationId=obligation, day='2026-11-04', quantity='97', kind='allocated', status='final', expectedPreviousId=None,
        evidence='Synthetic final allocation'))
    preview = dict(context=context, tradeId=trade, expectedRevision=read['expectedRevision'], cashSlot=claim['cashSlot'], day='2026-11-04',
                   selectionPolicy='final_allocated_billing_v1', profile=INDEXED, decimalPlaces=2, roundingPolicy='currency_minor_units_to_even_v1')
    value = c.call('gas/billing/preview', preview)
    assert (value['amount'], value['contribution'], value['previousId']) == ('-309.43', '-309.43', None), value
    assert (value['fixing']['value'], value['fixing']['spread'], value['fixing']['unitPrice'], value['fixing']['index']) == ('3.19', '0', '3.19', 'synthetic_in_term_index'), value
    assert value['contractualDueAt'][:10] == '2026-12-10'
    request = dict(preview=preview, operationId=str(uuid4()), previewHash=value['previewHash'], sourceId=value['sourceId'],
                   fixingObservationId=value['fixing']['observationId'], expectedPreviousId=None, correctionDueAt=None, reason=None,
                   evidence='Synthetic point publication billing review')
    determined = c.call('gas/billing/determine', request)
    assert (determined['amount'], determined['contribution']) == ('-309.43', '-309.43')
    assert c.call('gas/billing/determine', request) == determined
    assert c.call('gas/billing/preview', preview)['alreadyDetermined']
    print('PASS point publication billing: the location period\'s retained daily fixing bills an ended day, with retry and replay', flush=True)
