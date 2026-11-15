"""Explicit per-holding valuation choices in an owned native application."""
import argparse
import json
import os
from pathlib import Path
import subprocess
from uuid import uuid4

from check_reference_editing import main as native_main
from check_physical_option_value import fixture,checks
from close_session_fixture import sql_session
from owned_lifecycle_browser import run as run_browser,build_assets,ROOT
from owned_process import stop_owned_groups


def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"


def runtime(query,manifest):
    def call(sql,profile='trader'):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{manifest['identityIssuer']}','{profile}','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    return call


def record_source(query,manifest,saved,call,curves,retained=()):
    catalog=manifest['catalogId'];primary=manifest['revisionId'];source_session='synthetic_valuation_source'
    sql_session(query,manifest,source_session,'source_business_days','16:00',curves,['approver'],effective=('2026-10-01','2027-01-01'))
    ids=[identity for values in saved['pins'].values() for identity in values] if isinstance(saved['pins'],dict) else saved['pins']
    pins=json.loads(query("SELECT jsonb_agg(jsonb_build_object('definitionRevisionId',definition_revision_id,'seriesId',series_id,'observationId',id,'inputUse','variable') ORDER BY id) "
        f"FROM market_data.observations WHERE catalog_id='{catalog}' AND id=ANY(ARRAY[{','.join(repr(i)+'::uuid' for i in ids)}]);"))
    selected=[(primary,curve,'root') for curve in curves]+[(definition,curve,'retained') for definition,curve in retained]
    tuples=','.join(f"('{definition}','{curve}','{scope}')" for definition,curve,scope in selected)
    members=json.loads(query("SELECT jsonb_agg(jsonb_build_object('definitionRevisionId',m.revision_id,'curve',m.curve_code,'contractId',m.contract_id,'ordinal',m.ordinal,'scope',s.scope) "
        f"ORDER BY m.revision_id,m.curve_code,m.ordinal) FROM market_data.curve_members m JOIN(VALUES {tuples})s(definition,curve,scope) "
        f"ON(m.revision_id,m.curve_code)=(s.definition::uuid,s.curve) WHERE m.catalog_id='{catalog}';"))
    close=call(f"SELECT security.register_market_close('{catalog}','{primary}','{uuid4()}','Synthetic held valuation inputs','{source_session}','{saved['at'][:10]}','{saved['at']}',{literal(pins)},{literal(members)});",'approver')
    parts=json.loads(call(f"SELECT security.read_market_close_parts('{catalog}',ARRAY['{close}']::uuid[],NULL)::text;"))
    assert {row['definition_revision_id'] for row in parts[0]['sources']}=={definition for definition,_,_ in selected}
    return close,source_session


def run_application(query,manifest,packet,frozen,target,login,*,dotnet,browser,children):
    counts="SELECT jsonb_build_array((SELECT count(*) FROM trading.publications),(SELECT count(*) FROM market_data.observations),(SELECT count(*) FROM settlement_data.futures_margin_runs),(SELECT count(*) FROM trading.gas_owned_positions),(SELECT count(*) FROM trading.gas_futures_delivery_performances),(SELECT count(*) FROM trading.gas_futures_delivery_principal_runs))::text;"
    before=query(counts);directory=packet.parent.resolve();config=directory/(login+'-config.json')
    assert directory.name.startswith('oetrm-ref-') and config.is_file()
    environment={k:v for k,v in os.environ.items() if not k.startswith(('PG','OPENETRM_','ASPNETCORE_','DOTNET_'))}
    environment['OPENETRM_RISK_WORKER_CONFIG']=str(config)
    try:
        with (directory/'valuation-risk-worker.log').open('w') as log:
            worker=subprocess.Popen([str(dotnet),str(frozen/'OpenEtrm.Api.dll'),'--risk-worker','serve'],cwd=ROOT,env=environment,stdout=log,stderr=subprocess.STDOUT,start_new_session=True);children.append(worker)
            run_browser(query,manifest,dotnet,browser,children,target,'tests/browser/lifecycle-valuation.mjs',worker_enabled=False,
                artifacts=[f"valuation-{target['kind']}-bindings.png",f"valuation-{target['kind']}-report.png"])
        assert query(counts)==before,'Valuation UI or its risk worker changed accepted financial or market facts.'
    except Exception as error:
        jobs=query("SELECT coalesce(jsonb_agg(jsonb_build_object('id',id,'state',state,'error',error_code) ORDER BY created_at),'[]')::text FROM risk.jobs;")
        raise RuntimeError(str(error)+'\nOwned valuation worker:\n'+(directory/'valuation-risk-worker.log').read_text()+'\nOwned risk jobs:\n'+jobs) from error
    finally:stop_owned_groups(children)


def browser_checks(query,manifest,packet,frozen,setup,*,dotnet,browser,children):
    saved=json.loads(packet.read_text());catalog=manifest['catalogId'];primary=manifest['revisionId'];secondary=saved['secondaryRevision']
    call=runtime(query,manifest)
    # The application receives an actual held future and both independently
    # booked option rights. Existing negative scope tests run before this hook.
    future=json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{saved['bookId']}','{primary}',"
        f"'nymex_ng','{saved['contractId']}','2026-10-16T14:00:00Z',NULL,2,3,'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(setup['booking'])})::text;"))
    context=call(f"SELECT security.capture_date_context('{catalog}','{saved['at'][:10]}',NULL);")
    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(dict(operationId=str(uuid4()),tradeId=future['tradeId'],bindingId=future['bindingId'],
        expectedRevision=1,policyId=setup['marginId'],policyRevision=1,contextId=context,asOf=saved['at'],adoption='native_execution_inception_v1'))});")
    policy=query(f"SELECT policy_id::text||'|'||policy_revision::text FROM lifecycle.option_profiles WHERE(catalog_id,trade_id,action,revision)=('{catalog}','{saved['tradeId']}','option.expiry',1);").split('|')
    assert len(policy)==2
    call(f"SELECT lifecycle.save_option_profile('{catalog}',{literal(dict(operationId=str(uuid4()),tradeId=setup['foreignTradeId'],action='option.expiry',expectedRevision=0,
        policyId=policy[0],policyRevision=int(policy[1]),marginPolicyId=setup['marginId'],marginPolicyRevision=1,futureMarkCurve='nymex_ng',expiryCurve='nymex_ng'))});")
    close,source_session=record_source(query,manifest,saved,call,['nymex_ng','nymex_on'],[(secondary,'synthetic_inventory')])
    target=dict(kind='physical',closeId=close,sourceSession=source_session,at=saved['at'],bookId=saved['bookId'],futureTradeIds=[future['tradeId']],
        optionTradeIds=[saved['tradeId'],setup['foreignTradeId']],sourceFactorCount=2,economicSession='synthetic_physical_option_economic',pricingValue='native_full_economic')
    run_application(query,manifest,packet,frozen,target,'synthetic_physical_option_worker',dotnet=dotnet,browser=browser,children=children)


def cash_browser_checks(query,manifest,packet,frozen,*,dotnet,browser,children):
    saved=json.loads(packet.read_text());catalog=manifest['catalogId'];call=runtime(query,manifest)
    assert call("SELECT security.has_permission('ReadGasOwnedHoldings');")=='f','The cash UI consumer must qualify without Gas authority.'
    profile=json.loads(query("SELECT jsonb_build_object('policyId',policy_id,'policyRevision',policy_revision,'marginPolicyId',margin_policy_id,'marginPolicyRevision',margin_policy_revision,"
        f"'futureMarkCurve',future_mark_curve,'expiryCurve',expiry_curve)::text FROM lifecycle.option_profiles WHERE(catalog_id,trade_id,action,revision)=('{catalog}','{saved['tradeId']}','option.expiry',1);"))
    call(f"SELECT lifecycle.save_option_profile('{catalog}',{literal(dict(operationId=str(uuid4()),tradeId=saved['foreign']['tradeId'],action='option.expiry',expectedRevision=0,**profile))});")
    close,source_session=record_source(query,manifest,saved,call,['ice_cfb','synthetic_cash_option'])
    target=dict(kind='cash',closeId=close,sourceSession=source_session,at=saved['at'],bookId=saved['bookId'],futureTradeIds=[],
        optionTradeIds=[saved['tradeId'],saved['foreign']['tradeId']],sourceFactorCount=1,economicSession='synthetic_cash_option_economic',pricingValue='native_full_economic')
    run_application(query,manifest,packet,frozen,target,'synthetic_cash_option_worker',dotnet=dotnet,browser=browser,children=children)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin',type=Path,required=True);parser.add_argument('--dotnet',type=Path,required=True);parser.add_argument('--browser',type=Path,required=True)
    parser.add_argument('--no-build',action='store_true',help='Use the verification runner’s already-built assets')
    parser.add_argument('--kind',choices=('physical','cash'),default='physical',help='The explicit native option consumer to qualify')
    args=parser.parse_args()
    for path in (args.dotnet,args.browser):
        if not path.is_file():parser.error('Select an installed executable: '+str(path))
    os.environ['DOTNET']=str(args.dotnet.resolve())
    if not args.no_build:build_assets(args.dotnet.resolve())
    children=[]
    options=dict(dotnet=args.dotnet.resolve(),browser=args.browser.resolve(),children=children)
    if args.kind=='cash':
        from check_cash_option_value import fixture as cash_fixture,checks as cash_checks,upgrade_checks
        native_main(checks=lambda q,m:cash_checks(q,m,economic=True,browser=lambda *values:cash_browser_checks(*values,**options)),
            fixture_factory=cash_fixture,before_migration=upgrade_checks,arguments=['--pg-bin',str(args.pg_bin.resolve())],cleanup_ready=lambda:not children)
    else:
        native_main(checks=lambda q,m:checks(q,m,economic=True,browser=lambda *values:browser_checks(*values,**options)),
            fixture_factory=fixture,arguments=['--pg-bin',str(args.pg_bin.resolve())],cleanup_ready=lambda:not children)
