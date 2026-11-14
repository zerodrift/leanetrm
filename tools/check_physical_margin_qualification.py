"""Physical futures require an explicit continuation for every new enrollment."""
from copy import deepcopy
import json
from pathlib import Path
from uuid import uuid4

from check_reference_editing import main
from check_futures_physical_delivery import fixture,checks as physical_checks
from preview_fixture import catalogue_fixture


def helpers(query,manifest):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    def lit(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,profile='trader'):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{manifest['identityIssuer']}','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def refused(sql,profile='trader'):
        try:call(sql,profile)
        except RuntimeError as error:assert 'futures_delivery_policy_required' in str(error),str(error)
        else:raise AssertionError('A new physical enrollment accepted the cash-only v1 policy without a delivery continuation.')
    return lit,call,refused


def checks(query,manifest):
    def configured(query,manifest,setup):
        lit,call,refused=helpers(query,manifest);catalog=manifest['catalogId']
        old=deepcopy(setup['marginRequest']);old['operationId']=str(uuid4());old['policy']['id']=str(uuid4())
        old['policy']['implementation']='dated_futures_margin_policy_v1';del old['policy']['delivery']
        before=query(f"SELECT count(*) FROM settlement_data.futures_margin_policies WHERE catalog_id='{catalog}';")
        refused(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{lit(old)});",'editor')
        assert query(f"SELECT count(*) FROM settlement_data.futures_margin_policies WHERE catalog_id='{catalog}';")==before
        assert query("SELECT has_function_privilege('preview_runtime','settlement_data.require_qualified_physical_margin(settlement_data.futures_margin_policies)','EXECUTE');")=='f'
        print('PASS physical margin admission: new v1 policy refused, exact v2 delivery policy and native adoption remain executable',flush=True)
    physical_checks(query,manifest,configuration_checks=configured)


def upgrade_checks(query,migration):
    if migration.name!='0284_qualified_physical_margin.sql':return
    # Rehearse the actual old schema and accepted facts in a separate native
    # physical clone. No trigger is disabled and no historical fact is edited.
    name='synthetic_physical_margin_upgrade'
    roles=set(query('SELECT rolname FROM pg_roles;').splitlines())
    query(f'CREATE DATABASE {name} TEMPLATE postgres;')
    child=lambda sql:query('\\connect '+name+'\n'+sql)
    try:
        root=Path(__file__).resolve().parents[1]
        reference,market,security,manifest=fixture(root)
        for sql in (reference,market,security):child(sql)
        catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
        child(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
        child(catalogue_fixture(manifest,manifest['identityIssuer']))
        child("INSERT INTO security.role_permissions SELECT role_id,'ReadTrades' FROM security.role_permissions WHERE permission='WriteMarketDefinitions' ON CONFLICT DO NOTHING;")
        lit,call,refused=helpers(child,manifest)
        capture=lambda:call(f"SELECT security.capture_date_context('{catalog}','2026-10-16',NULL);")
        book=child(f"SELECT id FROM trading.books WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
        parties={}
        for role,table in [('legalEntity','legal_entities'),('counterparty','counterparties')]:
            key,revision=child(f"SELECT id::text||'|'||revision::text FROM trading.{table} WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;").split('|')
            parties[role+'Id'],parties[role+'Revision']=key,int(revision)
        contract,expiry=child(f"SELECT c.id::text||'|'||to_char(c.expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id) WHERE(c.catalog_id,c.revision_id,m.curve_code)=('{catalog}','{manifest['revisionId']}','nymex_ng') ORDER BY c.expiry_at LIMIT 1;").split('|')
        policy=dict(id=str(uuid4()),definitionRevisionId=manifest['revisionId'],contractId=contract,settlementCurve='nymex_ng',
            referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],currency='usd',fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',
            opening='include_opening_instant',increment='0.01',tie='to_even',stage='payment_total',roundingScope='held_binding_difference_payment_total_v1',implementation='dated_futures_margin_policy_v1',
            occurrences=[dict(id=str(uuid4()),at=expiry,dueAt=expiry,phase='final')])
        saved=dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf='2026-10-16T16:00:00Z',policy=policy)
        original_policy=json.loads(call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{lit(saved)})::text;",'editor'))
        def book_future():return json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}','{manifest['revisionId']}','nymex_ng','{contract}','2026-10-16T14:00:00Z',NULL,1,3,'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{lit(parties)})::text;"))
        trade=book_future()
        adopted=dict(operationId=str(uuid4()),tradeId=trade['tradeId'],bindingId=trade['bindingId'],expectedRevision=1,policyId=policy['id'],policyRevision=1,
            contextId=capture(),asOf='2026-10-16T16:00:00Z',adoption='native_execution_inception_v1')
        original_adoption=json.loads(call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{lit(adopted)})::text;"))
        second=book_future()
        child(migration.read_text())
        assert json.loads(call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{lit(saved)})::text;",'editor'))==original_policy
        assert json.loads(call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{lit(adopted)})::text;"))==original_adoption
        before=child(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
        acknowledged={**saved,'operationId':str(uuid4()),'expectedRevision':1,'contextId':capture()}
        assert json.loads(call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{lit(acknowledged)})::text;",'editor'))['revision']==1
        new_adoption={**adopted,'operationId':str(uuid4()),'tradeId':second['tradeId'],'bindingId':second['bindingId'],'contextId':capture()}
        refused(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{lit(new_adoption)});")
        assert child(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")==before
        assert child(f"SELECT count(*) FROM settlement_data.futures_margin_adoptions WHERE catalog_id='{catalog}';")=='1'
        print('PASS native physical-v1 upgrade: first receipts and fact acknowledgements retained, old policy cannot enroll another future and publishes no partial state',flush=True)
    finally:
        query(f'DROP DATABASE {name};')
        for role in set(query('SELECT rolname FROM pg_roles;').splitlines())-roles:
            query('DROP ROLE "'+role.replace('"','""')+'";')


if __name__=='__main__':main(checks=checks,fixture_factory=fixture,before_migration=upgrade_checks)
