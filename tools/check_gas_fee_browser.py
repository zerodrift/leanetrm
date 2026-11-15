"""Exercise the explicit fixed loan-fee form against an owned native API."""
import argparse
import json
import os
from decimal import Decimal
from pathlib import Path
from uuid import uuid4

from check_reference_editing import main as native_main
from check_gas_fee_terms import checks, fixture
from close_session_fixture import sql_session
from owned_lifecycle_browser import build_assets, run as run_browser


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ['pg-bin','dotnet','browser']:parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--no-build',action='store_true',help='Use the verification runner\'s already built binaries and assets')
    args=parser.parse_args()
    for path in [args.pg_bin/'psql',args.dotnet,args.browser]:
        if not path.is_file():parser.error('An installed executable is required: '+str(path))
    os.environ['DOTNET']=str(args.dotnet.resolve());children=[]
    if not args.no_build:build_assets(args.dotnet.resolve())

    def browser_checks(query,manifest,packet):
        catalog,tenant,workspace=(manifest[k] for k in ['catalogId','tenantId','workspaceId'])
        session='synthetic_loan_fee_close'
        sql_session(query,manifest,session,'test_utc_every_day','16:00:00',['future'],['trader'],effective=('2020-01-01','2022-01-01'))
        query("INSERT INTO security.role_permissions SELECT role_id,'PublishMarketData' FROM security.role_permissions WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
        frame=next(f for f in manifest['frames'] if f['at']=='2020-12-18T16:00:00Z')
        pins=json.loads(query("SELECT jsonb_agg(jsonb_build_object('definitionRevisionId',s.revision_id,'seriesId',s.id,'observationId',o.id,'inputUse','variable')) "
            "FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
            f"WHERE(o.catalog_id,o.definition_revision_id)=('{catalog}','{manifest['revisionId']}') AND o.id=ANY(ARRAY[{','.join(repr(v)+'::uuid' for v in frame['observationIds'])}]);"))
        members=[dict(definitionRevisionId=manifest['revisionId'],curve='future',contractId=contract,ordinal=i,scope='root')
            for i,contract in enumerate(frame['curveContracts']['future'])]
        def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        close=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');"
            f"SELECT security.register_market_close('{catalog}','{manifest['revisionId']}','{uuid4()}','Synthetic loan fee close','{session}','{packet['at'][:10]}','{packet['at']}',{literal(pins)},{literal(members)});COMMIT;")
        run_browser(query,manifest,args.dotnet.resolve(),args.browser.resolve(),children,{**packet,'closeId':close,'session':session},
            'tests/browser/gas-loan-fee.mjs',worker_enabled=False,artifacts=['lifecycle-loan-fee-review.png','lifecycle-loan-fee-mobile.png'])
        assert query(f"SELECT to_jsonb(f) FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.id)=('{catalog}','{packet['originalFeeId']}');")==packet['originalFeeJson']
        assert query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY id),'[]')::text) FROM trading.gas_owned_fee_payments p WHERE p.catalog_id='{catalog}';")==packet['bankHash']
        terms=json.loads(query(f"SELECT jsonb_build_object('revision',revision,'amount',amount::text,'dueAt',due_at) FROM trading.gas_loan_fee_terms "
            f"WHERE(catalog_id,loan_id)=('{catalog}','{packet['loanId']}') ORDER BY revision DESC LIMIT 1;"))
        assert terms['revision']==7 and Decimal(terms['amount'])==0 and terms['dueAt'] is None
        assert Decimal(query(f"SELECT sum(amount) FROM trading.gas_owned_fees WHERE(catalog_id,loan_id)=('{catalog}','{packet['loanId']}');"))==0
        print('PASS real fixed loan fee form: explicit terms, gross dated cash, original bank history, exact first-outcome retry and zero fee without an inferred bank refund',flush=True)

    native_main(checks=lambda q,m:checks(q,m,after_amendments=browser_checks),fixture_factory=fixture,
        arguments=['--pg-bin',str(args.pg_bin.resolve())],cleanup_ready=lambda:not children)


if __name__=='__main__':main()
