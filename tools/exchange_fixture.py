"""Independent exchange example with real product facts and generated prices.

Public contract facts and a bounded set of published lot totals are transcribed
as factual inputs with source links. No exchange price/history feed is bundled.
"""
from datetime import date, datetime, timezone
from decimal import Decimal
from pathlib import Path
import json
import re
from uuid import UUID, uuid5
from preview_fixture import fixture as baseline, trading_fixture as baseline_trading, installed_tzdb, tou_products

NAMESPACE=UUID('5a935d8a-8e48-43d6-83fd-4b7b7d4f991d')
def key(name): return str(uuid5(NAMESPACE,name))
def remap(text):
    return re.sub(r'\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b',
                  lambda m:m[0] if m[0].startswith('a0000000-') else key(m[0]),text)

# code, label, location, commodity, symbol, lot, basis, settlement, tick, source,
# explicit illustrative mark, frequency. Prices are original synthetic examples.
PRODUCTS=[
 ('nymex_ng','NYMEX Henry Hub Natural Gas · NG','henry_hub','gas','NG','10000','fixed_total','physical','0.001','https://www.cmegroup.com/markets/energy/natural-gas/natural-gas.contractSpecs.html','3.200','monthly'),
 ('ice_pmi','ICE PJM Western Hub RT Peak · PMI','pjm_western_hub','power','PMI','1','per_delivery_hour','cash','0.05','https://www.ice.com/products/6590369','52.00','monthly'),
 ('ice_pjc','ICE PJM Western Hub DA Peak · PJC','pjm_western_hub','power','PJC','1','per_delivery_hour','cash','0.05','https://www.ice.com/products/6590366','51.00','monthly'),
 ('ice_ern','ICE ERCOT North RT Peak · ERN','ercot_north','power','ERN','1','per_delivery_hour','cash','0.05','https://www.ice.com/products/6590337','48.00','monthly'),
 ('ice_spm','ICE CAISO SP-15 DA Peak · SPM','caiso_sp15','power','SPM','1','per_delivery_hour','cash','0.05','https://www.ice.com/products/6590382','44.00','monthly'),
 ('ice_npm','ICE CAISO NP-15 DA Peak · NPM','caiso_np15','power','NPM','1','per_delivery_hour','cash','0.05','https://www.ice.com/products/6590362','43.00','monthly'),
 ('ice_cfb','ICE Chicago Fixed Price · CFB','chicago','gas','CFB','2500','fixed_total','cash','0.0001','https://www.ice.com/products/68358298','3.0500','monthly'),
 ('ice_fqh','ICE HSC Fixed Price · FQH','houston_ship_channel','gas','FQH','2500','fixed_total','cash','0.0001','https://www.ice.com/products/69723155','2.9500','monthly'),
 ('ice_fqw','ICE Waha Fixed Price · FQW','waha','gas','FQW','2500','fixed_total','cash','0.0001','https://www.ice.com/products/69723181','-0.2500','monthly'),
 ('ice_dgd','ICE Chicago Basis · DGD','chicago','gas','DGD','2500','fixed_total','cash','0.0005','https://www.ice.com/products/6590132','-0.1500','monthly'),
 ('ice_hxs','ICE HSC Basis · HXS','houston_ship_channel','gas','HXS','2500','fixed_total','cash','0.0005','https://www.ice.com/products/6590137','-0.2500','monthly'),
 ('ice_wah','ICE Waha Basis · WAH','waha','gas','WAH','2500','fixed_total','cash','0.0005','https://www.ice.com/products/6590171','-3.4500','monthly'),
 ('ice_css','ICE Chicago Daily Swing · CSS','chicago','gas','CSS','2500','fixed_total','cash','0.0010','https://www.ice.com/products/6590224','3.1000','daily'),
 ('ice_ucs','ICE HSC Daily Swing · UCS','houston_ship_channel','gas','UCS','2500','fixed_total','cash','0.0010','https://www.ice.com/products/6590255','2.9000','daily'),
 ('ice_was','ICE Waha Daily Swing · WAS','waha','gas','WAS','2500','fixed_total','cash','0.0010','https://www.ice.com/products/6590256','-0.4000','daily'),
 ('ice_pda','ICE PJM Western Hub DA Peak Daily · PDA','pjm_western_hub','power','PDA','800','fixed_total','cash','0.05','https://www.ice.com/products/6590471','53.00','daily'),
 ('ice_dpn','ICE CAISO NP-15 DA Peak Daily · DPN','caiso_np15','power','DPN','400','fixed_total','cash','0.05','https://www.ice.com/products/6590450','42.00','daily'),
 ('ice_sdp','ICE CAISO SP-15 DA Peak Daily · SDP','caiso_sp15','power','SDP','400','fixed_total','cash','0.05','https://www.ice.com/products/6590477','45.00','daily'),
 ('ice_end','ICE ERCOT North RT Peak Daily · END','ercot_north','power','END','800','fixed_total','cash','0.05','https://www.ice.com/products/6590453','50.00','daily'),
]
# ICE expiry tables, checked 2026-10-18. These are per1MW lot quantities, not
# an assumed month length. Each row is an explicit dated definition input.
MONTHS=['2026-11-01','2026-12-01','2027-01-01','2027-02-01','2027-03-01','2027-04-01']
POWER_LOTS={'ice_pmi':[320,352,320,320,368,352],'ice_pjc':[320,352,320,320,368,352],
            'ice_ern':[320,352,320,320,368,352],'ice_spm':[384,416,400,384,432,416],
            'ice_npm':[384,416,400,384,432,416]}
MONTH_ENDS=['2026-11-30','2026-12-31','2027-01-29','2027-02-26','2027-03-31','2027-04-30']
BEFORE_MONTH=['2026-10-30','2026-11-30','2026-12-31','2027-01-29','2027-02-26','2027-03-31']
NG_EXPIRIES=['2026-10-28T18:30:00Z','2026-11-25T19:30:00Z','2026-12-29T19:30:00Z','2027-01-27T19:30:00Z','2027-02-24T19:30:00Z','2027-03-29T18:30:00Z']

# NYMEX CL and its options, from the public specifications, calendars and rulebooks
# retrieved 2026-11-01. CL last-trading and option expiry times of day are not
# stated by those sources, so their instants stay unknown and none of these
# contracts is executable. Example option series lie on the listed strike grid.
CME='https://www.cmegroup.com'
CL_LAST_TRADING=['2026-10-20','2026-11-20','2026-12-21','2027-01-20','2027-02-22','2027-03-22']
OPTION_SOURCES={
 'nymex_cl':(CME+'/markets/energy/crude-oil/light-sweet-crude.contractSpecs.html','Public specification facts and link only; no market prices or document text redistributed'),
 'nymex_cl_expiry':(CME+'/markets/energy/crude-oil/light-sweet-crude.calendar.html','Bounded factual monthly last-trading dates; no prices or source documents'),
 'nymex_on':(CME+'/markets/energy/natural-gas/natural-gas.contractSpecs.options.html','Public specification facts and link only; no market prices or document text redistributed'),
 'nymex_lo':(CME+'/markets/energy/crude-oil/light-sweet-crude.contractSpecs.options.html','Public specification facts and link only; no market prices or document text redistributed'),
 'nymex_on_expiry':(CME+'/markets/energy/natural-gas/natural-gas.calendar.options.html','Bounded factual option expiration dates; no prices or source documents'),
 'nymex_lo_expiry':(CME+'/markets/energy/crude-oil/light-sweet-crude.calendar.options.html','Bounded factual option expiration dates; no prices or source documents'),
 'nymex_on_rulebook':(CME+'/content/dam/cmegroup/rulebook/NYMEX/3/370.pdf','Rule facts only (document copyright 2009 New York Mercantile Exchange); the stated price multiple is used as both premium tick and settlement clearing increment'),
 'nymex_lo_rulebook':(CME+'/content/dam/cmegroup/rulebook/NYMEX/3/310.pdf','Rule facts only (document copyright 2009 New York Mercantile Exchange); the stated price multiple is used as both premium tick and settlement clearing increment'),
 'nymex_option_rulebook':(CME+'/content/dam/cmegroup/rulebook/NYMEX/3/300.pdf','Rule facts only (document copyright 2009 New York Mercantile Exchange); no document text redistributed'),
 'nymex_option_strikes':(CME+'/trading/files/strike-price/strike-price-listing-and-exercise-procedures-table.xlsx','Table as of 2026-08-24: exercise style, contrary instructions, margin style, at-the-money rule and near-the-money strike increments only'),
}
# code, name, underlying, multiplier, premium increment, strike interval, strike grid
# centre for the first maturity and its monthly step, option expiry dates (Dec 2026-Apr 2027).
OPTION_PRODUCTS=[
 ('nymex_on','NYMEX Henry Hub Natural Gas Option (American) · ON','nymex_ng','10000','0.001','0.05','3.25','0.05',
  ['2026-11-24','2026-12-28','2027-01-26','2027-02-23','2027-03-25']),
 ('nymex_lo','NYMEX Crude Oil Option · LO','nymex_cl','1000','0.01','0.50','60.50','0.50',
  ['2026-11-17','2026-12-16','2027-01-14','2027-02-17','2027-03-17']),
]
OPTION_RULES=[
 ('nymex_on_american_v1','exercise','American-style; exercisable on any business day prior to and until expiration day','not_applicable','nymex_on_rulebook'),
 ('nymex_lo_american_v1','exercise','American-style; exercisable on any business day prior to and until expiration day','not_applicable','nymex_lo_rulebook'),
 ('nymex_on_expiry_v1','expiry','Expires at the close of trading on the business day immediately preceding the expiration of the underlying futures; time of day not stated','unknown','nymex_on_rulebook'),
 ('nymex_lo_expiry_v1','expiry','Expires at the close of trading on the third business day immediately preceding the expiration of the underlying futures; time of day not stated','unknown','nymex_lo_rulebook'),
 ('nymex_itm_settlement_v1','automatic_exercise','On the last exercise day an option in the money by any amount against the underlying futures daily settlement price on its last trading day is exercised; exactly at the money calls are exercised and puts abandoned; otherwise abandoned','not_applicable','nymex_option_rulebook'),
 ('nymex_exercise_notice_v1','exercise_notice','Exercise notice by 4:15 p.m. from customer to clearing member and by 4:30 p.m. or 45 minutes after settlement posting to the clearing house; time zone not stated','unknown','nymex_option_rulebook'),
]

def crude_and_options(R):
    """Reference rows for NYMEX CL and the ON/LO option products and series."""
    def source(code,locator,note):
        return f"INSERT INTO ref_data.sources(catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review) VALUES({R},'{code}','external_document','{locator}','2026-11-01T00:00:00Z','{note}','not_reviewed');"
    refs=[source(code,*value) for code,value in OPTION_SOURCES.items()]
    refs+=[f"INSERT INTO ref_data.dimensions VALUES({R},'contract_count',0,0,0,0,0,0,0);",
      f"INSERT INTO ref_data.units VALUES({R},'option_contract','contract','contract_count');",
      f"INSERT INTO ref_data.commodities VALUES({R},'test_crude','Synthetic crude oil','volume');",
      f"INSERT INTO ref_data.commodity_units VALUES({R},'test_crude','petroleum_barrel','volume');",
      f"INSERT INTO ref_data.measurement_bases VALUES({R},'test_crude_volume','test_crude','volume'),({R},'test_gas_contracts','test_gas','contract_count'),({R},'test_crude_contracts','test_crude','contract_count');",
      f"INSERT INTO ref_data.quantity_specifications VALUES({R},'test_crude_quantity','test_crude','test_crude_volume','petroleum_barrel','volume'),"
      f"({R},'nymex_on_contract','test_gas','test_gas_contracts','option_contract','contract_count'),({R},'nymex_lo_contract','test_crude','test_crude_contracts','option_contract','contract_count');",
      f"INSERT INTO ref_data.locations(catalog_id,revision_id,code,name,type_code,clock_code) VALUES({R},'cushing','Cushing, Oklahoma','test_hub','cpt');",
      f"INSERT INTO ref_data.commodity_locations VALUES({R},'test_crude','cushing');",
      f"INSERT INTO ref_data.quote_specifications VALUES({R},'nymex_cl_quote','test_crude','outright','test_crude_quantity','usd',1,0.01,0.01,-10000,100000);",
      f"INSERT INTO ref_data.delivery_profiles VALUES({R},'nymex_cl_profile','exchange_published','cpt','source_business_days','fixed_total',60);",
      f"INSERT INTO ref_data.futures_products(catalog_id,revision_id,code,name,definition_state,exchange_code,clearing_house_code,commodity_code,location_code,quote_currency_code,lot_unit_code,quote_unit_code,lot_basis,contract_size,quote_multiplier,outright_tick_size,settlement_method,market_clock_code,trading_calendar_code,delivery_calendar_code,settlement_calendar_code) VALUES({R},'nymex_cl','NYMEX Light Sweet Crude Oil · CL','candidate','nymex','cme_clearing','test_crude','cushing','usd','petroleum_barrel','petroleum_barrel','fixed_total',1000,1,0.01,'physical','cpt','source_business_days','source_business_days','source_business_days');",
      f"INSERT INTO ref_data.futures_product_terms VALUES({R},'nymex_cl','nymex_cl_profile',NULL);",
      f"INSERT INTO ref_data.product_evidence VALUES({R},'nymex_cl','nymex_cl','units'),({R},'nymex_cl','nymex_cl_expiry','listing'),({R},'nymex_cl','nymex_cl_expiry','dates');"]
    for day,last in zip(MONTHS,CL_LAST_TRADING):
        start=date.fromisoformat(day);end=date(start.year+(start.month==12),start.month%12+1,1);code='nymex_cl_'+start.strftime('%Y%m%d')
        refs+=[f"INSERT INTO ref_data.futures_series VALUES({R},'{code}','nymex_cl','{day}','candidate','{start}','{end}');",
          f"INSERT INTO ref_data.futures_milestones VALUES({R},'{code}','first_trading','unknown',NULL,NULL,NULL,NULL);",
          f"INSERT INTO ref_data.futures_milestones VALUES({R},'{code}','last_trading','known','{last}',NULL,'cpt','nymex_cl_expiry');"]
    refs+=[f"INSERT INTO ref_data.futures_option_rules VALUES({R},'{code}','{kind}',1,'{text}','{clock}',NULL,'{src}');" for code,kind,text,clock,src in OPTION_RULES]
    for code,name,underlying,multiplier,increment,interval,centre,step,expiries in OPTION_PRODUCTS:
        commodity='test_gas' if underlying=='nymex_ng' else 'test_crude'
        refs.append(f"INSERT INTO ref_data.quote_specifications VALUES({R},'{code}_quote','{commodity}','outright','{commodity}_quantity','usd',1,{increment},{increment},0,100000);")
        refs.append("INSERT INTO ref_data.futures_option_products(catalog_id,revision_id,code,name,definition_state,exchange_code,underlying_product_code,quote_code,lot_quantity_code,multiplier,"
          "exercise_style,settlement_method,premium_style,contrary_instructions_allowed,strike_interval,strike_interval_source_code,exercise_rule_code,expiry_rule_code,automatic_exercise_rule_code,exercise_notice_rule_code) "
          f"VALUES({R},'{code}','{name}','candidate','nymex','{underlying}','{code}_quote','{code}_contract',{multiplier},'american','exercise_into_future','premium_paid_up_front',false,{interval},'nymex_option_strikes',"
          f"'{code}_american_v1','{code}_expiry_v1','nymex_itm_settlement_v1','nymex_exercise_notice_v1');")
        # Three strikes per maturity on the listed grid around the example's centre.
        for i,expiry in enumerate(expiries):
            month=MONTHS[i+1].replace('-','');centre_i=Decimal(centre)+i*Decimal(step)
            for strike in (centre_i-5*Decimal(interval),centre_i,centre_i+5*Decimal(interval)):
                for right in ('call','put'):
                    refs.append(f"INSERT INTO ref_data.futures_option_series VALUES({R},'{code}_{month}_{right[0]}{strike}','{code}','{underlying}_{month}','{right}',{strike},'candidate','{expiry}',NULL,NULL,'{code}_expiry');")
    return refs

def fixture(root):
    reference,old_market,security,manifest=baseline(root)
    reference=reference.replace("'synthetic', 'Synthetic test catalog'","'exchange_example', 'Exchange futures example'").replace("'other_synthetic', 'Other synthetic catalog'","'exchange_other', 'Other exchange fixture catalog'")
    security=security.replace("'preview'","'exchange_preview'")
    # Reuse units/configuration, excluding the unrelated invented venue/contracts.
    for table in ('futures_products','futures_series','futures_milestones','product_evidence','exchanges','clearing_houses','exchange_clearing_houses'):
        reference=re.sub(r'INSERT INTO ref_data\.'+table+r'\b[^;]+;','',reference)
    ref_statements=re.findall(r'(?:INSERT INTO ref_data\.|UPDATE ref_data\.revisions)[^;]+;',old_market)
    seal=next(s for s in ref_statements if s.startswith('UPDATE ref_data.revisions'))
    ref_statements.remove(seal)
    ref_statements=[s for s in ref_statements if not re.match(r'INSERT INTO ref_data\.(futures_series|futures_milestones|futures_product_terms)\b',s)]
    catalog=manifest['catalogId'];revision=manifest['revisionId'];rcat=manifest['referenceCatalogId'];rrev=manifest['referenceRevisionId']
    R=f"'{rcat}','{rrev}'";M=f"'{catalog}','{revision}'";refs=[];defs=[];observations=[]
    refs.append(f"INSERT INTO ref_data.commodity_units VALUES({R},'test_power','megawatt','power');")
    refs.extend([f"INSERT INTO ref_data.exchanges VALUES({R},'nymex','NYMEX'),({R},'ice_futures_us','ICE Futures U.S.');",
      f"INSERT INTO ref_data.clearing_houses VALUES({R},'cme_clearing','CME Clearing'),({R},'ice_clear_europe','ICE Clear Europe');",
      f"INSERT INTO ref_data.exchange_clearing_houses VALUES({R},'nymex','cme_clearing'),({R},'ice_futures_us','ice_clear_europe');"])
    refs.append(f"INSERT INTO ref_data.sources(catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review) VALUES({R},'ltd_qualification','manual_entry','operator:source-date-plus-ice-1800-et-v1','2026-10-18T00:00:00Z','Monthly source expiry date and ordinary last-trading hours; year-end dates excluded pending specific notices','redistributable');")
    refs.append(f"INSERT INTO ref_data.sources(catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review) VALUES({R},'ice_hours','external_document','https://www.ice.com/publicdocs/futures_us/ICE_Futures_US_Regular_Trading_Hours.pdf','2026-10-18T00:00:00Z','Public trading-hour facts and source link only','not_reviewed');")
    refs.append(f"INSERT INTO ref_data.sources(catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review) VALUES({R},'execution_qualification','manual_entry','operator:exchange-example-execution-bound-v1','2026-10-18T00:00:00Z','Explicit application execution bound; not an exchange event','redistributable');")
    # Public specifications name market clocks. Published lot totals are consumed
    # directly; the application eligibility bound is separate from exchange milestones.
    # The preview declares the tzdb release installed on its host; the API refuses any mismatch.
    tzdb=installed_tzdb()
    for clock,zone in [('ept','America/New_York'),('cpt','America/Chicago'),('ppt','America/Los_Angeles')]:
        refs.append(f"INSERT INTO ref_data.clock_rules VALUES({R},'{clock}','iana',NULL,'{zone}','{tzdb}');")
        refs.append(f"INSERT INTO ref_data.calendars VALUES({R},'{clock}_published_peak','{clock}',{64 if clock=='ppt' else 96},'2026-10-01','2027-05-01');")
        for holiday in ['2026-11-26','2026-12-25','2027-01-01']:
            refs.append(f"INSERT INTO ref_data.calendar_days VALUES({R},'{clock}_published_peak','{holiday}',false,'NERC holiday; published lot totals remain authoritative');")
    refs.append(f"INSERT INTO ref_data.calendars VALUES({R},'source_business_days','test_utc',96,'2026-10-13','2026-10-17');")
    locations={'henry_hub':'Henry Hub','chicago':'Chicago Citygate','houston_ship_channel':'Houston Ship Channel','waha':'Waha',
               'pjm_western_hub':'PJM Western Hub','ercot_north':'ERCOT North 345KV Hub','caiso_sp15':'CAISO SP-15','caiso_np15':'CAISO NP-15'}
    for code,name in locations.items():
        commodity='test_power' if code.startswith(('pjm','ercot','caiso')) else 'test_gas'
        location_clock='ppt' if code.startswith('caiso') else 'ept' if code.startswith('pjm') else 'cpt'
        refs.append(f"INSERT INTO ref_data.locations(catalog_id,revision_id,code,name,type_code,clock_code) VALUES({R},'{code}','{name}','test_hub','{location_clock}');")
        refs.append(f"INSERT INTO ref_data.commodity_locations VALUES({R},'{commodity}','{code}');")
    defs.append(f"INSERT INTO market_data.catalogs VALUES('{catalog}','exchange_futures');")
    defs.append(f"INSERT INTO market_data.revisions VALUES({M},{R},'draft',0);")
    defs.append(f"INSERT INTO market_data.revision_periods VALUES({M},'2026-10-01','2028-01-01');")
    contracts={};series_rows=[];indices=set()
    for p in PRODUCTS:
        code,name,location,commodity,symbol,size,basis,settlement,tick,source,mark,frequency=p
        q='test_power_quantity' if commodity=='power' else 'test_gas_quantity'
        quote_unit='megawatt_hour' if commodity=='power' else 'mmbtu_it';lot_unit='megawatt' if basis=='per_delivery_hour' else quote_unit
        exchange,clearing=('nymex','cme_clearing') if code=='nymex_ng' else ('ice_futures_us','ice_clear_europe')
        # Selected synthetic settlement-mark grid follows quotation precision;
        # execution uses the screen minimum from Resolution1, not block minima.
        clearing_increment='0.01' if commodity=='power' else '0.001' if code=='nymex_ng' else '0.0001'
        refs.append(f"INSERT INTO ref_data.sources(catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review) VALUES({R},'{code}','{"synthetic" if source.startswith("synthetic:") else "external_document"}','{source}','2026-10-18T00:00:00Z','Public specification facts and link only; no market prices or document text redistributed','not_reviewed');")
        if frequency=='monthly':
            expiry_source=source.replace('.contractSpecs.html','.calendar.html') if code=='nymex_ng' else source+'/expiry'
            refs.append(f"INSERT INTO ref_data.sources(catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review) VALUES({R},'{code}_expiry','external_document','{expiry_source}','2026-10-18T00:00:00Z','Bounded factual monthly listing/expiry inputs; no prices or source documents','not_reviewed');")
        if code in POWER_LOTS:refs.append(f"INSERT INTO ref_data.sources(catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review) VALUES({R},'{code}_lots','external_document','{source}/expiry','2026-10-18T00:00:00Z','Bounded factual dated contract-size inputs; no prices','not_reviewed');")
        quote_kind='basis' if code in ('ice_dgd','ice_hxs','ice_wah') else 'outright'
        refs.append(f"INSERT INTO ref_data.quote_specifications VALUES({R},'{code}_quote','test_{commodity}','{quote_kind}','{q}','usd',1,{tick},{clearing_increment},-10000,100000); ")
        index_code=(location+('_da_peak' if code in ('ice_pjc','ice_pda','ice_spm','ice_sdp','ice_npm','ice_dpn') else '_rt_peak')) if commodity=='power' else location+('_daily' if frequency=='daily' else '_bidweek')
        clock='ppt' if location.startswith('caiso') else 'cpt' if location=='ercot_north' else 'ept'
        profile_calendar=clock+'_published_peak' if commodity=='power' else 'source_business_days'
        if settlement=='cash':
            if index_code not in indices:
                # Power indexes settle on the NERC-holiday calendar of their market clock.
                refs.append(f"INSERT INTO ref_data.price_index_definitions VALUES({R},'{index_code}','test_{commodity}','{location}','usd','{quote_unit}',1,'{profile_calendar}');")
                indices.add(index_code)
        refs.append(f"INSERT INTO ref_data.delivery_profiles VALUES({R},'{code}_profile','exchange_published','{clock}','{profile_calendar}','{basis}',60);")
        refs.append(f"INSERT INTO ref_data.futures_products(catalog_id,revision_id,code,name,definition_state,exchange_code,clearing_house_code,commodity_code,location_code,quote_currency_code,lot_unit_code,quote_unit_code,lot_basis,contract_size,quote_multiplier,outright_tick_size,settlement_method,market_clock_code,trading_calendar_code,delivery_calendar_code,settlement_calendar_code) VALUES({R},'{code}','{name}','candidate','{exchange}','{clearing}','test_{commodity}','{location}','usd','{lot_unit}','{quote_unit}','{basis}',{size},1,{tick},'{settlement}','{clock}','source_business_days','{profile_calendar}','source_business_days');")
        refs.append(f"INSERT INTO ref_data.futures_product_terms VALUES({R},'{code}','{code}_profile',"+(f"'{index_code}'" if settlement=='cash' and quote_kind!='basis' else 'NULL')+");")
        refs.append(f"INSERT INTO ref_data.product_evidence VALUES({R},'{code}','{code}','units');")
        if frequency=='monthly':
            refs.append(f"INSERT INTO ref_data.product_evidence VALUES({R},'{code}','{code}_expiry','listing'),({R},'{code}','{code}_expiry','dates');")
            if code!='nymex_ng':refs.append(f"INSERT INTO ref_data.product_evidence VALUES({R},'{code}','ice_hours','dates'),({R},'{code}','ltd_qualification','dates');")
        if frequency!='monthly':continue
        defs.append(f"INSERT INTO market_data.curves VALUES({M},'{code}',{R},'test_{commodity}','{location}','{code}_quote','{code}_profile','test_future_target','direct','fixture','trader_mark','{name.replace(chr(39),chr(39)*2)}');")
        contracts[code]=[]
        # Only monthly maturities have confirmed exchange-list records here.
        # Daily product facts remain reference definitions; never invent listings.
        for i,day in enumerate(MONTHS):
            start=date.fromisoformat(day);end=date(start.year+(start.month==12),start.month%12+1,1)
            if code=='nymex_ng':expiry=NG_EXPIRIES[i]
            else:
                ltd=(MONTH_ENDS if commodity=='power' else BEFORE_MONTH)[i]
                expiry=ltd+('T22:00:00Z' if ltd<'2026-11-01' or ltd>='2027-03-14' else 'T23:00:00Z')
            sc=code+'_'+start.strftime('%Y%m%d');cid=key(sc);sid=key('price_'+sc);
            refs.append(f"INSERT INTO ref_data.futures_series VALUES({R},'{sc}','{code}','{start.isoformat()}','candidate','{start}','{end}');")
            if quote_kind=='basis':
                refs.append(f"INSERT INTO ref_data.futures_basis_settlements VALUES({R},'{sc}','{index_code}','nymex_ng_{start.strftime('%Y%m%d')}','index_minus_future_v1');")
            refs.append(f"INSERT INTO ref_data.futures_milestones VALUES({R},'{sc}','first_trading','unknown',NULL,NULL,NULL,NULL);")
            qualified=code=='nymex_ng' or not (expiry[5:7]=='12' and int(expiry[8:10])>=24)
            event=f"'{expiry}'" if qualified else 'NULL'
            last_clock='cpt' if code=='nymex_ng' else 'ept'
            last_source=code+'_expiry' if code=='nymex_ng' else 'ltd_qualification'
            refs.append(f"INSERT INTO ref_data.futures_milestones VALUES({R},'{sc}','last_trading','known','{expiry[:10]}',{event},'{last_clock}','{last_source}');")
            if code in POWER_LOTS:
                total=POWER_LOTS[code][i]
                refs.append(f"INSERT INTO ref_data.futures_lot_resolutions VALUES({R},'{sc}','{q}',{total},{total},'published_contract_size_v1','{code}_lots');")
            if not qualified:continue
            contracts[code].append(cid)
            refs.append(f"INSERT INTO ref_data.futures_execution_policies VALUES({R},'{sc}','contract_lot_execution_pnl_v2','explicit_utc_bound_v1','2026-10-01T00:00:00Z',NULL,NULL,'trader_mark','fixture','execution_qualification');")
            defs.extend([f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'future','{start}','{end}','{expiry}','{sc}');",
              f"INSERT INTO market_data.curve_members VALUES({M},'{code}','{cid}',{len(contracts[code])-1});",
              f"INSERT INTO market_data.series VALUES({M},'{sid}','{sc}',{R},'{cid}','{code}_quote',NULL);"])
            series_rows.append((sid,code,i,Decimal(mark),commodity))
    # Option series name exact underlying series, so they follow every futures series.
    refs.extend(crude_and_options(R))
    frames=[];now=datetime.now(timezone.utc).isoformat()
    for n in range(4):
        at=f'2026-10-{13+n:02d}T16:00:00Z';pins=[]
        for sid,code,i,mark,commodity in series_rows:
            oid=key(f'{sid}:{at}');pins.append(oid)
            value=mark+Decimal(i)*Decimal('0.05' if commodity=='gas' else '0.50')+Decimal(n)*Decimal('0.01' if commodity=='gas' else '0.25')
            observations.append(f"INSERT INTO market_data.observations VALUES('{catalog}','{oid}','{revision}','{sid}',{R},'fixture','synthetic_{oid}','1','trader_mark','final','{at}','{now}',{value},NULL);")
        frames.append({'at':at,'observationIds':pins,'curveContracts':contracts})
    sql='BEGIN;\n'+'\n'.join(ref_statements+refs+[seal]+defs+[f"UPDATE market_data.revisions SET state='sealed' WHERE catalog_id='{catalog}' AND id='{revision}';"]+observations
        +tou_products(catalog,R,{'eastern':'ept','central':'cpt','pacific':'ppt','fixed_est':'synthetic_fixed_est'},now,lambda n:key(f'tou_product_{n}')))+'\nCOMMIT;'
    manifest.update(frames=frames,example={'at':frames[-1]['at'],'curve':'nymex_ng'},schedules=[],
       identityIssuer='https://exchange-preview.invalid',futuresConvention='contract_lot_execution_pnl_v2')
    # All identity/catalog IDs are independent. Shared public role-template IDs
    # remain unchanged, and runtime login roles are created once by the baseline.
    security='\n'.join(line for line in security.splitlines() if not line.startswith('CREATE ROLE ')).replace('https://preview.invalid','https://exchange-preview.invalid')
    trading=baseline_trading(catalog)
    return remap(reference),remap(sql),remap(security),remap(trading),json.loads(remap(json.dumps(manifest)))
