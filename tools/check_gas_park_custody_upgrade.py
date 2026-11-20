"""Qualify custody installation over retained loans, fees and ordinary cash."""
import json
from pathlib import Path
from uuid import uuid4

from assistant_context import migration_body
from check_reference_editing import main
from seed_fixture_market import load_fixture
from gas_park_loan_fixture import ParkLoanFixture, literal, rejected
from gas_park_custody_fixture import fixture
from gas_balance_fee_fixture import BalanceFeeService, booking as balance_booking


def run():
    prepared = {}
    root = Path(__file__).resolve().parents[1]

    def digest(query, catalogs):
        trading = ['trades', 'instruments', 'gas_park_loan_instruments', 'gas_park_loan_trades', 'gas_park_loan_advances',
            'gas_park_loan_returns', 'gas_park_loan_commands', 'gas_park_loan_fee_terms', 'gas_park_loan_fee_determinations',
            'gas_park_loan_fee_return_inputs', 'gas_owned_accounts', 'gas_owned_evidence', 'gas_owned_positions', 'gas_owned_lots', 'gas_owned_loans',
            'gas_owned_repayments', 'gas_owned_consumptions', 'gas_owned_reservations', 'gas_owned_releases', 'gas_owned_fees',
            'gas_owned_fee_payments', 'gas_owned_commands', 'gas_owned_position_quantities', 'gas_owned_consumption_quantities',
            'gas_owned_loan_quantities', 'gas_loan_fee_terms', 'gas_owned_action_bindings', 'publications', 'knowledge_receipts',
            'booking_facts', 'trade_references', 'instrument_references', 'cash_payment_instruments', 'cash_payment_memberships',
            'futures_option_trade_revisions']
        tables = [('trading', table) for table in trading] + [('settlement_data', table) for table in ['cash_payment_commands', 'cash_payment_revisions']]
        selected = ','.join("'" + catalog + "'::uuid" for catalog in catalogs)
        return query('SELECT jsonb_object_agg(name,digest ORDER BY name) FROM(' + ' UNION ALL '.join(
            f"SELECT '{schema}.{table}'::text name,(SELECT md5(coalesce(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text),'[]')::text) "
            f"FROM {schema}.{table} x WHERE x.catalog_id=ANY(ARRAY[{selected}])) digest" for schema, table in tables) + ')s;')

    def before(query, migration):
        if migration.name != '0351_gas_park_custody.sql':
            return
        signatures = ['security.record_cash_payment(uuid,jsonb,timestamptz)', 'risk.authorized(risk.jobs)', 'risk.worker_read()',
            'risk.needs_gas(risk.jobs)', 'risk.claim(integer,text)', 'risk.owned_attempt(uuid,uuid,boolean,boolean)', 'risk.bind(uuid,uuid)']
        for signature in signatures:
            changes = [f"ALTER FUNCTION {signature} {setting};" for setting in ['STRICT', 'LEAKPROOF', 'PARALLEL SAFE',
                'SECURITY INVOKER', "SET work_mem='64kB'", 'OWNER TO openetrm_runtime']]
            changes += [f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
                f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN "
                "EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic source drift */ ');END$$;"]
            for change in changes:
                rejected(lambda: query('BEGIN;' + change + migration_body(migration.read_text()) + 'ROLLBACK;'), 'gas_custody_upgrade')
        query('BEGIN;SET LOCAL search_path=security,risk,public;' + migration_body(migration.read_text()) + 'ROLLBACK;')
        print('PASS custody upgrade refuses cash/worker source, owner, authority, settings and ACL drift under any caller search path.', flush=True)
        reference, market, security, manifest = fixture(root)
        load_fixture(query,reference,market,security,manifest)
        f = ParkLoanFixture(query, manifest)
        owned, book = f.account('retained_fixed_custody_predecessor')
        for action in ['gas.loan.borrow', 'gas.loan.repay', 'gas.cash.record_payment']:
            f.bind(owned, action)
        f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
        body = balance_booking(f, owned, None, book)
        body.pop('balanceFee'); body.update(profile='physical_gas_park_loan_v1', fixedFeeAmount='-12.5',
            returnAt='2020-12-21T12:00:00Z', feeDueAt='2020-12-22T12:00:00Z')
        booked = json.loads(f.call(f"SELECT security.book_gas_park_loan('{f.catalog}',{literal(body)},'{f.at}')::text;"))
        trade = booked['tradeId']; f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'

        def read(context=None):
            selected = context or f.capture()
            return json.loads(f.call(f"SELECT security.use_market_context('{f.catalog}','{selected}');SELECT security.use_trading_context('{f.catalog}','{f.at}');"
                f"SELECT security.read_gas_park_loan('{f.catalog}','{trade}','{selected}','{f.at}')::text;"))

        def command(action, value, *, operation=None, context=None):
            return json.loads(f.call(f"SELECT security.record_gas_park_loan('{f.catalog}','{operation or uuid4()}','{trade}','{action}',"
                f"{literal(value)},'{f.at}','{context or f.capture()}',false)::text;"))

        title = f.evidence(owned, 'synthetic_retained_title', 'title_receipt', '720', '2020-12-20T10:00:00Z')
        agreement = f.evidence(owned, 'synthetic_retained_agreement', 'loan_agreement', '720', '2020-12-20T10:00:00Z')
        advance_body = dict(expectedVersion=read()['stateVersion'], agreementEvidenceId=agreement, evidenceId=title,
            claimEvidenceId=None, lotId=None, reservationId=None, effectiveAt='2020-12-20T10:00:00Z')
        operation, context = str(uuid4()), f.capture()
        advanced = command('advance', advance_body, operation=operation, context=context)
        old_context = f.capture()
        evidence = f.evidence(owned, 'synthetic_retained_partial', 'loan_return', '270', '2020-12-20T12:00:00Z')
        command('return', dict(expectedVersion=read()['stateVersion'], businessKey='synthetic_retained_partial', evidenceId=evidence,
            lotId=advanced['stockId'], reservationId=None, quantity='270', effectiveAt='2020-12-20T12:00:00Z'))
        command('fee_payment', dict(expectedVersion=read()['stateVersion'], feeId=advanced['feeId'], businessKey='synthetic_retained_bank',
            expectedRevision=0, paidAt='2020-12-20T14:00:00Z', amount='-5', evidence='Synthetic retained bank allocation'))
        timed = BalanceFeeService(f, 'synthetic_retained_balance_fee')
        timed.advance(); timed_old = f.capture()
        timed.returned('270', '2020-12-20T11:00:00Z', 'synthetic_retained_timed_partial')
        selection = timed.selection('2020-12-20T11:00:00Z', 'synthetic_retained_determination')
        determination_operation, determination_context = str(uuid4()), f.capture()
        determined = timed.command('fee_determine', selection, operation=determination_operation, context=determination_context)
        assert determined['roundedTotal'] == '-2.88'
        timed.command('fee_payment', dict(expectedVersion=timed.read()['stateVersion'], feeId=determined['feeId'], businessKey='synthetic_retained_timed_bank',
            expectedRevision=0, paidAt='2020-12-20T11:30:00Z', amount='-1', evidence='Synthetic retained determined-fee payment'))
        # A real independent option premium exercises the shared cash RPC which
        # this migration factors for readonly receipt recovery.
        from exchange_fixture import fixture as exchange_fixture
        from preview_fixture import catalogue_fixture, trade_booking
        refs, marks, access, trading, other = exchange_fixture(root)
        load_fixture(query,refs,marks,access,other)
        query(trading)
        query(f"SELECT security.initialize_operating_policies('{other['tenantId']}','{other['workspaceId']}');")
        query(catalogue_fixture(other, other['identityIssuer']))

        def exchange_call(sql):
            token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                f"'{other['identityIssuer']}','trader','human_interactive','{other['tenantId']}','{other['workspaceId']}',NULL,'preview_runtime',60);")
            return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")

        other_catalog = other['catalogId']
        other_book = query(f"SELECT id FROM trading.books WHERE catalog_id='{other_catalog}' ORDER BY id LIMIT 1;")
        series = query(f"SELECT code FROM ref_data.futures_option_series WHERE(catalog_id,revision_id,product_code,option_right)=('{other['referenceCatalogId']}','{other['referenceRevisionId']}','nymex_on','call') ORDER BY expiry_at,strike LIMIT 1;")
        parties = trade_booking()
        for role, table in [('legalEntity', 'legal_entities'), ('counterparty', 'counterparties')]:
            party = query(f"SELECT id::text||'|'||revision::text FROM trading.{table} WHERE catalog_id='{other_catalog}' ORDER BY id LIMIT 1;").split('|')
            parties[role + 'Id'], parties[role + 'Revision'] = party[0], int(party[1])
        option = json.loads(exchange_call(f"SELECT security.book_futures_option('{other_catalog}','{uuid4()}','{other_book}','{other['revisionId']}','{series}',2,0.1,"
            f"'2026-10-19','premium_paid_up_front','2026-10-16T14:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{literal(parties)})::text;"))
        cash = dict(operationId=str(uuid4()), allocationId=str(uuid4()), tradeId=option['tradeId'], bindingId=option['premiumInstrumentId'],
            expectedRevision=0, paidAt='2026-10-16T15:00:00Z', amount='-5', policy='explicit_paid_cash_nominal_due_v1')
        paid = json.loads(exchange_call(f"SELECT security.record_cash_payment('{other_catalog}',{literal(cash)},'2026-10-16T16:00:00Z')::text;"))
        prepared.update(f=f, manifest=manifest, fixed_read=read, fixed_command=command, fixed_advance_body=advance_body, fixed_advanced=advanced,
            fixed_operation=operation, fixed_context=context, fixed_old=old_context, fixed_current=read(), fixed_owned=owned,
            timed=timed, timed_old=timed_old, timed_current=timed.read(), selection=selection, determined=determined,
            determination_operation=determination_operation, determination_context=determination_context, exchange_call=exchange_call,
            other=other, cash=cash, paid=paid, catalogs=[f.catalog, other_catalog])
        prepared['digest'] = digest(query, prepared['catalogs'])

    def loaded(_):
        return '', '', '', prepared['manifest']

    def verify(query, _):
        f = prepared['f']; timed = prepared['timed']; read = prepared['fixed_read']; command = prepared['fixed_command']
        assert digest(query, prepared['catalogs']) == prepared['digest'], 'Installation changed retained title, fee, bank, alias or first-outcome facts.'
        assert query('SELECT count(*) FROM trading.gas_park_custody_trades;') == '0'
        assert command('advance', prepared['fixed_advance_body'], operation=prepared['fixed_operation'], context=prepared['fixed_context']) == prepared['fixed_advanced']
        assert timed.command('fee_determine', prepared['selection'], operation=prepared['determination_operation'], context=prepared['determination_context']) == prepared['determined']
        assert read(prepared['fixed_old'])['balances']['outstandingPrincipal'] == '720'
        assert timed.read(prepared['timed_old'])['fee']['determinations'] == []
        for actual, expected in [(read(), prepared['fixed_current']), (timed.read(), prepared['timed_current'])]:
            assert {k:v for k,v in actual.items() if k not in ['contextId','knownAt','authorityUntil']} == {k:v for k,v in expected.items() if k not in ['contextId','knownAt','authorityUntil']}
        other = prepared['other']; call = prepared['exchange_call']; cash = prepared['cash']
        assert json.loads(call(f"SELECT security.record_cash_payment('{other['catalogId']}',{literal(cash)},'2026-10-16T16:00:00Z')::text;")) == prepared['paid']
        status = json.loads(call(f"SELECT security.cash_payment_status('{other['catalogId']}',{literal(cash)})::text;"))
        assert status['found'] and status['outcome'] == prepared['paid']
        future = {**cash, 'operationId':str(uuid4()), 'expectedRevision':1, 'amount':'-4', 'paidAt':'2026-10-16T13:00:00Z'}
        rejected(lambda: call(f"SELECT security.record_cash_payment('{other['catalogId']}',{literal(future)},'2026-10-16T16:00:00Z');"), 'cash_payment_membership_time')
        correction = {**future, 'paidAt':'2026-10-16T15:00:00Z'}
        assert json.loads(call(f"SELECT security.record_cash_payment('{other['catalogId']}',{literal(correction)},'2026-10-16T16:00:00Z')::text;"))['revision'] == 2
        evidence = f.evidence(prepared['fixed_owned'], 'synthetic_retained_final', 'loan_return', '450', '2020-12-20T15:00:00Z')
        command('return', dict(expectedVersion=read()['stateVersion'], businessKey='synthetic_retained_final', evidenceId=evidence,
            lotId=prepared['fixed_advanced']['stockId'], reservationId=None, quantity='450', effectiveAt='2020-12-20T15:00:00Z'))
        timed.returned('450', '2020-12-20T12:00:00Z', 'synthetic_retained_timed_final')
        assert read()['balances']['outstandingPrincipal'] == '0' and read()['fee']['unpaid'] == '-7.5'
        assert timed.read()['balances']['outstandingPrincipal'] == '0'
        print('PASS populated custody upgrade preserves fixed/time-fee title, bank allocations, aliases, receipts and historical cuts; ordinary cash replay/status/correction and both final returns remain executable.', flush=True)

    main(checks=verify, before_migration=before, fixture_factory=loaded,through_migration='0351')


if __name__ == '__main__':
    run()
