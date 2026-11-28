"""Native exact-calendar retention and original-request admission checks."""
import copy
import hashlib
import json
from uuid import uuid4
from authority_wait import lock_holder
from check_reference_editing import main


def calendar_terms(reference, revision, code='test_utc_every_day', publication=None):
    # Independently specified published fixture, not a database-generated oracle.
    calendar = dict(contract='captured_calendar_definition_v1', Exceptions=[], From='2020-01-01',
                    Key=['reference', ['reference_revision', reference, revision], code],
                    To='2100-01-01', WeekendMask=0)
    canonical = json.dumps(calendar, separators=(',', ':'))
    binding = dict(contract='side_calendar_binding_v1', Applicable=True, Calendar=calendar,
                   DefinitionDigest=hashlib.sha256(canonical.encode()).hexdigest(), Publication=publication)
    absent = dict(contract='side_calendar_binding_v1', Applicable=False, Calendar=None, DefinitionDigest=None, Publication=None)
    policy = dict(contract='side_calendar_policy_v1', Delivery=binding, Observation=absent,
                  Payment=binding, PaymentTiming='utc_payment_day_v1')
    key = dict(revision=dict(catalogId=reference, revisionId=revision), code=code)
    requested = dict(implementation='side_calendar_policy_v1', delivery=dict(applicable=True, calendar=key),
                     observation=dict(applicable=False, calendar=None), payment=dict(applicable=True, calendar=key))
    return policy, requested


def run_checks(query, manifest):
    from check_clock_captures import run_checks as check_clocks
    check_clocks(query, manifest)
    catalog = manifest['catalogId']; reference = manifest['referenceCatalogId']; revision = manifest['referenceRevisionId']
    assert query("SELECT to_regprocedure('security.list_side_calendars()') IS NOT NULL;") == 't', \
        'Authoring must discover exact authorized published calendars independently of pricing revisions.'
    assert query("SELECT to_regclass('trading.side_calendar_bindings') IS NOT NULL;") == 't', \
        'Every applicable Side purpose needs immutable exact-calendar foreign keys.'
    assert query("SELECT to_regprocedure('security.read_side_calendar(uuid,uuid,text)') IS NOT NULL;") == 't', \
        'Side calendars need a publication-qualified exact reader independent of pricing-reference visibility.'
    assert query("SELECT count(*) FROM pg_attribute WHERE attrelid='market_data.tou_product_versions'::regclass AND attname IN('calendar_catalog_id','calendar_revision_id','calendar_code','holiday_treatment');") == '4', \
        'Published TOUs must own exact calendar references and an explicit holiday convention.'
    def literal(value): return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"
    def runtime(statement):
        token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{statement} COMMIT;")
    no_contexts="SELECT to_regclass('market_data.temporal_contexts') IS NULL;"
    assert query(no_contexts)=='t'
    before_clock=query(f"SELECT coalesce(coverage_from::text,'NULL'),coalesce(certified_through::text,'NULL') FROM market_data.knowledge_clocks WHERE catalog_id='{catalog}';")
    assert query(f"SELECT count(*) FROM market_data.market_knowledge_receipts WHERE catalog_id='{catalog}';")=='0', 'The first exact read must certify a cold publication.'
    first_read=json.loads(runtime(f"SELECT security.read_side_calendar('{reference}','{revision}','test_utc_every_day');"))
    assert first_read['reference']['revisionId']==revision
    assert query(no_contexts)=='t'
    assert query(f"SELECT coalesce(coverage_from::text,'NULL'),coalesce(certified_through::text,'NULL') FROM market_data.knowledge_clocks WHERE catalog_id='{catalog}';")==before_clock
    assert query("SELECT to_regprocedure('security.side_payment_date(jsonb,date,date,jsonb)') IS NOT NULL;") == 't', \
        'Native payment dates must consume the explicit Side payment binding.'
    assert query("SELECT to_regprocedure('security.retain_ppa_terms(uuid,jsonb,integer,timestamptz,uuid)') IS NOT NULL;") == 't', \
        'PPA retention must bind every member to its verified staged canonical Side.'
    captured = json.loads(runtime(f"SELECT security.read_side_calendar('{reference}','{revision}','test_utc_every_day');"))
    receipt_state=query(f"SELECT market_certified_head,(SELECT count(*) FROM market_data.market_knowledge_receipts WHERE catalog_id='{catalog}') FROM market_data.knowledge_clocks WHERE catalog_id='{catalog}';")
    assert json.loads(runtime(f"SELECT security.read_side_calendar('{reference}','{revision}','test_utc_every_day');"))==captured
    assert query(f"SELECT market_certified_head,(SELECT count(*) FROM market_data.market_knowledge_receipts WHERE catalog_id='{catalog}') FROM market_data.knowledge_clocks WHERE catalog_id='{catalog}';")==receipt_state
    # Verifying retained source proof cannot require certifying a later event.
    query("""CREATE FUNCTION security.synthetic_calendar_proof(catalog uuid,revision uuid,proof jsonb) RETURNS jsonb
      LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS
      'SELECT security.resolve_side_calendar(catalog,revision,''test_utc_every_day'',proof)';
      ALTER FUNCTION security.synthetic_calendar_proof(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
      REVOKE ALL ON FUNCTION security.synthetic_calendar_proof(uuid,uuid,jsonb) FROM PUBLIC;
      GRANT EXECUTE ON FUNCTION security.synthetic_calendar_proof(uuid,uuid,jsonb) TO preview_runtime;""")
    token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    new_revision=str(uuid4())
    proof=query(f"""BEGIN;
      INSERT INTO market_data.revisions VALUES('{catalog}','{new_revision}','{reference}','{revision}','draft',0);
      INSERT INTO market_data.revision_periods VALUES('{catalog}','{new_revision}','2045-01-01','2046-01-01');
      UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{new_revision}');
      UPDATE market_data.knowledge_clocks SET timeline_id=timeline_id+1 WHERE catalog_id='{catalog}';
      SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');
      SELECT security.synthetic_calendar_proof('{reference}','{revision}',{literal(captured['publication'])});ROLLBACK;""")
    assert json.loads(proof)==captured
    query('DROP FUNCTION security.synthetic_calendar_proof(uuid,uuid,jsonb);')
    policy, requested = calendar_terms(reference, revision, publication=captured['publication'])
    def begin(captured, selected, request=None, envelope=None):
        identity = dict(contract='synthetic_calendar_admission_v1', CalendarPolicy=captured)
        header = dict(family='fixed', definitionRevisionId=manifest['revisionId'], referenceCatalogId=reference,
                      referenceRevisionId=revision, economicIdentity=json.dumps(identity, separators=(',', ':')),
                      pricingBindingIdentity='', schemaVersion='side_schedule_blocks_v2', generatorVersion='whole_term_side_blocks_v3',
                      manifest='c'*64, rowCount=1, edgeCount=0, bindingCount=0, rootCount=1, portCount=0, gridCount=0)
        command = envelope if envelope is not None else dict(fixed=dict(calendarPolicy=selected))
        return f"SELECT security.begin_side_schedule('{catalog}','{request or uuid4()}',0,{literal(command)},'BookTrade',NULL,NULL,{literal(header)});"
    def rejected(statement, code):
        runtime(f"DO $$BEGIN BEGIN {statement.replace('SELECT ', 'PERFORM ')} RAISE EXCEPTION 'accepted invalid calendar';"
                f"EXCEPTION WHEN OTHERS THEN IF position('{code}' in SQLERRM)=0 THEN RAISE;END IF;END;END$$;")
    def cold_source(label):
        source_catalog, source_revision, selected_revision = (str(uuid4()) for _ in range(3))
        query(f"""BEGIN;
          INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{reference}','{selected_revision}','{label}');
          INSERT INTO ref_data.clock_rules VALUES('{reference}','{selected_revision}','synthetic_utc','fixed_offset',0,NULL,NULL);
          INSERT INTO ref_data.calendars VALUES('{reference}','{selected_revision}','synthetic_days','synthetic_utc',0,'2020-01-01','2100-01-01');
          UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{reference}','{selected_revision}');
          INSERT INTO market_data.catalogs VALUES('{source_catalog}','{label}');
          INSERT INTO security.market_catalog_scopes VALUES('{source_catalog}','{manifest['tenantId']}','{manifest['workspaceId']}');
          INSERT INTO market_data.revisions VALUES('{source_catalog}','{source_revision}','{reference}','{selected_revision}','draft',0);
          INSERT INTO market_data.revision_periods VALUES('{source_catalog}','{source_revision}','2035-01-01','2036-01-01');
          UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{source_catalog}','{source_revision}');COMMIT;""")
        assert query(f"SELECT count(*) FROM market_data.market_knowledge_receipts WHERE catalog_id='{source_catalog}';")=='0'
        return source_catalog,selected_revision
    cold_catalog,cold_revision=cold_source('synthetic_cold_calendar')
    pricing_context=runtime(f"SELECT security.resolve_operation_context('{catalog}','2021-01-01',NULL);")
    frozen=f"SELECT security.use_market_context('{catalog}','{pricing_context}');"
    cold_read=json.loads(runtime(frozen+f"SELECT security.read_side_calendar('{reference}','{cold_revision}','synthetic_days');"))
    assert cold_read['publication']['CatalogId']==cold_catalog, 'An independent authorized source committed before the cutoff must work on its first read.'
    late_catalog,late_revision=cold_source('synthetic_late_calendar')
    rejected(frozen+f"SELECT security.read_side_calendar('{reference}','{late_revision}','synthetic_days');",'calendar_reference')
    assert query(no_contexts)=='t'
    # Admission can wait behind publication. Current authority must still exist
    # after that wait, and failed certification must not leave a receipt.
    from threading import Thread
    import time
    expiry_catalog,expiry_revision=cold_source('synthetic_calendar_expiry')
    grants=json.loads(query("SELECT json_agg(json_build_object('id',g.id,'until',g.granted_to)) FROM security.role_grants g JOIN security.identity_bindings b ON b.principal_id=g.principal_id WHERE b.issuer='https://preview.invalid' AND b.subject='trader';"))
    ids=','.join("'"+g['id']+"'" for g in grants)
    errors=[]
    def blocking_query(statement):
        try:query(statement)
        except RuntimeError as error:errors.append(str(error))
    holder=Thread(target=blocking_query,args=("SET application_name='synthetic_calendar_holder';BEGIN;SELECT pg_advisory_xact_lock(730849102);SELECT pg_sleep(4);COMMIT;",))
    holder.start()
    deadline=time.monotonic()+3
    while query("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='synthetic_calendar_holder' AND wait_event='PgSleep');")!='t':
        if time.monotonic()>deadline:raise AssertionError('The synthetic publication gate was not acquired')
        time.sleep(.02)
    reader=None
    try:
        query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '2 seconds' WHERE id IN({ids});")
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        reader=Thread(target=blocking_query,args=(f"SET application_name='synthetic_calendar_reader';SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT security.read_side_calendar('{reference}','{expiry_revision}','synthetic_days');COMMIT;",))
        reader.start();deadline=time.monotonic()+2
        while query("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='synthetic_calendar_reader' AND wait_event='advisory');")!='t':
            if time.monotonic()>deadline:raise AssertionError('The calendar reader did not wait at publication admission')
            time.sleep(.02)
        reader.join(6);holder.join(6)
        assert not reader.is_alive() and not holder.is_alive()
        assert len(errors)==1 and 'Temporal authority expired during admission' in errors[0],errors
        assert query(f"SELECT count(*) FROM market_data.market_knowledge_receipts WHERE catalog_id='{expiry_catalog}';")=='0'
    finally:
        if reader is not None:reader.join(8)
        holder.join(8)
        query(';'.join(f"UPDATE security.role_grants SET granted_to='{g['until']}' WHERE id='{g['id']}'" for g in grants)+';')
    early_context = runtime(f"SELECT security.resolve_operation_context('{catalog}','2021-01-01',NULL);")
    independent_revision, independent_source = str(uuid4()), str(uuid4())
    query(f"""
        BEGIN;
        INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{reference}','{independent_revision}','synthetic_independent_calendar');
        INSERT INTO ref_data.clock_rules(catalog_id,revision_id,code,kind,fixed_offset_seconds)
         VALUES('{reference}','{independent_revision}','synthetic_utc','fixed_offset',0);
        INSERT INTO ref_data.calendars VALUES('{reference}','{independent_revision}','test_utc_every_day','synthetic_utc',0,'2020-01-01','2100-01-01');
        INSERT INTO ref_data.calendars VALUES('{reference}','{independent_revision}','synthetic_six_day','synthetic_utc',48,'2026-12-01','2027-02-02');
        INSERT INTO ref_data.calendar_days VALUES
         ('{reference}','{independent_revision}','synthetic_six_day','2027-01-02',true,'Synthetic reopening'),
         ('{reference}','{independent_revision}','synthetic_six_day','2027-01-03',false,'Synthetic closure'),
         ('{reference}','{independent_revision}','synthetic_six_day','2027-01-31',false,'Synthetic month-end closure');
        UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{reference}','{independent_revision}');
        COMMIT;
    """)
    def listed(prefix=''):
        return json.loads(runtime(prefix+"SELECT coalesce(jsonb_agg(value),'[]'::jsonb) FROM security.list_side_calendars() value;"))
    assert not any(row['reference']['revisionId']==independent_revision for row in listed()), 'A sealed unpublished revision is not a booking choice.'
    read_independent = f"SELECT security.read_side_calendar('{reference}','{independent_revision}','test_utc_every_day');"
    rejected(read_independent, 'calendar_reference')
    query(f"""
        BEGIN;
        INSERT INTO market_data.revisions VALUES('{catalog}','{independent_source}','{reference}','{independent_revision}','draft',0);
        INSERT INTO market_data.revision_periods VALUES('{catalog}','{independent_source}','2035-01-01','2036-01-01');
        UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{independent_source}');
        COMMIT;
    """)
    later_context = runtime(f"SELECT security.resolve_operation_context('{catalog}','2021-01-01',NULL);")
    later = f"SELECT security.use_market_context('{catalog}','{later_context}');"
    assert runtime(later+f"SELECT security.reference_version_visible('{reference}','{independent_revision}');") == 'f', \
        'The exact-calendar reader must not broaden general pricing-reference visibility.'
    choices=listed(later)
    six_day=next(row for row in choices if row['reference']['revisionId']==independent_revision and row['reference']['code']=='synthetic_six_day')
    assert six_day['weekendMask']==48 and six_day['coverageFrom']=='2026-12-01' and len(six_day['exceptions'])==3
    assert not any(row['reference']['revisionId']==independent_revision for row in listed(f"SELECT security.use_market_context('{catalog}','{early_context}');")), 'Discovery must respect the original knowledge cutoff.'
    assert query("SET SESSION AUTHORIZATION preview_runtime;SELECT count(*) FROM security.list_side_calendars();")=='0', 'An unbound caller cannot enumerate calendars.'
    independent = json.loads(runtime(later+read_independent))
    assert independent['reference']['revisionId'] == independent_revision
    assert independent['publication']['EventOrder'] > captured['publication']['EventOrder']
    # This test-only wrapper exercises private native admission under exactly the
    # runtime caller's authority. It exists only in this disposable database.
    query("""CREATE FUNCTION security.synthetic_payment_date(policy jsonb,starts date,ends date,terms jsonb) RETURNS date
      LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS
      'SELECT security.side_payment_date(policy,starts,ends,terms)';
      ALTER FUNCTION security.synthetic_payment_date(jsonb,date,date,jsonb) OWNER TO openetrm_security_owner;
      REVOKE ALL ON FUNCTION security.synthetic_payment_date(jsonb,date,date,jsonb) FROM PUBLIC;
      GRANT EXECUTE ON FUNCTION security.synthetic_payment_date(jsonb,date,date,jsonb) TO preview_runtime;""")
    _, payment_policy=calendar_terms(reference,independent_revision,'synthetic_six_day')
    payment_rule=dict(frequency='monthly',rule='days_after_period_end',days=2,dayOfMonth=None,dayBasis='business_days',
                      shortMonth=None,adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1')
    def native_date(rule, starts='2026-12-01', ends='2027-01-01'):
        return runtime(later+f"SELECT security.synthetic_payment_date({literal(payment_policy)},'{starts}','{ends}',{literal(rule)});")
    assert native_date(payment_rule)=='2027-01-04', 'A reopened Saturday counts and a closed Sunday does not.'
    day_rule={**payment_rule,'rule':'day_of_following_month','days':None,'dayOfMonth':3,'dayBasis':None,'shortMonth':'reject'}
    assert native_date({**day_rule,'adjustment':'following'})=='2027-01-04'
    assert native_date({**day_rule,'adjustment':'preceding'})=='2027-01-02'
    assert native_date({**day_rule,'dayOfMonth':31,'adjustment':'modified_following'})=='2027-01-28'
    rejected(later+f"SELECT security.synthetic_payment_date({literal(payment_policy)},'2027-01-01','2027-02-01',{literal(payment_rule)});",'calendar_coverage')
    rejected(later+f"SELECT security.synthetic_payment_date({literal(payment_policy)},'2026-12-01','2027-01-01',{literal({**payment_rule,'calendar':'synthetic_six_day'})});",'side_calendar_conflict')
    query(f"INSERT INTO trading.publications VALUES('{catalog}',1,pg_current_xact_id());")
    query("""CREATE FUNCTION security.synthetic_fixed_period(catalog uuid,rc uuid,rr uuid,body jsonb,valuation timestamptz) RETURNS jsonb
      LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $fn$
      DECLARE prepared jsonb;BEGIN
       prepared=security.prepare_fixed_swap_period(catalog,rc,rr,'test_gas_outright',body,valuation,1,'2020-01-01T00:00:00Z'::timestamptz);
       -- The whole Side, rather than the shared period parser, owns completion.
       PERFORM security.dated_side_completion(jsonb_build_object('contract','FixedSide_v5','CalendarPolicy',jsonb_build_object('PaymentTiming','utc_payment_day_v1')),
        jsonb_build_object('DeliveryTo',body->>'deliveryTo','PaymentAt',prepared->>'paymentAt'));
       RETURN prepared;END $fn$;
      ALTER FUNCTION security.synthetic_fixed_period(uuid,uuid,uuid,jsonb,timestamptz) OWNER TO openetrm_security_owner;
      REVOKE ALL ON FUNCTION security.synthetic_fixed_period(uuid,uuid,uuid,jsonb,timestamptz) FROM PUBLIC;
      GRANT EXECUTE ON FUNCTION security.synthetic_fixed_period(uuid,uuid,uuid,jsonb,timestamptz) TO preview_runtime;""")
    period=dict(price='3',currency='usd',deliveryFrom='2026-12-01',deliveryTo='2027-01-01',paymentAt=None,
                paymentSchedule={**payment_rule,'days':1,'dayBasis':'calendar_days'},calendarPolicy=payment_policy)
    def native_period(body):
        return f"SELECT security.synthetic_fixed_period('{catalog}','{reference}','{revision}',{literal(body)},'2027-01-01T23:00:00Z');"
    assert json.loads(runtime(later+native_period(period)))['paymentAt'].startswith('2027-01-01T00:00:00'), 'Same-day unpaid cash survives native admission after midnight.'
    earlier=copy.deepcopy(period);earlier['paymentSchedule']['adjustment']='preceding'
    # The runtime valuation predates the roll result, so only completion admission
    # can reject this contract (Friday is a weekend in this exact calendar).
    rejected(later+native_period(earlier).replace('2027-01-01T23:00:00Z','2026-12-01T00:00:00Z'),'payment_before_completion')
    query('DROP FUNCTION security.synthetic_fixed_period(uuid,uuid,uuid,jsonb,timestamptz);')
    query('DROP FUNCTION security.synthetic_payment_date(jsonb,date,date,jsonb);')

    runtime(f"SELECT security.use_market_context('{catalog}','{early_context}');" +
            f"DO $$BEGIN BEGIN {read_independent.replace('SELECT ', 'PERFORM ', 1)} RAISE EXCEPTION 'future proof leaked';"
            "EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'calendar_reference' THEN RAISE;END IF;END;END$$;")
    rejected(begin(None, requested), 'side_calendar_policy')
    rejected(begin(policy, None), 'side_calendar_policy')
    changed = copy.deepcopy(policy); changed['Payment']['Calendar']['WeekendMask'] = 48
    rejected(begin(changed, requested), 'calendar_definition_mismatch')
    changed = copy.deepcopy(policy); changed['Payment']['DefinitionDigest'] = '0'*64
    rejected(begin(changed, requested), 'calendar_definition_mismatch')
    changed = copy.deepcopy(policy); changed['PaymentTiming'] = None
    rejected(begin(changed, requested), 'side_calendar_policy')
    inactive = {**policy, **{purpose:copy.deepcopy(policy['Observation']) for purpose in ('Delivery','Payment')}, 'PaymentTiming':None}
    inactive_request = dict(implementation='side_calendar_policy_v1', **{purpose:dict(applicable=False,calendar=None) for purpose in ('delivery','observation','payment')})
    rejected(begin(inactive, inactive_request), 'side_calendar_applicability')
    changed = copy.deepcopy(requested); changed['payment']['calendar']['code'] = 'test_business'
    rejected(begin(policy, changed), 'side_calendar_conflict')
    unavailable, unavailable_request = calendar_terms(reference, str(uuid4()))
    rejected(begin(unavailable, unavailable_request), 'calendar_reference')
    command = begin(policy, requested, str(uuid4()))
    schedule = runtime(command)
    assert runtime(command) == schedule, 'A retry must reuse the same retained schedule.'
    scheduled_envelope=dict(schema='scheduled_financial_swap_booking_v1',body=dict(scheduled=dict(first=dict(calendarPolicy=requested))),
                            valuation='2021-01-01T00:00:00Z',booking={})
    assert runtime(begin(policy,requested,envelope=scheduled_envelope)), 'A scheduled command retains its whole-Side policy through its original typed envelope.'

    assert runtime("SET LOCAL DateStyle='SQL, DMY';" + command) == schedule, 'Calendar identity must not depend on the caller date format.'
    side = query(f"SELECT side_id FROM trading.side_schedules WHERE id='{schedule}';")
    assert query(f"SELECT count(*) FROM trading.side_calendar_bindings WHERE side_id='{side}';") == '3'
    assert query(f"SELECT count(*) FROM trading.side_calendar_bindings WHERE side_id='{side}' AND applicable;") == '2'
    # Two purposes share one exact publication. Its visibility work must not
    # grow with unrelated publications or repeat the same authority lookup.
    query("""CREATE FUNCTION security.synthetic_calendar_visibility(catalog uuid,schedule uuid) RETURNS boolean
      LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS
      'SELECT security.side_calendar_proofs_visible(catalog,schedule,NULL)';
      ALTER FUNCTION security.synthetic_calendar_visibility(uuid,uuid) OWNER TO openetrm_security_owner;
      REVOKE ALL ON FUNCTION security.synthetic_calendar_visibility(uuid,uuid) FROM PUBLIC;
      GRANT EXECUTE ON FUNCTION security.synthetic_calendar_visibility(uuid,uuid) TO preview_runtime;""")
    try:
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        work=query(f"""SET track_functions='all';SET SESSION AUTHORIZATION preview_runtime;BEGIN;
          SELECT security.use_context('{token}');
          SELECT security.synthetic_calendar_visibility('{catalog}','{schedule}');
          SELECT jsonb_object_agg(funcname,calls) FROM pg_stat_xact_user_functions
           WHERE schemaname='security' AND funcname IN('side_calendar_cutoff','reference_access','market_access');ROLLBACK;""").splitlines()
        assert work[-2]=='t',work
        counts=json.loads(work[-1])
        assert counts=={'side_calendar_cutoff':1,'reference_access':1,'market_access':1},counts
        errors=[];results=[]
        # The lock is kept until the reader has waited for it with its grants and they have ended.
        holder=Thread(target=blocking_query,args=(lock_holder('synthetic_proof_holder','LOCK TABLE market_data.market_knowledge_receipts IN ACCESS EXCLUSIVE MODE',
            f"(SELECT bool_and(granted_to<=clock_timestamp()) FROM security.role_grants WHERE id IN({ids}))"),))
        holder.start();deadline=time.monotonic()+3
        while query("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='synthetic_proof_holder' AND wait_event='PgSleep');")!='t':
            if time.monotonic()>deadline:raise AssertionError('The synthetic proof relation lock was not acquired')
            time.sleep(.02)
        reader=None
        def read_proof():
            try:
                results.append(query(f"SET application_name='synthetic_proof_reader';SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT security.synthetic_calendar_visibility('{catalog}','{schedule}');COMMIT;"))
            except RuntimeError as error:errors.append(str(error))
        try:
            query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '2 seconds' WHERE id IN({ids});")
            reader=Thread(target=read_proof);reader.start();deadline=time.monotonic()+2
            while query("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='synthetic_proof_reader' AND wait_event='relation');")!='t':
                if time.monotonic()>deadline:raise AssertionError('The calendar proof reader did not wait on the receipt relation')
                time.sleep(.02)
            reader.join(6);holder.join(6)
            assert not reader.is_alive() and not holder.is_alive()
            assert not errors and results==['f'],(errors,results)
        finally:
            if reader is not None:reader.join(8)
            holder.join(8)
            query(';'.join(f"UPDATE security.role_grants SET granted_to='{g['until']}' WHERE id='{g['id']}'" for g in grants)+';')
        assert runtime(f"SELECT security.synthetic_calendar_visibility('{catalog}','{schedule}');")=='t'
    finally:query('DROP FUNCTION security.synthetic_calendar_visibility(uuid,uuid);')
    print('PASS calendar proof visibility resolves one cutoff and checks each distinct retained publication once and refuses authority expired during a proof wait',flush=True)
    assert query("SELECT count(*) FROM pg_constraint WHERE conrelid='trading.side_calendar_bindings'::regclass AND contype='f' AND confrelid='ref_data.calendars'::regclass;") == '1'
    assert runtime(f"SELECT count(*) FROM trading.side_calendar_bindings WHERE side_id='{side}';") == '0', 'Staging is not published business data.'
    query(f"DO $$BEGIN BEGIN UPDATE trading.side_calendar_bindings SET applicable=false WHERE side_id='{side}';"
          "RAISE EXCEPTION 'accepted mutation';EXCEPTION WHEN SQLSTATE '55000' THEN NULL;END;END$$;")
    assert query("SELECT count(*) FROM information_schema.role_table_grants WHERE grantee='openetrm_runtime' AND table_name='side_calendar_bindings' AND privilege_type IN('INSERT','UPDATE','DELETE');") == '0'
    uncovered = runtime(begin(policy, requested).replace('synthetic_calendar_admission_v1', 'synthetic_calendar_coverage_v1'))
    unit_code = query(f"SELECT quantity_code FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=('{reference}','{revision}','test_gas_outright');")
    row = dict(ordinal=0,key='synthetic_quantity',role='source',operation='quantity_literal',version=1,outputKind='quantity',
               unit=dict(revision=dict(catalogId=reference,revisionId=revision),code=unit_code),
               deliveryFrom='2100-01-01',deliveryTo='2100-01-02',valueAt='2100-01-03T00:00:00Z',paymentOrdinal=0,
               parameters=dict(numerator='1',denominator='1'))
    operation = query(f"SELECT request_id FROM trading.side_schedules WHERE id='{uncovered}';")
    runtime(f"SELECT security.append_side_schedule_rows('{catalog}','{operation}','{uncovered}',{literal([row])});")
    rejected(f"SELECT security.seal_side_schedule('{catalog}','{operation}','{uncovered}','{'c'*64}');", 'calendar_coverage')
    print('PASS Side calendars: exact published payloads, cold cross-catalog cutoffs, retained proofs, authority expiry during publication wait, typed immutable bindings and idempotent staging', flush=True)


if __name__ == '__main__':
    main(checks=run_checks)
