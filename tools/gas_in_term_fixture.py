"""The in-term gas demo as synthetic market data: a hub location whose daily index fixes from
September through November 9 2026, priced at the Friday 6 November 21:00 UTC close from daily hub plus basis
marks through November and one monthly hub plus basis point for December. Conventions are synthetic."""
from datetime import date, timedelta
from decimal import Decimal
from uuid import uuid4
from preview_fixture import CAT, REV, R, M, TENANT, WORKSPACE, identity, trade_booking

POINT = 'synthetic_gas_point_hub'
LOCATION = 'synthetic_hub_location'
SOURCES = ('synthetic_hub_daily', 'synthetic_hub_daily_basis', 'synthetic_hub_monthly', 'synthetic_hub_monthly_basis')
CLOSE = '2026-11-06T21:00:00Z'
TERM = (date(2026, 9, 1), date(2027, 1, 1))
WEEKEND = (date(2026, 11, 7), date(2026, 11, 10))
# The administrator who loads the in-term trade holds the authority a ticket booking requires (the trader role) beside
# access administration; a second, disabled one is refused, and a third books trades without reading market data.
# All are synthetic identities of the preview issuer.
ADMINISTRATOR, DISABLED_ADMINISTRATOR = ('in_term_administrator', identity('c', 90)), ('in_term_disabled_administrator', identity('c', 91))
BOOK_ONLY_ADMINISTRATOR = ('in_term_book_only_administrator', identity('c', 92))
BOOK_ONLY_ROLE = identity('c', 93)
# The gas day starts 09:00 at a fixed UTC-6 synthetic central clock; publication is 12:00 there.
DAY_START_MINUTE, OFFSET_SECONDS = 540, -21600


def published_at(day):
    """The last weekday before the gas day: Friday's publication prices Saturday, Sunday and Monday."""
    before = day - timedelta(days=1)
    while before.weekday() >= 5:
        before -= timedelta(days=1)
    return f'{before}T18:00:00Z'


def fixing(day):
    """Synthetic final daily index values that reproduce the demo's cash by month."""
    if day.month == 9: return Decimal('2.90')
    if day.month == 10 or day <= date(2026, 11, 2): return Decimal('3.00')
    return {3: Decimal('3.17'), 4: Decimal('3.19'), 5: Decimal('3.19'), 6: Decimal('3.18')}.get(day.day, Decimal('3.22'))


def marks():
    """Hub and basis marks at the close as (source, start, end, hub, basis); hub plus basis is the location price.
    The daily branch covers the whole daily term, since the published curve is evaluated over its complete
    shape; an ended gas day is marked at its own fixing and is never demanded by the started lifecycle."""
    basis = Decimal('0.08')
    def hub(day):
        if day < date(2026, 11, 6): return fixing(day) - basis
        return {date(2026, 11, 6): Decimal('3.10'), WEEKEND[0]: Decimal('3.14'), date(2026, 11, 30): Decimal('3.21')}.get(day, Decimal('3.16'))
    daily, day = [], TERM[0]
    while day < date(2026, 12, 1):
        end = WEEKEND[1] if day == WEEKEND[0] else day + timedelta(days=1)
        daily.append((0, day, end, hub(day), basis)); day = end
    return daily + [(2, date(2026, 12, 1), date(2027, 1, 1), Decimal('3.40'), Decimal('0.12'))]


def add_fixture(reference, market, security, manifest):
    for frame in manifest['frames']:
        frame['observationIds'] = list(frame['observationIds'])
    refs = [f"INSERT INTO ref_data.clock_rules(catalog_id,revision_id,code,kind,fixed_offset_seconds) VALUES({R},'synthetic_fixed_central','fixed_offset',{OFFSET_SECONDS});",
            f"INSERT INTO ref_data.locations(catalog_id,revision_id,code,name,type_code,clock_code) VALUES({R},'{POINT}','Synthetic hub delivery point','test_delivery_point','test_utc');",
            f"INSERT INTO ref_data.commodity_locations VALUES({R},'test_gas','{POINT}');",
            f"INSERT INTO ref_data.pipeline_delivery_points VALUES({R},'test_pipeline_a','synthetic_gas_zone_a','{POINT}');",
            f"INSERT INTO ref_data.gas_delivery_conventions VALUES({R},'test_pipeline_a','synthetic_gas_zone_a','{POINT}','test_gas','synthetic_fixed_central',{DAY_START_MINUTE});"]
    curve = lambda code, name: f"INSERT INTO market_data.curves VALUES({M},'{code}',{R},'test_gas','test_hub_a','test_gas_outright','test_uniform','test_index_target','direct','fixture','trader_mark','{name}');"
    definitions = [curve(LOCATION, 'Synthetic hub location index'), curve(SOURCES[0], 'Synthetic daily hub'), curve(SOURCES[1], 'Synthetic daily hub basis'),
                   curve(SOURCES[2], 'Synthetic monthly hub'), curve(SOURCES[3], 'Synthetic monthly hub basis'),
                   f"INSERT INTO market_data.gas_valuation_groups VALUES({M},'synthetic_hub_group',{R},'test_pipeline_a','test_gas','{LOCATION}',0);",
                   f"INSERT INTO market_data.gas_valuation_assignments(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code,product_code,curve_code,group_code,group_adjustment,location_adjustment) VALUES({M},'synthetic_hub_assignment',{R},'test_pipeline_a','synthetic_gas_zone_a','{POINT}','test_gas','{LOCATION}','synthetic_hub_group',0,0);"]
    ordinals = {}
    # Source contracts stay live through the term, so a mapped past gas day keeps its exact qualified shape.
    def contract(code, start, end, n):
        ordinal = ordinals[code] = ordinals.get(code, -1) + 1
        cid, sid = identity('5', 90000 + n), identity('9', 90000 + n)
        expiry = TERM[1] if code in SOURCES else end
        definitions.extend([f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'forward','{start}','{end}','{expiry}T00:00:00Z',NULL);",
                            f"INSERT INTO market_data.curve_members VALUES({M},'{code}','{cid}',{ordinal});",
                            f"INSERT INTO market_data.series VALUES({M},'{sid}','synthetic_in_term_{n}',{R},'{cid}','test_gas_outright',NULL);"])
        return sid
    def fixing_definition(sid, start, end, at):
        definitions.append(f"INSERT INTO market_data.index_fixing_observations(catalog_id,revision_id,index_code,observation_from,observation_to,reference_catalog_id,reference_revision_id,quote_code,series_id,source_code,fixing_at,implementation) VALUES({M},'test_price','{start}','{end}',{R},'test_gas_outright','{sid}','fixture','{at}','published_index_observations_v1');")
    observations, pins = [], []
    def observe(n, sid, role, at, value):
        oid = identity('7', 9000000 + n)
        pins.append(oid)
        observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{sid}',{R},'fixture','synthetic_in_term_{n}','1','{role}','final','{at}',CURRENT_TIMESTAMP,{value},NULL);")
    # Location index: monthly members for the physical assignment, published after each month, and one daily
    # index contract per gas day as the payment's published-fixing identity. Only its fixings are observed.
    n = 0
    for month in range(9, 13):
        start = date(2026, month, 1); end = date(2027, 1, 1) if month == 12 else date(2026, month + 1, 1)
        after = end
        while after.weekday() >= 5: after += timedelta(days=1)
        sid = contract(LOCATION, start, end, n); at = f'{after}T18:00:00Z'; fixing_definition(sid, start, end, at)
        if at <= CLOSE: observe(n, sid, 'index_fixing', at, fixing(start))
        n += 1
    day = TERM[0]
    while day < TERM[1]:
        sid = contract(LOCATION, day, day + timedelta(days=1), n); at = published_at(day)
        fixing_definition(sid, day, day + timedelta(days=1), at)
        if at <= CLOSE: observe(n, sid, 'index_fixing', at, fixing(day))
        day += timedelta(days=1); n += 1
    for source, start, end, hub, basis in marks():
        for offset, value in ((0, hub), (1, basis)):
            sid = contract(SOURCES[source + offset], start, end, n); observe(n, sid, 'trader_mark', CLOSE, value); n += 1
    # Upgrade probes load this fixture before physical setup tables exist.
    guarded = lambda table, sql: f"DO $fixture$ BEGIN IF to_regclass('{table}') IS NOT NULL THEN {sql} END IF;END$fixture$;"
    refs[3:] = [guarded(t, s) for t, s in zip(('ref_data.pipeline_delivery_points', 'ref_data.gas_delivery_conventions'), refs[3:])]
    definitions[5:7] = [guarded(t, s) for t, s in zip(('market_data.gas_valuation_groups', 'market_data.gas_valuation_assignments'), definitions[5:7])]
    # The definitions stay effective through the demo term.
    period = f"INSERT INTO market_data.revision_periods VALUES ({M},'2020-01-01','2022-01-01');"
    assert market.count(period) == 1
    market = market.replace(period, period.replace("'2022-01-01'", "'2027-02-01'"))
    market = market.replace('UPDATE ref_data.revisions SET', '\n'.join(refs) + '\nUPDATE ref_data.revisions SET', 1)
    market = market.replace('UPDATE market_data.revisions SET', '\n'.join(definitions) + '\nUPDATE market_data.revisions SET', 1)
    market = market.rsplit('COMMIT;', 1)[0] + '\n'.join(observations) + '\nCOMMIT;\n'
    security += f"""
INSERT INTO security.roles(id,code,name) VALUES('{BOOK_ONLY_ROLE}','synthetic_in_term_book_only','Synthetic booking without market reads');
INSERT INTO security.role_permissions SELECT '{BOOK_ONLY_ROLE}',p.permission FROM security.role_permissions p JOIN security.roles r ON r.id=p.role_id
 WHERE r.code='trader' AND p.permission<>'ReadMarketData';"""
    for (login, principal), enabled, roles in ((ADMINISTRATOR, 'true', "'trader','access_administrator'"), (DISABLED_ADMINISTRATOR, 'false', "'trader','access_administrator'"),
                                               (BOOK_ONLY_ADMINISTRATOR, 'true', "'synthetic_in_term_book_only'")):
        security += f"""
INSERT INTO security.principals VALUES('{principal}','human',{enabled});
INSERT INTO security.identity_bindings VALUES('https://preview.invalid','{login}','{principal}','human_interactive');
INSERT INTO security.workspace_memberships VALUES('{TENANT}','{WORKSPACE}','{principal}','{login}','Synthetic {login.replace('_', ' ')}',true,1);
INSERT INTO security.role_grants SELECT gen_random_uuid(),'{principal}',id,'{TENANT}','{WORKSPACE}',CURRENT_TIMESTAMP-INTERVAL '1 day',CURRENT_TIMESTAMP+INTERVAL '1 day',false
 FROM security.roles WHERE code IN({roles});"""
    standard = manifest['frames'][3]
    manifest['frames'].append({**standard, 'at': CLOSE, 'observationIds': standard['observationIds'] + pins})
    return reference, market, security, manifest


def custom_curves(publish, sources, catalog):
    """Publish the mixed location curve: daily hub plus basis through November, then one monthly December point."""
    def branch(code, name, hub, basis):
        return daily_branch(publish, sources, code, name, hub, basis)
    daily = branch('synthetic_hub_location_daily', 'Synthetic hub location daily', *SOURCES[:2])
    monthly = branch('synthetic_hub_location_monthly', 'Synthetic hub location monthly', *SOURCES[2:])
    published = lambda saved: {'id': None, 'kind': 'published_source', 'parameters': {'selection': 'all_output_points_v1'}, 'inputs': {},
                               'publishedSource': {'catalogId': catalog, 'curveId': saved['id'], 'version': saved['version'], 'outputNode': 'output'}}
    return publish({'code': 'synthetic_hub_location_mixed', 'name': 'Synthetic hub location, daily then monthly', 'output': 'output', 'nodes': [
        {**published(daily), 'id': 'before'}, {**published(monthly), 'id': 'after'},
        {'id': 'splice', 'kind': 'splice', 'parameters': {'from': '2026-09-01', 'cutover': '2026-12-01', 'to': '2027-01-01', 'implementation': 'whole_delivery_intervals_v1'}, 'inputs': {'before': 'before', 'after': 'after'}},
        {'id': 'output', 'kind': 'output', 'parameters': {}, 'inputs': {'values': 'splice'}}]})


def daily_branch(publish, sources, code, name, hub, basis, basis_weight='1', omit=None):
    """A location curve of hub plus basis_weight times basis per source contract pair; omit leaves out the pair starting that day."""
    pairs = [pair for pair in zip(sources[hub]['contracts'], sources[basis]['contracts']) if pair[0]['deliveryFrom'] != omit]
    weights = [{'outputPosition': i + 1, 'sourcePort': port, 'contractId': c['id'], 'weight': weight}
               for i, pair in enumerate(pairs) for port, c, weight in zip(('source_a', 'source_b'), pair, ('1', basis_weight))]
    return publish({'code': code, 'name': name, 'output': 'output', 'nodes': [
        {'id': 'hub', 'kind': 'source', 'parameters': {'curve': hub}, 'inputs': {}},
        {'id': 'basis', 'kind': 'source', 'parameters': {'curve': basis}, 'inputs': {}},
        {'id': 'mapping', 'kind': 'weighted_contracts', 'parameters': {'aggregation': 'signed_sum_v1'}, 'inputs': {'source_a': 'hub', 'source_b': 'basis'}, 'contractWeights': weights},
        {'id': 'dates', 'kind': 'dates', 'parameters': {'mode': 'context', 'sampling': 'context_utc_time', 'corrections': 'exact_context_frame_history_latest_visible'}, 'inputs': {}},
        {'id': 'sample', 'kind': 'sample', 'parameters': {}, 'inputs': {'selection': 'mapping', 'dates': 'dates'}},
        {'id': 'output', 'kind': 'output', 'parameters': {}, 'inputs': {'values': 'sample'}}]})


def premium_curve(publish, sources, omit=None):
    """A daily location curve at hub plus 3.25 times basis: the Nov 7-9 weekend strip marks 3.14 + 0.26 = 3.40 against its 3.22 fixing."""
    return daily_branch(publish, sources, 'synthetic_hub_premium' + ('_gap' if omit else ''), 'Synthetic hub premium location', *SOURCES[:2], '3.25', omit)


def trade(context, location, books, setup, curve):
    """Receive 100 MMBtu per gas day, 1 September to 31 December 2026; pay the daily index plus 0.00 on the 10th of the next month."""
    forecast = {'catalogId': CAT, 'curveId': curve['id'], 'version': curve['version'], 'outputNode': 'output', 'position': None,
                'selection': 'containing_delivery_interval_v1', 'implementation': 'published_custom_curve_projection_v1'}
    return {**context, **trade_booking(), 'operationId': str(uuid4()), 'bookId': books[0]['id'], 'definitionRevisionId': location['definitionRevisionId'],
            'gasSetupId': setup, 'discounting': False, 'profile': 'indexed_gas_composition_v1',
            'physicalSides': [dict(code='gas', pipeline=location['pipeline'], zone=location['zone'], deliveryPoint=location['deliveryPoint'], product=location['product'],
                                   direction='receive', deliveryFrom=str(TERM[0]), deliveryTo=str(TERM[1]), quantityBasis='per_operating_day', quantity='100', link=None, forecastCurve=forecast)],
            'cashSides': [dict(code='payment', role='indexed_consideration', direction='pay', source='gas', stage='delivery', price=None, tariffCode=None, paymentAt=None,
                               indexed=dict(index='test_price', observationConvention='daily_published_index_v1', paymentConvention='next_month_day_utc_v1',
                                            paymentDay=10, spread='0', forecastCurve=forecast))]}
