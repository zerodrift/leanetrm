"""Option reference storage in the owned exchange example: sourced product facts,
explicit synthetic strikes, unknown facts kept unknown, exact-version keys."""


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
    check('CL last-trading instants stay unknown, so no CL contract is executable',
          query(f"SELECT count(*) FILTER(WHERE m.event_at IS NULL),count(k.id) FROM ref_data.futures_series s JOIN ref_data.futures_milestones m ON(m.catalog_id,m.revision_id,m.series_code,m.milestone)=(s.catalog_id,s.revision_id,s.code,'last_trading') LEFT JOIN market_data.contracts k ON(k.reference_catalog_id,k.reference_revision_id,k.futures_series_code)=(s.catalog_id,s.revision_id,s.code) WHERE (s.catalog_id,s.revision_id)=({R}) AND s.product_code='nymex_cl';") == '6|0')

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
    check('five confirmed maturities per product, three strikes each, as calls and puts',
          query(f"SELECT string_agg(n::text,',' ORDER BY code) FROM(SELECT p.code,count(*) n FROM {series} GROUP BY p.code HAVING count(DISTINCT u.code)=5 AND count(*) FILTER(WHERE o.option_right='call')=15 AND count(*) FILTER(WHERE o.option_right='put')=15)x;") == '30,30')
    check('every series underlies its product\'s own futures product', query(f"SELECT count(*) FROM {series} AND f.code<>p.underlying_product_code;") == '0')
    check('strikes are multiples of the futures trading tick and of the strike interval', query(f"SELECT count(*) FROM {series} AND (o.strike % f.outright_tick_size<>0 OR o.strike % p.strike_interval<>0);") == '0')
    check('every option series expires before its underlying future',
          query(f"SELECT count(*) FROM {series} AND NOT(o.last_trading_date<m.business_date AND (o.expiry_at IS NULL OR m.event_at IS NULL OR o.expiry_at<m.event_at));") == '0')
    check('option expiry dates are the source-confirmed ones; their time of day stays unknown',
          query(f"SELECT string_agg(DISTINCT concat_ws('|',p.code,o.last_trading_date),';' ORDER BY concat_ws('|',p.code,o.last_trading_date)) FROM {series} AND o.expiry_at IS NULL AND o.instruction_cutoff_at IS NULL;")
          == 'nymex_lo|2026-11-17;nymex_lo|2026-12-16;nymex_lo|2027-01-14;nymex_lo|2027-02-17;nymex_lo|2027-03-17;'
             'nymex_on|2026-11-24;nymex_on|2026-12-28;nymex_on|2027-01-26;nymex_on|2027-02-23;nymex_on|2027-03-25')
    check('example series are explicit candidate rows on the confirmed strike grid', query(f"SELECT count(*) FROM {series} AND o.definition_state<>'candidate';") == '0')
    check('option reference tables carry the futures tables\' row security and grants',
          query("SELECT count(DISTINCT x) FROM(SELECT c.relrowsecurity,c.relforcerowsecurity,c.relacl::text,(SELECT string_agg(concat_ws('|',polname,polpermissive,polcmd,polroles::text,pg_get_expr(polqual,polrelid),pg_get_expr(polwithcheck,polrelid)),';' ORDER BY polname) FROM pg_policy WHERE polrelid=c.oid) FROM pg_class c WHERE c.oid IN('ref_data.futures_products'::regclass,'ref_data.futures_option_rules'::regclass,'ref_data.futures_option_products'::regclass,'ref_data.futures_option_series'::regclass))x;") == '1')

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
    check('a series whose underlying belongs to another revision fails on the exact-version foreign key',
          rejected(copy + f"INSERT INTO ref_data.futures_option_series SELECT (jsonb_populate_record(NULL::ref_data.futures_option_series,'{option}'::jsonb||jsonb_build_object('revision_id','{draft}'))).*;",
                   '23503', 'futures_option_series_underlying_fkey'))
    product = query(f"SELECT row_to_json(p) FROM ref_data.futures_option_products p WHERE {in_revision} AND code='nymex_on';")
    check('a european exercise style is rejected by the check constraint',
          rejected(copy + f"INSERT INTO ref_data.futures_option_products SELECT (jsonb_populate_record(NULL::ref_data.futures_option_products,'{product}'::jsonb||jsonb_build_object('revision_id','{draft}','code','european_on','exercise_style','european'))).*;",
                   '23514', 'futures_option_products_exercise_style_check'))
    check('a strike off the strike interval is rejected', rejected(
        draft_copy(chain[:-2] + ['futures_series', 'futures_milestones', 'futures_option_rules', 'futures_option_products'])
        + f"INSERT INTO ref_data.futures_option_series SELECT (jsonb_populate_record(NULL::ref_data.futures_option_series,'{option}'::jsonb||jsonb_build_object('revision_id','{draft}','code','off_tick','strike',(('{option}'::jsonb->>'strike')::numeric+0.001)))).*;",
        'P0001', message='option_strike'))

    # Market definitions: the option kind needs its exact option series and a known expiry instant.
    market = f"'{manifest['catalogId']}','20000000-0000-0000-0000-0000000000f7'"
    code = query(f"SELECT code FROM ref_data.futures_option_series WHERE {in_revision} ORDER BY code LIMIT 1;")
    draft_market = (f"INSERT INTO market_data.revisions VALUES({market},{R},'draft',0);"
                    f"INSERT INTO market_data.revision_periods VALUES({market},'2026-10-01','2028-01-01');")
    check('a futures-option contract requires its option series',
          rejected(draft_market + f"INSERT INTO market_data.contracts VALUES({market},gen_random_uuid(),{R},'future_option','2026-12-01','2027-01-01','2026-11-24T19:30:00Z',NULL,NULL);",
                   '23514', 'contracts_series_kind_check'))
    check('a futures-option contract cannot be sealed while its series expiry instant is unknown',
          rejected(draft_market + f"INSERT INTO market_data.contracts VALUES({market},gen_random_uuid(),{R},'future_option','2026-12-01','2027-01-01','2026-11-24T19:30:00Z',NULL,'{code}');"
                   f"UPDATE market_data.revisions SET state='sealed' WHERE (catalog_id,id)=({market});", '23514', message='Futures option constituent must match a listed series with a known expiry instant'))
    check('no option contract is executable while expiry instants are unknown',
          query(f"SELECT count(*) FROM market_data.contracts WHERE catalog_id='{manifest['catalogId']}' AND kind='future_option';") == '0')
    check('every exchange price observation is labeled synthetic',
          query(f"SELECT count(*) FILTER(WHERE source_event_id NOT LIKE 'synthetic\\_%'),count(*)>0 FROM market_data.observations WHERE catalog_id='{manifest['catalogId']}';") == '0|t')
