"""Exact saved delivery carrying for source-qualified native principal cash."""
import json
import sys
from decimal import Decimal
from pathlib import Path
import os
import subprocess

from check_reference_editing import main
from check_gas_futures_delivery import checks as delivery_checks
from check_lifecycle_actions import fixture as action_fixture

def fixture(root):
    return action_fixture(root,rate_inputs=[('2026-12-31',Decimal('0.04')),('2027-06-30',Decimal('0.045'))])


def checks(query,manifest,*,carrying):
    delivery_checks(query,manifest,carrying=carrying)
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer']
    def call(sql,profile='trader'):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('{issuer}','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    context=call(f"SELECT security.capture_date_context('{catalog}','2026-11-06',NULL);")
    book=query(f"SELECT book_id FROM trading.gas_owned_accounts WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    args=f"'{catalog}','{context}','2026-11-06T16:00:00Z',ARRAY['{book}'::uuid]"
    prefix=f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','2026-11-06T16:00:00Z');"
    original=json.loads(call(prefix+f"SELECT security.read_gas_owned_holdings({args})::text;"))
    selected=json.loads(call(prefix+f"SELECT security.read_gas_owned_profile_holdings({args})::text;"))
    assert selected['principalCarryingImplementation']=='saved_delivery_profile_principal_carrying_v1'
    assert selected['contextId']==original['contextId'] and selected['knownAt']==original['knownAt'] and selected['asOf']==original['asOf']
    assert len(selected['deliveryCash'])==len(original['deliveryCash'])>0
    for before,after in zip(original['deliveryCash'],selected['deliveryCash']):
        profile=after.pop('principalCarrying')
        assert after==before and profile['carrying']==carrying
        expected=json.loads(query("SELECT jsonb_build_object('profileId',d.profile_id,'profileRevision',d.profile_revision,'termsId',p.terms_id,'accountId',g.account_id) "
            "FROM trading.gas_futures_delivery_cash c JOIN settlement_data.futures_delivery_obligations d ON(d.catalog_id,d.position_id)=(c.catalog_id,c.position_id) "
            "JOIN settlement_data.futures_delivery_profiles p ON(p.catalog_id,p.id,p.revision)=(d.catalog_id,d.profile_id,d.profile_revision) "
            "JOIN trading.gas_futures_delivery_terms g ON(g.catalog_id,g.profile_id,g.profile_revision)=(p.catalog_id,p.id,p.revision) "
            f"WHERE(c.catalog_id,c.id)=('{catalog}','{before['id']}');"))
        assert all(profile[k]==v for k,v in expected.items())
    assert query("SELECT NOT EXISTS(SELECT 1 FROM pg_proc p CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner)))a "
        "WHERE p.oid='security.read_gas_owned_profile_holdings(uuid,uuid,timestamptz,uuid[])'::regprocedure AND a.grantee=0 AND a.privilege_type='EXECUTE');")=='t'
    assert query("SELECT p.prosecdef AND p.provolatile='v' AND NOT p.proretset AND p.prorettype='jsonb'::regtype AND "
        "p.proowner='openetrm_security_owner'::regrole AND p.proconfig=ARRAY['search_path=pg_catalog'] AND "
        "p.proargnames=ARRAY['catalog','context','at_time','books'] AND p.pronargdefaults=0 "
        "FROM pg_proc p WHERE oid='security.read_gas_owned_profile_holdings(uuid,uuid,timestamptz,uuid[])'::regprocedure;")=='t'
    try:call(prefix+f"SELECT security.read_gas_owned_profile_holdings({args});",'risk')
    except RuntimeError as error:assert any(code in str(error) for code in ('access_denied','gas_owned_denied')),str(error)
    else:raise AssertionError('Principal metadata bypassed native gas authority.')
    print('PASS native principal carrying: exact immutable profile lineage, unchanged ownership/cash and original certified source',flush=True)
    source=json.loads(query(f"SELECT jsonb_build_object('markContract',a.mark_contract_id,'mark',o.id) FROM trading.gas_owned_accounts a "
        "JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.contract_id)=(a.catalog_id,a.definition_revision_id,a.mark_contract_id) "
        "JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.series_id)=(s.catalog_id,s.revision_id,s.id) "
        f"WHERE(a.catalog_id,a.book_id,o.observed_at)=('{catalog}','{book}','{manifest['example']['at']}') LIMIT 1;"))
    pins=json.loads(query(f"SELECT jsonb_agg(id) FROM market_data.observations WHERE catalog_id='{catalog}' AND "
        f"(id='{source['mark']}' OR role='parameter' AND observed_at='{manifest['example']['at']}');"))
    host=query('SHOW unix_socket_directories;');packet=Path(host)/'principal-carrying-check.json'
    packet.write_text(json.dumps(dict(manifest=manifest,host=host,contextId=context,at='2026-11-06T16:00:00Z',
        bookId=book,markContract=source['markContract'],pins=pins,carrying=carrying)));packet.chmod(0o600)
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks',
        '--','--gas-principal-carrying',str(packet)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(),flush=True)


if __name__=='__main__':
    nominal='--nominal' in sys.argv
    if nominal:sys.argv.remove('--nominal')
    selected='nominal_mark_v1' if nominal else 'currency_curve_v1'
    main(checks=lambda query,manifest:checks(query,manifest,carrying=selected),fixture_factory=fixture)
