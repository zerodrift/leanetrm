"""Adversarial proofs for immutable custody/native reservation ownership."""
import json
from uuid import uuid4

from gas_park_loan_fixture import literal, rejected


def reservation_owner(f, accept_policy, return_policy):
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    account, book = f.account('synthetic_custody_exclusive_reservations')
    title_policy = f.bind(account, 'gas.owned.receive_title')
    f.bind(account, 'gas.owned.reserve'); f.bind(account, 'gas.owned.release')
    evidence = f.evidence(account, 'synthetic_exclusive_title', 'title_receipt', '1000', '2020-12-18T08:00:00Z')
    lot = f.native(account, 'receive_title', dict(evidenceId=evidence, quantity='1000', titleAt='2020-12-18T08:00:00Z',
        availableAt='2020-12-18T08:00:00Z', originPolicy='opening_owned_stock_v1', carrying='nominal_mark_v1'), policy=title_policy)['id']
    f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
    first = f.book(f.booking(book, 'customer', account, fee='0'))
    second_body = f.booking(book, 'customer', account, fee='0'); second_body['quantity'] = '450'
    second = f.book(second_body)
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    agreement = f.custody_evidence(first['tradeId'], 'synthetic_exclusive_agreement_a', 'custody_agreement', '720', '2020-12-20T09:00:00Z')
    receipt = f.custody_evidence(first['tradeId'], 'synthetic_exclusive_receipt_a', 'custody_receipt', '720', '2020-12-20T10:00:00Z')
    state = f.read(first['tradeId'])
    f.record(first['tradeId'], 'accept', dict(expectedVersion=state['stateVersion'], agreementEvidenceId=agreement['id'],
        receiptEvidenceId=receipt['id'], effectiveAt='2020-12-20T10:00:00Z', allocations=[dict(lotId=lot, quantity='720')]), accept_policy)
    agreement_b = f.custody_evidence(second['tradeId'], 'synthetic_exclusive_agreement_b', 'custody_agreement', '450', '2020-12-20T09:00:00Z')
    receipt_b = f.custody_evidence(second['tradeId'], 'synthetic_exclusive_receipt_b', 'custody_receipt', '450', '2020-12-20T12:00:00Z')
    returned = f.custody_evidence(first['tradeId'], 'synthetic_exclusive_return_a', 'custody_return', '270', '2020-12-20T12:00:00Z')
    a, b = f.read(first['tradeId']), f.read(second['tradeId'])
    body_a = dict(expectedVersion=a['stateVersion'], businessKey='synthetic_exclusive_return_a', evidenceId=returned['id'], quantity='270',
        effectiveAt='2020-12-20T12:00:00Z', allocations=[dict(reservationId=a['reservations'][0]['reservationId'], quantity='270')])
    context = f.capture()
    f.query("""CREATE FUNCTION trading.synthetic_custody_reservation_alias(catalog uuid,trade_a uuid,trade_b uuid,body_a jsonb,context uuid,
       head_b integer,agreement_b uuid,receipt_b uuid,accept_policy uuid,return_policy uuid,reuse_remainder boolean,reuse_receipt boolean) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
    DECLARE accepted trading.gas_park_custody_evidence;receipt trading.gas_park_custody_evidence;result jsonb;op uuid=gen_random_uuid();event uuid=gen_random_uuid();
      actor uuid;pub integer;reserve uuid;lot uuid;account uuid;native jsonb;body jsonb;request jsonb;outcome jsonb;BEGIN
      result=security.record_gas_park_custody(catalog,gen_random_uuid(),trade_a,'return',body_a,'2020-12-20T16:00:00Z',context,return_policy,1,false);
      reserve=(result#>>'{allocations,0,remainderReservationId}')::uuid;lot=(result#>>'{allocations,0,lotId}')::uuid;
      IF reuse_receipt THEN op=(result->>'operationId')::uuid;END IF;
      SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
      IF NOT reuse_remainder THEN
        SELECT owned_account_id INTO STRICT account FROM trading.gas_park_custody_trades WHERE(catalog_id,trade_id)=(catalog,trade_b);
        SELECT max(publication_order)+1 INTO pub FROM trading.publications WHERE catalog_id=catalog;
        native=trading.apply_gas_owned_financial(catalog,account,'reserve',jsonb_build_object('lotId',lot,'quantity','450',
          'businessKey','custody:'||trade_b::text||':'||event::text||':'||lot::text,'effectiveAt','2020-12-20T12:00:00Z'),
          '2020-12-20T16:00:00Z',context,actor,pub);reserve=(native->>'id')::uuid;
      END IF;
      SELECT e.* INTO STRICT accepted FROM trading.gas_park_custody_evidence e WHERE(e.catalog_id,e.id)=(catalog,agreement_b);
      SELECT e.* INTO STRICT receipt FROM trading.gas_park_custody_evidence e WHERE(e.catalog_id,e.id)=(catalog,receipt_b);
      SELECT max(publication_order)+1 INTO pub FROM trading.publications WHERE catalog_id=catalog;
      INSERT INTO trading.publications VALUES(catalog,pub,pg_current_xact_id());
      INSERT INTO trading.gas_park_custody_acceptances VALUES(catalog,trade_b,event,op,accepted.source_key,accepted.id,receipt.source_key,receipt.id,
        context,head_b,accept_policy,1,actor,pub,'2020-12-20T12:00:00Z');
      INSERT INTO trading.gas_park_custody_lots VALUES(catalog,trade_b,lot,reserve,450);
      body=trading.gas_park_custody_recorded_body(catalog,'accept',event);
      request=jsonb_build_object('tradeId',trade_b,'action','accept','body',body,'asOf',TIMESTAMPTZ '2020-12-20T16:00:00Z','contextId',context,'policyId',accept_policy,'policyRevision',1);
      outcome=trading.gas_park_custody_recorded_outcome(catalog,'accept',event)||jsonb_build_object('operationId',op,'changed',true);
      IF NOT reuse_receipt THEN INSERT INTO trading.gas_park_custody_commands VALUES(catalog,op,trade_b,actor,'accept',request,outcome,clock_timestamp()+interval '30 seconds',accept_policy,1);END IF;
    END$$;ALTER FUNCTION trading.synthetic_custody_reservation_alias(uuid,uuid,uuid,jsonb,uuid,integer,uuid,uuid,uuid,uuid,boolean,boolean) OWNER TO openetrm_security_owner;
    GRANT EXECUTE ON FUNCTION trading.synthetic_custody_reservation_alias(uuid,uuid,uuid,jsonb,uuid,integer,uuid,uuid,uuid,uuid,boolean,boolean) TO openetrm_runtime;""")
    admitted = []
    for reuse, wrong_receipt in [(True,False),(False,False),(True,True)]:
        try:
            f.call(f"SELECT trading.synthetic_custody_reservation_alias('{f.catalog}','{first['tradeId']}','{second['tradeId']}',{literal(body_a)},'{context}',"
                f"{b['stateVersion']},'{agreement_b['id']}','{receipt_b['id']}','{accept_policy}','{return_policy}',{str(reuse).lower()},{str(wrong_receipt).lower()});SET CONSTRAINTS ALL IMMEDIATE;", rollback=True)
        except RuntimeError as error:
            expected = 'gas_custody_receipt' if wrong_receipt else 'gas_custody_allocation' if reuse else 'gas_owned_context'
            assert expected in str(error), str(error)
        else:
            admitted.append('wrong parent receipt' if wrong_receipt else 'aliased remainder' if reuse else 'unreviewed sibling release')
    assert not admitted, 'Custody accepted invalid reservation ownership/source: ' + ', '.join(admitted)
    assert f.read(first['tradeId'])['balances']['inCustody'] == '720' and f.read(second['tradeId'])['acceptance'] is None
    print('PASS a reservation cannot fund two custody owners, and a sibling command cannot refresh the admitted stock source.', flush=True)

    f.record(first['tradeId'], 'return', body_a, return_policy, context=context)
    refreshed = f.capture()
    current_b = f.read(second['tradeId'], context=refreshed)
    f.record(second['tradeId'], 'accept', dict(expectedVersion=current_b['stateVersion'], agreementEvidenceId=agreement_b['id'],
        receiptEvidenceId=receipt_b['id'], effectiveAt='2020-12-20T12:00:00Z', allocations=[dict(lotId=lot, quantity='450')]),
        accept_policy, context=refreshed)
    actual_a, actual_b = f.read(first['tradeId']), f.read(second['tradeId'])
    assert actual_a['balances']['inCustody'] == actual_b['balances']['inCustody'] == '450'
    assert actual_a['reservations'][0]['reservationId'] != actual_b['reservations'][0]['reservationId']
    positions = f.holdings(book)['positions']
    assert len(positions) == 1 and positions[0]['id'] == lot and positions[0]['remaining_quantity'] == '1000'
    print('PASS a genuinely refreshed source admits B after committed A return, with distinct reservations900 and unchanged title1000.', flush=True)


def source_currency(f, accept_policy, return_policy):
    """Keep future stock boundaries and revised quantities in the reviewed cut."""
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    account, book = f.account('synthetic_custody_stock_source')
    title_policy = f.bind(account, 'gas.owned.receive_title')
    reserve_policy = f.bind(account, 'gas.owned.reserve')
    f.bind(account, 'gas.owned.release')
    dispatch_policy = f.bind(account, 'gas.owned.dispatch')
    correction_policy = f.bind(account, 'gas.owned.reconcile_quantity')
    title_at, future_at = '2020-12-18T08:00:00Z', '2020-12-23T10:00:00Z'
    title = f.evidence(account, 'synthetic_source_title', 'title_receipt', '1000', title_at)
    lot = f.native(account, 'receive_title', dict(evidenceId=title, quantity='1000', titleAt=title_at,
        availableAt=title_at, originPolicy='opening_owned_stock_v1', carrying='nominal_mark_v1'), policy=title_policy)['id']
    f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
    booked = f.book(f.booking(book, 'customer', account, fee='0'))
    trade = booked['tradeId']
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    agreement = f.custody_evidence(trade, 'synthetic_source_agreement', 'custody_agreement', '720', '2020-12-20T09:00:00Z')
    receipt = f.custody_evidence(trade, 'synthetic_source_receipt', 'custody_receipt', '720', '2020-12-20T10:00:00Z')
    body = dict(expectedVersion=f.read(trade)['stateVersion'], agreementEvidenceId=agreement['id'], receiptEvidenceId=receipt['id'],
        effectiveAt='2020-12-20T10:00:00Z', allocations=[dict(lotId=lot, quantity='720')])
    old = f.capture()
    f.at, f.day = '2020-12-23T16:00:00Z', '2020-12-23'
    f.native(account, 'reserve', dict(lotId=lot, quantity='100', businessKey='synthetic_future_independent_reserve',
        effectiveAt=future_at), policy=reserve_policy)
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    before = f.publication()
    rejected(lambda: f.record(trade, 'accept', body, accept_policy, context=old), 'gas_owned_context')
    assert f.publication() == before and f.read(trade)['acceptance'] is None
    f.record(trade, 'accept', body, accept_policy)
    print('PASS a source-unknown future reservation refuses acceptance despite sufficient stock; the refreshed source accepts720.', flush=True)

    # The physical evidence is already in the review cut; only its later stock
    # use is unknown, and that use is beyond the selected business instant.
    f.at, f.day = '2020-12-23T16:00:00Z', '2020-12-23'
    delivery = f.evidence(account, 'synthetic_future_source_dispatch', 'physical_delivery', '50', future_at)
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'

    def return_body(key, quantity, when):
        evidence = f.custody_evidence(trade, key, 'custody_return', quantity, when)
        state = f.read(trade)
        return dict(expectedVersion=state['stateVersion'], businessKey=key, evidenceId=evidence['id'], quantity=quantity,
            effectiveAt=when, allocations=[dict(reservationId=state['reservations'][0]['reservationId'], quantity=quantity)])

    returned = return_body('synthetic_source_return_270', '270', '2020-12-20T13:00:00Z')
    old = f.capture()
    f.at, f.day = '2020-12-23T16:00:00Z', '2020-12-23'
    dispatched = f.native(account, 'dispatch', dict(lotId=lot, evidenceId=delivery, quantity='50', reservationId=None,
        effectiveAt=future_at), policy=dispatch_policy)['id']
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    before = f.publication()
    rejected(lambda: f.record(trade, 'return', returned, return_policy, context=old), 'gas_owned_context')
    assert f.publication() == before and f.read(trade)['balances']['inCustody'] == '720'
    f.record(trade, 'return', returned, return_policy)
    print('PASS a source-unknown future dispatch refuses a stale return; a refreshed source releases270 without changing title.', flush=True)

    def reconcile(positions, consumptions):
        selection = dict(positions=positions, consumptions=consumptions, evidence='Synthetic exact source quantity correction')
        return json.loads(f.call(f"SELECT security.reconcile_gas_owned_quantities('{f.catalog}','{uuid4()}','{account}',"
            f"{literal(selection)},'{f.at}','{f.capture()}','{correction_policy}',1,false)::text;"))

    returned = return_body('synthetic_source_return_100', '100', '2020-12-20T14:00:00Z')
    old = f.capture()
    revised_title = f.evidence(account, 'synthetic_source_title', 'title_receipt', '1100', title_at, revision=1)
    reconcile([dict(positionId=lot, expectedRevision=1, quantity='1100', retracted=False, evidenceId=revised_title,
        sourceAllocation='preserve_unallocated_source_quantity_v1')], [])
    before = f.publication()
    rejected(lambda: f.record(trade, 'return', returned, return_policy, context=old), 'gas_quantity_revision')
    assert f.publication() == before and f.read(trade)['balances']['inCustody'] == '450'
    f.record(trade, 'return', returned, return_policy)
    assert f.holdings(book)['positions'][0]['remaining_quantity'] == '1100'

    returned = return_body('synthetic_source_return_50', '50', '2020-12-20T15:00:00Z')
    old = f.capture()
    f.at, f.day = '2020-12-23T16:00:00Z', '2020-12-23'
    revised_delivery = f.evidence(account, 'synthetic_future_source_dispatch', 'physical_delivery', '40', future_at, revision=1)
    reconcile([], [dict(consumptionId=dispatched, expectedRevision=1, lotId=lot, quantity='40', retracted=False,
        evidenceId=revised_delivery, reservationId=None, sourceAllocation='preserve_unallocated_source_quantity_v1')])
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    before = f.publication()
    rejected(lambda: f.record(trade, 'return', returned, return_policy, context=old), 'gas_quantity_revision')
    assert f.publication() == before and f.read(trade)['balances']['inCustody'] == '350'
    f.record(trade, 'return', returned, return_policy)
    assert f.read(trade)['balances']['inCustody'] == '300'
    assert f.holdings(book)['positions'][0]['remaining_quantity'] == '1100'
    f.at, f.day = '2020-12-23T16:00:00Z', '2020-12-23'
    assert f.holdings(book)['positions'][0]['remaining_quantity'] == '1060'
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    print('PASS revised lot and future consumption quantities each refuse the old source; refreshed returns retain title1100 and independently dispatched40.', flush=True)


def no_use(f, accept_policy):
    """Attack every installed stock use with an identified custody reservation."""
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    account, book = f.account('synthetic_custody_no_use')
    policies = {action: f.bind(account, action) for action in ['gas.owned.receive_title', 'gas.owned.reserve', 'gas.owned.release',
        'gas.owned.dispatch', 'gas.owned.loss', 'gas.loan.borrow', 'gas.loan.repay', 'gas.owned.reconcile_quantity']}
    title_at = '2020-12-18T08:00:00Z'
    title = f.evidence(account, 'synthetic_no_use_owned', 'title_receipt', '1000', title_at)
    lot = f.native(account, 'receive_title', dict(evidenceId=title, quantity='1000', titleAt=title_at, availableAt=title_at,
        originPolicy='opening_owned_stock_v1', carrying='nominal_mark_v1'), policy=policies['gas.owned.receive_title'])['id']
    f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
    trade = f.book(f.booking(book, 'customer', account, fee='0'))['tradeId']
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    loan_at, use_at = '2020-12-20T09:00:00Z', '2020-12-20T11:00:00Z'
    loan_title = f.evidence(account, 'synthetic_no_use_loan_title', 'title_receipt', '10', loan_at)
    loan_agreement = f.evidence(account, 'synthetic_no_use_loan_agreement', 'loan_agreement', '10', loan_at)
    loan = f.native(account, 'borrow', dict(agreementId=loan_agreement, evidenceId=loan_title, quantity='10', titleAt=loan_at,
        availableAt=loan_at, returnAt='2020-12-21T12:00:00Z', returnMarkCurve='future', returnMarkContractId=f.contract,
        carrying='nominal_mark_v1', feeAmount='0', feeDueAt=None, lenderId=f.party, lenderRevision=1), policy=policies['gas.loan.borrow'])
    agreement = f.custody_evidence(trade, 'synthetic_no_use_agreement', 'custody_agreement', '720', loan_at)
    receipt = f.custody_evidence(trade, 'synthetic_no_use_receipt', 'custody_receipt', '720', '2020-12-20T10:00:00Z')
    f.record(trade, 'accept', dict(expectedVersion=f.read(trade)['stateVersion'], agreementEvidenceId=agreement['id'],
        receiptEvidenceId=receipt['id'], effectiveAt='2020-12-20T10:00:00Z', allocations=[dict(lotId=lot, quantity='720')]), accept_policy)
    reserved = f.read(trade)['reservations'][0]['reservationId']
    before = f.publication()
    rejected(lambda: f.native(account, 'release', dict(reservationId=reserved, effectiveAt=use_at),
        policy=policies['gas.owned.release']), 'gas_custody_reservation_protected')
    assert f.publication() == before
    for action, kind, policy in [('dispatch', 'physical_delivery', 'gas.owned.dispatch'), ('loss', 'physical_loss', 'gas.owned.loss'),
        ('repay', 'loan_return', 'gas.loan.repay')]:
        evidence = f.evidence(account, 'synthetic_no_use_' + action, kind, '1', use_at)
        body = dict(lotId=lot, evidenceId=evidence, quantity='1', reservationId=reserved, effectiveAt=use_at)
        if action == 'repay':
            body['loanId'] = loan['id']
        before = f.publication()
        rejected(lambda: f.native(account, action, body, policy=policies[policy]), 'gas_custody_reservation_protected')
        assert f.publication() == before
    free_evidence = f.evidence(account, 'synthetic_no_use_free_delivery', 'physical_delivery', '5', use_at)
    free = f.native(account, 'dispatch', dict(lotId=lot, evidenceId=free_evidence, quantity='5', reservationId=None,
        effectiveAt=use_at), policy=policies['gas.owned.dispatch'])
    correction = dict(positions=[], consumptions=[dict(consumptionId=free['id'], expectedRevision=1, lotId=lot, quantity='5',
        retracted=False, evidenceId=free_evidence, reservationId=reserved, sourceAllocation='preserve_unallocated_source_quantity_v1')],
        evidence='Synthetic attempted reassignment of free delivery to custody')
    before = f.publication()
    rejected(lambda: f.call(f"SELECT security.reconcile_gas_owned_quantities('{f.catalog}','{uuid4()}','{account}',{literal(correction)},"
        f"'{f.at}','{f.capture()}','{policies['gas.owned.reconcile_quantity']}',1,false);"), 'gas_custody_reservation_protected')
    assert f.publication() == before
    rows = {row['id']: row for row in f.holdings(book)['positions']}
    assert rows[lot]['remaining_quantity'] == '995' and rows[loan['stockId']]['remaining_quantity'] == '10'
    assert f.read(trade)['balances']['inCustody'] == '720'
    print('PASS generic release, dispatch, loss, loan repayment and quantity reassignment cannot spend custody reservations; free stock5 remains usable.', flush=True)
