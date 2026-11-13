"""Synthetic crude futures, a monthly differential and a nine-day business calendar for contract observation curves.

Every name, date and price is synthetic. Five monthly futures E to J expire at 19:30 UTC and settle daily; every observation
is sampled at 12:00 UTC. Four closes hold the context marks; the other settlements are history read at their own day's
12:00. The 2027-01-19 settlement of G is recorded by the check, after its first knowledge view."""
from preview_fixture import CAT, REV, R, M

CURVE, DIFFERENTIAL, CALENDAR = 'synthetic_crude_future', 'synthetic_crude_differential', 'synthetic_crude_days'
# Contract, delivery month, last trading day; each expires at 19:30 UTC.
FUTURES = [('E', '2026-12', '2026-11-20'), ('F', '2027-01', '2026-12-21'), ('G', '2027-02', '2027-01-20'),
           ('H', '2027-03', '2027-02-22'), ('J', '2027-04', '2027-03-22')]
BUSINESS_DAYS = ['2026-12-15', '2026-12-16', '2026-12-17', '2026-12-18', '2027-01-18', '2027-01-19', '2027-01-20', '2027-01-21', '2027-01-22']
# Context closes at 12:00 UTC: the marks of every live future and the 2027-01 differential.
CLOSES = {'2026-12-01': {'F': '70.00', 'G': '69.40', 'H': '68.80', 'J': '68.20'},
          '2026-12-22': {'G': '69.40', 'H': '68.80', 'J': '68.20'},
          '2027-01-20': {'G': '69.30', 'H': '68.85', 'J': '68.25'},
          '2027-01-25': {'H': '68.60', 'J': '68.10'}}
DIFFERENTIAL_MARK = '-1.50'
# Settlements read as history at 12:00 on observation days that are no context date.
HISTORY = {'2026-12-15': {'F': '70.00', 'G': '69.40', 'H': '68.80'}, '2026-12-16': {'F': '70.30', 'G': '69.60', 'H': '68.90'},
           '2026-12-17': {'F': '69.90', 'G': '69.40', 'H': '68.80'}, '2026-12-18': {'F': '70.20', 'G': '69.60', 'H': '69.10'},
           '2027-01-18': {'G': '69.60'}, '2027-01-21': {'H': '68.90'}, '2027-01-22': {'H': '68.70'}}
LATE_SETTLEMENT = ('2027-01-19', 'G', '69.50')


def key(kind, n):
    return f'c7c7{kind}000-0000-0000-0000-{n:012d}'


CONTRACT = {code: key('1', n) for n, code in enumerate([f[0] for f in FUTURES] + ['D'])}
SERIES = {code: key('2', n) for n, code in enumerate([f[0] for f in FUTURES] + ['D'])}


def observation(oid, contract, day, value, role='daily_settlement'):
    """Receipt time, audit metadata only, is the observed instant: the observations table requires observed_at <= received_at,
    and these instants need not precede the current time. Knowledge comes from the committing transaction, never from this column."""
    return (f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{SERIES[contract]}',{R},'fixture',"
            f"'synthetic_crude_{contract}_{day}','1','{role}','final','{day}T12:00:00Z','{day}T12:00:00Z',{value},NULL);")


def prepare(market, manifest):
    """Add the synthetic crude definitions to the primary bundle, extend its effective period through these contexts and
    add one 12:00 close per context date."""
    refs = [f"INSERT INTO ref_data.commodities VALUES({R},'synthetic_crude','Synthetic crude oil','volume');",
            f"INSERT INTO ref_data.commodity_units VALUES({R},'synthetic_crude','petroleum_barrel','volume');",
            f"INSERT INTO ref_data.measurement_bases VALUES({R},'synthetic_crude_standard','synthetic_crude','volume');",
            f"INSERT INTO ref_data.quantity_specifications VALUES({R},'synthetic_crude_quantity','synthetic_crude','synthetic_crude_standard','petroleum_barrel','volume');",
            f"INSERT INTO ref_data.quote_specifications VALUES({R},'synthetic_crude_outright','synthetic_crude','outright','synthetic_crude_quantity','usd',1,0.01,0.01,-1000,1000);",
            f"INSERT INTO ref_data.commodity_locations VALUES({R},'synthetic_crude','test_hub_a');",
            f"INSERT INTO ref_data.price_index_definitions VALUES({R},'synthetic_crude_differential_index','synthetic_crude','test_hub_a','usd','petroleum_barrel',1,'test_business');",
            f"INSERT INTO ref_data.settlement_targets VALUES({R},'synthetic_crude_differential_target','index','synthetic_crude_differential_index',NULL);",
            f"INSERT INTO ref_data.futures_products SELECT (jsonb_populate_record(NULL::ref_data.futures_products,to_jsonb(p)||'{{\"code\":\"{CURVE}\",\"name\":\"Synthetic crude future\","
            f"\"commodity_code\":\"synthetic_crude\",\"lot_unit_code\":\"petroleum_barrel\",\"quote_unit_code\":\"petroleum_barrel\",\"contract_size\":1000,\"outright_tick_size\":0.01}}'::jsonb)).* "
            f"FROM ref_data.futures_products p WHERE(p.catalog_id,p.revision_id,p.code)=({R},'test_gas_future');",
            f"INSERT INTO ref_data.futures_product_terms VALUES({R},'{CURVE}','test_uniform',NULL);",
            # Every day is closed but the nine stated business days.
            f"INSERT INTO ref_data.calendars VALUES({R},'{CALENDAR}','test_utc',127,'2026-11-01','2027-04-01');",
            *(f"INSERT INTO ref_data.calendar_days VALUES({R},'{CALENDAR}','{day}',true,'Synthetic observation day');" for day in BUSINESS_DAYS)]
    definitions = [f"INSERT INTO market_data.curves VALUES({M},'{CURVE}',{R},'synthetic_crude','test_hub_a','synthetic_crude_outright','test_uniform','test_future_target','direct','fixture','daily_settlement','Synthetic crude futures');",
                   f"INSERT INTO market_data.curves VALUES({M},'{DIFFERENTIAL}',{R},'synthetic_crude','test_hub_a','synthetic_crude_outright','test_uniform','synthetic_crude_differential_target','direct','fixture','trader_mark','Synthetic crude differential');"]
    for ordinal, (code, month, last) in enumerate(FUTURES):
        year, number = map(int, month.split('-'))
        start, end = f'{month}-01', f'{year + number // 12}-{number % 12 + 1:02d}-01'
        series = f'synthetic_crude_{year}{number:02d}'
        refs.extend([f"INSERT INTO ref_data.futures_series VALUES({R},'{series}','{CURVE}','{month}','synthetic','{start}','{end}');",
                     f"INSERT INTO ref_data.futures_milestones VALUES({R},'{series}','first_trading','known','2026-01-02','2026-01-02 00:00:00+00','test_utc','fixture');",
                     f"INSERT INTO ref_data.futures_milestones VALUES({R},'{series}','last_trading','known','{last}','{last} 19:30:00+00','test_utc','fixture');",
                     f"INSERT INTO ref_data.futures_execution_policies(catalog_id,revision_id,series_code,implementation,eligibility_rule,eligible_from,source_date_offset_seconds,source_date_tzdb_version,price_role,price_source_code,evidence_source_code) "
                     f"VALUES({R},'{series}','contract_lot_execution_pnl_v2','source_first_trade_instant_v1','2026-01-02 00:00:00+00',NULL,NULL,'daily_settlement','fixture','fixture');"])
        definitions.extend([f"INSERT INTO market_data.contracts VALUES({M},'{CONTRACT[code]}',{R},'future','{start}','{end}','{last} 19:30:00+00','{series}');",
                            f"INSERT INTO market_data.series VALUES({M},'{SERIES[code]}','synthetic_crude_future_{code.lower()}',{R},'{CONTRACT[code]}','synthetic_crude_outright',NULL);",
                            f"INSERT INTO market_data.curve_members VALUES({M},'{CURVE}','{CONTRACT[code]}',{ordinal});"])
    # The differential is one monthly forward for 2027-01, live through its delivery month.
    definitions.extend([f"INSERT INTO market_data.contracts VALUES({M},'{CONTRACT['D']}',{R},'forward','2027-01-01','2027-02-01','2027-02-01 00:00:00+00',NULL);",
                        f"INSERT INTO market_data.series VALUES({M},'{SERIES['D']}','synthetic_crude_differential_2027_01',{R},'{CONTRACT['D']}','synthetic_crude_outright',NULL);",
                        f"INSERT INTO market_data.curve_members VALUES({M},'{DIFFERENTIAL}','{CONTRACT['D']}',0);"])
    observations, n = [], 0
    for day, marks in HISTORY.items():
        for contract, value in marks.items():
            observations.append(observation(key('3', n), contract, day, value)); n += 1
    frames = []
    for day, marks in CLOSES.items():
        pins = []
        for contract, value in marks.items():
            pins.append(key('3', n)); observations.append(observation(pins[-1], contract, day, value)); n += 1
        pins.append(key('3', n)); observations.append(observation(pins[-1], 'D', day, DIFFERENTIAL_MARK, 'trader_mark')); n += 1
        frames.append({'at': f'{day}T12:00:00Z', 'observationIds': pins, 'frozenObservationIds': [],
                       'curveContracts': {CURVE: [CONTRACT[c] for c in marks], DIFFERENTIAL: [CONTRACT['D']]}})
    period = f"INSERT INTO market_data.revision_periods VALUES ({M},'2020-01-01','2022-01-01');"
    assert market.count(period) == 1
    market = market.replace(period, period.replace("'2022-01-01'", "'2027-05-01'"))
    market = market.replace('UPDATE ref_data.revisions SET', '\n'.join(refs) + '\nUPDATE ref_data.revisions SET', 1)
    market = market.replace('UPDATE market_data.revisions SET', '\n'.join(definitions) + '\nUPDATE market_data.revisions SET', 1)
    market = market.rsplit('COMMIT;', 1)[0] + '\n'.join(observations) + '\nCOMMIT;\n'
    return market, {**manifest, 'frames': sorted(manifest['frames'] + frames, key=lambda f: f['at'])}
