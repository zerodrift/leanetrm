"""Exact saved swap lifecycle profiles against native owned period metadata."""
import json
import os
from pathlib import Path
import subprocess
from uuid import uuid4
from copy import deepcopy
from check_reference_editing import main
from check_index_fixings import RULE
from preview_fixture import catalogue_fixture, trading_fixture,trade_booking,fixture as baseline,identity


def fixture(root):return baseline(root,fixing_frames=True)


def checks(query,manifest):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    query(trading_fixture(catalog))
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    issuer='https://preview.invalid'
    query(catalogue_fixture(manifest,issuer))
    def lit(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                    f"'{issuer}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def reject(body,code):
        try:call(f"SELECT lifecycle.save_swap_profile('{catalog}',{lit(body)});")
        except RuntimeError as error:assert code in str(error),str(error)
        else:raise AssertionError('Expected '+code)
    snapshot=str(uuid4())
    period=dict(currency='usd',deliveryFrom='2021-01-01',deliveryTo='2021-02-01',paymentSchedule=RULE)
    booking=dict(operationId=str(uuid4()),snapshotId=snapshot,bookId='d1000000-0000-0000-0000-000000000001',definitionRevisionId=manifest['revisionId'],
                 convention='independent_side_financial_swap_v2',knowledgeMode='current',quantity='100',fixedCoefficient='-1',discounting=False,commodity='test_gas',
                 fixed=dict(**period,price='3'),floating=dict(**period,curve='base_forward',index='test_price',resetFrequency='monthly',averagingMethod='none',spread='0',
                 settlement=dict(mode='curve_target',index=None,projectionPolicy=None)),**trade_booking())
    host=query('SHOW unix_socket_directories;')
    path=Path(host)/'lifecycle-swap-check.json'
    def managed(packet):
        path.write_text(json.dumps(dict(manifest=manifest,host=host,**packet)));path.chmod(0o600)
        checked=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--lifecycle-swap-native',str(path)],
                              cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=90)
        if checked.returncode:raise RuntimeError(checked.stdout+checked.stderr)
        for line in checked.stdout.splitlines():
            if line.startswith('PASS '):print(line,flush=True)
        return {key:json.loads(line[len(key)+1:]) for line in checked.stdout.splitlines() for key in ['LIFECYCLE_SWAP_BOOKED','LIFECYCLE_SWAP_REVIEWED'] if line.startswith(key+' ')}
    booked=managed(dict(snapshotId=snapshot,booking=booking))['LIFECYCLE_SWAP_BOOKED']
    policy=dict(operationId=str(uuid4()),policyId=str(uuid4()),expectedRevision=0,action='swap.determine',mode='review',
                fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
    call(f"SELECT lifecycle.save_policy('{catalog}',{lit(policy)});")
    profile=dict(operationId=str(uuid4()),profileId=str(uuid4()),tradeId=booked['tradeId'],expectedRevision=0,originRevision=1,action='swap.determine',
                 policyId=policy['policyId'],policyRevision=1,membershipOrdinal=1,paymentOrdinal=0,deliveryFrom='2021-01-01',deliveryTo='2021-02-01',
                 referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],currency='usd',decimalPlaces=2,
                 roundingPolicy='currency_minor_units_to_even_v1',duePolicy='swap_due_nominal_v1',
                 recognitionPolicy='swap_completed_period_trading_pnl_v1',correctionPolicy='swap_explicit_correction_due_v1')
    first=json.loads(call(f"SELECT lifecycle.save_swap_profile('{catalog}',{lit(profile)})::text;"))
    assert json.loads(call(f"SELECT lifecycle.save_swap_profile('{catalog}',{lit(profile)})::text;"))==first
    publications=query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
    acknowledged=json.loads(call(f"SELECT lifecycle.save_swap_profile('{catalog}',{lit({**profile,'operationId':str(uuid4())})})::text;"))
    assert acknowledged['revision']==first['revision']==1
    assert query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")==publications
    for changed,code in [({'paymentOrdinal':1},'lifecycle_swap_occurrence'),({'currency':'absent'},'lifecycle_swap_occurrence'),
                         ({'decimalPlaces':3},'settlement_rounding'),({'expectedRevision':'0'},'lifecycle_input')]:
        reject({**profile,'operationId':str(uuid4()),**changed},code)
    call(f"SELECT lifecycle.finish_command('{catalog}','{profile['operationId']}','swap_profile',{lit(profile)},'AmendTrade',clock_timestamp()+interval '20 seconds');")
    source=call(f"SELECT security.capture_date_context('{catalog}','2021-01-15',NULL);")
    binding=query(f"SELECT binding_id FROM trading.swap_trade_revisions WHERE(catalog_id,trade_id,revision)=('{catalog}','{booked['tradeId']}',1);")
    packet=dict(snapshotId=snapshot,frameAt='2021-01-15T08:00:00Z',
                    context=dict(contextId=source,asOf='2021-01-15T08:00:00Z',businessDate='2021-01-15',snapshotId=snapshot),
                    target=dict(handler='financial_swap',scopeId=booked['tradeId'],bindingId=binding))
    original=managed({**packet,'expectedVersion':1,'expectedCurrentVersion':1})['LIFECYCLE_SWAP_REVIEWED']
    def counts():return query(f"SELECT(SELECT count(*) FROM settlement_data.swap_determinations WHERE trade_id='{booked['tradeId']}')::text||':'||(SELECT count(*) FROM settlement_data.swap_cash_payments WHERE trade_id='{booked['tradeId']}')::text||':'||(SELECT count(*) FROM settlement_data.swap_payment_allocations WHERE trade_id='{booked['tradeId']}')::text||':'||(SELECT count(*) FROM settlement_data.swap_commands WHERE trade_id='{booked['tradeId']}' AND acknowledged_operation_id IS NULL)::text;")
    assert counts()=='1:0:0:1'
    old=identity('7',5000000)
    pins=next(f['observationIds'] for f in manifest['frames'] if f['at']=='2021-01-15T08:00:00Z')
    current=packet
    for n,value in [(2,'3.6'),(3,'3.6')]:
        correction=str(uuid4())
        query(f"INSERT INTO market_data.observations SELECT catalog_id,'{correction}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,'{n}',role,publication_state,observed_at,clock_timestamp(),{value},id FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{old}');")
        pins=[correction if p==old else p for p in pins];old=correction
        latest=call(f"SELECT security.capture_date_context('{catalog}','2021-01-15',NULL);")
        current={**packet,'snapshotId':str(uuid4()),'selectedObservations':pins,'correctionDueAt':f'2021-01-{14+n:02}T08:00:00Z',
                 'context':{**packet['context'],'contextId':latest},'expectedVersion':n,'expectedCurrentVersion':n}
        current['context']['snapshotId']=current['snapshotId']
        managed(current)
        assert counts()==f'{n}:0:0:{n}'
        publications=query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
        managed({**packet,'reviewedRequest':original,'expectedVersion':1,'expectedCurrentVersion':n})
        assert counts()==f'{n}:0:0:{n}' and query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")==publications
    malformed=deepcopy(original);malformed['selection']['determination']['expectedVersion']=-1
    managed({**packet,'reviewedRequest':malformed,'expectedError':'lifecycle_input'})
    latest=query(f"SELECT id FROM settlement_data.swap_determinations WHERE trade_id='{booked['tradeId']}' ORDER BY ordinal LIMIT 1;")
    payment_policy={**policy,'operationId':str(uuid4()),'policyId':str(uuid4()),'action':'swap.payment'}
    call(f"SELECT lifecycle.save_policy('{catalog}',{lit(payment_policy)});")
    call(f"SELECT lifecycle.save_swap_profile('{catalog}',{lit({**profile,'operationId':str(uuid4()),'profileId':str(uuid4()),'action':'swap.payment','policyId':payment_policy['policyId']})});")
    old_payment_context=call(f"SELECT security.capture_date_context('{catalog}','2021-01-20',NULL);")
    payment=dict(expectedRevision=1,expectedVersion=3,determinationId=latest,amount='100',currency=original['selection']['determination']['preview']['currency'],
                 effectiveAt='2021-01-20T08:00:00Z',reference='Synthetic observed cash 1',reversesId=None)
    paid_packet={**current,'action':'swap.payment','payment':payment,'context':{**current['context'],'contextId':old_payment_context,'asOf':'2021-01-20T08:00:00Z','businessDate':'2021-01-20'},
                 'expectedVersion':4,'expectedCurrentVersion':4}
    managed(paid_packet);assert counts()=='3:1:1:4'
    after_payment=call(f"SELECT security.capture_date_context('{catalog}','2021-01-21',NULL);")
    later_payment={**paid_packet,'payment':{**payment,'expectedVersion':4},'context':{**paid_packet['context'],'contextId':after_payment,'asOf':'2021-01-21T08:00:00Z','businessDate':'2021-01-21'}}
    publications=query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
    managed(later_payment);assert counts()=='3:1:1:4'
    assert query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")==publications
    managed({**later_payment,'payment':{**later_payment['payment'],'amount':'101'},'expectedError':'settlement_payment_evidence_conflict'})
    assert counts()=='3:1:1:4'
    managed({**packet,'history':True,'paymentCount':0,'determinationCount':0,'bookId':booking['bookId']})
    future_known=call(f"SELECT security.capture_date_context('{catalog}','2021-01-14',NULL);")
    history={**current,'history':True,'paymentCount':1,'determinationCount':3,'future':True,'futureDeterminations':True,'bookId':booking['bookId'],
             'context':{**current['context'],'contextId':future_known,'asOf':'2021-01-14T08:00:00Z','businessDate':'2021-01-14'}}
    managed(history)
    actual=json.loads(call(f"SELECT security.use_market_context('{catalog}','{future_known}');SELECT security.use_trading_context('{catalog}','2021-01-14T08:00:00Z','2021-01-14'::date);SELECT settlement_data.swap_state('{catalog}','{booked['tradeId']}')::text;"))
    assert actual['determinations']==[] and actual['payments']==[], 'Actual opening paid recognition consumed future-effective known facts.'
    quantity=dict(revision=dict(catalogId=manifest['referenceCatalogId'],revisionId=manifest['referenceRevisionId']),code='test_gas_quantity')
    periods=[('2021-01-01','2021-02-01'),('2021-02-01','2021-03-01')]
    def side(fixed):
        native_periods=[]
        for start,end in periods:
            terms={**(booking['fixed'] if fixed else booking['floating']),'deliveryFrom':start,'deliveryTo':end}
            native_periods.append(dict(amount='100',fixed=terms if fixed else None,floating=None if fixed else terms))
        return dict(basis='period_total',quantity=quantity,amount=None,allocation=None,priceApplication='period_average_times_quantity_v1',power=None,periods=native_periods)
    scheduled={**booking,'operationId':str(uuid4()),'quantity':None,'fixedCoefficient':None,'fixed':None,'floating':None,
               'swapType':'scheduled_financial_swap','firstCoefficient':'-1',
               'scheduled':dict(implementation='whole_term_scheduled_financial_swap_v1',first=side(True),second=side(False))}
    multi=managed(dict(snapshotId=snapshot,booking=scheduled))['LIFECYCLE_SWAP_BOOKED']
    profiles=[]
    for ordinal,(start,end) in enumerate(periods):
        occurrence={**profile,'operationId':str(uuid4()),'profileId':str(uuid4()),'tradeId':multi['tradeId'],'paymentOrdinal':ordinal,
                    'deliveryFrom':start,'deliveryTo':end}
        call(f"SELECT lifecycle.save_swap_profile('{catalog}',{lit(occurrence)});")
        profiles.append(occurrence['profileId'])
    multi_source=call(f"SELECT security.capture_date_context('{catalog}','2021-01-15',NULL);")
    multi_binding=query(f"SELECT binding_id FROM trading.swap_trade_revisions WHERE(catalog_id,trade_id,revision)=('{catalog}','{multi['tradeId']}',1);")
    managed({**packet,'context':{**packet['context'],'contextId':multi_source},'target':dict(handler='financial_swap',scopeId=multi['tradeId'],bindingId=multi_binding),'profileIds':profiles})
    assert query("SELECT bool_and(NOT has_function_privilege('preview_runtime',p.oid,'EXECUTE')) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE(n.nspname,p.proname) IN(('settlement_data','determine_swap'),('security','record_swap_cash'),('security','record_swap_net'));")=='t'
    print('PASS accepted swap snapshots: old source after later corrections, same-price changed lineage, no publication or financial sequence on acknowledgement',flush=True)
    print('PASS native swap lifecycle settings: exact period ordinal/quote currency, idempotent financial configuration, stable publication and final source admission',flush=True)


if __name__=='__main__':main(checks=checks,fixture_factory=fixture)
