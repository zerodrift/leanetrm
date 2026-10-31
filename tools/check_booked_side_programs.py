"""Executable schedules must be booked, sealed and read as immutable content."""

import copy
import json
import subprocess
from pathlib import Path


def runtime_query(query,manifest,statement,profile='trader',prefix='',commit=True):
    token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    return query(f"BEGIN;{prefix}SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');{statement}"+
                 ('COMMIT;' if commit else 'ROLLBACK;'))


def capture_context(query,manifest,day):
    return runtime_query(query,manifest,f"SELECT security.capture_date_context('{manifest['catalogId']}','{day}',NULL);")


def append_program(catalog, request, schedule, content):
    encoded=content.encode('utf-8') if isinstance(content,str) else content
    return f"SELECT security.seal_side_schedule_program('{catalog}','{request}','{schedule}',decode('{encoded.hex()}','hex'));"


def literal_program(header, rows, roots, grids=()):
    """Independent seven-instruction quantity × price × discount fixture."""
    operands=({}, {}, {'quantity':[0],'price':[1]}, {}, {'amount':[2],'discount':[3]},
              {'amount':[2],'discounted':[4]}, {'input':[5]})
    dependents=([2], [2], [4,5], [4], [5], [6], [])
    normalized=[]
    for row in rows:
        output=dict(row,grid=row.get('grid'))
        output['valueAt']=output['valueAt'].replace('Z','+00:00')
        normalized.append(output)
    geometry=[dict(grid,origin=grid['origin'].replace('Z','+00:00')) for grid in grids]
    return dict(version='booked_side_program_v1',scheduleManifest=header['manifest'],family=header['family'],
        economicIdentity=header['economicIdentity'],pricingBindingIdentity=header['pricingBindingIdentity'],
        instructions=[dict(output=row,inputs=operands[n],bindings=[],dependents=dependents[n],
                           consumers=[5] if n==3 else []) for n,row in enumerate(normalized)],
        grids=geometry,roots=[{key:root[key] for key in ('name','row','measure')} for root in roots],ports=[])


def check_literal_program(runtime, query, catalog, request, schedule, program, reject):
    def append(document):return append_program(catalog,request,schedule,json.dumps(document,separators=(',',':')))
    original=json.dumps(program,separators=(',',':'))
    mutations=[]
    for change in ('price','operand','dependent','consumer','missing','unknown','root','version'):
        proposed=copy.deepcopy(program)
        if change=='price':proposed['instructions'][1]['output']['parameters']['numerator']='4'
        elif change=='operand':proposed['instructions'][2]['inputs']['quantity']=[1]
        elif change=='dependent':proposed['instructions'][2]['dependents']=[4]
        elif change=='consumer':proposed['instructions'][3]['consumers']=[]
        elif change=='missing':del proposed['instructions'][0]['output']['grid']
        elif change=='unknown':proposed['instructions'][0]['output']['invented']=None
        elif change=='root':proposed['roots']=list(reversed(proposed['roots']))
        elif change=='version':proposed['version']='unsupported_program'
        mutations.append((append(proposed),'side_schedule_unsupported' if change=='version' else 'side_schedule_program'))
    for content in (original.replace('"ordinal":0','"ordinal":0.0',1),
                    original.replace('{','{"version":"booked_side_program_v1",',1),
                    original.replace('{','{"ver\\u0073ion":"booked_side_program_v1",',1),
                    original.replace('"ordinal":0','"ordinal":0e0',1)):
        mutations.append((append_program(catalog,request,schedule,content),'side_schedule_program'))
    mutations.append((append_program(catalog,request,schedule,b'\xff'),'side_schedule_program'))
    for statement,code in mutations:runtime(reject(statement,code))
    assert query(f"SELECT count(*) FROM trading.side_schedule_programs WHERE schedule_id='{schedule}';")=='0', \
        'Rejected executable content must leave no staged program'
    runtime(append(program));runtime(append(program))
    assert query(f"SELECT count(*) FROM trading.side_schedule_programs WHERE schedule_id='{schedule}';")=='1', \
        'Identical executable retries retain exactly one artifact'
    changed=copy.deepcopy(program);changed['instructions'][1]['output']['parameters']['numerator']='4'
    runtime(reject(append(changed),'side_schedule_program_conflict'))
    assert query(f"SELECT encode(content,'hex') FROM trading.side_schedule_programs WHERE schedule_id='{schedule}';")==original.encode().hex(), \
        'Stored bytes must remain exactly the admitted program'
    print('PASS booked programs: changed prices, operands, reverse links, consumers, schema and raw token ambiguity refuse; exact retries retain bytes',flush=True)


def install_stage_prerequisite(runtime, query, catalog, request, schedule):
    """Existing topology tests require an artifact before testing other seals.

    Their assertions concern ports/geometry, not program compilation. Program
    correspondence is tested independently above with literal instructions.
    """
    content=query(f"SELECT trading.side_program_document('{catalog}','{schedule}')::text;")
    runtime(append_program(catalog,request,schedule,content))


def run_direct_read_checks(query,manifest,root,dotnet,environment,instruments,required_families):
    """Only published bytes may be consumed; disable every normalized-content RPC."""
    from uuid import UUID
    catalog=str(UUID(manifest['catalogId']))
    items=[]
    for entry in instruments:
        identifier=str(UUID(entry['instrumentId']));at=entry['asOf']
        selected=capture_context(query,manifest,at[:10])
        known=query(f"SELECT to_jsonb(known_at)#>>'{{}}' FROM market_data.temporal_contexts WHERE(catalog_id,id)=('{catalog}','{selected}');")
        sides=json.loads(query(f"""SELECT jsonb_agg(jsonb_build_object('sideId',m.side_id,'scheduleId',m.schedule_id,
             'digest',encode(sha256(p.content),'hex')) ORDER BY m.slot)
            FROM trading.instrument_side_schedules m JOIN trading.side_schedule_programs p
             ON(p.catalog_id,p.schedule_id)=(m.catalog_id,m.schedule_id)
            WHERE(m.catalog_id,m.instrument_id)=('{catalog}','{identifier}');"""))
        native=query(f"SELECT EXISTS(SELECT 1 FROM trading.ppa_instruments WHERE(catalog_id,instrument_id)=('{catalog}','{identifier}')); ")=='t'
        if not native:
            operation=query(f"SELECT operation_id FROM trading.swap_trade_revisions WHERE(catalog_id,instrument_id)=('{catalog}','{identifier}') ORDER BY revision LIMIT 1;")
            wrong_command=f"ALTER TABLE trading.side_schedule_requests DISABLE TRIGGER immutable_record;UPDATE trading.side_schedule_requests SET command_hash=sha256(convert_to('synthetic different swap booking','UTF8')) WHERE(catalog_id,request_id)=('{catalog}','{operation}');"
            refusal=f"DO $$BEGIN PERFORM security.publish_side_schedules('{catalog}','{operation}','{identifier}');RAISE EXCEPTION 'Expected swap command binding refusal';EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'side_schedule_command' THEN RAISE;END IF;END$$;"
            runtime_query(query,manifest,refusal,prefix=wrong_command,commit=False)
            read=f"PERFORM security.read_swap_publication('{catalog}','{operation}','{identifier}')"
            def denied_swap(statement,code='42501',message=None):
                exact='' if message is None else f"IF SQLERRM<>'{message}' THEN RAISE;END IF;"
                return f"DO $$BEGIN {statement};RAISE EXCEPTION 'Expected swap publication refusal';EXCEPTION WHEN SQLSTATE '{code}' THEN {exact} END$$;"
            runtime_query(query,manifest,denied_swap(read),profile='viewer')
            for permission in ('ReadTrades','BookTrade'):
                runtime_query(query,manifest,denied_swap(read),prefix=f"DELETE FROM security.role_permissions WHERE permission='{permission}';",commit=False)
            runtime_query(query,manifest,denied_swap(read),prefix=f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{manifest['referenceCatalogId']}';",commit=False)
            runtime_query(query,manifest,denied_swap(read,'P0001','side_schedule_command'),prefix=wrong_command,commit=False)
            runtime_query(query,manifest,denied_swap(read.replace(identifier,'00000000-0000-0000-0000-000000000001'),'P0001','side_schedule_command'))
            runtime_query(query,manifest,f"SELECT security.use_market_context('{catalog}','{selected}');"+denied_swap(read))
            query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION openetrm_risk_worker;{denied_swap(read)}ROLLBACK;")

        if native:
            statement=f"SELECT security.read_ppa_program_metadata('{catalog}','{identifier}','{selected}','{at}')::text;"
            metadata=json.loads(runtime_query(query,manifest,statement))
            assert metadata['implementation']=='ppa_instrument_v2'
            operation=query(f"SELECT operation_id FROM trading.ppa_trade_revisions r JOIN trading.ppa_revision_bindings b ON(b.catalog_id,b.trade_id,b.revision)=(r.catalog_id,r.trade_id,r.revision) WHERE(b.catalog_id,b.instrument_id)=('{catalog}','{identifier}') ORDER BY r.revision LIMIT 1;")
            wrong_command=f"ALTER TABLE trading.side_schedule_requests DISABLE TRIGGER immutable_record;UPDATE trading.side_schedule_requests SET command_hash=sha256(convert_to('synthetic different booking','UTF8')) WHERE(catalog_id,request_id)=('{catalog}','{operation}');"
            refusal=f"DO $$BEGIN PERFORM security.publish_side_schedules('{catalog}','{operation}','{identifier}');RAISE EXCEPTION 'Expected command binding refusal';EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'side_schedule_command' THEN RAISE;END IF;END$$;"
            runtime_query(query,manifest,refusal,prefix=wrong_command,commit=False)
            identities=f"SELECT * FROM security.read_ppa_publication_identities('{catalog}','{operation}','{identifier}');"
            assert runtime_query(query,manifest,identities), 'Booking authority reads complete sealed member identities'
            def denied_identity(statement,code='42501',message=None):
                exact='' if message is None else f"IF SQLERRM<>'{message}' THEN RAISE;END IF;"
                return f"DO $$BEGIN {statement};RAISE EXCEPTION 'Expected publication identity refusal';EXCEPTION WHEN SQLSTATE '{code}' THEN {exact} END$$;"
            read_identity=f"PERFORM security.read_ppa_publication_identities('{catalog}','{operation}','{identifier}')"
            runtime_query(query,manifest,denied_identity(read_identity),profile='viewer')
            for permission in ('ReadTrades','BookTrade'):
                runtime_query(query,manifest,denied_identity(read_identity),prefix=f"DELETE FROM security.role_permissions WHERE permission='{permission}';",commit=False)
            wrong_actor=f"ALTER TABLE trading.side_schedule_requests DISABLE TRIGGER immutable_record;UPDATE trading.side_schedule_requests r SET actor_id=(SELECT id FROM security.principals WHERE id<>r.actor_id ORDER BY id LIMIT 1) WHERE(catalog_id,request_id)=('{catalog}','{operation}');"
            runtime_query(query,manifest,denied_identity(read_identity),prefix=wrong_actor,commit=False)
            runtime_query(query,manifest,f"SELECT security.use_market_context('{catalog}','{selected}');"+denied_identity(read_identity))
            withdrawal=f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{manifest['referenceCatalogId']}';"
            runtime_query(query,manifest,denied_identity(read_identity),prefix=withdrawal,commit=False)
            runtime_query(query,manifest,denied_identity(read_identity,'P0001','side_schedule_command'),prefix=wrong_command,commit=False)
            wrong_instrument=read_identity.replace(identifier,'00000000-0000-0000-0000-000000000001')
            runtime_query(query,manifest,denied_identity(wrong_instrument,'P0001','side_schedule_command'))
            query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION openetrm_risk_worker;{denied_identity(read_identity)}ROLLBACK;")
            assert query("SELECT has_function_privilege('openetrm_runtime','security.publish_admitted_side_schedules(uuid,uuid,uuid)','EXECUTE');")=='f'

            if 'expectedMetadata' in entry:
                wanted=entry['expectedMetadata']
                assert metadata['currency']==wanted['currency'] and metadata['unitQuantity']==wanted['unitQuantity']
                assert metadata['discounting']==wanted['discounting']
                assert metadata['clock']==wanted['delivery']['clock'] and metadata['intervalMinutes']==wanted['delivery']['intervalMinutes']
                assert metadata['deliveryFrom']==wanted['delivery']['from'] and metadata['deliveryTo']==wanted['delivery']['to']
                assert [{key:m[key] for key in ('role','ordinal','coefficient')} for m in metadata['members']]==wanted['members']
            assert [m['sideId'] for m in metadata['members']]==[m['sideId'] for m in sides]
            assert all(m['coefficient'] in (-1,1) for m in metadata['members'])
            assert all(m['normalizationPolicy'] in ('contract_unit_v1','sum_capacity_in_force_v1') for m in metadata['members'])
            if any(m['assets'] for m in metadata['members']):
                corruption=f"ALTER TABLE trading.ppa_instrument_assets DISABLE TRIGGER immutable_record;DELETE FROM trading.ppa_instrument_assets WHERE(catalog_id,instrument_id)=('{catalog}','{identifier}');"
                refusal=f"DO $$BEGIN PERFORM security.read_ppa_program_metadata('{catalog}','{identifier}','{selected}','{at}');RAISE EXCEPTION 'Expected normalization mismatch';EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'ppa_normalization' THEN RAISE;END IF;END$$;"
                runtime_query(query,manifest,refusal,prefix=corruption,commit=False)
        items.append({**entry,'contextId':selected,'knownAt':known,'sides':sides,'native':native})
    fixture=root/'booked-programs.json'
    fixture.write_text(json.dumps(dict(instruments=items,requiredFamilies=list(required_families))))
    signatures=['read_ppa_terms(uuid,uuid)','read_side_schedule_header(uuid,uuid,uuid,uuid,uuid,timestamp with time zone)',
                *[f'read_side_schedule_{part}(uuid,uuid,uuid,uuid,uuid,timestamp with time zone,integer,integer)'
                  for part in ('grids','rows','edges','bindings','roots','ports')]]
    functions=','.join('security.'+signature for signature in signatures)
    query('REVOKE EXECUTE ON FUNCTION '+functions+' FROM openetrm_runtime;')
    try:
        subprocess.run([str(dotnet),'run','--project','tests/OpenEtrm.Api.Checks','--','--booked-programs',str(fixture)],
                       cwd=Path(__file__).resolve().parents[1],env=environment,check=True,timeout=120)
    finally:
        query('GRANT EXECUTE ON FUNCTION '+functions+' TO openetrm_runtime;')
    return items


def book_native_programs(url,manifest,query):
    from copy import deepcopy
    from uuid import uuid4
    from benchmark_ppa_http import terms,midnight,instant
    from ppa_demo_terms import profile_terms
    from preview_client import PreviewClient
    from preview_fixture import trade_booking
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'trader'})
    frame=next(f for f in client.call('workspace')['frames'] if f['at'].startswith('2020-12-18T16:00:00'))
    certificate=terms(manifest,minutes=15)
    capacity=terms(manifest,five=False)
    capacity['memberships'][0].update(role='capacity_delivery')
    capacity['memberships'][0]['side'].update(kind='capacity',location=None,
        quantity=dict(kind='accredited',sources=[],stage=None,capacity=None,energy=None,hours=None,slots=[],assets=[manifest['ppaBookings']['workloads']['60']['asset']]),
        attribute=dict(quantity='test_ppa_capacity_days_quantity',conversion=None,eligibility=None,holding=None,disposition=None,rule='mw_day_per_operating_day_v1'))
    capacity['memberships'][1].update(role='capacity_payment')
    capacity['memberships'][1]['side']['quantity']['slots']=[dict(role='capacity_delivery',ordinal=0)]
    capacity['memberships'][1]['side']['fixedPrice'].update(quantity='test_ppa_capacity_days_quantity',price='4')
    virtual=profile_terms(manifest)
    virtual['memberships'][1]['side']['quantity']=deepcopy(virtual['memberships'][0]['side']['quantity'])
    virtual['memberships'][0].update(role='indexed_payment')
    virtual['memberships'][0]['side'].update(kind='indexed',location=None,
        indexPrice=dict(curve=manifest['intervalCurves']['shaped'],quantity='test_power_quantity',multiplier='1',indexMultiplier='1',settlement=None,adder='0'))
    # These are booking/storage checks. No market samples are needed to publish
    # a complete schedule with explicit dynamic source references.
    items=[]
    assert virtual['delivery']['clock']=='synthetic_eastern'
    for selected,zone in ((certificate,manifest['ppaBookings']['workloads']['15']['zone']),
                          (capacity,manifest['ppaBookings']['workloads']['60']['zone']),
                          (virtual,'America/New_York')):
        body=dict(operationId=str(uuid4()),snapshotId=frame['id'],tradeId=None,expectedRevision=None,
            bookId='d1000000-0000-0000-0000-000000000001',boundaryAt=None,
            coverageFrom=instant(midnight(selected['delivery']['from'],zone)),
            coverageTo=instant(midnight(selected['delivery']['to'],zone)),quantity='80',reason='Synthetic executable schedule qualification',
            memo='',booking=trade_booking(),terms=deepcopy(selected))
        if not items:
            signature='security.read_ppa_publication_identities(uuid,uuid,uuid)'
            original=query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
            fragment='RETURN QUERY SELECT item.slot,item.side_id,item.economic_identity,item.pricing_binding_identity;'
            assert fragment in original
            corrupted=original.replace(fragment,"RETURN QUERY SELECT item.slot,item.side_id,item.economic_identity||'synthetic mismatch',item.pricing_binding_identity;")
            before=query("SELECT count(*) FROM trading.ppa_trade_revisions;")
            try:
                query(corrupted)
                refusal=client.call('ppa/book',body,expected=422)
                assert refusal['code']=='side_schedule_identity'
                assert query("SELECT count(*) FROM trading.ppa_trade_revisions;")==before, 'Identity refusal must roll back the new trade revision'
                assert query(f"SELECT count(*) FROM trading.ppa_trade_revisions WHERE operation_id='{body['operationId']}';")=='0'
            finally:
                query(original)
        result=client.call('ppa/book',body)
        assert client.call('ppa/book',body)==result, 'Native schedule booking must return its first exact retry outcome'
        assert len(result['bindings'])==1, 'Each storage fixture books one explicit instrument binding'
        instrument=result['bindings'][0]['instrumentId']
        items.append(dict(instrumentId=instrument,asOf=frame['at'],expectedMetadata=dict(
            delivery=selected['delivery'],currency=selected['currency'],unitQuantity=selected['unitQuantity'],
            discounting=selected['discounting'],members=[dict(role=m['role'],ordinal=m['ordinal'],coefficient=int(m['coefficient'])) for m in selected['memberships']])))
    return items


def run_authority_checks(query,manifest,entry,before_context):
    from uuid import uuid4
    catalog=manifest['catalogId'];instrument=entry['instrumentId'];selected=entry['contextId'];at=entry['asOf']
    def read(identifier=instrument,context=selected):
        return f"PERFORM security.read_instrument_side_programs('{catalog}','{identifier}','{context}','{at}')"
    def refused(statement,code='42501',message=None):
        exact='' if message is None else f"IF SQLERRM<>'{message}' THEN RAISE;END IF;"
        return f"DO $$BEGIN {statement};RAISE EXCEPTION 'Expected executable read refusal';EXCEPTION WHEN SQLSTATE '{code}' THEN {exact} END$$;"
    for identifier in (instrument,str(uuid4())):
        runtime_query(query,manifest,refused(read(identifier)),profile='viewer')
    runtime_query(query,manifest,refused(f"PERFORM security.read_instrument_side_programs('{catalog}','{instrument}',NULL,NULL)"))
    runtime_query(query,manifest,refused(read(context=before_context)))
    unpublished=query(f"""SELECT p.schedule_id FROM trading.side_schedule_programs p WHERE p.catalog_id='{catalog}'
        AND NOT EXISTS(SELECT 1 FROM trading.side_schedule_publications v WHERE(v.catalog_id,v.schedule_id)=(p.catalog_id,p.schedule_id))
        ORDER BY p.schedule_id LIMIT 1;""")
    assert unpublished, 'Visibility qualification requires a complete sealed unpublished program'
    # Normal history excludes a revision without its committed knowledge receipt;
    # booking-time publication reads are independently exercised before commit by HTTP.
    normal=f"SELECT count(*) FROM trading.read_swap_revisions('{catalog}',NULL,false,ARRAY[]::uuid[]) r WHERE r->>'instrument_id'='{instrument}';"
    history_bind=f"SELECT security.use_market_context('{catalog}','{selected}');SELECT security.use_trading_context('{catalog}','{at}');"
    assert runtime_query(query,manifest,history_bind+normal)!='0'
    assert runtime_query(query,manifest,normal,profile='viewer')=='0'
    missing_receipt=f"ALTER TABLE trading.knowledge_receipts DISABLE TRIGGER ALL;DELETE FROM trading.knowledge_receipts WHERE(catalog_id,publication_order) IN(SELECT catalog_id,publication_order FROM trading.swap_trade_revisions WHERE(catalog_id,instrument_id)=('{catalog}','{instrument}'));"
    assert runtime_query(query,manifest,history_bind+normal,prefix=missing_receipt,commit=False)=='0'
    assert runtime_query(query,manifest,f"SELECT security.use_market_context('{catalog}','{before_context}');SELECT security.use_trading_context('{catalog}','{at}');"+normal)=='0'
    for function in ('trading.read_swap_revisions(uuid,uuid,boolean,uuid[])','trading.swap_revision_terms(uuid,uuid,integer)','trading.swap_revision_rows(uuid,uuid,integer)','trading.swap_observation_terms(uuid,uuid[])'):
        assert query(f"SELECT prosecdef FROM pg_proc WHERE oid='{function}'::regprocedure;")=='f'
    assert query("SELECT 'jit=off'=ANY(proconfig) FROM pg_proc WHERE oid='trading.read_swap_revisions(uuid,uuid,boolean,uuid[])'::regprocedure;")=='t'
    header_plan=query(f"EXPLAIN(FORMAT JSON) SELECT (h.value||m.value)::text FROM security.read_instrument_side_schedule_headers('{catalog}','{instrument}',NULL,NULL) m(value) CROSS JOIN LATERAL security.read_side_schedule_header('{catalog}',(m.value->>'schedule_id')::uuid,NULL,'{instrument}',NULL,NULL) h(value) ORDER BY (m.value->>'slot')::integer;")
    assert json.loads(header_plan)[0]['Plan']['Plan Rows']<=8, 'Header rechecks must retain the schema-bounded planner cardinality'
    restored=runtime_query(query,manifest,history_bind+"SET LOCAL jit=on;"+normal+"SELECT current_setting('jit');")
    assert restored.splitlines()[-1]=='on', 'The bounded read must restore its caller JIT setting'
    assert query("SELECT has_function_privilege('openetrm_runtime','security.swap_publication_authority(uuid,uuid,uuid)','EXECUTE');")=='f'
    published=entry['sides'][0]['scheduleId']
    bind=f"SELECT security.use_market_context('{catalog}','{selected}');SELECT security.use_trading_context('{catalog}','{at}');"
    visibility=f"SELECT jsonb_build_array((SELECT count(*) FROM trading.side_schedule_programs WHERE schedule_id='{published}'),(SELECT count(*) FROM trading.side_schedule_programs WHERE schedule_id='{unpublished}'));"
    permissive='CREATE POLICY synthetic_program_allow ON trading.side_schedule_programs FOR SELECT TO openetrm_runtime USING(true);'
    assert json.loads(runtime_query(query,manifest,bind+visibility,prefix=permissive,commit=False))==[1,0], \
        'The same bound context must see a published program and hide sealed staging, even with an extra permissive policy'
    withdrawal=f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{manifest['referenceCatalogId']}';"
    runtime_query(query,manifest,refused(read()),prefix=withdrawal,commit=False)
    floor=f"SELECT trading.record_history_boundary('{catalog}','{uuid4()}','{selected}');"
    runtime_query(query,manifest,refused(read(context=before_context),'P0001','trading_history_archived'),prefix=floor,commit=False)
    query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION openetrm_risk_worker;{refused(read())}ROLLBACK;")
    def cells(identifier=instrument,context=selected):
        return f"PERFORM security.read_instrument_schedule_cells('{catalog}','{identifier}','{context}','{at}')"
    for identifier in (instrument,str(uuid4())):
        runtime_query(query,manifest,refused(cells(identifier)),profile='viewer')
    runtime_query(query,manifest,refused(f"PERFORM security.read_instrument_schedule_cells('{catalog}','{instrument}',NULL,NULL)"))
    runtime_query(query,manifest,refused(cells(context=before_context)))
    runtime_query(query,manifest,refused(cells()),prefix=withdrawal,commit=False)
    runtime_query(query,manifest,refused(cells(context=before_context),'P0001','trading_history_archived'),prefix=floor,commit=False)
    query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION openetrm_risk_worker;{refused(cells())}ROLLBACK;")
    def metadata(identifier=instrument,context=selected):
        return f"PERFORM security.read_ppa_program_metadata('{catalog}','{identifier}','{context}','{at}')"
    for identifier in (instrument,str(uuid4())):
        runtime_query(query,manifest,refused(metadata(identifier)),profile='viewer')
    runtime_query(query,manifest,refused(f"PERFORM security.read_ppa_program_metadata('{catalog}','{instrument}',NULL,NULL)"))
    runtime_query(query,manifest,refused(metadata(context=before_context)))
    runtime_query(query,manifest,refused(metadata()),prefix=withdrawal,commit=False)
    runtime_query(query,manifest,refused(metadata(context=before_context),'P0001','trading_history_archived'),prefix=floor,commit=False)
    query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION openetrm_risk_worker;{refused(metadata())}ROLLBACK;")
    def swap_metadata(identifier=instrument,context=selected):
        return f"PERFORM security.read_swap_program_metadata('{catalog}','{identifier}','{context}','{at}')"
    for identifier in (instrument,str(uuid4())):
        runtime_query(query,manifest,refused(swap_metadata(identifier)),profile='viewer')
    runtime_query(query,manifest,refused(f"PERFORM security.read_swap_program_metadata('{catalog}','{instrument}',NULL,NULL)"))
    runtime_query(query,manifest,refused(swap_metadata(context=before_context)))
    runtime_query(query,manifest,refused(swap_metadata()),prefix=withdrawal,commit=False)
    runtime_query(query,manifest,refused(swap_metadata(context=before_context),'P0001','trading_history_archived'),prefix=floor,commit=False)
    query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION openetrm_risk_worker;{refused(swap_metadata())}ROLLBACK;")
    assert query("SELECT has_function_privilege('openetrm_runtime','security.seal_side_schedule_ports(uuid,uuid,jsonb)','EXECUTE');")=='f', 'The pre-mapping seal cannot bypass executable connection admission'
    assert query("SELECT count(*) FROM trading.instrument_schedule_cell_seals WHERE mapping_count=0;")!='0', 'Independent sides still require an explicit empty composition marker'
    assert query("SELECT count(*) FROM trading.side_schedule_request_seals r LEFT JOIN trading.instrument_schedule_cell_seals c USING(catalog_id,request_id) WHERE c.request_id IS NULL;")=='0', 'Every sealed request must retain executable connection admission'
    mapping_count=query('SELECT count(*) FROM trading.instrument_schedule_cells;')
    query("DO $$BEGIN BEGIN UPDATE trading.instrument_schedule_cells SET input_cells=input_cells; RAISE EXCEPTION 'Accepted immutable mapping update'; EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'instrument_schedule_immutable' THEN RAISE;END IF;END;END$$;")
    for mutation in ('DELETE FROM trading.instrument_schedule_cells',
                     'INSERT INTO trading.instrument_schedule_cells SELECT * FROM trading.instrument_schedule_cells LIMIT 1'):
        query("DO $$BEGIN BEGIN "+mutation+"; RAISE EXCEPTION 'Accepted immutable mapping mutation'; EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'instrument_schedule_immutable' THEN RAISE;END IF;END;END$$;")
    composition_request=query(f"SELECT request_id FROM trading.instrument_schedule_compositions WHERE(catalog_id,instrument_id)=('{catalog}','{instrument}');")
    # Simulate an internal publisher omitting the required marker. This entire
    # corruption probe is rolled back in the owned synthetic database.
    query(f"""BEGIN;
        ALTER TABLE trading.instrument_schedule_cell_seals DISABLE TRIGGER immutable_record;
        ALTER TABLE trading.instrument_schedule_compositions DISABLE TRIGGER immutable_record;
        DELETE FROM trading.instrument_schedule_cell_seals WHERE(catalog_id,request_id)=('{catalog}','{composition_request}');
        DELETE FROM trading.instrument_schedule_compositions WHERE(catalog_id,instrument_id)=('{catalog}','{instrument}');
        DO $$BEGIN BEGIN
          INSERT INTO trading.instrument_schedule_compositions VALUES('{catalog}','{instrument}','{composition_request}');
          SET CONSTRAINTS ALL IMMEDIATE;
          RAISE EXCEPTION 'Published without executable connection marker';
        EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'instrument_schedule_missing' THEN RAISE;END IF;END;END$$;
        ROLLBACK;""")
    for name,code in (('0147_instrument_schedule_cells.sql','instrument_schedule_rebooking_required'),
                      ('0148_swap_program_admission.sql','swap_program_rebooking_required')):
        migration=Path(__file__).resolve().parents[1]/'db/migrations'/name
        try:
            query(migration.read_text())
        except RuntimeError as error:
            if code not in str(error):raise
        else:raise AssertionError('An existing sealed request must require explicit rebooking at the program cutover')
    assert query('SELECT count(*) FROM trading.instrument_schedule_cells;')==mapping_count, 'Refused installation must preserve existing connections'
    before=query('SELECT count(*) FROM trading.side_schedule_programs;')
    migration=Path(__file__).resolve().parents[1]/'db/migrations/0129_booked_side_programs.sql'
    try:
        query(migration.read_text())
    except RuntimeError as error:
        if 'side_schedule_program_rebooking_required' not in str(error):raise
    else:
        raise AssertionError('Installing the executable format over retained schedules must require explicit rebooking')
    assert query('SELECT count(*) FROM trading.side_schedule_programs;')==before, 'A refused installation must retain every existing program'
    print('PASS booked programs and instrument mappings: scoped reads, history, revoked source, worker claims, immutable retries and mandatory publication markers',flush=True)


def run_schema_checks(query):
    assert query("SELECT to_regclass('trading.side_schedule_programs') IS NOT NULL;") == 't', \
        'Booking must persist the complete executable Side program'
    assert query("""
        SELECT count(*) FROM pg_constraint
        WHERE conrelid='trading.side_schedule_programs'::regclass AND contype='f'
          AND confrelid='trading.side_schedules'::regclass;
    """) == '1', 'Executable content must belong to its exact retained schedule'
    assert query("""
        SELECT relrowsecurity AND relforcerowsecurity
        FROM pg_class WHERE oid='trading.side_schedule_programs'::regclass;
    """) == 't', 'Executable schedule content must have forced row authorization'
    assert query("""
        SELECT count(*) FROM pg_policy
        WHERE polrelid='trading.side_schedule_programs'::regclass
          AND polname='temporal_read' AND NOT polpermissive;
    """) == '1', 'Executable reads must inherit publication visibility'
    assert query("""
        SELECT count(*) FROM information_schema.role_table_grants
        WHERE grantee IN('openetrm_runtime','openetrm_risk_worker') AND table_schema='trading'
          AND table_name='side_schedule_programs' AND privilege_type IN('INSERT','UPDATE','DELETE');
    """) == '0', 'Runtime clients cannot bypass executable publication validation'
    assert query("""
        SELECT count(*) FROM pg_trigger WHERE tgrelid='trading.side_schedule_seals'::regclass
          AND tgname='require_executable_program' AND tgenabled='O';
    """) == '1', 'A retained schedule cannot seal without its validated executable program'
    escaped_empty_uuid=r'{"id":"\u00300000000-0000-0000-0000-000000000000"}'
    assert query(f"SELECT NOT trading.side_program_integer_tokens('{escaped_empty_uuid}'::json);")=='t', \
        'Decoded empty UUIDs must refuse even when their JSON string uses escapes'
    print('PASS booked programs: exact schedule ownership, forced temporal reads and mandatory seal admission', flush=True)
