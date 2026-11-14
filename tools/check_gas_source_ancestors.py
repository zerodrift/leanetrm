"""Financial dependency ancestry uses the captured knowledge prefix only."""
import json
from uuid import uuid4

from check_reference_editing import main
from check_gas_pending_writes import checks as pending_checks, upgrade_checks


def checks(query,manifest):
    pending_checks(query,manifest)
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    owned=json.loads(query(f"SELECT to_jsonb(a) FROM trading.gas_owned_accounts a WHERE catalog_id='{catalog}' AND kind='owned_inventory' ORDER BY publication_order LIMIT 1;"))
    operator=query(f"SELECT id FROM trading.gas_owned_accounts WHERE catalog_id='{catalog}' AND kind='operator_balance' ORDER BY publication_order LIMIT 1;")
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture(day):return call(f"SELECT security.capture_date_context('{catalog}','{day}',NULL);")
    def command(account,action,body,at,code=None):
        policy=json.loads(query(f"SELECT jsonb_build_object('id',policy_id,'revision',policy_revision) FROM trading.gas_owned_action_bindings "
            f"WHERE(catalog_id,account_id,action)=('{catalog}','{account}','{code}') ORDER BY revision DESC LIMIT 1;")) if code else None
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{account}',{literal(body)},'{at}','{capture(at[:10])}',"
            +(f"'{policy['id']}',{policy['revision']}" if policy else 'NULL,NULL')+",false)::text;"))
    at='2020-12-20T16:00:00Z';future='2020-12-24T16:00:00Z';movement='2020-12-23T12:00:00Z'
    def evidence(account,kind,quantity,when,recorded_at):
        return command(account,'evidence',dict(sourceKey=str(uuid4()),kind=kind,quantity=quantity,occurredAt=when,
            evidence='Synthetic captured dependency',expectedRevision=0),recorded_at)['id']
    receipt=evidence(owned['id'],'title_receipt','2','2020-12-20T14:00:00Z',at)
    stock=command(owned['id'],'receive_title',dict(evidenceId=receipt,quantity='2',titleAt='2020-12-20T14:00:00Z',availableAt='2020-12-20T14:00:00Z',
        originPolicy='opening_owned_stock_v1',carrying='nominal_mark_v1'),at,'gas.owned.receive_title')['id']
    statement=evidence(operator,'operator_statement','-1',movement,future)
    delivery=evidence(owned['id'],'physical_delivery','1',movement,future)
    before=capture('2020-12-20')
    claim=command(operator,'operator_claim',dict(evidenceId=statement,quantity='-1',effectiveAt=movement,dueAt='2020-12-25T12:00:00Z',
        carrying='nominal_mark_v1',settlementMode='return_quantity_v1',cashout=None),future,'gas.operator.record')['id']
    command(operator,'operator_settle',dict(positionId=claim,quantity='1',ownedAccountId=owned['id'],lotId=stock,reservationId=None,evidenceId=delivery,
        effectiveAt=movement,availableAt=None,sourceObservationId=None),future,'gas.operator.settle')
    after=capture('2020-12-20')
    # Test-only definer exposes the private graph without a financial write or
    # a current-head fence. Its grant is removed with the disposable probe.
    query("CREATE FUNCTION security.synthetic_gas_ancestor_probe(cat uuid,ids uuid[],ctx uuid,at_time timestamptz) RETURNS uuid[] "
        "LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS 'SELECT trading.gas_owned_financial_ancestors(cat,ids,ctx,at_time)';"
        "ALTER FUNCTION security.synthetic_gas_ancestor_probe(uuid,uuid[],uuid,timestamptz) OWNER TO openetrm_security_owner;"
        "REVOKE ALL ON FUNCTION security.synthetic_gas_ancestor_probe(uuid,uuid[],uuid,timestamptz) FROM PUBLIC;"
        "GRANT EXECUTE ON FUNCTION security.synthetic_gas_ancestor_probe(uuid,uuid[],uuid,timestamptz) TO openetrm_runtime;")
    try:
        def ancestors(context,position):
            return json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"
                f"SELECT to_jsonb(security.synthetic_gas_ancestor_probe('{catalog}',ARRAY['{position}']::uuid[],'{context}','{at}'))::text;"))
        assert set(ancestors(before,stock))=={stock},'An unknown future-effective settlement entered the captured funding graph'
        assert set(ancestors(after,stock))=={stock,claim},'A known future-effective settlement was discarded by its business date'
        try:ancestors(before,claim)
        except RuntimeError as error:assert 'gas_owned_context' in str(error),str(error)
        else:raise AssertionError('An initial unknown financial target was admitted into source ancestry')
    finally:
        query('DROP FUNCTION security.synthetic_gas_ancestor_probe(uuid,uuid[],uuid,timestamptz);')
    print('PASS captured gas ancestors: unknown future links and initial targets refuse; known future dependencies survive without advancing effective time',flush=True)


if __name__=='__main__':main(checks=checks,before_migration=upgrade_checks)
