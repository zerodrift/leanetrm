"""A monthly gas bill and its adjustment note over synthetic daily determinations, through the native operations.

The trade receives 100 a day on 1 and 2 January 2021, pays a fixed 3.00 and receives a fee of 0.10. Expected totals
are summed by hand from the determinations: see the worked example of the monthly bill design.
"""
from copy import deepcopy
import json
from uuid import uuid4
from preview_client import PreviewClient
from check_physical_operations import january_delivery_frame

POLICY = 'gas_trade_month_gross_v1'
MONTH = '2021-01-01'
PAYMENT, FEE = 1, 2


def bill_checks(c, query, manifest, terms, before, after, url):
    from check_booked_side_programs import runtime_query
    from native_read_transport import select_context_inputs
    body = deepcopy(terms); body['operationId'] = str(uuid4())
    trade = c.call('gas/composition/book', body)['tradeId']
    valued = c.call('gas/composition/editor', dict(context=before, tradeId=trade))['valuation']['presentValue']
    obligation = c.call('gas/quantities/read', dict(context=after, tradeId=trade, selectionPolicy='operational_projection_v1'))['rows'][0]['obligationId']

    def final(day, value, previous=None):
        return c.call('gas/quantities/record', dict(context=after, tradeId=trade, expectedRevision=1, operationId=str(uuid4()),
            obligationId=obligation, day=day, quantity=value, kind='allocated', status='final', expectedPreviousId=previous,
            evidence='Synthetic final allocation'))

    def determine(slot, day, reason=None):
        p = dict(context=after, tradeId=trade, expectedRevision=1, cashSlot=slot, day=day, selectionPolicy='final_allocated_billing_v1',
                 profile='gas_final_daily_fixed_cash_v1', decimalPlaces=2, roundingPolicy='currency_minor_units_to_even_v1')
        value = c.call('gas/billing/preview', p)
        return c.call('gas/billing/determine', dict(preview=p, operationId=str(uuid4()), previewHash=value['previewHash'], sourceId=value['sourceId'],
            expectedPreviousId=value['previousId'], correctionDueAt='2021-03-02T00:00:00Z' if reason else None, reason=reason,
            evidence='Synthetic billing review'))

    def read(view=after):
        return c.call('gas/bills/read', dict(context=view, tradeId=trade))

    def month(view=after):
        (only,) = read(view)['months']
        assert only['month'] == MONTH, only
        return only

    def reviewed(view=after):
        return dict(context=view, tradeId=trade, expectedRevision=1, month=MONTH, scopePolicy=POLICY)

    def accept(value, evidence='Synthetic monthly bill review', **changes):
        return {**dict(preview=reviewed(), operationId=str(uuid4()), previewHash=value['previewHash'], contentDigest=value['contentDigest'],
                       expectedPreviousId=value['previousId'], evidence=evidence), **changes}

    def lines(document):
        return [(line['cashSlot'], line['day'], line['amount']) for line in document['lines']]

    scope = read()
    assert (scope['scopePolicy'], scope['amountPolicy']) == (POLICY, 'sum_of_daily_determinations_v1')
    assert [(p['cashSlot'], p['covered'], p['reason']) for p in scope['payments']] == [(PAYMENT, True, None), (FEE, True, None)], scope['payments']
    every_day = [dict(cashSlot=slot, day=day) for slot in (PAYMENT, FEE) for day in ('2021-01-01', '2021-01-02')]
    assert (month()['state'], month()['missing'], month()['documents']) == ('incomplete', every_day, [])

    first = final('2021-01-01', '100'); other_day = final('2021-01-02', '100')
    determine(PAYMENT, '2021-01-01'); determine(FEE, '2021-01-01')
    final('2021-01-01', '98', first['revisionId'])
    assert determine(PAYMENT, '2021-01-01', 'quantity_correction')['contribution'] == '6'
    assert (month()['state'], month()['missing']) == ('incomplete', [dict(cashSlot=PAYMENT, day='2021-01-02'), dict(cashSlot=FEE, day='2021-01-02')])
    waiting = c.call('gas/bills/preview', reviewed())
    assert (waiting['state'], waiting['document'], waiting['previewHash'], waiting['contentDigest']) == ('incomplete', None, None, None), waiting
    forged = dict(preview=reviewed(), operationId=str(uuid4()), previewHash='0' * 64, contentDigest='0' * 64, expectedPreviousId=None, evidence='Synthetic incomplete month')
    assert c.call('gas/bills/issue', forged, expected=422)['code'] == 'gas_bill_incomplete'

    determine(PAYMENT, '2021-01-02'); determine(FEE, '2021-01-02')
    before_bill = read()
    assert before_bill['months'][0]['state'] == 'ready'
    retained = {**after, 'knowledgeMode': 'retained', 'contextId': before_bill['context']['contextId']}
    value = c.call('gas/bills/preview', reviewed())
    document = value['document']
    assert (value['state'], value['previousId'], document['kind']) == ('ready', None, 'bill'), value
    assert lines(document) == [(PAYMENT, '2021-01-01', '-300'), (PAYMENT, '2021-01-01', '6'), (PAYMENT, '2021-01-02', '-300'),
                               (FEE, '2021-01-01', '10'), (FEE, '2021-01-02', '10')], document
    assert (document['payTotal'], document['receiveTotal'], document['currency']) == ('-594', '20', 'usd'), document
    assert 'total' not in document and 'netTotal' not in document

    # The writer assembles the document itself: a forged total, or a review of other lines, records nothing.
    honest = dict(payTotal='-594', receiveTotal='20')
    for calculation, digest, refusal in [({**honest, 'payTotal': '-593.99'}, value['contentDigest'], 'gas_bill_amount'),
                                         ({**honest, 'receiveTotal': '20.01'}, value['contentDigest'], 'gas_bill_amount'),
                                         (honest, '0' * 64, 'gas_bill_conflict')]:
        wire = json.dumps(accept(value, contentDigest=digest)).replace("'", "''")
        runtime_query(query, manifest, select_context_inputs(manifest['catalogId'], value['context']) + f"""
            DO $probe$ BEGIN BEGIN
              PERFORM security.issue_gas_bill('{manifest['catalogId']}','{wire}','2021-01-15T08:00:00Z','{value['context']['contextId']}','{json.dumps(calculation)}');
              RAISE EXCEPTION 'forged_gas_bill_issued';
            EXCEPTION WHEN raise_exception THEN
              IF SQLERRM<>'{refusal}' THEN RAISE;END IF;
            END;END$probe$;""")
    assert month()['documents'] == []

    assert c.call('gas/bills/issue', accept(value, previewHash='0' * 64), expected=409)['code'] == 'gas_bill_stale_preview'
    facts = f"SELECT md5(string_agg(d::text,',' ORDER BY d.cash_slot,d.day,d.ordinal)) FROM trading.gas_cash_determinations d WHERE d.trade_id='{trade}';"
    recorded = query(facts)
    request = accept(value)
    bill = c.call('gas/bills/issue', request)
    assert query(facts) == recorded
    assert (bill['ordinal'], bill['previousId'], bill['payTotal'], bill['receiveTotal']) == (1, None, '-594', '20'), bill
    assert c.call('gas/bills/issue', request) == bill
    assert c.call('gas/bills/status', dict(operationId=request['operationId'])) == bill
    assert c.call('gas/bills/issue', {**request, 'operationId': str(uuid4())})['billId'] == bill['billId']
    other = PreviewClient(url); other.bootstrap(); other.call('profile', dict(profile='trader'))
    elsewhere = {**reviewed(), 'context': {**after, 'snapshotId': january_delivery_frame(other.call('workspace'))}}
    assert other.call('gas/bills/issue', {**request, 'operationId': str(uuid4()), 'preview': elsewhere})['billId'] == bill['billId']
    c.call('gas/bills/issue', {**request, 'operationId': str(uuid4()), 'evidence': 'A different review'}, expected=409)

    issued = month()
    assert (issued['state'], issued['missing'], len(issued['documents'])) == ('issued', [], 1)
    (kept,) = issued['documents']
    assert (kept['id'], kept['kind'], kept['ordinal'], kept['payTotal'], kept['receiveTotal']) == (bill['billId'], 'bill', 1, '-594', '20')
    assert lines(kept) == lines(document)
    nothing = c.call('gas/bills/preview', reviewed())
    assert (nothing['state'], nothing['document'], nothing['previousId']) == ('issued', None, bill['billId'])

    # A determination made after the bill goes on a linked adjustment note; the bill itself does not change.
    final('2021-01-02', '98', other_day['revisionId'])
    assert determine(FEE, '2021-01-02', 'quantity_correction')['contribution'] == '-0.2'
    assert month()['state'] == 'adjustment_ready'
    change = c.call('gas/bills/preview', reviewed())
    assert (change['state'], change['previousId'], change['document']['kind']) == ('adjustment_ready', bill['billId'], 'adjustment_note')
    assert lines(change['document']) == [(FEE, '2021-01-02', '-0.2')]
    assert (change['document']['payTotal'], change['document']['receiveTotal']) == ('0', '-0.2')
    # A review of other lines is never answered with an existing document: naming no predecessor would select the
    # bill, whose lines are not the reviewed ones.
    assert c.call('gas/bills/issue', accept(change, expectedPreviousId=None), expected=409)['code'] == 'gas_bill_conflict'
    # A determination recorded between review and acceptance refuses the stale review.
    assert determine(PAYMENT, '2021-01-02', 'quantity_correction')['contribution'] == '6'
    assert c.call('gas/bills/issue', accept(change, evidence='Synthetic adjustment review'), expected=409)['code'] == 'gas_bill_stale_preview'
    assert month()['state'] == 'adjustment_ready' and len(month()['documents']) == 1
    change = c.call('gas/bills/preview', reviewed())
    assert lines(change['document']) == [(PAYMENT, '2021-01-02', '6'), (FEE, '2021-01-02', '-0.2')]
    note = c.call('gas/bills/issue', accept(change, evidence='Synthetic adjustment review'))
    assert (note['ordinal'], note['previousId'], note['payTotal'], note['receiveTotal']) == (2, bill['billId'], '6', '-0.2'), note
    # With every determination on a document, the writer has nothing to issue.
    empty = json.dumps(accept(change, expectedPreviousId=note['billId'], evidence='Synthetic empty note')).replace("'", "''")
    current = c.call('gas/bills/preview', reviewed())
    runtime_query(query, manifest, select_context_inputs(manifest['catalogId'], current['context']) + f"""
        DO $probe$ BEGIN BEGIN
          PERFORM security.issue_gas_bill('{manifest['catalogId']}','{empty}','2021-01-15T08:00:00Z','{current['context']['contextId']}','{json.dumps(dict(payTotal='0', receiveTotal='0'))}');
          RAISE EXCEPTION 'empty_gas_bill_issued';
        EXCEPTION WHEN raise_exception THEN
          IF SQLERRM<>'gas_bill_empty' THEN RAISE;END IF;
        END;END$probe$;""")
    final_state = month()
    assert final_state['state'] == 'issued' and [d['kind'] for d in final_state['documents']] == ['bill', 'adjustment_note']
    assert lines(final_state['documents'][0]) == lines(document)

    # Each determination is on exactly one document, and each document's totals are the sums of its lines by direction.
    assert query(f"SELECT count(*)||':'||count(DISTINCT l.determination_id)||':'||(SELECT count(*) FROM trading.gas_cash_determinations d WHERE d.trade_id='{trade}') "
                 f"FROM trading.gas_bill_lines l WHERE l.trade_id='{trade}';") == '7:7:7'
    assert query("SELECT bool_and(b.pay_total=coalesce((SELECT sum(l.amount) FROM trading.gas_bill_lines l WHERE l.bill_id=b.id AND l.cash_slot=1),0) "
                 "AND b.receive_total=coalesce((SELECT sum(l.amount) FROM trading.gas_bill_lines l WHERE l.bill_id=b.id AND l.cash_slot=2),0))::text "
                 f"FROM trading.gas_bills b WHERE b.trade_id='{trade}';") == 'true'
    query("DO $$BEGIN UPDATE trading.gas_bills SET pay_total=42;RAISE EXCEPTION 'mutable bill';EXCEPTION WHEN SQLSTATE '55000' THEN NULL;END$$;")
    # The schema itself keeps a determination off a second document.
    query(f"""SET ROLE openetrm_security_owner;DO $$BEGIN
      INSERT INTO trading.gas_bill_lines SELECT l.catalog_id,'{note['billId']}',l.trade_id,l.month,l.determination_id,l.cash_slot,l.day,l.amount
       FROM trading.gas_bill_lines l WHERE l.bill_id='{bill['billId']}' LIMIT 1;
      RAISE EXCEPTION 'determination on two documents';EXCEPTION WHEN unique_violation THEN NULL;END$$;RESET ROLE;""")

    # A working view whose business time precedes the month's documents cannot issue for it, and says why.
    behind = c.call('gas/bills/read', dict(context=before, tradeId=trade))['months']
    assert [(m['month'], m['state'], m['refusal']) for m in behind] == [(MONTH, 'refused', 'gas_bill_later_document')], behind
    assert c.call('gas/bills/preview', {**reviewed(), 'context': before}, expected=422)['code'] == 'gas_bill_later_document'
    viewer = PreviewClient(url); viewer.bootstrap(); viewer.call('profile', dict(profile='viewer'))
    watching = {**after, 'snapshotId': january_delivery_frame(viewer.call('workspace'))}
    viewer.call('gas/bills/issue', {**accept(change), 'preview': {**reviewed(), 'context': watching}}, expected=403)

    # The view captured before the bill still sees a ready month, no document and nothing documented.
    earlier = month(retained)
    assert (earlier['state'], earlier['documents']) == ('ready', [])
    assert c.call('gas/bills/preview', reviewed(retained))['document']['payTotal'] == '-594'
    assert c.call('gas/bills/issue', {**accept(change), 'preview': reviewed(retained)}, expected=422)['code'] == 'historical_read_only'
    assert c.call('gas/composition/editor', dict(context=before, tradeId=trade))['valuation']['presentValue'] == valued

    # A trade whose payments an exercise right governs has no billable payment day, and says why.
    governed = deepcopy(terms)
    governed.update(operationId=str(uuid4()), exerciseRights=[dict(entries=['receipt', 'payment'], period='delivery_day', holder='us', settlement='physical', leadHours=6)])
    ruled = c.call('gas/composition/book', governed)['tradeId']
    unbillable = c.call('gas/bills/read', dict(context=after, tradeId=ruled))
    assert [(p['covered'], p['reason']) for p in unbillable['payments']] == [(False, 'rights'), (False, 'rights')] and unbillable['months'] == [], unbillable
    assert c.call('gas/bills/preview', {**reviewed(), 'tradeId': ruled}, expected=422)['code'] == 'gas_bill_scope'
    print('PASS gas monthly bill: incomplete month refused with its missing days, bill over every determination with separate totals, '
          'forged and stale reviews refused, replay and another actor, linked adjustment note, immutable documents and earlier views', flush=True)
