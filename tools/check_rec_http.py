"""Native certificate trade and manually evidenced serial ownership checks."""
from copy import deepcopy
from uuid import uuid4
from exact_values import fraction
from preview_client import PreviewClient, initial_frame


def run_checks(client, query, manifest):
    client.call('market/current', {})
    client.call('profile', {'profile': 'trader'})
    frame = initial_frame(client.call('workspace'), '2020-12-15')['id']
    context = dict(snapshotId=frame, convention='rec_forward_v1', knowledgeMode='current', contextId=None)
    route = 'rec/trades/'
    catalog = client.call(route + 'catalog', {'context': context})
    assert catalog['canBook'] and catalog['knownAt'] and catalog['contextId']
    assignment = next(a for a in catalog['assignments'] if a['code'] == 'synthetic_rec_2019')
    calendar = dict(implementation='side_calendar_policy_v1', delivery=dict(applicable=False, calendar=None),
        observation=dict(applicable=False, calendar=None), payment=dict(applicable=False, calendar=None))
    terms = dict(definitionRevisionId=catalog['definitionRevisionId'], assignmentCode=assignment['code'],
        productCode=assignment['productCode'], vintageFrom=assignment['vintageFrom'], vintageTo=assignment['vintageTo'],
        deliveryFrom='2021-01-15T00:00:00Z', deliveryTo='2021-01-16T00:00:00Z', quantity='60',
        physicalDirection='deliver', considerationDirection='receive',
        consideration=dict(mode='fixed', quoteCode=assignment['quoteCode'], fixedPrice='40', curveCode=None, contractId=None),
        paymentRule=dict(frequency='monthly', rule='days_after_period_end', days=10, dayOfMonth=None,
            dayBasis='calendar_days', shortMonth=None, adjustment='unadjusted', calendar=None, implementation='monthly_payment_date_rules_v1'),
        calendarPolicy=calendar, discounting=False)
    preview = client.call(route + 'preview', dict(context=context, terms=terms))
    assert fraction(preview['valuation']['value']) == -600
    assert [s['coefficient'] for s in preview['valuation']['sides']] == ['-1', '1']
    assert [fraction(s['rows'][0]['quantity']) for s in preview['valuation']['sides']] == [60, 60]
    assert preview['valuation']['sides'][0]['rows'][0]['paymentAt'].startswith('2021-01-16T00:00:00')
    assert preview['valuation']['sides'][1]['rows'][0]['paymentAt'].startswith('2021-02-10T00:00:00')
    book = catalog['books'][0]; entity = catalog['legalEntities'][0]; party = catalog['counterparties'][0]
    command = dict(context=context, operationId=str(uuid4()), tradeId=str(uuid4()), ownerId=catalog['ownerId'],
        bookId=book['id'], tradeDate='2020-12-15', legalEntityId=entity['id'], legalEntityRevision=entity['revision'],
        counterpartyId=party['id'], counterpartyRevision=party['revision'], terms=terms)
    before = int(query('SELECT count(*) FROM trading.trades;'))
    saved = client.call(route + 'book', command, timeout=60)
    assert saved['changed'] and saved['tradeId'] == command['tradeId']
    assert client.call(route + 'book', command) == saved
    assert client.call(route + 'status', dict(operationId=command['operationId'], ownerId=command['ownerId'])) == saved
    duplicate = deepcopy(command); duplicate['operationId'] = str(uuid4())
    assert not client.call(route + 'book', duplicate, timeout=60)['changed']
    assert int(query('SELECT count(*) FROM trading.trades;')) == before + 1
    selected = dict(context=context, tradeId=saved['tradeId'], revision=1)
    reopened = client.call(route + 'get', selected)
    assert reopened['request'] == command and len(reopened['sides']) == 2
    value = client.call(route + 'valuation', selected)
    assert fraction(value['valuation']['value']) == -600
    client.call('market/context', dict(effectiveOn='2020-12-15',knownAt=value['knownAt'],mode='at',selection='latest_correction'))
    historical = initial_frame(client.call('workspace'), '2020-12-15')['id']
    retained = deepcopy(selected); retained['context'].update(snapshotId=historical, knowledgeMode='retained', contextId=value['contextId'])
    assert client.call(route + 'valuation', retained) == client.call(route + 'valuation', retained)
    client.call(route + 'book', command, expected=422)
    client.call('market/current', {})
    dates = dict(effectiveOn='2020-12-15', knowledgeMode='current', retained=None, convention='rec_business_date_end_utc_v1')
    operator = PreviewClient(client.url); operator.bootstrap(); operator.call('profile', {'profile': 'operator'})
    readonly = client.call('rec/directory', {'context': dates})
    assert readonly['canConfigure'] is False and readonly['canRecord'] is False, 'A Trader reads REC inventory and changes nothing'
    directory = operator.call('rec/directory', {'context': dates})
    owner = directory['ownerId']; account_id = str(uuid4()); at = '2020-12-15T16:00:00Z'
    account = dict(context=dates, requestId=str(uuid4()), ownerId=owner, accountId=account_id, bookId=book['id'],
        legalEntityId=entity['id'], legalEntityRevision=entity['revision'], referenceCatalogId=catalog['referenceCatalogId'],
        referenceRevisionId=catalog['referenceRevisionId'], registryCode='fixture', registryAccountReference='SYNTHETIC-HTTP-OWNED', effectiveAt=at)
    receipt = operator.call('rec/account', account)
    assert operator.call('rec/account', account) == receipt
    destination = dict(context=dates, requestId=str(uuid4()), ownerId=owner, destinationId=str(uuid4()),
        counterpartyId=party['id'], counterpartyRevision=party['revision'], referenceCatalogId=catalog['referenceCatalogId'],
        referenceRevisionId=catalog['referenceRevisionId'], registryCode='fixture', registryAccountReference='SYNTHETIC-HTTP-DELIVERY', effectiveAt=at)
    operator.call('rec/destination', destination)
    lot = str(uuid4())
    def record(action, generation, date_context=dates, effective_at=at):
        return dict(context=date_context, requestId=str(uuid4()), ownerId=owner, accountId=account_id,
            businessId=str(uuid4()), expectedGeneration=generation, effectiveAt=effective_at, action=action)
    incoming = dict(kind='receipt', lotId=lot, productCode=terms['productCode'], registryLotReference='SYNTHETIC-HTTP-LOT',
        vintageFrom=terms['vintageFrom'], vintageTo=terms['vintageTo'], **{'from':'0','to':'100'}, receiptKind='issuance',
        evidenceSource='fixture', evidenceReference='SYNTHETIC-HTTP-ISSUANCE')
    issue = record(incoming, 0); issued = operator.call('rec/record', issue)
    assert operator.call('rec/record', issue) == issued
    assert client.call('rec/record', issue, expected=403)['code'] == 'access_denied'
    assert client.call('rec/account', account, expected=403)['code'] == 'access_denied'
    holding = client.call('rec/read', dict(context=dates, accountId=account_id))
    delivery = next(d for d in holding['deliveryChoices'] if d['tradeId'] == saved['tradeId'])
    assert fraction(delivery['remaining']) == 60
    allocate = record(dict(kind='allocate', lotId=lot, **{'from':'0','to':'60'}, deliveryId=delivery['id']), 1)
    allocated = operator.call('rec/record', allocate)
    retire = dict(kind='retire', lotId=lot, **{'from':'60','to':'100'}, beneficiaryKind='legal_entity', beneficiaryId=entity['id'],
        beneficiaryRevision=entity['revision'], purposeCode='synthetic_voluntary', evidenceSource='fixture', evidenceReference='SYNTHETIC-HTTP-RETIREMENT')
    invalid_retire = deepcopy(retire); invalid_retire.update({'from':'0','to':'1'})
    operator.call('rec/record', record(invalid_retire, 2), expected=422)
    operator.call('rec/record', record(retire, 2))
    transfer = dict(kind='transfer', lotId=lot, **{'from':'0','to':'60'}, allocationId=allocated['factId'], allocationRevision=allocated['revision'],
        destinationId=destination['destinationId'], evidenceSource='fixture', evidenceReference='SYNTHETIC-HTTP-TRANSFER')
    early = operator.call('rec/record', record(transfer, 3), expected=422)
    assert early['code'] == 'rec_delivery_window'
    delivery_dates = dict(dates, effectiveOn='2021-01-15')
    sent = record(transfer, 3, delivery_dates, '2021-01-15T12:00:00Z')
    transferred = operator.call('rec/record', sent)
    assert operator.call('rec/status', dict(requestId=sent['requestId'], ownerId=owner)) == transferred
    final = client.call('rec/read', dict(context=delivery_dates, accountId=account_id))
    balance = next(b for b in final['balances'] if b['productCode'] == terms['productCode'])
    assert fraction(balance['available']) == 0 and fraction(balance['allocated']) == 0
    assert fraction(balance['transferred']) == 60 and fraction(balance['retired']) == 40
    assert final['generation'] == 4 and len(final['history']) == 4
    older = client.call('rec/read', dict(context=dates, accountId=account_id))
    older_balance = next(b for b in older['balances'] if b['productCode'] == terms['productCode'])
    assert fraction(older_balance['allocated']) == 60 and fraction(older_balance['retired']) == 40
    assert fraction(client.call(route + 'valuation', selected)['valuation']['value']) == -600
    print('PASS REC HTTP: retained native physical/cash schedules, first outcomes, exact product/vintage and 100 issued = 60 transferred + 40 retired', flush=True)
    # Retain a separate unfulfilled native target and exact large serial range
    # for the browser's own allocation/save/reload workflow.
    browser_command = deepcopy(command)
    browser_command.update(operationId=str(uuid4()), tradeId=str(uuid4()))
    browser_command['terms'].update(quantity='100', deliveryFrom='2021-01-17T00:00:00Z', deliveryTo='2021-01-18T00:00:00Z')
    browser_trade = client.call(route + 'book', browser_command, timeout=60)
    browser_lot = str(uuid4()); first = 9007199254740993
    browser_receipt = deepcopy(incoming)
    browser_receipt.update(lotId=browser_lot, registryLotReference='SYNTHETIC-HTTP-BROWSER-LOT',
        evidenceReference='SYNTHETIC-HTTP-BROWSER-ISSUANCE', **{'from':str(first),'to':str(first+100)})
    operator.call('rec/record', record(browser_receipt, final['generation'], delivery_dates, '2021-01-15T16:00:00Z'))
    browser_holding = client.call('rec/read', dict(context=delivery_dates, accountId=account_id))
    browser_delivery = next(d for d in browser_holding['deliveryChoices'] if d['tradeId'] == browser_trade['tradeId'])
    assert fraction(browser_delivery['remaining']) == 100
    assert any(s['lotId'] == browser_lot and s['from'] == str(first) and s['state'] == 'available' for s in browser_holding['segments'])
    # Independent native sessions compete for one shared booked delivery. Each
    # account has enough eligible serials; capacity, not either local balance,
    # admits at most one sixty-certificate allocation.
    other_account = deepcopy(account)
    other_account.update(requestId=str(uuid4()), accountId=str(uuid4()), registryAccountReference='SYNTHETIC-HTTP-OTHER-OWNED')
    operator.call('rec/account', other_account)
    other_lot = str(uuid4()); other_receipt = deepcopy(incoming)
    other_receipt.update(lotId=other_lot, registryLotReference='SYNTHETIC-HTTP-CAPACITY-LOT', evidenceReference='SYNTHETIC-HTTP-CAPACITY-ISSUANCE')
    other_issue = record(other_receipt, 0, delivery_dates, '2021-01-15T16:00:00Z')
    other_issue['accountId'] = other_account['accountId']
    other_issued = operator.call('rec/record', other_issue)
    import json
    from concurrent.futures import ThreadPoolExecutor
    def signed(sql):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','operator','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
        return ('BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;'
            f"SELECT security.use_context('{token}');" + sql + 'COMMIT;')
    race_inputs = [(account_id, browser_lot, first, browser_holding['generation']),
        (other_account['accountId'], other_lot, 0, other_issued['generation'])]
    attempts = []
    for target_account, target_lot, start, generation in race_inputs:
        action = dict(kind='allocate', lotId=target_lot, **{'from':str(start),'to':str(start+60)}, deliveryId=browser_delivery['id'])
        literal = json.dumps(action).replace("'", "''")
        attempts.append(signed(f"SELECT security.record_rec_inventory('{manifest['catalogId']}','{uuid4()}','{target_account}','{uuid4()}',{generation},'2021-01-15T16:00:00Z','{literal}'::jsonb);"))
    def compete(sql):
        try:
            return json.loads(query(sql))
        except RuntimeError as error:
            assert 'rec_delivery_capacity' in str(error), str(error)
            return None
    with ThreadPoolExecutor(max_workers=2) as workers:
        competed = list(workers.map(compete, attempts))
    winners = [i for i, outcome in enumerate(competed) if outcome is not None]
    assert len(winners) == 1, 'Two owned accounts cannot overcommit one shared certificate delivery.'
    winner = winners[0]; allocated_race = competed[winner]
    winning_account, winning_lot, winning_first, _ = race_inputs[winner]
    release = record(dict(kind='release',lotId=winning_lot, **{'from':str(winning_first),'to':str(winning_first+60)},
        allocationId=allocated_race['factId'],allocationRevision=allocated_race['revision']),allocated_race['generation'],delivery_dates,'2021-01-15T16:00:00Z')
    release['accountId'] = winning_account
    operator.call('rec/record',release)
    browser_holding = client.call('rec/read', dict(context=delivery_dates, accountId=account_id))
    browser_delivery = next(d for d in browser_holding['deliveryChoices'] if d['tradeId'] == browser_trade['tradeId'])
    assert fraction(browser_delivery['remaining']) == 100
    available = [s for s in browser_holding['segments'] if s['lotId'] == browser_lot and s['state'] == 'available']
    assert sum(fraction(s['quantity']) for s in available) == 100 and min(int(s['from']) for s in available) == first
    print('PASS REC shared delivery: competing accounts cannot allocate 120 against 100; exact release restores the native capacity', flush=True)
    return dict(tradeId=browser_trade['tradeId'], revision=1, tradeReference=browser_trade['tradeReference'], accountId=account_id,
        lotId=browser_lot, deliveryId=browser_delivery['id'], productCode=terms['productCode'], quoteCode=assignment['quoteCode'],
        businessDate='2021-01-15', effectiveAt='2021-01-15T16:00:00Z', **{'from':str(first),'to':str(first+10)},
        newDeliveryFrom='2021-01-18T00:00:00Z', newDeliveryTo='2021-01-19T00:00:00Z')
