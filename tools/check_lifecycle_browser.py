"""Full Lifecycle UI over this tree's API and an owned native synthetic database."""
import argparse
import json
import os
from pathlib import Path
from uuid import uuid4

from check_reference_editing import main as native_main
from check_lifecycle_scheduling import fixture, configured
from check_futures_physical_delivery import checks as physical_checks


def run(query,manifest,dotnet,browser,children):
    def prepared(query,manifest,setup):
        saved=configured(query,manifest,setup)
        catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
        def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
        def call(sql):
            token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
            return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
        booked=json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{setup['bookId']}','{manifest['revisionId']}',"
            f"'nymex_ng','{setup['contractId']}','2026-10-16T14:00:00Z',NULL,2,3,'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(setup['booking'])})::text;"))
        adoption={**setup['adoption'],'operationId':str(uuid4()),'tradeId':booked['tradeId'],'bindingId':booked['bindingId'],
            'contextId':call(f"SELECT security.capture_date_context('{catalog}','2026-10-16',NULL);")}
        call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(adoption)});")
        close_at=query(f"SELECT to_char(at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM market_data.market_closes WHERE(catalog_id,id)=('{catalog}','{saved['closeId']}');")
        account=setup['profile']['accountId'];title_key='synthetic_browser_opening_title';title_policy=str(uuid4())
        call(f"SELECT lifecycle.save_policy('{catalog}',{literal(dict(operationId=str(uuid4()),policyId=title_policy,expectedRevision=0,action='gas.owned.receive_title',mode='review',fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
        correction_policy=str(uuid4())
        call(f"SELECT lifecycle.save_policy('{catalog}',{literal(dict(operationId=str(uuid4()),policyId=correction_policy,expectedRevision=0,action='gas.owned.reconcile_quantity',mode='review',fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
        for command,body in [('bind_policy',dict(action='gas.owned.receive_title',policyId=title_policy,policyRevision=1,effectiveAt='2026-10-01T00:00:00Z')),
            ('bind_policy',dict(action='gas.owned.reconcile_quantity',policyId=correction_policy,policyRevision=1,effectiveAt='2026-10-01T00:00:00Z')),
            ('evidence',dict(sourceKey=title_key,kind='title_receipt',quantity='0.125',occurredAt=close_at,evidence='Synthetic independently observed opening title',expectedRevision=0))]:
            context=call(f"SELECT security.capture_date_context('{catalog}','{close_at[:10]}',NULL);")
            call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{command}','{account}',{literal(body)},'{close_at}','{context}',NULL,NULL,false);")
        fixture=dict(closeId=saved['closeId'],tradeId=booked['tradeId'],reference=query(f"SELECT 'TR-'||lpad(reference_number::text,6,'0') FROM trading.trade_references WHERE(catalog_id,trade_id)=('{catalog}','{booked['tradeId']}');"),reviewJobId=saved['reviewJobId'],
            gasAccountId=account,gasAccountCode='synthetic_physical_delivery',titleSourceKey=title_key,titleAt=close_at)
        from owned_lifecycle_browser import run as run_browser
        run_browser(query,manifest,dotnet,browser,children,fixture,'tests/browser/lifecycle.mjs',artifacts=[
            'lifecycle-desktop.png','lifecycle-mobile.png','lifecycle-physical-review.png','lifecycle-gas-review.png','lifecycle-gas-quantity-review.png'])
        assert query(f"SELECT count(*) FROM settlement_data.futures_delivery_obligations WHERE(catalog_id,trade_id,binding_id)=('{catalog}','{booked['tradeId']}','{booked['bindingId']}');")=='1'
        assert query(f"SELECT count(*) FROM trading.gas_owned_lots l JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(l.catalog_id,l.id) JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(p.catalog_id,p.evidence_id) WHERE l.catalog_id='{catalog}' AND e.source_key='{title_key}' AND p.quantity=0.125;")=='1'
        assert query(f"SELECT count(*) FROM trading.gas_owned_positions_current p JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(p.catalog_id,p.evidence_id) JOIN trading.gas_owned_position_quantities q ON(q.catalog_id,q.position_id,q.revision)=(p.catalog_id,p.id,2) WHERE p.catalog_id='{catalog}' AND e.source_key='{title_key}' AND p.quantity=0.0625 AND q.quantity=p.quantity AND q.evidence_id=p.evidence_id;")=='1'
        assert query(f"SELECT count(*) FROM trading.gas_owned_quantity_corrections WHERE(catalog_id,account_id)=('{catalog}','{account}');")=='1'
        print('PASS full app Lifecycle browser: real API, shared financial action, local time, scheduled native worker, retained outcome and independent obligation',flush=True)
    physical_checks(query,manifest,configuration_checks=prepared)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin',type=Path,required=True);parser.add_argument('--dotnet',type=Path,required=True);parser.add_argument('--browser',type=Path,required=True)
    parser.add_argument('--no-build',action='store_true',help='Use assets already built by the qualified verification runner; do not rebuild during the check')
    args=parser.parse_args()
    for path in [args.dotnet,args.browser]:
        if not path.is_file():parser.error('An installed executable is required: '+str(path))
    os.environ['DOTNET']=str(args.dotnet.resolve())
    if not args.no_build:
        from owned_lifecycle_browser import build_assets
        build_assets(args.dotnet.resolve())
    children=[]
    native_main(checks=lambda q,m:run(q,m,args.dotnet.resolve(),args.browser.resolve(),children),fixture_factory=fixture,arguments=['--pg-bin',str(args.pg_bin.resolve())],cleanup_ready=lambda:not children)


if __name__=='__main__':main()
