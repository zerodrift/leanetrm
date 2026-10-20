"""Qualify native-factor alias policies using a new tiny immutable bundle."""
import json
from uuid import uuid4


def run_checks(owner,body,query,manifest):
    catalog=manifest['catalogId'];revision,contract,series,observation=(str(uuid4()) for _ in range(4))
    aliases=['synthetic_native_alias_a','synthetic_native_alias_b']
    # Copy only immutable source terms into a new draft, then seal normally.
    # Existing sealed definitions and observations remain untouched.
    query(f"""BEGIN;
    INSERT INTO market_data.revisions SELECT(jsonb_populate_record(NULL::market_data.revisions,to_jsonb(r)||jsonb_build_object('id','{revision}','state','draft','generation',0))).* FROM market_data.revisions r WHERE(catalog_id,id)=('{catalog}','{manifest['revisionId']}');
    INSERT INTO market_data.contracts SELECT(jsonb_populate_record(NULL::market_data.contracts,to_jsonb(c)||jsonb_build_object('revision_id','{revision}','id','{contract}'))).* FROM market_data.contracts c WHERE(catalog_id,revision_id,id)=('{catalog}','{manifest['revisionId']}','50000000-0000-0000-0000-000000000002');
    INSERT INTO market_data.series SELECT(jsonb_populate_record(NULL::market_data.series,to_jsonb(s)||jsonb_build_object('revision_id','{revision}','id','{series}','code','synthetic_alias_native','contract_id','{contract}'))).* FROM market_data.series s WHERE(catalog_id,revision_id,id)=('{catalog}','{manifest['revisionId']}','90000000-0000-0000-0000-000000000002');
    INSERT INTO market_data.curves SELECT(jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||jsonb_build_object('revision_id','{revision}','code',a,'name',a))).* FROM market_data.curves c CROSS JOIN unnest(ARRAY['{aliases[0]}','{aliases[1]}'])a WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{manifest['revisionId']}','bridge_basis');
    INSERT INTO market_data.curve_members SELECT '{catalog}','{revision}',a,'{contract}',0 FROM unnest(ARRAY['{aliases[0]}','{aliases[1]}'])a;
    INSERT INTO market_data.revision_periods VALUES('{catalog}','{revision}','2020-01-01','2022-01-01');
    UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{revision}');
    INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object('id','{observation}','definition_revision_id','{revision}','series_id','{series}','source_event_id','synthetic_alias_{observation}','received_at',clock_timestamp(),'corrects_id',NULL))).* FROM market_data.observations o WHERE(catalog_id,id)=('{catalog}','70000000-0000-0000-0000-000000000002');
    COMMIT;""")
    assert query(f"SELECT count(*) FROM market_data.curves WHERE(catalog_id,revision_id)=('{catalog}','{revision}');")=='2'
    def human_token():
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',120);")
    members=[{'curve':code,'contractId':contract,'ordinal':0} for code in aliases]
    pins=[{'seriesId':series,'observationId':observation,'inputUse':'variable'}]
    def encoded(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    anchor=query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{human_token()}');SELECT security.register_market_close('{catalog}','{revision}','{uuid4()}','Synthetic native alias anchor','2020-12-18T16:00:00Z',{encoded(pins)},{encoded(members)});COMMIT;").splitlines()[-1]
    for second_bound,rejected in [(8,True),(7,False)]:
        request={**body,'schedule':{**body['schedule'],'requestId':str(uuid4()),'name':'Synthetic native alias policy'},
            'definition':{**body['definition'],'priorCloseId':anchor,'curveCodes':aliases,
                'leafMoves':[{'curve':aliases[0],'maxTicks':7},{'curve':aliases[1],'maxTicks':second_bound}]}}
        job=owner.call('jobs/pilot-close/create',request)
        try:
            query(f"UPDATE automation.jobs SET next_due_at='2020-12-19T16:00:00Z' WHERE id='{job['id']}';")
            claim=json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_next('{uuid4()}',120,true)::text;"))
            assert claim['jobId']==job['id']
            token=query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_pilot_context('{claim['id']}',{claim['fence']},'{claim['workerId']}');")
            marks=[{'seriesId':series,'anchorObservationId':observation,'moveTicks':0,'value':'0.1'}]
            statement=f"PERFORM automation.publish_pilot_close({encoded(marks)},'[]'::jsonb,{encoded(members)});"
            if rejected:statement=f"BEGIN {statement} RAISE EXCEPTION 'Conflicting native alias bounds were silently combined';EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'pilot_close_inputs' THEN RAISE;END IF;END;"
            query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}',security.capture_date_context('{catalog}','2020-12-19',NULL));SELECT security.use_trading_context('{catalog}','2020-12-19T16:00:00Z');DO $$BEGIN {statement} END$$;ROLLBACK;")
            assert query(f"SELECT count(*) FROM market_data.market_closes WHERE run_id='{claim['id']}';")=='0'
        finally:owner.call('jobs/control',{'requestId':str(uuid4()),'id':job['id'],'action':'cancel','expectedVersion':job['version']})
    print('PASS pilot native aliases: conflicting bounds reject; two direct aliases with the same explicit bound share one admissible native factor',flush=True)
