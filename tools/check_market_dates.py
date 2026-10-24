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
    client.call('market/dates',{'curve':'missing_curve','timeZone':'UTC'},expected=422)
    client.call('market/dates',{'curve':'ng_monthly','timeZone':'Not/AZone'},expected=422)
    templates={row['code']:row for row in client.call('custom-curves')['templates']}
    client.call('market/dates',{'definition':templates['ng_prompt']['definition'],'timeZone':'UTC'},expected=400)
    average_request={'curve':None,'definition':templates['ng_average']['definition'],'timeZone':'UTC'}
    if query and manifest:
        # Count reads of one retained fact under a bound financial context.
        # Qualifying many dates must not reload the complete history per date.
        anchor=query(f"SELECT id FROM market_data.observations WHERE catalog_id='{manifest['catalogId']}' AND definition_revision_id='{manifest['revisionId']}' ORDER BY observed_at,id LIMIT 1;")
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
            assert reads=='1', f'Custom date availability reloaded bound observation history {reads} times'
            coordinates=query(f"SELECT count(DISTINCT known_at),count(DISTINCT effective_on) FROM market_data.temporal_contexts WHERE catalog_id='{manifest['catalogId']}' AND known_at>'{before_cutoff}'::timestamptz;").split('|')
            assert coordinates[0]=='1' and int(coordinates[1])>1, 'Working date availability must resolve each effective date at one certified knowledge cutoff'
            assert client.call('custom-curves')['context'] is None, 'Availability changed the working header context'
        finally:
            query('DROP POLICY synthetic_calendar_history_read ON market_data.observations;DROP FUNCTION public.synthetic_calendar_history_read(uuid);DROP SEQUENCE public.synthetic_calendar_history_reads;')
    else:
        average=client.call('market/dates',average_request)
    assert '2020-11-18' not in {row['date'] for row in average['dates']}
    assert '2020-12-18' in {row['date'] for row in average['dates']}
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
    if query and manifest:
        # An observation outside every offered exact snapshot cannot affect a
        # built-in calendar. Custom history intentionally has a broader read.
        offered=client.bootstrap()['frames']
        pins={identifier for saved in offered for identifier in saved['observationIds']}
        selected=','.join("'"+str(identifier)+"'::uuid" for identifier in pins)
        unused=query(f"SELECT id FROM market_data.observations WHERE catalog_id='{manifest['catalogId']}' AND definition_revision_id='{manifest['revisionId']}' AND NOT(id=ANY(ARRAY[{selected}])) ORDER BY id LIMIT 1;")
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
            client.call('market/dates',{'curve':None,'definition':templates['ng_average']['definition'],'timeZone':'UTC'},expected=403)
        finally:
            query('DROP POLICY synthetic_unused_calendar_observation ON market_data.observations;DROP FUNCTION public.synthetic_unused_calendar_observation(uuid);')
        # A required pin for any offered frame cannot be silently substituted,
        # including when the selected curve would not itself consume that fact.
        anchor=next(iter(frame['observationIds']))
        affected={saved['id'] for saved in offered if anchor in saved['observationIds']}
        query(f"CREATE POLICY synthetic_hidden_calendar_observation ON market_data.observations AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(id<>'{anchor}'::uuid);")
        try:
            for body,expected in zip(requests,before):
                actual=client.call('market/dates',body)
                expected_ids={saved['id'] for day in expected['dates'] for saved in day['frames']}-affected
                assert {saved['id'] for day in actual['dates'] for saved in day['frames']}==expected_ids
        finally:
            query('DROP POLICY synthetic_hidden_calendar_observation ON market_data.observations;')
        assert all(client.call('market/dates',body)==expected for body,expected in zip(requests,before))
        print('PASS built-in calendar reads exact pins across all frames, rates and corrections; hidden pins never fall back and custom history retains its broader read',flush=True)
    print('PASS market calendar availability: exact curve dependencies, complete recipe windows, user-zone days, explicit duplicate snapshots, certified context and current reference authority',flush=True)
