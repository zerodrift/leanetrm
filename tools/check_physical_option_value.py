"""Ordinary physical option value on exact native option and inventory sources."""
import json
import os
from pathlib import Path
import sys
import subprocess
import shutil
from uuid import uuid4

from check_physical_futures_value import fixture as physical_sources, checks as physical_setup
from check_reference_editing import main
from economic_worker_fixture import run_saved
from futures_delivery_fixture import configure
from rate_curve_fixture import discount_default
import physical_option_admission_fixture as admission


def fixture(root,*,child_original=False):
    source=unique_reference(physical_sources(root))
    if not child_original:return source
    reference,market,security,manifest=source
    catalog,original=manifest['catalogId'],manifest['physicalSecondaryRevision'];child=str(uuid4())
    table=lambda name,change: (f"INSERT INTO market_data.{name} SELECT(jsonb_populate_record(NULL::market_data.{name},to_jsonb(x)||"
        f"jsonb_build_object('revision_id','{child}')||{change})).* FROM market_data.{name} x WHERE(x.catalog_id,x.revision_id)=('{catalog}','{original}');")
    statements=[f"INSERT INTO market_data.revisions SELECT(jsonb_populate_record(NULL::market_data.revisions,to_jsonb(x)||jsonb_build_object('id','{child}','state','draft','generation',0))).* FROM market_data.revisions x WHERE(catalog_id,id)=('{catalog}','{original}');",
        table('revision_periods',"'{}'::jsonb"),table('contracts',"'{}'::jsonb"),
        table('curves',"jsonb_build_object('code','synthetic_child_inventory','name','Synthetic native child inventory')"),
        table('curve_members',"jsonb_build_object('curve_code','synthetic_child_inventory')"),table('series',"'{}'::jsonb"),
        table('rate_curves',"'{}'::jsonb"),table('rate_curve_tenors',"'{}'::jsonb"),
        f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{child}');",
        f"INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(x)||jsonb_build_object('id',gen_random_uuid(),'definition_revision_id','{child}',"
        f"'source_event_id','synthetic_child_'||x.source_event_id,'value',x.value+CASE WHEN x.role='parameter' THEN 0.01 ELSE 0.2 END))).* FROM market_data.observations x WHERE(x.catalog_id,x.definition_revision_id)=('{catalog}','{original}');"]
    return reference,market+'\nBEGIN;\n'+'\n'.join(statements)+'\nCOMMIT;',security,{**manifest,'physicalChildRevision':child}


def unique_reference(source):
    reference,market,security,manifest=source
    # The vanilla reference calibration contract requires a unique native
    # underlying curve. This bounded consumer does not infer a preferred curve
    # from multiple otherwise compatible sources.
    alternatives=('nymex_ng_alternate','nymex_ng_wrong_mark')
    statements=market.split(';')
    selected=[statement for statement in statements if not any(code in statement for code in alternatives)]
    assert len(statements)-len(selected)==4
    return reference,';'.join(selected),security,manifest


def option_value(query,manifest,call,capture,delivery,margin,book,booking,contract,mark,*,economic=False,native_child=False,browser=None):
    catalog=manifest['catalogId'];primary=manifest['revisionId'];secondary=manifest['physicalSecondaryRevision']
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    series=query(f"SELECT s.code FROM ref_data.futures_option_series s JOIN market_data.contracts c "
        "ON(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code)=(s.catalog_id,s.revision_id,s.underlying_series_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.id,s.option_right)=('{catalog}','{primary}','{contract}','call') ORDER BY s.strike LIMIT 1;")
    assert series,'The exact primary source must contain an option on this physical future.'
    booked=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{primary}','{series}',2,0.1,"
        f"'2026-10-30','premium_paid_up_front','2026-10-16T14:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{literal(booking)})::text;"))
    foreign=json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{primary}','{series}',1,0.1,"
        f"'2026-10-30','premium_paid_up_front','2026-10-16T14:01:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{literal(booking)})::text;"))
    assert foreign['tradeId']!=booked['tradeId']
    policy=dict(operationId=str(uuid4()),policyId=str(uuid4()),expectedRevision=0,action='option.expiry',mode='review',
        fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(policy)});")
    profile=dict(operationId=str(uuid4()),tradeId=booked['tradeId'],action='option.expiry',expectedRevision=0,
        policyId=policy['policyId'],policyRevision=1,marginPolicyId=margin['id'],marginPolicyRevision=1,
        futureMarkCurve='nymex_ng',expiryCurve='nymex_ng')
    call(f"SELECT lifecycle.save_option_profile('{catalog}',{literal(profile)});")
    child=None
    if native_child:
        child_margin=margin
        if 'physicalChildRevision' in manifest:
            definition=manifest['physicalChildRevision'];reference=f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
            query(f"BEGIN;INSERT INTO trading.publications SELECT '{catalog}',max(publication_order)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';"+
                discount_default(catalog,str(uuid4()),definition,reference,'usd','usd_rates',f"(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}')",'2026-10-01T00:00:00Z')+'COMMIT;')
            terms=delivery['profile']
            selected=configure(query,manifest,call,capture,curve='nymex_ng',contract=contract,book=book,entity=booking['legalEntityId'],
                entity_revision=booking['legalEntityRevision'],mark_contract=mark,account_code='synthetic_native_child_inventory',account_effective_at='2026-10-01T00:00:00Z',
                as_of='2026-10-16T16:00:00Z',delivery_from=terms['fromAt'],delivery_to=terms['toAt'],cash_due=terms['cashDueAt'],carrying=terms['carrying'],
                policy_from='2026-10-01T00:00:00Z',policy_until='2027-01-01T00:00:00Z',mark_definition_revision_id=definition,mark_curve='synthetic_child_inventory')
            child_margin={**margin,'id':str(uuid4()),'delivery':dict(id=selected['profile']['id'],revision=1)}
            call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf='2026-10-16T16:00:00Z',policy=child_margin))});",'editor')
        exercise_policy={**policy,'operationId':str(uuid4()),'policyId':str(uuid4()),'action':'option.exercise','mode':'manual'}
        call(f"SELECT lifecycle.save_policy('{catalog}',{literal(exercise_policy)});")
        exercise_profile={**profile,'operationId':str(uuid4()),'action':'option.exercise','policyId':exercise_policy['policyId'],'expiryCurve':None,'marginPolicyId':child_margin['id']}
        call(f"SELECT lifecycle.save_option_profile('{catalog}',{literal(exercise_profile)});")
        request=dict(operationId=str(uuid4()),context=dict(contextId=capture(),asOf='2026-10-16T16:30:00Z',businessDate='2026-10-16'),
            target=dict(handler='listed_option',scopeId=booked['tradeId'],bindingId=booked['optionInstrumentId']),action='option.exercise',
            policyId=exercise_policy['policyId'],policyRevision=1,automatic=False,previewHash='0'*64,
            selection=dict(evidenceId=str(uuid4()),lots='2',expectedEventVersion=0,profileRevision=1))
        request['previewHash']=json.loads(call(f"SELECT lifecycle.option_proposal('{catalog}',{literal(request)})::text;"))['hash']
        child=json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{literal(request)})::text;"))
    if economic:
        from close_session_fixture import sql_session
        sql_session(query,manifest,'synthetic_physical_option_economic','source_business_days','17:00',['nymex_ng','synthetic_inventory'],[],
            effective=('2026-10-01','2027-01-01'),curve_sources={'synthetic_inventory':secondary})
        if native_child:
            for code,cutoff in (('before','16:29'),('at','16:30')):
                sql_session(query,manifest,'synthetic_physical_option_'+code,'source_business_days',cutoff,['nymex_ng','synthetic_inventory'],[],
                    effective=('2026-10-01','2027-01-01'),curve_sources={'synthetic_inventory':secondary})
    pins=json.loads(query(f"SELECT jsonb_object_agg(definition_revision_id,pins) FROM(SELECT definition_revision_id,jsonb_agg(id ORDER BY id)pins "
        f"FROM market_data.observations WHERE catalog_id='{catalog}' AND observed_at='2026-10-16T16:00:00Z' GROUP BY definition_revision_id)s;"))
    option_contract=query(f"SELECT contract_id FROM trading.futures_option_trade_revisions WHERE(catalog_id,trade_id)=('{catalog}','{booked['tradeId']}');")
    directory=Path(query('SHOW unix_socket_directories;')).resolve();assert directory.name.startswith('oetrm-ref-')
    packet=directory/'physical-option-value.json'
    packet.write_text(json.dumps(dict(manifest=manifest,host=str(directory),contextId=capture(),at='2026-10-16T16:00:00Z',pins=pins,
        bookId=book,tradeId=booked['tradeId'],bindingId=booked['optionInstrumentId'],contractId=contract,optionContractId=option_contract,
        markContractId=mark,secondaryRevision=secondary,childRevision=manifest.get('physicalChildRevision'),child=child)));packet.chmod(0o600)
    root=Path(__file__).resolve().parents[1];frozen=directory/'physical-option-binaries'
    shutil.copytree(root/'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0',frozen)
    login='synthetic_physical_option_worker'
    original_until=admission.prepare(query,manifest,login)
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),str(frozen/'OpenEtrm.Api.Checks.dll'),'--physical-option-economic' if economic else '--physical-option-value',str(packet)],
        cwd=root,capture_output=True,text=True,timeout=120)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(),flush=True)
    admission.check(query,manifest,login,packet,foreign,original_until)
    run_saved(query,manifest,packet,frozen,login,permissions=('ReadGasOwnedHoldings',),expected_jobs=2,
        verify_mode='--economic-worker-verify' if economic else '--physical-option-worker-verify')
    if browser is not None:browser(query,manifest,packet,frozen,dict(marginId=margin['id'],booking=booking,foreignTradeId=foreign['tradeId']))


def checks(query,manifest,*,economic=False,native_child=False,browser=None):
    physical_setup(query,manifest,option_value=lambda *args:option_value(*args,economic=economic,native_child=native_child,browser=browser))


if __name__=='__main__':
    child_original='--child-original-source' in sys.argv
    if child_original:sys.argv.remove('--child-original-source')
    native_child='--native-child' in sys.argv or child_original
    if '--native-child' in sys.argv:sys.argv.remove('--native-child')
    economic='--economic' in sys.argv or native_child
    if '--economic' in sys.argv:sys.argv.remove('--economic')
    root=Path(__file__).resolve().parents[1]
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),'build','tests/OpenEtrm.Api.Checks'],cwd=root,capture_output=True,text=True,timeout=120)
    if result.returncode:raise RuntimeError(result.stdout+result.stderr)
    main(checks=lambda query,manifest:checks(query,manifest,economic=economic,native_child=native_child),fixture_factory=lambda root:fixture(root,child_original=child_original))
