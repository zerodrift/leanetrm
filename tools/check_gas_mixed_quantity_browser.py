"""Real Lifecycle forms reconcile a corrected title and its accepted dispatch."""
import argparse
import json
import os
from pathlib import Path
from uuid import uuid4
from check_reference_editing import main as native_main
from check_gas_mixed_quantity_reconciliation import checks, fixture as native_fixture
from close_session_fixture import sql_session
from owned_lifecycle_browser import run as run_browser, build_assets


def fixture(root):
    reference,market,security,manifest=native_fixture(root)
    marker="UPDATE ref_data.revisions SET state='sealed'"
    assert market.count(marker)==1
    market=market.replace(marker,f"INSERT INTO ref_data.calendars VALUES('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}',"
        "'synthetic_mixed_quantity_days','test_utc',0,'2026-01-01','2027-01-01');\n"+marker)
    return reference,market,security,manifest


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ['pg-bin','dotnet','browser']:parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--no-build',action='store_true',help='Use the already built assets from the verification runner')
    args=parser.parse_args();os.environ['DOTNET']=str(args.dotnet.resolve());children=[]
    if not args.no_build:build_assets(args.dotnet.resolve())
    def correct(query,manifest,packet):
        catalog,tenant,workspace=(manifest[k] for k in ['catalogId','tenantId','workspaceId'])
        before=int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';"))
        sql_session(query,manifest,'synthetic_mixed_quantity_close','synthetic_mixed_quantity_days','16:00:00',['nymex_ng'],['trader'],effective=('2026-01-01','2027-01-01'))
        query("INSERT INTO security.role_permissions SELECT role_id,'PublishMarketData' FROM security.role_permissions WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
        pins=json.loads(query(f"SELECT jsonb_agg(jsonb_build_object('definitionRevisionId',s.revision_id,'seriesId',s.id,'observationId',o.id,'inputUse','variable')) "
            "FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
            f"WHERE(o.catalog_id,o.definition_revision_id)=('{catalog}','{manifest['revisionId']}') AND o.id=ANY(ARRAY[{','.join(repr(v)+'::uuid' for v in manifest['frames'][0]['observationIds'])}]);"))
        contract=query(f"SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{manifest['revisionId']}','nymex_ng') ORDER BY ordinal LIMIT 1;")
        members=[dict(definitionRevisionId=manifest['revisionId'],curve='nymex_ng',contractId=contract,ordinal=0,scope='root')]
        def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        close=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');"
            f"SELECT security.register_market_close('{catalog}','{manifest['revisionId']}','{uuid4()}','Synthetic mixed quantity close','synthetic_mixed_quantity_close','2026-11-06','{packet['at']}',{literal(pins)},{literal(members)});COMMIT;")
        # Publishing the fixture's close convention has its own typed trading
        # publication (D401); count the financial action after that setup fact.
        prepared=int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';"))
        assert prepared==before+1 and query(f"SELECT publication_order FROM market_data.lifecycle_close_session_publications WHERE(catalog_id,session_code,session_version)=('{catalog}','synthetic_mixed_quantity_close',1);")==str(prepared)
        run_browser(query,manifest,args.dotnet.resolve(),args.browser.resolve(),children,{**packet,'closeId':close},
            'tests/browser/gas-quantity-bundle.mjs',worker_enabled=False,artifacts=['lifecycle-gas-bundle-review.png'])
        saved=json.loads(query(f"SELECT jsonb_build_object('operation',operation_id,'context',source_context_id) FROM trading.gas_quantity_bundles "
            f"WHERE(catalog_id,account_id)=('{catalog}','{packet['accountId']}') AND ordinary_changed;"))
        return saved['operation'],saved['context'],prepared
    native_main(checks=lambda q,m:checks(q,m,run_correction=correct),fixture_factory=fixture,
        arguments=['--pg-bin',str(args.pg_bin.resolve())],cleanup_ready=lambda:not children)


if __name__=='__main__':main()
