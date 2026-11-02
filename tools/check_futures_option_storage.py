"""Option reference and market storage in the owned exchange example: sourced product facts,
configured close-of-trading times, explicit strikes, synthetic settlements and exact-version keys."""


def run_checks(query, manifest):
    R = f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
    in_revision = f"(catalog_id,revision_id)=({R})"

    def check(name, condition):
        assert condition, name
        print('PASS ' + name, flush=True)

    def rejected(sql, state, constraint='', message=None):
        """The statements fail with this SQLSTATE on this named constraint (and message), then roll back."""
        expected = '' if message is None else f"IF SQLERRM<>'{message}' THEN RAISE EXCEPTION 'Rejected with %, expected {message}',SQLERRM;END IF;"
        return query(f"""BEGIN;
DO $$DECLARE c text;BEGIN {sql} RAISE EXCEPTION 'Expected rejection';
EXCEPTION WHEN SQLSTATE '{state}' THEN GET STACKED DIAGNOSTICS c=CONSTRAINT_NAME;
 IF c<>'{constraint}' THEN RAISE EXCEPTION 'Rejected by %, expected {constraint}',c;END IF;{expected}END$$;
ROLLBACK;""") is not None

    cl = query(f"SELECT exchange_code,settlement_method,contract_size,outright_tick_size,lot_unit_code FROM ref_data.futures_products WHERE {in_revision} AND code='nymex_cl';")
    check('NYMEX CL is a physical 1,000-barrel future with a 0.01 tick', cl == 'nymex|physical|1000|0.01|petroleum_barrel')
    cl_dates = query(f"SELECT string_agg(m.business_date::text,',' ORDER BY s.delivery_from) FROM ref_data.futures_series s JOIN ref_data.futures_milestones m ON(m.catalog_id,m.revision_id,m.series_code,m.milestone)=(s.catalog_id,s.revision_id,s.code,'last_trading') WHERE (s.catalog_id,s.revision_id)=({R}) AND s.product_code='nymex_cl';")
    check('CL keeps the six source-confirmed last trading dates',
          cl_dates == '2026-10-20,2026-11-20,2026-12-21,2027-01-20,2027-02-22,2027-03-22')
    check('close-of-trading times are configured per product and milestone from an explicit operator qualification',
          query(f"SELECT string_agg(concat_ws('|',coalesce(t.futures_product_code,t.option_product_code),t.milestone,t.local_time,t.clock_code,s.kind,s.locator),';' ORDER BY coalesce(t.futures_product_code,t.option_product_code)) FROM ref_data.trading_close_times t JOIN ref_data.sources s ON(s.catalog_id,s.revision_id,s.code)=(t.catalog_id,t.revision_id,t.source_code) WHERE (t.catalog_id,t.revision_id)=({R}) AND s.code='nymex_close_qualification';")
          == ';'.join(f'{code}|{milestone}|14:30:00|ept|manual_entry|operator:nymex-close-1430-new-york-v1' for code, milestone in
                      [('nymex_cl', 'last_trading'), ('nymex_lo', 'option_expiry'), ('nymex_ng', 'last_trading'), ('nymex_on', 'option_expiry')]))
    check('CL last trading is 14:30 New York on each confirmed date, and every CL contract is executable at that instant',
          query(f"SELECT count(*) FILTER(WHERE m.event_at AT TIME ZONE 'America/New_York'=m.business_date+TIME '14:30' AND k.expiry_at=m.event_at),count(*) FROM ref_data.futures_series s JOIN ref_data.futures_milestones m ON(m.catalog_id,m.revision_id,m.series_code,m.milestone)=(s.catalog_id,s.revision_id,s.code,'last_trading') LEFT JOIN market_data.contracts k ON(k.reference_catalog_id,k.reference_revision_id,k.futures_series_code)=(s.catalog_id,s.revision_id,s.code) WHERE (s.catalog_id,s.revision_id)=({R}) AND s.product_code='nymex_cl';") == '6|6')
    check('NG and CL futures curves value at synthetic daily settlements under a matching execution policy',
          query(f"SELECT string_agg(concat_ws('|',c.code,c.direct_role,(SELECT string_agg(DISTINCT p.price_role,',') FROM ref_data.futures_execution_policies p JOIN ref_data.futures_series s ON(s.catalog_id,s.revision_id,s.code)=(p.catalog_id,p.revision_id,p.series_code) WHERE (p.catalog_id,p.revision_id)=({R}) AND s.product_code=c.code)),';' ORDER BY c.code) FROM market_data.curves c WHERE c.catalog_id='{manifest['catalogId']}' AND c.code IN('nymex_ng','nymex_cl');")
          == 'nymex_cl|daily_settlement|daily_settlement;nymex_ng|daily_settlement|daily_settlement')

    products = query(f"SELECT string_agg(concat_ws('|',code,underlying_product_code,exercise_style,settlement_method,multiplier),';' ORDER BY code) FROM ref_data.futures_option_products WHERE {in_revision};")
    check('ON and LO are American options exercised into exactly nymex_ng and nymex_cl',
          products == 'nymex_lo|nymex_cl|american|exercise_into_future|1000;nymex_on|nymex_ng|american|exercise_into_future|10000')
    check('each option product quotes its own premium increment as tick and clearing increment',
          query(f"SELECT string_agg(concat_ws('|',p.code,q.trading_tick,q.clearing_increment),';' ORDER BY p.code) FROM ref_data.futures_option_products p JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(p.catalog_id,p.revision_id,p.quote_code) WHERE (p.catalog_id,p.revision_id)=({R});")
          == 'nymex_lo|0.01|0.01;nymex_on|0.001|0.001')
    check('each option product counts one option contract as its lot quantity',
          query(f"SELECT string_agg(concat_ws('|',p.code,z.unit_code),';' ORDER BY p.code) FROM ref_data.futures_option_products p JOIN ref_data.quantity_specifications z ON(z.catalog_id,z.revision_id,z.code)=(p.catalog_id,p.revision_id,p.lot_quantity_code) WHERE (p.catalog_id,p.revision_id)=({R});")
          == 'nymex_lo|option_contract;nymex_on|option_contract')
    check('the automatic exercise rule is the versioned in-the-money-at-settlement rule, without contrary instructions',
          query(f"SELECT string_agg(concat_ws('|',p.code,a.code,a.kind,a.version,p.contrary_instructions_allowed,p.premium_style),';' ORDER BY p.code) FROM ref_data.futures_option_products p JOIN ref_data.futures_option_rules a ON(a.catalog_id,a.revision_id,a.code)=(p.catalog_id,p.revision_id,p.automatic_exercise_rule_code) WHERE (p.catalog_id,p.revision_id)=({R});")
          == 'nymex_lo|nymex_itm_settlement_v1|automatic_exercise|1|f|premium_paid_up_front;nymex_on|nymex_itm_settlement_v1|automatic_exercise|1|f|premium_paid_up_front')
    check('exercise-notice cutoffs are sourced facts whose time zone stays unknown, so no cutoff instant is derived',
          query(f"SELECT string_agg(DISTINCT concat_ws('|',n.kind,n.clock_state,n.clock_code IS NULL,s.locator),';') FROM ref_data.futures_option_products p JOIN ref_data.futures_option_rules n ON(n.catalog_id,n.revision_id,n.code)=(p.catalog_id,p.revision_id,p.exercise_notice_rule_code) JOIN ref_data.sources s ON(s.catalog_id,s.revision_id,s.code)=(n.catalog_id,n.revision_id,n.source_code) WHERE (p.catalog_id,p.revision_id)=({R});")
          == 'exercise_notice|unknown|t|https://www.cmegroup.com/content/dam/cmegroup/rulebook/NYMEX/3/300.pdf'
          and query(f"SELECT count(*) FROM ref_data.futures_option_series WHERE {in_revision} AND instruction_cutoff_at IS NOT NULL;") == '0')
    check('near-the-money strike intervals follow the exchange listing table',
          query(f"SELECT string_agg(concat_ws('|',p.code,p.strike_interval,s.locator),';' ORDER BY p.code) FROM ref_data.futures_option_products p JOIN ref_data.sources s ON(s.catalog_id,s.revision_id,s.code)=(p.catalog_id,p.revision_id,p.strike_interval_source_code) WHERE (p.catalog_id,p.revision_id)=({R});")
          == 'nymex_lo|0.50|https://www.cmegroup.com/trading/files/strike-price/strike-price-listing-and-exercise-procedures-table.xlsx;'
             'nymex_on|0.05|https://www.cmegroup.com/trading/files/strike-price/strike-price-listing-and-exercise-procedures-table.xlsx')
    series = f"ref_data.futures_option_series o JOIN ref_data.futures_option_products p ON(p.catalog_id,p.revision_id,p.code)=(o.catalog_id,o.revision_id,o.product_code) JOIN ref_data.futures_series u ON(u.catalog_id,u.revision_id,u.code)=(o.catalog_id,o.revision_id,o.underlying_series_code) JOIN ref_data.futures_products f ON(f.catalog_id,f.revision_id,f.code)=(u.catalog_id,u.revision_id,u.product_code) JOIN ref_data.futures_milestones m ON(m.catalog_id,m.revision_id,m.series_code,m.milestone)=(u.catalog_id,u.revision_id,u.code,'last_trading') WHERE (o.catalog_id,o.revision_id)=({R})"
    check('five confirmed maturities per product, three strikes each as calls and puts, plus one deep in-the-money ON call',
          query(f"SELECT string_agg(x,';' ORDER BY x) FROM(SELECT concat_ws('|',p.code,count(DISTINCT u.code),count(*) FILTER(WHERE o.option_right='call'),count(*) FILTER(WHERE o.option_right='put')) x FROM {series} GROUP BY p.code)y;")
          == 'nymex_lo|5|15|15;nymex_on|5|16|15')
    check('every series underlies its product\'s own futures product', query(f"SELECT count(*) FROM {series} AND f.code<>p.underlying_product_code;") == '0')
    check('strikes are multiples of the futures trading tick and of the strike interval', query(f"SELECT count(*) FROM {series} AND (o.strike % f.outright_tick_size<>0 OR o.strike % p.strike_interval<>0);") == '0')
    check('every option series expires before its underlying future',
          query(f"SELECT count(*) FROM {series} AND NOT(o.last_trading_date<m.business_date AND o.expiry_at<m.event_at);") == '0')
    check('options expire at 14:30 New York on the source-confirmed dates; no instruction cutoff is set',
          query(f"SELECT string_agg(DISTINCT concat_ws('|',p.code,o.last_trading_date),';' ORDER BY concat_ws('|',p.code,o.last_trading_date)) FROM {series} AND o.expiry_at AT TIME ZONE 'America/New_York'=o.last_trading_date+TIME '14:30' AND o.instruction_cutoff_at IS NULL;")
          == 'nymex_lo|2026-11-17;nymex_lo|2026-12-16;nymex_lo|2027-01-14;nymex_lo|2027-02-17;nymex_lo|2027-03-17;'
             'nymex_on|2026-11-24;nymex_on|2026-12-28;nymex_on|2027-01-26;nymex_on|2027-02-23;nymex_on|2027-03-25')
    check('example series are explicit candidate rows on the confirmed strike grid', query(f"SELECT count(*) FROM {series} AND o.definition_state<>'candidate';") == '0')
    check('option and close-time reference tables carry the futures tables\' row security and grants',
          query("SELECT count(DISTINCT x) FROM(SELECT c.relrowsecurity,c.relforcerowsecurity,c.relacl::text,(SELECT string_agg(concat_ws('|',polname,polpermissive,polcmd,polroles::text,pg_get_expr(polqual,polrelid),pg_get_expr(polwithcheck,polrelid)),';' ORDER BY polname) FROM pg_policy WHERE polrelid=c.oid) FROM pg_class c WHERE c.oid IN('ref_data.futures_products'::regclass,'ref_data.futures_option_rules'::regclass,'ref_data.futures_option_products'::regclass,'ref_data.futures_option_series'::regclass,'ref_data.trading_close_times'::regclass))x;") == '1')

    # A private draft copy of the product chain, so each write fails on exactly one rule.
    draft = '20000000-0000-0000-0000-0000000000f6'
    D = f"'{manifest['referenceCatalogId']}','{draft}'"
    chain = ['dimensions', 'currencies', 'units', 'commodities', 'commodity_units', 'measurement_bases', 'quantity_specifications',
             'quote_specifications', 'exchanges', 'clearing_houses', 'exchange_clearing_houses', 'clock_rules', 'calendars', 'location_types',
             'locations', 'commodity_locations', 'sources', 'futures_products', 'futures_option_rules', 'futures_option_products']
    def draft_copy(tables):
        return f"INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES({D},'option_storage_check');" + ''.join(
            f"INSERT INTO ref_data.{t} SELECT (jsonb_populate_record(NULL::ref_data.{t},to_jsonb(x)||jsonb_build_object('revision_id','{draft}'))).* FROM ref_data.{t} x WHERE (catalog_id,revision_id)=({R});"
            for t in tables)
    copy = draft_copy(chain)
    underlying = query(f"SELECT underlying_series_code FROM ref_data.futures_option_series WHERE {in_revision} ORDER BY code LIMIT 1;")
    option = query(f"SELECT row_to_json(o) FROM ref_data.futures_option_series o WHERE {in_revision} AND underlying_series_code='{underlying}' ORDER BY code LIMIT 1;")
    check('a series whose underlying is not readable in its own revision is refused before it is stored, never stored unchecked',
          rejected(copy + f"INSERT INTO ref_data.futures_option_series SELECT (jsonb_populate_record(NULL::ref_data.futures_option_series,'{option}'::jsonb||jsonb_build_object('revision_id','{draft}'))).*;",
                   'P0001', message='option_reference_unreadable'))
    product = query(f"SELECT row_to_json(p) FROM ref_data.futures_option_products p WHERE {in_revision} AND code='nymex_on';")
    check('a european exercise style is rejected by the check constraint',
          rejected(copy + f"INSERT INTO ref_data.futures_option_products SELECT (jsonb_populate_record(NULL::ref_data.futures_option_products,'{product}'::jsonb||jsonb_build_object('revision_id','{draft}','code','european_on','exercise_style','european'))).*;",
                   '23514', 'futures_option_products_exercise_style_check'))
    milestone = query(f"SELECT row_to_json(m) FROM ref_data.futures_milestones m WHERE {in_revision} AND milestone='last_trading' AND series_code='nymex_cl_20261201';")
    # An ON series: its strike moved by one NG futures tick stays on the tick but leaves the 0.05 strike grid.
    on_option = query(f"SELECT row_to_json(o) FROM ref_data.futures_option_series o WHERE {in_revision} AND product_code='nymex_on' AND last_trading_date='2026-11-24' ORDER BY code LIMIT 1;")
    tick = query(f"SELECT outright_tick_size FROM ref_data.futures_products WHERE {in_revision} AND code='nymex_ng';")
    listed = chain + ['trading_close_times', 'futures_series', 'futures_milestones']
    def option_insert(changes):
        return f"INSERT INTO ref_data.futures_option_series SELECT (jsonb_populate_record(NULL::ref_data.futures_option_series,'{on_option}'::jsonb||jsonb_build_object('revision_id','{draft}','code','changed'{changes}))).*;"
    check('a strike on the futures tick but off the strike grid is rejected', rejected(
        draft_copy(listed) + option_insert(f",'strike',(('{on_option}'::jsonb->>'strike')::numeric+{tick})"), 'P0001', message='option_strike'))
    check('a series whose option product is not readable in its own revision is refused', rejected(
        draft_copy(listed) + option_insert(",'product_code','unlisted_option_product'"), 'P0001', message='option_reference_unreadable'))
    check('a series whose underlying is another product\'s future is rejected', rejected(
        draft_copy(listed) + option_insert(",'underlying_series_code','nymex_cl_20261201'"), 'P0001', message='option_underlying'))
    check('a series that does not expire before its underlying future is rejected', rejected(
        draft_copy(listed) + option_insert(f",'last_trading_date','2026-11-25','expiry_at',ref_data.close_instant('{manifest['referenceCatalogId']}','{draft}','option_expiry','nymex_on','2026-11-25')"),
        'P0001', message='option_expiry'))
    close = query(f"SELECT row_to_json(t) FROM ref_data.trading_close_times t WHERE {in_revision} AND futures_product_code='nymex_cl';")
    check('a close configured after its product\'s milestones must reproduce their stored instants', rejected(
        draft_copy(chain + ['futures_series', 'futures_milestones'])
        + f"INSERT INTO ref_data.trading_close_times SELECT (jsonb_populate_record(NULL::ref_data.trading_close_times,'{close}'::jsonb||jsonb_build_object('revision_id','{draft}','local_time','15:30'))).*;",
        'P0001', message='close_time_mismatch'))
    check('a configured close is a whole second', rejected(
        draft_copy(chain)
        + f"INSERT INTO ref_data.trading_close_times SELECT (jsonb_populate_record(NULL::ref_data.trading_close_times,'{close}'::jsonb||jsonb_build_object('revision_id','{draft}','local_time','14:30:00.5'))).*;",
        '23514', 'trading_close_times_local_time_check'))
    check('a last-trading milestone on another clock than its configured close is rejected', rejected(
        draft_copy(chain + ['trading_close_times', 'futures_series'])
        + f"INSERT INTO ref_data.futures_milestones SELECT (jsonb_populate_record(NULL::ref_data.futures_milestones,'{milestone}'::jsonb||jsonb_build_object('revision_id','{draft}','clock_code','cpt'))).*;",
        'P0001', message='close_time_mismatch'))
    check('known last-trading instants use their configured close\'s clock and cite its qualification',
          query(f"SELECT count(*) FILTER(WHERE m.clock_code=t.clock_code AND EXISTS(SELECT 1 FROM ref_data.product_evidence e WHERE (e.catalog_id,e.revision_id,e.product_code,e.source_code,e.fact_group)=(t.catalog_id,t.revision_id,t.futures_product_code,t.source_code,'dates'))),count(*) FROM ref_data.futures_milestones m JOIN ref_data.futures_series s ON(s.catalog_id,s.revision_id,s.code)=(m.catalog_id,m.revision_id,m.series_code) JOIN ref_data.trading_close_times t ON(t.catalog_id,t.revision_id,t.futures_product_code)=(s.catalog_id,s.revision_id,s.product_code) WHERE (m.catalog_id,m.revision_id)=({R}) AND m.milestone='last_trading' AND m.event_at IS NOT NULL;") == '67|67')
    check('an option series without a configured expiry time is a structured error, never a default', rejected(
        draft_copy(chain + ['futures_series', 'futures_milestones'])
        + f"INSERT INTO ref_data.futures_option_series SELECT (jsonb_populate_record(NULL::ref_data.futures_option_series,'{option}'::jsonb||jsonb_build_object('revision_id','{draft}'))).*;",
        'P0001', message='close_time_setting_missing'))
    check('an option series expiry that differs from its configured close is rejected', rejected(
        draft_copy(chain + ['trading_close_times', 'futures_series', 'futures_milestones'])
        + f"INSERT INTO ref_data.futures_option_series SELECT (jsonb_populate_record(NULL::ref_data.futures_option_series,'{option}'::jsonb||jsonb_build_object('revision_id','{draft}','expiry_at',(('{option}'::jsonb->>'expiry_at')::timestamptz+interval '1 hour')))).*;",
        'P0001', message='close_time_mismatch'))
    check('a last-trading instant that differs from its product\'s configured close is rejected', rejected(
        draft_copy(chain + ['trading_close_times', 'futures_series'])
        + f"INSERT INTO ref_data.futures_milestones SELECT (jsonb_populate_record(NULL::ref_data.futures_milestones,'{milestone}'::jsonb||jsonb_build_object('revision_id','{draft}','event_at',(('{milestone}'::jsonb->>'event_at')::timestamptz-interval '1 hour')))).*;",
        'P0001', message='close_time_mismatch'))

    # Market definitions: the option kind needs its exact option series and a known expiry instant.
    market = f"'{manifest['catalogId']}','20000000-0000-0000-0000-0000000000f7'"
    code = query(f"SELECT code FROM ref_data.futures_option_series WHERE {in_revision} ORDER BY code LIMIT 1;")
    draft_market = (f"INSERT INTO market_data.revisions VALUES({market},{R},'draft',0);"
                    f"INSERT INTO market_data.revision_periods VALUES({market},'2026-10-01','2028-01-01');")
    check('a futures-option contract requires its option series',
          rejected(draft_market + f"INSERT INTO market_data.contracts VALUES({market},gen_random_uuid(),{R},'future_option','2026-12-01','2027-01-01','2026-11-24T19:30:00Z',NULL,NULL);",
                   '23514', 'contracts_series_kind_check'))
    check('a futures-option contract cannot be sealed with an expiry other than its series\'',
          rejected(draft_market + f"INSERT INTO market_data.contracts SELECT {market},gen_random_uuid(),{R},'future_option',u.delivery_from,u.delivery_to,o.expiry_at+interval '1 hour',NULL,o.code FROM ref_data.futures_option_series o JOIN ref_data.futures_series u ON(u.catalog_id,u.revision_id,u.code)=(o.catalog_id,o.revision_id,o.underlying_series_code) WHERE (o.catalog_id,o.revision_id,o.code)=({R},'{code}');"
                   f"UPDATE market_data.revisions SET state='sealed' WHERE (catalog_id,id)=({market});", '23514', message='Futures option constituent must match its listed series'))
    check('every option series is an executable contract on its product\'s direct daily-settlement curve',
          query(f"SELECT count(*) FILTER(WHERE k.expiry_at=o.expiry_at AND c.direct_role='daily_settlement' AND c.rule='direct' AND c.quote_code=p.quote_code),count(*) FROM ref_data.futures_option_series o JOIN ref_data.futures_option_products p ON(p.catalog_id,p.revision_id,p.code)=(o.catalog_id,o.revision_id,o.product_code) JOIN market_data.contracts k ON(k.reference_catalog_id,k.reference_revision_id,k.futures_option_series_code)=(o.catalog_id,o.revision_id,o.code) JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(k.catalog_id,k.revision_id,k.id) JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.revision_id,m.curve_code) WHERE (o.catalog_id,o.revision_id)=({R});") == '61|61')
    settled = f"""WITH obs AS(SELECT o.observed_at,o.value,k.futures_series_code,k.futures_option_series_code FROM market_data.observations o
 JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id)
 JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(s.catalog_id,s.revision_id,s.contract_id)
 WHERE o.catalog_id='{manifest['catalogId']}' AND o.role='daily_settlement'),
x AS(SELECT os.code,op.value premium,fu.value futures,os.strike,os.option_right,q.clearing_increment inc,
 greatest(CASE os.option_right WHEN 'call' THEN fu.value-os.strike ELSE os.strike-fu.value END,0) intrinsic
 FROM obs op JOIN ref_data.futures_option_series os ON(os.catalog_id,os.revision_id,os.code)=({R},op.futures_option_series_code)
 JOIN ref_data.futures_option_products p ON(p.catalog_id,p.revision_id,p.code)=(os.catalog_id,os.revision_id,os.product_code)
 JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(p.catalog_id,p.revision_id,p.quote_code)
 JOIN obs fu ON fu.futures_series_code=os.underlying_series_code AND fu.observed_at=op.observed_at)"""
    check('each option settles in every frame above intrinsic plus one increment and within its bound; one deep in-the-money near-expiry call settles at intrinsic',
          query(settled + " SELECT count(*),count(*) FILTER(WHERE premium=intrinsic),string_agg(DISTINCT code,',') FILTER(WHERE premium=intrinsic),"
                "count(*) FILTER(WHERE premium<>intrinsic AND premium<=intrinsic+inc),count(*) FILTER(WHERE premium%inc<>0 OR premium>CASE option_right WHEN 'call' THEN futures ELSE strike END) FROM x;")
          == '244|4|nymex_on_20261201_c2.00|0|0')
    check('every exchange price observation is labeled synthetic',
          query(f"SELECT count(*) FILTER(WHERE source_event_id NOT LIKE 'synthetic\\_%'),count(*)>0 FROM market_data.observations WHERE catalog_id='{manifest['catalogId']}';") == '0|t')
    check('a close on a clock the caller cannot see is a structured error, never a missing instant that passes the close checks',
          rejected(f"PERFORM ref_data.local_instant({R},'unseen_clock','14:30','2026-10-16');", 'P0001', message='close_time_clock'))
