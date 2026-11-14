"""Complete both synthetic native delivery sides through reviewed public actions."""
import json
import os
from decimal import Decimal
from pathlib import Path
import subprocess
from uuid import uuid4


def execute(query,manifest,capture,position,body,*,at,business_date):
    root=Path(__file__).resolve().parents[1];directory=Path(query('SHOW unix_socket_directories;')).resolve()
    assert directory.name.startswith('oetrm-ref-')
    packet=directory/'complete-native-physical.json'
    packet.write_text(json.dumps(dict(manifest=manifest,host=str(directory),at=at,businessDate=business_date,
        contextId=capture(business_date),policyId=position['action_policy_id'],selection=body)));packet.chmod(0o600)
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks',
        '--','--gas-physical-completion-fixture',str(packet)],cwd=root,capture_output=True,text=True,timeout=120)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    return json.loads(result.stdout.split('NATIVE_PHYSICAL_COMPLETION ',1)[1].splitlines()[0])


def complete(query,manifest,call,capture):
    catalog=manifest['catalogId']
    at='2026-11-07T16:00:00Z'
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    rows=json.loads(query("SELECT jsonb_agg(to_jsonb(s) ORDER BY quantity DESC) FROM(SELECT p.id,p.quantity,p.account_id,t.action_policy_id "
        "FROM trading.gas_owned_positions p JOIN settlement_data.futures_delivery_obligations d ON(d.catalog_id,d.position_id)=(p.catalog_id,p.id) "
        "JOIN trading.gas_futures_delivery_terms t ON(t.catalog_id,t.profile_id,t.profile_revision)=(d.catalog_id,d.profile_id,d.profile_revision) "
        f"WHERE p.catalog_id='{catalog}')s;"))
    assert len(rows)==2 and Decimal(rows[0]['quantity'])>0>Decimal(rows[1]['quantity'])
    def evidence(account,kind,quantity,when,key):
        body=dict(sourceKey=key,kind=kind,quantity=str(quantity),occurredAt=when,evidence='Synthetic observed complete physical performance',expectedRevision=0)
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','evidence','{account}',{literal(body)},'{at}','{capture('2026-11-07')}',NULL,NULL,false)::text;"))['id']
    def remaining(position):
        performed=Decimal(query(f"SELECT coalesce(sum(quantity),0) FROM trading.gas_futures_delivery_performances WHERE(catalog_id,position_id)=('{catalog}','{position['id']}');"))
        return abs(Decimal(position['quantity']))-performed
    def perform(position,quantity,when,title,supply):
        version=int(query(f"SELECT max(revision) FROM trading.gas_futures_delivery_principal_runs WHERE(catalog_id,position_id)=('{catalog}','{position['id']}');"))
        body=dict(positionId=position['id'],businessReference='synthetic_complete_long' if title else 'synthetic_complete_short',expectedVersion=version,
            quantity=str(quantity),performedAt=when,titleEvidenceId=title,availableAt=when if title else None,supply=supply)
        execute(query,manifest,capture,position,body,at=at,business_date='2026-11-07')
        print('PASS reviewed native physical completion: exact remaining quantity, derived principal and original idempotent outcome',flush=True)
    long,short=rows
    quantity=remaining(long);assert quantity>0
    perform(long,quantity,'2026-11-07T10:00:00Z',evidence(long['account_id'],'title_receipt',quantity,'2026-11-07T10:00:00Z','synthetic_complete_title'),[])
    quantity=remaining(short);assert quantity>0
    # This fixture explicitly dispatches every surviving synthetic lot in title
    # order. The production operation receives these exact identified splits.
    lots=json.loads(query("SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY title_at,id),'[]') FROM(SELECT l.id,l.title_at,p.quantity-coalesce((SELECT sum(c.quantity) "
        "FROM trading.gas_owned_consumptions c WHERE(c.catalog_id,c.lot_id)=(l.catalog_id,l.id)),0) AS remaining FROM trading.gas_owned_lots l JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(l.catalog_id,l.id) "
        f"WHERE(l.catalog_id,l.account_id)=('{catalog}','{short['account_id']}'))s WHERE remaining>0;"))
    assert sum(Decimal(l['remaining']) for l in lots)==quantity
    supply=[dict(lotId=lot['id'],evidenceId=evidence(short['account_id'],'physical_delivery',Decimal(lot['remaining']),'2026-11-07T11:00:00Z','synthetic_complete_dispatch_'+str(i)),
        reservationId=None,quantity=str(lot['remaining'])) for i,lot in enumerate(lots)]
    perform(short,quantity,'2026-11-07T11:00:00Z',None,supply)
    assert all(remaining(position)==0 for position in rows)
    assert Decimal(query(f"SELECT sum(p.quantity)-(SELECT sum(quantity) FROM trading.gas_owned_consumptions WHERE catalog_id='{catalog}') FROM trading.gas_owned_lots l JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(l.catalog_id,l.id) WHERE l.catalog_id='{catalog}';"))==0
