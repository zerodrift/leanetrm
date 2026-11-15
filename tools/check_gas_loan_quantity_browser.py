"""Review an exact native loan and recorded-use correction through Lifecycle."""
import argparse
from datetime import datetime, timezone
from decimal import Decimal
import json
import os
from pathlib import Path
from uuid import uuid4

from check_reference_editing import main as native_main
from close_session_fixture import sql_session
from gas_loan_quantity_fixture import configure, fixture
from owned_lifecycle_browser import build_assets, run as run_browser


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ['pg-bin','dotnet','browser']:parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--no-build',action='store_true',help="Use the verification runner's built binaries and assets")
    args=parser.parse_args()
    for path in [args.pg_bin/'psql',args.dotnet,args.browser]:
        if not path.is_file():parser.error('An installed executable is required: '+str(path))
    os.environ['DOTNET']=str(args.dotnet.resolve());children=[]
    if not args.no_build:build_assets(args.dotnet.resolve())

    def checks(query,manifest):
        state=configure(query,manifest,form_source_counterexamples=False)
        catalog,tenant,workspace=(manifest[key] for key in ['catalogId','tenantId','workspaceId'])
        account,loan,source=state['account'],state['loan'],state['source'];at=state['at']
        assert source['loan']['expectedRevision']==4 and Decimal(source['loan']['quantity'])==Decimal('.375')
        dispatch=next(row for row in source['consumptions'] if row['id']==state['dispatch'])
        assert dispatch['editable'] and Decimal(dispatch['quantity'])==Decimal('.25')
        fee_bank_sql=f"""SELECT jsonb_build_object('fees',(SELECT jsonb_agg(to_jsonb(f) ORDER BY f.id) FROM trading.gas_owned_fees f WHERE(catalog_id,loan_id)=('{catalog}','{loan['id']}')),
            'payments',(SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id) FROM trading.gas_owned_fee_payments p JOIN trading.gas_owned_fees f ON(f.catalog_id,f.id)=(p.catalog_id,p.fee_id) WHERE(f.catalog_id,f.loan_id)=('{catalog}','{loan['id']}')));"""
        fee_bank_before=query(fee_bank_sql)
        def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
        def call(sql):
            token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
            return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
        def capture():return call(f"SELECT security.capture_date_context('{catalog}','{at[:10]}',NULL);")
        evidence={row['id']:row for row in source['evidence']}
        def correct_document(evidence_id,quantity):
            head=evidence[evidence_id]
            occurred=datetime.fromisoformat(head['occurred_at'].replace('Z','+00:00')).astimezone(timezone.utc).isoformat().replace('+00:00','Z')
            body=dict(sourceKey=head['source_key'],kind=head['kind'],quantity=quantity,occurredAt=occurred,
                expectedRevision=head['revision'],evidence='Synthetic browser observed source correction')
            return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','evidence','{account}',{literal(body)},'{at}','{capture()}',NULL,NULL,false)::text;"))['id']
        title=correct_document(source['loan']['title_evidence_id'],'0.3125')
        agreement=correct_document(source['loan']['agreement_evidence_id'],'0.3125')
        use_evidence=correct_document(dispatch['evidence_id'],'0.125')
        session='synthetic_loan_quantity_close'
        sql_session(query,manifest,session,'test_utc_every_day','16:00:00',['future'],['trader'],effective=('2020-01-01','2022-01-01'))
        query("INSERT INTO security.role_permissions SELECT role_id,'PublishMarketData' FROM security.role_permissions WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
        frame=next(value for value in manifest['frames'] if value['at']=='2020-12-18T16:00:00Z')
        pins=json.loads(query("SELECT jsonb_agg(jsonb_build_object('definitionRevisionId',s.revision_id,'seriesId',s.id,'observationId',o.id,'inputUse','variable')) "
            "FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
            f"WHERE(o.catalog_id,o.definition_revision_id)=('{catalog}','{manifest['revisionId']}') AND o.id=ANY(ARRAY[{','.join(repr(value)+'::uuid' for value in frame['observationIds'])}]);"))
        members=[dict(definitionRevisionId=manifest['revisionId'],curve='future',contractId=contract,ordinal=index,scope='root')
            for index,contract in enumerate(frame['curveContracts']['future'])]
        close=call(f"SELECT security.register_market_close('{catalog}','{manifest['revisionId']}','{uuid4()}','Synthetic loan quantity close','{session}','{at[:10]}','{at}',{literal(pins)},{literal(members)});")
        packet=dict(accountId=account,loanId=loan['id'],stockId=loan['stockId'],returnId=loan['returnId'],dispatchId=state['dispatch'],
            dispatchRevision=dispatch['financialRevision'],policyId=state['policy'],policyRevision=1,titleEvidenceId=title,
            agreementEvidenceId=agreement,useEvidenceId=use_evidence,closeId=close,session=session)
        run_browser(query,manifest,args.dotnet.resolve(),args.browser.resolve(),children,packet,'tests/browser/gas-loan-quantity.mjs',
            worker_enabled=False,artifacts=['lifecycle-loan-quantity-review.png','lifecycle-loan-quantity-mobile.png'])
        assert query(fee_bank_sql)==fee_bank_before,'Loan quantity correction changed an independent fee or bank revision'
        terms=json.loads(query(f"SELECT jsonb_build_object('revision',revision,'quantity',quantity::text) FROM trading.gas_owned_loan_quantities WHERE(catalog_id,loan_id)=('{catalog}','{loan['id']}') ORDER BY revision DESC LIMIT 1;"))
        assert terms['revision']==5 and Decimal(terms['quantity'])==Decimal('.3125')
        context=capture()
        held=json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"
            f"SELECT security.read_gas_owned_holdings('{catalog}','{context}','{at}',ARRAY['{state['book']}']::uuid[])::text;"))
        positions={row['id']:Decimal(row['remaining_quantity']) for row in held['positions']}
        assert positions[loan['stockId']]==Decimal('.1875') and positions[loan['returnId']]==Decimal('-.0625')
        assert positions.get(state['funding'],Decimal(0))==0
        print('PASS real loan quantity form: native gross .3125, exact dispatch .125, stock .1875, debt -.0625, unchanged fixed fee/bank and first-outcome retry',flush=True)

    native_main(checks=checks,fixture_factory=fixture,arguments=['--pg-bin',str(args.pg_bin.resolve())],cleanup_ready=lambda:not children)


if __name__=='__main__':main()
