"""The explicit regional_currency_examples_v1 synthetic seed, on the current schema.

Load new exact-version source facts, then use the managed seed runner for native
catalogue/session publication, close capture and discounted swap booking. Existing
USD sources and trades are never rebound. This is a bounded 2020 example, not a
market data service, holiday calendar, FX model or regional gas product.
"""
import json
from uuid import UUID, uuid5

from rate_curve_fixture import admitted_curve, _id as rate_id
from seed_exact_rows import literal, rows_sql, hosted_write
from seed_fixture_market import load_market
from synthetic_side_calendars import side_calendar_policy

VERSION = 'regional_currency_examples_v1'
DAYS = ('2020-07-15', '2020-12-15', '2020-12-16', '2020-12-17', '2020-12-18')
PILLARS = ('2020-12-19', '2021-02-03', '2022-02-03')
CURRENCIES = (
    ('gbp', 'GBP', 'Pound Sterling', '50', '40', ('0.025', '0.030', '0.035')),
    ('eur', 'EUR', 'Euro', '60', '50', ('0.015', '0.020', '0.025')),
    ('cad', 'CAD', 'Canadian Dollar', '70', '60', ('0.035', '0.040', '0.045')),
)


def install(query, manifest, *, identity=None):
    """Append the selected source preset. Replays verify facts and reuse identities.

    query is the explicitly owned seed connection, never an application runtime
    connection. identity, when supplied, is an authenticated hosted owner session;
    it cannot manufacture an application principal or bypass its permissions.
    """
    if manifest.get('mode') not in ('local-synthetic-preview','hosted-pilot') or manifest['mode']=='hosted-pilot' and identity is None:
        raise ValueError('regional_seed_identity: hosted source loading requires its current owner session')
    cat, ref, original, primary = (str(UUID(manifest[key])) for key in
        ('catalogId', 'referenceCatalogId', 'referenceRevisionId', 'revisionId'))
    stable = lambda name: str(uuid5(UUID(cat), VERSION + ':' + name))
    revision, definition = stable('reference'), stable('definition')
    write = lambda sql: hosted_write(query, sql, cat, ref, identity)
    # PostgreSQL casts decimal text back to NUMERIC without a binary-float hop.
    read = lambda sql: json.loads(query(sql), parse_float=str)
    source = read(f"""SELECT row_to_json(x) FROM(SELECT q.*,u.unit_code,u.dimension_code,u.basis_code,
      c.location_code,p.calendar_code,l.type_code FROM market_data.curves c
      JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
      JOIN ref_data.quantity_specifications u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.quantity_code)
      JOIN ref_data.delivery_profiles p ON(p.catalog_id,p.revision_id,p.code)=(c.reference_catalog_id,c.reference_revision_id,c.profile_code)
      JOIN ref_data.locations l ON(l.catalog_id,l.revision_id,l.code)=(c.reference_catalog_id,c.reference_revision_id,c.location_code)
      WHERE(c.catalog_id,c.revision_id,c.code)=('{cat}','{primary}','future'))x;""")
    if not source or source['kind'] != 'outright' or str(source['multiplier']) != '1':
        raise ValueError('regional_seed_source: exact supported future quote required')
    original_frames = {}
    for day in DAYS[1:]:
        matches = [f for f in manifest['frames'] if f['at'] == day+'T16:00:00Z' and not f.get('ppaBundle')]
        if len(matches) != 1:
            raise ValueError('regional_seed_dates: select one exact 16:00 UTC source frame per standard day')
        original_frames[day] = matches[0]
    if not all(day in original_frames for day in DAYS[1:]):
        raise ValueError('regional_seed_dates: select the bounded 2020 standard source preset')
    now = query("SELECT clock_timestamp()::text;")
    sql = ["BEGIN;SET LOCAL lock_timeout='5s';SELECT pg_advisory_xact_lock(730849102);"]
    def add(table, rows, keys=('catalog_id', 'revision_id', 'code'), **options):
        sql.append(rows_sql(table, rows, keys, **options))
    add('ref_data.revisions', [dict(catalog_id=ref, id=revision, revision_key=VERSION)], ('catalog_id', 'id'))
    def copy(table, predicate, keys=('catalog_id', 'revision_id', 'code'), *, allow_empty=False):
        rows = read(f"SELECT coalesce(json_agg(r),'[]') FROM ref_data.{table} r WHERE catalog_id='{ref}' AND revision_id='{original}' AND ({predicate});")
        if not rows and not allow_empty:
            raise ValueError('regional_seed_reference: missing ' + table)
        for row in rows:
            row['revision_id'] = revision
        add('ref_data.' + table, rows, keys)
    for table, value in [('currencies', source['currency_code']), ('dimensions', source['dimension_code']),
        ('units', source['unit_code']), ('commodities', source['commodity_code']),
        ('measurement_bases', source['basis_code']), ('quantity_specifications', source['quantity_code']),
        ('location_types', source['type_code'])]:
        copy(table, 'code=' + literal(value))
    calendar_filter = f"code IN({literal(source['calendar_code'])},'test_utc_every_day')"
    clocks = f"code IN(SELECT clock_code FROM ref_data.calendars WHERE catalog_id='{ref}' AND revision_id='{original}' AND {calendar_filter} UNION SELECT clock_code FROM ref_data.locations WHERE catalog_id='{ref}' AND revision_id='{original}' AND code={literal(source['location_code'])})"
    copy('clock_rules', clocks)
    copy('clock_captures', 'clock_code IN(SELECT code FROM ref_data.clock_rules WHERE catalog_id='+literal(ref)+' AND revision_id='+literal(original)+' AND '+clocks+')', ('catalog_id','revision_id','clock_code'))
    copy('calendars', calendar_filter)
    copy('calendar_days', calendar_filter.replace('code IN', 'calendar_code IN'), ('catalog_id','revision_id','calendar_code','business_date'), allow_empty=True)
    copy('locations', 'code=' + literal(source['location_code']))
    copy('commodity_units', 'commodity_code='+literal(source['commodity_code'])+' AND unit_code='+literal(source['unit_code']), ('catalog_id','revision_id','commodity_code','unit_code'))
    copy('commodity_locations', 'commodity_code='+literal(source['commodity_code'])+' AND location_code='+literal(source['location_code']), ('catalog_id','revision_id','commodity_code','location_code'))
    r = dict(catalog_id=ref, revision_id=revision)
    add('ref_data.currencies', [dict(**r, code=code, display_code=display, name=name, display_minor_units=2)
        for code, display, name, *_ in CURRENCIES])
    add('ref_data.sources', [dict(**r, code=VERSION, kind='synthetic', locator='synthetic:'+VERSION,
        retrieved_at=now, license_reference='Project-authored synthetic examples', distribution_review='redistributable')],
        compare=(*r, 'code', 'kind', 'locator', 'license_reference', 'distribution_review'))
    add('ref_data.delivery_profiles', [dict(**r, code='regional_uniform', kind='uniform',
        clock_code=query(f"SELECT clock_code FROM ref_data.calendars WHERE catalog_id='{ref}' AND revision_id='{original}' AND code={literal(source['calendar_code'])};"),
        calendar_code=source['calendar_code'], quantity_basis='fixed_total', interval_minutes=60)])
    add('ref_data.value_specifications', [dict(**r, code='regional_decimal_rate', kind='dimensionless',
        numerator_quantity_code=None, denominator_quantity_code=None, minimum=-0.1, maximum=1)])
    quote_fields = read(f"SELECT row_to_json(q) FROM ref_data.quote_specifications q WHERE(catalog_id,revision_id,code)=('{ref}','{original}',{literal(source['code'])});")
    for code, display, _, *_ in CURRENCIES:
        add('ref_data.quote_specifications', [{**quote_fields, 'revision_id':revision, 'code':code+'_price', 'currency_code':code}])
        add('ref_data.price_index_definitions', [dict(**r, code=code+'_monthly_index', commodity_code=source['commodity_code'],
            location_code=source['location_code'], currency_code=code, quote_unit_code=source['unit_code'],
            quote_multiplier=source['multiplier'], calendar_code=source['calendar_code'])])
        add('ref_data.settlement_targets', [dict(**r, code=code+'_index_target', kind='index', index_a_code=code+'_monthly_index', index_b_code=None)])
    sql.append(f"UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id,state)=('{ref}','{revision}','draft');")
    m = dict(catalog_id=cat, revision_id=definition)
    add('market_data.revisions', [dict(catalog_id=cat, id=definition, reference_catalog_id=ref,
        reference_revision_id=revision, state='draft', generation=0)], ('catalog_id','id'),
        compare=('catalog_id','id','reference_catalog_id','reference_revision_id'))
    add('market_data.revision_periods', [dict(**m, effective_from='2020-01-01', effective_to='2022-01-01')], ('catalog_id','revision_id'))
    market_ref = dict(reference_catalog_id=ref, reference_revision_id=revision)
    for code, display, _, *_ in CURRENCIES:
        contract, series = stable(code+':contract'), stable(code+':price')
        add('market_data.contracts', [dict(**m, id=contract, **market_ref, kind='forward', delivery_from='2021-01-01',
            delivery_to='2021-02-01', expiry_at='2021-02-01T12:00:00Z', futures_series_code=None)], ('catalog_id','revision_id','id'))
        add('market_data.curves', [dict(**m, code=code+'_forward', **market_ref, commodity_code=source['commodity_code'],
            location_code=source['location_code'], quote_code=code+'_price', profile_code='regional_uniform',
            settlement_target_code=code+'_index_target', rule='direct', direct_source_code=VERSION,
            direct_role='trader_mark', name='Synthetic '+display+' forward')])
        add('market_data.series', [dict(**m, id=series, code=code+'_forward', **market_ref,
            contract_id=contract, quote_code=code+'_price', value_specification_code=None)], ('catalog_id','revision_id','id'))
        add('market_data.curve_members', [dict(**m, curve_code=code+'_forward', contract_id=contract, ordinal=0)], ('catalog_id','revision_id','curve_code','contract_id'))
        add('market_data.index_fixing_observations', [dict(**m, index_code=code+'_monthly_index', observation_from='2021-01-01',
            observation_to='2021-02-01', **market_ref, quote_code=code+'_price', series_id=series, source_code=VERSION,
            fixing_at='2021-02-01T12:00:00Z', implementation='published_index_observations_v1')],
            ('catalog_id','revision_id','index_code','observation_from','observation_to'))
    sql.append('COMMIT;')
    write('\n'.join(sql))
    rates = {}
    for code, display, _, *_ in CURRENCIES:
        curve = code+'_interest_rates'
        admitted, series = admitted_curve(cat, definition, f"'{ref}','{revision}'", curve, 'Synthetic '+display+' interest rates',
            [(day, day) for day in PILLARS], currency=code, unit='decimal_per_year', specification='regional_decimal_rate',
            source=VERSION, day_count='actual_365_fixed', compounding='simple', interpolation='log_linear_discount', extrapolation='none')
        mapping_id, curve_id = rate_id(curve, 'dates'), rate_id(curve, 'curve')
        def resume(phase_index, phase):
            object_id = mapping_id if phase_index == 1 else curve_id
            table = 'contract_date_versions' if phase_index == 1 else 'curve_definition_versions'
            if phase_index == 1:
                expected = [dict(ordinal=i,label=day,role='date',literal_date=day) for i,day in enumerate(PILLARS)]
                check = f"""(SELECT jsonb_agg(jsonb_build_object('ordinal',c.ordinal,'label',c.label,'role',f.role,'literal_date',f.literal_date) ORDER BY c.ordinal)
FROM market_data.contract_date_contracts c JOIN market_data.contract_date_fields f USING(catalog_id,definition_id,version,ordinal)
WHERE(c.catalog_id,c.definition_id,c.version)=('{cat}','{mapping_id}',1)) IS DISTINCT FROM {literal(json.dumps(expected))}::jsonb"""
                check += f" OR EXISTS(SELECT 1 FROM market_data.contract_date_versions WHERE(catalog_id,definition_id,version)=('{cat}','{mapping_id}',1) AND engine_version<>'date_list_v1')"
            else:
                expected = dict(reference_catalog_id=ref,reference_revision_id=revision,currency_code=code,mark_unit='decimal_per_year',
                    day_count='actual_365_fixed',compounding='simple',interpolation='log_linear_discount',extrapolation='none',
                    mark_source_code=VERSION,mapping_definition_id=mapping_id,mapping_version=1,mapping_generation=1,contract_count=3,kind='interest_rate')
                fields = ','.join(literal(k)+',v.'+k for k in expected)
                check = f"(SELECT jsonb_build_object({fields}) FROM market_data.curve_definition_versions v WHERE(catalog_id,definition_id,version)=('{cat}','{curve_id}',1)) IS DISTINCT FROM {literal(json.dumps(expected))}::jsonb"
                tenors = [dict(ordinal=i,label=day,series_id=sid) for i,(day,sid) in enumerate(zip(PILLARS,series))]
                check += f" OR (SELECT jsonb_agg(jsonb_build_object('ordinal',ordinal,'label',label,'series_id',series_id) ORDER BY ordinal) FROM market_data.rate_curve_tenors WHERE(catalog_id,revision_id,curve_code)=('{cat}','{definition}','{curve}')) IS DISTINCT FROM {literal(json.dumps(tenors))}::jsonb"
                admitted_terms = dict(catalog_id=cat,revision_id=definition,code=curve,reference_catalog_id=ref,reference_revision_id=revision,
                    source_code=VERSION,name='Synthetic '+display+' interest rates',definition_id=curve_id,definition_version=1,
                    currency_code=code,mark_unit='decimal_per_year',day_count='actual_365_fixed',compounding='simple',
                    interpolation='log_linear_discount',extrapolation='none',mapping_definition_id=mapping_id,mapping_version=1)
                fields = ','.join(literal(k)+',r.'+k for k in admitted_terms)
                check += f" OR (SELECT jsonb_build_object({fields}) FROM market_data.rate_curves r WHERE(catalog_id,revision_id,code)=('{cat}','{definition}','{curve}')) IS DISTINCT FROM {literal(json.dumps(admitted_terms))}::jsonb"
                series_terms = [dict(id=sid,code=curve+'.tenor.'+sid.replace('-','')[:16],reference_catalog_id=ref,
                    reference_revision_id=revision,contract_id=None,quote_code=None,value_specification_code='regional_decimal_rate') for sid in series]
                fields = ','.join(literal(k)+',s.'+k for k in series_terms[0])
                check += f" OR (SELECT jsonb_agg(jsonb_build_object({fields}) ORDER BY s.id) FROM market_data.series s WHERE(catalog_id,revision_id)=('{cat}','{definition}') AND s.id IN({','.join(literal(sid) for sid in series)})) IS DISTINCT FROM {literal(json.dumps(sorted(series_terms,key=lambda s:s['id'])))}::jsonb"
            return f"""PERFORM pg_advisory_xact_lock(730849102);
IF EXISTS(SELECT 1 FROM market_data.{table} WHERE(catalog_id,definition_id,version)=('{cat}','{object_id}',1)) THEN
 IF EXISTS(SELECT 1 FROM market_data.{table} WHERE(catalog_id,definition_id,version)=('{cat}','{object_id}',1) AND(state<>'published' OR generation<>1)) OR {check}
 THEN RAISE EXCEPTION 'regional_seed_conflict';END IF;
ELSE
{phase}
END IF;"""
        block = 'DO $rate_curve$ BEGIN IF true THEN\n'+admitted+'\nEND IF; END $rate_curve$;\nCOMMIT;'
        load_market(query, block, '', manifest, resume_phase=resume, identity=identity)
        rates[code] = series
    write(f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id,state)=('{cat}','{definition}','draft');")
    facts = ["BEGIN;SELECT pg_advisory_xact_lock(730849102);"]
    frames = []
    observation_fields = ('catalog_id','id','definition_revision_id','series_id','reference_catalog_id','reference_revision_id',
        'source_code','source_event_id','source_revision_id','role','publication_state','observed_at','received_at','value','corrects_id')
    def observations(rows):
        facts.append(rows_sql('market_data.observations', rows, ('catalog_id','id'), compare=tuple(k for k in observation_fields if k != 'received_at')))
    def summer(frame):
            at = DAYS[0]+'T15:00:00Z'
            pins = ','.join(literal(pin) for pin in frame['observationIds'])
            if not pins:
                return dict(frame,at=at)
            copied = read(f"SELECT json_agg(o) FROM market_data.observations o WHERE catalog_id='{cat}' AND id IN({pins});")
            if len(copied) != len(frame['observationIds']) or frame.get('frozenObservationIds'):
                raise ValueError('regional_seed_source: complete unfrozen July source marks required')
            new_rows = []
            pin_map = {}
            for old in copied:
                if old['role'] not in ('trader_mark','daily_settlement','parameter'):
                    raise ValueError('regional_seed_source: July cannot fabricate a final fixing')
                oid = stable('summer:'+old['id']);pin_map[old['id']] = oid
                new_rows.append({k: {**old, 'id':oid,'source_event_id':oid,'source_revision_id':'1',
                    'observed_at':at,'received_at':now,'corrects_id':None}[k] for k in observation_fields})
            observations(new_rows)
            return {**frame, 'at':at, 'observationIds':[pin_map[p] for p in frame['observationIds']], 'frozenObservationIds':[]}
    for day in DAYS:
        at = day+('T15:00:00Z' if day == DAYS[0] else 'T16:00:00Z')
        source_day = DAYS[1] if day == DAYS[0] else day
        primary_frame = summer(original_frames[source_day]) if day == DAYS[0] else original_frames[day]
        others = []
        for market in manifest.get('inputMarkets',[]):
            if market['revisionId'] == definition:
                continue
            matching = [f for f in market['frames'] if f['at'] == source_day+'T16:00:00Z']
            if len(matching) != 1:
                raise ValueError('regional_seed_dates: each original input source requires one exact daily selection')
            selected = summer(matching[0]) if day == DAYS[0] else matching[0]
            if not selected['observationIds'] and not selected['curveContracts']:
                continue
            headers = read(f"SELECT json_agg(json_build_object('definitionRevisionId',id,'referenceCatalogId',reference_catalog_id,'referenceRevisionId',reference_revision_id)) FROM market_data.revisions WHERE(catalog_id,id)=('{cat}',{literal(market['revisionId'])});")
            if len(headers) != 1:
                raise ValueError('regional_seed_source: missing exact supplemental source')
            others.append(dict(source=headers[0],frame=selected))
        pins, rows = [], []
        for code, _, _, price, _, marks in CURRENCIES:
            for ordinal, (series, value) in enumerate([(stable(code+':price'), price), *zip(rates[code], marks)]):
                oid = stable(day+':'+code+':'+str(ordinal));pins.append(oid)
                rows.append(dict(catalog_id=cat,id=oid,definition_revision_id=definition,series_id=series,**market_ref,
                    source_code=VERSION,source_event_id=oid,source_revision_id='1',role='trader_mark' if ordinal == 0 else 'parameter',
                    publication_state='final',observed_at=at,received_at=now,value=value,corrects_id=None))
        observations(rows)
        frames.append(dict(day=day, primary=primary_frame, others=others, regional=dict(at=at, observationIds=pins, frozenObservationIds=[],
            curveContracts={code+'_forward':[stable(code+':contract')] for code,*_ in CURRENCIES})))
    facts.append('COMMIT;');write('\n'.join(facts))
    # Defaults are exact currency references within this same definition revision.
    # A repeated seed keeps the original publication and receipt time.
    for code,*_ in CURRENCIES:
        mapping = stable(code+':discount-default')
        write(f"""BEGIN;SELECT pg_advisory_xact_lock(730849102);
DO $default$ DECLARE previous trading.currency_discount_defaults;publication bigint;
BEGIN
 SELECT * INTO previous FROM trading.currency_discount_defaults WHERE(catalog_id,id)=('{cat}','{mapping}');
 IF FOUND THEN
  IF(previous.definition_revision_id,previous.reference_catalog_id,previous.reference_revision_id,previous.currency_code,previous.curve_code,previous.effective_at)
   IS DISTINCT FROM('{definition}'::uuid,'{ref}'::uuid,'{revision}'::uuid,'{code}'::ref_data.code,'{code}_interest_rates'::ref_data.code,'2020-07-15T00:00:00Z'::timestamptz)
   THEN RAISE EXCEPTION 'regional_seed_conflict';END IF;
 ELSE
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id='{cat}';
  INSERT INTO trading.publications VALUES('{cat}',publication,pg_current_xact_id());
  INSERT INTO trading.currency_discount_defaults(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,currency_code,curve_code,publication_order,effective_at)
   VALUES('{cat}','{mapping}','{definition}','{ref}','{revision}','{code}','{code}_interest_rates',publication,'2020-07-15T00:00:00Z');
 END IF;
END $default$;COMMIT;""")
    return dict(version=VERSION,definitionRevisionId=definition,referenceCatalogId=ref,referenceRevisionId=revision,
        commodity=source['commodity_code'],quantityCode=source['quantity_code'],frames=frames,
        currencies=[code for code,*_ in CURRENCIES])


def plan(query, manifest, seed, *, actors, principals, portfolio_id, parties, select_for=(), hosted=None, identity=None):
    """Create/reuse the exact seed book and prepare the native application plan."""
    if manifest.get('mode')=='hosted-pilot' and (hosted is None or identity is None):
        raise ValueError('regional_seed_identity: supply the exact hosted installation and owner session')
    cat = manifest['catalogId']
    stable = lambda name: str(uuid5(UUID(cat), VERSION+':'+name))
    source = lambda definition, reference: dict(definitionRevisionId=definition,
        referenceCatalogId=manifest['referenceCatalogId'], referenceRevisionId=reference)
    primary = source(manifest['revisionId'], manifest['referenceRevisionId'])
    regional = source(seed['definitionRevisionId'], seed['referenceRevisionId'])
    def part(frame, selected):
        return dict(source=selected, observationIds=frame['observationIds'], frozenObservationIds=frame.get('frozenObservationIds',[]),
            curveContracts=frame['curveContracts'],rootCurves=sorted(frame['curveContracts']))
    frames = [dict(day=f['day'],parts=[part(f['primary'],primary),part(f['regional'],regional),
        *[part(other['frame'],other['source']) for other in f['others']]]) for f in seed['frames']]
    curves = sorted({curve for frame in frames for part0 in frame['parts'] for curve in part0['rootCurves']})
    sessions = [dict(code=code,name=name,calendar=calendar,timeZone=zone,cutoff=cutoff,
        effectiveFrom=DAYS[0],effectiveTo='2022-01-01',curves=curves,saveId=stable(code+':save'),publishId=stable(code+':publish'),
        listingId=stable(code+':listing'),grants=[dict(requestId=stable(code+':grant:'+p),principalId=p) for p in principals],
        frames=[dict(day=f['day'],frameId=stable(code+':'+f['day']+':frame'),
            requestId=stable(code+':'+f['day']+':capture'),parts=f['parts']) for f in frames])
        for code,name,calendar,zone,cutoff in [('nyc_close','NYC Close','test_new_york_days','America/New_York','16:00'),
            ('uk_close','UK Close','test_london_days','Europe/London','16:30')]]
    def listing_expected(code, request):
        prior = json.loads(query(f"SELECT coalesce(json_agg(request),'[]') FROM presentation.close_session_listing_commands WHERE(catalog_id,request_id)=({literal(cat)},{literal(request)});"))
        if len(prior) > 1:
            raise ValueError('regional_seed_actor: listing command belongs to another actor')
        return prior[0][2] if prior else int(query(f"SELECT coalesce(max(version),0) FROM presentation.close_session_listings WHERE(catalog_id,code)=({literal(cat)},{literal(code)});"))
    for session in sessions:
        session['listingExpectedVersion'] = listing_expected(session['code'],session['listingId'])
    retired = []
    for code in ('demo_close_1600','synthetic_starter_close'):
        exists = query(f"SELECT count(*) FROM market_data.close_session_versions WHERE(catalog_id,code,version,state,calendar_code,cutoff_local)=({literal(cat)},{literal(code)},1,'published','test_utc_every_day','16:00');")
        if exists == '1':
            request = stable(code+':retire')
            original_name=query(f"SELECT name FROM market_data.close_sessions WHERE(catalog_id,code)=({literal(cat)},{literal(code)});")
            retired.append(dict(code=code,name=original_name,sessionVersion=1,requestId=request,expectedVersion=listing_expected(code,request)))
    book = stable('book')
    # Explicit seed portfolio: preserve the existing portfolio identity, never infer
    # a book from whichever row happened to be inserted first.
    portfolios = json.loads(query(f"SELECT coalesce(json_agg(p),'[]') FROM trading.portfolios p WHERE(catalog_id,id)=({literal(cat)},{literal(str(UUID(portfolio_id)))});"))
    if len(portfolios) != 1:
        raise ValueError('regional_seed_scope: the exact selected portfolio is unavailable')
    for table,prefix in [('legal_entities','legalEntity'),('counterparties','counterparty')]:
        identifier,version = str(UUID(parties[prefix+'Id'])),int(parties[prefix+'Revision'])
        rows = json.loads(query(f"SELECT coalesce(json_agg(t),'[]') FROM trading.{table} t WHERE(catalog_id,id,revision)=({literal(cat)},{literal(identifier)},{version});"))
        if len(rows) != 1:
            raise ValueError('regional_seed_parties: exact selected party version required')
    calendar = dict(revision=dict(catalogId=seed['referenceCatalogId'],revisionId=seed['referenceRevisionId']),code='test_utc_every_day')
    payment = dict(frequency='monthly',rule='days_after_period_end',days=3,dayOfMonth=None,dayBasis='calendar_days',
        shortMonth=None,adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1')
    swaps = []
    for code, display, _, _, strike, _ in CURRENCIES:
        terms = dict(currency=code,deliveryFrom='2021-01-01',deliveryTo='2021-02-01',paymentSchedule=payment)
        swaps.append(dict(**parties,operationId=stable(code+':swap'),snapshotId=sessions[1]['frames'][0]['frameId'],
            convention='independent_side_financial_swap_v2',knowledgeMode='current',bookId=book,
            definitionRevisionId=seed['definitionRevisionId'],commodity=seed['commodity'],quantity='10',fixedCoefficient='-1',discounting=True,
            fixed=dict(**terms,calendarPolicy=side_calendar_policy(calendar,delivery=True,observation=False,payment=True),price=strike),
            floating=dict(**terms,calendarPolicy=side_calendar_policy(calendar,delivery=True,observation=True,payment=True),
                spread='0',settlement=dict(mode='curve_target',index=None,projectionPolicy=None),curve=code+'_forward',index=code+'_monthly_index',resetFrequency='monthly',averagingMethod='none')))
    catalogue_id = stable('catalogue')
    previous = json.loads(query(f"SELECT coalesce(json_agg(request),'[]') FROM market_data.catalogue_commands WHERE(catalog_id,request_id)=({literal(cat)},{literal(catalogue_id)});"))
    if len(previous) > 1:
        raise ValueError('regional_seed_actor: command already belongs to another actor')
    if previous:
        catalogue = previous[0]
    else:
        catalogue = json.loads(query(f"SELECT jsonb_build_object('expected',id,'definition',security.catalogue_json(catalog_id,id)) FROM market_data.catalogue_publications WHERE catalog_id={literal(cat)} ORDER BY publication_order DESC LIMIT 1;"))
        # Keep only the native definition's four authored fields.
        catalogue['definition'] = {key:catalogue['definition'][key] for key in ('effectiveFrom','effectiveTo','curves','roles')}
    for code,*_ in CURRENCIES:
        for suffix,kind in (('_forward','curve'),('_interest_rates','rate')):
            entry = dict(code=code+suffix,kind=kind,**regional)
            matches = [c for c in catalogue['definition']['curves'] if c['code'] == entry['code']]
            if matches and matches != [entry]:
                raise ValueError('regional_seed_conflict: catalogue curve differs')
            if not matches:
                catalogue['definition']['curves'].append(entry)
    # Existing supplemental pricing remains an explicit source of both closes.
    for selected in {json.dumps(p['source'],sort_keys=True):p for f in frames for p in f['parts']}.values():
        selected_source = selected['source']
        for code in selected['rootCurves']:
            entry = dict(code=code,kind='curve',**selected_source)
            matches = [c for c in catalogue['definition']['curves'] if c['code'] == code]
            if matches and matches != [entry]:
                raise ValueError('regional_seed_conflict: existing exact source differs')
            if not matches:
                catalogue['definition']['curves'].append(entry)
    catalogue['definition']['roles']['reference:currencies'] = regional
    # Match native publication normalization before retaining the first plan.
    catalogue['definition']['curves'].sort(key=lambda entry:entry['code'])
    # A new synthetic book uses the existing publication directory. Exact retries
    # retain its original publication, independently of later unrelated books.
    hosted_write(query,f"""BEGIN;SELECT pg_advisory_xact_lock(730849102);
DO $book$ DECLARE previous trading.books;publication integer;
BEGIN
 SELECT * INTO previous FROM trading.books WHERE(catalog_id,id)=('{cat}','{book}');
 IF FOUND THEN
  IF(previous.portfolio_id,previous.name) IS DISTINCT FROM('{portfolios[0]['id']}'::uuid,'Synthetic regional currencies'::text)
   THEN RAISE EXCEPTION 'regional_seed_conflict';END IF;
 ELSE
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id='{cat}';
  INSERT INTO trading.publications VALUES('{cat}',publication,pg_current_xact_id());
  INSERT INTO trading.books VALUES('{cat}','{book}','{portfolios[0]['id']}','Synthetic regional currencies',publication,'2020-07-15T00:00:00Z');
 END IF;
END $book$;COMMIT;""",cat,manifest['referenceCatalogId'],identity)
    return dict(version=VERSION,catalogId=cat,primary=primary,regional=regional,actors=actors,hosted=hosted,
        selectFor=list(select_for),sessions=sessions,swaps=swaps,
        book=dict(id=book,portfolioId=portfolios[0]['id'],name='Synthetic regional currencies'),
        catalogue=dict(requestId=catalogue_id,expectedHead=catalogue['expected'],definition=catalogue['definition']),retire=retired)
