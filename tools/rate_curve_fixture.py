"""Synthetic definition-driven rate curves (D359) for owned preview fixtures.

One call yields a published calendar-day daily schedule, a published date list of tenors over it, a published
interest-rate curve definition naming that list, the definition's admission into the fixture's definition revision with
one series per tenor, and each close's tenor marks. Rows are written as the database owner, as other fixtures seed
published library rows; capturing a knowledge context certifies their publications.
"""
import json
from uuid import UUID, uuid5

NAMESPACE = UUID('e1000000-0000-0000-0000-000000000359')


def _id(*parts):
    return str(uuid5(NAMESPACE, ':'.join(parts)))


def rolling_curve(cat, rev, ref, code, name, tenors, marks, *, first='2020-01-01', last='2022-12-31',
                  currency='usd', unit='percent_per_year', specification='test_zero_rate_percent', source='fixture',
                  day_count='actual_365_fixed', compounding='continuous', interpolation='log_linear_discount', extrapolation='flat_forward'):
    """tenors: [(label, calendar-day index)]; marks: {close instant: [mark per tenor]}. Each publication takes its library's next
    publication order, which knowledge certification requires to be contiguous. Returns (definition SQL to run before the revision is sealed, observation SQL, {close instant: [observation ids]})."""
    schedule, date_list, curve, context = _id(code, 'schedule'), _id(code, 'dates'), _id(code, 'curve'), _id(code, 'context')
    symbol = f'oe.{code}_days'
    def order(table):
        return f"(SELECT coalesce(max(publication_order),0)+1 FROM market_data.{table} WHERE catalog_id='{cat}')"
    daily = {'name': f'{name} days', 'symbol': symbol, 'firstMonth': None, 'lastMonth': None, 'anchor': None, 'day': None,
             'shortMonth': None, 'adjustment': None, 'calendar': None, 'dates': None,
             'daily': {'days': 'calendar', 'firstDate': first, 'lastDate': last}}
    rows = [
        f"INSERT INTO market_data.temporal_contexts VALUES('{cat}','{context}','{first}','{first}T00:00:00Z',0,'{first}T00:00:00Z','{first}T00:00:00Z');",
        f"INSERT INTO market_data.date_schedules VALUES('{cat}','{schedule}','{symbol}');",
        "INSERT INTO market_data.date_schedule_versions(catalog_id,schedule_id,version,generation,state,definition,selection,effective_from,effective_to,"
        f"engine_version,publication_order,published_at,publication_xid) VALUES('{cat}','{schedule}',1,1,'published','{json.dumps(daily)}'::jsonb,"
        f"'on_or_after','{first}','{last}'::date+1,'date_schedule_v4',{order('date_schedule_versions')},clock_timestamp(),pg_current_xact_id());",
        f"INSERT INTO market_data.contract_date_definitions VALUES('{cat}','{date_list}','{code}_tenors');",
        "INSERT INTO market_data.contract_date_versions(catalog_id,definition_id,version,generation,state,name,engine_version,validation_context_id,"
        f"publication_order,published_at,publication_xid) VALUES('{cat}','{date_list}',1,1,'draft','{name} tenors','date_list_v1','{context}',NULL,NULL,NULL);"]
    for ordinal, (label, index) in enumerate(tenors):
        rows.append(f"INSERT INTO market_data.contract_date_contracts VALUES('{cat}','{date_list}',1,{ordinal},'{label}',NULL);")
        rows.append(f"INSERT INTO market_data.contract_date_fields VALUES('{cat}','{date_list}',1,{ordinal},'date',NULL,'{cat}','{schedule}',1,{index},NULL);")
    rows += [
        f"UPDATE market_data.contract_date_versions SET state='published',publication_order={order('contract_date_versions')},published_at=clock_timestamp(),"
        f"publication_xid=pg_current_xact_id() WHERE(catalog_id,definition_id,version)=('{cat}','{date_list}',1);",
        f"INSERT INTO market_data.curve_definitions VALUES('{cat}','{curve}','{code}');",
        "INSERT INTO market_data.curve_definition_versions(catalog_id,definition_id,version,generation,state,name,kind,reference_catalog_id,"
        "reference_revision_id,mapping_definition_id,mapping_version,mapping_generation,contract_count,interpolation,currency_code,mark_unit,day_count,"
        "compounding,extrapolation,mark_source_code,validation_context_id,publication_order,published_at,publication_xid) "
        f"VALUES('{cat}','{curve}',1,1,'published','{name}','interest_rate',{ref},'{date_list}',1,1,{len(tenors)},'{interpolation}','{currency}',"
        f"'{unit}','{day_count}','{compounding}','{extrapolation}','{source}','{context}',{order('curve_definition_versions')},clock_timestamp(),pg_current_xact_id());",
        "INSERT INTO market_data.rate_curves(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,source_code,name,definition_id,"
        "definition_version,currency_code,mark_unit,day_count,compounding,interpolation,extrapolation,mapping_definition_id,mapping_version) "
        f"VALUES('{cat}','{rev}','{code}',{ref},'{source}','{name}','{curve}',1,'{currency}','{unit}','{day_count}','{compounding}','{interpolation}',"
        f"'{extrapolation}','{date_list}',1);"]
    series = [tenor_series(cat, code, label) for label, _ in tenors]
    for ordinal, ((label, _), sid) in enumerate(zip(tenors, series)):
        rows.append(f"INSERT INTO market_data.series VALUES('{cat}','{rev}','{sid}','{code}.tenor.{sid.replace('-', '')[:16]}',{ref},NULL,NULL,'{specification}');")
        rows.append(f"INSERT INTO market_data.rate_curve_tenors VALUES('{cat}','{rev}','{code}',{ordinal},'{label}','{sid}');")
    observations, pins = [], {}
    for at, values in marks.items():
        pins[at] = []
        for (label, _), sid, value in zip(tenors, series, values):
            oid = _id(code, label, at)
            pins[at].append(oid)
            observations.append(f"INSERT INTO market_data.observations VALUES('{cat}','{oid}','{rev}','{sid}',{ref},'{source}','{code}_{label}_{at}','1',"
                                f"'parameter','final','{at}',clock_timestamp(),{value},NULL);")
    # Upgrade checks load the same fixture into earlier schemas, which have no admitted rate curves.
    def current(sql):
        return f"DO $rate_curve$ BEGIN IF to_regclass('market_data.rate_curve_tenors') IS NOT NULL THEN\n{sql}\nEND IF; END $rate_curve$;"
    return current('\n'.join(rows)), current('\n'.join(observations)), pins


def tenor_series(cat, code, label):
    """The tenor's series identity, as RateCurveAdmission.TenorSeries derives it: one per catalog, curve code and label."""
    return str(uuid5(UUID(cat), f'rate_tenor:{code}:{label}'))
