"""The installed application has runtime contexts and no saved-context contract."""


def check_schema(query):
    retired=('market_data.temporal_contexts','market_data.market_contexts',
             'market_data.catalogue_contexts','trading.contexts')
    for relation in retired:
        assert query(f"SELECT to_regclass('{relation}') IS NULL;")=='t',f'Retired context store remains: {relation}'
    obsolete=query("""SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
        WHERE n.nspname NOT IN('pg_catalog','information_schema')
        AND (p.prosrc ~ '(market_data\\.(temporal_contexts|market_contexts|catalogue_contexts)|trading\\.contexts)'
            OR p.prosrc ~ 'security\\.read_(temporal|market|catalogue|trading)_contexts'
            OR p.prosrc ~ 'automation\\.lifecycle_sources'
            OR p.proname IN('capture_date_context','store_schedule_cut','store_workspace_source',
                'retain_workspace_write_source','retain_operation_read_source')) ORDER BY 1;""")
    assert not obsolete,'Active saved-context APIs or dependencies remain: '+obsolete
    assert query("SELECT to_regprocedure('risk.capture_history_clock(uuid,uuid,jsonb,jsonb)') IS NULL;")=='t','An id-only clock-capture entry point remains'
    assert query("SELECT to_regprocedure('risk.capture_workspace_clock(uuid,uuid,jsonb,jsonb)') IS NULL;")=='t','A tokenless clock-capture entry point remains'
    assert query("SELECT has_function_privilege('openetrm_risk_worker','security.workspace_cut_visible(uuid,uuid,date,timestamptz,integer,text,integer)','EXECUTE');")=='t','Worker cannot verify reconstructed inputs'
    assert query("SELECT has_function_privilege('openetrm_runtime','security.workspace_context_id(uuid,date,timestamptz)','EXECUTE');")=='t','Owner coordinate constraint cannot verify its correlation'
    complete=query("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='automation.runs'::regclass AND conname='occurrence_inputs_complete';")
    query("""BEGIN;
        CREATE TEMP TABLE synthetic_occurrence_inputs AS SELECT input_catalog_id,input_business_on,input_known_at,input_system_id,input_timeline,input_policy FROM automation.runs WITH NO DATA;
        ALTER TABLE synthetic_occurrence_inputs ADD CONSTRAINT complete_inputs """+complete+""";
        DO $$DECLARE valid jsonb='{"input_catalog_id":"00000000-0000-0000-0000-000000000001","input_business_on":"2020-12-18","input_known_at":"2026-01-01T00:00:00Z","input_system_id":"synthetic","input_timeline":1,"input_policy":"current_at_first_admission_v1"}';field text;
        BEGIN
         INSERT INTO synthetic_occurrence_inputs SELECT(jsonb_populate_record(NULL::synthetic_occurrence_inputs,valid)).*;
         INSERT INTO synthetic_occurrence_inputs DEFAULT VALUES;
         FOREACH field IN ARRAY ARRAY['input_catalog_id','input_business_on','input_known_at','input_system_id','input_timeline','input_policy'] LOOP
          BEGIN
           INSERT INTO synthetic_occurrence_inputs SELECT(jsonb_populate_record(NULL::synthetic_occurrence_inputs,valid-field)).*;
           RAISE EXCEPTION 'Missing occurrence input accepted: %',field;
          EXCEPTION WHEN check_violation THEN NULL;END;
         END LOOP;
        END$$;
        ROLLBACK;""")
    print('PASS no saved context relations, lookup APIs or active dependencies',flush=True)


def check_native_reads(query,manifest):
    from datetime import date,timedelta
    catalog=manifest['catalogId']
    for role,code in [('openetrm_runtime','read_source_denied'),('openetrm_risk_worker','risk_job_denied')]:
        query.raw(f"""BEGIN;SET LOCAL SESSION AUTHORIZATION {role};DO $$BEGIN
            PERFORM security.resolve_operation_context('{catalog}','2020-12-18',NULL);
            RAISE EXCEPTION 'Unbound runtime input selection succeeded';
            EXCEPTION WHEN insufficient_privilege THEN IF SQLERRM<>'{code}' THEN RAISE;END IF;END$$;ROLLBACK;""")
    def runtime(sql,finish=True):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        return (query if finish else query.raw)(f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');{sql}"+
            ('SELECT security.finish_operation_read();' if finish else '')+'COMMIT;')
    first=runtime(f"SELECT id,known_at,known_through,system_id,timeline FROM security.resolve_read_cut('{catalog}','2020-12-18',NULL,false);").split('|')
    later=runtime(f"SELECT id,known_at,known_through,system_id,timeline FROM security.resolve_read_cut('{catalog}','2020-12-18',NULL,false);").split('|')
    def args(c):return f"'{catalog}','{c[0]}','2020-12-18','{c[1]}',{c[2]},'{c[3]}',{c[4]}"
    def refused(sql,state,code):
        return f"DO $$BEGIN {sql} RAISE EXCEPTION 'Expected rejection';EXCEPTION WHEN SQLSTATE '{state}' THEN IF SQLERRM<>'{code}' THEN RAISE;END IF;END$$;"
    try:runtime('SELECT security.register_read_source('+args(first)+');',finish=False)
    except RuntimeError as error:assert 'read_source_not_released' in str(error),error
    else:raise AssertionError('A read committed operation inputs')
    bound='SELECT security.bind_operation_read('+args(first)+');'
    runtime(bound+refused('PERFORM security.register_read_source('+args(later)+');','P0001','temporal_context'))
    result=runtime('SELECT security.register_read_source('+args(later)+');'+bound+
        f"SELECT count(*) FROM security.operation_knowledge WHERE id='{later[0]}';")
    assert result=='0','A pre-registered source widened the parent ceiling'
    corrupt=first[:];corrupt[3]='different-database'
    runtime(refused('PERFORM security.register_read_source('+args(corrupt)+');','P0001','knowledge_coverage'))
    # More unique reads than the former lifetime limit, in independent bounded
    # operations. Every operation discloses its row then releases its coordinates.
    total=4105
    for offset in range(0,total,128):
        end=min(total,offset+128)
        runtime(f"""DO $$DECLARE day date;identifier uuid;BEGIN
         FOR n IN {offset}..{end-1} LOOP
          day=DATE '2040-01-01'+n;identifier=security.resolve_operation_context('{catalog}',day,'{first[1]}');
          IF(SELECT count(*) FROM security.operation_knowledge WHERE id=identifier)<>1 THEN RAISE EXCEPTION 'Missing operation inputs';END IF;
         END LOOP;
        END$$;""")
    assert query('SELECT count(*) FROM security.request_contexts WHERE read_sources IS NOT NULL OR calendar_cut IS NOT NULL OR market_context_id IS NOT NULL OR business_source_context_id IS NOT NULL;')=='0'
    print('PASS 4,105 distinct runtime reads, unbound authority refusals, parent ceilings, clock lineage and commit release',flush=True)


def run_checks(url,query,manifest,browser=None,run_worker=None):
    import json
    from uuid import uuid4
    from preview_client import PreviewClient,initial_frame
    from check_workspace_as_of import open_tab,prepare
    check_schema(query)
    check_native_reads(query,manifest)
    account=PreviewClient(url);account.bootstrap();account.call('profile',{'profile':'trader'})
    tab=open_tab(account)
    frame=initial_frame(tab.call('workspace'),'2020-12-18')
    selector={'snapshotId':frame['id'],'convention':'trading_workspace_v1','knowledgeMode':'current'}
    request={'context':selector,'view':{'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None},'bookingCatalog':'directory','summaries':[]}
    first=tab.call('trading/workspace',request);proof=tab.last_read_references.copy();context=first['catalog']['context']
    assert context==first['blotter']['context']
    exact=request | {'context':selector | {'knowledgeMode':'retained','contextId':context['contextId']}}
    for _ in range(3):
        current=tab.call('trading/workspace',request)
        assert current['catalog']['context']['knownAt']>context['knownAt']
        replay=tab.call('trading/workspace',exact,headers={'X-OpenETRM-Read-References':json.dumps(proof)})
        assert replay['catalog']['context']==context
        assert replay['blotter']['holdings']==first['blotter']['holdings']
    other=open_tab(account)
    other.call('trading/workspace',exact,expected=403,headers={'X-OpenETRM-Read-References':json.dumps(proof)})
    assert other.call('date-schedules/catalog',{'context':context},expected=422)['code']=='temporal_context','A public tuple was treated as a certified replay without lineage'
    bad={key:value+'corrupt' for key,value in proof.items()}
    assert tab.call('trading/workspace',exact,expected=422,headers={'X-OpenETRM-Read-References':json.dumps(bad)})['code']=='read_reference'
    editor=PreviewClient(url);editor.bootstrap();editor.call('profile',{'profile':'editor'})
    author=open_tab(editor);validation=prepare(author,'2020-12-18')
    mapping={'requestId':str(uuid4()),'code':'synthetic_runtime_validation','name':'Synthetic runtime validation','context':validation,'contracts':None,'dates':[{'label':'Delivery','date':'2021-02-01'}]}
    saved=author.call('date-schedules/contracts/save',mapping)
    new_author=open_tab(editor)
    assert new_author.call('date-schedules/contracts/save',mapping)==saved
    owner=query(f"SELECT validation_business_on::text||'|'||validation_known_at::text FROM market_data.contract_date_versions WHERE definition_id='{saved['id']}';")
    assert owner.startswith('2020-12-18|'),owner
    revised_context=prepare(author,'2020-12-18')
    edit=mapping | {'requestId':str(uuid4()),'id':saved['id'],'version':saved['version'],'generation':saved['generation'],
        'expectedState':'draft','context':revised_context,'name':'Synthetic revised runtime validation'}
    revised=author.call('date-schedules/contracts/save',edit)
    assert revised['generation']==saved['generation']+1 and revised['version']==saved['version']
    assert author.call('date-schedules/contracts/save',edit)==revised
    assert query(f"SELECT validation_context_id='{revised_context['contextId']}'::uuid AND validation_known_at='{revised_context['knownAt']}'::timestamptz FROM market_data.contract_date_versions WHERE definition_id='{saved['id']}';")=='t'
    print('PASS startup, independent current reads, exact historical replay, transport isolation and owner validation inputs',flush=True)
    if browser:browser()
    if run_worker:
        from seed_preview_ppa import seed_ppa
        from seed_demo_instruments import seed_ppas
        seed_ppa(url,manifest)
        ppa=PreviewClient(url);ppa.bootstrap();ppa.call('profile',{'profile':'trader'});seed_ppas(ppa,manifest)
        risk=PreviewClient(url);risk.bootstrap();risk.call('profile',{'profile':'risk'});risk=open_tab(risk)
        selected=initial_frame(risk.call('workspace'),'2020-12-18')
        policy={'snapshotId':selected['id'],'convention':'trading_workspace_v1','knowledgeMode':'current'}
        catalog=risk.call('portfolio/risk/catalog',{'context':policy,'bookIds':[],'portfolioIds':[]})
        references=risk.last_read_references.copy();source=catalog['context'];factor=catalog['factors'][0]
        calculation={'context':policy | {'knowledgeMode':'retained','contextId':source['contextId']},'bookIds':[],'portfolioIds':[],
            'detail':'books','convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],'pricingPolicy':catalog['pricingPolicy'],
            'bumps':[{'factor':factor['factor'],'size':'0.1','convention':catalog['bumpConvention']}],'stress':[]}
        command={'requestId':str(uuid4()),'request':calculation}
        job=risk.call('portfolio/risk/jobs',command,headers={'X-OpenETRM-Read-References':json.dumps(references)})
        assert job['state']=='queued',job
        admitted=query(f"SELECT input_business_on::text||'|'||input_known_at::text||'|'||input_system_id||'|'||input_timeline FROM risk.jobs WHERE id='{job['id']}';")
        # Another publication/working read cannot refresh an already admitted job.
        prepare(tab,'2020-12-19')
        risk.call('profile',{'profile':'risk'})
        assert risk.call('portfolio/risk/jobs',command,headers={'X-OpenETRM-Read-References':json.dumps(references)})['id']==job['id']
        conflict=json.loads(json.dumps(command));conflict['request']['bumps'][0]['size']='0.2'
        assert risk.call('portfolio/risk/jobs',conflict,expected=409)['code']=='risk_job_conflict'
        run_worker()
        reader=PreviewClient(url);reader.bootstrap();reader.call('profile',{'profile':'risk'})
        completed=reader.call('portfolio/risk/jobs/'+job['id'])
        assert completed['state']=='completed',completed
        assert completed['context']['knownAt']==source['knownAt']
        assert query(f"SELECT input_business_on::text||'|'||input_known_at::text||'|'||input_system_id||'|'||input_timeline FROM risk.jobs WHERE id='{job['id']}';")==admitted
        stored=query(f"SELECT frame::text||request::text FROM risk.jobs WHERE id='{job['id']}';")
        assert 'readReference' not in stored and 'X-OpenETRM' not in stored
        print('PASS separate worker reconstructs admitted inputs including PPA sources after tab reset; exact retry and unchanged cutoff',flush=True)
        # A directory authorizes each owner without spending one browser-proof
        # slot per internal source. The selected job still exposes its exact cut.
        for _ in range(8):
            listing=risk.call('portfolio/risk/catalog',{'context':policy,'bookIds':[],'portfolioIds':[]})
            extra=json.loads(json.dumps(command));extra['requestId']=str(uuid4());extra['request']['context']['contextId']=listing['context']['contextId']
            latest=risk.call('portfolio/risk/jobs',extra)
        directory=reader.call('portfolio/risk/jobs')
        assert len(directory)>=9 and not reader.last_read_references,directory
        reader.call('portfolio/risk/jobs/'+job['id'])
        assert set(reader.last_read_references)=={source['contextId']}
        try:
            query(f"""BEGIN;UPDATE risk.jobs target SET context_id=other.context_id,
                input_business_on=other.input_business_on,input_known_at=other.input_known_at,
                input_system_id=other.input_system_id,input_timeline=other.input_timeline
                FROM risk.jobs other WHERE target.id='{job['id']}' AND other.id='{latest['id']}';ROLLBACK;""")
        except RuntimeError as error:assert 'read_inputs_conflict' in str(error),error
        else:raise AssertionError('An immutable job accepted another complete input tuple')
        assert query(f"SELECT input_business_on::text||'|'||input_known_at::text||'|'||input_system_id||'|'||input_timeline FROM risk.jobs WHERE id='{job['id']}';")==admitted
        print('PASS nine distinct job cutoffs list without transport quota; selected job exposes only its own reference',flush=True)
    check_schema(query)
