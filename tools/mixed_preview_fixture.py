"""Persist the exported managed synthetic case into a newly owned preview only."""
import json
from decimal import Decimal
from datetime import datetime, timezone
from preview_fixture import TENANT, WORKSPACE, security_fixture


# Synthetic display names; every exported curve must have exactly one.
CURVE_NAMES = {
    'benchmark_delivery': 'Benchmark gas · daily & monthly', 'basis_delivery': 'Location basis · daily & monthly',
    'location_delivery': 'Location gas · daily & monthly', 'location_all_in': 'Location gas · all-in prices',
    'benchmark_balmo': 'Benchmark gas · balance of month', 'basis_balmo': 'Location basis · balance of month',
    'location_balmo': 'Location gas · balance of month'}


def fixture(export_path):
    data=json.loads(export_path.read_text())
    cat,rev,rcat,rrev=(data[k] for k in ('catalogId','revisionId','referenceCatalogId','referenceRevisionId'))
    def value(v):
        if v is None:return 'NULL'
        if isinstance(v,bool):return 'true' if v else 'false'
        return "'"+str(v).replace("'","''")+"'"
    def insert(table,rows,columns=None):
        return f"INSERT INTO {table}"+(f" ({columns})" if columns else '')+' VALUES '+','.join(
            '('+','.join(value(v) for v in row)+')' for row in rows)+';'
    ref=[];market=[]
    def r(table,*rows,columns=None):ref.append(insert('ref_data.'+table,[(rcat,rrev,*row) for row in rows],columns))
    def m(table,*rows):market.append(insert('market_data.'+table,[(cat,rev,*row) for row in rows]))
    ref.extend(['BEGIN;',insert('ref_data.catalogs',[(rcat,'mixed_gas','Synthetic mixed-period gas')]),
                insert('ref_data.revisions',[(rcat,rrev,'mixed_gas_v1')],'catalog_id,id,revision_key')])
    r('currencies',('usd','USD','US dollar',2))
    r('dimensions',('energy',1,2,-2,0,0,0,0))
    r('units',('mmbtu','MMBtu','energy'))
    r('commodities',('gas','Synthetic natural gas','energy'))
    r('commodity_units',('gas','mmbtu','energy'))
    r('measurement_bases',('gas_energy','gas','energy'))
    r('quantity_specifications',('gas_mmbtu','gas','gas_energy','mmbtu','energy'))
    r('quote_specifications',('gas_out','gas','outright','gas_mmbtu','usd',1,'0.001','0.001',-1000,1000),
      ('gas_basis','gas','basis','gas_mmbtu','usd',1,'0.001','0.001',-1000,1000))
    r('clock_rules',('utc','fixed_offset',0),columns='catalog_id,revision_id,code,kind,fixed_offset_seconds')
    r('calendars',('synthetic_calendar','utc',96,'2025-01-01','2028-01-01'),
      columns='catalog_id,revision_id,code,clock_code,weekend_mask,coverage_from,coverage_to')
    r('location_types',('hub','Synthetic pricing hub'))
    r('locations',('benchmark_hub','Synthetic benchmark hub','hub','utc'),('location_hub','Synthetic location hub','hub','utc'),
      columns='catalog_id,revision_id,code,name,type_code,clock_code')
    r('commodity_locations',('gas','benchmark_hub'),('gas','location_hub'))
    r('sources',('synthetic_marks','synthetic','synthetic:mixed-resolution-v1','2026-01-01T00:00:00Z',
                 'Project-authored synthetic fixture','redistributable'),
      columns='catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review')
    r('price_index_definitions',('benchmark_daily_index','gas','benchmark_hub','usd','mmbtu',1,'synthetic_calendar'),
      ('location_daily_index','gas','location_hub','usd','mmbtu',1,'synthetic_calendar'))
    r('delivery_profiles',('uniform_fixed_utc_0900','uniform','utc','synthetic_calendar','fixed_total',60))
    r('settlement_targets',('benchmark_target','index','benchmark_daily_index',None),
      ('location_target','index','location_daily_index',None),
      ('basis_target','index_difference','location_daily_index','benchmark_daily_index'))
    r('interest_rate_definitions',('basic_rate','usd','synthetic_calendar','actual_365_fixed','simple','openetrm_rates_v1'),
      columns='catalog_id,revision_id,code,currency_code,calendar_code,day_count,compounding,implementation')
    r('value_specifications',('zero_rate','dimensionless',None,None,'-0.1',1))
    ref.extend([f"UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id={value(rcat)} AND id={value(rrev)};",'COMMIT;'])
    market.extend(['BEGIN;',insert('market_data.catalogs',[(cat,'mixed_gas')]),
                   insert('market_data.revisions',[(cat,rev,rcat,rrev,'draft',0)])])
    for c in data['contracts']:
        m('contracts',(c['id'],rcat,rrev,'forward',c['deliveryFrom'],c['deliveryTo'],c['expiry'],None))
    for c in data['curves']:
        target='basis_target' if c['indexB'] else 'benchmark_target' if c['indexA']=='benchmark_daily_index' else 'location_target'
        m('curves',(c['code'],rcat,rrev,'gas',c['location'],c['quote'],'uniform_fixed_utc_0900',target,c['rule'],
                    'synthetic_marks' if c['rule']=='direct' else None,'trader_mark' if c['rule']=='direct' else None,CURVE_NAMES[c['code']]))
        for ordinal,contract in enumerate(c['members']):m('curve_members',(c['code'],contract,ordinal))
        for source in c['inputs']:
            if source['expiryRule']!='require_live':raise ValueError('Unsupported explicit mixed-preview expiry policy')
            market.append(insert('market_data.curve_inputs',[(cat,rev,c['code'],source['slot'],source['source'],source['expiryRule'])],
                                 'catalog_id,revision_id,curve_code,slot,source_curve_code,expiry_rule'))
            for mapping in source['mappings']:
                m('constituent_mappings',(c['code'],source['slot'],mapping['target'],source['source'],mapping['source']))
    for s in data['series']:m('series',(s['id'],'series_'+s['id'].split('-')[-1],rcat,rrev,s['contractId'],s['quote'],None))
    m('rate_curves',('interest_rates',rcat,rrev,'basic_rate','synthetic_marks','valuation_utc_date','Interest rates'))
    rate_series=[]
    for ordinal,maturity in enumerate(['2026-06-01','2027-01-01','2027-02-28']):
        sid=f'97000000-0000-0000-0000-{ordinal+1:012d}';rate_series.append(sid)
        m('series',(sid,'rate_'+str(ordinal),rcat,rrev,None,None,'zero_rate'))
        m('rate_curve_points',('interest_rates',maturity,sid,ordinal))
    m('revision_periods',('2026-01-01','2028-01-01'))
    market.append(f"UPDATE market_data.revisions SET state='sealed' WHERE catalog_id={value(cat)} AND id={value(rev)};")
    now=datetime.now(timezone.utc).isoformat()
    frames=[]
    for index,frame in enumerate(data['frames']):
        pins=[]
        for o in frame['observations']:
            pins.append(o['id'])
            market.append(insert('market_data.observations',[(cat,o['id'],rev,o['seriesId'],rcat,rrev,'synthetic_marks',
                f"frame_{index}_{o['seriesId']}",'1','trader_mark','final',o['observedAt'],now,o['value'],None)]))
        for ordinal,sid in enumerate(rate_series):
            oid=f'98000000-0000-0000-0000-{index*10+ordinal+1:012d}';pins.append(oid)
            market.append(insert('market_data.observations',[(cat,oid,rev,sid,rcat,rrev,'synthetic_marks',
                f'rate_{index}_{ordinal}','1','parameter','final',frame['at'],now,str(Decimal(250+ordinal*50+index)/Decimal(10000)),None)]))
        frames.append({'at':frame['at'],'observationIds':pins,'curveContracts':frame['curveContracts']})
    market.append('COMMIT;')
    manifest={'futuresConvention':'contract_lot_execution_pnl_v2','mode':'local-synthetic-preview','catalogId':cat,'revisionId':rev,'referenceCatalogId':rcat,
              'referenceRevisionId':rrev,'tenantId':TENANT,'workspaceId':WORKSPACE,'frames':frames,
              'example':{'at':frames[0]['at'],'curve':'location_delivery'}}
    return '\n'.join(ref),'\n'.join(market),security_fixture(cat,rcat),manifest
