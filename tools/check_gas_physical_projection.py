"""Economic projection of exact native physical parents, stock and principal."""
import json
import os
from pathlib import Path
import subprocess
import shutil
from uuid import uuid4

from check_reference_editing import main
from check_gas_principal_carrying import fixture as principal_fixture,checks as principal_checks
from close_session_fixture import sql_session
from economic_worker_fixture import provision,run_saved


def fixture(root):
    reference,market,security,manifest=principal_fixture(root)
    calendar="'source_business_days','test_utc',96,'2026-10-13','2026-10-17'"
    assert market.count(calendar)==1
    return reference,market.replace(calendar,"'source_business_days','test_utc',96,'2026-10-13','2027-01-01'"),security,manifest


def checks(query,manifest):
    principal_checks(query,manifest,carrying='nominal_mark_v1')
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer'];at='2026-11-05T16:00:00Z'
    query("INSERT INTO security.role_permissions SELECT role_id,'RunScenario' FROM security.role_permissions WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('{issuer}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return call(f"SELECT security.capture_date_context('{catalog}','2026-11-05',NULL);")
    sql_session(query,manifest,'synthetic_native_physical_projection','source_business_days','16:00',['nymex_ng'],[],effective=('2026-10-01','2027-01-01'))
    accounts=json.loads(query(f"SELECT jsonb_agg(to_jsonb(a) ORDER BY id) FROM trading.gas_owned_accounts a WHERE catalog_id='{catalog}';"))
    selections=[]
    for account in accounts:
        profile=dict(profileId=str(uuid4()),expectedRevision=0,implementation='gas_accepted_quantity_lifecycle_projection_v3',
            quantityDisposition='retain_quantity_obligations_v1',effectiveAt=at,performances=[],cashouts=[])
        call(f"SELECT security.configure_gas_owned_projection('{catalog}','{uuid4()}','projection_profile','{account['id']}',{literal(profile)},'{at}','{capture()}',false);")
        selections.append(dict(accountId=account['id'],profileId=profile['profileId'],revision=1))
    account=accounts[0]
    pins=json.loads(query("SELECT jsonb_agg(o.id ORDER BY o.id) FROM market_data.observations o JOIN market_data.series s "
        "ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
        f"WHERE(o.catalog_id,o.definition_revision_id,o.observed_at)=('{catalog}','{manifest['revisionId']}','{manifest['example']['at']}') "
        f"AND(o.role='parameter' OR s.contract_id='{account['mark_contract_id']}');"))
    parents=json.loads(query(f"SELECT jsonb_agg(jsonb_build_object('tradeId',trade_id,'bindingId',binding_id) ORDER BY trade_id,binding_id) "
        f"FROM settlement_data.futures_delivery_obligations WHERE catalog_id='{catalog}';"))
    contracts=json.loads(query(f"SELECT jsonb_agg(contract_id ORDER BY ordinal) FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)="
        f"('{catalog}','{manifest['revisionId']}','nymex_ng');"))
    host=query('SHOW unix_socket_directories;');packet=Path(host)/'native-physical-projection.json'
    packet.write_text(json.dumps(dict(manifest=manifest,host=host,contextId=capture(),at=at,bookId=account['book_id'],pins=pins,
        contracts=contracts,parents=parents,gasProfiles=selections)));packet.chmod(0o600)
    root=Path(__file__).resolve().parents[1];frozen=Path(host)/'physical-economic-binaries'
    shutil.copytree(root/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0',frozen)
    provision(query,manifest,'synthetic_physical_continuation_worker',permissions=('ReadGasOwnedHoldings',))
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),str(frozen/'OpenEtrm.Api.Checks.dll'),
        '--gas-physical-projection',str(packet)],cwd=root,capture_output=True,text=True,timeout=120)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(),flush=True)
    run_saved(query,manifest,packet,frozen,'synthetic_physical_continuation_worker',permissions=('ReadGasOwnedHoldings',),expected_jobs=2)


if __name__=='__main__':
    build=subprocess.run([os.environ.get('DOTNET','dotnet'),'build','tests/OpenEtrm.Api.Checks','--no-restore'],
        cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=120)
    if build.returncode:raise RuntimeError(build.stdout+build.stderr)
    main(checks=checks,fixture_factory=fixture)
