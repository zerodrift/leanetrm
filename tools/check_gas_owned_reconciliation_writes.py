"""Complete ordinary gas source corrections preserve observed event identity."""
import json
import os
import subprocess
from datetime import datetime,timezone
from decimal import Decimal
from pathlib import Path
from uuid import uuid4

from check_reference_editing import main
from check_gas_owned_reconciliation import checks as source_checks
from gas_owned_reconciliation_seal import upgrade_checks


def checks(query,manifest):
    selected=source_checks(query,manifest)
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    at=selected['at'];account=selected['account']
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,rollback=False):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}"+('ROLLBACK;' if rollback else 'COMMIT;'))
    def capture():return call(f"SELECT security.capture_date_context('{catalog}','2020-12-20',NULL);")
    # This concrete new consumer has an explicit manual execution contract.
    # The implementation migration supplies the same native registry entry.
    query("INSERT INTO lifecycle.action_kinds VALUES('gas.owned.reconcile_quantity',false,'ConfigureGasOwnedHoldings','RecordGasOwnedHoldings') ON CONFLICT DO NOTHING;")
    assert query("SELECT NOT automatic AND configuration_permission='ConfigureGasOwnedHoldings' AND execution_permission='RecordGasOwnedHoldings' FROM lifecycle.action_kinds WHERE code='gas.owned.reconcile_quantity';")=='t'
    policy=str(uuid4())
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action='gas.owned.reconcile_quantity',mode='manual',
        fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
    call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','bind_policy','{account}',"
        f"{literal(dict(action='gas.owned.reconcile_quantity',policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))},'{at}','{capture()}',NULL,NULL,false);")
    operation=str(uuid4());context=capture()
    sql=f"SELECT security.reconcile_gas_owned_quantities('{catalog}','{operation}','{account}',{literal(selected['body'])},'{at}','{context}','{policy}',1,false)::text;"
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    accepted=json.loads(call(sql))
    assert accepted['changed'] and accepted['operationId']==operation and accepted['positions'][0]['currentRevision']==2
    assert json.loads(call(sql))==accepted
    def held(source):
        rows=json.loads(call(f"SELECT security.use_market_context('{catalog}','{source}');SELECT security.use_trading_context('{catalog}','{at}');"
            f"SELECT security.read_gas_owned_holdings('{catalog}','{source}','{at}',ARRAY['{selected['book']}']::uuid[])::text;"))['positions']
        return next(p for p in rows if p['id']==selected['stock'])
    assert Decimal(str(held(selected['before'])['remaining_quantity']))==Decimal('0.75')
    current=capture();assert Decimal(str(held(current)['remaining_quantity']))==Decimal('0.5')
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==str(int(publication)+1)
    fresh=sql.replace(operation,str(uuid4()));ack=json.loads(call(fresh))
    assert not ack['changed'] and ack['semanticAcknowledgement'] and ack['positions'][0]['acknowledgedRevision']==2
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==str(int(publication)+1)
    print('PASS ordinary financial correction: exact title and surviving dispatch, preserved original context, one financial publication and native first-outcome/semantic acknowledgement',flush=True)
    from gas_owned_reconciliation_cases import checks as resource_checks
    resource_checks(query,call,capture,catalog,account,selected,policy)
    # A fresh source fact remains uncommitted while independent receipt faults
    # and original-authority expiry are tested. The same proposal then goes
    # through the public managed registry, preview, execution and retry.
    title=json.loads(query(f"SELECT to_jsonb(e) FROM trading.gas_owned_evidence e WHERE(catalog_id,id)=('{catalog}','{selected['body']['positions'][0]['evidenceId']}');"))
    title_at=datetime.fromisoformat(title['occurred_at']).astimezone(timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.%fZ')
    revised=json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','evidence','{account}',"
        f"{literal(dict(sourceKey=title['source_key'],kind='title_receipt',quantity='0.875',occurredAt=title_at,evidence='Synthetic explicitly revised owned title',expectedRevision=2))},'{at}','{capture()}',NULL,NULL,false)::text;"))['id']
    selection={**selected['body'],'positions':[{**selected['body']['positions'][0],'expectedRevision':2,'quantity':'0.875','evidenceId':revised}]}
    identity=str(uuid4());context=capture()
    proposal=f"SELECT security.reconcile_gas_owned_quantities('{catalog}','{identity}','{account}',{literal(selection)},'{at}','{context}','{policy}',1,false)::text;"
    from gas_owned_reconciliation_seal import checks as seal_checks
    seal_checks(query,call,proposal,catalog)
    from gas_owned_reconciliation_authority import checks as authority_checks
    authority_checks(query,call,proposal,manifest)
    host=query('SHOW unix_socket_directories;');path=Path(host)/'gas-owned-correction.json'
    path.write_text(json.dumps(dict(manifest=manifest,host=host,at=at,contextId=context,operationId=identity,accountId=account,policyId=policy,selection=selection)))
    dotnet=os.environ.get('DOTNET','dotnet')
    subprocess.run([dotnet,'run','--project','tests/OpenEtrm.Api.Checks','--no-build','--no-restore','--','--gas-owned-correction',str(path)],check=True,cwd=Path(__file__).resolve().parents[1])
    assert query(f"SELECT quantity FROM trading.gas_owned_positions_current WHERE(catalog_id,id)=('{catalog}','{selected['stock']}');")=='0.875'
    assert query(f"SELECT count(*) FROM trading.gas_owned_quantity_corrections WHERE(catalog_id,operation_id)=('{catalog}','{identity}');")=='1'


if __name__=='__main__':main(checks=checks,before_migration=upgrade_checks)
