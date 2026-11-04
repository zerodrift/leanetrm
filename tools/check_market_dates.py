"""Authorized, exact-snapshot date availability for the header calendar."""
from copy import deepcopy
from uuid import uuid4
from preview_client import PreviewClient


def run_checks(url,query=None,manifest=None):
    client=PreviewClient(url);boot=client.bootstrap()
    client.call('market/dates',{'curve':'ng_monthly','timeZone':'UTC'},expected=403)
    client.call('profile',{'profile':'viewer'})
    client.call('market/dates',{'timeZone':'UTC'},expected=400)
    client.call('market/dates',{'curve':None},expected=400)
    monthly=client.call('market/dates',{'curve':'ng_monthly','timeZone':'UTC'})
    assert monthly['selectionPolicy']=='exact_snapshot_no_close_substitution_v1'
    month_days={row['date']:row['frames'] for row in monthly['dates']}
    assert '2020-12-11' in month_days
    bridge=client.call('market/dates',{'curve':'bridge_basis','timeZone':'UTC'})
    assert {row['date'] for row in bridge['dates']}=={'2020-12-15','2020-12-16','2020-12-17','2020-12-18'}
    assert '2020-12-11' not in {row['date'] for row in bridge['dates']}
    tokyo=client.call('market/dates',{'curve':'ng_monthly','timeZone':'Asia/Tokyo'})
    shifted=next(row for row in tokyo['dates'] if row['date']=='2020-12-12')['frames']
    assert all(row['businessDateUTC']=='2020-12-11' and row['at'].startswith('2020-12-11T16:00:00') for row in shifted)
    assert {row['id'] for day in monthly['dates'] for row in day['frames']}=={row['id'] for day in tokyo['dates'] for row in day['frames']}
    known={row['id'] for row in boot['frames']}
    workspace=client.call('market/dates',{'curve':None,'timeZone':'UTC'})
    assert {row['id'] for day in workspace['dates'] for row in day['frames']}<=known
    for body,full in [({'curve':None,'timeZone':'UTC'},workspace),({'curve':'ng_monthly','timeZone':'Asia/Tokyo'},tokyo),({'curve':'bridge_basis','timeZone':'UTC'},bridge)]:
        latest=client.call('market/dates',{**body,'maximumDates':1})
        assert latest['dates']==full['dates'][-1:] and latest['maxDate']==full['maxDate']
        assert latest['hasMoreCandidates'] is True
    for maximum in (0,129):
        client.call('market/dates',{'curve':None,'timeZone':'UTC','maximumDates':maximum},expected=422)
    if query and manifest:
        newest={frame['id'] for frame in workspace['dates'][-1]['frames']}
        latest_pins={pin for frame in boot['frames'] if frame['id'] in newest for pin in frame['observationIds']}
        old_pin=next(pin for frame in boot['frames'] for pin in frame['observationIds'] if pin not in latest_pins)
        query(f"""CREATE FUNCTION public.synthetic_older_candidate(id uuid) RETURNS boolean LANGUAGE plpgsql VOLATILE AS $$BEGIN
          IF id='{old_pin}'::uuid THEN RAISE EXCEPTION 'Synthetic older candidate read' USING ERRCODE='42501';END IF;
          RETURN true;END$$;
          CREATE POLICY synthetic_older_candidate ON market_data.observations AS RESTRICTIVE
           FOR SELECT TO openetrm_runtime USING(public.synthetic_older_candidate(id));""")
        try:
            assert client.call('market/dates',{'curve':None,'timeZone':'UTC','maximumDates':1})['dates']==workspace['dates'][-1:]
            client.call('market/dates',{'curve':None,'timeZone':'UTC'},expected=403)
        finally:
            query('DROP POLICY synthetic_older_candidate ON market_data.observations;DROP FUNCTION public.synthetic_older_candidate(uuid);')
        print('PASS latest-date availability never reads an older candidate after its newest date qualifies',flush=True)
    client.call('market/dates',{'curve':'missing_curve','timeZone':'UTC'},expected=422)
    client.call('market/dates',{'curve':'ng_monthly','timeZone':'Not/AZone'},expected=422)
    templates={row['code']:row for row in client.call('custom-curves')['templates']}
    client.call('market/dates',{'definition':templates['ng_prompt']['definition'],'timeZone':'UTC'},expected=400)
    average_request={'curve':None,'definition':templates['ng_average']['definition'],'timeZone':'UTC'}
    if query and manifest:
        # Count reads of one retained fact under a bound financial context.
        # Qualifying many dates must not reload the complete history per date.
        anchor=query(f"SELECT id FROM market_data.observations WHERE catalog_id='{manifest['catalogId']}' AND definition_revision_id='{manifest['revisionId']}' AND role='daily_settlement' ORDER BY observed_at,id LIMIT 1;")
        query(f"""CREATE SEQUENCE public.synthetic_calendar_history_reads;
          CREATE FUNCTION public.synthetic_calendar_history_read(observation uuid) RETURNS boolean
          LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$BEGIN
           IF observation='{anchor}'::uuid AND EXISTS(SELECT 1 FROM security.request_contexts c
             WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
              AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL) THEN
            PERFORM nextval('public.synthetic_calendar_history_reads');
           END IF;
           RETURN true;END$$;
          CREATE POLICY synthetic_calendar_history_read ON market_data.observations AS RESTRICTIVE
           FOR SELECT TO openetrm_runtime USING(public.synthetic_calendar_history_read(id));""")
        try:
            before_cutoff=query(f"SELECT coalesce(max(known_at),'-infinity'::timestamptz)::text FROM market_data.temporal_contexts WHERE catalog_id='{manifest['catalogId']}';")
            average=client.call('market/dates',average_request)
            reads=query('SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM public.synthetic_calendar_history_reads;')
            assert reads in ('1','2'), f'Custom date availability reloaded history beyond the shared samples and one exact frame: {reads}'
            coordinates=query(f"SELECT count(DISTINCT known_at),count(DISTINCT effective_on) FROM market_data.temporal_contexts WHERE catalog_id='{manifest['catalogId']}' AND known_at>'{before_cutoff}'::timestamptz;").split('|')
            assert coordinates[0]=='1' and int(coordinates[1])>1, 'Working date availability must resolve each effective date at one certified knowledge cutoff'
            assert client.call('custom-curves')['context'] is None, 'Availability changed the working header context'
        finally:
            query('DROP POLICY synthetic_calendar_history_read ON market_data.observations;DROP FUNCTION public.synthetic_calendar_history_read(uuid);DROP SEQUENCE public.synthetic_calendar_history_reads;')
    else:
        average=client.call('market/dates',average_request)
    assert '2020-11-18' not in {row['date'] for row in average['dates']}
    assert '2020-12-18' in {row['date'] for row in average['dates']}
    latest_average=client.call('market/dates',{**average_request,'maximumDates':1})
    assert latest_average['dates']==average['dates'][-1:]
    if query and manifest:
        business=deepcopy(templates['ng_average']['definition'])
        calendar=client.call('custom-curves')['calendars'][0]['code']
        next(node for node in business['nodes'] if node['kind']=='dates')['parameters'].update(mode='prior_business',calendar=calendar)
        anchor_day=query(f"SELECT min(business_date) FROM ref_data.calendar_days WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}' AND calendar_code='{calendar}';")
        query(f"""CREATE SEQUENCE public.synthetic_calendar_materializations;
          CREATE FUNCTION public.synthetic_calendar_materialization(code text,day date) RETURNS boolean
          LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$BEGIN
           IF code='{calendar}' AND day='{anchor_day}'::date THEN PERFORM nextval('public.synthetic_calendar_materializations');END IF;
           RETURN true;END$$;
          CREATE POLICY synthetic_calendar_materialization ON ref_data.calendar_days AS RESTRICTIVE
           FOR SELECT TO openetrm_runtime USING(public.synthetic_calendar_materialization(calendar_code,business_date));""")
        try:
            body={'curve':None,'definition':business,'timeZone':'UTC'}
            first=client.call('market/dates',body)
            reads=int(query('SELECT last_value FROM public.synthetic_calendar_materializations;'))
            assert first['dates'] and 1<=reads<=3, f'Immutable calendar was reloaded per candidate: {reads}'
            assert client.call('market/dates',body)['dates']==first['dates']
            later=int(query('SELECT last_value FROM public.synthetic_calendar_materializations;'))
            assert 1<=later-reads<=3, 'Calendar materialization must be local to each authorized operation'
        finally:
            query('DROP POLICY synthetic_calendar_materialization ON ref_data.calendar_days;DROP FUNCTION public.synthetic_calendar_materialization(text,date);DROP SEQUENCE public.synthetic_calendar_materializations;')
        print('PASS custom availability shares immutable calendar materialization only within its authorized operation',flush=True)
    # The materializer still prepares every upstream source sample even when a
    # lookup consumes one maturity. Batching must preserve that exact boundary.
    lookup=deepcopy(templates['ng_average']['definition'])
    values=next(node['id'] for node in lookup['nodes'] if node['kind']=='mean')
    lookup['nodes'] += [
        {'id':'targets','kind':'dates','inputs':{},'parameters':{'mode':'delivery_range','from':'2021-01-01','to':'2021-01-02',
            'sampling':'context_utc_time','corrections':'exact_context_frame_history_latest_visible'}},
        {'id':'lookup','kind':'lookup','inputs':{'values':values,'dates':'targets'},'parameters':{
            'axis':'delivery_start','method':'containing_delivery','maxGapDays':'62','leftBoundary':'reject','rightBoundary':'reject',
            'implementation':'asset_curve_lookup_v1','output':'daily_delivery_v1'}}]
    next(node for node in lookup['nodes'] if node['kind']=='output')['inputs']={'values':'lookup'}
    lookup_dates=client.call('market/dates',{'curve':None,'definition':lookup,'timeZone':'UTC'})
    assert '2020-12-18' in {day['date'] for day in lookup_dates['dates']}
    assert '2020-12-11' not in {day['date'] for day in lookup_dates['dates']}
    assert client.call('market/dates',{'curve':None,'definition':lookup,'timeZone':'UTC','maximumDates':1})['dates']==lookup_dates['dates'][-1:]
    last=next(frame for frame in boot['frames'] if frame['at'].startswith('2020-12-18T16:00:00'))
    assert client.call('custom-curves/preview',{'snapshotId':last['id'],'definition':lookup})['rows']
    earlier=next(frame for frame in boot['frames'] if frame['at'].startswith('2020-12-11T16:00:00'))
    assert client.call('custom-curves/preview',{'snapshotId':earlier['id'],'definition':lookup},expected=422)['code']=='curve_coverage'
    frozen=deepcopy(templates['ng_prompt']['definition'])
    next(node for node in frozen['nodes'] if node['kind']=='source')['parameters']['curve']='ng_fixed_location'
    frozen_dates=client.call('market/dates',{'curve':None,'definition':frozen,'timeZone':'UTC'})
    assert '2020-12-18' in {row['date'] for row in frozen_dates['dates']}
    assert '2020-12-17' not in {row['date'] for row in frozen_dates['dates']}
    incomplete_window=deepcopy(templates['ng_average']['definition'])
    next(node for node in incomplete_window['nodes'] if node['kind']=='dates')['parameters']['count']='100'
    no_dates=client.call('market/dates',{'curve':None,'definition':incomplete_window,'timeZone':'UTC'})
    assert no_dates['dates']==[] and no_dates['minDate'] is None and no_dates['maxDate'] is None
    invalid=deepcopy(templates['ng_average']['definition']);invalid['nodes'][-1]['inputs']={}
    client.call('market/dates',{'curve':None,'definition':invalid,'timeZone':'UTC'},expected=422)
    client.call('market/dates',{'curve':'ng_monthly','definition':templates['ng_prompt']['definition'],'timeZone':'UTC'},expected=422)
    # A retained knowledge view can only expose frames on its exact effective day.
    captured=client.call('market/context',{'effectiveOn':'2020-12-18','knownAt':None,'mode':'current','selection':'latest_correction'})
    historical=client.call('market/dates',{'curve':'ng_monthly','timeZone':'UTC'})
    assert historical['context']['contextId']==captured['context']['contextId']
    assert {row['date'] for row in historical['dates']}=={'2020-12-18'}
    assert all(row['businessDateUTC']=='2020-12-18' for day in historical['dates'] for row in day['frames'])
    if query and manifest:
        scope=manifest['referenceCatalogId']
        deleted=query(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{scope}' RETURNING tenant_id,workspace_id,access_mode;")
        try:client.call('market/dates',{'curve':'ng_monthly','timeZone':'UTC'},expected=403)
        finally:
            tenant,workspace,access=deleted.split('|')
            query(f"INSERT INTO security.reference_catalog_scopes VALUES('{scope}','{tenant}','{workspace}','{access}');")
    client.call('market/current',{})
    client.call('profile',{'profile':'editor'})
    frame=next(row for row in boot['frames'] if row['at'].startswith('2020-12-18T16:00:00'))
    observations=client.call('workspace')['observations']
    chosen=next(row for row in observations if row['id'] in frame['observationIds'] and row['role']=='daily_settlement')
    changed=client.call('corrections',{'snapshotId':frame['id'],'observationId':chosen['id'],'value':chosen['value']})['frame']
    duplicate=client.call('market/dates',{'curve':'ng_monthly','timeZone':'UTC'})
    closes=next(row for row in duplicate['dates'] if row['date']=='2020-12-18')['frames']
    assert {frame['id'],changed['id']}<={row['id'] for row in closes}
    latest=client.call('market/dates',{'curve':'ng_monthly','timeZone':'UTC','maximumDates':1})
    assert latest['dates']==duplicate['dates'][-1:], 'The latest-date read must retain every tied close.'
    if query and manifest:
        # An observation outside every offered exact snapshot cannot affect a
        # built-in calendar. Custom history intentionally has a broader read.
        offered=client.bootstrap()['frames']
        pins={identifier for saved in offered for identifier in saved['observationIds']}
        selected=','.join("'"+str(identifier)+"'::uuid" for identifier in pins)
        unused=query(f"SELECT id FROM market_data.observations WHERE catalog_id='{manifest['catalogId']}' AND definition_revision_id='{manifest['revisionId']}' AND series_id IN(SELECT series_id FROM market_data.rate_curve_points WHERE catalog_id='{manifest['catalogId']}' AND revision_id='{manifest['revisionId']}') AND NOT(id=ANY(ARRAY[{selected}])) ORDER BY id LIMIT 1;")
        if not unused:
            # Append a valid same-value correction without adding it to a session
            # frame. Existing immutable observations and prices are unchanged.
            unused=str(uuid4())
            query(f"""INSERT INTO market_data.observations
              (catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,
               source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value,corrects_id)
              SELECT catalog_id,'{unused}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,
               source_event_id,'{uuid4()}',role,publication_state,observed_at,clock_timestamp(),value,id
              FROM market_data.observations o WHERE catalog_id='{manifest['catalogId']}' AND definition_revision_id='{manifest['revisionId']}'
              AND series_id IN(SELECT series_id FROM market_data.rate_curve_points WHERE catalog_id='{manifest['catalogId']}' AND revision_id='{manifest['revisionId']}')
              AND NOT EXISTS(SELECT 1 FROM market_data.observations child WHERE child.catalog_id=o.catalog_id AND child.corrects_id=o.id)
              ORDER BY id LIMIT 1;""")
        requests=[{'curve':code,'timeZone':'UTC'} for code in (None,'ng_monthly','interest_rates')]
        before=[client.call('market/dates',body) for body in requests]
        query(f"""CREATE FUNCTION public.synthetic_unused_calendar_observation(id uuid) RETURNS boolean
          LANGUAGE plpgsql VOLATILE AS $$BEGIN
          IF id='{unused}'::uuid THEN RAISE EXCEPTION 'Synthetic unused calendar observation read' USING ERRCODE='42501';END IF;
          RETURN true;END$$;
          CREATE POLICY synthetic_unused_calendar_observation ON market_data.observations AS RESTRICTIVE
          FOR SELECT TO openetrm_runtime USING(public.synthetic_unused_calendar_observation(id));""")
        try:
            for body,expected in zip(requests,before):
                assert client.call('market/dates',body)==expected, 'Built-in availability must use only all offered frame pins.'
            # PostgreSQL may inspect broader RLS candidates even when returned
            # observations are window-scoped. A native policy error still fails.
            client.call('market/dates',{'curve':None,'definition':templates['ng_average']['definition'],'timeZone':'UTC'},expected=403)
        finally:
            query('DROP POLICY synthetic_unused_calendar_observation ON market_data.observations;DROP FUNCTION public.synthetic_unused_calendar_observation(uuid);')
        # A required pin for any offered frame cannot be silently substituted,
        # including when the selected curve would not itself consume that fact.
        anchor=query(f"SELECT id FROM market_data.observations WHERE catalog_id='{manifest['catalogId']}' AND id=ANY(ARRAY[{','.join(repr(identifier)+'::uuid' for identifier in frame['observationIds'])}]) AND series_id IN(SELECT series_id FROM market_data.rate_curve_points WHERE catalog_id='{manifest['catalogId']}' AND revision_id='{manifest['revisionId']}') ORDER BY id LIMIT 1;")
        affected={saved['id'] for saved in offered if anchor in saved['observationIds']}
        prompt_body={'curve':None,'definition':templates['ng_prompt']['definition'],'timeZone':'UTC'}
        prompt_before=client.call('market/dates',prompt_body)
        query(f"CREATE POLICY synthetic_hidden_calendar_observation ON market_data.observations AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(id<>'{anchor}'::uuid);")
        try:
            for body,expected in [*zip(requests,before),(prompt_body,prompt_before)]:
                actual=client.call('market/dates',body)
                expected_ids={saved['id'] for day in expected['dates'] for saved in day['frames']}-affected
                assert {saved['id'] for day in actual['dates'] for saved in day['frames']}==expected_ids
        finally:
            query('DROP POLICY synthetic_hidden_calendar_observation ON market_data.observations;')
        assert all(client.call('market/dates',body)==expected for body,expected in zip(requests,before))
        print('PASS built-in calendar reads exact pins across all frames, rates and corrections; hidden pins never fall back and recipes read only their declared sample windows',flush=True)
    print('PASS market calendar availability: exact curve dependencies, complete recipe windows, user-zone days, explicit duplicate snapshots, certified context and current reference authority',flush=True)
