"""Independent billing admission and cash oracles over synthetic retained terms."""
from copy import deepcopy
import json
from uuid import uuid4


def entry_checks(c, query, manifest, terms, after, *, dotnet=None):
    body = deepcopy(terms)
    body['operationId'] = str(uuid4())
    body['physicalSides'][0]['quantityPolicy'] = 'delivery_schedule_v1'
    cash = {**body['cashSides'][0], 'price': '2.25'}
    body['cashSides'] = [
        {**cash, 'code': 'shared', 'share': '0.4'},
        {**cash, 'code': 'band', 'part': dict(kind='band', lower='30', upper='70')},
        {**cash, 'code': 'shortfall', 'part': dict(kind='shortfall', lower='100', upper=None)},
        {**cash, 'code': 'partial', 'deliveryFrom': '2021-01-02', 'deliveryTo': '2021-01-03'}]
    saved = c.call('gas/composition/book', body)
    trade = saved['tradeId']
    rows = c.call('gas/quantities/read', dict(context=after, tradeId=trade, selectionPolicy='operational_projection_v1'))
    obligation = rows['rows'][0]['obligationId']
    def quantity(day, value, previous=None):
        return c.call('gas/quantities/record', dict(context=after, tradeId=trade, expectedRevision=1,
            operationId=str(uuid4()), obligationId=obligation, day=day, quantity=value,
            kind='allocated', status='final', expectedPreviousId=previous, evidence='Synthetic entry final'))
    first_source = quantity('2021-01-01', '80')
    quantity('2021-01-02', '80')
    from fractions import Fraction
    valued = c.call('gas/composition/editor', dict(context=after, tradeId=trade))['valuation']['sides']
    assert [Fraction(row['presentValue']) for row in valued[1:]] == [Fraction('-144'), Fraction('-180'), Fraction('-90'), Fraction('-180')]
    preview = dict(context=after, tradeId=trade, expectedRevision=1, cashSlot=1,
        day='2021-01-01', selectionPolicy='final_allocated_billing_v1',
        profile='gas_final_daily_entry_cash_v1', decimalPlaces=2, roundingPolicy='currency_minor_units_to_even_v1')
    missing = c.call('gas/billing/preview', preview)['reconciliation']
    assert missing['status'] == 'incomplete' and missing['scheduled'] is None and missing['finalLessScheduled'] is None
    c.call('gas/quantities/record', dict(context=after, tradeId=trade, expectedRevision=1,
        operationId=str(uuid4()), obligationId=obligation, day='2021-01-01', quantity='100',
        kind='scheduled', status='provisional', expectedPreviousId=None, evidence='Synthetic scheduled quantity'))
    reconciled = c.call('gas/billing/preview', preview)['reconciliation']
    assert reconciled['status'] == 'difference' and reconciled['finalLessScheduled'] == '-20'
    assert reconciled['scheduledLessContract'] == '0' and not reconciled['automaticOriginalEligible']
    def determine(p, *, due=None, reason=None):
        value = c.call('gas/billing/preview', p)
        request = dict(preview=p, operationId=str(uuid4()), previewHash=value['previewHash'],
            sourceId=value['sourceId'], expectedPreviousId=value['previousId'], correctionDueAt=due,
            reason=reason, evidence='Synthetic entry billing review')
        return c.call('gas/billing/determine', request), request
    for slot, day, amount in [(1, '2021-01-01', '-72'), (2, '2021-01-01', '-90'),
                               (3, '2021-01-01', '-45'), (4, '2021-01-02', '-180')]:
        p = {**preview, 'cashSlot': slot, 'day': day}
        value = c.call('gas/billing/preview', p)
        assert value['amount'] == amount, value
        if slot == 1:
            from check_booked_side_programs import runtime_query
            from native_read_transport import select_context_inputs
            bad = dict(preview=p, operationId=str(uuid4()), previewHash=value['previewHash'],
                sourceId=value['sourceId'], expectedPreviousId=None, correctionDueAt=None,
                reason=None, evidence='Synthetic forged entry amount')
            wire = json.dumps(bad).replace("'", "''")
            for calc in [dict(sourceId=value['sourceId'], amount='-71', numerator='-71', denominator='1'),
                         dict(sourceId=value['sourceId'], amount='-71', numerator='-72', denominator='1')]:
                runtime_query(query, manifest, select_context_inputs(manifest['catalogId'], value['context']) + f"""
                    DO $probe$ BEGIN BEGIN
                      PERFORM security.record_gas_billing('{manifest['catalogId']}','{wire}',
                        '2021-01-15T08:00:00Z','{value['context']['contextId']}','{json.dumps(calc)}');
                      RAISE EXCEPTION 'forged_entry_billing_accepted';
                    EXCEPTION WHEN raise_exception THEN
                      IF SQLERRM<>'gas_billing_amount' THEN RAISE;END IF;
                    END;END$probe$;""")
        result, request = determine(p)
        assert result['amount'] == amount and result['contribution'] == amount
        if slot == 1:
            original, original_request = result, request
    c.call('gas/billing/preview', {**preview, 'cashSlot': 4}, expected=422)
    c.call('gas/billing/preview', {**preview, 'profile': 'gas_final_daily_fixed_cash_v1'}, expected=422)
    old_history = c.call('gas/billing/read', dict(context=after, tradeId=trade))
    quantity('2021-01-01', '60', first_source['revisionId'])
    corrected, command = determine(preview, due='2021-03-02T00:00:00Z', reason='quantity_correction')
    assert corrected['amount'] == '-54' and corrected['contribution'] == '18'
    assert c.call('gas/billing/determine', command) == corrected
    assert c.call('gas/billing/determine', {**original_request, 'operationId': str(uuid4())})['determinationId'] == original['determinationId']
    read = c.call('gas/billing/read', dict(context=after, tradeId=trade))
    assert [d['rounded_total'] for d in read['determinations'] if int(d['cash_slot']) == 1] == ['-72', '-54'], read['determinations']
    retained = {**after, 'knowledgeMode': 'retained', 'contextId': old_history['context']['contextId']}
    assert c.call('gas/billing/read', dict(context=retained, tradeId=trade))['determinations'] == old_history['determinations']
    assert all(r['day'] == '2021-01-02' for r in read['claims'] if r['cashSlot'] == 4)
    operating = dict(convention='gas_processing_instant_utc_v1', businessDate='2021-01-15',
        evaluationAt='2021-01-15T08:00:00Z', knowledgeMode='current', retained=None)
    selection = {k: v for k, v in preview.items() if k != 'context'}
    operational = c.call('gas/billing/operating-preview', dict(context=operating, selection=selection))
    assert operational['amount'] == '-54' and operational['alreadyDetermined']
    work = dict(context=operating, bookIds=[body['bookId']], **{'from': '2021-01-01', 'to': '2021-01-03'},
        selectionPolicy=preview['selectionPolicy'], profile=preview['profile'], after=None, limit=8)
    items = []
    for _ in range(64):
        page = c.call('gas/billing/worklist', work)
        assert len(page['items']) <= 8
        items.extend(page['items'])
        if not page['hasMore']:
            break
        work.update(after=page['next'], context={**operating, 'knowledgeMode': 'retained', 'retained': page['context']})
    else:
        raise AssertionError('The bounded billing worklist did not finish')
    own = [r for r in items if r['selection']['tradeId'] == trade]
    assert len(own) == 7 and len({(r['selection']['cashSlot'], r['selection']['day']) for r in own}) == 7, own
    assert next(r for r in own if r['selection']['cashSlot'] == 1 and r['selection']['day'] == '2021-01-01')['state'] == 'already_determined'
    # A completed membership must not be selected forever on a one-item page.
    single = {**work, 'after': None, 'limit': 1}
    one_at_a_time = []
    for _ in range(128):
        page = c.call('gas/billing/worklist', single)
        one_at_a_time.extend(page['items'])
        if not page['hasMore']:
            break
        assert page['next'] != single['after'], 'The worklist cursor did not progress'
        single.update(after=page['next'], context={**operating, 'knowledgeMode': 'retained', 'retained': page['context']})
    else:
        raise AssertionError('Single-item pages did not finish')
    assert [(r['occurrenceId'], r['state']) for r in one_at_a_time] == [(r['occurrenceId'], r['state']) for r in items]
    c.call('gas/billing/worklist', {**work, 'bookIds': [str(uuid4())], 'after': None}, expected=403)
    c.call('gas/billing/worklist', {**work, 'bookIds': []}, expected=422)
    lifecycle_checks(c, query, manifest, body, after, operating, dotnet)
    print('PASS entry billing: retained share/band/shortfall/own dates, exact cash, correction history and old-fact replay', flush=True)


def lifecycle_checks(c, query, manifest, terms, frame, operating, dotnet):
    body = deepcopy(terms)
    body['operationId'] = str(uuid4())
    saved = c.call('gas/composition/book', body)
    trade = saved['tradeId']
    rows = c.call('gas/quantities/read', dict(context=frame, tradeId=trade, selectionPolicy='operational_projection_v1'))
    obligation = rows['rows'][0]['obligationId']
    def quantity(kind, value, previous=None):
        return c.call('gas/quantities/record', dict(context=frame, tradeId=trade, expectedRevision=1,
            operationId=str(uuid4()), obligationId=obligation, day='2021-01-01', quantity=value,
            kind=kind, status='final' if kind == 'allocated' else 'provisional', expectedPreviousId=previous,
            evidence='Synthetic lifecycle reconciliation'))
    source = quantity('allocated', '100')
    policy = str(uuid4())
    c.call('lifecycle/policies/save', dict(operationId=str(uuid4()), policyId=policy, expectedRevision=0,
        action='gas.bill_daily', mode='automatic', fromAt='2021-01-01T00:00:00Z', untilAt='2022-01-01T00:00:00Z',
        implementation='explicit_lifecycle_action_policy_v1'))
    selection = dict(tradeId=trade, expectedRevision=1, cashSlot=1, day='2021-01-01',
        selectionPolicy='final_allocated_billing_v1', profile='gas_final_daily_entry_cash_v1', decimalPlaces=2,
        roundingPolicy='currency_minor_units_to_even_v1')
    pin = None
    def request(automatic, previous=None, correction_due=None, reason=None):
        nonlocal pin
        value = c.call('gas/billing/operating-preview', dict(context=operating, selection=selection))
        pin = value['context']
        return dict(operationId=str(uuid4()), context=dict(contextId=value['context']['contextId'],
            asOf=operating['evaluationAt'], businessDate=operating['businessDate']),
            target=dict(handler='gas_billing', scopeId=trade, bindingId=saved['instrumentId']),
            action='gas.bill_daily', policyId=policy, policyRevision=1, automatic=automatic, previewHash='0'*64,
            selection=dict(billing=selection, processingConvention=operating['convention'],
                scheduledPolicy='manual_scheduled_quantity_v1', sourceId=value['sourceId'], expectedPreviousId=previous,
                correctionDueAt=correction_due, reason=reason, evidence='Synthetic lifecycle billing'))
    automatic = request(True)
    review = c.call('lifecycle/preview', automatic)
    assert not review['offer']['eligible'] and review['offer']['reason'] == 'gas_billing_reconciliation'
    c.call('lifecycle/execute', {**automatic, 'previewHash': review['hash']}, expected=422)
    from check_booked_side_programs import runtime_query
    from native_read_transport import select_context_inputs
    def native_refusal(body, calculation, code):
        wire = json.dumps(body).replace("'", "''")
        calc = json.dumps(calculation).replace("'", "''")
        runtime_query(query, manifest, select_context_inputs(manifest['catalogId'], pin) + f"""
            DO $probe$ DECLARE deadline timestamptz;BEGIN
              deadline=lifecycle.capture_action_authority('{manifest['catalogId']}','gas.bill_daily','{wire}');
              BEGIN PERFORM lifecycle.execute_gas_billing('{manifest['catalogId']}','{wire}',deadline,'{calc}');
                RAISE EXCEPTION 'unsafe_scheduled_billing_accepted';
              EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'{code}' THEN RAISE;END IF;END;
            END$probe$;""")
    calc = dict(sourceId=source['revisionId'], amount='-90', numerator='-90', denominator='1', scheduledSourceId=None)
    native_refusal(automatic, calc, 'gas_billing_reconciliation')
    scheduled = quantity('scheduled', '100')
    automatic = request(True)
    review = c.call('lifecycle/preview', automatic)
    assert review['offer']['eligible'] and review['details']['amount'] == '-90'
    native_refusal(automatic, {**calc, 'amount': '-89', 'scheduledSourceId': scheduled['revisionId']}, 'gas_billing_amount')
    automatic['previewHash'] = review['hash']
    outcome = c.call('lifecycle/execute', automatic)
    assert outcome['amount'] == '-90'
    assert c.call('lifecycle/execute', automatic) == outcome
    # A direct native caller must retain the original action deadline through
    # commit as well as the producer's separate billing authority admission.
    delayed = {**automatic, 'operationId': str(uuid4()), 'automatic': False}
    wire = json.dumps(delayed).replace("'", "''")
    raw_calc = json.dumps({**calc, 'scheduledSourceId': scheduled['revisionId']}).replace("'", "''")
    runtime_query(query, manifest, select_context_inputs(manifest['catalogId'], pin) + f"""
        DO $probe$ BEGIN BEGIN
          PERFORM lifecycle.execute_gas_billing('{manifest['catalogId']}','{wire}',clock_timestamp()+interval '1 second','{raw_calc}');
          PERFORM pg_sleep(1.1);PERFORM security.check_operation_seals();
          RAISE EXCEPTION 'expired_billing_action_committed';
        EXCEPTION WHEN insufficient_privilege THEN IF SQLERRM<>'futures_margin_denied' THEN RAISE;END IF;END;END$probe$;""")
    quantity('allocated', '80', source['revisionId'])
    assert c.call('lifecycle/execute', automatic) == outcome
    acknowledged = c.call('lifecycle/execute', {**automatic, 'operationId': str(uuid4())})
    assert acknowledged['determinationId'] == outcome['determinationId']
    correction = request(True, outcome['determinationId'], '2021-03-02T00:00:00Z', 'quantity_correction')
    review = c.call('lifecycle/preview', correction)
    assert not review['offer']['eligible'] and review['offer']['reason'] == 'gas_billing_automatic_correction'
    c.call('lifecycle/execute', {**correction, 'previewHash': review['hash']}, expected=422)
    correction['automatic'] = False
    correction['previewHash'] = c.call('lifecycle/preview', correction)['hash']
    adjusted = c.call('lifecycle/execute', correction)
    assert adjusted['amount'] == '-72' and adjusted['contribution'] == '18'
    assert c.call('lifecycle/execute', correction) == adjusted
    schedule_checks(c, query, manifest, terms, frame, operating, policy, dotnet)
    print('PASS billing lifecycle: unmatched originals and automatic corrections refuse; reviewed corrections and accepted retries retain exact native receipts', flush=True)


def schedule_checks(c, query, manifest, terms, frame, operating, policy, dotnet):
    import hashlib
    from uuid import UUID
    from check_booked_side_programs import runtime_query
    from lifecycle_schedule_fixture import execute
    body = {**deepcopy(terms), 'operationId': str(uuid4())}
    saved = c.call('gas/composition/book', body)
    trade = saved['tradeId']
    rows = c.call('gas/quantities/read', dict(context=frame, tradeId=trade, selectionPolicy='operational_projection_v1'))
    obligation = rows['rows'][0]['obligationId']
    for kind, status in [('scheduled', 'provisional'), ('allocated', 'final')]:
        c.call('gas/quantities/record', dict(context=frame, tradeId=trade, expectedRevision=1, operationId=str(uuid4()),
            obligationId=obligation, day='2021-01-01', kind=kind, status=status, quantity='100', expectedPreviousId=None,
            evidence='Synthetic scheduled billing source'))
    source = dict(cashSlot=1, day='2021-01-01', processingConvention=operating['convention'],
        businessDate=operating['businessDate'], evaluationAt=operating['evaluationAt'],
        selectionPolicy='final_allocated_billing_v1', billingProfile='gas_final_daily_entry_cash_v1',
        decimalPlaces=2, roundingPolicy='currency_minor_units_to_even_v1', scheduledPolicy='manual_scheduled_quantity_v1')
    key = f"gas_daily_billing_v1/{manifest['catalogId']}/{trade}/1/2021-01-01"
    occurrence = str(UUID(hashlib.sha256(key.encode()).hexdigest()[:32]))
    claim = execute(query, manifest, lambda sql: runtime_query(query, manifest, sql), close_id=None,
        target=dict(handler='gas_billing', scopeId=trade, bindingId=saved['instrumentId']), action='gas.bill_daily',
        occurrence_id=occurrence, policy_id=policy, policy_revision=1,
        settings=dict(tradeRevision=1, optionProfileRevision=None, swapProfileId=None, swapProfileRevision=None,
            marginPolicyId=None, marginPolicyRevision=None, gasSource=source), execution_permission='AmendTrade', extra_permissions=[], terminal_boundary=True, dotnet=dotnet)
    assert query(f"SELECT trim_scale(rounded_total)::text FROM trading.gas_cash_determinations WHERE trade_id='{trade}';") == '-90'
    assert query(f"SELECT (input_known_at IS NOT NULL AND input_business_on='2021-01-15')::text FROM automation.runs WHERE id='{claim['id']}';") == 'true'


def rights_checks(c, query, manifest, terms, before, after):
    from check_booked_side_programs import runtime_query
    from native_read_transport import select_context_inputs

    # Cover the payment itself, its quantity source, and an unrelated membership.
    for entries, blocked in [(['receipt', 'payment'], True), (['receipt'], True), (['other', 'fee'], False)]:
        for outcome in (None, 'exercised', 'abandoned'):
            body = deepcopy(terms)
            if not blocked:
                body['physicalSides'].append({**body['physicalSides'][0], 'code': 'other', 'quantity': '17'})
            body.update(operationId=str(uuid4()), exerciseRights=[dict(
                entries=entries, period='delivery_day', holder='us',
                settlement='physical', leadHours=6)])
            booked = c.call('gas/composition/book', body)
            trade = booked['tradeId']
            if outcome is not None:
                c.call('gas/exercise/record', dict(context=before, tradeId=trade,
                    instrumentId=booked['instrumentId'], expectedRevision=1, operationId=str(uuid4()),
                    elections=[dict(right=0, fromDate='2021-01-01', outcome=outcome,
                        effectiveAt='2020-12-18T16:00:00Z', expectedPreviousId=None,
                        evidence='Synthetic billing eligibility election')]))
            quantities = c.call('gas/quantities/read', dict(context=after, tradeId=trade,
                selectionPolicy='operational_projection_v1'))
            obligation = quantities['rows'][0]['obligationId']
            source = c.call('gas/quantities/record', dict(context=after, tradeId=trade,
                expectedRevision=1, operationId=str(uuid4()), obligationId=obligation,
                kind='allocated', status='final', day='2021-01-01', quantity='80',
                evidence='Synthetic final quantity for billing eligibility', expectedPreviousId=None))
            cash_slot = len(body['physicalSides'])
            preview = dict(context=after, tradeId=trade, expectedRevision=1, cashSlot=cash_slot,
                day='2021-01-01', selectionPolicy='final_allocated_billing_v1',
                profile='gas_final_daily_fixed_cash_v1', decimalPlaces=2,
                roundingPolicy='currency_minor_units_to_even_v1')
            read = c.call('gas/billing/read', dict(context=after, tradeId=trade))
            claim = next(row for row in read['claims'] if row['cashSlot'] == cash_slot and row['day'] == '2021-01-01')
            assert claim['supported'] is not blocked, (entries, outcome, claim)
            if not blocked:
                assert c.call('gas/billing/preview', preview)['amount'] == '-240'
                continue
            assert c.call('gas/billing/preview', preview, expected=422)['code'] == 'gas_billing_rights'
            # Bypass the managed adapter: valid source and arithmetic cannot make
            # an unqualified right-dependent payment into an ordinary bill.
            request = dict(preview=preview, operationId=str(uuid4()), previewHash='synthetic',
                sourceId=source['revisionId'], expectedPreviousId=None, correctionDueAt=None,
                reason=None, evidence='Synthetic direct billing eligibility probe')
            wire = json.dumps(request).replace("'", "''")
            calc = json.dumps(dict(sourceId=source['revisionId'], amount='-240', numerator='-240', denominator='1'))
            pin = read['context']
            runtime_query(query, manifest, select_context_inputs(manifest['catalogId'], pin) + f"""
                DO $probe$ BEGIN BEGIN
                  PERFORM security.record_gas_billing('{manifest['catalogId']}','{wire}',
                    '2021-01-15T08:00:00Z','{pin['contextId']}','{calc}');
                  RAISE EXCEPTION 'right_dependent_billing_accepted';
                EXCEPTION WHEN raise_exception THEN
                  IF SQLERRM<>'gas_billing_rights' THEN RAISE;END IF;
                END;END$probe$;""")
    print('PASS billing eligibility: undecided/exercised/abandoned payment and source rights refuse; disjoint rights retain ordinary billing', flush=True)


def swing_checks(c, query, manifest, terms, before, after):
    from check_booked_side_programs import runtime_query
    from native_read_transport import select_context_inputs

    point = next(p for p in c.call('gas/catalog', before)['locations'] if p['deliveryPoint'] == 'synthetic_billing_swing_point')
    for disjoint in (False, True):
        body = deepcopy(terms)
        body['physicalSides'][0].update({key: point[key] for key in ('pipeline', 'zone', 'deliveryPoint', 'product')})
        if disjoint:
            body['physicalSides'].append({**deepcopy(body['physicalSides'][0]), 'code': 'other'})
            body['cashSides'][1]['source'] = 'other'
        body.update(operationId=str(uuid4()), swingRights=[dict(
            side='other' if disjoint else 'receipt', entries=['other', 'fee'] if disjoint else ['receipt', 'payment', 'fee'],
            holder='us', dailyMinimum='80', dailyMaximum='120', planningStep='20', limitPeriod='term',
            planRule='best_plan_on_forwards_v1', deadlineRule='lead_before_first_gas_day_v1', leadHours=6,
            quantityPolicy='swing_elected_quantity_v1', pricing=dict(model='gas_swing_one_factor_lognormal_v1',
                dayCount='act_365_fixed_instant_v1', numericalPolicy='swing_trinomial_6h_unit_reference_v2',
                marksConvention='curve_marks_log_return_held_v1'))])
        booked = c.call('gas/composition/book', body)
        trade = booked['tradeId']
        quantities = c.call('gas/quantities/read', dict(context=after, tradeId=trade, selectionPolicy='operational_projection_v1'))
        obligation = next(row['obligationId'] for row in quantities['rows'] if row['sideCode'] == 'receipt')
        allocation = c.call('gas/quantities/record', dict(context=after, tradeId=trade, expectedRevision=1,
            operationId=str(uuid4()), obligationId=obligation, kind='allocated', status='final', day='2021-01-01',
            quantity='80', evidence='Synthetic swing billing eligibility', expectedPreviousId=None))
        slot = len(body['physicalSides'])
        preview = dict(context=after, tradeId=trade, expectedRevision=1, cashSlot=slot, day='2021-01-01',
            selectionPolicy='final_allocated_billing_v1', profile='gas_final_daily_entry_cash_v1',
            decimalPlaces=2, roundingPolicy='currency_minor_units_to_even_v1')
        for elected in (False, True):
            if elected:
                heads = c.call('gas/swing/read', dict(context=before, tradeId=trade, instrumentId=booked['instrumentId']))['heads']
                c.call('gas/swing/record', dict(context=before, tradeId=trade, instrumentId=booked['instrumentId'],
                    expectedRevision=1, operationId=str(uuid4()), right=0,
                    expectedHeads=[dict(day=h['day'], revisionId=h['revisionId']) for h in heads],
                    elections=[dict(day='2021-01-01', quantity='80', exercisingParty='us',
                        submittedAt='2020-12-18T16:00:00Z', effectiveAt='2020-12-18T16:00:00Z',
                        evidence='Synthetic billing eligibility election')]))
            read = c.call('gas/billing/read', dict(context=after, tradeId=trade))
            claim = next(row for row in read['claims'] if row['cashSlot'] == slot and row['day'] == '2021-01-01')
            assert claim['supported'] == disjoint, (disjoint, elected, claim)
            if disjoint:
                assert c.call('gas/billing/preview', preview)['amount'] == '-240'
                continue
            assert c.call('gas/billing/preview', preview, expected=422)['code'] == 'gas_billing_rights'
            request = dict(preview=preview, operationId=str(uuid4()), previewHash='synthetic',
                sourceId=allocation['revisionId'], expectedPreviousId=None, correctionDueAt=None, reason=None,
                evidence='Synthetic native swing eligibility probe')
            wire = json.dumps(request).replace("'", "''")
            calc = json.dumps(dict(sourceId=allocation['revisionId'], amount='-240', numerator='-240', denominator='1'))
            pin = read['context']
            runtime_query(query, manifest, select_context_inputs(manifest['catalogId'], pin) + f"""
                DO $probe$ BEGIN BEGIN
                  PERFORM security.record_gas_billing('{manifest['catalogId']}','{wire}',
                    '2021-01-15T08:00:00Z','{pin['contextId']}','{calc}');
                  RAISE EXCEPTION 'swing_billing_accepted';
                EXCEPTION WHEN raise_exception THEN
                  IF SQLERRM<>'gas_billing_rights' THEN RAISE;END IF;
                END;END$probe$;""")
    print('PASS billing eligibility: unelected and elected swing payments refuse; disjoint payments remain eligible', flush=True)
