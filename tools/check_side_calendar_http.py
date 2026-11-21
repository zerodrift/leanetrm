"""Synthetic calendar authoring through managed preview and native retention."""
from copy import deepcopy
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import trade_booking


from synthetic_side_calendars import side_calendar_policy


def run_checks(url, query, manifest):
    client=PreviewClient(url);client.bootstrap();client.call('profile', {'profile':'trader'})
    frame=next(f for f in client.call('workspace')['frames'] if f['at'].startswith('2020-12-18'))
    context=dict(snapshotId=frame['id'], convention='independent_side_financial_swap_v2', knowledgeMode='current')
    catalog=client.call('swaps/catalog', context)
    index=next(i for i in catalog['indices'] if i['curve']=='daily_gas')
    reference=dict(revision=dict(catalogId=manifest['referenceCatalogId'], revisionId=manifest['referenceRevisionId']), code='test_utc_every_day')
    payment=dict(frequency='monthly', rule='days_after_period_end', days=18, dayOfMonth=None,
                 dayBasis='calendar_days', shortMonth=None, adjustment='unadjusted', calendar=None, implementation='monthly_payment_date_rules_v1')
    body={**context, 'operationId':str(uuid4()), **trade_booking(), 'bookId':catalog['books'][0]['id'],
          'commodity':index['commodity'], 'definitionRevisionId':index['definitionRevisionId'],
          'quantity':'100', 'fixedCoefficient':'-1', 'discounting':False,
          'fixed':dict(price='3', currency=index['currencyCode'], deliveryFrom='2021-01-01', deliveryTo='2021-02-01', paymentSchedule=payment, calendarPolicy=side_calendar_policy(reference, delivery=True, observation=False, payment=True)),
          'floating':dict(spread='0', settlement=dict(mode='curve_target', index=None, projectionPolicy=None), curve=index['curve'], index=index['index'],
                          currency=index['currencyCode'], deliveryFrom='2021-01-01', deliveryTo='2021-02-01', paymentSchedule=payment,
                          resetFrequency='daily', averagingMethod='arithmetic', calendarPolicy=side_calendar_policy(reference, delivery=True, observation=True, payment=True))}
    omitted=deepcopy(body);del omitted['fixed']['calendarPolicy']
    assert client.call('swaps/preview', omitted, expected=422)['code']=='side_calendar_policy'
    preview=client.call('swaps/preview', body)['valuation']
    assert preview['presentValue']=='16'
    assert all(s['payments'][0]['paymentDate']=='2021-02-18' for s in preview['sides'])
    booked=client.call('swaps/book', body)
    assert client.call('swaps/book', body)==booked
    retained=client.call('swaps/editor', dict(context=context, tradeId=booked['tradeId'], revision=None))
    assert retained['valuation']['presentValue']=='16'
    assert retained['state']['terms']['fixed']['calendarPolicy']==body['fixed']['calendarPolicy']
    assert retained['state']['terms']['floating']['calendarPolicy']==body['floating']['calendarPolicy']
    assert query(f"SELECT count(*) FROM trading.side_calendar_bindings b JOIN trading.swap_instruments i ON i.catalog_id=b.catalog_id AND b.side_id IN(i.fixed_side_id,i.floating_side_id) WHERE i.instrument_id='{booked['instrumentId']}';")=='6'
    print('PASS Side calendar HTTP: explicit omissions, daily observations, monthly payment dates, canonical retention and replay', flush=True)

    independent_revision,source_revision=str(uuid4()),str(uuid4())
    query(f"""BEGIN;
      INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{manifest['referenceCatalogId']}','{independent_revision}','synthetic_independent_payment');
      INSERT INTO ref_data.clock_rules(catalog_id,revision_id,code,kind,fixed_offset_seconds) VALUES('{manifest['referenceCatalogId']}','{independent_revision}','synthetic_utc','fixed_offset',0);
      INSERT INTO ref_data.calendars VALUES('{manifest['referenceCatalogId']}','{independent_revision}','synthetic_independent_payment','synthetic_utc',96,'2020-01-01','2041-01-01');
      UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{manifest['referenceCatalogId']}','{independent_revision}');
      INSERT INTO market_data.revisions VALUES('{manifest['catalogId']}','{source_revision}','{manifest['referenceCatalogId']}','{independent_revision}','draft',0);
      INSERT INTO market_data.revision_periods VALUES('{manifest['catalogId']}','{source_revision}','2040-01-01','2041-01-01');
      UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{manifest['catalogId']}','{source_revision}');COMMIT;""")
    calendar_catalog=client.call('swaps/catalog',context)['sideCalendars']
    independent_choice=next(row for row in calendar_catalog if row['reference']['revision']['revisionId']==independent_revision)
    assert independent_choice['coverageFrom']=='2020-01-01' and independent_choice['coverageTo']=='2041-01-01'
    assert independent_choice['weekendMask']==96 and independent_choice['exceptions']==[]
    assert independent_choice['definitionDigest'] and independent_choice['publication']['eventOrder']>0
    observation_options=client.call('swaps/observation-options',dict(context=context,
        definitionRevisionId=index['definitionRevisionId'],curve=index['curve'],index=index['index'],
        settlement=body['floating']['settlement'],resetFrequency='daily',averagingMethod='observation_arithmetic',
        deliveryFrom='2021-01-01',deliveryTo='2021-02-01',observationCalendar=independent_choice['reference']))
    assert observation_options['availability'] is None,observation_options['availability']
    assert len(observation_options['observations'])==21
    assert (observation_options['observations'][0]['from'],observation_options['observations'][-1]['from'])==('2021-01-01','2021-01-29')
    assert any(row['reference']==independent_choice['reference'] for row in observation_options['observationCalendars'])
    separate=deepcopy(body);separate['operationId']=str(uuid4())
    separate['fixed']['calendarPolicy']['payment']['calendar']=dict(revision=dict(catalogId=manifest['referenceCatalogId'],revisionId=independent_revision),code='synthetic_independent_payment')
    separate['fixed']['paymentSchedule']={**payment,'dayBasis':'business_days','days':5}
    separate_preview=client.call('swaps/preview',separate)['valuation']
    assert separate_preview['sides'][0]['payments'][0]['paymentDate']=='2021-02-05'
    separate_saved=client.call('swaps/book',separate)
    separate_editor=client.call('swaps/editor',dict(context=context,tradeId=separate_saved['tradeId'],revision=None))
    assert separate_editor['valuation']['sides'][0]['payments'][0]['paymentDate']=='2021-02-05'
    assert separate_editor['state']['terms']['fixed']['calendarPolicy']==separate['fixed']['calendarPolicy']
    assert separate_saved['instrumentId']!=booked['instrumentId']
    print('PASS calendar revision HTTP: payment calendar remains independent of the pricing bundle effective interval',flush=True)

    periods=[('2021-01-01','2021-02-01','100'),('2021-02-01','2021-03-01','200')]
    def scheduled_side(kind):
        entries=[]
        for starts,ends,amount in periods:
            term=deepcopy(body[kind]);del term['calendarPolicy'];term.update(deliveryFrom=starts,deliveryTo=ends)
            entries.append(dict(amount=amount,fixed=term if kind=='fixed' else None,floating=term if kind=='floating' else None))
        return dict(basis='period_total',quantity=index['quantityChoices'][0]['specification'],amount=None,allocation=None,
                    priceApplication='period_average_times_quantity_v1',power=None,periods=entries,calendarPolicy=body[kind]['calendarPolicy'])
    scheduled={**body,'operationId':str(uuid4()),'quantity':None,'fixedCoefficient':None,'fixed':None,'floating':None,
               'swapType':'scheduled_financial_swap','firstCoefficient':'-1',
               'scheduled':dict(implementation='whole_term_scheduled_financial_swap_v1',first=scheduled_side('fixed'),second=scheduled_side('floating'))}
    scheduled_preview=client.call('swaps/preview',scheduled)['valuation']
    assert scheduled_preview['presentValue']=='107'
    scheduled_saved=client.call('swaps/book',scheduled)
    scheduled_editor=client.call('swaps/editor',dict(context=context,tradeId=scheduled_saved['tradeId'],revision=None))
    assert scheduled_editor['valuation']['presentValue']=='107'
    assert [len(p['resets']) for p in scheduled_editor['valuation']['sides'][1]['payments']]==[31,28]
    assert [p['paymentDate'] for p in scheduled_editor['valuation']['sides'][1]['payments']]==['2021-02-18','2021-03-18']
    assert scheduled_editor['state']['terms']['scheduled']['first']['calendarPolicy']==body['fixed']['calendarPolicy']
    conflicting=deepcopy(scheduled);conflicting['scheduled']['first']['periods'][0]['fixed']['calendarPolicy']=body['fixed']['calendarPolicy']
    assert client.call('swaps/preview',conflicting,expected=422)['code']=='side_calendar_conflict'
    print('PASS scheduled calendar HTTP: two monthly settlements, 59 daily observations, independent cash oracle and whole-Side ownership',flush=True)

    # A fixed Side is reusable across pricing revisions with the same reference
    # contract. Its first publication's market is not a new clock requirement.
    monthly=next(i for i in catalog['indices'] if i['curve']=='base_forward')
    reused=deepcopy(scheduled);reused.update(operationId=str(uuid4()),definitionRevisionId=monthly['definitionRevisionId'])
    for entry in reused['scheduled']['second']['periods']:
        entry['floating'].update(curve=monthly['curve'],index=monthly['index'],resetFrequency='monthly',averagingMethod='none')
    reused_saved=client.call('swaps/book',reused)
    reused_editor=client.call('swaps/editor',dict(context=context,tradeId=reused_saved['tradeId'],revision=None))
    assert reused_editor['valuation']['sides'][0]['sideId']==scheduled_editor['valuation']['sides'][0]['sideId']
    assert reused_editor['valuation']['sides'][1]['sideId']!=scheduled_editor['valuation']['sides'][1]['sideId']
    print('PASS native completion admission: one fixed Side serves independent pricing revisions without acquiring another clock',flush=True)
    from check_booked_side_programs import runtime_query
    import json
    side_body=json.dumps(reused['scheduled']['second']).replace("'","''")
    guard='security.require_scheduled_clock(uuid,uuid,integer,jsonb,jsonb,uuid)'
    runtime_query(query,manifest,f"""DO $source$ BEGIN BEGIN
      PERFORM security.require_scheduled_clock('{manifest['catalogId']}','{scheduled['operationId']}',1,
        '{side_body}'::jsonb,'{{}}'::jsonb,'{monthly['definitionRevisionId']}');
      RAISE EXCEPTION 'foreign_floating_source_accepted';
      EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'clock_reference' THEN RAISE;END IF;END;END $source$;""",
      prefix=f'GRANT EXECUTE ON FUNCTION {guard} TO openetrm_runtime;',commit=False)
    print('PASS native source admission: reused floating program cannot change its pricing revision',flush=True)

    # Exercise native save/publication with admitted programs and a newly
    # authorized request, rather than relying on the managed caller to match
    # them. The control proves the copied stage is otherwise publishable.
    from synthetic_schedule_stage import copy_stage
    surcharge=deepcopy(scheduled);surcharge['operationId']=str(uuid4())
    for entry in surcharge['scheduled']['second']['periods']:entry['floating']['spread']='1'
    surcharge_saved=client.call('swaps/book',surcharge)
    assert client.call('swaps/editor',dict(context=context,tradeId=surcharge_saved['tradeId'],revision=None))['valuation']['presentValue']=='407'
    literal=lambda value:"'"+json.dumps(value).replace("'","''")+"'::jsonb"
    # Single-period composed bookings have the same native agreement boundary.
    disagreements=[]
    client.call('profile',{'profile':'editor'})
    definition=deepcopy(next(t['definition'] for t in client.call('custom-curves')['templates'] if t['code']=='ng_average'))
    definition.update(code='synthetic_calendar_forecast',name='Synthetic calendar forecast')
    for node in definition['nodes']:
        if node['kind']=='source':node['parameters']['curve']='base_forward'
        if node['kind']=='select':node['parameters']['count']='1'
        if node['kind']=='dates':node['parameters'].update(count='1',includeContext='true')
    draft=client.call('custom-curves/save',dict(requestId=str(uuid4()),snapshotId=context['snapshotId'],definition=definition,layout={}))
    client.call('profile',{'profile':'approver'})
    published=client.call('custom-curves/publish',dict(snapshotId=context['snapshotId'],requestId=str(uuid4()),id=draft['id'],version=draft['version'],generation=draft['generation']))
    client.call('profile',{'profile':'trader'})
    choices=next(i for i in client.call('swaps/catalog',context)['indices'] if i['curve']=='base_forward')
    projection=next(p['reference'] for p in choices['customProjections'] if p['reference']['curveId']==published['id'])
    custom=deepcopy(body);custom.update(operationId=str(uuid4()),definitionRevisionId=monthly['definitionRevisionId'])
    custom['floating'].update(curve=monthly['curve'],index=monthly['index'],resetFrequency='monthly',averagingMethod='none',
        customProjection={**projection,'position':None,'selection':'containing_delivery_interval_v1'})
    custom_saved=client.call('swaps/book',custom)
    assert client.call('swaps/editor',dict(context=context,tradeId=custom_saved['tradeId'],revision=None))['valuation']['presentValue']=='10'
    for source_operation,changed in ((body['operationId'],None),(body['operationId'],'price'),(custom['operationId'],None),(custom['operationId'],'query')):
        original=json.loads(query(f"SELECT request::text FROM trading.swap_trade_revisions WHERE operation_id='{source_operation}';"))
        operation=str(uuid4());native=deepcopy(original)
        native.pop('sideFixings',None)
        if changed=='price':native['fixed']['price']=4
        payload=literal(native)
        prefix=copy_stage(manifest['catalogId'],source_operation,operation)+f"""
         INSERT INTO trading.side_schedule_request_seals SELECT catalog_id,'{operation}',slot_count,input_count
          FROM trading.side_schedule_request_seals WHERE(catalog_id,request_id)=('{manifest['catalogId']}','{source_operation}');
         ALTER TABLE trading.side_schedule_requests DISABLE TRIGGER USER;
         UPDATE trading.side_schedule_requests SET command_hash=sha256(convert_to(({payload})::text,'UTF8'))
          WHERE(catalog_id,request_id)=('{manifest['catalogId']}','{operation}');
         ALTER TABLE trading.side_schedule_requests ENABLE TRIGGER USER;
        """
        if changed=='price':
            prefix+=f"""SET LOCAL session_replication_role=replica;
             DELETE FROM trading.swap_fixed_sides WHERE(catalog_id,id)=('{manifest['catalogId']}','{retained['valuation']['sides'][0]['sideId']}');
             SET LOCAL session_replication_role=origin;"""
        if changed=='query':
            schedule=query(f"SELECT schedule_id FROM trading.side_schedule_request_items WHERE(catalog_id,request_id,slot)=('{manifest['catalogId']}','{source_operation}',1);")
            assert query(f"SELECT count(*) FROM trading.side_schedule_rows WHERE schedule_id='{schedule}' AND operation='curve_price_lookup';")=='1'
            prefix+=f"""SET LOCAL session_replication_role=replica;
             UPDATE trading.side_schedule_rows SET parameters=jsonb_set(parameters,'{{from}}','\"2021-01-02\"'::jsonb)
              WHERE(catalog_id,schedule_id,operation)=('{manifest['catalogId']}','{schedule}','curve_price_lookup');
             UPDATE trading.side_schedule_programs SET content=convert_to(trading.side_program_document(catalog_id,schedule_id)::text,'UTF8'),
              content_hash=sha256(convert_to(trading.side_program_document(catalog_id,schedule_id)::text,'UTF8')) WHERE(catalog_id,schedule_id)=('{manifest['catalogId']}','{schedule}');
             SET LOCAL session_replication_role=origin;
             SELECT trading.require_side_program_content('{manifest['catalogId']}','{schedule}',content)
              FROM trading.side_schedule_programs WHERE(catalog_id,schedule_id)=('{manifest['catalogId']}','{schedule}');"""
        write=f"""result=security.save_composed_swap('{manifest['catalogId']}','{operation}',NULL,NULL,(p->>'book')::uuid,
         (p->>'definition')::uuid,(p->>'referenceCatalog')::uuid,(p->>'referenceRevision')::uuid,p->>'commodity',
         p->'fixed',NULL,p->'floating',(p->>'quantity')::numeric,(p->>'coefficient')::numeric,(p->>'discounting')::boolean,
         (p->>'valuation')::timestamptz,(p->>'effective')::timestamptz,p->>'reason',p->>'implementation',p->>'memo',p->'booking','{{}}'::uuid[]);
         PERFORM security.publish_side_schedules('{manifest['catalogId']}','{operation}',(result->>'instrumentId')::uuid);"""
        if changed:write="BEGIN "+write+"RAISE EXCEPTION 'mismatched_composed_program_accepted';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'side_schedule_definition' THEN RAISE;END IF;END;"
        try:runtime_query(query,manifest,f'DO $agreement$ DECLARE result jsonb;p jsonb={payload};BEGIN '+write+'END $agreement$;',prefix=prefix,commit=False)
        except RuntimeError as error:
            if 'mismatched_composed_program_accepted' not in str(error):raise
            disagreements.append('composed_'+changed)
    for changed in (None,'price','new_price','spread','constant','quantity','weight','pv','identity','allocation','allocation_tag','index','third_adder','mapping','trade_quantity','relative_quantity','authored_quantity','canonical_payment','canonical_first_fixing','canonical_average','canonical_implementation','canonical_calendar','canonical_observations','canonical_fixings','canonical_contracts','canonical_side_tag','canonical_tag','canonical_extra','canonical_projection','canonical_spread','canonical_pricing','canonical_price_shape','canonical_side_extra','canonical_gas_clock'):
        original_request=surcharge if changed=='third_adder' else scheduled
        native=deepcopy(original_request);operation=str(uuid4());native['operationId']=operation
        if changed in ('price','new_price'):native['scheduled']['first']['periods'][0]['fixed']['price']='4'
        if changed=='authored_quantity':native['scheduled']['first']['periods'][0]['amount']='101'
        if changed=='spread':native['scheduled']['second']['periods'][0]['floating']['spread']='1'
        parties={key:native[key] for key in ('legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision')}
        literal=lambda value:"'"+json.dumps(value).replace("'","''")+"'::jsonb"
        shape=[dict(relativeNumerator='1',relativeDenominator='1',periods=[dict(numerator=str(n),denominator='3',fixingPins=[]) for n in (1,2)]) for _ in range(2)]
        if changed=='relative_quantity':shape[1]['relativeNumerator']='2'
        prefix=copy_stage(manifest['catalogId'],original_request['operationId'],operation)+f"""
          INSERT INTO trading.side_schedule_request_seals SELECT catalog_id,'{operation}',slot_count,input_count
           FROM trading.side_schedule_request_seals WHERE(catalog_id,request_id)=('{manifest['catalogId']}','{original_request['operationId']}');
          ALTER TABLE trading.side_schedule_requests DISABLE TRIGGER USER;
          UPDATE trading.side_schedule_requests SET command_hash=sha256(convert_to(trading.scheduled_swap_command(
           {literal(native)},'2020-12-18T16:00:00Z',{literal(parties)})::text,'UTF8'))
           WHERE(catalog_id,request_id)=('{manifest['catalogId']}','{operation}');
          ALTER TABLE trading.side_schedule_requests ENABLE TRIGGER USER;
        """
        if changed=='new_price':
            # Remove only the native fixed period/header in this rolled-back
            # owner fixture, leaving a sealed Side awaiting its first write.
            prefix+=f"""SET LOCAL session_replication_role=replica;
             DELETE FROM trading.swap_side_periods WHERE(catalog_id,side_id)=('{manifest['catalogId']}','{scheduled_editor['valuation']['sides'][0]['sideId']}');
             DELETE FROM trading.swap_whole_term_sides WHERE(catalog_id,id)=('{manifest['catalogId']}','{scheduled_editor['valuation']['sides'][0]['sideId']}');
             SET LOCAL session_replication_role=origin;"""
        if changed in ('constant','quantity','weight','pv','identity','allocation','allocation_tag','index','third_adder','mapping') or changed and changed.startswith('canonical_'):
            slot=1 if changed in ('index','third_adder','mapping') or changed in ('canonical_first_fixing', 'canonical_average', 'canonical_implementation', 'canonical_calendar', 'canonical_observations', 'canonical_fixings', 'canonical_contracts', 'canonical_side_tag','canonical_tag', 'canonical_extra', 'canonical_projection', 'canonical_spread', 'canonical_pricing') else 0
            schedule=query(f"SELECT schedule_id FROM trading.side_schedule_request_items WHERE(catalog_id,request_id,slot)=('{manifest['catalogId']}','{original_request['operationId']}',{slot});")
            if changed=='identity':
                mutation=f"""UPDATE trading.sides SET economic_identity=jsonb_set(economic_identity::jsonb,'{{QuantityBasis,2}}','\"test_power_quantity\"'::jsonb)::text
                 WHERE(catalog_id,id)=('{manifest['catalogId']}','{scheduled_editor['valuation']['sides'][0]['sideId']}');"""
            elif changed in ('canonical_first_fixing', 'canonical_average', 'canonical_implementation', 'canonical_calendar', 'canonical_observations', 'canonical_fixings', 'canonical_contracts', 'canonical_side_tag','canonical_tag', 'canonical_extra', 'canonical_projection', 'canonical_spread', 'canonical_pricing'):
                side_id=scheduled_editor['valuation']['sides'][1]['sideId']
                values={'canonical_first_fixing':('{Periods,0,FirstFixingAt}',"to_jsonb('2021-01-02T00:00:00.0000000+00:00'::text)"),
                    'canonical_average':('{Periods,0,Averaging}','to_jsonb(1)'),
                    'canonical_implementation':('{Periods,0,ObservationImplementation}',"to_jsonb('unsupported'::text)"),
                    'canonical_calendar':('{Periods,0,Calendar}',"'[]'::jsonb"),
                    'canonical_observations':('{Periods,0,ObservationSchedule}',"(economic_identity::jsonb#>'{Periods,0,ObservationSchedule}')||jsonb_build_array(economic_identity::jsonb#>'{Periods,0,ObservationSchedule,0}')"),
                    'canonical_fixings':('{Periods,0,Fixings}',"(economic_identity::jsonb#>'{Periods,0,Fixings}')||jsonb_build_array(economic_identity::jsonb#>'{Periods,0,Fixings,0}')"),
                    'canonical_contracts':('{Periods,0,ContractObservations}',"'[]'::jsonb"),
                    'canonical_side_tag':('{contract}',"'null'::jsonb"),
                    'canonical_tag':('{Periods,0,contract}',"to_jsonb('floating_side_period_v4'::text)"),
                    'canonical_extra':('{Periods,0,UnexpectedRule}',"to_jsonb(true)"),
                    'canonical_projection':('{Periods,0,ProjectionCurve,0}',"to_jsonb('unsupported'::text)"),
                    'canonical_spread':('{Periods,0,Spread}',"jsonb_build_array('price','0',jsonb_build_array('price_unit',economic_identity::jsonb#>'{DateQuote,3}',economic_identity::jsonb#>'{DateQuote,4}',economic_identity::jsonb#>>'{DateQuote,5}'))")}
                if changed=='canonical_pricing':mutation=f"UPDATE trading.sides SET pricing_binding_identity='{{}}' WHERE(catalog_id,id)=('{manifest['catalogId']}','{side_id}');"
                else:
                    path,value=values[changed]
                    mutation=f"UPDATE trading.sides SET economic_identity=jsonb_set(economic_identity::jsonb,'{path}',{value})::text WHERE(catalog_id,id)=('{manifest['catalogId']}','{side_id}');"
            elif changed in ('canonical_price_shape','canonical_side_extra','canonical_gas_clock'):
                path,value={
                    'canonical_price_shape':('{Price}',"jsonb_build_object('contract','ppa_stepped_price_v1','Steps',(SELECT jsonb_agg(jsonb_build_object('contract','ppa_price_step_v1','From',p->'DeliveryFrom','Price',economic_identity::jsonb#>'{Price,Price}') ORDER BY n) FROM jsonb_array_elements(economic_identity::jsonb->'Periods') WITH ORDINALITY r(p,n)))"),
                    'canonical_side_extra':('{UnexpectedRule}',"to_jsonb(true)"),
                    'canonical_gas_clock':('{GasDeliveryClock}',"'{}'::jsonb")}[changed]
                mutation=f"UPDATE trading.sides SET economic_identity=jsonb_set(economic_identity::jsonb,'{path}',{value})::text WHERE(catalog_id,id)=('{manifest['catalogId']}','{scheduled_editor['valuation']['sides'][0]['sideId']}');"
            elif changed=='canonical_payment':
                mutation=f"""UPDATE trading.sides SET economic_identity=jsonb_set(economic_identity::jsonb,'{{Periods,0,PaymentSchedule,4}}',to_jsonb(19))::text
                 WHERE(catalog_id,id)=('{manifest['catalogId']}','{scheduled_editor['valuation']['sides'][0]['sideId']}');"""
            elif changed in ('allocation','allocation_tag'):
                path='{Periods,0,Allocation}' if changed=='allocation' else '{Periods,0,Allocation,0}'
                value='null' if changed=='allocation' else '"unsupported_ratio"'
                mutation=f"""UPDATE trading.sides SET economic_identity=jsonb_set(economic_identity::jsonb,'{path}','{value}'::jsonb)::text
                 WHERE(catalog_id,id)=('{manifest['catalogId']}','{scheduled_editor['valuation']['sides'][0]['sideId']}');"""
            elif changed=='index':
                other_index=next(i['index'] for i in catalog['indices'] if i['index']!=index['index'])
                mutation=f"""UPDATE trading.side_schedule_bindings SET index_code='{other_index}'
                 WHERE(catalog_id,schedule_id,port)=('{manifest['catalogId']}','{schedule}','index');"""
            elif changed=='mapping':
                mutation=f"""UPDATE trading.side_schedule_rows SET parameters=jsonb_set(parameters,'{{mapping}}','\"delivery_index_fixings_v1\"'::jsonb)
                 WHERE(catalog_id,schedule_id,operation)=('{manifest['catalogId']}','{schedule}','price_application');"""
            elif changed=='third_adder':
                mutation=f"""INSERT INTO trading.side_schedule_edges SELECT catalog_id,schedule_id,target,port,2,source,
                  (SELECT max(sequence_ordinal)+1 FROM trading.side_schedule_edges WHERE(catalog_id,schedule_id)=(e.catalog_id,e.schedule_id))
                 FROM trading.side_schedule_edges e WHERE(catalog_id,schedule_id,port,ordinal)=('{manifest['catalogId']}','{schedule}','input',1)
                  AND target=(SELECT min(ordinal) FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,operation)=('{manifest['catalogId']}','{schedule}','price_add'));
                 UPDATE trading.side_schedules SET edge_count=edge_count+1 WHERE(catalog_id,id)=('{manifest['catalogId']}','{schedule}');"""
            elif changed=='pv':
                mutation=f"""UPDATE trading.side_schedule_edges SET source=(SELECT row_ordinal FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id,name)=('{manifest['catalogId']}','{schedule}','payment/0/pv'))
                 WHERE(catalog_id,schedule_id,target,ordinal)=('{manifest['catalogId']}','{schedule}',(SELECT row_ordinal FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id,name)=('{manifest['catalogId']}','{schedule}','pv')),1);"""
            else:
                operation_kind={'constant':'constant_price','quantity':'quantity_literal','weight':'price_application'}[changed]
                mutation=f"""UPDATE trading.side_schedule_rows SET parameters=jsonb_set(parameters,'{{numerator}}',to_jsonb(((parameters->>'numerator')::numeric+1)::text))
                 WHERE(catalog_id,schedule_id,operation,payment_ordinal)=('{manifest['catalogId']}','{schedule}','{operation_kind}',0);"""
            prefix+=f"""SET LOCAL session_replication_role=replica;{mutation}
             UPDATE trading.side_schedule_programs SET content=convert_to(trading.side_program_document(catalog_id,schedule_id)::text,'UTF8'),
              content_hash=sha256(convert_to(trading.side_program_document(catalog_id,schedule_id)::text,'UTF8'))
              WHERE(catalog_id,schedule_id)=('{manifest['catalogId']}','{schedule}');
             SET LOCAL session_replication_role=origin;
             SELECT trading.require_side_program_content('{manifest['catalogId']}','{schedule}',content)
              FROM trading.side_schedule_programs WHERE(catalog_id,schedule_id)=('{manifest['catalogId']}','{schedule}');"""
        write=f"""result=security.save_scheduled_swap('{manifest['catalogId']}',{literal(native)},
          {literal(shape)},{301 if changed=='trade_quantity' else 300},'2020-12-18T16:00:00Z',{literal(parties)},'{{}}'::uuid[]);
          PERFORM security.publish_side_schedules('{manifest['catalogId']}','{operation}',(result->>'instrumentId')::uuid);"""
        if changed is not None:
            expected_error="delivery_completion" if changed=="canonical_gas_clock" else "side_schedule_definition"
            write="BEGIN "+write+f"RAISE EXCEPTION 'mismatched_program_accepted:{changed}'; EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'{expected_error}' THEN RAISE;END IF;END;"
        try:runtime_query(query,manifest,'DO $agreement$ DECLARE result jsonb;BEGIN '+write+'END $agreement$;',prefix=prefix,commit=False)
        except RuntimeError as error:
            if 'mismatched_program_accepted:' not in str(error):raise
            disagreements.append(changed)
    from check_scheduled_pair_admission import run_checks as check_pair_admission
    try:check_pair_admission(query,manifest,scheduled)
    except AssertionError as error:disagreements.append(str(error))
    assert not disagreements, f'Native admission accepted mismatches: {disagreements}'
    print('PASS native request agreement: identical staged programs publish; fresh/reused price mismatches and rehashed expression mutations refuse',flush=True)


    from check_dated_quantity_admission import run_checks as check_quantity_admission
    check_quantity_admission(query,manifest,scheduled)
    from fractions import Fraction
    previous=deepcopy(scheduled);previous['operationId']=str(uuid4())
    previous['scheduled']['second']['calendarPolicy']['observation']['calendar']['code']='test_business'
    for entry in previous['scheduled']['second']['periods']:
        entry['floating'].update(averagingMethod='calendar_days_previous',observationSchedule=dict(implementation='side_observation_schedule_v1',calendar=None,quantities=None))
    prior_preview=client.call('swaps/preview',previous)['valuation']
    # Explicit expected observation numbers from the synthetic daily source:
    # January 1 is closed; Saturday/Sunday map to the preceding weekday.
    jan=[0,0,0,4,5,6,7,8,8,8,11,12,13,14,15,15,15,18,19,20,21,22,22,22,25,26,27,28,29,29,29]
    feb=[32,33,34,35,36,36,36,39,40,41,42,43,43,43,46,47,48,49,50,50,50,53,54,55,56,57,57,57]
    from exact_values import fraction
    expected=Fraction(sum(jan),31)+2*Fraction(sum(feb),28)
    assert Fraction(prior_preview['presentValue'])==expected
    prior_saved=client.call('swaps/book',previous)
    prior_editor=client.call('swaps/editor',dict(context=context,tradeId=prior_saved['tradeId'],revision=None))
    assert Fraction(prior_editor['valuation']['presentValue'])==expected
    assert prior_editor['valuation']['sides'][1]['payments'][0]['resets'][0]['from']=='2020-12-31'
    assert prior_editor['state']['terms']['scheduled']['second']['calendarPolicy']==previous['scheduled']['second']['calendarPolicy']
    print('PASS observation calendar HTTP: distinct purpose bindings, previous-day guards and exact weighted monthly cash',flush=True)

    power_index=next(i for i in catalog['indices'] if i['curve']=='daily_power')
    power=deepcopy(scheduled);power.update(operationId=str(uuid4()),commodity=power_index['commodity'])
    for slot,kind in [('first','fixed'),('second','floating')]:
        side=power['scheduled'][slot]
        side.update(basis='selected_interval_power',quantity=dict(revision=reference['revision'],code='test_capacity_quantity'),
                    power=dict(clock=dict(revision=reference['revision'],code='synthetic_eastern'),product='weekday_block',startHour=6,endHour=22,saturday=False,calendar=None))
        for entry in side['periods']:
            entry['amount']='2'
            if kind=='fixed':entry[kind]['price']='40'
            else:entry[kind].update(curve=power_index['curve'],index=power_index['index'])
    power_preview=client.call('swaps/preview',power)
    assert power_preview['valuation']['presentValue']=='3987.2'
    assert [p['deliveryHours'] for p in power_preview['quantityInputs'][0]['periods']]==['336','320']
    power_saved=client.call('swaps/book',power)
    power_retained=client.call('swaps/editor',dict(context=context,tradeId=power_saved['tradeId'],revision=None))
    assert power_retained['valuation']['presentValue']=='3987.2'
    assert query(f"SELECT count(*) FROM trading.side_schedules h JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id) JOIN trading.side_schedule_request_items i ON(i.catalog_id,i.schedule_id)=(h.catalog_id,h.id) WHERE i.request_id='{power['operationId']}' AND s.economic_identity::jsonb#>>'{{PowerQuantity,contract}}'='scheduled_power_quantity_parameters_v1';")=='2'
    completed=query(f"SELECT to_char(settlement_data.swap_completion_at('{manifest['catalogId']}','{power_saved['tradeId']}',1,'{power_saved['instrumentId']}',0,'2021-01-01','2021-02-01') AT TIME ZONE 'UTC','YYYY-MM-DD HH24:MI:SS');")
    assert completed=='2021-02-01 05:00:00', 'Completion is the full contractual clock boundary, even when peak ends on Friday.'
    query(f"""BEGIN;DO $test$ DECLARE draft uuid=gen_random_uuid();definition jsonb;period jsonb;BEGIN
      INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{manifest['referenceCatalogId']}',draft,'synthetic_unpublished_clock');
      INSERT INTO ref_data.clock_rules SELECT catalog_id,draft,code,kind,fixed_offset_seconds,iana_zone,tzdb_version FROM ref_data.clock_rules
       WHERE(catalog_id,revision_id,code)=('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','synthetic_eastern');
      INSERT INTO ref_data.clock_captures SELECT catalog_id,draft,clock_code,coverage_from,coverage_to,payload,definition_digest,tzdb_version FROM ref_data.clock_captures
       WHERE(catalog_id,revision_id,clock_code)=('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','synthetic_eastern');
      SELECT s.economic_identity::jsonb INTO STRICT definition FROM trading.side_schedule_request_items i JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(i.catalog_id,i.schedule_id)
       JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id) WHERE(i.request_id,i.slot)=('{power['operationId']}',0);
      period=definition#>'{{Periods,0}}';definition=jsonb_set(definition,'{{PowerQuantity,Clock,1,2}}',to_jsonb(draft::text));
      definition=jsonb_set(definition,'{{DateQuoteKey,1,2}}',to_jsonb(draft::text));
      BEGIN PERFORM security.dated_side_completion(definition,period);RAISE EXCEPTION 'unpublished_clock_accepted';
       EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'clock_reference' THEN RAISE;END IF;END;
     END $test$;ROLLBACK;""")
    composed_power=deepcopy(body);composed_power.update(operationId=str(uuid4()),commodity=power_index['commodity'],definitionRevisionId=power_index['definitionRevisionId'])
    composed_power['fixed']['price']='40'
    composed_power['floating'].update(curve=power_index['curve'],index=power_index['index'])
    client.call('swaps/book',composed_power)
    from check_composed_calendar_admission import run_checks as check_composed_admission
    check_composed_admission(query,manifest,composed_power,power)
    from check_dated_power_admission import run_checks as check_power_admission
    check_power_admission(query,manifest,power,power_saved)
    signature='security.save_scheduled_swap(uuid,jsonb,jsonb,numeric,timestamptz,jsonb,uuid[])'
    original=query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    mutation="""
 compiled=jsonb_set(compiled,'{0,clock,rules}',to_jsonb(replace(compiled#>>'{0,clock,rules}','America/New_York','Synthetic/Changed')));
 compiled=jsonb_set(compiled,'{0,clock,fingerprint}',to_jsonb(encode(sha256(convert_to(compiled#>>'{0,clock,rules}','UTF8')),'hex')));
"""
    assert '\nBEGIN\n' in original
    try:
        query(original.replace('\nBEGIN\n','\nBEGIN\n'+mutation,1))
        invalid=deepcopy(power);invalid['operationId']=str(uuid4())
        assert client.call('swaps/book',invalid,expected=422)['code']=='clock_reference'
    finally: query(original)
    # A staged header and its clock must belong to the pricing definition that
    # native booking accepted, even if every staged clock field agrees internally.
    marker="kind=(side_body->'periods'->0->>'fixed') IS NOT NULL;"
    assert marker in original
    try:
        query(original.replace(marker,"definition=gen_random_uuid();"+marker,1))
        invalid=deepcopy(power);invalid['operationId']=str(uuid4())
        assert client.call('swaps/book',invalid,expected=422)['code']=='clock_reference'
    finally: query(original)
    east=deepcopy(power);east['operationId']=str(uuid4())
    east['scheduled']['first']['power']['clock']['code']='synthetic_fixed_east'
    for entry in east['scheduled']['first']['periods']:entry['fixed']['paymentSchedule']['days']=0
    east_preview=client.call('swaps/preview',east)
    assert [p['paymentDate'] for p in east_preview['valuation']['sides'][0]['payments']]==['2021-01-31','2021-02-28']
    east_saved=client.call('swaps/book',east)
    east_retained=client.call('swaps/editor',dict(context=context,tradeId=east_saved['tradeId'],revision=None))
    assert east_retained['valuation']['presentValue']==east_preview['valuation']['presentValue']=='3987.2'
    assert [p['paymentDate'] for p in east_retained['valuation']['sides'][0]['payments']]==['2021-01-31','2021-02-28']
    assert query(f"SELECT to_char(settlement_data.swap_completion_at('{manifest['catalogId']}','{east_saved['tradeId']}',1,'{east_saved['instrumentId']}',0,'2021-01-01','2021-02-01') AT TIME ZONE 'UTC','YYYY-MM-DD HH24:MI:SS');")=='2021-01-31 13:00:00'
    print('PASS scheduled power calendar HTTP: captured generation parameters, explicit weekly hours and cash replay',flush=True)

    from ppa_demo_terms import profile_terms
    source_editor=PreviewClient(url);source_editor.bootstrap();source_editor.call('profile', {'profile':'editor'});source_editor.bootstrap()
    source_editor.call('interval-series/revise',dict(requestId=str(uuid4()),seriesId=manifest['intervalCurves']['shapeSeries']['additive'],
                        expectedRevision=0,reason='Synthetic zero hourly shape',segments=[dict(start='2031-11-01T04:00:00Z',values=['0']*721)]))
    terms=profile_terms(manifest)
    coverage=dict(coverageFrom='2031-11-02T04:00:00Z', coverageTo='2031-11-03T05:00:00Z')
    valid=client.call('ppa/validate', dict(snapshotId=frame['id'], terms=terms, quantity='80', **coverage))
    ppa_body=dict(operationId=str(uuid4()),snapshotId=frame['id'],tradeId=None,expectedRevision=None,
                  bookId='d1000000-0000-0000-0000-000000000001',boundaryAt=None,quantity='80',reason='Synthetic calendar retention',
                  memo='',booking=trade_booking(),terms=terms,**coverage)
    ppa=client.call('ppa/book',ppa_body)
    assert client.call('ppa/book',ppa_body)==ppa
    known=client.call('interval-series')['knownAt']
    editor=client.call('ppa/editor', dict(snapshotId=frame['id'], tradeId=ppa['tradeId'], knownAt=known))
    assert editor['instruments'][0]['terms']==terms
    assert client.call('ppa/validate', dict(snapshotId=frame['id'], terms=editor['instruments'][0]['terms'], quantity='80', **coverage))['identity']==valid['identity']
    assert query(f"SELECT count(*) FROM trading.ppa_memberships m JOIN trading.sides s ON(s.catalog_id,s.id)=(m.catalog_id,(SELECT canonical_side_id FROM trading.ppa_sides WHERE(catalog_id,id)=(m.catalog_id,m.side_id))) WHERE m.instrument_id='{ppa['bindings'][0]['instrumentId']}';")=='2'
    query(f"""BEGIN;ALTER TABLE trading.sides DISABLE TRIGGER USER;
      DO $test$ DECLARE schedule uuid;expected jsonb;BEGIN
        SELECT i.schedule_id INTO STRICT schedule FROM trading.side_schedule_request_items i WHERE(i.catalog_id,i.request_id,i.slot)=('{manifest['catalogId']}','{ppa_body['operationId']}',0);
        SELECT jsonb_build_array(jsonb_build_object('ordinal',0,'deliveryFrom','2031-11-01','deliveryTo','2031-12-01','paymentAt',r.value_at,
         'paymentRule',(r.parameters->>'paymentRule')::jsonb,'unadjustedPayment','none')) INTO STRICT expected
         FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.operation)=('{manifest['catalogId']}',schedule,'output_guard') LIMIT 1;
        SET LOCAL DateStyle='SQL, DMY';
        PERFORM trading.require_side_payment_dates('{manifest['catalogId']}',schedule,expected);
        SET LOCAL DateStyle='ISO, YMD';
        BEGIN
        UPDATE trading.sides s SET economic_identity=jsonb_set(s.economic_identity::jsonb,'{{Terms,Schedule,ClockRules}}',to_jsonb('{{}}'::text))::text
         WHERE(s.catalog_id,s.id)=(SELECT h.catalog_id,h.side_id FROM trading.side_schedule_request_items i JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(i.catalog_id,i.schedule_id)
          WHERE(i.catalog_id,i.request_id,i.slot)=('{manifest['catalogId']}','{ppa_body['operationId']}',0));
        PERFORM trading.require_side_payment_dates('{manifest['catalogId']}',schedule,expected);
        RAISE EXCEPTION 'altered_ppa_clock_accepted';
       EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'clock_reference' THEN RAISE;END IF;END;END $test$;ROLLBACK;""")
    query(f"""BEGIN;ALTER TABLE trading.sides DISABLE TRIGGER USER;
      DO $test$ DECLARE schedule uuid;expected jsonb;capture ref_data.clock_captures;BEGIN
       SELECT i.schedule_id INTO STRICT schedule FROM trading.side_schedule_request_items i WHERE(i.catalog_id,i.request_id,i.slot)=('{manifest['catalogId']}','{ppa_body['operationId']}',1);
       SELECT * INTO STRICT capture FROM ref_data.clock_captures WHERE(catalog_id,revision_id,clock_code)=('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','synthetic_fixed_east');
       SELECT jsonb_build_array(jsonb_build_object('ordinal',0,'deliveryFrom','2031-11-01','deliveryTo','2031-12-01','paymentAt',r.value_at,
        'paymentRule',(r.parameters->>'paymentRule')::jsonb,'unadjustedPayment','none')) INTO STRICT expected
        FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.operation)=('{manifest['catalogId']}',schedule,'output_guard') LIMIT 1;
       BEGIN
        UPDATE trading.sides s SET economic_identity=jsonb_set(jsonb_set(jsonb_set(s.economic_identity::jsonb,
         '{{IntervalDefinition,InputPorts,0,Schedule,ClockRules}}',to_jsonb(capture.payload)),
         '{{IntervalDefinition,InputPorts,0,Schedule,ClockFingerprint}}',to_jsonb(encode(capture.definition_digest,'hex'))),
         '{{IntervalDefinition,InputPorts,0,Schedule,Delivery,Clock,2}}',to_jsonb(capture.clock_code::text))::text
         WHERE(s.catalog_id,s.id)=(SELECT h.catalog_id,h.side_id FROM trading.side_schedules h WHERE(h.catalog_id,h.id)=('{manifest['catalogId']}',schedule));
        PERFORM trading.require_side_payment_dates('{manifest['catalogId']}',schedule,expected);RAISE EXCEPTION 'changed_source_clock_accepted';
       EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'ppa_schedule_source' THEN RAISE;END IF;END;
      END $test$;ROLLBACK;""")
    print('PASS PPA calendar HTTP: linked delivery ownership, canonical members, captured clock identity and native replay', flush=True)

    common=dict(snapshotId=frame['id'],definitionRevisionId=manifest['revisionId'],referenceCatalogId=manifest['referenceCatalogId'],
                referenceRevisionId=manifest['referenceRevisionId'],knownAt=known,pendingActuals='require_actuals_v1',detailMonths=['2031-11-01'])
    draft=client.call('ppa/preview',dict(**common,scope='draft',selectedIds=[],explicitSubset=False,terms=terms,quantity='80',**coverage))
    replay=client.call('ppa/preview',dict(**common,scope='trades',selectedIds=[ppa['tradeId']],explicitSubset=True,terms=None,quantity=None,coverageFrom=None,coverageTo=None))
    from exact_values import fraction
    assert fraction(replay['valueExcludingOverdue'])==fraction(draft['valueExcludingOverdue'])==2500
    assert sum(len(s['measures']['rows']) for s in replay['bindings'][0]['sides'])==50, 'Both Sides retain the same 25-hour fall-back delivery grid.'
    print('PASS PPA calendar HTTP: 25-hour DST quantity and cash replay agree with the original preview', flush=True)

    duplicate_terms=deepcopy(terms)
    charge=deepcopy(duplicate_terms['memberships'][1]);charge.update(role='fixed_charge',coefficient='1')
    duplicate_terms['memberships'].append(charge)
    duplicate_body={**ppa_body,'operationId':str(uuid4()),'terms':duplicate_terms}
    client.call('ppa/validate',dict(snapshotId=frame['id'],terms=duplicate_terms,quantity='80',**coverage))
    duplicate=client.call('ppa/book',duplicate_body)
    duplicate_editor=client.call('ppa/editor',dict(snapshotId=frame['id'],tradeId=duplicate['tradeId'],knownAt=client.call('interval-series')['knownAt']))
    assert duplicate_editor['instruments'][0]['terms']==duplicate_terms
    ids=duplicate_editor['instruments'][0]['sideIds']
    assert len({s['sideId'] for s in ids})==3
    duplicate_value=client.call('ppa/preview',dict(**{**common,'knownAt':client.call('interval-series')['knownAt']},scope='trades',selectedIds=[duplicate['tradeId']],explicitSubset=True,terms=None,quantity=None,coverageFrom=None,coverageTo=None))
    assert fraction(duplicate_value['valueExcludingOverdue'])==82500
    print('PASS PPA membership HTTP: identical strike and fixed-charge economics retain independent membership identity and cash',flush=True)

    from check_booked_side_programs import runtime_query
    query("""CREATE FUNCTION security.synthetic_verify_payment_dates(catalog uuid,operation uuid,instrument uuid) RETURNS void
      LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$SELECT security.require_staged_payment_dates(catalog,operation,instrument)$$;
      ALTER FUNCTION security.synthetic_verify_payment_dates(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
      REVOKE ALL ON FUNCTION security.synthetic_verify_payment_dates(uuid,uuid,uuid) FROM PUBLIC;
      GRANT EXECUTE ON FUNCTION security.synthetic_verify_payment_dates(uuid,uuid,uuid) TO preview_runtime;""")
    try:
        for zone in ['America/Chicago','Asia/Kathmandu']:
            runtime_query(query,manifest,f"SET LOCAL TimeZone='{zone}';SELECT security.synthetic_verify_payment_dates('{manifest['catalogId']}','{ppa_body['operationId']}','{ppa['bindings'][0]['instrumentId']}');")
    finally:
        query('DROP FUNCTION security.synthetic_verify_payment_dates(uuid,uuid,uuid);')
    print('PASS native payment admission: session timezones leave contractual PPA payment dates unchanged',flush=True)
