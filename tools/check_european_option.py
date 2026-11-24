"""A European listed futures option on an owned synthetic source (D446): the stored exercise style, booking,
value beside an American series on the same future, refused early exercise and expiry by the product's rule."""
from decimal import Decimal
import json
import os
from pathlib import Path
import subprocess
from uuid import uuid4

from check_reference_editing import main
from check_cash_option_value import fixture as american_fixture
from preview_fixture import catalogue_fixture

PRODUCT='synthetic_european_option'
SERIES=(('synthetic_european_call','call'),('synthetic_european_put','put'))
AMERICAN='synthetic_cash_call'
UNDERLYING='ice_cfb_20261101'
STRIKE='3.10'
FRAME='2026-10-16T16:00:00Z'


def fixture(root):
    # The cash option fixture supplies a cash-settled future valued at synthetic daily settlements and an American call
    # on it. This adds a European product on the same future, cloned from that product: a call and a put struck at 3.10,
    # each settling 0.2 above its intrinsic value in every frame.
    reference,market,security,manifest=american_fixture(root,solved=True)
    catalog,definition=manifest['catalogId'],manifest['revisionId'];refs=f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
    seal=f"UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{manifest['referenceCatalogId']}' AND id='{manifest['referenceRevisionId']}';"
    assert market.count(seal)==1
    terms=(f"INSERT INTO ref_data.sources(catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review) VALUES({refs},'{PRODUCT}','synthetic','synthetic:european-option-contract','2026-10-01T00:00:00Z','Original synthetic option contract','not_reviewed');"
        f"INSERT INTO ref_data.futures_option_products SELECT(jsonb_populate_record(NULL::ref_data.futures_option_products,to_jsonb(p)||jsonb_build_object("
        f"'code','{PRODUCT}','name','Synthetic European option on a cash future','exercise_style','european','strike_interval_source_code','{PRODUCT}'))).* "
        f"FROM ref_data.futures_option_products p WHERE(catalog_id,revision_id,code)=({refs},'synthetic_cash_option');"
        f"INSERT INTO ref_data.trading_close_times SELECT(jsonb_populate_record(NULL::ref_data.trading_close_times,to_jsonb(t)||jsonb_build_object('code','{PRODUCT}_expiry',"
        f"'option_product_code','{PRODUCT}','source_code','{PRODUCT}'))).* FROM ref_data.trading_close_times t WHERE(catalog_id,revision_id,milestone,option_product_code)=({refs},'option_expiry','synthetic_cash_option');"
        +''.join(f"INSERT INTO ref_data.futures_option_series VALUES({refs},'{code}','{PRODUCT}','{UNDERLYING}','{right}',{STRIKE},'synthetic','2026-10-27',"
            f"ref_data.close_instant({refs},'option_expiry','{PRODUCT}','2026-10-27'),NULL,'{PRODUCT}');" for code,right in SERIES))
    market=market.replace(seal,terms+seal)
    seal=f"UPDATE market_data.revisions SET state='sealed' WHERE catalog_id='{catalog}' AND id='{definition}';"
    assert market.count(seal)==1
    contracts={code:(str(uuid4()),str(uuid4())) for code,_ in SERIES}
    rows=(f"INSERT INTO market_data.curves SELECT(jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||jsonb_build_object('code','{PRODUCT}','name','Synthetic European option settlement'))).* "
        f"FROM market_data.curves c WHERE(catalog_id,revision_id,code)=('{catalog}','{definition}','synthetic_cash_option');"
        +''.join(f"INSERT INTO market_data.contracts SELECT '{catalog}','{definition}','{contract}',{refs},'future_option',c.delivery_from,c.delivery_to,s.expiry_at,NULL,s.code "
            f"FROM ref_data.futures_option_series s JOIN market_data.contracts c ON(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code)=(s.catalog_id,s.revision_id,s.underlying_series_code) "
            f"WHERE(s.catalog_id,s.revision_id,s.code,c.catalog_id,c.revision_id)=({refs},'{code}','{catalog}','{definition}');"
            f"INSERT INTO market_data.curve_members VALUES('{catalog}','{definition}','{PRODUCT}','{contract}',{ordinal});"
            f"INSERT INTO market_data.series VALUES('{catalog}','{definition}','{series}','{code}',{refs},'{contract}','nymex_on_quote',NULL);"
            for ordinal,(code,(contract,series)) in enumerate(contracts.items())))
    assert market.count(seal)==1
    market=market.replace(seal,rows+seal)
    def settlements(code,right):
        intrinsic=f"o.value-{STRIKE}" if right=='call' else f"{STRIKE}-o.value"
        return (f"INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object('id',gen_random_uuid(),'series_id','{contracts[code][1]}',"
            f"'source_event_id','{code}_'||o.source_event_id,'role','daily_settlement','value',greatest({intrinsic},0)+.2))).* FROM market_data.observations o JOIN market_data.series s "
            f"ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(s.catalog_id,s.revision_id,s.contract_id) "
            f"WHERE(o.catalog_id,o.definition_revision_id,c.futures_series_code,o.role)=('{catalog}','{definition}','{UNDERLYING}','trader_mark');")
    observations=''.join(settlements(code,right) for code,right in SERIES)
    return reference,market+'\nBEGIN;\n'+observations+'\nCOMMIT;',security,{**manifest,'europeanOptionContracts':{code:contract for code,(contract,_) in contracts.items()}}


def checks(query,manifest):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'));issuer=manifest['identityIssuer']
    definition=manifest['revisionId'];refs=f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
    def check(name,condition,detail=None):
        assert condition,name if detail is None else f'{name}: {detail}'
        print('PASS '+name,flush=True)
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest,issuer))
    # The managed read runs as a trader who may value a scenario, and this fixture's settings editor reads trades.
    query("INSERT INTO security.role_permissions SELECT role_id,'RunScenario' FROM security.role_permissions WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
    query("INSERT INTO security.role_permissions SELECT role_id,'ReadTrades' FROM security.role_permissions WHERE permission='WriteMarketDefinitions' ON CONFLICT DO NOTHING;")
    # A person who holds the Trade supervisor role and no other: AmendTrade without BookTrade.
    supervisor=str(uuid4())
    query(f"INSERT INTO security.principals VALUES('{supervisor}','human',true);"
        f"INSERT INTO security.identity_bindings VALUES('{issuer}','supervisor','{supervisor}','human_interactive');"
        f"INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{supervisor}','supervisor','Synthetic supervisor',true,1);"
        f"INSERT INTO security.role_grants SELECT gen_random_uuid(),'{supervisor}',id,'{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 day',false "
        "FROM security.roles WHERE code='trade_supervisor';")
    assert query(f"SELECT count(*) FROM security.role_grants WHERE principal_id='{supervisor}';")=='1'
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,profile='trader'):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('{issuer}','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture(day='2026-10-16'):return call(f"SELECT security.resolve_operation_context('{catalog}','{day}',NULL);")
    def refused(action,*codes):
        try:action()
        except RuntimeError as error:assert any(code in str(error) for code in codes),str(error)
        else:raise AssertionError('Expected '+' or '.join(codes))
    denied=('No booking access','futures_margin_denied','lifecycle_denied')

    stored=query(f"SELECT concat_ws('|',p.exercise_style,p.settlement_method,CASE WHEN p.contrary_instructions_allowed THEN 'instructions' ELSE 'no_instructions' END,"
        "(SELECT string_agg(s.code||':'||s.option_right,',' ORDER BY s.code) FROM ref_data.futures_option_series s WHERE(s.catalog_id,s.revision_id,s.product_code)=(p.catalog_id,p.revision_id,p.code))) "
        f"FROM ref_data.futures_option_products p WHERE(p.catalog_id,p.revision_id,p.code)=({refs},'{PRODUCT}');")
    check('a European product that accepts no contrary instructions is stored with its call and its put',
        stored=='european|exercise_into_future|no_instructions|synthetic_european_call:call,synthetic_european_put:put',stored)

    book=query(f"SELECT id FROM trading.books WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    entity=query(f"SELECT id FROM trading.legal_entities WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    party=query(f"SELECT id FROM trading.counterparties WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    booking=dict(legalEntityId=entity,legalEntityRevision=1,counterpartyId=party,counterpartyRevision=1)
    def book_option(series,lots,profile='trader'):
        return json.loads(call(f"SELECT security.book_futures_option('{catalog}','{uuid4()}','{book}','{definition}','{series}',{lots},0.1,'2026-10-30',"
            f"'premium_paid_up_front','2026-10-16T14:00:00Z',NULL,'futures_option_premium_v1',NULL,NULL,NULL,{literal(booking)})::text;",profile))
    long_call,long_put,short_call=book_option('synthetic_european_call',3),book_option('synthetic_european_put',2),book_option('synthetic_european_call',-1)
    american=book_option(AMERICAN,1)
    for profile in ('editor','supervisor'):refused(lambda:book_option('synthetic_european_call',1,profile),*denied)
    check('a trader books European options; a settings editor and a Trade supervisor cannot',
        query(f"SELECT count(*) FROM trading.futures_option_trade_revisions WHERE catalog_id='{catalog}';")=='4')

    contract,final=query(f"SELECT id||'|'||to_char(expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM market_data.contracts "
        f"WHERE(catalog_id,revision_id,futures_series_code)=('{catalog}','{definition}','{UNDERLYING}');").split('|')
    settled=("FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
        f"WHERE(o.catalog_id,o.definition_revision_id,o.observed_at)=('{catalog}','{definition}','{FRAME}')")
    codes=','.join(f"'{code}'" for code in (AMERICAN,*(code for code,_ in SERIES)))
    pins=json.loads(query(f"SELECT jsonb_agg(o.id ORDER BY o.id) {settled} AND(o.role='parameter' OR o.role='daily_settlement' AND(s.code IN({codes}) OR s.contract_id='{contract}'));"))
    settlement=query(f"SELECT trim_scale(o.value)::text {settled} AND o.role='daily_settlement' AND s.code='synthetic_european_call';")
    multiplier,increment=query("SELECT trim_scale(ref_data.futures_option_multiplier(s.catalog_id,s.revision_id,s.code))::text||'|'||trim_scale(q.clearing_increment)::text "
        "FROM ref_data.futures_option_series s JOIN ref_data.futures_option_products p ON(p.catalog_id,p.revision_id,p.code)=(s.catalog_id,s.revision_id,s.product_code) "
        "JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(p.catalog_id,p.revision_id,p.quote_code) "
        f"WHERE(s.catalog_id,s.revision_id,s.code)=({refs},'synthetic_european_call');").split('|')
    assert multiplier=='2500',multiplier
    directory=Path(query('SHOW unix_socket_directories;'));packet=directory/'european-option-read.json'
    packet.write_text(json.dumps(dict(manifest=manifest,host=str(directory),contextId=capture(),at=FRAME,bookId=book,pins=pins,futureContracts=[contract],
        americanContracts=[manifest['cashOptionContract']],europeanContracts=list(manifest['europeanOptionContracts'].values()),
        european=dict(series='synthetic_european_call',tradeId=long_call['tradeId'],bindingId=long_call['optionInstrumentId'],settlement=settlement,multiplier=multiplier,lots='3',
            clearingIncrement=increment),american=dict(series=AMERICAN,tradeId=american['tradeId']))));packet.chmod(0o600)
    read=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--european-option-read',str(packet)],
        cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=120)
    if read.returncode:raise RuntimeError(read.stdout+read.stderr)
    print(read.stdout.strip(),flush=True)

    # Nothing is recorded without configuration: a margin policy on the future, an action policy and the trade's profile.
    margin=dict(id=str(uuid4()),definitionRevisionId=definition,contractId=contract,settlementCurve='ice_cfb',referenceCatalogId=manifest['referenceCatalogId'],
        referenceRevisionId=manifest['referenceRevisionId'],currency='usd',fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',opening='include_opening_instant',
        increment='0.01',tie='to_even',stage='payment_total',roundingScope='held_binding_difference_payment_total_v1',implementation='dated_futures_margin_policy_v1',
        occurrences=[dict(id=str(uuid4()),at='2026-10-16T16:45:00Z',dueAt='2026-10-20T12:00:00Z',phase='daily'),dict(id=str(uuid4()),at=final,dueAt='2026-11-02T12:00:00Z',phase='final')])
    call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf=FRAME,policy=margin))});",'editor')
    policies={}
    for action,mode in (('option.exercise','manual'),('option.assignment','manual'),('option.expiry','review')):
        policies[action]=str(uuid4())
        call(f"SELECT lifecycle.save_policy('{catalog}',{literal(dict(operationId=str(uuid4()),policyId=policies[action],expectedRevision=0,action=action,mode=mode,fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
    def configure(trade,action,profile='trader'):
        call(f"SELECT lifecycle.save_option_profile('{catalog}',{literal(dict(operationId=str(uuid4()),tradeId=trade['tradeId'],action=action,expectedRevision=0,policyId=policies[action],policyRevision=1,marginPolicyId=margin['id'],marginPolicyRevision=1,futureMarkCurve='ice_cfb',expiryCurve='ice_cfb' if action=='option.expiry' else None))});",profile)
    def request(trade,action,*,at='2026-10-16T16:30:00Z',lots=None,evidence=None):
        return dict(operationId=str(uuid4()),context=dict(contextId=capture(at[:10]),asOf=at,businessDate=at[:10]),
            target=dict(handler='listed_option',scopeId=trade['tradeId'],bindingId=trade['optionInstrumentId']),action=action,policyId=policies[action],policyRevision=1,
            automatic=False,previewHash='0'*64,selection=dict(evidenceId=evidence or str(uuid4()),lots=lots,expectedEventVersion=0,profileRevision=1))
    def propose(body,profile='trader'):return json.loads(call(f"SELECT lifecycle.option_proposal('{catalog}',{literal(body)})::text;",profile))

    # The same request is proposed for the American holding and refused for the European ones by their style alone.
    for trade,action in ((american,'option.exercise'),(long_call,'option.exercise'),(short_call,'option.assignment')):configure(trade,action)
    assert Decimal(propose(request(american,'option.exercise',lots='1'))['details']['lots'])==1
    refused(lambda:propose(request(long_call,'option.exercise',lots='1')),'option_exercise_style')
    refused(lambda:propose(request(short_call,'option.assignment',lots='1')),'option_exercise_style')
    check('exercise and assignment of a European holding before expiry are refused in the proposal with option_exercise_style, and write nothing',
        query(f"SELECT count(*) FROM trading.futures_option_events WHERE catalog_id='{catalog}';")=='0')
    primitive=query.raw("SELECT pg_get_functiondef('security.apply_futures_option_event(uuid,uuid,uuid,text,numeric,timestamptz,uuid,uuid[],jsonb)'::regprocedure);")
    refusal="IF product.exercise_style='european' THEN RAISE EXCEPTION 'option_exercise_style' USING ERRCODE='P0001';END IF;"
    window='PERFORM ref_data.require_option_notice(product,series_row,event_kind,event_at);\n  IF event_at<r.execution_at OR security.futures_option_market_date'
    # No path reaches the primitive without passing the proposal first, so its refusal is read from its installed definition.
    check('the event primitive states the same refusal once, ahead of its notice and exercise-window tests',
        primitive.count(refusal)==1 and primitive.count(window)==1 and primitive.index(refusal)<primitive.index(window))

    # Expiry is unchanged: once the expiry instant has passed and the underlying's final daily settlement of that day is
    # known, the automatic rule decides. A settlement of 3.5 exercises the call struck at 3.10 and abandons the put.
    at=query(f"SELECT to_char(expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM ref_data.futures_option_series WHERE(catalog_id,revision_id,code)=({refs},'synthetic_european_call');")
    configure(long_call,'option.expiry');configure(long_put,'option.expiry','supervisor')
    def expiry(trade):return request(trade,'option.expiry',at=at,evidence=trade['tradeId'])
    refused(lambda:propose(expiry(long_call)),'option_expiry_settlement_missing')
    source=query("SELECT o.id FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
        f"WHERE(o.catalog_id,o.definition_revision_id,o.role,s.contract_id)=('{catalog}','{definition}','daily_settlement','{contract}') ORDER BY o.observed_at LIMIT 1;")
    official=str(uuid4())
    query(f"INSERT INTO market_data.observations SELECT catalog_id,'{official}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,"
        f"'synthetic_european_option_expiry','1','daily_settlement','final','{at}',clock_timestamp(),3.5,NULL FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{source}');")
    refused(lambda:propose(expiry(long_put),'supervisor'),*denied)
    for trade,outcome,future in ((long_call,'exercised','3'),(long_put,'abandoned',None)):
        body=expiry(trade);proposal=propose(body)
        assert proposal['details']['decision']==outcome and Decimal(str(proposal['details']['strike']))==Decimal(STRIKE),proposal
        body['previewHash']=proposal['hash'];result=json.loads(call(f"SELECT lifecycle.execute_option_action('{catalog}',{literal(body)})::text;"))
        assert result['outcome']==outcome and Decimal(result['remainingLots'])==0,result
        recorded=query("SELECT coalesce((SELECT trim_scale(r.lots)::text||'|'||trim_scale(r.execution_price)::text FROM trading.trade_revisions r "
            f"WHERE(r.catalog_id,r.trade_id,r.revision)=(e.catalog_id,e.futures_trade_id,1)),'none')||'|'||(e.settlement_observation_id='{official}')::text "
            f"FROM trading.futures_option_events e WHERE(e.catalog_id,e.trade_id)=('{catalog}','{trade['tradeId']}');")
        assert recorded==('none' if future is None else future+'|3.1')+'|true',recorded
    check('after the expiry instant the automatic rule exercises the European call into its future at the strike, under its margin policy, and abandons the put; a Trade supervisor configures a profile and cannot record the expiry',
        query("SELECT count(*) FROM settlement_data.futures_margin_adoptions a JOIN trading.futures_option_events e ON(e.catalog_id,e.futures_trade_id)=(a.catalog_id,a.trade_id) "
            f"WHERE(e.catalog_id,e.trade_id)=('{catalog}','{long_call['tradeId']}');")=='1' and
        query(f"SELECT string_agg(kind,',' ORDER BY kind) FROM trading.futures_option_events WHERE catalog_id='{catalog}';")=='expiry,expiry')


if __name__=='__main__':
    root=Path(__file__).resolve().parents[1]
    build=subprocess.run([os.environ.get('DOTNET','dotnet'),'build','tests/OpenEtrm.Api.Checks','--no-restore','-m:1','-nodeReuse:false'],cwd=root,capture_output=True,text=True,timeout=180)
    if build.returncode:raise RuntimeError(build.stdout+build.stderr)
    main(checks=checks,fixture_factory=fixture)
