"""Synthetic independently published second bundle for exact-pin HTTP admission."""
from copy import deepcopy
from uuid import uuid4
from preview_fixture import CAT, REV, RCAT, RREV, TENANT, WORKSPACE

REFERENCE='10000000-0000-0000-0000-000000000074'
VERSION='20000000-0000-0000-0000-000000000074'
DEFINITION='60000000-0000-0000-0000-000000000074'


def install(query, manifest):
    # Clone the public synthetic reference vocabulary into an independently
    # authorized catalog. Identical codes deliberately do not imply compatible pins.
    query(f"""BEGIN;
    INSERT INTO ref_data.catalogs VALUES('{REFERENCE}','synthetic_ppa_r3','Synthetic revision admission');
    INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{REFERENCE}','{VERSION}','synthetic_r3');
    DO $copy$ DECLARE t text;BEGIN
      FOREACH t IN ARRAY security.reference_display_tables() LOOP
        EXECUTE format('INSERT INTO ref_data.%1$I SELECT(jsonb_populate_record(NULL::ref_data.%1$I,to_jsonb(r)||jsonb_build_object(''catalog_id'',$3,''revision_id'',$4))).* FROM ref_data.%1$I r WHERE catalog_id=$1 AND revision_id=$2',t)
        USING '{RCAT}'::uuid,'{RREV}'::uuid,'{REFERENCE}'::uuid,'{VERSION}'::uuid;
      END LOOP;
    END $copy$;
    UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{REFERENCE}','{VERSION}');
    INSERT INTO security.reference_catalog_scopes VALUES('{REFERENCE}','{TENANT}','{WORKSPACE}','owner');
    INSERT INTO market_data.revisions VALUES('{CAT}','{DEFINITION}','{REFERENCE}','{VERSION}','draft',0);
    INSERT INTO market_data.revision_periods SELECT catalog_id,'{DEFINITION}',effective_from,effective_to FROM market_data.revision_periods WHERE(catalog_id,revision_id)=('{CAT}','{REV}');
    COMMIT;""")
    asset=manifest['ppaBookings']['workloads']['60']['asset']
    source=manifest['ppaBookings']['workloads']['60']
    certificate=manifest['ppaBookings']['certificate']
    # Explicit new interval series headers; no cross-reference header reuse.
    forecast,meter=str(uuid4()),str(uuid4())
    statements=[]
    for old,new in [(source['forecast'],forecast),(source['meter'],meter)]:
        statements.append(f"INSERT INTO market_data.interval_series SELECT(jsonb_populate_record(NULL::market_data.interval_series,to_jsonb(r)||jsonb_build_object('id','{new}','code','synthetic_r3_{new.replace('-','')}','reference_catalog_id','{REFERENCE}','reference_revision_id','{VERSION}'))).* FROM market_data.interval_series r WHERE id='{old}';")
    def clone(table,where,extra=''):
        return f"INSERT INTO market_data.{table} SELECT(jsonb_populate_record(NULL::market_data.{table},to_jsonb(r)||jsonb_build_object('revision_id','{DEFINITION}','reference_catalog_id','{REFERENCE}','reference_revision_id','{VERSION}'){extra})).* FROM market_data.{table} r WHERE(r.catalog_id,r.revision_id)=('{CAT}','{REV}') AND {where};"
    # A small, valid period curve retains the exact old certificate vintage. The
    # changed asset capacity and assignment make publication observably different.
    statements.extend([clone('curves',f"r.code='{certificate}'"),
        clone('contracts',f"r.id IN(SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{CAT}','{REV}','{certificate}'))"),
        clone('series',f"r.contract_id IN(SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{CAT}','{REV}','{certificate}'))"),
        clone('curve_members',f"r.curve_code='{certificate}'"),
        clone('generation_assets',f"r.code='{asset}'"),
        clone('generation_asset_versions',f"r.asset_code='{asset}'","||jsonb_build_object('capacity_basis',300,'accredited_capacity',240)"),
        clone('volume_sources',f"r.code='{asset}'",f"||jsonb_build_object('forecast_series_id','{forecast}','metered_series_id','{meter}')"),
        clone('location_valuation_assignments',f"r.code='{asset}_certificate'","||jsonb_build_object('adjustment',2)")])
    # A complete second physical price definition, with the same code as the
    # old marked curve, is independent of identical names in the first bundle.
    marked=manifest['intervalCurves']['marked']
    actual_source=str(uuid4())
    statements.append(f"INSERT INTO power_data.sources SELECT(jsonb_populate_record(NULL::power_data.sources,to_jsonb(r)||jsonb_build_object('id','{actual_source}','point','synthetic_r3','reference_catalog_id','{REFERENCE}','reference_revision_id','{VERSION}'))).* FROM power_data.sources r WHERE id='{manifest['intervalCurves']['source']}';")
    statements.extend([clone('curves',f"r.code='{marked}'"),
        clone('contracts',f"r.id IN(SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{CAT}','{REV}','{marked}')) AND r.id NOT IN(SELECT id FROM market_data.contracts WHERE(catalog_id,revision_id)=('{CAT}','{DEFINITION}'))"),
        clone('series',f"r.contract_id IN(SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{CAT}','{REV}','{marked}')) AND r.id NOT IN(SELECT id FROM market_data.series WHERE(catalog_id,revision_id)=('{CAT}','{DEFINITION}'))"),
        clone('curve_members',f"r.curve_code='{marked}'"),
        clone('curve_blocks',f"r.curve_code='{marked}'"),
        clone('contract_intervals',f"r.contract_id IN(SELECT contract_id FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{CAT}','{REV}','{marked}'))"),
        clone('curve_physical_price_sources',f"r.curve_code='{marked}'",f"||jsonb_build_object('source_id','{actual_source}')"),
        clone('curve_actual_rules',f"r.curve_code='{marked}'"),
        clone('location_valuation_assignments',f"r.code='{asset}_past_energy'","||jsonb_build_object('adjustment',3)")])
    query('BEGIN;'+''.join(statements)+f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{CAT}','{DEFINITION}');COMMIT;")
    bundle=dict(definitionRevisionId=DEFINITION,referenceCatalogId=REFERENCE,referenceRevisionId=VERSION)
    first=deepcopy(manifest['frames'][0]);first.update(observationIds=[],curveContracts={},ppaBundle=bundle)
    later=deepcopy(first);later['at']=manifest['ppaBookings']['overdueAt']
    limited=deepcopy(manifest['frames'][0]);limited['at']='2020-12-15T16:01:00Z'
    limited['curveContracts'][certificate]=limited['curveContracts'][certificate][1:2]
    manifest['frames'].extend([first,later,limited])
    manifest['ppaBookings']['limitedAt']=limited['at']
    manifest['ppaR3']=dict(bundle=bundle,forecast=forecast,meter=meter,asset=asset)
