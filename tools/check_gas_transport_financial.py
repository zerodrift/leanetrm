"""Qualify booked whole-rights Transport and independent ordinary fee cash."""
import json
from copy import deepcopy
from uuid import uuid4

from check_reference_editing import main
from gas_transport_financial_fixture import FinancialTransportFixture, fixture


def rejected(work, code):
    try:
        work()
    except RuntimeError as error:
        assert code in str(error), str(error)
    else:
        raise AssertionError('Expected ' + code)


def checks(query, manifest):
    f = FinancialTransportFixture(query, manifest)
    # Other products can inspect future valuation dates with an earlier knowledge
    # cutoff. An empty Transport scope must not impose its valuation restriction.
    future_at = '2198-12-19T16:00:00Z'
    future_context = f.capture(day=future_at[:10])
    from datetime import datetime
    from native_read_transport import captured_input
    assert datetime.fromisoformat(captured_input(query,future_context)['known_at']) < datetime.fromisoformat(future_at)
    def future_scope(trades='NULL'):
        return f.call(f"SELECT security.use_market_context('{f.catalog}','{future_context}');"
            f"SELECT security.use_trading_context('{f.catalog}','{future_at}','{future_at[:10]}');"
            f"SELECT security.require_gas_transport_financial_scope('{f.catalog}','{future_context}','{future_at}',ARRAY['{f.book}']::uuid[],{trades});")
    assert future_scope() == 'f'
    from gas_transport_financial_capacities import capacity_facts
    capacity_facts(f, rejected)
    assert f.valuation_reference != manifest['referenceRevisionId']
    # Both exact bundles and the final genuine package observation must be
    # certified and readable before missing financial behavior counts as RED.
    context = f.capture()
    proof = json.loads(f.call(f"SELECT security.use_market_context('{f.catalog}','{context}');"
        f"SELECT jsonb_build_object('operational',security.reference_version_visible('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'),"
        f"'valuation',security.reference_version_visible('{f.reference}','{f.valuation_reference}'),"
        f"'mark',security.market_fact_visible('{f.catalog}','{f.definition_revision}','{f.observation}'));"))
    assert proof == dict(operational=True, valuation=True, mark=True), proof
    assert query(f"SELECT reference_revision_id FROM trading.gas_transport_capacity_entitlements "
        f"WHERE(catalog_id,id)=('{f.catalog}','{f.entitlement}');") == manifest['referenceRevisionId']
    print('PASS genuine final whole-package mark uses a later exact valuation reference; retained entitlement keeps its original gas basis.', flush=True)
    booking = f.booking()
    rejected(lambda: f.book_financial({**booking, 'operationId': str(uuid4()), 'packageQuantity': 'ordinary_gas_quantity'}), 'gas_transport_financial_unit')
    empty_definition = f.empty_pricing_definition()
    rejected(lambda: f.book_financial({**booking, 'operationId': str(uuid4()), 'contextId': f.capture(), 'definitionRevisionId': empty_definition}), 'gas_transport_financial_source')
    outcome = f.book_financial(booking)
    # The old cut remains empty after booking; a fresh cut finds the agreement
    # only within the selected scope and retains the financial context refusal.
    assert future_scope() == 'f'
    future_context = f.capture(day=future_at[:10])
    assert future_scope(f"ARRAY['{uuid4()}']::uuid[]") == 'f'
    rejected(future_scope, 'gas_transport_financial_context')
    print('PASS future-date Transport absence respects exact scope and retained knowledge; a present financial agreement retains its context refusal.', flush=True)
    source = f.read_financial(outcome['tradeId'])
    assert source['terms']['valuationReferenceRevisionId'] == f.valuation_reference
    assert source['terms']['operationalReferenceRevisionId'] == manifest['referenceRevisionId']
    assert source['financialState']['id'] == booking['operationId']
    assert source['mark'] is None and source['unpricedReason']
    assert source['cash']['amount'] == '-12.5'
    assert f.book_financial(booking) == outcome
    print('PASS real booking/recovery retains one whole live rights state, explicit missing mark and independent signed fixed-fee cash.', flush=True)
    mark = f.mark(outcome['tradeId'], booking['operationId'])
    rejected(lambda: f.record_financial(f.alternate_mark(mark)), 'gas_transport_financial_mark')
    rejected(lambda: f.record_financial(mark, effective='2020-12-19T15:59:59Z'), 'gas_transport_financial_mark_time')
    context = f.capture()
    mark_operation = str(uuid4())
    from gas_transport_financial_managed import checkpoint, lifecycle_checkpoint, value
    checkpoint(f, outcome['tradeId'], 'read', context=context, reason='rights_mark_required')
    value(f, outcome['tradeId'], context=context, rate_ids=[], reason='rights_mark_required')
    checkpoint(f, outcome['tradeId'], 'command', operation=mark_operation, command=mark, context=context)
    selected = f.record_financial(mark, operation=mark_operation, context=context)
    assert selected['markId'] == mark_operation and selected['financialStateId'] == booking['operationId']
    assert f.record_financial(mark, operation=mark_operation, context=context) == selected
    ack_operation = str(uuid4())
    assert f.record_financial(mark, operation=ack_operation, context=False) == {**selected, 'changed': False}
    assert query(f"SELECT source_context_id IS NULL FROM trading.gas_transport_financial_commands WHERE(catalog_id,operation_id)=('{f.catalog}','{ack_operation}');") == 't'
    assert f.record_financial(mark, context=booking['contextId']) == {**selected, 'changed': False}
    assert query(f"SELECT source_context_id FROM trading.gas_transport_financial_marks WHERE(catalog_id,id)=('{f.catalog}','{mark_operation}');") == context
    priced = f.read_financial(outcome['tradeId'])
    assert priced['mark']['observationId'] == f.observation and priced['unpricedReason'] is None
    assert priced['mark']['sourceKnownAt'] < priced['mark']['knownAt']
    assert f.read_financial(outcome['tradeId'], context=context)['mark'] is None
    assert priced['cash'] == source['cash']
    value(f, outcome['tradeId'], rate_ids=f.rate_observations[f.valuation_at], mark=priced['mark'], expected='7.5')
    assert f.book_financial({**booking, 'operationId': str(uuid4())}) == {**outcome, 'changed': False}
    for key, replacement in [('observationId', str(uuid4())), ('financialStateId', str(uuid4())), ('quote', 'unknown_quote')]:
        wrong = {**mark, key: replacement, 'expectedPreviousMarkId': mark_operation}
        rejected(lambda: f.record_financial(wrong), 'gas_transport_financial_')
    print('PASS exact genuine signed mark adoption, source-before-publication lineage, no-copy money, old-cut unpriced and first/semantic receipt recovery.', flush=True)

    # Operating plan mutations do not change the financial whole-rights state.
    capacity = f.read(f.entitlement)
    plan_operation, plan = f.plan(capacity, 'synthetic_financial_independent_plan', 40)
    witness = f.hourly_witness(capacity, plan['claims'])
    # This package has a day-long limit; retain the actual idle final segment.
    for row in witness['intervals']:
        if row['through'].startswith('2020-12-20T02:'):
            row['through'] = '2020-12-21T00:00:00Z'
    f.record(plan, witness, operation=plan_operation)
    assert f.read_financial(outcome['tradeId'])['financialState']['id'] == booking['operationId']
    assert f.read_financial(outcome['tradeId'])['mark']['id'] == mark_operation
    revised = deepcopy(f.operational_definition)
    capacity = f.read(f.entitlement)
    revised.update(expectedStateVersion=capacity['stateVersion'], expectedPreviousId=f.capacity_version,
        evidence='Synthetic explicitly revised commercial capacity')
    revised['agreement'][0]['maximumPerHour'] = '90'
    revised_version = f.record(revised)['versionId']
    before_adoption = f.capture()
    amendment = dict(kind='adopt_capacity_version', tradeId=outcome['tradeId'], expectedFinancialStateId=booking['operationId'],
        capacityVersionId=revised_version, evidence='Synthetic explicit financial capacity adoption')
    amended = f.record_financial(amendment)
    initial_amended = amended
    current = f.read_financial(outcome['tradeId'])
    assert current['financialState']['capacityVersionId'] == revised_version and current['mark'] is None
    assert current['cash'] == source['cash']
    value(f, outcome['tradeId'], rate_ids=[], reason='rights_mark_required')
    assert f.read_financial(outcome['tradeId'], context=before_adoption)['mark']['id'] == mark_operation
    second_mark = f.mark(outcome['tradeId'], amended['financialStateId'])
    f.record_financial(second_mark)
    value(f, outcome['tradeId'], rate_ids=f.rate_observations[f.valuation_at], mark=f.read_financial(outcome['tradeId'])['mark'], expected='7.5')
    assert len(f.read_financial(outcome['tradeId'])['markHistory']) == 2
    assert f.record_financial(mark, context=False) == {**selected, 'changed': False}
    assert f.read_financial(outcome['tradeId'])['financialState']['id'] == amended['financialStateId']
    print('PASS operating plans leave the whole package unchanged; explicit version adoption invalidates only its old mark and equal-money re-adoption retains independent state provenance.', flush=True)

    from gas_transport_financial_seals import source_and_seals
    source_and_seals(f, booking, outcome, rejected)
    from gas_transport_financial_time import future_capacity, accept_future_capacity
    future_adoption = future_capacity(f, outcome['tradeId'], amended['financialStateId'], rejected)
    from gas_transport_financial_authority import authority, entry_authority, worker_validation
    worker_validation(f, rejected)
    authority(f, outcome['tradeId'], rejected)
    entry_authority(f)
    amended = accept_future_capacity(f, future_adoption)

    policy = f.expiry_policy()
    f.at = '2020-12-21T23:59:59.999999Z'
    expiry_at = '2020-12-21T00:00:00Z'
    expiry_context = f.capture()
    pending = f.read_financial(outcome['tradeId'], context=expiry_context, at=expiry_at)
    assert pending['financialState']['status'] == 'live' and pending['unpricedReason'] == 'expiry_transition_required'
    close = dict(kind='expire', tradeId=outcome['tradeId'], expectedFinancialStateId=amended['financialStateId'],
        policyId=policy, policyRevision=1, evidence='Synthetic explicit accepted contractual expiry')
    rejected(lambda: f.record_financial(close, effective='2020-12-20T23:59:59Z', at=expiry_at), 'gas_transport_financial_expiry')
    closed = lifecycle_checkpoint(f, outcome['tradeId'], amended['financialStateId'], policy,
        context=expiry_context, at=expiry_at)
    final = f.read_financial(outcome['tradeId'], at=expiry_at)
    assert final['financialState']['status'] == 'closed' and final['financialState']['previousId'] == amended['financialStateId']
    assert final['financialState']['id'] == final['expiry']['id'] == closed['financialStateId']
    assert final['mark'] is None and final['unpricedReason'] is None and final['cash'] == source['cash']
    checkpoint(f, outcome['tradeId'], 'read', at=expiry_at, status='closed')
    value(f, outcome['tradeId'], rate_ids=f.rate_observations[expiry_at], at=expiry_at, expected='-12.5')
    assert f.record_financial(amendment, context=False) == {**initial_amended, 'changed': False}
    assert f.read_financial(outcome['tradeId'], at=expiry_at)['financialState']['status'] == 'closed'
    assert f.read_financial(outcome['tradeId'], context=expiry_context, at=expiry_at)['financialState']['status'] == 'live'
    print('PASS explicit exact-Through manual expiry seals zero-rights state, preserves unpaid cash and old knowledge, and old acknowledgements cannot reopen it.', flush=True)
    from gas_transport_financial_expiry_ack import accepted_expiry
    accepted_expiry(f, closed, rejected)
    from gas_transport_financial_cash import provider_and_cash
    provider_and_cash(f, outcome, rejected)
    from gas_transport_financial_cross_date import cross_date
    cross_date(f)
    from gas_transport_financial_eligibility import eligibility
    eligibility(f, rejected)


if __name__ == '__main__':
    main(checks, fixture_factory=fixture)
