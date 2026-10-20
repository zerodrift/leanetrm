"""Exact party versions belong to retained trades, never reusable instruments."""
from uuid import uuid4
import json
from preview_client import PreviewClient


def run_checks(url, query, manifest, client=None):
    c = client or PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    frame = next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2020-12-18'))
    context = {'snapshotId': frame['id'], 'convention': 'fixed_total_cash_execution_pnl_v1', 'knowledgeMode': 'current'}
    catalog = c.call('futures/catalog', context)
    entity, other_entity = [next(p for p in catalog['legalEntities'] if p['code'] == code) for code in ('entity_1', 'entity_2')]
    party, other_party = [next(p for p in catalog['counterparties'] if p['code'] == code) for code in ('party_1', 'party_2')]
    def booking(e=entity, p=party):
        return {'legalEntityId': e['id'], 'legalEntityRevision': e['revision'],
                'counterpartyId': p['id'], 'counterpartyRevision': p['revision']}
    contract = catalog['contracts'][0]
    body = {**context, **booking(), 'operationId': str(uuid4()), 'bookId': catalog['books'][0]['id'],
            'curve': contract['curve'], 'contractId': contract['id'], 'lots': '2', 'executionPrice': '3'}
    first = c.call('futures/book', body)
    assert first == c.call('futures/book', body)
    second = c.call('futures/book', {**body, **booking(other_entity, other_party), 'lots': '3', 'operationId': str(uuid4())})
    assert first['instrumentId'] == second['instrumentId']
    def detail(revision=None):
        return c.call('futures/editor', {'context': context, 'tradeId': first['tradeId'], 'revision': revision})
    assert all(detail()['state']['booking'][key] == value for key, value in booking().items())
    retained = detail()['context']['contextId']
    amend = {**body, **booking(other_entity, other_party), 'operationId': str(uuid4()),
             'tradeId': first['tradeId'], 'expectedRevision': 1, 'effectiveAt': catalog['asOf'],
             'reason': 'Synthetic party correction', 'amendmentConvention': 'unprocessed_futures_booking_amendment_v1'}
    c.call('futures/amend-preview', amend)
    changed = c.call('futures/amend', amend)
    assert changed == c.call('futures/amend', amend) and changed['instrumentId'] == first['instrumentId']
    assert detail()['state']['booking']['counterpartyId'] == other_party['id']
    assert detail(1)['state']['booking']['counterpartyId'] == party['id'] and detail(1)['state']['booking']['tradeDate'] == detail()['state']['booking']['tradeDate']
    historical = c.call('futures/editor', {'context': {**context, 'knowledgeMode': 'retained', 'contextId': retained},
                                         'tradeId': first['tradeId'], 'revision': None})
    assert historical['state']['revision'] == 1 and historical['state']['booking']['counterpartyId'] == party['id']
    c.call('futures/book', {**body, 'counterpartyId': other_party['id']}, expected=409)
    for patch in ({'counterpartyId': str(uuid4())}, {'legalEntityRevision': 999}, {'counterpartyRevision': None},
                  {'tradeDate': '2020-02-30'}):
        c.call('futures/book', {**body, 'operationId': str(uuid4()), **patch}, expected=422)
    c.call('profile', {'profile': 'viewer'})
    c.call('futures/book', {**body, 'operationId': str(uuid4())}, expected=403)
    print('PASS exact trade party versions/date persist, amendments retain history, independent trades reuse instruments and invalid/missing parties reject', flush=True)

    c.call('profile', {'profile': 'trader'})
    swap_context = {**context, 'convention': 'independent_side_financial_swap_v2'}
    swaps = c.call('swaps/catalog', swap_context)
    index = next(i for i in swaps['indices'] if i['curve'] == 'base_forward')
    rule = {'frequency': 'monthly', 'rule': 'days_after_period_end', 'days': 18, 'dayOfMonth': None,
            'dayBasis': 'calendar_days', 'shortMonth': None, 'adjustment': 'unadjusted', 'calendar': None,
            'implementation': 'monthly_payment_date_rules_v1'}
    swap = {**swap_context, **booking(), 'operationId': str(uuid4()), 'bookId': body['bookId'],
            'definitionRevisionId': index['definitionRevisionId'], 'commodity': index['commodity'], 'quantity': '100', 'fixedCoefficient': '-1', 'discounting': False,
            'fixed': {'price': '3', 'currency': index['currencyCode'], 'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01', 'paymentSchedule': rule},
            'floating': {'curve': index['curve'], 'index': index['index'], 'currency': index['currencyCode'],
                         'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01',
                         'paymentSchedule': rule, 'resetFrequency': 'monthly', 'averagingMethod': 'none'}}
    c.call('swaps/preview', swap); booked = c.call('swaps/book', swap)
    edit = {**swap, **booking(other_entity, other_party), 'tradeId': booked['tradeId'],
            'expectedRevision': 1, 'operationId': str(uuid4()), 'effectiveAt': swaps['asOf'], 'reason': 'Synthetic party amendment'}
    c.call('swaps/preview', edit); changed_swap = c.call('swaps/amend', edit)
    assert changed_swap == c.call('swaps/amend', edit) and changed_swap['instrumentId'] == booked['instrumentId']
    for revision, selected in ((1, party), (2, other_party)):
        state = c.call('swaps/editor', {'context': swap_context, 'tradeId': booked['tradeId'], 'revision': revision})['state']
        assert state['booking']['counterpartyId'] == selected['id']
    c.call('swaps/book', {**swap, 'operationId': str(uuid4()), 'legalEntityId': str(uuid4())}, expected=422)
    print('PASS swap party-only amendments preserve unit instrument identity and exact prior party/date history', flush=True)

    catalog_id = manifest['catalogId']; future_id = str(uuid4()); foreign_catalog = str(uuid4()); foreign_id = str(uuid4())
    query(f"""BEGIN; SELECT pg_advisory_xact_lock(730849102);
        INSERT INTO trading.publications SELECT '{catalog_id}',max(publication_order)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog_id}';
        INSERT INTO trading.counterparties SELECT catalog_id,id,2,code,'Synthetic party A revised',
          (SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog_id}'),'2020-01-01T00:00:00Z'
          FROM trading.counterparties WHERE catalog_id='{catalog_id}' AND id='{party['id']}' AND revision=1;
        INSERT INTO trading.counterparties SELECT '{catalog_id}','{future_id}',1,'future_party','Synthetic future party',max(publication_order),'2020-12-19T00:00:00Z' FROM trading.publications WHERE catalog_id='{catalog_id}';
        INSERT INTO market_data.catalogs VALUES('{foreign_catalog}','party_scope_{foreign_catalog.replace('-', '')}');
        INSERT INTO trading.publications VALUES('{foreign_catalog}',1,pg_current_xact_id());
        INSERT INTO trading.counterparties VALUES('{foreign_catalog}','{foreign_id}',1,'foreign_party','Synthetic foreign party',1,'2020-01-01T00:00:00Z');
        COMMIT;""")
    current = c.call('futures/catalog', context)
    assert next(p for p in current['counterparties'] if p['id'] == party['id'])['revision'] == 2
    old_catalog = c.call('futures/catalog', {**context, 'knowledgeMode': 'retained', 'contextId': retained})
    assert next(p for p in old_catalog['counterparties'] if p['id'] == party['id'])['revision'] == 1
    new_version = c.call('futures/book', {**body, 'operationId': str(uuid4()), 'counterpartyRevision': 2})
    state = c.call('futures/editor', {'context': context, 'tradeId': new_version['tradeId'], 'revision': None})['state']['booking']
    assert state['counterpartyRevision'] == 2 and state['counterparty'] == 'Synthetic party A revised'
    assert detail(1)['state']['booking']['counterparty'] == party['name']
    for selected in (future_id, foreign_id):
        c.call('futures/book', {**body, 'operationId': str(uuid4()), 'counterpartyId': selected}, expected=422)

    def runtime(sql):
        token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                      f"'https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{token}'); {sql} COMMIT;")
    payload = json.dumps({**booking(), 'counterpartyRevision': 2})
    runtime(f"""SELECT security.use_market_context('{catalog_id}','{retained}');SELECT security.use_trading_context('{catalog_id}','2020-12-18T16:00:00Z');
       DO $$ BEGIN PERFORM security.require_trade_parties('{catalog_id}','{payload}'::jsonb,'2020-12-18T15:00:00Z');
       RAISE EXCEPTION 'Expected unknown party version rejection'; EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'trade_party' THEN RAISE; END IF; END $$;""")
    assert runtime(f"SELECT count(*) FROM trading.counterparties WHERE catalog_id='{foreign_catalog}';") == '0'
    assert query("SELECT bool_and(relrowsecurity AND relforcerowsecurity) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='trading' AND c.relname IN('legal_entities','counterparties');") == 't'
    query(f"""DO $$ BEGIN INSERT INTO trading.trade_revisions SELECT (jsonb_populate_record(NULL::trading.trade_revisions,to_jsonb(r)||
      jsonb_build_object('revision',3,'previous_revision',2,'event_id','{uuid4()}','operation_id','{uuid4()}','counterparty_id','{foreign_id}'))).* FROM trading.trade_revisions r WHERE trade_id='{first['tradeId']}' AND revision=2;
      RAISE EXCEPTION 'Expected cross-catalog party FK rejection'; EXCEPTION WHEN foreign_key_violation THEN NULL; END $$;""")
    print('PASS exact party versions preserve old names, retained knowledge excludes later publications, future-effective/foreign parties reject and exact-version FKs/RLS hold', flush=True)
