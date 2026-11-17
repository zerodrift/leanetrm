"""An explicit market business date is independent of the UTC valuation date."""
import json
from uuid import uuid4


def cross_date(f):
    previous = f.book, f.entitlement, f.capacity_version, f.at, f.valuation_at
    try:
        day, at = '2020-12-20', '2020-12-19T23:00:00Z'
        mark_id, rate_ids = str(uuid4()), []
        sql = [f"INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||"
            f"jsonb_build_object('id','{mark_id}','source_event_id','{mark_id}','source_revision_id','1','corrects_id',NULL,'observed_at','{at}','received_at',clock_timestamp(),'value',20))).* "
            f"FROM market_data.observations o WHERE(o.catalog_id,o.id)=('{f.catalog}','{f.observation}');"]
        for original in f.rate_observations['2020-12-19T16:00:00Z']:
            identifier = str(uuid4())
            rate_ids.append(identifier)
            sql.append(f"INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||"
                f"jsonb_build_object('id','{identifier}','source_event_id','{identifier}','observed_at','{at}','received_at',clock_timestamp()))).* "
                f"FROM market_data.observations o WHERE(o.catalog_id,o.id)=('{f.catalog}','{original}');")
        f.query('\n'.join(sql))
        from gas_transport_financial_managed import checkpoint, value
        cases = [('zero', '0', None, '20'), ('due', '12.5', at, '7.5'),
            ('outstanding', '12.5', '2020-12-23T16:00:00Z', None)]
        for label, amount, due, expected in cases:
            f.at = '2020-12-19T23:59:59.999999Z'
            f.book, definition, version = f.new_capacity('synthetic_explicit_business_day_' + label,
                name='Synthetic explicit business date ' + label)
            f.entitlement, f.capacity_version = definition['entitlementId'], version['versionId']
            f.valuation_at, f.at = at, '2020-12-20T23:59:59.999999Z'
            context = f.capture()
            proof = json.loads(f.call(f"SELECT security.use_market_context('{f.catalog}','{context}');"
                f"SELECT security.use_trading_context('{f.catalog}','{at}','{day}');"
                f"SELECT jsonb_build_object('dayMatches',security.trading_context_day_matches('{f.catalog}','{context}','{at}'),"
                f"'entitlementVisible',security.trading_visible('{f.catalog}',{version['stateVersion']},'{f.effective}'));"))
            assert proof == dict(dayMatches=True, entitlementVisible=True), proof
            body = {**f.booking(key='synthetic_explicit_business_day_' + label), 'contextId': context,
                'fixedFeeAmount': amount, 'feeDueAt': due}
            booked = f.book_financial(body)
            current_context = f.capture()
            row = f.read_financial(booked['tradeId'], context=current_context, business_date=day)
            assert row['effectiveOn'] == day and row['asOf'].startswith('2020-12-19T23:00:00')
            assert f.query(f"SELECT trade_date=(executed_at AT TIME ZONE 'UTC')::date FROM trading.booking_facts WHERE(catalog_id,trade_id)=('{f.catalog}','{booked['tradeId']}');") == 't'
            if label == 'zero':
                checkpoint(f, booked['tradeId'], 'read', context=current_context, business_date=day, reason='rights_mark_required')
                value(f, booked['tradeId'], context=current_context, rate_ids=[], reason='rights_mark_required')
            mark = {**f.mark(booked['tradeId'], body['operationId']), 'observationId': mark_id}
            checkpoint(f, booked['tradeId'], 'command', context=current_context, business_date=day,
                operation=str(uuid4()), command=mark)
            current_context = f.capture()
            priced = f.read_financial(booked['tradeId'], context=current_context, business_date=day)
            if expected is not None:
                value(f, booked['tradeId'], context=current_context, rate_ids=[], mark=priced['mark'], expected=expected)
            else:
                value(f, booked['tradeId'], context=current_context, rate_ids=rate_ids, mark=priced['mark'], refusal='rate_context')
        print('PASS explicit business date20 with UTC valuation19T23: zero-fee rights20, due-fee complete value7.5 without rates, and outstanding-fee rate_context refusal preserve D359; D29 booking dates retain actual server UTC.', flush=True)
    finally:
        f.book, f.entitlement, f.capacity_version, f.at, f.valuation_at = previous
