"""A synthetic published futures curve definition admitted into a fixture revision (D362).

The published mapping names saved date lists. The native admission guard verifies every contract under an authenticated
Editor context before the fixture revision is sealed.
"""
import json
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
    schedules = {role: uuid5(NAMESPACE, f'{code}:{role}') for role in ('start', 'end', 'last_trading')}
    for role, position in (('start', 2), ('end', 3), ('last_trading', 4)):
        symbol = f'oe.{code}_{role}'
        definition = dict(name=f'{name} {role}', symbol=symbol, firstMonth=None, lastMonth=None, anchor=None, day=None,
                          shortMonth=None, adjustment=None, calendar=None, dates=[member[position] for member in members])
        rows += [f"INSERT INTO market_data.date_schedules VALUES('{cat}','{schedules[role]}','{symbol}');",
                 "INSERT INTO market_data.date_schedule_versions(catalog_id,schedule_id,version,generation,state,definition,selection,effective_from,"
                 f"effective_to,engine_version,publication_order,published_at,publication_xid) VALUES('{cat}','{schedules[role]}',1,1,'published',"
                 f"'{json.dumps(definition)}'::jsonb,'absolute_index','2020-01-01','2022-01-01','date_schedule_v3',"
                 f"{order('date_schedule_versions')},clock_timestamp(),pg_current_xact_id());"]
    for ordinal, (_, label, start, end, last) in enumerate(members):
        rows.append(f"INSERT INTO market_data.contract_date_contracts VALUES('{cat}','{mapping}',1,{ordinal},'{label}','15:30');")
        rows += [f"INSERT INTO market_data.contract_date_fields VALUES('{cat}','{mapping}',1,{ordinal},'{role}',NULL,'{cat}','{schedule}',1,{ordinal},NULL);"
                 for role, schedule in schedules.items()]
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


def verify_admissions(cat, rev, tenant, workspace):
    """Run after the published fixture sources and security setup have committed, before sealing the draft revision."""
    return f"""BEGIN;
DO $admit$ DECLARE capability uuid;context uuid;BEGIN
 IF to_regclass('market_data.contract_curve_admission_requests') IS NULL THEN RETURN;END IF;
 SET LOCAL SESSION AUTHORIZATION preview_authenticator;
 capability=security.issue_context('https://preview.invalid','editor','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);
 SET LOCAL SESSION AUTHORIZATION preview_runtime;
 PERFORM security.use_context(capability);
 context=security.capture_date_context('{cat}','2021-01-15',NULL);
 INSERT INTO market_data.contract_date_admissions(catalog_id,id,revision_id,contract_id,mapping_id,mapping_version,ordinal,context_id,actor_id)
 SELECT c.catalog_id,gen_random_uuid(),c.revision_id,m.contract_id,v.mapping_definition_id,v.mapping_version,m.ordinal,context,
  security.custom_curve_actor(c.catalog_id,'WriteMarketDefinitions')
 FROM market_data.curves c JOIN market_data.curve_definition_versions v
  ON(v.catalog_id,v.definition_id,v.version)=(c.catalog_id,c.definition_id,c.definition_version)
 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code)
 WHERE(c.catalog_id,c.revision_id)=('{cat}','{rev}');
END $admit$;
COMMIT;
"""


def prepare_market(market, security, manifest):
    """Latest-schema load only: upgrades retain the original transaction-safe fixture for their rollback checks."""
    marker = 'UPDATE market_data.revisions SET'
    assert market.count(marker) == 1
    admissions = verify_admissions(*(manifest[key] for key in ('catalogId', 'revisionId', 'tenantId', 'workspaceId')))
    return market.replace(marker, 'COMMIT;\n' + security + '\n' + admissions + '\nBEGIN;\n' + marker, 1)
