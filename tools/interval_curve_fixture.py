"""Original synthetic interval-curve data for --interval-curves-check; no market or exchange claims."""
from copy import deepcopy
from datetime import date, datetime, timedelta, timezone
from decimal import Decimal
from zoneinfo import ZoneInfo

from preview_fixture import CAT, REV, R, M, TENANT, WORKSPACE, TOU_PRODUCTS, identity

MARKED = 'test_power_hourly_marked'
PEAK = 'test_power_peak_monthly'
OFF = 'test_power_offpeak_monthly'
SHAPED = 'test_power_hourly_shaped'
SCALED = 'test_power_hourly_scaled'
FLAT = 'test_power_flat_monthly'
UNCOVERED = 'test_power_hourly_uncovered'  # Its block's January 2032 lies outside the holiday calendar, so it cannot resolve.
HOURLY_PROFILE = 'test_hourly_eastern'
HOLIDAYS = 'synthetic_eastern_holidays'
FACTOR = 'test_power_shape_factor'
# Operating-day spans of the marked curve, in ordinal order: 2 and 3 November 2031 (25 + 24 hours on the New York clock;
# 01:00 on the 2nd repeats), then 14 and 15 December 2020, delivered before and during the first fixture frame.
OPERATING_DAYS = [(date(2031, 11, 2), date(2031, 11, 4)), (date(2020, 12, 14), date(2020, 12, 16))]
MARKED_AT = '2020-11-01T16:00:00Z'  # Earlier than every fixture frame, so each frame selects the marks.
MONTHS = [(2031, 3), (2031, 11), (2031, 12), (2020, 12)]
BLOCK_MARKS = {PEAK: [55, 60, 70, 58], OFF: [35, 40, 45, 38]}
EDITOR = identity('c', 2)  # The synthetic editor defines the shape series.


def tou_product(code):
    return identity('d', next(n for n, product in enumerate(TOU_PRODUCTS, start=1) if product[0] == code))


def marked_hours():
    """Each hour of OPERATING_DAYS as (start UTC, end UTC, operating date, hour beginning, minute, occurrence)."""
    zone, hours, seen = ZoneInfo('America/New_York'), [], {}
    for span in OPERATING_DAYS:
        at, end = [datetime(d.year, d.month, d.day, tzinfo=zone).astimezone(timezone.utc) for d in span]
        while at < end:
            local = at.astimezone(zone)
            label = (local.date(), local.hour, local.minute)
            seen[label] = seen.get(label, 0) + 1
            hours.append((at, at + timedelta(hours=1), local.date(), local.hour, local.minute, seen[label]))
            at += timedelta(hours=1)
    return hours


def mark(ordinal):
    return Decimal(40) + Decimal(ordinal) / 4


def block_contracts():
    """{block curve: [(contract id, series id, observation id, first day, mark)]} for March, November and December 2031."""
    blocks = {}
    for b, code in enumerate([PEAK, OFF]):
        blocks[code] = [(identity('d', 740000 + 10 * b + m), identity('d', 741000 + 10 * b + m), identity('d', 742000 + 10 * b + m),
                         date(year, month, 1), BLOCK_MARKS[code][m]) for m, (year, month) in enumerate(MONTHS)]
    return blocks


def next_month(day):
    return date(day.year + day.month // 12, day.month % 12 + 1, 1)


def moved(security):
    """Split the security fixture: the tenant, workspace and editor identity the shape series need, and the rest."""
    first = (f"INSERT INTO security.tenants VALUES ('{TENANT}'", f"INSERT INTO security.workspaces VALUES ('{TENANT}'",
             f"INSERT INTO security.principals VALUES ('{EDITOR}'", "INSERT INTO security.identity_bindings VALUES ('https://preview.invalid','editor'",
             f"INSERT INTO security.workspace_memberships VALUES ('{TENANT}','{WORKSPACE}','{EDITOR}'")
    lines = security.split('\n')
    early = [line for line in lines if line.startswith(first)]
    if len(early) != len(first):
        raise ValueError('The security fixture must define the tenant, workspace and editor exactly once')
    return '\n'.join(early), '\n'.join(line for line in lines if line not in early)


def prepare(reference, market, security, manifest):
    manifest = deepcopy(manifest)
    refs = [f"INSERT INTO ref_data.calendars VALUES({R},'{HOLIDAYS}','synthetic_eastern',96,'2020-01-01','2032-01-01');",
            f"INSERT INTO ref_data.calendar_days VALUES({R},'{HOLIDAYS}','2031-11-27',false,'Synthetic weekday holiday');",
            f"INSERT INTO ref_data.delivery_profiles VALUES({R},'{HOURLY_PROFILE}','uniform','synthetic_eastern','{HOLIDAYS}','per_delivery_hour',60);",
            f"INSERT INTO ref_data.value_specifications VALUES({R},'{FACTOR}','dimensionless',NULL,NULL,0,10);"]
    refs += [f"INSERT INTO ref_data.commodity_units VALUES({R},'test_power','megawatt','power');",
             f"INSERT INTO ref_data.measurement_bases VALUES({R},'test_capacity','test_power','power');",
             f"INSERT INTO ref_data.quantity_specifications VALUES({R},'test_capacity_quantity','test_power','test_capacity','megawatt','power');",
             f"INSERT INTO ref_data.quote_specifications VALUES({R},'test_capacity_quote','test_power','outright','test_capacity_quantity','usd',1,0.025,0.001,-1000,1000);",
             f"INSERT INTO ref_data.price_index_definitions VALUES({R},'test_capacity_index','test_power','test_hub_a','usd','megawatt',1,'test_business');",
             f"INSERT INTO ref_data.settlement_targets VALUES({R},'test_capacity_target','index','test_capacity_index',NULL);"]
    anchor_expiry=datetime.now(timezone.utc)-timedelta(days=2)
    now = datetime.now(timezone.utc).isoformat()
    shape, factor, source = identity('d', 750001), identity('d', 750002), identity('d', 760001)
    identities, security = moved(security)
    # Published TOU products move ahead of the definitions that bind blocks to them.
    tou = [line for line in market.split('\n') if line.startswith('INSERT INTO market_data.tou_product')]
    market = '\n'.join(line for line in market.split('\n') if line not in tou)
    definitions = tou + [
        identities,
        f"INSERT INTO market_data.interval_series VALUES('{CAT}','{shape}','test_power_hourly_shape','Synthetic hourly shape · offsets','additive_price_shape',"
        f"{R},'test_power_outright',NULL,NULL,'synthetic_eastern',60,'{EDITOR}','{TENANT}','{WORKSPACE}','{now}');",
        f"INSERT INTO market_data.interval_series VALUES('{CAT}','{factor}','test_power_hourly_factor','Synthetic hourly shape · factors','multiplicative_price_shape',"
        f"{R},NULL,'{FACTOR}',NULL,'synthetic_eastern',60,'{EDITOR}','{TENANT}','{WORKSPACE}','{now}');",
        f"INSERT INTO market_data.curves VALUES({M},'{MARKED}',{R},'test_power','test_hub_a','test_power_outright',"
        f"'{HOURLY_PROFILE}','test_power_target','direct','fixture','trader_mark','Synthetic hourly power · marked');"]
    observations, contracts, ids, ends = [], [], [], []
    for ordinal, (start, end, operating, hour, minute, occurrence) in enumerate(marked_hours()):
        cid, sid, oid = identity('d', 710000 + ordinal), identity('d', 720000 + ordinal), identity('d', 730000 + ordinal)
        definitions += [
            f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'forward','{operating}','{operating + timedelta(days=1)}','{end.isoformat()}',NULL);",
            f"INSERT INTO market_data.contract_intervals VALUES({M},'{cid}','{start.isoformat()}','{end.isoformat()}','{operating}',{hour},{minute},{occurrence});",
            f"INSERT INTO market_data.series VALUES({M},'{sid}','interval_marked_{ordinal}',{R},'{cid}','test_power_outright',NULL);",
            f"INSERT INTO market_data.curve_members VALUES({M},'{MARKED}','{cid}',{ordinal});"]
        observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{sid}',{R},'fixture',"
                            f"'interval_marked_{ordinal}','1','trader_mark','final','{MARKED_AT}','{now}',{mark(ordinal)},NULL);")
        contracts.append(cid)
        ids.append(oid)
        ends.append(end)
    # The marked curve's collected actuals come from one synthetic day-ahead source, due an hour after each interval ends.
    definitions += [
        f"INSERT INTO power_data.sources(id,catalog_id,name,provider,dataset,market,point,minutes,reference_catalog_id,reference_revision_id,"
        f"location_code,clock_code,currency_code,quantity_code) SELECT '{source}',c.catalog_id,'Synthetic PJM hourly source','PJM','da_hrl_lmps','DA',"
        f"'synthetic_hourly',60,c.reference_catalog_id,c.reference_revision_id,c.location_code,'synthetic_eastern',q.currency_code,q.quantity_code "
        f"FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.code)=({M},'{MARKED}');",
        f"INSERT INTO market_data.curve_physical_price_sources VALUES({M},'{MARKED}',{R},'{source}');",
        f"INSERT INTO market_data.curve_actual_rules VALUES({M},'{MARKED}','interval_end',60,'interval_actual_due_v1');"]
    blocks, block_ids = block_contracts(), []
    for code, membership, name in [(PEAK, 'peak', 'Synthetic power · monthly peak'), (OFF, 'off', 'Synthetic power · monthly off-peak')]:
        definitions.append(f"INSERT INTO market_data.curves VALUES({M},'{code}',{R},'test_power','test_hub_a','test_power_outright',"
                           f"'{HOURLY_PROFILE}','test_power_target','direct','fixture','trader_mark','{name}');")
        for ordinal, (cid, sid, oid, first, value) in enumerate(blocks[code]):
            expiry = anchor_expiry if first==date(2031,3,1) else datetime(first.year, first.month, first.day, tzinfo=timezone.utc) - timedelta(days=2)
            definitions += [
                f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'forward','{first}','{next_month(first)}','{expiry.isoformat()}',NULL);",
                f"INSERT INTO market_data.series VALUES({M},'{sid}','{code}_{first:%Y_%m}',{R},'{cid}','test_power_outright',NULL);",
                f"INSERT INTO market_data.curve_members VALUES({M},'{code}','{cid}',{ordinal});"]
            observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{sid}',{R},'fixture',"
                                f"'{code}_{first:%Y_%m}','1','trader_mark','final','{MARKED_AT}','{now}',{value},NULL);")
            block_ids.append(oid)
        definitions.append(f"INSERT INTO market_data.curve_expiry_rules VALUES({M},'{code}','freeze_final_settlement_v1');")
        definitions.append(f"INSERT INTO market_data.curve_blocks VALUES({M},'{code}','{membership}','{tou_product('pjm')}',1);")
    for code, name, method, series in [(SHAPED, 'Synthetic hourly power · shaped offsets', 'additive', shape),
                                       (SCALED, 'Synthetic hourly power · shaped factors', 'multiplicative', factor)]:
        definitions += [f"INSERT INTO market_data.shaped_curves VALUES({M},'{code}','{name}','{method}','duration_weighted_block_mean_v1','block_shape_v1','{series}');",
                        f"INSERT INTO market_data.shaped_curve_blocks VALUES({M},'{code}','{PEAK}','period');",
                        f"INSERT INTO market_data.shaped_curve_blocks VALUES({M},'{code}','{OFF}','period');"]
    definitions.append(f"INSERT INTO market_data.shaped_curve_actual_rules VALUES({M},'{SHAPED}',{R},'{source}','interval_end',60,'interval_actual_due_v1');")
    # Explicit current-day-through-month-end live anchors. No final settlement is
    # manufactured for a month that has not completed at this frame's valuation.
    balances={}
    for n,(period,membership,value) in enumerate([(PEAK,'peak',62),(OFF,'off',42)]):
        code=period.replace('monthly','balance')
        cid,sid,oid=(identity('d',744000+n*10+i) for i in range(3))
        definitions += [
            f"INSERT INTO market_data.curves VALUES({M},'{code}',{R},'test_power','test_hub_a','test_power_outright','{HOURLY_PROFILE}','test_power_target','direct','fixture','trader_mark','Synthetic remaining December {membership}');",
            f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'forward','2020-12-15','2021-01-01','2021-01-01T05:00:00Z',NULL);",
            f"INSERT INTO market_data.series VALUES({M},'{sid}','{code}_2020_12',{R},'{cid}','test_power_outright',NULL);",
            f"INSERT INTO market_data.curve_members VALUES({M},'{code}','{cid}',0);",
            f"INSERT INTO market_data.curve_blocks VALUES({M},'{code}','{membership}','{tou_product('pjm')}',1);",
            f"INSERT INTO market_data.shaped_curve_blocks VALUES({M},'{SHAPED}','{code}','balance');",
            f"INSERT INTO market_data.curve_expiry_rules VALUES({M},'{code}','freeze_final_settlement_v1');"]
        observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{sid}',{R},'fixture','{code}_2020_12','1','trader_mark','final','{MARKED_AT}','{now}',{value},NULL);")
        balances[membership]={'curve':code,'contractId':cid,'seriesId':sid,'observationId':oid,'mark':value}
    flat = identity('d', 743000)
    definitions += [
        f"INSERT INTO market_data.curves VALUES({M},'{FLAT}',{R},'test_power','test_hub_a','test_power_outright',"
        f"'{HOURLY_PROFILE}','test_power_target','direct','fixture','trader_mark','Synthetic power · monthly all hours');",
        f"INSERT INTO market_data.contracts VALUES({M},'{flat}',{R},'forward','2032-01-01','2032-02-01','2031-12-30T00:00:00+00:00',NULL);",
        f"INSERT INTO market_data.series VALUES({M},'{identity('d', 743001)}','{FLAT}_2032_01',{R},'{flat}','test_power_outright',NULL);",
        f"INSERT INTO market_data.curve_members VALUES({M},'{FLAT}','{flat}',0);",
        f"INSERT INTO market_data.curve_blocks VALUES({M},'{FLAT}','all',NULL,NULL);",
        f"INSERT INTO market_data.shaped_curves VALUES({M},'{UNCOVERED}','Synthetic hourly power · uncovered','additive','duration_weighted_block_mean_v1','block_shape_v1','{shape}');",
        f"INSERT INTO market_data.shaped_curve_blocks VALUES({M},'{UNCOVERED}','{FLAT}','period');"]
    capacity='test_capacity_period'
    cap_contract,cap_series,cap_mark,cap_fixing=(identity('d',770000+i) for i in range(4))
    definitions += [
        f"INSERT INTO market_data.curves VALUES({M},'{capacity}',{R},'test_power','test_hub_a','test_capacity_quote','test_uniform','test_capacity_target','direct','fixture','trader_mark','Synthetic capacity period');",
        f"INSERT INTO market_data.contracts VALUES({M},'{cap_contract}',{R},'forward','2021-01-01','2021-02-01','2020-12-16T12:00:00Z',NULL);",
        f"INSERT INTO market_data.series VALUES({M},'{cap_series}','test_capacity_january',{R},'{cap_contract}','test_capacity_quote',NULL);",
        f"INSERT INTO market_data.curve_members VALUES({M},'{capacity}','{cap_contract}',0);",
        f"INSERT INTO market_data.index_fixing_observations VALUES({M},'test_capacity_index','2021-01-01','2021-02-01',{R},'test_capacity_quote','{cap_series}','fixture','2020-12-16T12:00:00Z','published_index_observations_v1');"]
    for oid,role,observed,value in [(cap_mark,'trader_mark',MARKED_AT,5),(cap_fixing,'index_fixing','2020-12-16T12:00:00Z',4)]:
        observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{cap_series}',{R},'fixture','capacity_{role}','1','{role}','final','{observed}','{now}',{value},NULL);")
    # As a close would, each frame selects and pins only the intervals still live at its instant.
    for frame in manifest['frames']:
        live = [n for n, end in enumerate(ends) if end > datetime.fromisoformat(frame['at'].replace('Z', '+00:00'))]
        frame['observationIds'] = frame['observationIds'] + [ids[n] for n in live] + block_ids + [b['observationId'] for b in balances.values()]
        views = frame.setdefault('curveContracts', {})
        views[MARKED] = [contracts[n] for n in live]
        capacity_fixed=datetime.fromisoformat(frame['at'].replace('Z','+00:00'))>=datetime(2020,12,16,12,tzinfo=timezone.utc)
        frame['observationIds'] += [cap_fixing if capacity_fixed else cap_mark]
        views[capacity]=[] if capacity_fixed else [cap_contract]
        for balance in balances.values():
            views[balance['curve']]=[balance['contractId']]
        for code in (PEAK, OFF):
            views[code] = [cid for cid, *_ in blocks[code]]
    missing_capacity=deepcopy(next(f for f in manifest['frames'] if cap_fixing in f['observationIds']))
    missing_capacity['at']=(datetime.fromisoformat(missing_capacity['at'].replace('Z','+00:00'))+timedelta(seconds=1)).isoformat()
    missing_capacity['observationIds'].remove(cap_fixing)
    manifest['frames'].append(missing_capacity)
    # Synthetic pre-delivery contract finals, explicitly separate from a completed delivery-month index.
    # HTTP checks publish these after the initial shape cutoff to qualify knowledge exclusion too.
    frozen_frame=deepcopy(manifest['frames'][0])
    frozen_frame['at']=(datetime.now(timezone.utc)+timedelta(minutes=2)).isoformat()
    frozen=[]
    for n,code in enumerate((PEAK,OFF)):
        cid,sid,old,first,value=blocks[code][0]
        oid=identity('d',780000+n)
        frozen_frame['observationIds'].remove(old)
        frozen_frame['observationIds'].append(oid)
        frozen.append({'id':oid,'seriesId':sid,'observedAt':(anchor_expiry+timedelta(days=1)).isoformat(),'value':58 if n==0 else 38})
    frozen_frame['frozenObservationIds']=[f['id'] for f in frozen]
    manifest['frames'].append(frozen_frame)
    market = market.replace('UPDATE ref_data.revisions SET', '\n'.join(refs) + '\nUPDATE ref_data.revisions SET', 1)
    market = market.replace('UPDATE market_data.revisions SET', '\n'.join(definitions) + '\nUPDATE market_data.revisions SET', 1)
    market = market.rsplit('COMMIT;', 1)[0] + '\n' + '\n'.join(observations) + '\nCOMMIT;\n'
    manifest['intervalCurves'] = {'marked': MARKED, 'contracts': contracts, 'observations': ids, 'source': source,
                                  'shaped': SHAPED, 'scaled': SCALED, 'uncovered': UNCOVERED, 'peak': PEAK, 'off': OFF,
                                  'shapeSeries': {'additive': shape, 'multiplicative': factor}, 'balances':balances, 'capacity':capacity, 'capacityFixing':cap_fixing, 'missingCapacityAt':missing_capacity['at'], 'frozenAt':frozen_frame['at'], 'frozenObservations':frozen,
                                  'blocks': {code: [{'contractId': cid, 'seriesId': sid, 'observationId': oid, 'month': f'{first:%Y-%m}', 'mark': value}
                                                    for cid, sid, oid, first, value in blocks[code]] for code in (PEAK, OFF)},
                                  'pjm': tou_product('pjm'), 'nyiso': tou_product('nyiso'), 'spp': tou_product('spp')}
    return reference, market, security, manifest
