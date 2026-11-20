"""Review native operator cashout prefixes through the owned Lifecycle app."""
import argparse
from decimal import Decimal
import json
import os
from pathlib import Path
from uuid import uuid4

from check_gas_operator_quantity_reconciliation import checks as native_checks
from check_reference_editing import main as native_main
from close_session_fixture import sql_session
from gas_operator_managed_cases import checks as managed_checks
from gas_operator_browser_cases import seed as physical_seed,prepare as physical_prepare,verify as physical_verify
from gas_operator_browser_authority import control as authority_control
from gas_operator_physical_value_fixture import fixture as physical_fixture
from owned_lifecycle_browser import build_assets,run as run_browser


def fixture(root):
    reference,market,security,manifest=physical_fixture(root)
    manifest['identityIssuer']='https://preview.invalid'
    return reference,market,security,manifest


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ['pg-bin','dotnet','browser']:parser.add_argument('--'+name,type=Path,required=True)
    parser.add_argument('--no-build',action='store_true',help="Use the verification runner's built binaries and assets")
    args=parser.parse_args()
    for path in [args.pg_bin/'psql',args.dotnet,args.browser]:
        if not path.is_file():parser.error('An installed executable is required: '+str(path))
    os.environ['DOTNET']=str(args.dotnet.resolve());children=[]
    if not args.no_build:build_assets(args.dotnet.resolve())

    def browser_checks(query,manifest,call,capture,read,record,amend,packet):
        managed_checks(query,manifest,call,capture,read,record,amend,packet)
        catalog=manifest['catalogId'];account=packet['accountId'];root=packet['rootClaimId']
        original_cash_id=query(f"SELECT cash_id FROM trading.gas_owned_claim_settlements WHERE(catalog_id,position_id)=('{catalog}','{root}') ORDER BY publication_order LIMIT 1;")
        record('fee_payment',dict(feeId=original_cash_id,businessKey='synthetic_operator_browser_bank',expectedRevision=0,amount='0.5',
            paidAt='2020-12-20T14:00:00Z',evidence='Synthetic original operator bank allocation'),'gas.cash.record_payment')
        context=capture();claims=[];cash_ids=[]
        for claim_id in [root,packet['otherClaimId']]:
            source=read(claim_id,context);chosen=dict(claimId=claim_id,expectedRevision=source['claim']['expectedRevision'],
                quantity=source['claim']['quantity'],evidenceId=source['claim']['evidenceId'],settlements=[])
            for index,row in enumerate(source['settlements']):
                chosen['settlements'].append(dict(settlementId=row['id'],expectedRevision=row['expectedRevision'],
                    quantity=['0.625','2.375'][index] if claim_id==root else row['quantity'],evidenceId=row['evidenceId'],sourceObservationId=row['sourceObservationId']))
            claims.append(chosen);cash_ids.extend(row['id'] for row in source['cash'])
        assert [row['expectedRevision'] for row in claims[0]['settlements']]==[3,3]
        original_cash_sql="SELECT jsonb_agg(to_jsonb(f) ORDER BY f.id) FROM trading.gas_owned_fees f "\
            f"WHERE f.catalog_id='{catalog}' AND f.id IN({','.join(repr(value) for value in cash_ids)});"
        original_cash=query(original_cash_sql)
        bank_sql=f"SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id) FROM trading.gas_owned_fee_payments p WHERE p.catalog_id='{catalog}';"
        bank=query(bank_sql)
        physical=physical_seed(query,manifest,call,capture,record,zero=False)
        zero=physical_seed(query,manifest,call,capture,record,zero=True)
        physical_prepare(query,manifest,call,capture,record,physical,zero=False)
        physical_prepare(query,manifest,call,capture,record,zero,zero=True)
        from native_read_transport import captured_context
        physical['futureUseInputs']=captured_context(query,physical['futureUseContext']['contextId'])
        session='synthetic_operator_quantity_close';at='2020-12-23T16:00:00Z'
        sql_session(query,manifest,session,'test_utc_every_day','16:00:00',['future'],['trader'],effective=('2020-01-01','2022-01-01'))
        query("INSERT INTO security.role_permissions SELECT role_id,'PublishMarketData' FROM security.role_permissions WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
        frame=next(value for value in manifest['frames'] if value['at']=='2020-12-18T16:00:00Z')
        pins=json.loads(query("SELECT jsonb_agg(jsonb_build_object('definitionRevisionId',s.revision_id,'seriesId',s.id,'observationId',o.id,'inputUse','variable')) "
            "FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
            f"WHERE(o.catalog_id,o.definition_revision_id)=('{catalog}','{manifest['revisionId']}') AND o.id=ANY(ARRAY[{','.join(repr(value)+'::uuid' for value in frame['observationIds'])}]);"))
        members=[dict(definitionRevisionId=manifest['revisionId'],curve='future',contractId=contract,ordinal=index,scope='root')
            for index,contract in enumerate(frame['curveContracts']['future'])]
        def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
        close=call(f"SELECT security.register_market_close('{catalog}','{manifest['revisionId']}','{uuid4()}','Synthetic operator correction close','{session}','{at[:10]}','{at}',{literal(pins)},{literal(members)});")
        publication=int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';"))
        directory=Path(query('SHOW unix_socket_directories;')).resolve()
        with authority_control(query,manifest,directory) as authority:
            run_browser(query,manifest,args.dotnet.resolve(),args.browser.resolve(),children,dict(accountId=account,rootClaimId=root,claims=claims,
                policyId=packet['policyId'],policyRevision=packet['policyRevision'],closeId=close,session=session,physical=physical,zero=zero,authority=authority),
                'tests/browser/gas-operator-quantity.mjs',worker_enabled=False,
                artifacts=['lifecycle-operator-quantity-review.png','lifecycle-operator-quantity-mobile.png'])
        assert query(original_cash_sql)==original_cash and query(bank_sql)==bank,'Operator correction changed original cash or recorded bank revisions.'
        assert int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';"))==publication+3
        after=read(root,capture());steps=after['settlements']
        assert [row['expectedRevision'] for row in steps]==[4,4]
        assert [Decimal(row['quantity']) for row in steps]==[Decimal('.625'),Decimal('2.375')]
        assert [Decimal(row['cashAmount']) for row in steps]==[Decimal('.63'),Decimal('2.39')]
        assert read(packet['otherClaimId'],capture())['settlements'][0]['expectedRevision']==1
        physical_verify(query,manifest,call,capture,physical)
        physical_verify(query,manifest,call,capture,zero)
        print('PASS real operator correction forms: cash prefix .63/2.39, physical8/loss1 and zero withdrawal, one publication per correction and retained retries, original gross cash/bank and peer unchanged',flush=True)

    native_main(checks=lambda query,manifest:native_checks(query,manifest,after_writer=browser_checks),fixture_factory=fixture,
        arguments=['--pg-bin',str(args.pg_bin.resolve())],cleanup_ready=lambda:not children)


if __name__=='__main__':main()
