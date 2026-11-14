"""Corrected applied sources retain facts and refuse false complete valuations."""
import json
import os
from pathlib import Path
import subprocess
from uuid import uuid4
from datetime import datetime, timezone

from check_reference_editing import main
from check_gas_owned_holdings import checks as owned_checks


def checks(query, manifest):
    owned_checks(query, manifest)
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    at='2020-12-20T16:00:00Z'
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def instant(value):return datetime.fromisoformat(value).astimezone(timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.%fZ')
    def call(sql):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture(day='2020-12-20'):return call(f"SELECT security.capture_date_context('{catalog}','{day}',NULL);")
    original=json.loads(query(f"SELECT to_jsonb(a) FROM trading.gas_owned_accounts a WHERE catalog_id='{catalog}' AND kind='operator_balance';"))
    operator=str(uuid4());cashout_book='d1000000-0000-0000-0000-000000000002'
    def command(action,body,policy=None):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{operator}',{literal(body)},'{at}','{capture()}',"
            +(f"'{policy}',1" if policy else 'NULL,NULL')+",false)::text;"))
    command('account',dict(code='synthetic_corrected_cashout',kind='operator_balance',bookId=cashout_book,
        legalEntityId=original['legal_entity_id'],legalEntityRevision=original['legal_entity_revision'],counterpartyId=original['counterparty_id'],counterpartyRevision=original['counterparty_revision'],
        referenceCatalogId=original['reference_catalog_id'],referenceRevisionId=original['reference_revision_id'],definitionRevisionId=original['definition_revision_id'],
        markCurve=original['mark_curve_code'],markContractId=original['mark_contract_id'],quantity=original['quantity_code'],currency=original['currency_code'],location=original['location_code'],
        titlePolicy=original['title_policy'],lossPolicy=original['loss_policy'],effectiveAt='2020-12-18T00:00:00Z'))
    policies={}
    for action in ('gas.operator.record','gas.operator.settle'):
        policy=str(uuid4());policies[action]=policy
        call(f"SELECT lifecycle.save_policy('{catalog}',{literal(dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action=action,mode='manual',
            fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
        command('bind_policy',dict(action=action,policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))
    def evidence(kind,when):
        return command('evidence',dict(sourceKey=str(uuid4()),kind=kind,quantity='1',occurredAt=when,evidence='Synthetic applied source',expectedRevision=0))['id']
    observation=json.loads(query(f"SELECT to_jsonb(o) FROM market_data.observations o WHERE catalog_id='{catalog}' AND source_event_id LIKE 'synthetic_cashout_%';"))
    claim=command('operator_claim',dict(evidenceId=evidence('operator_statement','2020-12-19T12:00:00Z'),quantity='1',effectiveAt='2020-12-19T12:00:00Z',
        dueAt='2020-12-21T12:00:00Z',carrying='currency_curve_v1',settlementMode='cashout_final_price_v1',cashout=dict(source=observation['source_code'],role=observation['role'],
            observedAt=instant(observation['observed_at']),cashDueAt='2020-12-21T12:00:00Z',rounding='payment_total_half_even_2dp_v1')),policies['gas.operator.record'])
    command('operator_settle',dict(positionId=claim['id'],quantity='1',ownedAccountId=None,lotId=None,reservationId=None,
        evidenceId=evidence('cashout_determination','2020-12-20T13:00:00Z'),effectiveAt='2020-12-20T13:00:00Z',availableAt=None,sourceObservationId=observation['id']),policies['gas.operator.settle'])
    before=capture()
    consumed=json.loads(query(f"SELECT to_jsonb(e)||jsonb_build_object('lot_id',c.lot_id,'book_id',a.book_id) FROM trading.gas_owned_consumptions c "
        "JOIN trading.gas_owned_loans l ON(l.catalog_id,l.received_lot_id,l.quantity)=(c.catalog_id,c.lot_id,c.quantity) "
        "JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(c.catalog_id,c.evidence_id) JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(e.catalog_id,e.account_id) "
        f"WHERE c.catalog_id='{catalog}';"))
    # The newest source moves the reported event into the future. Select the
    # head by knowledge first: its later event date cannot resurrect the older
    # source that supported an already-applied dispatch.
    body=dict(sourceKey=consumed['source_key'],kind=consumed['kind'],quantity='99',occurredAt='2020-12-24T11:00:00Z',evidence='Synthetic corrected dispatch',expectedRevision=consumed['revision'])
    call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','evidence','{consumed['account_id']}',{literal(body)},'2020-12-24T16:00:00Z','{capture('2020-12-24')}',NULL,NULL,false);")
    after_evidence=capture()
    changed=str(uuid4())
    query(f"INSERT INTO market_data.observations SELECT catalog_id,'{changed}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,"
        f"source_event_id,'2',role,publication_state,observed_at,clock_timestamp(),1.015,id FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{observation['id']}');")
    after_observation=capture()
    # Operational and repair discovery still sees accepted facts. A corrected
    # raw source does not itself rewrite title, claim quantity, principal or cash.
    for book in (consumed['book_id'],cashout_book):
        state=json.loads(call(f"SELECT security.use_market_context('{catalog}','{after_observation}');SELECT security.use_trading_context('{catalog}','{at}');"
            f"SELECT security.read_gas_owned_holdings('{catalog}','{after_observation}','{at}',ARRAY['{book}']::uuid[])::text;"))
        assert state['fees'] and not any(p['id']==consumed['lot_id'] for p in state['positions'])
    host=query('SHOW unix_socket_directories;');path=Path(host)/'gas-applied-check.json'
    path.write_text(json.dumps(dict(manifest=manifest,host=host,at=at,beforeContext=before,afterEvidence=after_evidence,afterObservation=after_observation,
        consumptionBook=consumed['book_id'],cashoutBook=cashout_book,markId='70000000-0000-0000-0000-000000000001',contractId='50000000-0000-0000-0000-000000000001')))
    path.chmod(0o600)
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--gas-applied-source',str(path)],
        cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(),flush=True)


if __name__=='__main__':main(checks=checks)
