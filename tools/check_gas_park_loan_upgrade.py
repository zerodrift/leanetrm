"""Rehearse additive Park and Loan installation over retained native gas facts."""
from pathlib import Path
from uuid import uuid4

from check_reference_editing import main
from gas_park_loan_fixture import ParkLoanFixture, fixture


def run():
    prepared = {}
    root = Path(__file__).resolve().parents[1]

    def state(query, catalog):
        tables = ['gas_owned_accounts', 'gas_owned_evidence', 'gas_owned_positions', 'gas_owned_lots', 'gas_owned_loans',
            'gas_owned_repayments', 'gas_owned_consumptions', 'gas_owned_fees', 'gas_owned_fee_payments', 'gas_owned_commands',
            'gas_owned_position_quantities', 'gas_owned_consumption_quantities', 'gas_owned_loan_quantities', 'gas_loan_fee_terms',
            'gas_owned_action_bindings', 'publications', 'knowledge_receipts']
        return query('SELECT jsonb_object_agg(name,digest ORDER BY name) FROM(' + ' UNION ALL '.join(
            "SELECT '" + table + "'::text name,(SELECT md5(coalesce(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text),'[]')::text) "
            f"FROM trading.{table} x WHERE x.catalog_id='{catalog}') digest" for table in tables) + ')s;')

    def before(query, migration):
        if migration.name != '0338_gas_park_loan.sql':
            return
        from assistant_context import migration_body
        from gas_park_loan_fixture import rejected
        signature = 'risk.needs_gas(risk.jobs)'
        changes = [f"ALTER FUNCTION {signature} {setting};" for setting in [
            "STABLE", "STRICT", "LEAKPROOF", "PARALLEL SAFE", "SECURITY INVOKER", "SET work_mem='64kB'",
            "OWNER TO openetrm_runtime"]]
        changes += [f"GRANT EXECUTE ON FUNCTION {signature} TO openetrm_runtime;",
            f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN "
            "EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic drift */ ');END$$;"]
        for change in changes:
            rejected(lambda: query('BEGIN;' + change + migration_body(migration.read_text()) + 'ROLLBACK;'),
                'gas_park_loan_upgrade: unexpected gas worker classifier')
        prompt = 'assistant.is_prompt_page(text)'
        prompt_changes = [f"ALTER FUNCTION {prompt} {setting};" for setting in [
            "STABLE", "STRICT", "LEAKPROOF", "PARALLEL SAFE", "SECURITY DEFINER", "SET work_mem='64kB'", "OWNER TO openetrm_runtime"]]
        prompt_changes += [f"GRANT EXECUTE ON FUNCTION {prompt} TO openetrm_runtime;",
            f"DO $$DECLARE d text=pg_get_functiondef('{prompt}'::regprocedure);BEGIN "
            "EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic drift */ ');END$$;"]
        for change in prompt_changes:
            rejected(lambda: query('BEGIN;' + change + migration_body(migration.read_text()) + 'ROLLBACK;'),
                'gas_park_loan_prompt_predecessor')
        query('BEGIN;SET LOCAL search_path=security,risk,public;' + migration_body(migration.read_text()) + 'ROLLBACK;')
        print('PASS Park and Loan upgrade refuses gas classifier source, owner, admission, settings and ACL drift under any caller search path', flush=True)
        print('PASS Park and Loan entry prompt registration refuses source, owner, language settings and ACL drift', flush=True)
        reference, market, security, manifest = fixture(root)
        from seed_fixture_market import load_market
        query(reference)
        security=load_market(query,market,security,manifest)
        if security:query(security)
        f = ParkLoanFixture(query, manifest)
        owned, book = f.account('park_loan_predecessor')
        policies = {action: f.bind(owned, action) for action in ['gas.loan.borrow', 'gas.loan.repay', 'gas.cash.record_payment']}
        at = '2020-12-20T10:00:00Z'
        title = f.evidence(owned, 'synthetic_predecessor_title', 'title_receipt', '720', at)
        agreement = f.evidence(owned, 'synthetic_predecessor_agreement', 'loan_agreement', '720', at)
        body = dict(agreementId=agreement, evidenceId=title, quantity='720', titleAt=at, availableAt=at,
            returnAt='2020-12-21T12:00:00Z', returnMarkCurve='future', returnMarkContractId=f.contract,
            carrying='nominal_mark_v1', feeAmount='-12.5', feeDueAt='2020-12-22T12:00:00Z', lenderId=f.party, lenderRevision=1)
        operation, context = str(uuid4()), f.capture()
        loan = f.native(owned, 'borrow', body, policy=policies['gas.loan.borrow'], operation=operation, context=context)
        evidence = f.evidence(owned, 'synthetic_predecessor_partial', 'loan_return', '270', '2020-12-20T12:00:00Z')
        f.native(owned, 'repay', dict(loanId=loan['id'], lotId=loan['stockId'], evidenceId=evidence, quantity='270',
            reservationId=None, effectiveAt='2020-12-20T12:00:00Z'), policy=policies['gas.loan.repay'])
        fee = query(f"SELECT id FROM trading.gas_owned_fees WHERE(catalog_id,loan_id)=('{f.catalog}','{loan['id']}');")
        f.native(owned, 'fee_payment', dict(feeId=fee, businessKey='synthetic_predecessor_bank', expectedRevision=0,
            paidAt='2020-12-20T14:00:00Z', amount='-5', evidence='Synthetic retained bank allocation'), policy=policies['gas.cash.record_payment'])
        prepared.update(f=f, manifest=manifest, owned=owned, book=book, policies=policies, loan=loan, body=body,
            operation=operation, context=context, digest=state(query, f.catalog))

    def loaded(_):
        assert prepared
        return '', '', '', prepared['manifest']

    def verify(query, _):
        f = prepared['f']
        assert state(query, f.catalog) == prepared['digest'], 'Installation changed retained gas facts, bank allocations or first outcomes.'
        assert query(f"SELECT count(*) FROM trading.gas_park_loan_trades WHERE catalog_id='{f.catalog}';") == '0'
        for signature in ['security.require_gas_park_loan_scope(uuid,uuid,timestamptz,uuid[],uuid[])',
                          'security.gas_park_loan_trade_head(uuid,uuid)',
                          'security.gas_park_loan_projection_fence(uuid,uuid,timestamptz,timestamptz,uuid[])']:
            assert query(f"SELECT has_function_privilege('openetrm_risk_worker','{signature}','EXECUTE');") == 't'
        assert query("SELECT has_function_privilege('openetrm_risk_worker','security.record_gas_park_loan(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,boolean)','EXECUTE');") == 'f'
        assert f.native(prepared['owned'], 'borrow', prepared['body'], policy=prepared['policies']['gas.loan.borrow'],
            operation=prepared['operation'], context=prepared['context']) == prepared['loan']
        held = f.holdings(prepared['book'])
        assert {row['remaining_quantity'] for row in held['positions']} == {'450', '-450'}
        assert held['fees'][0]['amount'] == '-12.5' and held['payments'][0]['amount'] == '-5'
        # New service correction guards do not claim or disable legacy loans.
        final = f.evidence(prepared['owned'], 'synthetic_predecessor_final', 'loan_return', '450', '2020-12-20T15:00:00Z')
        f.native(prepared['owned'], 'repay', dict(loanId=prepared['loan']['id'], lotId=prepared['loan']['stockId'], evidenceId=final,
            quantity='450', reservationId=None, effectiveAt='2020-12-20T15:00:00Z'), policy=prepared['policies']['gas.loan.repay'])
        assert not f.holdings(prepared['book'])['positions']
        print('PASS additive Park and Loan upgrade: retained stock/debt/fees/bank/receipts unchanged, no legacy conversion and ordinary native final return remains executable', flush=True)

    main(checks=verify, before_migration=before, fixture_factory=loaded,through_migration='0338')


if __name__ == '__main__':
    run()
