"""Contracts for immutable Side schedule content and atomic publication."""


def run_checks(query):
    for part in ('grids','rows','edges','bindings','roots','ports'):
        assert query(f"SELECT to_regprocedure('security.read_side_schedule_{part}(uuid,uuid,uuid,uuid,uuid,timestamp with time zone,integer,integer)') IS NOT NULL;")=='t', \
            f'Complete retained schedules require bounded typed {part} reads'
    signatures=(
        'append_side_schedule_rows(uuid,uuid,uuid,jsonb)',
        'append_side_schedule_edges(uuid,uuid,uuid,jsonb)',
        'append_side_schedule_aux(uuid,uuid,uuid,text,jsonb)',
        'append_side_schedule_outputs(uuid,uuid,uuid,text,jsonb)',
        'seal_side_schedule(uuid,uuid,uuid,text)',
        'seal_side_schedule_request(uuid,uuid,jsonb)',
    )
    functions=','.join("'security."+signature+"'::regprocedure" for signature in signatures)
    assert query(f"""
        SELECT count(*) FROM pg_proc WHERE oid IN({functions})
         AND proconfig @> ARRAY['enable_nestloop=off','jit=off'];
    """)=='6', 'Bounded retained-schedule booking guards must disable LLVM compilation locally, including on cold first use'
    for table in ('sides', 'side_schedules', 'side_schedule_grids', 'side_schedule_rows', 'side_schedule_edges',
                  'side_schedule_bindings', 'side_schedule_seals', 'side_schedule_publications'):
        assert query(f"SELECT to_regclass('trading.{table}') IS NOT NULL;") == 't', \
            f'Retained Side schedules require trading.{table}'
    assert query("""
        SELECT count(*) FROM pg_constraint WHERE conrelid='trading.side_schedule_grids'::regclass
         AND contype='f' AND confrelid='ref_data.clock_rules'::regclass;
    """) == '1', 'A compact block grid must retain its exact clock version through a real foreign key'
    assert query("""
        SELECT count(*) FROM pg_constraint WHERE conrelid='trading.side_schedule_rows'::regclass
         AND contype='f' AND confrelid='trading.side_schedules'::regclass;
    """) == '1', 'Every logical row belongs to an exact immutable schedule'
    assert query("""
        SELECT count(*) FROM pg_constraint WHERE conrelid='trading.side_schedule_edges'::regclass
         AND contype='f' AND confrelid='trading.side_schedule_rows'::regclass;
    """) == '2', 'Every ordered edge has exact local source and target foreign keys'
    assert query("""
        SELECT count(*) FROM pg_constraint WHERE conrelid='trading.side_schedule_publications'::regclass
         AND contype='f' AND confrelid='trading.publications'::regclass;
    """) == '1', 'Business visibility must use a real final trading publication'
    assert query("""
        SELECT count(*) FROM pg_class WHERE oid IN('trading.side_schedule_rows'::regclass,
         'trading.side_schedule_edges'::regclass,'trading.side_schedule_bindings'::regclass)
         AND relrowsecurity AND relforcerowsecurity;
    """) == '3', 'Unpublished content needs forced row authorization'
    assert query("""
        SELECT count(*) FROM pg_policy WHERE polrelid IN('trading.side_schedule_rows'::regclass,
         'trading.side_schedule_edges'::regclass,'trading.side_schedule_bindings'::regclass)
         AND polname='temporal_read' AND NOT polpermissive;
    """) == '3', 'Each content table must inherit restrictive publication visibility'
    assert query("""
        SELECT count(*) FROM information_schema.role_table_grants
        WHERE grantee='openetrm_runtime' AND table_schema='trading'
         AND table_name IN('side_schedules','side_schedule_rows','side_schedule_edges','side_schedule_bindings')
         AND privilege_type IN('INSERT','UPDATE','DELETE');
    """) == '0', 'Clients cannot supply or mutate a retained pricing graph'
    print('PASS retained schedules: exact graph ownership and publication-scoped immutable content', flush=True)


def run_stage_checks(query, manifest):
    """Literal arithmetic provides an independent small complete pricing graph."""
    import json
    from uuid import uuid4

    catalog=manifest['catalogId'];reference=manifest['referenceCatalogId'];revision=manifest['referenceRevisionId']
    quantity=query(f"SELECT quantity_code FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=('{reference}','{revision}','test_gas_outright');")
    unit=dict(revision=dict(catalogId=reference,revisionId=revision),code=quantity)
    currency=dict(revision=dict(catalogId=reference,revisionId=revision),code='usd')
    request=str(uuid4())
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def runtime(statement,actor='trader'):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{actor}','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{statement} COMMIT;")
    header=dict(family='fixed',definitionRevisionId=manifest['revisionId'],referenceCatalogId=reference,referenceRevisionId=revision,
        economicIdentity='{"contract":"synthetic_schedule_check_v2"}',pricingBindingIdentity='',schemaVersion='side_schedule_blocks_v2',generatorVersion='whole_term_side_blocks_v2',
        manifest='a'*64,rowCount=7,edgeCount=7,bindingCount=0,rootCount=2,portCount=0,gridCount=0)
    begin=f"SELECT security.begin_side_schedule('{catalog}','{request}',0,{literal(dict(test='complete_literal_side'))},'BookTrade',NULL,NULL,{literal(header)});"
    schedule=runtime(begin)
    assert runtime(begin)==schedule, 'The same request must retain its original stage identity'
    def row(ordinal,operation,kind,role='intermediate',parameters=None):
        return dict(ordinal=ordinal,key=f'logical/{ordinal}',role=role,operation=operation,version=1,outputKind=kind,
            unit=unit if kind in ('quantity','price') else None,currency=currency if kind in ('price','money') else None,
            quoteMultiplier='1' if kind=='price' else None,deliveryFrom='2021-01-01',deliveryTo='2021-02-01',interval=None,
            observationFrom=None,observationTo=None,valueAt='2021-02-18T00:00:00Z',paymentOrdinal=0,
            occurrenceKey=None,termsDigest=None,parameters=parameters or {})
    rows=[row(0,'quantity_literal','quantity','source',dict(numerator='1',denominator='1')),
        row(1,'constant_price','price','source',dict(numerator='3',denominator='1')),
        row(2,'amount','money','contribution'),row(3,'discount','factor','source',dict(policy='context_required_v1')),
        row(4,'pv','money'),row(5,'output_guard','money','root',dict(measure='present_value_v1',lifecycle='undue_payment_v1',pricePolicy='selected_context_v1',
            averaging='none',firstFixingAt='none',paymentRule='explicit_payment_date_v1',unadjustedPayment='none')),
        row(6,'sum','money','root')]
    rows[5]=dict(rows[5],occurrenceKey='synthetic_scalar_payment_v2',termsDigest='b'*64)
    def append_rows(values):return f"SELECT security.append_side_schedule_rows('{catalog}','{request}','{schedule}',{literal(values)});"
    def reject(statement,code):
        return f"DO $$BEGIN BEGIN {statement.replace('SELECT ','PERFORM ',1)} RAISE EXCEPTION 'expected rejection';EXCEPTION WHEN OTHERS THEN IF position('{code}' in SQLERRM)=0 THEN RAISE;END IF;END;END$$;"
    def denied(statement):
        return f"DO $$BEGIN BEGIN {statement.replace('SELECT ','PERFORM ',1)} RAISE EXCEPTION 'expected authorization rejection';EXCEPTION WHEN insufficient_privilege THEN NULL;END;END$$;"
    # Deny before looking up an identifier: neither missing instruments nor
    # another actor's staged request may become an existence probe.
    runtime(denied(f"SELECT security.read_instrument_side_schedule_headers('{catalog}','{uuid4()}',NULL,NULL);"),'viewer')
    for operation in (request,str(uuid4())):
        for call in (f"lock_side_schedule_request('{catalog}','{operation}')",
                     f"read_side_schedule_state('{catalog}','{operation}','{schedule}')",
                     f"read_side_schedule_rows('{catalog}','{schedule}','{operation}',NULL,NULL,NULL,0,1)",
                     f"seal_side_schedule_request('{catalog}','{operation}','[]'::jsonb)",
                     f"publish_side_schedules('{catalog}','{operation}','{uuid4()}')",
                     f"publish_operation_side_schedules('{catalog}','{operation}')"):
            runtime(denied(f'SELECT security.{call};'),'viewer')
    runtime(append_rows(rows));runtime(append_rows(rows))
    typed_scope=f"'{catalog}','{schedule}','{request}',NULL,NULL,NULL"
    assert runtime(f"SELECT count(*) FROM security.read_side_schedule_rows({typed_scope},0,7);")=='7', \
        'Typed pages retain every logical row under the request authority'
    assert runtime(f"SELECT quote_multiplier FROM security.read_side_schedule_rows({typed_scope},1,1);")=='1', \
        'Financial numeric fields cross the typed boundary as exact text'
    for first,maximum in (('0','NULL'),('NULL','1'),('0','2049')):
        runtime(reject(f"SELECT count(*) FROM security.read_side_schedule_rows({typed_scope},{first},{maximum});",'side_schedule_page'))
    for part in ('grids','edges','bindings','roots','ports'):
        assert runtime(f"SELECT count(*) FROM security.read_side_schedule_{part}({typed_scope},0,7);")=='0'
    payload=json.dumps(rows).replace("'","''")
    chunk=f"SELECT security.append_side_schedule_chunk('{catalog}','{request}','{schedule}','rows',0,'{payload}');"
    runtime(chunk);runtime(chunk)
    def state():return json.loads(runtime(f"SELECT security.read_side_schedule_state('{catalog}','{request}','{schedule}');"))
    assert state()['sealedManifest'] is None and len(state()['chunks'])==1, 'Partial chunks cannot authorize sealed-content reuse'
    assert query(f"SELECT count(*) FROM trading.side_schedule_chunks WHERE schedule_id='{schedule}';")=='1', 'A verified content chunk must retain one resumable receipt'
    assert query(f"SELECT count(*) FROM trading.side_schedule_rows WHERE schedule_id='{schedule}';")=='7'
    assert runtime(f"SELECT count(*) FROM trading.side_schedule_rows WHERE schedule_id='{schedule}';")=='0', 'Staging must not be visible as business data'
    changed=[dict(rows[1],parameters=dict(numerator='4',denominator='1'))]
    runtime(reject(append_rows(changed),'side_schedule_chunk_conflict'))
    runtime(reject(chunk.replace(payload,json.dumps(changed).replace("'","''")),'side_schedule_chunk_conflict'))
    invalid=[dict(rows[1],parameters=dict(numerator='3',denominator='1',curve='invented'))]
    invalid_statement=append_rows(invalid).replace('SELECT ','PERFORM ',1)
    runtime(f"DO $$BEGIN BEGIN {invalid_statement} RAISE EXCEPTION 'Accepted an unknown price parameter';"
        "EXCEPTION WHEN check_violation THEN NULL;WHEN raise_exception THEN "
        "IF SQLERRM<>'side_schedule_chunk_conflict' THEN RAISE;END IF;END;END$$;")
    seal=f"SELECT security.seal_side_schedule('{catalog}','{request}','{schedule}','{'a'*64}');"
    runtime(reject(seal,'side_schedule_incomplete'))
    edges=[dict(target=t,port=p,ordinal=0,source=s,sequence=n) for n,(t,p,s) in enumerate([
        (2,'quantity',0),(2,'price',1),(4,'amount',2),(4,'discount',3),(5,'amount',2),(5,'discounted',4),(6,'input',5)])]
    runtime(f"SELECT security.append_side_schedule_edges('{catalog}','{request}','{schedule}',{literal(edges)});")
    roots=[dict(name='cash',row=2,measure='contractual_amount',sequence=0),dict(name='pv',row=6,measure='pv',sequence=1)]
    runtime(f"SELECT security.append_side_schedule_aux('{catalog}','{request}','{schedule}','roots',{literal(roots)});")
    from check_booked_side_programs import append_program,check_literal_program,literal_program,install_stage_prerequisite
    runtime(reject(seal,'side_schedule_missing'))
    program=literal_program(header,rows,roots)
    check_literal_program(runtime,query,catalog,request,schedule,program,reject)
    runtime(seal);runtime(seal)
    runtime(append_program(catalog,request,schedule,json.dumps(program,separators=(',',':'))))
    assert state()['sealedManifest']=='a'*64, 'Retries require the exact immutable content seal, independently of chunk presence'
    runtime(append_rows(rows));runtime(chunk)
    runtime(f"SELECT security.seal_side_schedule_request('{catalog}','{request}','[]'::jsonb);")
    runtime(f"SELECT security.lock_side_schedule_request('{catalog}','{request}');")
    assert query(f"SELECT count(*) FROM trading.side_schedule_seals WHERE schedule_id='{schedule}';")=='1'
    assert runtime(f"SELECT count(*) FROM trading.side_schedule_rows WHERE schedule_id='{schedule}';")=='0', 'Sealing must not publish a schedule'
    runtime(reject(begin.replace(literal(dict(test='complete_literal_side')),literal(dict(test='changed'))),'side_schedule_request_conflict'))
    def stage_graph(name,**changes):
        invalid_request=str(uuid4());invalid_header=dict(header,economicIdentity=json.dumps({'contract':name}),**changes)
        invalid_schedule=runtime(begin.replace(request,invalid_request).replace(literal(header),literal(invalid_header)))
        def scoped(statement):return statement.replace(request,invalid_request).replace(schedule,invalid_schedule)
        return scoped,invalid_schedule
    def complete_program_fixture(name,values,named,geometries=()):
        scoped,identifier=stage_graph(name,rowCount=len(values),rootCount=len(named),gridCount=len(geometries))
        if geometries:
            runtime(scoped(f"SELECT security.append_side_schedule_grids('{catalog}','{request}','{schedule}',{literal(geometries)});"))
        runtime(scoped(append_rows(values)))
        runtime(scoped(f"SELECT security.append_side_schedule_edges('{catalog}','{request}','{schedule}',{literal(edges)});"))
        runtime(scoped(f"SELECT security.append_side_schedule_aux('{catalog}','{request}','{schedule}','roots',{literal(named)});"))
        operation=query(f"SELECT request_id FROM trading.side_schedules WHERE id='{identifier}';")
        return operation,identifier
    def unreadable_program(name,values,named,geometries=(),code='side_schedule_program'):
        operation,identifier=complete_program_fixture(name,values,named,geometries)
        content=query(f"SELECT trading.side_program_document('{catalog}','{identifier}')::text;")
        runtime(reject(append_program(catalog,operation,identifier,content),code))
        assert query(f"SELECT count(*) FROM trading.side_schedule_programs WHERE schedule_id='{identifier}';")=='0', \
            'Invalid structure or a typed seal failure must roll back the executable insert'
    unreadable_program('synthetic_empty_program_key',[dict(r,key='') if r['ordinal']==0 else r for r in rows],roots)
    unreadable_program('synthetic_empty_program_root',rows,[dict(r,name='') if r['name']=='cash' else r for r in roots])
    unreadable_program('synthetic_empty_program_measure',rows,[dict(r,measure='') if r['name']=='cash' else r for r in roots])
    unreadable_program('synthetic_duplicate_program_pv',rows,[dict(r,measure='pv') for r in roots])
    unreadable_program('synthetic_orphan_program_row',rows+[row(7,'quantity_literal','quantity','source',dict(numerator='1',denominator='1'))],roots)
    unreadable_program('synthetic_unguarded_program_source',rows+[row(7,'discount','factor','source',dict(policy='context_required_v1'))],
        roots+[dict(name='unconsumed_factor',row=7,measure='factor',sequence=2)])
    incomplete,incomplete_id=stage_graph('synthetic_incomplete_executable')
    incomplete_request=query(f"SELECT request_id FROM trading.side_schedules WHERE id='{incomplete_id}';")
    content=query(f"SELECT trading.side_program_document('{catalog}','{incomplete_id}')::text;")
    runtime(reject(append_program(catalog,incomplete_request,incomplete_id,content),'side_schedule_program'))
    bad_source=[dict(r,operation='price_lookup',parameters=dict(selection='interval_attribute_lookup_v1')) if r['ordinal']==1 else r for r in rows]
    unreadable_program('synthetic_atomic_program_source_failure',bad_source,roots,code='side_schedule_sources')
    from concurrent.futures import ThreadPoolExecutor
    for conflicting in (False,True):
        name='synthetic_program_seal_race_'+str(conflicting)
        operation,identifier=complete_program_fixture(name,rows,roots)
        exact=literal_program(dict(header,economicIdentity=json.dumps({'contract':name})),rows,roots)
        first_payload=json.dumps(exact,separators=(',',':'))
        other_payload=json.dumps(exact,indent=1) if conflicting else first_payload
        def attempt(payload):
            try:runtime(append_program(catalog,operation,identifier,payload));return 'sealed'
            except RuntimeError as error:
                if 'side_schedule_program_conflict' not in str(error):raise
                return 'conflict'
        with ThreadPoolExecutor(max_workers=2) as executor:
            outcomes=list(executor.map(attempt,(first_payload,other_payload)))
        assert sorted(outcomes)==(['conflict','sealed'] if conflicting else ['sealed','sealed']), \
            'Concurrent retries admit one exact artifact and reject different bytes'
        assert query(f"SELECT (SELECT count(*) FROM trading.side_schedule_programs WHERE schedule_id='{identifier}')::text||':'||(SELECT count(*) FROM trading.side_schedule_seals WHERE schedule_id='{identifier}')::text;")=='1:1'
    print('PASS booked program sealing: unreadable/incomplete artifacts refuse, typed failures roll back bytes and concurrent retries retain one seal',flush=True)
    # Four delivery cells use one retained geometry and seven vector/scalar
    # operations. Delivery cardinality must not manufacture SQL operation rows.
    vector,vector_id=stage_graph('synthetic_four_cell_block_v2',gridCount=1)
    clock=query(f"SELECT code FROM ref_data.clock_rules WHERE(catalog_id,revision_id)=('{reference}','{revision}') AND kind='fixed_offset' AND fixed_offset_seconds=0 ORDER BY code LIMIT 1;")
    assert clock, 'The synthetic block fixture requires one exact UTC clock'
    day=int(query("SELECT DATE '2021-01-01'-DATE '0001-01-01';"))
    grid=dict(ordinal=0,key='delivery/2021-01',clock=dict(revision=dict(catalogId=reference,revisionId=revision),code=clock),
        month='2021-01-01',firstRowOrdinal=0,origin='2021-01-01T00:00:00Z',stepTicks=36000000000,axisCount=4,
        selection=[0,1,2,3],lengths=[1,1,1,1],operatingDays=[day]*4,localMinutes=[0,60,120,180],occurrences=[1]*4)
    unreadable_program('synthetic_unused_program_grid',rows,roots,[grid])
    grid_append=f"SELECT security.append_side_schedule_grids('{catalog}','{request}','{schedule}',{literal([grid])});"
    runtime(vector(grid_append));runtime(vector(grid_append))
    assert runtime(vector(f"SELECT cardinality(selection) FROM security.read_side_schedule_grids({typed_scope},0,1);"))=='4'
    assert runtime(f"SELECT count(*) FROM trading.side_schedule_grids WHERE schedule_id='{vector_id}';")=='0', \
        'Unpublished vector geometry must remain private to its authorized request'
    vector_rows=[dict(r,grid=0,deliveryFrom=None,deliveryTo=None,interval=None) if r['ordinal'] in (0,2,4,5) else dict(r) for r in rows]
    vector_rows[5]=dict(vector_rows[5],occurrenceKey=None,termsDigest=None,
        parameters=dict(vector_rows[5]['parameters'],lifecycle='interval_payment_v1',measure='value_excluding_overdue_v1'))
    vector_rows[6]=dict(vector_rows[6],operation='sum_cells')
    unreadable_program('synthetic_oversized_program_grid',vector_rows,roots,[dict(grid,axisCount=2147483647)])
    runtime(vector(append_rows(vector_rows)))
    runtime(vector(f"SELECT security.append_side_schedule_edges('{catalog}','{request}','{schedule}',{literal(edges)});"))
    runtime(vector(f"SELECT security.append_side_schedule_aux('{catalog}','{request}','{schedule}','roots',{literal(roots)});"))
    vector_header=dict(header,economicIdentity=json.dumps({'contract':'synthetic_four_cell_block_v2'}),gridCount=1)
    vector_request=query(f"SELECT request_id FROM trading.side_schedules WHERE id='{vector_id}';")
    runtime(append_program(catalog,vector_request,vector_id,json.dumps(literal_program(vector_header,vector_rows,roots,[grid]),separators=(',',':'))))
    runtime(vector(seal));runtime(vector(seal))
    assert query(f"SELECT (SELECT count(*) FROM trading.side_schedule_grids WHERE schedule_id='{vector_id}')::text||':'||(SELECT count(*) FROM trading.side_schedule_rows WHERE schedule_id='{vector_id}')::text;")=='1:7'
    invalid_grid,invalid_grid_id=stage_graph('synthetic_misaligned_block_grid_v2',gridCount=1)
    malformed=dict(grid,lengths=[1,1,1])
    statement=invalid_grid(grid_append.replace(literal([grid]),literal([malformed]))).replace('SELECT ','PERFORM ',1)
    runtime(f"DO $$BEGIN BEGIN {statement} RAISE EXCEPTION 'Accepted unequal geometry columns';EXCEPTION WHEN check_violation THEN NULL;END;END$$;")
    assert query(f"SELECT count(*) FROM trading.side_schedule_grids WHERE schedule_id='{invalid_grid_id}';")=='0'
    absent_clock=dict(grid,clock=dict(grid['clock'],code='synthetic_missing_clock'))
    statement=invalid_grid(grid_append.replace(literal([grid]),literal([absent_clock]))).replace('SELECT ','PERFORM ',1)
    runtime(f"DO $$BEGIN BEGIN {statement} RAISE EXCEPTION 'Accepted an unavailable exact clock';EXCEPTION WHEN foreign_key_violation THEN NULL;END;END$$;")
    mismatch,_=stage_graph('synthetic_incompatible_vector_windows_v2',gridCount=2)
    other_grid=dict(grid,ordinal=1,key='shifted_delivery',origin='2021-01-01T01:00:00Z',localMinutes=[60,120,180,240])
    runtime(mismatch(grid_append.replace(literal([grid]),literal([grid,other_grid]))))
    mismatched_rows=[dict(r,grid=1) if r['ordinal']==2 else r for r in vector_rows]
    runtime(mismatch(append_rows(mismatched_rows)))
    runtime(mismatch(f"SELECT security.append_side_schedule_edges('{catalog}','{request}','{schedule}',{literal(edges)});"))
    runtime(mismatch(f"SELECT security.append_side_schedule_aux('{catalog}','{request}','{schedule}','roots',{literal(roots)});"))
    runtime(reject(mismatch(seal),'side_schedule_grid'))
    def reject_graph(name,values,connections,code):
        scoped,_=stage_graph(name)
        runtime(scoped(append_rows(values)))
        runtime(scoped(f"SELECT security.append_side_schedule_edges('{catalog}','{request}','{schedule}',{literal(connections)});"))
        runtime(scoped(f"SELECT security.append_side_schedule_aux('{catalog}','{request}','{schedule}','roots',{literal(roots)});"))
        runtime(reject(scoped(seal),code))
    missing_rows=[dict(r,operation='price_lookup',parameters={'selection':'interval_attribute_lookup_v1'}) if r['ordinal']==1 else r for r in rows]
    reject_graph('synthetic_missing_price_source_v1',missing_rows,edges,'side_schedule_sources')
    invalid_edges=[dict(e,port='unused_quantity') if e['target']==2 and e['port']=='quantity' else e for e in edges]
    reject_graph('synthetic_unknown_input_port_v1',rows,invalid_edges,'side_schedule_ports')
    duplicate_keys=[dict(r,key=rows[0]['key']) if r['ordinal']==1 else r for r in rows]
    reject_graph('synthetic_duplicate_logical_key_v1',duplicate_keys,edges,'side_schedule_topology')
    wrong_unit=[dict(r,unit=dict(unit,code='test_power_quantity')) if r['ordinal']==0 else r for r in rows]
    reject_graph('synthetic_incompatible_amount_unit_v1',wrong_unit,edges,'side_schedule_ports')
    duplicate,duplicate_id=stage_graph('synthetic_conflicting_batch_v1')
    conflicting=[rows[0],dict(rows[0],parameters=dict(numerator='2',denominator='1'))]
    runtime(reject(duplicate(append_rows(conflicting)),'side_schedule_chunk_conflict'))
    assert query(f"SELECT count(*) FROM trading.side_schedule_rows WHERE schedule_id='{duplicate_id}';")=='0', \
        'Conflicting facts inside one batch must roll back every inserted row'
    extra,extra_id=stage_graph('synthetic_unused_binding_key_v1')
    runtime(extra(append_rows(rows)))
    binding=dict(row_ordinal=1,port='index',kind='index',reference_catalog_id=reference,
        reference_revision_id=revision,index_code='test_price',sequence_ordinal=0)
    def append_binding(value):return extra(f"SELECT security.append_side_schedule_aux('{catalog}','{request}','{schedule}','bindings',{literal([value])});")
    runtime(append_binding(binding))
    ambiguous=dict(binding,port='another_index',sequence_ordinal=1,location_code='test_hub_a')
    statement=append_binding(ambiguous).replace('SELECT ','PERFORM ',1)
    runtime(f"DO $$BEGIN BEGIN {statement} RAISE EXCEPTION 'Accepted an unrelated exact location on an index binding';EXCEPTION WHEN check_violation THEN NULL;END;END$$;")
    assert query(f"SELECT count(*) FROM trading.side_schedule_bindings WHERE schedule_id='{extra_id}';")=='1', \
        'An unrelated known reference must refuse without inserting an ambiguous binding'
    fixing=json.loads(query(f"""
        SELECT (to_jsonb(f)||jsonb_build_object('curve',m.curve_code,'contract',s.contract_id,
          'quantity',q.quantity_code,'currency',q.currency_code))::text
        FROM market_data.index_fixing_observations f
        JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(f.catalog_id,f.revision_id,f.series_id)
        JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(s.catalog_id,s.revision_id,s.contract_id)
        JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.revision_id,m.curve_code)
        JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(f.reference_catalog_id,f.reference_revision_id,f.quote_code)
        WHERE f.catalog_id='{catalog}' AND f.revision_id='{manifest['revisionId']}' AND c.quote_code=f.quote_code
          AND f.observation_to-f.observation_from>1 AND q.multiplier=1 LIMIT 1;
    """))
    selected=['reference',['reference_revision',reference,revision],fixing['index_code']]
    selection=json.dumps(dict(contract='price_settlement_selection_v1',CurveTargetIndex=selected,Mode=1,
        ProjectionPolicy=None,SelectedIndex=selected),separators=(',',':'))
    lookup_rows=[dict(r,unit=dict(unit,code=fixing['quantity']) if r['outputKind'] in ('price','quantity') else None,
        currency=dict(currency,code=fixing['currency']) if r['outputKind'] in ('price','money') else None) for r in rows]
    lookup_rows[1]=dict(lookup_rows[1],operation='price_lookup',parameters=dict(selection=selection),
        observationFrom=fixing['observation_from'],observationTo=fixing['observation_to'])
    bindings=[dict(row_ordinal=1,port='market',kind='contract_price',source_catalog_id=catalog,
        definition_revision_id=fixing['revision_id'],curve_code=fixing['curve'],contract_id=fixing['contract'],sequence_ordinal=0),
        dict(row_ordinal=1,port='index',kind='index',reference_catalog_id=reference,reference_revision_id=revision,index_code=fixing['index_code'],sequence_ordinal=1),
        dict(row_ordinal=1,port='quote',kind='quote',reference_catalog_id=reference,reference_revision_id=revision,quote_code=fixing['quote_code'],sequence_ordinal=2),
        dict(row_ordinal=1,port='fixing',kind='index_fixing',reference_catalog_id=reference,reference_revision_id=revision,
            source_code=fixing['source_code'],source_catalog_id=catalog,definition_revision_id=fixing['revision_id'],series_id=fixing['series_id'],
            fixing_index_code=fixing['index_code'],fixing_from=fixing['observation_from'],fixing_to=fixing['observation_to'],
            quote_code=fixing['quote_code'],coordinate='published_index_observations_v1',sequence_ordinal=3)]
    for mismatch in (False,True):
        scoped,scoped_schedule=stage_graph('synthetic_fixing_window_'+str(mismatch),family='floating',bindingCount=4)
        proposed=[dict(r) for r in lookup_rows]
        if mismatch:
            from datetime import date,timedelta
            proposed[1]['observationFrom']=(date.fromisoformat(fixing['observation_from'])+timedelta(days=1)).isoformat()
        runtime(scoped(append_rows(proposed)))
        runtime(scoped(f"SELECT security.append_side_schedule_edges('{catalog}','{request}','{schedule}',{literal(edges)});"))
        runtime(scoped(f"SELECT security.append_side_schedule_aux('{catalog}','{request}','{schedule}','bindings',{literal(bindings)});"))
        runtime(scoped(f"SELECT security.append_side_schedule_aux('{catalog}','{request}','{schedule}','roots',{literal(roots)});"))
        if not mismatch:
            scoped_request=query(f"SELECT request_id FROM trading.side_schedules WHERE id='{scoped_schedule}';")
            install_stage_prerequisite(runtime,query,catalog,scoped_request,scoped_schedule)
        runtime(reject(scoped(seal),'side_schedule_sources') if mismatch else scoped(seal))
    obsolete=[]
    for variant in ('missing_fixing','first_fixing_marker'):
        proposed=[dict(row) for row in lookup_rows]
        selected_bindings=bindings[:-1] if variant=='missing_fixing' else bindings
        if variant=='first_fixing_marker':
            proposed[5]=dict(proposed[5],parameters=dict(proposed[5]['parameters'],firstFixingAt='2020-12-31T12:00:00.0000000+00:00'))
        scoped,scoped_schedule=stage_graph('synthetic_obsolete_'+variant,family='floating',bindingCount=len(selected_bindings))
        runtime(scoped(append_rows(proposed)))
        runtime(scoped(f"SELECT security.append_side_schedule_edges('{catalog}','{request}','{schedule}',{literal(edges)});"))
        runtime(scoped(f"SELECT security.append_side_schedule_aux('{catalog}','{request}','{schedule}','bindings',{literal(selected_bindings)});"))
        runtime(scoped(f"SELECT security.append_side_schedule_aux('{catalog}','{request}','{schedule}','roots',{literal(roots)});"))
        scoped_request=query(f"SELECT request_id FROM trading.side_schedules WHERE id='{scoped_schedule}';")
        # Derive bytes from the same staged rows: this must fail source admission,
        # not a deliberately mismatched bytecode or a missing artifact.
        content=query(f"SELECT trading.side_program_document('{catalog}','{scoped_schedule}')::text;")
        try:
            runtime(reject(append_program(catalog,scoped_request,scoped_schedule,content),'side_schedule_sources'))
        except RuntimeError as error:
            if 'expected rejection' not in str(error):
                raise
            obsolete.append(variant+': '+str(error))
    assert not obsolete, 'Obsolete dated programs passed sealing: '+str(obsolete)
    print('PASS dated source sealing refuses coherent bytes with absent fixing or obsolete first-fixing timestamp',flush=True)
    for mismatched in (False,True):
        operation=str(uuid4());name='synthetic_quantity_subselection_'+str(mismatched)
        output_roots=roots+[dict(name='quantity',row=0,measure='schedule_quantity',sequence=2)]
        links=[dict(target=1,port='synthetic_energy/0',source=0,output='quantity')]
        target_grid=dict(grid,origin='2021-01-01T05:00:00Z' if mismatched else '2021-01-01T01:00:00Z',
            firstRowOrdinal=1,axisCount=2,selection=[0,1],lengths=[1,1],operatingDays=[day]*2,
            localMinutes=[300,360] if mismatched else [60,120],occurrences=[1,1])
        for slot,geometry in ((0,grid),(1,target_grid)):
            proposed=dict(header,economicIdentity=json.dumps(dict(contract=name,slot=slot)),gridCount=1,rootCount=3,
                portCount=slot,bindingCount=slot)
            identifier=runtime(f"SELECT security.begin_side_schedule('{catalog}','{operation}',{slot},{literal(dict(test=name))},'BookTrade',NULL,NULL,{literal(proposed)});")
            def write(function,*args):
                arguments=','.join("'"+arg.replace("'","''")+"'" if isinstance(arg,str) else literal(arg) for arg in args)
                return runtime(f"SELECT security.{function}('{catalog}','{operation}','{identifier}',{arguments});")
            write('append_side_schedule_grids',[geometry])
            proposed_rows=[dict(r) for r in vector_rows]
            if slot:
                proposed_rows[0]=dict(proposed_rows[0],operation='quantity_input',parameters=dict(stage='declared_port_v1'))
                write('append_side_schedule_aux','ports',[dict(name='synthetic_energy/0',slot=dict(role=1,ordinal=0),kind='quantity',unit=unit,shapeIdentity='synthetic_full_geometry_v2',sequence=0)])
            write('append_side_schedule_rows',proposed_rows);write('append_side_schedule_edges',edges)
            if slot:
                write('append_side_schedule_aux','bindings',[dict(row_ordinal=0,port='quantity',kind='sibling_quantity',
                    reference_catalog_id=reference,reference_revision_id=revision,quantity_code=quantity,
                    coordinate='synthetic_energy/0',position=0,sequence_ordinal=0)])
            write('append_side_schedule_aux','roots',output_roots)
            write('append_side_schedule_outputs','sets',[dict(name='quantity',unit=unit,shapeIdentity='synthetic_full_geometry_v2')])
            write('append_side_schedule_outputs','members',[dict(set='quantity',root='quantity')])
            install_stage_prerequisite(runtime,query,catalog,operation,identifier)
            runtime(f"SELECT security.seal_side_schedule('{catalog}','{operation}','{identifier}','{'a'*64}');")
        composition=f"SELECT security.seal_side_schedule_request('{catalog}','{operation}',{literal(links)});"
        runtime(reject(composition,'side_schedule_ports') if mismatched else composition)
    def compose_blocks(name,source_grids,target_grids,refused=False):
        """Different storage partitions must describe the same exact logical cells."""
        operation=str(uuid4());port='synthetic_energy/0'
        for slot,geometries in enumerate((source_grids,target_grids)):
            count=len(geometries)
            proposed=dict(header,economicIdentity=json.dumps(dict(contract=name,slot=slot)),
                gridCount=count,rowCount=7*count+1,edgeCount=8*count,rootCount=2*count+1,
                portCount=slot,bindingCount=slot*count)
            identifier=runtime(f"SELECT security.begin_side_schedule('{catalog}','{operation}',{slot},{literal(dict(test=name))},'BookTrade',NULL,NULL,{literal(proposed)});")
            def write(function,*args):
                arguments=','.join("'"+arg.replace("'","''")+"'" if isinstance(arg,str) else literal(arg) for arg in args)
                return runtime(f"SELECT security.{function}('{catalog}','{operation}','{identifier}',{arguments});")
            values=[];connections=[];named=[];sources=[]
            for ordinal,geometry in enumerate(geometries):
                base=7*ordinal
                for r in vector_rows:
                    value=dict(r,ordinal=base+r['ordinal'],key=f"block/{ordinal}/{r['key']}")
                    if value.get('grid') is not None:value['grid']=ordinal
                    if r['ordinal']==6:value['role']='intermediate'
                    if slot and r['ordinal']==0:
                        value.update(operation='quantity_input',parameters=dict(stage='declared_port_v1'))
                    values.append(value)
                for e in edges:connections.append(dict(e,target=base+e['target'],source=base+e['source'],sequence=len(connections)))
                named.extend([dict(name=f'cash/{ordinal}',row=base+2,measure='contractual_amount',sequence=len(named)),
                    dict(name=f'quantity/{ordinal}',row=base,measure='schedule_quantity',sequence=len(named)+1)])
                sources.append(dict(set='quantity',root=f'quantity/{ordinal}'))
            values.append(dict(row(7*count,'sum','money','root'),key='pv'))
            for ordinal in range(count):
                connections.append(dict(target=7*count,port='input',ordinal=ordinal,source=7*ordinal+6,sequence=len(connections)))
            named.append(dict(name='pv',row=7*count,measure='pv',sequence=len(named)))
            write('append_side_schedule_grids',[dict(g,ordinal=n,key=f'geometry/{n}') for n,g in enumerate(geometries)])
            if slot:write('append_side_schedule_aux','ports',[dict(name=port,slot=dict(role=1,ordinal=0),kind='quantity',unit=unit,shapeIdentity='synthetic_full_geometry_v2',sequence=0)])
            write('append_side_schedule_rows',values);write('append_side_schedule_edges',connections)
            if slot:write('append_side_schedule_aux','bindings',[dict(row_ordinal=7*n,port='quantity',kind='sibling_quantity',
                reference_catalog_id=reference,reference_revision_id=revision,quantity_code=quantity,
                coordinate=port,position=0,sequence_ordinal=n) for n in range(count)])
            write('append_side_schedule_aux','roots',named)
            write('append_side_schedule_outputs','sets',[dict(name='quantity',unit=unit,shapeIdentity='synthetic_full_geometry_v2')])
            write('append_side_schedule_outputs','members',sources)
            install_stage_prerequisite(runtime,query,catalog,operation,identifier)
            runtime(f"SELECT security.seal_side_schedule('{catalog}','{operation}','{identifier}','{'a'*64}');")
        composition=f"SELECT security.seal_side_schedule_request('{catalog}','{operation}',{literal([dict(target=1,port=port,source=0,output='quantity')])});"
        runtime(reject(composition,'side_schedule_ports') if refused else composition)
    first=dict(grid,axisCount=2,selection=[0,1],lengths=[1,1],operatingDays=[day]*2,localMinutes=[0,60],occurrences=[1,1])
    second=dict(first,origin='2021-01-01T02:00:00Z',firstRowOrdinal=2,localMinutes=[120,180])
    compose_blocks('synthetic_exact_month_block_v2',[grid],[grid])
    compose_blocks('synthetic_quantity_union_v2',[first,second],[grid])
    compose_blocks('synthetic_payment_price_split_v2',[grid],[first,second])
    # Coarse block bounds overlap, but their selected cells are disjoint.
    even=dict(first,axisCount=4,selection=[0,2],localMinutes=[0,120])
    odd=dict(even,origin='2021-01-01T01:00:00Z',axisCount=3,localMinutes=[60,180])
    compose_blocks('synthetic_sparse_quantity_union_v2',[even,odd],[grid])
    compose_blocks('synthetic_duplicate_source_cells_v2',[grid,grid],[grid],True)
    compose_blocks('synthetic_overlapping_source_labels_v2',[grid,dict(first,localMinutes=[120,180])],[grid],True)
    compose_blocks('synthetic_source_label_mismatch_v2',[first,dict(second,localMinutes=[0,60])],[grid],True)
    compose_blocks('synthetic_duplicate_required_cells_v2',[grid],[first,first],True)
    print('PASS retained schedule staging: complete graph, invisible content, exact chunk replay, conflicts and immutable seal',flush=True)


def run_temporal_checks(url,query,manifest):
    """A shared fixed Side does not inherit an unrelated curve's validity start."""
    from uuid import uuid4
    from preview_client import PreviewClient
    from preview_fixture import trade_booking
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'trader'})
    frames=client.call('workspace')['frames']
    later=next(f for f in frames if f['at'].startswith('2020-12-18'))
    earlier=next(f for f in frames if f['at'].startswith('2020-12-15'))
    from check_booked_side_programs import capture_context
    before_programs=capture_context(query,manifest,'2020-12-18')
    def context(frame):return dict(snapshotId=frame['id'],knowledgeMode='current',convention='independent_side_financial_swap_v2')
    catalog=client.call('swaps/catalog',context(later))
    primary=next(i for i in catalog['indices'] if i['curve']=='base_forward')
    secondary=next(i for i in catalog['indices'] if i['curve']=='daily_gas')
    payment=dict(frequency='monthly',rule='days_after_period_end',days=18,dayOfMonth=None,dayBasis='calendar_days',shortMonth=None,adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1')
    def book(frame,index,daily):
        floating=dict(settlement=dict(mode='curve_target',index=None,projectionPolicy=None),curve=index['curve'],index=index['index'],currency=index['currencyCode'],deliveryFrom='2021-01-01',deliveryTo='2021-02-01',paymentSchedule=payment,resetFrequency='daily' if daily else 'monthly',averagingMethod='arithmetic' if daily else 'none',spread='0')
        body={**context(frame),**trade_booking(), 'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],
            'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity'],'quantity':'10','fixedCoefficient':'-1','discounting':False,
            'fixed':dict(price='3',currency=index['currencyCode'],deliveryFrom='2021-01-01',deliveryTo='2021-02-01',paymentSchedule=payment),'floating':floating}
        return client.call('swaps/book',body)
    first=book(later,secondary,True)
    second=book(earlier,primary,False)
    specification=next(q['specification'] for q in primary['quantityChoices'] if q['specification']['code']==primary['quantityCode'])
    fixed_periods=[];floating_periods=[]
    for start,end,amount,price in (('2021-01-01','2021-02-01','10','3'),('2021-02-01','2021-03-01','20','4')):
        fixed_periods.append(dict(amount=amount,floating=None,fixed=dict(price=price,currency=primary['currencyCode'],deliveryFrom=start,deliveryTo=end,paymentSchedule=payment)))
        floating_periods.append(dict(amount=amount,fixed=None,floating=dict(settlement=dict(mode='curve_target',index=None,projectionPolicy=None),
            curve=primary['curve'],index=primary['index'],currency=primary['currencyCode'],deliveryFrom=start,deliveryTo=end,
            paymentSchedule=payment,resetFrequency='monthly',averagingMethod='none',spread='0')))
    def schedule_side(periods):
        return dict(basis='period_total',quantity=specification,amount=None,allocation=None,
                    priceApplication='period_average_times_quantity_v1',power=None,periods=periods)
    whole=client.call('swaps/book',{**context(later),**trade_booking(),'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],
        'definitionRevisionId':primary['definitionRevisionId'],'commodity':primary['commodity'],'quantity':None,'fixedCoefficient':None,
        'discounting':False,'fixed':None,'floating':None,'swapType':'scheduled_financial_swap','firstCoefficient':'-1',
        'scheduled':dict(implementation='whole_term_scheduled_financial_swap_v1',first=schedule_side(fixed_periods),second=schedule_side(floating_periods))})
    side=query(f"SELECT count(DISTINCT fixed_side_id) FROM trading.swap_instruments WHERE instrument_id IN('{first['instrumentId']}','{second['instrumentId']}');")
    assert side=='1','The identical fixed Side must remain shared across independent floating markets'
    editor=client.call('swaps/editor',dict(context=context(earlier),tradeId=second['tradeId'],revision=None))
    assert editor['valuation']['presentValue'] is not None,'The earlier effective trade must read its shared complete retained graph'
    print('PASS retained schedule temporal reuse: a later-first shared Side reads under the earlier independent market boundary',flush=True)
    return [dict(instrumentId=first['instrumentId'],asOf=later['at'],fixedUnitPv='3'),
            dict(instrumentId=second['instrumentId'],asOf=earlier['at'],fixedUnitPv='3'),
            dict(instrumentId=whole['instrumentId'],asOf=later['at'],minimumPayments=2)],before_programs
