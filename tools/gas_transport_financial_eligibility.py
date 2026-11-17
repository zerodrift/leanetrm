"""Native source admission agrees with the pricer's strict live-point boundary."""
import json
from uuid import uuid4


def _source(f, customer_expiry, provider_expiry):
    definition = str(uuid4())
    observations = [str(uuid4()), str(uuid4())]
    sql = ['BEGIN;SELECT pg_advisory_xact_lock(730849102);',
        f"INSERT INTO market_data.revisions VALUES('{f.catalog}','{definition}','{f.reference}','{f.valuation_reference}','draft',0);"]
    for table in ['revision_periods', 'contracts', 'curves', 'series', 'curve_members']:
        expiry = (f",'expiry_at',CASE WHEN r.id='{f.contract}'::uuid THEN '{customer_expiry}'::timestamptz "
            f"ELSE '{provider_expiry}'::timestamptz END") if table == 'contracts' else ''
        sql.append(f"INSERT INTO market_data.{table} SELECT(jsonb_populate_record(NULL::market_data.{table},"
            f"to_jsonb(r)||jsonb_build_object('revision_id','{definition}'{expiry}))).* FROM market_data.{table} r "
            f"WHERE(catalog_id,revision_id)=('{f.catalog}','{f.definition_revision}');")
    sql.append(f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{f.catalog}','{definition}');")
    for original, identifier in zip([f.observation, f.provider_observation], observations):
        sql.append(f"INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,"
            f"to_jsonb(o)||jsonb_build_object('id','{identifier}','definition_revision_id','{definition}','source_event_id','{identifier}'))).* "
            f"FROM market_data.observations o WHERE(catalog_id,id)=('{f.catalog}','{original}');")
    sql.append('COMMIT;')
    f.query('\n'.join(sql))
    f.manifest.setdefault('inputMarkets', []).append(dict(revisionId=definition, frames=[dict(at=f.valuation_at,
        frozenObservationIds=[], observationIds=observations,
        curveContracts={'transport_rights': [f.contract], 'provider_transport_rights': [f.provider_contract]})]))
    return definition, observations


def _booking(f, definition, key):
    book, capacity, version = f.new_capacity(key + '_capacity')
    return {**f.booking(key=key, fee='0'), 'bookId': book, 'contextId': f.capture(),
        'entitlementId': capacity['entitlementId'], 'capacityVersionId': version['versionId'], 'definitionRevisionId': definition}


def booking_eligibility(f, rejected):
    previous = f.at, f.valuation_at
    try:
        f.at, f.valuation_at = '2020-12-19T23:59:59.999999Z', '2020-12-19T16:00:00Z'
        expired, _ = _source(f, f.valuation_at, f.valuation_at)
        assert f.query(f"SELECT bool_and(expiry_at<='{f.valuation_at}'::timestamptz) FROM market_data.contracts WHERE(catalog_id,revision_id)=('{f.catalog}','{expired}');") == 't'
        rejected(lambda: f.book_financial(_booking(f, expired, 'synthetic_expired_only_financial_source')), 'gas_transport_financial_source')

        # One microsecond before expiry is genuinely live, even though the
        # service package itself remains available well beyond that instant.
        expiry = '2020-12-19T16:00:00.000001Z'
        definition, observations = _source(f, expiry, expiry)
        body = _booking(f, definition, 'synthetic_live_microsecond_source')
        booked = f.book_financial(body)
        mark = {**f.mark(booked['tradeId'], body['operationId']),
            'marketDefinitionRevisionId': definition, 'observationId': observations[0]}
        accepted = f.record_financial(mark)
        source = f.read_financial(booked['tradeId'])
        from gas_transport_financial_managed import value
        value(f, booked['tradeId'], rate_ids=[], mark=source['mark'], expected='20')
        f.valuation_at = expiry
        rejected(lambda: f.book_financial(_booking(f, definition, 'synthetic_exact_expiry_financial_source')), 'gas_transport_financial_source')
        assert f.record_financial(mark, effective='2020-12-19T16:00:00Z', context=False) == {**accepted, 'changed': False}
        assert f.read_financial(booked['tradeId'], at='2020-12-19T16:00:00Z')['mark']['id'] == accepted['markId']
        print('PASS financial booking requires a strictly live compatible source: equality refuses, one microsecond before prices20, and an authentic old mark acknowledgement never rewinds or rechecks new-source expiry.', flush=True)
    finally:
        f.at, f.valuation_at = previous


def mark_eligibility(f, rejected):
    previous = f.at, f.valuation_at
    try:
        f.at, f.valuation_at = '2020-12-19T23:59:59.999999Z', '2020-12-19T16:00:00Z'
        definition, observations = _source(f, '2020-12-21T00:00:00Z', f.valuation_at)
        body = _booking(f, definition, 'synthetic_mixed_expiry_financial_source')
        booked = f.book_financial(body)
        invalid = {**f.mark(booked['tradeId'], body['operationId']), 'marketDefinitionRevisionId': definition,
            'curveCode': 'provider_transport_rights', 'contractId': f.provider_contract,
            'seriesId': f.provider_series, 'observationId': observations[1]}
        rejected(lambda: f.record_financial(invalid), 'gas_transport_financial_mark')
        assert f.read_financial(booked['tradeId'])['markHistory'] == []
        valid = {**f.mark(booked['tradeId'], body['operationId']),
            'marketDefinitionRevisionId': definition, 'observationId': observations[0]}
        f.record_financial(valid)
        source = f.read_financial(booked['tradeId'])
        from gas_transport_financial_managed import value
        value(f, booked['tradeId'], rate_ids=[], mark=source['mark'], expected='20')
        print('PASS a mixed live/expired definition remains bookable, but exact expired-member adoption refuses atomically and its independently selected live member prices20.', flush=True)
    finally:
        f.at, f.valuation_at = previous


def eligibility(f, rejected):
    booking_eligibility(f, rejected)
    mark_eligibility(f, rejected)
