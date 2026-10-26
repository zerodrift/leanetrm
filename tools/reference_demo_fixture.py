"""Add an explicitly synthetic, rate-enabled demo bundle without replacing inputs."""
from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4, uuid5, NAMESPACE_URL
import json


def install(query, manifest):
    cat,ref,original=[str(UUID(manifest[k])) for k in ('catalogId','referenceCatalogId','referenceRevisionId')]
    identity=lambda name:str(uuid5(NAMESPACE_URL,cat+':reference-demo-v1:'+name))
    revision,definition=identity('reference'),identity('definition')
    main=[f for f in manifest['frames'] if not f.get('ppaBundle')]
    if not main:raise ValueError('Explicit original market frames are required')
    last=max(datetime.fromisoformat(f['at'].replace('Z','+00:00')) for f in main)
    delivery=(last.date().replace(day=1)+timedelta(days=32)).replace(day=1)
    end=(delivery+timedelta(days=32)).replace(day=1);payment=end+timedelta(days=1)
    maturities=[last.date()+timedelta(days=1),payment,payment.replace(year=payment.year+1)]
    def lit(x):return "'"+str(x).replace("'","''")+"'"
    chosen=json.loads(query(f"""SELECT row_to_json(x) FROM(SELECT q.code AS quote,q.currency_code,q.commodity_code,
      q.quantity_code,u.unit_code,u.dimension_code,u.basis_code,i.location_code,i.calendar_code,l.type_code
      FROM market_data.curves c JOIN ref_data.quote_specifications q
       ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
      JOIN ref_data.quantity_specifications u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.quantity_code)
      JOIN ref_data.settlement_targets t ON(t.catalog_id,t.revision_id,t.code)=(c.reference_catalog_id,c.reference_revision_id,c.settlement_target_code)
      JOIN ref_data.price_index_definitions i ON(i.catalog_id,i.revision_id,i.code)=(t.catalog_id,t.revision_id,t.index_a_code)
      JOIN ref_data.locations l ON(l.catalog_id,l.revision_id,l.code)=(i.catalog_id,i.revision_id,i.location_code)
      WHERE c.catalog_id='{cat}' AND c.revision_id={lit(manifest['revisionId'])} AND q.kind='outright' AND q.multiplier=1
       AND q.minimum<=50 AND q.maximum>=50 AND i.quote_unit_code=u.unit_code AND i.quote_multiplier=q.multiplier
      ORDER BY c.code LIMIT 1)x;"""))
    if not chosen:raise ValueError('An existing compatible outright indexed quote is required')
    sql=["BEGIN;SET LOCAL lock_timeout='5s';SELECT pg_advisory_xact_lock(730849102);",
         f"INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{ref}','{revision}','synthetic_reference_demo_v1');"]
    def copy(table,predicate):
        sql.append(f"INSERT INTO ref_data.{table} SELECT(jsonb_populate_record(NULL::ref_data.{table},to_jsonb(r)||jsonb_build_object('revision_id','{revision}'))).* FROM ref_data.{table} r WHERE catalog_id='{ref}' AND revision_id='{original}' AND ({predicate});")
    for table,key,value in [('currencies','code',chosen['currency_code']),('dimensions','code',chosen['dimension_code']),
       ('units','code',chosen['unit_code']),('commodities','code',chosen['commodity_code']),('measurement_bases','code',chosen['basis_code']),
       ('quantity_specifications','code',chosen['quantity_code']),('location_types','code',chosen['type_code'])]:
        copy(table,key+'='+lit(value))
    copy('clock_rules',f"code IN(SELECT clock_code FROM ref_data.calendars WHERE catalog_id='{ref}' AND revision_id='{original}' AND code={lit(chosen['calendar_code'])} UNION SELECT clock_code FROM ref_data.locations WHERE catalog_id='{ref}' AND revision_id='{original}' AND code={lit(chosen['location_code'])})")
    copy('calendars','code='+lit(chosen['calendar_code']));copy('calendar_days','calendar_code='+lit(chosen['calendar_code']))
    copy('locations','code='+lit(chosen['location_code']))
    copy('commodity_units',f"commodity_code={lit(chosen['commodity_code'])} AND unit_code={lit(chosen['unit_code'])}")
    copy('commodity_locations',f"commodity_code={lit(chosen['commodity_code'])} AND location_code={lit(chosen['location_code'])}")
    sql.append(f"INSERT INTO ref_data.quote_specifications SELECT(jsonb_populate_record(NULL::ref_data.quote_specifications,to_jsonb(r)||jsonb_build_object('revision_id','{revision}','code','demo_price'))).* FROM ref_data.quote_specifications r WHERE catalog_id='{ref}' AND revision_id='{original}' AND code={lit(chosen['quote'])};")
    rr=f"'{ref}','{revision}'"
    sql.extend([
      f"INSERT INTO ref_data.sources(catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review) VALUES({rr},'demo_reference_marks','synthetic','synthetic:reference-demo-v1',clock_timestamp(),'Project-authored synthetic example','redistributable');",
      f"INSERT INTO ref_data.price_index_definitions VALUES({rr},'demo_monthly_index',{lit(chosen['commodity_code'])},{lit(chosen['location_code'])},{lit(chosen['currency_code'])},{lit(chosen['unit_code'])},(SELECT multiplier FROM ref_data.quote_specifications WHERE catalog_id='{ref}' AND revision_id='{revision}' AND code='demo_price'),{lit(chosen['calendar_code'])});",
      f"INSERT INTO ref_data.delivery_profiles SELECT {rr},'demo_uniform','uniform',clock_code,code,'fixed_total',60 FROM ref_data.calendars WHERE catalog_id='{ref}' AND revision_id='{revision}';",
      f"INSERT INTO ref_data.settlement_targets VALUES({rr},'demo_index_target','index','demo_monthly_index',NULL);",
      f"INSERT INTO ref_data.interest_rate_definitions VALUES({rr},'demo_simple_rate',{lit(chosen['currency_code'])},{lit(chosen['calendar_code'])},'actual_365_fixed','simple','openetrm_rates_v1');",
      f"INSERT INTO ref_data.value_specifications VALUES({rr},'demo_decimal_rate','dimensionless',NULL,NULL,-0.1,1);",
      f"UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{ref}' AND id='{revision}';",
      f"INSERT INTO market_data.revisions VALUES('{cat}','{definition}',{rr},'draft',0);",
      f"INSERT INTO market_data.revision_periods VALUES('{cat}','{definition}','1901-01-01','2199-12-31');"])
    contract=identity('contract');series=identity('price-series');fixing=end.isoformat()+'T12:00:00Z'
    sql.extend([
      f"INSERT INTO market_data.contracts VALUES('{cat}','{definition}','{contract}',{rr},'forward','{delivery}','{end}','{fixing}',NULL);",
      f"INSERT INTO market_data.curves VALUES('{cat}','{definition}','demo_forward',{rr},{lit(chosen['commodity_code'])},{lit(chosen['location_code'])},'demo_price','demo_uniform','demo_index_target','direct','demo_reference_marks','trader_mark','Synthetic reference demo forward');",
      f"INSERT INTO market_data.series VALUES('{cat}','{definition}','{series}','demo_forward',{rr},'{contract}','demo_price',NULL);",
      f"INSERT INTO market_data.curve_members VALUES('{cat}','{definition}','demo_forward','{contract}',0);",
      f"INSERT INTO market_data.index_fixing_observations VALUES('{cat}','{definition}','demo_monthly_index','{delivery}','{end}',{rr},'demo_price','{series}','demo_reference_marks','{fixing}','published_index_observations_v1');",
      f"INSERT INTO market_data.rate_curves VALUES('{cat}','{definition}','demo_discount_curve',{rr},'demo_simple_rate','demo_reference_marks','valuation_utc_date','Synthetic simple-rate discount curve');"])
    rates=[('0.025',identity('rate-0')),('0.03',identity('rate-1')),('0.035',identity('rate-2'))]
    for i,(_,sid) in enumerate(rates):
        sql.extend([f"INSERT INTO market_data.series VALUES('{cat}','{definition}','{sid}','demo_rate_{i}',{rr},NULL,NULL,'demo_decimal_rate');",
          f"INSERT INTO market_data.rate_curve_points VALUES('{cat}','{definition}','demo_discount_curve','{maturities[i]}','{sid}',{i});"])
    sql.append(f"UPDATE market_data.revisions SET state='sealed' WHERE catalog_id='{cat}' AND id='{definition}';")
    frames=[]
    for frame in main:
        pins=[]
        for i,(value,sid) in enumerate([('50',series),*rates]):
            oid=identity('observation:'+frame['at']+':'+str(i));pins.append(oid)
            sql.append(f"INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value) VALUES('{cat}','{oid}','{definition}','{sid}',{rr},'demo_reference_marks','{oid}','1',{'trader_mark' if i==0 else 'parameter'!r},'final',{lit(frame['at'])},clock_timestamp(),{value});")
        frames.append({'at':frame['at'],'observationIds':pins,'curveContracts':{'demo_forward':[contract]}})
    mapping=identity('discount-mapping')
    sql.extend([f"INSERT INTO trading.publications SELECT '{cat}',coalesce(max(publication_order),0)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{cat}';",
      f"INSERT INTO trading.currency_discount_defaults SELECT '{cat}','{mapping}','{definition}',{rr},{lit(chosen['currency_code'])},'demo_discount_curve','log_discount_geometric_root_to_even_18_v1',max(publication_order),{lit(min(f['at'] for f in main))} FROM trading.publications WHERE catalog_id='{cat}';",'COMMIT;'])
    query('\n'.join(sql))
    manifest.setdefault('swapMarkets',[]).append({'revisionId':definition,'frames':frames})
    manifest['referenceCatalog']={'referenceRevisionId':revision,'definitionRevisionId':definition,'tables':['interest_rate_definitions','value_specifications']}
    return {'definitionRevisionId':definition,'referenceRevisionId':revision,'referenceCatalogId':ref,'deliveryFrom':str(delivery),'deliveryTo':str(end),'paymentDate':str(payment),'mappingId':mapping,'currency':chosen['currency_code'],'frames':frames}
