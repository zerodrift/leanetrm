"""A synthetic published futures curve definition admitted into a fixture revision (D362).

The definition's contracts are literal dates, so its mapping needs no schedules. Rows are written as the database owner, as
other fixtures seed published library rows; capturing a knowledge context certifies their publications.
"""
from uuid import UUID, uuid5

NAMESPACE = UUID('e1000000-0000-0000-0000-000000000362')


def admitted_future_curve(cat, rev, ref, code, name, members, *, commodity, location, quote, profile, product, target, source):
    """members: [(contract id, label, start, end, last trading date)], whose series the caller inserts. Returns definition SQL to
    run before the revision is sealed; upgrade checks load the fixture into earlier schemas, which get nothing."""
    mapping, curve = uuid5(NAMESPACE, f'{code}:dates'), uuid5(NAMESPACE, f'{code}:curve')
    context = uuid5(UUID(cat), 'contract-curve-fixture-context')
    first = '2020-01-02'  # the rate fixture retains a context on the first

    def order(table):
        return f"(SELECT coalesce(max(publication_order),0)+1 FROM market_data.{table} WHERE catalog_id='{cat}')"
    rows = [f"INSERT INTO market_data.temporal_contexts VALUES('{cat}','{context}','{first}','{first}T00:00:00Z',0,'{first}T00:00:00Z','{first}T00:00:00Z');",
            f"INSERT INTO market_data.contract_date_definitions VALUES('{cat}','{mapping}','{code[:24]}_months');",
            "INSERT INTO market_data.contract_date_versions(catalog_id,definition_id,version,generation,state,name,engine_version,validation_context_id) "
            f"VALUES('{cat}','{mapping}',1,1,'draft','{name} months','contract_dates_v1','{context}');"]
    for ordinal, (_, label, start, end, last) in enumerate(members):
        rows.append(f"INSERT INTO market_data.contract_date_contracts VALUES('{cat}','{mapping}',1,{ordinal},'{label}','15:30');")
        rows += [f"INSERT INTO market_data.contract_date_fields VALUES('{cat}','{mapping}',1,{ordinal},'{role}','{at}',NULL,NULL,NULL,NULL,NULL);"
                 for role, at in (('start', start), ('end', end), ('last_trading', last))]
    rows += [
        f"UPDATE market_data.contract_date_versions SET state='published',publication_order={order('contract_date_versions')},published_at=clock_timestamp(),"
        f"publication_xid=pg_current_xact_id() WHERE(catalog_id,definition_id,version)=('{cat}','{mapping}',1);",
        f"INSERT INTO market_data.curve_definitions VALUES('{cat}','{curve}','{code}');",
        "INSERT INTO market_data.curve_definition_versions(catalog_id,definition_id,version,generation,state,name,kind,reference_catalog_id,reference_revision_id,"
        "mapping_definition_id,mapping_version,mapping_generation,contract_count,commodity_code,location_code,quote_code,profile_code,futures_product_code,"
        "settlement_target_code,price_rule,mark_role,missing_price,interpolation,mark_source_code,validation_context_id,publication_order,published_at,publication_xid) "
        f"VALUES('{cat}','{curve}',1,1,'published','{name}','future',{ref},'{mapping}',1,1,{len(members)},'{commodity}','{location}','{quote}','{profile}','{product}',"
        f"'{target}','direct','daily_settlement','reject','none','{source}','{context}',{order('curve_definition_versions')},clock_timestamp(),pg_current_xact_id());",
        "INSERT INTO market_data.curves(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,commodity_code,location_code,quote_code,profile_code,"
        f"settlement_target_code,rule,direct_source_code,direct_role,name,definition_id,definition_version) VALUES('{cat}','{rev}','{code}',{ref},'{commodity}','{location}',"
        f"'{quote}','{profile}','{target}','direct','{source}','daily_settlement','{name}','{curve}',1);"]
    rows += [f"INSERT INTO market_data.curve_members VALUES('{cat}','{rev}','{code}','{contract}',{ordinal});" for ordinal, (contract, *_) in enumerate(members)]
    body = '\n'.join(rows)
    return f"DO $contract_curve$ BEGIN IF to_regclass('market_data.contract_curve_admission_requests') IS NOT NULL THEN\n{body}\nEND IF; END $contract_curve$;"
