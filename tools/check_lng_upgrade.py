"""Populated additive upgrade and predecessor refusal in one owned rollback."""
import json
import re
from uuid import uuid4
from check_booked_side_programs import literal_program


def run_checks(query, root, manifest, retained):
    def body(sql):
        return re.sub(r'(?m)^(BEGIN|COMMIT);\s*$', '', sql)
    def literal(value):
        return "'"+json.dumps(value,separators=(',',':')).replace("'","''")+"'::jsonb"
    catalog=manifest['catalogId'];reference=manifest['referenceCatalogId'];revision=manifest['referenceRevisionId'];request=str(uuid4())
    unit={'revision':{'catalogId':reference,'revisionId':revision},'code':'test_gas_quantity'}
    currency={'revision':unit['revision'],'code':'usd'}
    header=dict(family='fixed',definitionRevisionId=manifest['revisionId'],referenceCatalogId=reference,referenceRevisionId=revision,
        economicIdentity='{"contract":"synthetic_upgrade_cash_v1"}',pricingBindingIdentity='',schemaVersion='side_schedule_blocks_v2',generatorVersion='whole_term_side_blocks_v2',
        manifest='a'*64,rowCount=7,edgeCount=7,bindingCount=0,rootCount=2,portCount=0,gridCount=0)
    rows=[]
    for n,(op,kind,role,parameters) in enumerate([
        ('quantity_literal','quantity','source',dict(numerator='1',denominator='1')),
        ('constant_price','price','source',dict(numerator='3',denominator='1')),
        ('amount','money','contribution',{}),('discount','factor','source',dict(policy='context_required_v1')),
        ('pv','money','intermediate',{}),('output_guard','money','root',dict(measure='present_value_v1',lifecycle='undue_payment_v1',pricePolicy='selected_context_v1',averaging='none',firstFixingAt='none',paymentRule='explicit_payment_date_v1',unadjustedPayment='none')),
        ('sum','money','root',{})]):
        rows.append(dict(ordinal=n,key=f'logical/{n}',role=role,operation=op,version=1,outputKind=kind,
            unit=unit if kind in ('quantity','price') else None,currency=currency if kind in ('price','money') else None,
            quoteMultiplier='1' if kind=='price' else None,deliveryFrom='2021-01-01',deliveryTo='2021-02-01',interval=None,
            observationFrom=None,observationTo=None,valueAt='2021-02-18T00:00:00Z',paymentOrdinal=0,occurrenceKey=None,termsDigest=None,parameters=parameters))
    rows[5].update(occurrenceKey='synthetic_upgrade_payment_v1',termsDigest='b'*64)
    edges=[dict(target=t,port=p,ordinal=0,source=s,sequence=n) for n,(t,p,s) in enumerate([
        (2,'quantity',0),(2,'price',1),(4,'amount',2),(4,'discount',3),(5,'amount',2),(5,'discounted',4),(6,'input',5)])]
    roots=[dict(name='cash',row=2,measure='contractual_amount',sequence=0),dict(name='pv',row=6,measure='pv',sequence=1)]
    program=json.dumps(literal_program(header,rows,roots),separators=(',',':')).encode().hex()
    migrations=[root/'db/migrations'/name for name in ['0176_lng_cargo_references.sql','0177_lng_cargo_programs.sql','0178_lng_cargo_admission.sql','0179_lng_portfolio_scope.sql']]
    upgrade='\n'.join(body(p.read_text()) for p in migrations)
    guarded='\n'.join(body(p.read_text()) for p in migrations[:2]).replace("'","''")
    loaded='BEGIN;\n'+'\n'.join(body(sql) for sql in retained)
    query(loaded+f"""
    CREATE TEMP TABLE lng_upgrade_id(id uuid,header jsonb);
    GRANT SELECT,INSERT ON lng_upgrade_id TO preview_runtime;
    SET LOCAL SESSION AUTHORIZATION preview_authenticator;
    SELECT set_config('lng.upgrade_token',security.issue_context('https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60)::text,true);
    SET LOCAL SESSION AUTHORIZATION preview_runtime;
    SELECT security.use_context(current_setting('lng.upgrade_token')::uuid);
    DO $stage$ DECLARE s uuid;c uuid='{catalog}';r uuid='{request}';BEGIN
      s=security.begin_side_schedule(c,r,0,'{{"test":"synthetic_upgrade"}}'::jsonb,'BookTrade',NULL,NULL,{literal(header)});
      PERFORM security.append_side_schedule_rows(c,r,s,{literal(rows)});
      PERFORM security.append_side_schedule_edges(c,r,s,{literal(edges)});
      PERFORM security.append_side_schedule_aux(c,r,s,'roots',{literal(roots)});
      PERFORM security.seal_side_schedule_program(c,r,s,decode('{program}','hex'));
      PERFORM security.seal_side_schedule(c,r,s,'{'a'*64}');
      INSERT INTO lng_upgrade_id VALUES(s,security.read_side_schedule_header(c,s,r,NULL,NULL,NULL));
    END $stage$;
    RESET SESSION AUTHORIZATION;
    CREATE TEMP TABLE lng_old_columns AS
    SELECT n.nspname,c.relname,string_agg(format('%I',a.attname),',' ORDER BY a.attnum) columns
    FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
    WHERE c.relkind='r' AND n.nspname IN('ref_data','market_data','trading','risk','power_data','settlement_data','presentation') GROUP BY n.nspname,c.relname;
    CREATE FUNCTION pg_temp.lng_legacy_state() RETURNS jsonb LANGUAGE plpgsql AS $state$
    DECLARE t record;result jsonb='{{}}';digest text;BEGIN
      FOR t IN SELECT * FROM lng_old_columns ORDER BY nspname,relname LOOP
        EXECUTE format('SELECT encode(sha256(convert_to(coalesce(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),''[]''::jsonb)::text,''UTF8'')),''hex'') FROM(SELECT %s FROM %I.%I)r',t.columns,t.nspname,t.relname) INTO digest;
        result=result||jsonb_build_object(t.nspname||'.'||t.relname,digest);
      END LOOP;RETURN result;
    END $state$;
    CREATE TEMP TABLE lng_upgrade_before AS SELECT pg_temp.lng_legacy_state() value;
    DO $lng_predecessor_check$ DECLARE original text=pg_get_functiondef('security.begin_side_schedule(uuid,uuid,integer,jsonb,text,uuid,integer,jsonb)'::regprocedure);BEGIN
      BEGIN
        EXECUTE regexp_replace(original,'BEGIN','BEGIN PERFORM 1;');
        EXECUTE '{guarded}';
        RAISE EXCEPTION 'Unexpected predecessor was accepted';
      EXCEPTION WHEN raise_exception THEN IF SQLERRM NOT LIKE 'lng_program_upgrade_predecessor:%' THEN RAISE;END IF;END;
      IF to_regclass('ref_data.lng_products') IS NOT NULL OR to_regclass('trading.lng_instruments') IS NOT NULL
      OR pg_get_functiondef('security.begin_side_schedule(uuid,uuid,integer,jsonb,text,uuid,integer,jsonb)'::regprocedure)<>original
      OR pg_temp.lng_legacy_state()<>(SELECT value FROM lng_upgrade_before) THEN RAISE EXCEPTION 'Refused LNG upgrade changed the old state';END IF;
    END $lng_predecessor_check$;
    """+upgrade+f"""
    DO $verify$ BEGIN
      IF pg_temp.lng_legacy_state()<>(SELECT value FROM lng_upgrade_before) OR(SELECT count(*) FROM trading.side_schedule_programs)<>1
      THEN RAISE EXCEPTION 'LNG upgrade changed retained rows or programme bytes';END IF;
    END $verify$;
    SET LOCAL SESSION AUTHORIZATION preview_runtime;
    DO $read$ DECLARE s uuid;before jsonb;BEGIN
      SELECT id,header INTO STRICT s,before FROM lng_upgrade_id;
      IF security.read_side_schedule_header('{catalog}',s,'{request}',NULL,NULL,NULL)<>before
      THEN RAISE EXCEPTION 'The old runtime schedule read changed during upgrade';END IF;
    END $read$;
    ROLLBACK;
    """)
    assert query("SELECT to_regclass('ref_data.lng_products') IS NULL;")=='t'
    print('PASS LNG upgrade: populated prior reference/market/trading rows and sealed programme bytes unchanged; old authorized reader preserved; unexpected predecessor refuses atomically',flush=True)
