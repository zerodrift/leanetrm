"""Native loan withdrawal retains exact proof and independently surviving cash."""
import json
import os
from decimal import Decimal
from pathlib import Path
import subprocess
import sys
from uuid import uuid4

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from gas_loan_quantity_fixture import configure as loan_checks
from gas_loan_withdrawal import checks as withdraw
from gas_loan_fee_only import checks as fee_only


def checks(query,manifest,*,state=None):
    state=loan_checks(query,manifest) if state is None else state
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    account,at=state['account'],state['at'];issuer=manifest['identityIssuer']
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,rollback=False):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{issuer}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}"+('ROLLBACK;' if rollback else 'COMMIT;'))
    def capture():return call(f"SELECT security.resolve_operation_context('{catalog}','2020-12-20',NULL);")
    def record(action,body):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{account}',{literal(body)},'{at}','{capture()}',NULL,NULL,false)::text;"))
    def evidence(key,kind,quantity,when,revision):return record('evidence',dict(sourceKey=key,kind=kind,quantity=quantity,occurredAt=when,
        evidence='Synthetic explicit complete principal withdrawal',expectedRevision=revision))['id']
    retired=withdraw(query,call,record,evidence,capture,state,manifest)
    definition=state['original']['definition_revision_id'];mark_curve=state['original']['mark_curve_code']
    contract=state['original']['mark_contract_id']
    mark=Decimal(query(f"SELECT value FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','70000000-0000-0000-0000-000000000001');"))
    multiplier=Decimal(query("SELECT q.multiplier FROM market_data.curves c JOIN ref_data.quote_specifications q "
        "ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{definition}','{mark_curve}');"))
    rates=json.loads(query("SELECT jsonb_agg(o.id ORDER BY t.ordinal)::text FROM market_data.rate_curves c JOIN market_data.rate_curve_tenors t "
        "ON(t.catalog_id,t.revision_id,t.curve_code)=(c.catalog_id,c.revision_id,c.code) JOIN market_data.observations o "
        "ON(o.catalog_id,o.definition_revision_id,o.series_id,o.source_code)=(c.catalog_id,c.revision_id,t.series_id,c.source_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{definition}','interest_rates') "
        "AND o.role='parameter' AND o.publication_state='final' AND o.observed_at='2020-12-18T16:00:00Z' "
        "AND NOT EXISTS(SELECT 1 FROM market_data.observations n WHERE(n.catalog_id,n.corrects_id)=(o.catalog_id,o.id));"))
    tenors=int(query(f"SELECT count(*) FROM market_data.rate_curve_tenors WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{definition}','interest_rates');"))
    assert rates and len(rates)==tenors and query(f"SELECT bool_and(value=0) FROM market_data.observations WHERE catalog_id='{catalog}' AND id=ANY(ARRAY["+
        ','.join("'"+rate+"'::uuid" for rate in rates)+"]);")=='t'
    host=Path(query('SHOW unix_socket_directories;'));path=host/'gas-loan-withdrawal.json'
    packet=dict(manifest=manifest,host=str(host),at=at,**retired,bookId=state['book'],markId='70000000-0000-0000-0000-000000000001',
        contractId=contract,rateIds=rates,expectedPv=str(Decimal('.25')*mark*multiplier-Decimal('.05')),feeOnly=False)
    root=Path(__file__).resolve().parents[1]
    def verify():
        path.write_text(json.dumps(packet));path.chmod(0o600)
        subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--project','tests/OpenEtrm.Api.Checks','--no-build','--no-restore',
            '--','--gas-loan-withdrawal',str(path)],cwd=root,check=True)
    verify()
    packet.update(fee_only(query,call,state,manifest));verify()
