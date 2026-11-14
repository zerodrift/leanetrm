"""Ordinary physical option value on exact native option and inventory sources."""
import json
import os
from pathlib import Path
import subprocess
import shutil
from uuid import uuid4

from check_physical_futures_value import fixture as physical_sources, checks as physical_setup
from check_reference_editing import main
from economic_worker_fixture import run_saved
import physical_option_admission_fixture as admission


def fixture(root):
    reference,market,security,manifest=physical_sources(root)
    # The vanilla reference calibration contract requires a unique native
    # underlying curve. This bounded consumer does not infer a preferred curve
    # from multiple otherwise compatible sources.
    alternatives=('nymex_ng_alternate','nymex_ng_wrong_mark')
    statements=market.split(';')
    selected=[statement for statement in statements if not any(code in statement for code in alternatives)]
    assert len(statements)-len(selected)==4
    return reference,';'.join(selected),security,manifest


def option_value(query,manifest,call,capture,delivery,margin,book,booking,contract,mark):
    catalog=manifest['catalogId'];primary=manifest['revisionId'];secondary=manifest['physicalSecondaryRevision']
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    series=query(f"SELECT s.code FROM ref_data.futures_option_series s JOIN market_data.contracts c "
        "ON(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code)=(s.catalog_id,s.revision_id,s.underlying_series_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.id,s.option_right)=('{catalog}','{primary}','{contract}','call') ORDER BY s.strike LIMIT 1;")
    assert series,'The exact primary source must contain an option on this physical future.'
    booked=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{primary}','{series}',2,0.1,"
        f"'2026-10-30','premium_paid_up_front','2026-10-16T14:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{literal(booking)})::text;"))
    foreign=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{primary}','{series}',1,0.1,"
        f"'2026-10-30','premium_paid_up_front','2026-10-16T14:01:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{literal(booking)})::text;"))
    assert foreign['tradeId']!=booked['tradeId']
    policy=dict(operationId=str(uuid4()),policyId=str(uuid4()),expectedRevision=0,action='option.expiry',mode='review',
        fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(policy)});")
    profile=dict(operationId=str(uuid4()),tradeId=booked['tradeId'],action='option.expiry',expectedRevision=0,
        policyId=policy['policyId'],policyRevision=1,marginPolicyId=margin['id'],marginPolicyRevision=1,
        futureMarkCurve='nymex_ng',expiryCurve='nymex_ng')
    call(f"SELECT lifecycle.save_option_profile('{catalog}',{literal(profile)});")
    pins=json.loads(query(f"SELECT jsonb_object_agg(definition_revision_id,pins) FROM(SELECT definition_revision_id,jsonb_agg(id ORDER BY id)pins "
        f"FROM market_data.observations WHERE catalog_id='{catalog}' AND observed_at='2026-10-16T16:00:00Z' GROUP BY definition_revision_id)s;"))
    option_contract=query(f"SELECT contract_id FROM trading.futures_option_trade_revisions WHERE(catalog_id,trade_id)=('{catalog}','{booked['tradeId']}');")
    directory=Path(query('SHOW unix_socket_directories;')).resolve();assert directory.name.startswith('oetrm-ref-')
    packet=directory/'physical-option-value.json'
    packet.write_text(json.dumps(dict(manifest=manifest,host=str(directory),contextId=capture(),at='2026-10-16T16:00:00Z',pins=pins,
        bookId=book,tradeId=booked['tradeId'],bindingId=booked['optionInstrumentId'],contractId=contract,optionContractId=option_contract,
        markContractId=mark,secondaryRevision=secondary)));packet.chmod(0o600)
    root=Path(__file__).resolve().parents[1];frozen=directory/'physical-option-binaries'
    shutil.copytree(root/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0',frozen)
    login='synthetic_physical_option_worker'
    original_until=admission.prepare(query,manifest,login)
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),str(frozen/'OpenEtrm.Api.Checks.dll'),'--physical-option-value',str(packet)],
        cwd=root,capture_output=True,text=True,timeout=120)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(),flush=True)
    admission.check(query,manifest,login,packet,foreign,original_until)
    run_saved(query,manifest,packet,frozen,login,permissions=('ReadGasOwnedHoldings',),expected_jobs=2,verify_mode='--physical-option-worker-verify')


def checks(query,manifest):physical_setup(query,manifest,option_value=option_value)


if __name__=='__main__':
    root=Path(__file__).resolve().parents[1]
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),'build','tests/OpenEtrm.Api.Checks'],cwd=root,capture_output=True,text=True,timeout=120)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    main(checks=checks,fixture_factory=fixture)
