"""Qualify additive time-fee installation over immutable fixed-fee service facts."""
import json
from pathlib import Path
from uuid import uuid4

from check_reference_editing import main
from seed_fixture_market import load_fixture
from gas_park_loan_fixture import ParkLoanFixture, fixture, literal, rejected


def run():
    prepared = {}
    root = Path(__file__).resolve().parents[1]

    def state(query, catalog):
        tables = ['gas_park_loan_instruments', 'gas_park_loan_trades', 'gas_park_loan_advances', 'gas_park_loan_returns',
            'gas_park_loan_commands', 'gas_owned_accounts', 'gas_owned_evidence', 'gas_owned_positions', 'gas_owned_lots',
            'gas_owned_loans', 'gas_owned_repayments', 'gas_owned_consumptions', 'gas_owned_fees', 'gas_owned_fee_payments',
            'gas_owned_commands', 'gas_owned_position_quantities', 'gas_owned_consumption_quantities', 'gas_owned_loan_quantities',
            'gas_loan_fee_terms', 'gas_owned_action_bindings', 'publications', 'knowledge_receipts', 'booking_facts',
            'trade_references', 'instrument_references']
        return query('SELECT jsonb_object_agg(name,digest ORDER BY name) FROM(' + ' UNION ALL '.join(
            "SELECT '" + table + "'::text name,(SELECT md5(coalesce(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text),'[]')::text) "
            f"FROM trading.{table} x WHERE x.catalog_id='{catalog}') digest" for table in tables) + ')s;')

    def before(query, migration):
        if migration.name != '0350_gas_park_loan_balance_fee.sql':
            return
        from assistant_context import migration_body
        signatures = ['security.book_gas_park_loan(uuid,jsonb,timestamptz)',
            'security.read_gas_park_loan(uuid,uuid,uuid,timestamptz)',
            'security.record_gas_park_loan(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,boolean)',
            'trading.gas_park_loan_fact(jsonb)',
            'security.gas_park_loan_projection_fence(uuid,uuid,timestamptz,timestamptz,uuid[],uuid[])']
        for signature in signatures:
            for change in [f"ALTER FUNCTION {signature} {setting};" for setting in [
                    'STRICT', 'LEAKPROOF', 'PARALLEL SAFE', "SET work_mem='64kB'", 'OWNER TO openetrm_runtime']] + [
                    f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
                    f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN "
                    "EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic source drift */ ');END$$;"]:
                rejected(lambda: query('BEGIN;' + change + migration_body(migration.read_text()) + 'ROLLBACK;'), 'gas_balance_fee_upgrade')
        query('BEGIN;SET LOCAL search_path=security,trading,public;' + migration_body(migration.read_text()) + 'ROLLBACK;')
        print('PASS balance-fee upgrade refuses changed producer/probe source, actor boundary, settings, owner or ACL.', flush=True)
        reference, market, security, manifest = fixture(root)
        load_fixture(query,reference,market,security,manifest)
        f = ParkLoanFixture(query, manifest)
        owned, book = f.account('retained_fixed_fee')
        for action in ['gas.loan.borrow', 'gas.loan.repay', 'gas.cash.record_payment']:
            f.bind(owned, action)
        f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
        body = dict(operationId=str(uuid4()), profile='physical_gas_park_loan_v1', convention='physical_gas_fixed_quantity_v4',
            knowledgeMode='current', contextId=f.capture(), snapshotId=str(uuid4()), bookId=book,
            definitionRevisionId=manifest['revisionId'], legalEntityId=f.entity, legalEntityRevision=1,
            counterpartyId=f.party, counterpartyRevision=1, ownedAccountId=owned, claimAccountId=None,
            serviceKind='loan', perspective='customer', quantity='720', advanceAt='2020-12-20T10:00:00Z',
            returnAt='2020-12-21T12:00:00Z', fixedFeeAmount='-12.5', feeDirection='pay', feeDueAt='2020-12-22T12:00:00Z',
            titlePolicy='fungible_gas_title_transfer_return_v1', returnPolicy='same_quantity_partial_returns_v1',
            returnTiming='accepted_advance_then_returns_v1', carrying='nominal_mark_v1')
        booked = json.loads(f.call(f"SELECT security.book_gas_park_loan('{f.catalog}',{literal(body)},'{f.at}')::text;"))
        trade = booked['tradeId']
        f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'

        def read(context=None):
            context = context or f.capture()
            return json.loads(f.call(f"SELECT security.use_market_context('{f.catalog}','{context}');"
                f"SELECT security.use_trading_context('{f.catalog}','{f.at}');"
                f"SELECT security.read_gas_park_loan('{f.catalog}','{trade}','{context}','{f.at}')::text;"))

        def command(action, value, *, operation=None, context=None):
            return json.loads(f.call(f"SELECT security.record_gas_park_loan('{f.catalog}','{operation or uuid4()}',"
                f"'{trade}','{action}',{literal(value)},'{f.at}','{context or f.capture()}',false)::text;"))

        title = f.evidence(owned, 'retained_title', 'title_receipt', '720', '2020-12-20T10:00:00Z')
        agreement = f.evidence(owned, 'retained_agreement', 'loan_agreement', '720', '2020-12-20T10:00:00Z')
        advance_body = dict(expectedVersion=read()['stateVersion'], agreementEvidenceId=agreement, evidenceId=title,
            claimEvidenceId=None, lotId=None, reservationId=None, effectiveAt='2020-12-20T10:00:00Z')
        operation, context = str(uuid4()), f.capture()
        advanced = command('advance', advance_body, operation=operation, context=context)
        old_context = f.capture()
        evidence = f.evidence(owned, 'retained_partial', 'loan_return', '270', '2020-12-20T12:00:00Z')
        command('return', dict(expectedVersion=read()['stateVersion'], businessKey='retained_partial', evidenceId=evidence,
            lotId=advanced['stockId'], reservationId=None, quantity='270', effectiveAt='2020-12-20T12:00:00Z'))
        command('fee_payment', dict(expectedVersion=read()['stateVersion'], feeId=advanced['feeId'], businessKey='retained_bank',
            expectedRevision=0, paidAt='2020-12-20T14:00:00Z', amount='-5', evidence='Synthetic retained bank payment'))
        current = read()
        prepared.update(f=f, manifest=manifest, owned=owned, book=book, trade=trade, body=body, booked=booked,
            advanced=advanced, advance_body=advance_body, operation=operation, context=context, old_context=old_context,
            read=read, command=command, current=current, digest=state(query, f.catalog))

    def loaded(_):
        return '', '', '', prepared['manifest']

    def verify(query, _):
        f = prepared['f']; read = prepared['read']; command = prepared['command']
        assert state(query, f.catalog) == prepared['digest'], 'Installation changed retained principal, fixed fee, bank allocation, alias or receipt facts.'
        assert query('SELECT count(*) FROM trading.gas_park_loan_fee_terms;') == '0'
        assert command('advance', prepared['advance_body'], operation=prepared['operation'], context=prepared['context']) == prepared['advanced']
        current = read()
        assert {k: v for k, v in current.items() if k not in ['contextId', 'knownAt', 'authorityUntil']} == {
            k: v for k, v in prepared['current'].items() if k not in ['contextId', 'knownAt', 'authorityUntil']}
        assert current['implementation'] == 'physical_gas_park_loan_v1' and 'balanceFee' not in current['terms']
        assert current['fee'] == dict(amount='-12.5', paid='-5', unpaid='-7.5', dueAt='2020-12-22T12:00:00+00:00')
        historical = read(prepared['old_context'])
        assert historical['balances']['outstandingPrincipal'] == '720' and historical['fee']['paid'] == '0'
        evidence = f.evidence(prepared['owned'], 'retained_final', 'loan_return', '450', '2020-12-20T15:00:00Z')
        command('return', dict(expectedVersion=read()['stateVersion'], businessKey='retained_final', evidenceId=evidence,
            lotId=prepared['advanced']['stockId'], reservationId=None, quantity='450', effectiveAt='2020-12-20T15:00:00Z'))
        assert read()['balances']['outstandingPrincipal'] == '0' and read()['fee']['unpaid'] == '-7.5'
        print('PASS populated time-fee upgrade preserves every fixed-fee fact, alias, first outcome and historical cut; fixed-fee final return remains executable.', flush=True)

    main(checks=verify, before_migration=before, fixture_factory=loaded,through_migration='0350')


if __name__ == '__main__':
    run()
