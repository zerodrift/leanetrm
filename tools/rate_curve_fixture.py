"""Synthetic definition-driven rate curves (D359) for owned preview fixtures.

A curve is a published date list of tenors, a published interest-rate curve definition naming that list, and the definition's
admission into the fixture's definition revision with one series per tenor. A tenor is a literal date, which stays fixed at
every close, or a calendar-day index over a published daily schedule, which rolls with each close's date. Rows are written as
the database owner, as other fixtures seed published library rows; capturing a knowledge context certifies their publications.

Upgrade checks load the same fixtures into the schemas before D359, so each call states what it writes there too: dated
pillars for a dated curve, nothing for a curve that only exists as a definition.
"""
import json
from uuid import UUID, uuid5

NAMESPACE = UUID('e1000000-0000-0000-0000-000000000359')
DISCOUNT_POLICY = 'log_discount_geometric_root_to_even_18_v1'


def _id(*parts):
    return str(uuid5(NAMESPACE, ':'.join(parts)))


def tenor_series(cat, code, label, unit):
    """The tenor's series identity, as RateCurveAdmission.TenorSeries derives it: one per catalog, curve code, label and mark unit."""
    return str(uuid5(UUID(cat), f'rate_tenor:{code}:{label}:{unit}'))


def by_schema(admitted, earlier=''):
    """SQL for the current schema, or for a schema before admitted rate curves."""
    return (f"DO $rate_curve$ BEGIN IF to_regclass('market_data.rate_curve_tenors') IS NOT NULL THEN\n{admitted}\n"
            f"ELSE\n{earlier or 'NULL;'}\nEND IF; END $rate_curve$;")


def admitted_curve(cat, rev, ref, code, name, tenors, *, currency, unit, specification, source, day_count, compounding,
                   interpolation, extrapolation, series=None, first='2020-01-01', last='2022-12-31'):
    """tenors: [(label, literal 'YYYY-MM-DD' date or calendar-day index)]. series: the tenors' existing series ids, which the
    caller inserts; without them each tenor gets its admission identity and a series row here. Returns the current-schema
    definition SQL, to run before the revision is sealed, and the tenors' series ids."""
    date_list, curve, context = _id(code, 'dates'), _id(code, 'curve'), str(uuid5(UUID(cat), 'rate-fixture-context'))

    def order(table):
        return f"(SELECT coalesce(max(publication_order),0)+1 FROM market_data.{table} WHERE catalog_id='{cat}')"
    rows = [f"INSERT INTO market_data.temporal_contexts VALUES('{cat}','{context}','{first}','{first}T00:00:00Z',0,'{first}T00:00:00Z',"
            f"'{first}T00:00:00Z') ON CONFLICT DO NOTHING;"]
    schedule = None
    if any(isinstance(at, int) for _, at in tenors):
        schedule, symbol = _id(code, 'schedule'), f'oe.{code}_days'
        daily = {'name': f'{name} days', 'symbol': symbol, 'firstMonth': None, 'lastMonth': None, 'anchor': None, 'day': None,
                 'shortMonth': None, 'adjustment': None, 'calendar': None, 'dates': None,
                 'daily': {'days': 'calendar', 'firstDate': first, 'lastDate': last}}
        rows += [f"INSERT INTO market_data.date_schedules VALUES('{cat}','{schedule}','{symbol}');",
                 "INSERT INTO market_data.date_schedule_versions(catalog_id,schedule_id,version,generation,state,definition,selection,effective_from,"
                 f"effective_to,engine_version,publication_order,published_at,publication_xid) VALUES('{cat}','{schedule}',1,1,'published',"
                 f"'{json.dumps(daily)}'::jsonb,'on_or_after','{first}','{last}'::date+1,'date_schedule_v4',{order('date_schedule_versions')},"
                 "clock_timestamp(),pg_current_xact_id());"]
    rows += [f"INSERT INTO market_data.contract_date_definitions VALUES('{cat}','{date_list}','{code[:24]}_tenors');",
             "INSERT INTO market_data.contract_date_versions(catalog_id,definition_id,version,generation,state,name,engine_version,validation_context_id,"
             f"publication_order,published_at,publication_xid) VALUES('{cat}','{date_list}',1,1,'draft','{name} tenors','date_list_v1','{context}',NULL,NULL,NULL);"]
    for ordinal, (label, at) in enumerate(tenors):
        rows.append(f"INSERT INTO market_data.contract_date_contracts VALUES('{cat}','{date_list}',1,{ordinal},'{label}',NULL);")
        field = f"NULL,'{cat}','{schedule}',1,{at},NULL" if isinstance(at, int) else f"'{at}',NULL,NULL,NULL,NULL,NULL"
        rows.append(f"INSERT INTO market_data.contract_date_fields VALUES('{cat}','{date_list}',1,{ordinal},'date',{field});")
    rows += [
        f"UPDATE market_data.contract_date_versions SET state='published',publication_order={order('contract_date_versions')},published_at=clock_timestamp(),"
        f"publication_xid=pg_current_xact_id() WHERE(catalog_id,definition_id,version)=('{cat}','{date_list}',1);",
        f"INSERT INTO market_data.curve_definitions VALUES('{cat}','{curve}','{code}');",
        "INSERT INTO market_data.curve_definition_versions(catalog_id,definition_id,version,generation,state,name,kind,reference_catalog_id,"
        "reference_revision_id,mapping_definition_id,mapping_version,mapping_generation,contract_count,interpolation,currency_code,mark_unit,day_count,"
        "compounding,extrapolation,mark_source_code,validation_context_id,publication_order,published_at,publication_xid) "
        f"VALUES('{cat}','{curve}',1,1,'published','{name}','interest_rate',{ref},'{date_list}',1,1,{len(tenors)},'{interpolation}','{currency}',"
        f"'{unit}','{day_count}','{compounding}','{extrapolation}','{source}','{context}',{order('curve_definition_versions')},clock_timestamp(),pg_current_xact_id());"]
    # Another definition revision of the same fixture admits the same published definition again.
    rows = [f"IF NOT EXISTS(SELECT 1 FROM market_data.curve_definitions WHERE(catalog_id,code)=('{cat}','{code}')) THEN", *rows, "END IF;",
        "INSERT INTO market_data.rate_curves(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,source_code,name,definition_id,"
        "definition_version,currency_code,mark_unit,day_count,compounding,interpolation,extrapolation,mapping_definition_id,mapping_version) "
        f"VALUES('{cat}','{rev}','{code}',{ref},'{source}','{name}','{curve}',1,'{currency}','{unit}','{day_count}','{compounding}','{interpolation}',"
        f"'{extrapolation}','{date_list}',1);"]
    owned = series is None
    series = series or [tenor_series(cat, code, label, unit) for label, _ in tenors]
    for ordinal, ((label, _), sid) in enumerate(zip(tenors, series)):
        if owned:
            rows.append(f"INSERT INTO market_data.series VALUES('{cat}','{rev}','{sid}','{code}.tenor.{sid.replace('-', '')[:16]}',{ref},NULL,NULL,'{specification}');")
        rows.append(f"INSERT INTO market_data.rate_curve_tenors VALUES('{cat}','{rev}','{code}',{ordinal},'{label}','{sid}');")
    return '\n'.join(rows), series


def dated_curve(cat, rev, ref, code, name, pillars, series, *, currency, source, interest_rate, specification='test_zero_rate', named=True):
    """A curve whose tenors are fixed dated pillars marked as decimal annual simple ACT/365 Fixed zero rates, interpolated
    log-linearly in discount factor and not extrapolated: the convention dated rate curves were recorded under before D359.
    Earlier schemas get the same pillars as dated samples under interest_rate, named only where curves have names (0026). The
    caller inserts the series."""
    admitted, _ = admitted_curve(cat, rev, ref, code, name, [(maturity, maturity) for maturity in pillars], series=series, currency=currency,
                                 unit='decimal_per_year', specification=specification, source=source, day_count='actual_365_fixed',
                                 compounding='simple', interpolation='log_linear_discount', extrapolation='none')
    earlier = '\n'.join([f"INSERT INTO market_data.rate_curves(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,interest_rate_code,"
                         f"source_code,date_basis{',name' if named else ''}) VALUES('{cat}','{rev}','{code}',{ref},'{interest_rate}','{source}','valuation_utc_date'"
                         f"{f",'{name}'" if named else ''});"] +
                        [f"INSERT INTO market_data.rate_curve_points VALUES('{cat}','{rev}','{code}','{maturity}','{sid}',{n});"
                         for n, (maturity, sid) in enumerate(zip(pillars, series))])
    return by_schema(admitted, earlier)


def discount_default(cat, id, rev, ref, currency, curve, order, effective_at):
    """A currency's default discount curve; before D359 it also named the interpolation policy, which the curve now owns."""
    return by_schema(f"INSERT INTO trading.currency_discount_defaults(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,"
                     f"currency_code,curve_code,publication_order,effective_at) VALUES('{cat}','{id}','{rev}',{ref},'{currency}','{curve}',{order},'{effective_at}');",
                     f"INSERT INTO trading.currency_discount_defaults(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,"
                     f"currency_code,curve_code,interpolation_policy,publication_order,effective_at) VALUES('{cat}','{id}','{rev}',{ref},'{currency}','{curve}',"
                     f"'{DISCOUNT_POLICY}',{order},'{effective_at}');")


def rolling_curve(cat, rev, ref, code, name, tenors, marks, *, currency='usd', unit='percent_per_year', specification='test_zero_rate_percent',
                  source='fixture', day_count='actual_365_fixed', compounding='continuous', interpolation='log_linear_discount',
                  extrapolation='flat_forward'):
    """tenors: [(label, calendar-day index)]; marks: {close instant: [mark per tenor]}. Returns (definition SQL, observation SQL,
    {close instant: [observation ids]}); earlier schemas get neither."""
    sql, series = admitted_curve(cat, rev, ref, code, name, tenors, currency=currency, unit=unit, specification=specification, source=source,
                                 day_count=day_count, compounding=compounding, interpolation=interpolation, extrapolation=extrapolation)
    observations, pins = [], {}
    for at, values in marks.items():
        pins[at] = []
        for (label, _), sid, value in zip(tenors, series, values):
            oid = _id(code, label, at)
            pins[at].append(oid)
            observations.append(f"INSERT INTO market_data.observations VALUES('{cat}','{oid}','{rev}','{sid}',{ref},'{source}','{code}_{label}_{at}','1',"
                                f"'parameter','final','{at}',clock_timestamp(),{value},NULL);")
    return by_schema(sql), by_schema('\n'.join(observations)), pins
