"""Original, explicitly synthetic market inputs for durable PPA HTTP qualification.

Definitions are inserted before sealing. Audited interval values are deliberately
absent: the checks publish them through the editor's authorized HTTP operation.
The long workloads match the managed baseline's November 2031 start and have no
holidays; their 60/40 blocks and 0.5 forecast admit an independent H + 10P oracle.
"""
from copy import deepcopy
from datetime import date, datetime, timezone

from preview_fixture import CAT, REV, R, M, TENANT, WORKSPACE, identity
from interval_curve_fixture import EDITOR, MARKED, tou_product, next_month

CALENDAR = 'test_ppa_long_calendar'
CERTIFICATE = 'test_ppa_long_certificates'
CAPACITY = 'test_ppa_capacity_days'
FIRST = date(2031, 11, 1)
LAST = date(2051, 11, 1)


def trading(catalog):
    """Two separately measured books, with the baseline's explicit portfolio."""
    return '\n'.join(f"INSERT INTO trading.books VALUES('{catalog}','d1000000-0000-0000-0000-{n:012}','d0000000-0000-0000-0000-000000000001','Synthetic PPA benchmark {n}',1,'2020-01-01T00:00:00Z');" for n in (3,4))


def prepare(reference, market, security, manifest):
    manifest = deepcopy(manifest)
    now = datetime.now(timezone.utc).isoformat()
    refs = [f"INSERT INTO ref_data.calendars VALUES({R},'{CALENDAR}','synthetic_eastern',96,'2020-01-01','2052-01-01');",
            f"INSERT INTO ref_data.calendars VALUES({R},'{CALENDAR}_central','synthetic_central',96,'2020-01-01','2052-01-01');",
            f"INSERT INTO ref_data.units VALUES({R},'megawatt_day','MW-day','energy');",
            f"INSERT INTO ref_data.commodity_units VALUES({R},'test_power','megawatt_day','energy');",
            f"INSERT INTO ref_data.measurement_bases VALUES({R},'test_ppa_capacity_days_basis','test_power','energy');",
            f"INSERT INTO ref_data.quantity_specifications VALUES({R},'test_ppa_capacity_days_quantity','test_power','test_ppa_capacity_days_basis','megawatt_day','energy');",
            f"INSERT INTO ref_data.quote_specifications VALUES({R},'test_ppa_capacity_days_quote','test_power','outright','test_ppa_capacity_days_quantity','usd',1,0.01,0.01,-1000,1000);",
            f"INSERT INTO ref_data.price_index_definitions VALUES({R},'test_ppa_capacity_days_index','test_power','test_hub_a','usd','megawatt_day',1,'test_business');",
            f"INSERT INTO ref_data.settlement_targets VALUES({R},'test_ppa_capacity_days_target','index','test_ppa_capacity_days_index',NULL);"]
    definitions, observations, contracts, pins = [], [], {}, []

    def period(code, profile, quote, target, rows, seed, membership=None, tou='pjm'):
        definitions.append(f"INSERT INTO market_data.curves VALUES({M},'{code}',{R},'test_power','test_hub_a','{quote}','{profile}','{target}','direct','fixture','trader_mark','Synthetic PPA {code}');")
        if membership:
            definitions.append(f"INSERT INTO market_data.curve_blocks VALUES({M},'{code}','{membership}','{tou_product(tou)}',1);")
            definitions.append(f"INSERT INTO market_data.curve_expiry_rules VALUES({M},'{code}','freeze_final_settlement_v1');")
        contracts[code] = []
        for n, (start, end, value) in enumerate(rows):
            cid, sid, oid = (identity('d', seed + n * 3 + k) for k in range(3))
            # The retained synthetic 2020 vintage has an explicitly live mark
            # through the January 2021 holding valuation; no expiry fallback.
            expiry = date(2022, 1, 1) if code == CERTIFICATE and start.year == 2020 else end
            contracts[code].append(cid); pins.append(oid)
            definitions.extend([
                f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'forward','{start}','{end}','{expiry}T00:00:00Z',NULL);",
                f"INSERT INTO market_data.series VALUES({M},'{sid}','{code}_{n}',{R},'{cid}','{quote}',NULL);",
                f"INSERT INTO market_data.curve_members VALUES({M},'{code}','{cid}',{n});"])
            observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{sid}',{R},'fixture','synthetic_ppa_{seed}_{n}','1','trader_mark','final','2020-11-01T16:00:00Z','{now}',{value},NULL);")
        return sid

    # Monthly certificate vintages match the baseline's exact contract count.
    vintage = [(date(2020, 12, 1), date(2021, 1, 1), 5)]
    month = FIRST
    while month < LAST:
        vintage.append((month, next_month(month), 5)); month = next_month(month)
    period(CERTIFICATE, 'test_uniform', 'test_rec_quote', 'test_rec_target', vintage, 900000)
    sid = period(CAPACITY, 'test_uniform', 'test_ppa_capacity_days_quote', 'test_ppa_capacity_days_target',
                 [(date(2031, 11, 1), date(2031, 12, 1), 4)], 901000)
    definitions.append(f"INSERT INTO market_data.index_fixing_observations VALUES({M},'test_ppa_capacity_days_index','2031-11-01','2031-12-01',{R},'test_ppa_capacity_days_quote','{sid}','fixture','2031-12-01T00:00:00Z','published_index_observations_v1');")
    workloads = {}
    for ordinal, minutes in enumerate((60, 15)):
        clock = 'synthetic_eastern' if minutes == 60 else 'synthetic_central'
        calendar = CALENDAR if minutes == 60 else CALENDAR + '_central'
        winter, summer = (5, 4) if minutes == 60 else (6, 5)
        provider, dataset, price_market = ('PJM', 'da_hrl_lmps', 'DA') if minutes == 60 else ('ERCOT', 'np6-905-cd/spp_node_zone_hub', 'RT')
        asset = f'test_ppa_long_asset_{minutes}'; location = f'test_ppa_long_node_{minutes}'
        profile = f'test_ppa_long_profile_{minutes}'; shaped = f'test_ppa_long_shaped_{minutes}'
        shape, forecast, meter, source = (identity('d', 902000 + ordinal * 10 + k) for k in range(4))
        refs.extend([f"INSERT INTO ref_data.locations(catalog_id,revision_id,code,name,type_code,clock_code) VALUES({R},'{location}','Synthetic PPA node {minutes}','test_hub','{clock}');",
                     f"INSERT INTO ref_data.commodity_locations VALUES({R},'test_power','{location}');",
                     f"INSERT INTO ref_data.delivery_profiles VALUES({R},'{profile}','uniform','{clock}','{calendar}','per_delivery_hour',{minutes});"])
        for series, kind, value, quantity in [(shape, 'additive_price_shape', 'NULL', 'NULL'),
                (forecast, 'capacity_factor_forecast', "'test_ppa_capacity_factor'", 'NULL'),
                (meter, 'metered_energy', 'NULL', "'test_power_quantity'")]:
            quote = "'test_power_outright'" if kind == 'additive_price_shape' else 'NULL'
            definitions.append(f"INSERT INTO market_data.interval_series VALUES('{CAT}','{series}','{asset}_{kind}','Synthetic PPA {minutes} {kind}','{kind}',{R},{quote},{value},{quantity},'{clock}',{minutes},'{EDITOR}','{TENANT}','{WORKSPACE}','{now}');")
        definitions.extend([
            f"INSERT INTO market_data.generation_assets VALUES({M},'{asset}','Synthetic PPA asset {minutes}',{R},'{location}','test_capacity_quantity','{clock}');",
            f"INSERT INTO market_data.generation_asset_versions VALUES({M},'{asset}','2020-01-01T{winter:02}:00:00Z','2052-01-01T{winter:02}:00:00Z',200,160);",
            f"INSERT INTO market_data.volume_sources(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,asset_code,clock_code,interval_minutes,forecast_series_id,forecast_stage,metered_series_id,metered_stage,loss,range_rule,availability_mode,availability,curtailment_mode,curtailment,degradation_mode,degradation,degradation_basis,degradation_base_date,meter_due_rule,meter_due_offset_minutes) VALUES({M},'{asset}',{R},'{asset}','{clock}',{minutes},'{forecast}','generated','{meter}','generated',0,'generation_nonnegative_v1','reflected',NULL,'reflected',NULL,'reflected',NULL,NULL,NULL,'meter_due_v1',129600);",
            f"INSERT INTO power_data.sources(id,catalog_id,name,provider,dataset,market,point,minutes,reference_catalog_id,reference_revision_id,location_code,clock_code,currency_code,quantity_code) VALUES('{source}','{CAT}','Synthetic PPA {minutes} prices','{provider}','{dataset}','{price_market}','synthetic_ppa_{minutes}',{minutes},{R},'test_hub_a','{clock}','usd','test_power_quantity');"])
        if minutes == 60:
            definitions.append(definitions[-2].replace(f"VALUES({M},'{asset}',",f"VALUES({M},'{asset}_deadline',",1).replace("129600);","60);"))
        for b, (membership, value) in enumerate((('peak', 60), ('off', 40))):
            block = f'test_ppa_long_{minutes}_{membership}'
            rows=[];month=FIRST
            while month < LAST:
                rows.append((month, next_month(month), value));month=next_month(month)
            period(block, profile, 'test_power_outright', 'test_power_target', rows, 910000 + ordinal * 10000 + b * 1000, membership, 'pjm' if minutes == 60 else 'spp')
            definitions.append(f"INSERT INTO market_data.shaped_curve_blocks VALUES({M},'{shaped}','{block}','period');")
        # The parent must precede its child rows.
        definitions.insert(next(i for i,x in enumerate(definitions) if f"VALUES({M},'{shaped}'," in x),
            f"INSERT INTO market_data.shaped_curves VALUES({M},'{shaped}','Synthetic PPA {minutes} shaped','additive','duration_weighted_block_mean_v1','block_shape_v1','{shape}');")
        definitions.extend([
            f"INSERT INTO market_data.shaped_curve_actual_rules VALUES({M},'{shaped}',{R},'{source}','interval_end',60,'interval_actual_due_v1');",
            f"INSERT INTO market_data.location_valuation_assignments VALUES({M},'{asset}_energy',{R},'{location}','energy','2031-11-01T{summer:02}:00:00Z','2051-11-01T{summer:02}:00:00Z',NULL,'{shaped}',0);",
            f"INSERT INTO market_data.location_valuation_assignments VALUES({M},'{asset}_certificate',{R},'{location}','certificate','2020-01-01T05:00:00Z','2052-01-01T05:00:00Z','{CERTIFICATE}',NULL,0);",
            f"INSERT INTO market_data.location_valuation_assignments VALUES({M},'{asset}_capacity',{R},'{location}','capacity','2031-11-01T04:00:00Z','2031-12-01T05:00:00Z','{CAPACITY}',NULL,0);"])
        if minutes == 60:
            definitions.append(f"INSERT INTO market_data.location_valuation_assignments VALUES({M},'{asset}_past_energy',{R},'{location}','energy','2020-01-01T05:00:00Z','2021-01-01T05:00:00Z','{MARKED}',NULL,0);")
        workloads[str(minutes)] = dict(asset=asset, location=location, shape=shape, forecast=forecast, meter=meter, source=source, shaped=shaped, clock=clock, calendar=calendar, zone='America/New_York' if minutes == 60 else 'America/Chicago', peakStart=7 if minutes == 60 else 6)
    market = market.replace('UPDATE ref_data.revisions SET', '\n'.join(refs) + '\nUPDATE ref_data.revisions SET', 1)
    market = market.replace('UPDATE market_data.revisions SET', '\n'.join(definitions) + '\nUPDATE market_data.revisions SET', 1)
    market = market.rsplit('COMMIT;', 1)[0] + '\n' + '\n'.join(observations) + '\nCOMMIT;\n'
    for frame in manifest['frames']:
        frame['observationIds'].extend(pins); frame['curveContracts'].update(deepcopy(contracts))
    overdue = deepcopy(manifest['frames'][0]);overdue['at'] = '2021-01-10T00:00:00Z'
    manifest['frames'].append(overdue)
    manifest['ppaBookings'] = dict(workloads=workloads, calendar=CALENDAR, certificate=CERTIFICATE,
                                   capacity=CAPACITY, overdueAt=overdue['at'], first=str(FIRST), last=str(LAST))
    return reference, market, security, manifest
