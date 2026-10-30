"""Financial cash for retained dated observations; not futures margining."""
from copy import deepcopy
from fractions import Fraction
from uuid import uuid4

from preview_fixture import CAT, identity


def run(c, context, body, query):
    frames = c.call('workspace')['frames']
    def at(prefix):
        return {**context, 'snapshotId': next(f['id'] for f in frames if f['at'].startswith(prefix))}
    close = at('2021-02-01T08:00:00')
    partial = at('2021-01-15T08:00:00')
    clone = deepcopy(body)
    clone['operationId'] = str(uuid4())
    trade = c.call('swaps/book', clone)['tradeId']
    currency = c.call('swaps/editor', dict(context=close, tradeId=trade, revision=None))['valuation']['currency']
    def preview(selected, slot=1):
        return dict(context=selected, tradeId=trade, expectedRevision=1, membershipOrdinal=slot,
                    paymentOrdinal=0, currency=currency, decimalPlaces=2, roundingPolicy='currency_minor_units_to_even_v1')
    def command(p, value, version, due=None):
        return dict(preview=p, operationId=str(uuid4()), expectedVersion=version, previewHash=value['previewHash'],
                    duePolicy='swap_due_nominal_v1', recognitionPolicy='swap_completed_period_trading_pnl_v1',
                    correctionPolicy='swap_explicit_correction_due_v1', correctionDueAt=due)
    def inspect(selected):
        return c.call('swaps/settlement/inspect', dict(context=selected, tradeId=trade, revision=None,
            duePolicy='swap_due_nominal_v1', recognitionPolicy='swap_completed_period_trading_pnl_v1'))
    # Before all contractual anchors are due the same source cannot determine cash.
    early = c.call('swaps/settlement/preview', preview(partial), expected=422)
    assert early['code'] == 'settlement_not_final'
    p = preview(close)
    value = c.call('swaps/settlement/preview', p)
    assert Fraction(value['calculation']['amount']) == 816, value
    prices = value['calculation']['prices']
    assert len(prices) == 31 and all(x['contractObservation'] is not None and len(x['lineage']) == 1 for x in prices)
    assert prices[0]['contractObservation']['contract']['key']['id'] == identity('5', 6)
    request = command(p, value, 0)
    accepted = c.call('swaps/settlement/determine', request)
    assert accepted['total'] == '816'
    assert c.call('swaps/settlement/determine', request) == accepted
    assert query(f"SELECT count(*) FROM settlement_data.swap_determinations WHERE trade_id='{trade}';").strip() == '1'
    # Zero fixed cash is still a separate gross occurrence and completion source.
    fixed_p = preview(close, 0)
    fixed = c.call('swaps/settlement/preview', fixed_p)
    c.call('swaps/settlement/determine', command(fixed_p, fixed, 1))
    payment = dict(context=close, tradeId=trade, operationId=str(uuid4()), expectedRevision=1, expectedVersion=2,
        determinationId=accepted['determinationId'], amount='816', currency=currency,
        effectiveAt='2021-02-01T08:00:00Z', reference='Synthetic dated-observation payment', reversesId=None)
    paid_result = c.call('swaps/settlement/payment', payment)
    assert c.call('swaps/settlement/payment', payment) == paid_result
    paid = inspect(close)
    assert paid['valuation']['remainingValue'] == '0' and paid['valuation']['netCash'] == '816'
    assert paid['valuation']['realized'] == '816'
    pinned = {**close, 'knowledgeMode': 'retained', 'contextId': paid['context']['contextId']}
    stale = c.call('swaps/settlement/preview', p)
    correction = str(uuid4())
    query(f"""INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,
        reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,
        role,publication_state,observed_at,received_at,value,corrects_id)
        SELECT catalog_id,'{correction}',definition_revision_id,series_id,reference_catalog_id,
        reference_revision_id,source_code,source_event_id,'{correction}',role,publication_state,
        observed_at,clock_timestamp(),value+1,id FROM market_data.observations
        WHERE catalog_id='{CAT}' AND id='{identity('7', 8700003)}';""")
    assert inspect(close)['valuation']['remainingValue'] == '0', 'Published correction must not rewrite determined cash'
    stale_result = c.call('swaps/settlement/determine', command(p, stale, 3, '2021-02-02T00:00:00Z'), expected=409)
    assert stale_result['code'] == 'settlement_stale_preview'
    revised = c.call('swaps/settlement/preview', p)
    assert revised['calculation']['contractHash'] == value['calculation']['contractHash']
    assert revised['calculation']['inputHash'] != value['calculation']['inputHash']
    assert revised['previousTotal'] == '816' and revised['proposedContribution'] == '1'
    c.call('swaps/settlement/determine', command(p, revised, 3), expected=422)
    adjustment = command(p, revised, 3, '2021-02-02T00:00:00Z')
    corrected = c.call('swaps/settlement/determine', adjustment)
    assert corrected['total'] == '817' and corrected['adjustment'] == '1'
    assert c.call('swaps/settlement/determine', adjustment) == corrected
    replay = inspect(pinned)
    assert replay['valuation'] == paid['valuation'] and replay['settlement'] == paid['settlement']
    assert query(f"SELECT count(*) FROM settlement_data.swap_occurrences WHERE trade_id='{trade}' AND membership_ordinal=1;").strip() == '1'
    # Final carry is an explicit different price source; earlier daily samples
    # keep their prices and each of the last four observations carries final 50.
    paid_trade = trade
    carry = deepcopy(body)
    carry['operationId'] = str(uuid4())
    source = carry['scheduled']['second']['periods'][0]['floating']
    source['curve'] = 'future_carry'
    source['contractObservations']['expiryPolicy'] = 'carry_final_settlement'
    source['contractObservations']['selection'] = dict(mode='exact', contract=prices[0]['contractObservation']['contract']['key'], promptOffset=None, roll=None, boundaries=None)
    trade = c.call('swaps/book', carry)['tradeId']
    carry_p = preview(close)
    carry_value = c.call('swaps/settlement/preview', carry_p)
    assert Fraction(carry_value['calculation']['amount']) == 859
    carry_prices = carry_value['calculation']['prices']
    assert len({x['lineage'][0]['id'] for x in carry_prices[-4:]}) == 1
    carry_request = command(carry_p, carry_value, 0)
    carry_result = c.call('swaps/settlement/determine', carry_request)
    assert carry_result['total'] == '859' and c.call('swaps/settlement/determine', carry_request) == carry_result
    print('PASS dated-observation cash: finality, exact source evidence, gross determination, retry, payment, correction, stale refusal and retained replay')
    return dict(paidTradeId=paid_trade, carryTradeId=trade, paidContext=pinned, currentContext=close,
                correctedBase='1', carryBase='859')


def check_unavailable_redetermination(c, settlement):
    """Posted cash survives missing revised prices; a new determination does not."""
    c.call('profile', {'profile': 'trader'})
    try:
        for trade, expected in ((settlement['paidTradeId'], settlement['correctedBase']),
                                (settlement['carryTradeId'], settlement['carryBase'])):
            selected = settlement['currentContext']
            held = c.call('swaps/settlement/inspect', dict(context=selected, tradeId=trade, revision=None,
                duePolicy='swap_due_nominal_v1', recognitionPolicy='swap_completed_period_trading_pnl_v1'))
            assert held['valuation']['remainingValue'] == expected, held
            missing = c.call('swaps/settlement/preview', dict(context=selected, tradeId=trade,
                expectedRevision=1, membershipOrdinal=1, paymentOrdinal=0, currency=held['currency'],
                decimalPlaces=2, roundingPolicy='currency_minor_units_to_even_v1'), expected=422)
            assert missing['code'] == 'missing_observation', missing
    finally:
        c.call('profile', {'profile': 'risk'})
