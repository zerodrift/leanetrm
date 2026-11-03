"""Retained execution does not regenerate schedules or follow new mappings."""
import json
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from check_lng_cargo_schema import physical_selection
from lng_fixture import REVISION
from check_booked_side_programs import runtime_query


def run_checks(url,query,manifest,receipt):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    frame=initial_frame(c.call('workspace'),'2020-12-18')
    context={'snapshotId':frame['id'],'convention':'physical_lng_cargo_v1','knowledgeMode':'current'}
    original=json.loads(query(f"SELECT request FROM trading.trades WHERE(catalog_id,id)=('{manifest['catalogId']}','{receipt['tradeId']}');"))
    scope=query("SELECT pg_get_functiondef('security.lng_position_scope(uuid,uuid[],uuid[],uuid[])'::regprocedure);")
    assert scope.count('\nBEGIN\n')==1
    # Model a whole inventory whose other reference namespace is unreadable.
    # The native exact-trade branch stays unchanged and currently authorized.
    guarded=scope.replace('\nBEGIN\n',"\nBEGIN\n IF trades IS NULL THEN RAISE EXCEPTION 'Synthetic unrelated reference namespace is inaccessible' USING ERRCODE='42501';END IF;\n",1)
    try:
        query(guarded)
        selection=dict(context={**context,'convention':'trading_workspace_v1'},tradeId=receipt['tradeId'],bindingId=receipt['instrumentId'],revision=1,instrumentType='physical_lng')
        c.call('trading/lng',selection)
        assert Decimal(c.call('trading/position',selection)['holding']['presentValue'])==49000
        c.call('trading/positions',dict(context=selection['context'],bookIds=[],portfolioIds=[],offset=0,limit=64),expected=403)
    finally:
        query(scope)
    definitions=json.loads(query("SELECT jsonb_agg(pg_get_functiondef(p.oid)) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname=ANY(ARRAY['read_side_schedule_rows','read_side_schedule_edges','read_side_schedule_bindings','read_side_schedule_grids','read_side_schedule_roots','read_side_schedule_ports']);"))
    assert len(definitions)>=6
    try:
        for source in definitions:
            assert 'AS $function$' in source
            blocked=source.split('AS $function$',1)[0].replace('LANGUAGE sql','LANGUAGE plpgsql')+"AS $function$ BEGIN RAISE EXCEPTION 'lng_normalized_replay_forbidden'; END $function$;"
            query(blocked)
        value=c.call('lng/editor',dict(context=context,tradeId=receipt['tradeId']))
        assert Decimal(value['valuation']['presentValue'])==49000
        view=dict(mode='trades',bookIds=[],portfolioIds=[],groupBy=[],measures=[],pivotConvention=None)
        rows=c.call('trading/blotter',dict(context={**context,'convention':'trading_workspace_v1'},view=view))['holdings']
        assert Decimal(next(p for p in rows if p['tradeId']==receipt['tradeId'])['presentValue'])==49000
    finally:
        for source in definitions:query(source)
    # A later visible assignment has full physical/month coverage but no marks.
    # Its existence must make new entry ambiguous; it cannot replace booked pins.
    catalog=manifest['catalogId'];other=str(uuid4())
    query(f"""BEGIN;
      INSERT INTO market_data.revisions SELECT(jsonb_populate_record(NULL::market_data.revisions,to_jsonb(r)||jsonb_build_object('id','{other}','state','draft','generation',0))).*
      FROM market_data.revisions r WHERE(catalog_id,id)=('{catalog}','{REVISION}');
      DO $clone$ DECLARE t text;BEGIN
        FOREACH t IN ARRAY ARRAY['revision_periods','contracts','series','curves','curve_members','index_fixing_observations','lng_valuation_assignments'] LOOP
          EXECUTE format('INSERT INTO market_data.%I SELECT(jsonb_populate_record(NULL::market_data.%I,to_jsonb(r)||jsonb_build_object(''revision_id'',''{other}''))).* FROM market_data.%I r WHERE(catalog_id,revision_id)=(''{catalog}'',''{REVISION}'')',t,t,t);
        END LOOP;
      END $clone$;
      UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{other}');COMMIT;""")
    error=c.call('lng/resolve',{**context,'physical':physical_selection(manifest),'dischargeFrom':original['dischargeFrom'],'dischargeTo':original['dischargeTo']},expected=422)
    assert error['code']=='lng_price_mapping_ambiguous',error
    encoded=json.dumps(original).replace("'","''")
    replay=json.loads(runtime_query(query,manifest,f"SELECT security.book_physical_lng('{catalog}','{encoded}'::jsonb,'2020-12-18T16:00:00Z');"))
    assert replay==receipt
    loaded=c.call('lng/editor',dict(context=context,tradeId=receipt['tradeId']))
    assert loaded['terms']['incoterms']==original['incoterms'] and Decimal(loaded['valuation']['presentValue'])==49000
    print('PASS LNG retained execution: editor and Blotter use stored programmes without normalized readers; later ambiguous mapping blocks new entry while original retry and pins remain exact',flush=True)
