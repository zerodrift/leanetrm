"""In-term gas admission, recorded payments and the owner-only load, against the synthetic in-term demo."""
from copy import deepcopy
import json
import re
from uuid import uuid4
from assistant_context import check_guard, migration_body
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking
from check_booked_side_programs import runtime_query, capture_context
from gas_in_term_fixture import CLOSE, POINT, custom_curves, trade

STARTED = {'whole_gas_day_delivery_v1', 'accrued_payment_v1'}


def run_checks(url, query, manifest):
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    catalog = manifest['catalogId']
    frame = initial_frame(c.call('workspace'), '2026-11-06')
    def publish(graph):
        c.call('profile', {'profile': 'editor'})
        saved = c.call('custom-curves/save', {'requestId': str(uuid4()), 'snapshotId': frame['id'], 'definition': graph, 'layout': {}})
        c.call('profile', {'profile': 'approver'})
        return c.call('custom-curves/publish', {'requestId': str(uuid4()), 'id': saved['id'], 'version': saved['version'], 'generation': saved['generation']})
    curve = custom_curves(publish, {s['code']: s for s in c.call('custom-curves')['sources']}, catalog)
    c.call('profile', {'profile': 'trader'})
    context = dict(snapshotId=frame['id'], convention='physical_gas_fixed_quantity_v4', knowledgeMode='current')
    gas = c.call('gas/catalog', context)
    location = next(p for p in gas['locations'] if p['deliveryPoint'] == POINT)
    body = trade(context, location, gas['books'], gas['gasSetupId'], curve)
    encoded = json.dumps(body).replace("'", "''")
    def sql_value(statement, **options):
        return runtime_query(query, manifest, statement, **options).splitlines()[-1]
    def refused(statement, code, **options):
        try: runtime_query(query, manifest, statement, **options)
        except RuntimeError as error: assert code in str(error), error
        else: raise AssertionError(f'Accepted instead of {code}')

    # (a) The ticket stages the started composition's programs, then still refuses its booking.
    started = c.call('gas/composition/book', body, expected=422)
    assert started['code'] == 'gas_delivery_lifecycle', started
    assert query(f"SELECT command_hash=sha256(convert_to('{encoded}'::jsonb::text,'UTF8')) FROM trading.side_schedule_requests WHERE(catalog_id,request_id)=('{catalog}','{body['operationId']}');") == 't'
    refused(f"SELECT security.book_gas_composition('{catalog}','{encoded}'::jsonb,'{CLOSE}');", 'gas_delivery_lifecycle')
    print('PASS a started gas composition is staged by the ticket and still refused by the booking operation', flush=True)

    # (b) The administrator load is owner-only and admits the same programs through the same writer.
    load = f"SELECT security.load_gas_composition_in_term('{catalog}','{encoded}'::jsonb,'{CLOSE}');"
    refused(load, 'permission denied for function load_gas_composition_in_term')
    assert query("SELECT has_function_privilege('openetrm_runtime','security.load_gas_composition_in_term(uuid,jsonb,timestamptz)','EXECUTE'),"
                 "has_function_privilege('openetrm_runtime','security.write_gas_composition(uuid,jsonb,timestamptz,boolean)','EXECUTE');") == 'f|f'
    # The administrator binds the booking actor's request context in an owner session, then loads and
    # publishes the staged programs in one transaction, as the ticket's booking transaction does.
    def owner(statement):
        token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_owner',60);")
        return json.loads(next(line for line in query(f"BEGIN;SELECT security.use_context('{token}');{statement}COMMIT;").splitlines() if line.startswith('{')))
    loaded = owner(load + f"SELECT security.publish_operation_side_schedules('{catalog}','{body['operationId']}');")
    assert owner(load) == loaded
    assert lifecycles(query, catalog, loaded['instrumentId']) == {0: {'whole_gas_day_delivery_v1'}, 1: {'accrued_payment_v1'}}
    trade_id = loaded['tradeId']
    print('PASS the owner-only in-term load admits the started composition once, with whole gas-day and accrued payment programs', flush=True)

    # A forward composition booked through the ticket now selects the started-delivery lifecycles too.
    forward = deepcopy(body); forward.update(operationId=str(uuid4()), profile='fixed_gas_composition_v1')
    forward['physicalSides'][0].update(deliveryFrom='2026-12-01', deliveryTo='2027-01-01')
    forward['cashSides'] = [dict(code='payment', role='consideration', direction='pay', source='gas', stage='delivery', price='3.5', tariffCode=None, paymentAt='2027-01-10T00:00:00Z')]
    other = c.call('gas/composition/book', forward)
    assert lifecycles(query, catalog, other['instrumentId']) == {0: {'whole_gas_day_delivery_v1'}, 1: {'accrued_payment_v1'}}
    # A non-composition booking keeps its own future lifecycles.
    standard = initial_frame(c.call('workspace'), '2020-12-18')
    swap_context = {'snapshotId': standard['id'], 'convention': 'independent_side_financial_swap_v2', 'knowledgeMode': 'current'}
    index = next(i for i in c.call('swaps/catalog', swap_context)['indices'] if i['curve'] == 'base_forward')
    rule = {'frequency': 'monthly', 'rule': 'days_after_period_end', 'days': 18, 'dayOfMonth': None, 'dayBasis': 'calendar_days', 'shortMonth': None, 'adjustment': 'unadjusted', 'calendar': None, 'implementation': 'monthly_payment_date_rules_v1'}
    leg = {'currency': index['currencyCode'], 'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01', 'paymentSchedule': rule}
    swap = c.call('swaps/book', {**swap_context, **trade_booking(), 'operationId': str(uuid4()), 'bookId': gas['books'][0]['id'], 'definitionRevisionId': index['definitionRevisionId'],
                                 'commodity': index['commodity'], 'quantity': '100', 'fixedCoefficient': '-1', 'discounting': False, 'fixed': {'price': '3', **leg},
                                 'floating': {'spread': '0', 'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'curve': index['curve'], 'index': index['index'],
                                              **leg, 'resetFrequency': 'monthly', 'averagingMethod': 'none'}})
    swap_lifecycles = set().union(*lifecycles(query, catalog, swap['instrumentId']).values())
    assert swap_lifecycles and not swap_lifecycles & STARTED, swap_lifecycles
    print('PASS ticket compositions retain started-delivery lifecycles while a swap keeps its own', flush=True)

    # (c) Payments are idempotent by operation and by fact; a changed amount is a later-known revision.
    capture_context(query, manifest, '2026-11-06')
    # A historical view between payment and recording sees a payment only once its receipt is known.
    business = '2026-10-20'
    before_receipts = capture_context(query, manifest, business).splitlines()[-1]
    record = lambda payment: f"SELECT security.record_gas_cash_payment('{catalog}','{json.dumps(payment)}'::jsonb,'{CLOSE}');"
    def september(amount, last):
        return dict(operationId=str(uuid4()), tradeId=trade_id, expectedRevision=1, slot=1, businessKey='synthetic-2026-09-remittance', paidAt='2026-10-10T00:00:00Z',
                    currency='usd', amount=amount, allocations=[dict(occurrence=n, amount='-290' if n < 29 else last) for n in range(30)])
    partial = september('-8600', '-190')
    original = json.loads(sql_value(record(partial)))
    assert json.loads(sql_value(record(partial))) == original and original['revision'] == 1
    rows = lambda: query(f"SELECT count(*) FROM trading.gas_cash_payments WHERE(catalog_id,trade_id)=('{catalog}','{trade_id}');")
    assert rows() == '1'
    capture_context(query, manifest, '2026-11-06')
    corrected = september('-8700', '-290')
    revision = json.loads(sql_value(record(corrected)))
    assert (revision['revision'], revision['previousId']) == (2, original['paymentId']) and revision['paymentId'] != original['paymentId']
    assert json.loads(sql_value(record({**corrected, 'operationId': str(uuid4())})))['paymentId'] == revision['paymentId'] and rows() == '2'
    # A retried no-op keeps its first outcome after a later revision of the same business key.
    same = {**corrected, 'operationId': str(uuid4())}
    assert json.loads(sql_value(record(same)))['paymentId'] == revision['paymentId']
    later = json.loads(sql_value(record(september('-8650', '-240'))))
    assert later['revision'] == 3 and json.loads(sql_value(record(same)))['paymentId'] == revision['paymentId'] and rows() == '3'
    restored = json.loads(sql_value(record(september('-8700', '-290'))))
    assert (restored['revision'], restored['previousId']) == (4, later['paymentId']) and rows() == '4'
    capture_context(query, manifest, '2026-11-06')
    after_receipts = capture_context(query, manifest, business).splitlines()[-1]
    def visible(context):
        return sql_value(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{business}T00:00:00Z');"
                         f"SELECT count(*) FROM trading.gas_cash_payments WHERE(catalog_id,trade_id)=('{catalog}','{trade_id}');")
    assert (visible(before_receipts), visible(after_receipts)) == ('0', '4')
    known = query(f"SELECT bool_and(later.known_from>earlier.known_from) FROM trading.gas_cash_payments p JOIN trading.knowledge_receipts later ON(later.catalog_id,later.publication_order)=(p.catalog_id,p.publication_order)"
                  f" JOIN trading.gas_cash_payments o ON(o.catalog_id,o.id)=(p.catalog_id,p.previous_id) JOIN trading.knowledge_receipts earlier ON(earlier.catalog_id,earlier.publication_order)=(o.catalog_id,o.publication_order)"
                  f" WHERE(p.catalog_id,p.id)=('{catalog}','{revision['paymentId']}');")
    assert known == 't', known
    assert query(f"SELECT count(*),sum(amount) FROM trading.gas_cash_payment_allocations WHERE(catalog_id,payment_id)=('{catalog}','{revision['paymentId']}');") == '30|-8700'
    refused(record({**partial, 'amount': '-8700'}), 'gas_payment_conflict')
    for change, code in [(dict(amount='-8700', allocations=[dict(occurrence=0, amount='-8600')]), 'gas_payment_amount'),
                         (dict(amount='290', allocations=[dict(occurrence=0, amount='290')]), 'gas_payment_amount'),
                         (dict(currency='eur'), 'gas_payment_currency'),
                         (dict(amount='-290', allocations=[dict(occurrence=80, amount='-290')]), 'gas_payment_occurrence'),
                         (dict(amount='-290', allocations=[dict(occurrence=122, amount='-290')]), 'gas_payment_occurrence'),
                         (dict(slot=0), 'gas_payment_slot'), (dict(paidAt='2026-11-07T00:00:00Z'), 'gas_payment_input'), (dict(paidAt='2026-10-10'), 'gas_payment_input')]:
        refused(record({**september('-8700', '-290'), **change}), code)
    print('PASS gas payments: exact retries, one row per fact, a later-known revision, conserved allocations and explicit refusals', flush=True)

    # (d) A payment can allocate only to its own membership's retained occurrences.
    other_schedule, other_row = query(f"SELECT s.schedule_id,r.ordinal FROM trading.instrument_side_schedules s JOIN trading.side_schedule_rows r USING(catalog_id,schedule_id)"
                                      f" WHERE(s.catalog_id,s.instrument_id,s.slot,r.local_key)=('{catalog}','{other['instrumentId']}',1,'payment/0/value');").split('|')
    copy = f"INSERT INTO trading.gas_cash_payments SELECT catalog_id,'{uuid4()}',trade_id,trade_revision,{{instrument}},slot,'synthetic-foreign',1,NULL,paid_at,reference_catalog_id,reference_revision_id,currency_code,-290,'{uuid4()}',actor_id,publication_order FROM trading.gas_cash_payments WHERE id='{revision['paymentId']}' RETURNING id"
    for statement, code in [(copy.format(instrument=f"'{other['instrumentId']}'") + ';', 'gas_cash_payments_catalog_id_trade_id_trade_revision_instr_fkey'),
                            (f"WITH p AS({copy.format(instrument='instrument_id')}) INSERT INTO trading.gas_cash_payment_allocations SELECT '{catalog}',id,0,'{other_schedule}',{other_row},-290 FROM p;SET CONSTRAINTS ALL IMMEDIATE;", 'gas_payment_allocation')]:
        try: query('BEGIN;' + statement + 'ROLLBACK;')
        except RuntimeError as error: assert code in str(error), error
        else: raise AssertionError(f'A foreign occurrence was accepted instead of {code}')
    assert query("SELECT has_table_privilege('openetrm_runtime','trading.gas_cash_payments','INSERT'),has_table_privilege('openetrm_runtime','trading.gas_cash_payment_allocations','INSERT');") == 'f|f'
    print('PASS gas payment allocations name only their own trade, membership and retained occurrence', flush=True)


def lifecycles(query, catalog, instrument):
    rows = query(f"SELECT s.slot,r.parameters->>'lifecycle' FROM trading.instrument_side_schedules s JOIN trading.side_schedule_rows r USING(catalog_id,schedule_id)"
                 f" WHERE(s.catalog_id,s.instrument_id,r.operation)=('{catalog}','{instrument}','output_guard') GROUP BY 1,2;")
    found = {}
    for line in rows.splitlines():
        slot, lifecycle = line.split('|'); found.setdefault(int(slot), set()).add(lifecycle)
    return found


def upgrade_checks(query, migration):
    """Before 0215: pinned helpers refuse drift, and stored future-lifecycle rows are neither changed nor re-validated."""
    text = migration.read_text()
    signatures = re.findall(r"^\s*\('([^']+)','[a-f0-9]{64}'", text, re.MULTILINE)
    assert len(signatures) == 7
    changes = [(change, signature) for signature in signatures for change in (
        f"ALTER FUNCTION {signature} SET work_mem='64kB';", f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
        f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
        f"DO $drift$ DECLARE body text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(body,'AS $function$','AS $function$ /* synthetic drift */');END$drift$;")]
    check_guard(query, text, '0215', 'in_term_upgrade', changes)
    try: query("BEGIN;CREATE FUNCTION security.write_gas_composition(uuid,jsonb,timestamptz,boolean) RETURNS jsonb LANGUAGE sql AS 'SELECT NULL::jsonb';" + migration_body(text) + 'ROLLBACK;')
    except RuntimeError as error: assert 'in_term_upgrade: unexpected private writer' in str(error), error
    else: raise AssertionError('A preexisting private writer was admitted')
    # A composition stored before 0215: its physical and cash guards keep their future lifecycles.
    schedule = '7f000000-0000-0000-0000-000000000215'
    def guard(ordinal, key, lifecycle, measure, grid):
        parameters = json.dumps(dict(lifecycle=lifecycle, measure=measure, pricePolicy='selected_context_v1', averaging='none', firstFixingAt='none',
                                     paymentRule='explicit_payment_date_v1', unadjustedPayment='none'))
        occurrence = "NULL,NULL" if grid else "'payment/0','synthetic-terms'"
        return (f"INSERT INTO trading.side_schedule_rows(catalog_id,schedule_id,ordinal,local_key,role,operation,operation_version,output_kind,currency_catalog_id,currency_revision_id,currency_code,"
                f"occurrence_key,terms_digest,parameters,grid_ordinal) VALUES('40000000-0000-0000-0000-000000000001','{schedule}',{ordinal},'{key}','root','output_guard',1,'money',"
                f"'10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001','usd',{occurrence},'{parameters}',{0 if grid else 'NULL'});")
    stored = f"(SELECT jsonb_agg(to_jsonb(r) ORDER BY r.ordinal) FROM trading.side_schedule_rows r WHERE r.schedule_id='{schedule}')"
    query("BEGIN;SET LOCAL session_replication_role=replica;" + guard(0, 'gas/0/value', 'future_gas_delivery_v1', 'value_excluding_overdue_v1', True) +
          guard(1, 'payment/0/value', 'undue_payment_v1', 'present_value_v1', False) + f"CREATE TEMP TABLE in_term_before AS SELECT {stored} AS value;SET LOCAL session_replication_role=origin;" +
          migration_body(text) + f"""DO $check$ BEGIN
 IF (SELECT value FROM in_term_before) IS DISTINCT FROM {stored} OR (SELECT array_agg(parameters->>'lifecycle' ORDER BY ordinal) FROM trading.side_schedule_rows WHERE schedule_id='{schedule}')<>ARRAY['future_gas_delivery_v1','undue_payment_v1']
 THEN RAISE EXCEPTION 'A stored future-lifecycle composition changed during the in-term upgrade';END IF;
END $check$;ROLLBACK;""")
    print('PASS in-term upgrade: seven helper bodies, owners, ACLs and configurations are pinned; stored future-lifecycle programs are left unchanged', flush=True)
