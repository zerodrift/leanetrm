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
from datetime import time
from decimal import ROUND_HALF_EVEN, localcontext
import importlib.util
from zoneinfo import ZoneInfo
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
 ('nymex_cl','NYMEX Light Sweet Crude Oil · CL','cushing','crude','CL','1000','fixed_total','physical','0.01','https://www.cmegroup.com/markets/energy/crude-oil/light-sweet-crude.contractSpecs.html','60.00','monthly'),
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
# Source-confirmed NYMEX last trading dates (NG checked 2026-10-18, CL 2026-11-01).
NYMEX_LAST_TRADING={'nymex_ng':['2026-10-28','2026-11-25','2026-12-29','2027-01-27','2027-02-24','2027-03-29'],
                    'nymex_cl':['2026-10-20','2026-11-20','2026-12-21','2027-01-20','2027-02-22','2027-03-22']}
# Quantity specification and unit of each commodity's quote.
QUOTE_UNITS={'gas':('test_gas_quantity','mmbtu_it'),'power':('test_power_quantity','megawatt_hour'),'crude':('test_crude_quantity','petroleum_barrel')}
# Mark grid: monthly step and daily step of the generated prices by commodity.
MARK_STEPS={'gas':('0.05','0.01'),'power':('0.50','0.25'),'crude':('0.50','0.25')}
# The observation role of each product's generated prices; NYMEX futures underlie options valued at daily settlements.
PRICE_ROLES={'nymex_ng':'daily_settlement','nymex_cl':'daily_settlement'}
CLOCKS={'ept':'America/New_York','cpt':'America/Chicago','ppt':'America/Los_Angeles'}
# Configured local close-of-trading times: (local time, clock, qualification source). Instants are derived from a
# confirmed date and this configuration, in SQL where stored and in the managed backend where materialized.
NYMEX_CLOSE=('14:30','ept','nymex_close_qualification')
ICE_CLOSE=('18:00','ept','ltd_qualification')
LAST_TRADING_CLOSE={'nymex_ng':NYMEX_CLOSE,'nymex_cl':NYMEX_CLOSE,**{p[0]:ICE_CLOSE for p in PRODUCTS if p[0].startswith('ice_') and p[11]=='monthly'}}
OPTION_EXPIRY_CLOSE={'nymex_on':NYMEX_CLOSE,'nymex_lo':NYMEX_CLOSE}

# NYMEX options, from the public specifications, calendars and rulebooks retrieved
# 2026-11-01. Example option series lie on the listed strike grid.
CME='https://www.cmegroup.com'
OPTION_SOURCES={
 'nymex_close_qualification':('manual_entry','operator:nymex-close-1430-new-york-v1','Operator qualification of the close of trading; not an exchange-published instant'),
 'nymex_on':('external_document',CME+'/markets/energy/natural-gas/natural-gas.contractSpecs.options.html','Public specification facts and link only; no market prices or document text redistributed'),
 'nymex_lo':('external_document',CME+'/markets/energy/crude-oil/light-sweet-crude.contractSpecs.options.html','Public specification facts and link only; no market prices or document text redistributed'),
 'nymex_on_expiry':('external_document',CME+'/markets/energy/natural-gas/natural-gas.calendar.options.html','Bounded factual option expiration dates; no prices or source documents'),
 'nymex_lo_expiry':('external_document',CME+'/markets/energy/crude-oil/light-sweet-crude.calendar.options.html','Bounded factual option expiration dates; no prices or source documents'),
 'nymex_on_rulebook':('external_document',CME+'/content/dam/cmegroup/rulebook/NYMEX/3/370.pdf','Rule facts only (document copyright 2009 New York Mercantile Exchange); the stated price multiple is used as both premium tick and settlement clearing increment'),
 'nymex_lo_rulebook':('external_document',CME+'/content/dam/cmegroup/rulebook/NYMEX/3/310.pdf','Rule facts only (document copyright 2009 New York Mercantile Exchange); the stated price multiple is used as both premium tick and settlement clearing increment'),
 'nymex_option_rulebook':('external_document',CME+'/content/dam/cmegroup/rulebook/NYMEX/3/300.pdf','Rule facts only (document copyright 2009 New York Mercantile Exchange); no document text redistributed'),
 'nymex_option_strikes':('external_document',CME+'/trading/files/strike-price/strike-price-listing-and-exercise-procedures-table.xlsx','Table as of 2026-08-24: exercise style, contrary instructions, margin style, at-the-money rule and near-the-money strike increments only'),
}
# code, name, underlying, multiplier, premium increment, strike interval, strike grid
# centre for the first maturity and its monthly step, option expiry dates (Dec 2026-Apr 2027),
# and the synthetic smile of the generated settlements: (base, skew, curve, term) of
# volatility = base + skew*k + curve*k^2 + term*T, with k = ln(strike/futures) and T in years.
# Every smile value is synthetic, not a market level.
OPTION_PRODUCTS=[
 ('nymex_on','NYMEX Henry Hub Natural Gas Option (American) · ON','nymex_ng','10000','0.001','0.05','3.25','0.05',
  ['2026-11-24','2026-12-28','2027-01-26','2027-02-23','2027-03-25'],('0.55','-0.20','2.00','0.10')),
 ('nymex_lo','NYMEX Crude Oil Option · LO','nymex_cl','1000','0.01','0.50','60.50','0.50',
  ['2026-11-17','2026-12-16','2027-01-14','2027-02-17','2027-03-17'],('0.36','-0.30','4.00','0.08')),
]
# Each maturity lists these strikes, in strike intervals from its centre, each as a call and a put.
STRIKE_STEPS=(-6,-4,-2,0,2,4,6)
# Fixed synthetic volatility offsets of single series from the smile: the rich (+) and cheap (-) points a fitted
# surface shows. Each is an out-of-the-money series at every close, away from the missing-settlement cases.
SMILE_OFFSETS={
 'nymex_on_20261201_c3.45':Decimal('0.03'),'nymex_on_20270201_p3.15':Decimal('-0.03'),
 'nymex_on_20270301_c3.60':Decimal('-0.03'),'nymex_on_20270401_p3.25':Decimal('0.03'),
 'nymex_lo_20261201_c62.50':Decimal('0.03'),'nymex_lo_20270201_p59.50':Decimal('-0.03'),
 'nymex_lo_20270301_c64.00':Decimal('-0.03'),'nymex_lo_20270401_p60.50':Decimal('0.03'),
}
# Synthetic USD zero rates (simple, ACT/365F) at their pillar dates; the first pillar is the example premium payment date.
USD_RATES=[('2026-10-19',Decimal('0.04')),('2027-06-30',Decimal('0.045'))]
# The close, the option series and the underlying future whose daily settlements that close does not pin: the
# missing-settlement and missing-underlying cases.
MISSING_SETTLEMENT=('2026-10-13T17:00:00Z','nymex_on_20270101_c3.10','nymex_cl_20270101')
# One deliberately deep in-the-money near-expiry series settles exactly at intrinsic: the fallback case.
AT_INTRINSIC=('nymex_on','20261201','call',Decimal('2.00'))
OPTION_RULES=[
 ('nymex_on_american_v1','exercise','American-style; exercisable on any business day prior to and until expiration day','not_applicable','nymex_on_rulebook'),
 ('nymex_lo_american_v1','exercise','American-style; exercisable on any business day prior to and until expiration day','not_applicable','nymex_lo_rulebook'),
 ('nymex_on_expiry_v1','expiry','Expires at the close of trading on the business day immediately preceding the expiration of the underlying futures; time of day not stated','unknown','nymex_on_rulebook'),
 ('nymex_lo_expiry_v1','expiry','Expires at the close of trading on the third business day immediately preceding the expiration of the underlying futures; time of day not stated','unknown','nymex_lo_rulebook'),
 ('nymex_itm_settlement_v1','automatic_exercise','On the last exercise day an option in the money by any amount against the underlying futures daily settlement price on its last trading day is exercised; exactly at the money calls are exercised and puts abandoned; otherwise abandoned','not_applicable','nymex_option_rulebook'),
 ('nymex_exercise_notice_v1','exercise_notice','Exercise notice by 4:15 p.m. from customer to clearing member and by 4:30 p.m. or 45 minutes after settlement posting to the clearing house; time zone not stated','unknown','nymex_option_rulebook'),
]
# The automatic exercise rule's stated parameters: any in-the-money amount exercises; at the money, calls exercise and puts abandon.
AUTOMATIC_EXERCISE={'nymex_itm_settlement_v1':"'settlement_moneyness_v1',0,'exercise','abandon'"}

def source_row(R,code,kind,locator,note,retrieved='2026-11-01'):
    review='redistributable' if kind=='manual_entry' else 'not_reviewed'
    return f"INSERT INTO ref_data.sources(catalog_id,revision_id,code,kind,locator,retrieved_at,license_reference,distribution_review) VALUES({R},'{code}','{kind}','{locator}','{retrieved}T00:00:00Z','{note}','{review}');"

def close_time(R,milestone,product,setting):
    local,clock,source=setting;column='futures_product_code' if milestone=='last_trading' else 'option_product_code'
    return (f"INSERT INTO ref_data.trading_close_times(catalog_id,revision_id,code,version,milestone,{column},local_time,clock_code,source_code) "
            f"VALUES({R},'{product}_{milestone}_close',1,'{milestone}','{product}','{local}','{clock}','{source}');")

def local_instant(day,setting):
    """The same configured derivation as ref_data.close_instant, for the generated prices' years to expiry only."""
    local,clock,_=setting
    return datetime.combine(date.fromisoformat(day),time.fromisoformat(local),ZoneInfo(CLOCKS[clock])).astimezone(timezone.utc)

def black76(root):
    spec=importlib.util.spec_from_file_location('futures_option_oracle',root/'tests/fixtures/futures_option_oracle.py')
    oracle=importlib.util.module_from_spec(spec);spec.loader.exec_module(oracle)
    return oracle.black76

def crude_reference(R):
    """The crude commodity, its barrel quantity and the Cushing location used by NYMEX CL and LO."""
    return [f"INSERT INTO ref_data.dimensions VALUES({R},'contract_count',0,0,0,0,0,0,0);",
      f"INSERT INTO ref_data.units VALUES({R},'option_contract','contract','contract_count');",
      f"INSERT INTO ref_data.commodities VALUES({R},'test_crude','Synthetic crude oil','volume');",
      f"INSERT INTO ref_data.commodity_units VALUES({R},'test_crude','petroleum_barrel','volume');",
      f"INSERT INTO ref_data.measurement_bases VALUES({R},'test_crude_volume','test_crude','volume'),({R},'test_gas_contracts','test_gas','contract_count'),({R},'test_crude_contracts','test_crude','contract_count');",
      f"INSERT INTO ref_data.quantity_specifications VALUES({R},'test_crude_quantity','test_crude','test_crude_volume','petroleum_barrel','volume'),"
      f"({R},'nymex_on_contract','test_gas','test_gas_contracts','option_contract','contract_count'),({R},'nymex_lo_contract','test_crude','test_crude_contracts','option_contract','contract_count');"]

def option_reference(R):
    """Option rules, products and series; each series expires at its product's configured time on its confirmed date."""
    refs=[f"INSERT INTO ref_data.futures_option_rules VALUES({R},'{code}','{kind}',1,'{text}','{clock}',NULL,'{src}',{AUTOMATIC_EXERCISE.get(code,'NULL,NULL,NULL,NULL')});"
          for code,kind,text,clock,src in OPTION_RULES]
    series=[]
    for code,name,underlying,multiplier,increment,interval,centre,step,expiries,_ in OPTION_PRODUCTS:
        commodity='test_gas' if underlying=='nymex_ng' else 'test_crude'
        refs.append(f"INSERT INTO ref_data.quote_specifications VALUES({R},'{code}_quote','{commodity}','outright','{commodity}_quantity','usd',1,{increment},{increment},0,100000);")
        refs.append("INSERT INTO ref_data.futures_option_products(catalog_id,revision_id,code,name,definition_state,exchange_code,underlying_product_code,quote_code,lot_quantity_code,multiplier,"
          "exercise_style,settlement_method,premium_style,contrary_instructions_allowed,strike_interval,strike_interval_source_code,exercise_rule_code,expiry_rule_code,automatic_exercise_rule_code,exercise_notice_rule_code) "
          f"VALUES({R},'{code}','{name}','candidate','nymex','{underlying}','{code}_quote','{code}_contract',{multiplier},'american','exercise_into_future','premium_paid_up_front',false,{interval},'nymex_option_strikes',"
          f"'{code}_american_v1','{code}_expiry_v1','nymex_itm_settlement_v1','nymex_exercise_notice_v1');")
        refs.append(close_time(R,'option_expiry',code,OPTION_EXPIRY_CLOSE[code]))
        # Seven strikes per maturity on the listed grid around the example's centre.
        for i,expiry in enumerate(expiries):
            month=MONTHS[i+1].replace('-','');centre_i=Decimal(centre)+i*Decimal(step)
            strikes=[(right,centre_i+n*Decimal(interval)) for n in STRIKE_STEPS for right in ('call','put')]
            if AT_INTRINSIC[:2]==(code,month):strikes.append(AT_INTRINSIC[2:])
            for right,strike in strikes:
                sc=f'{code}_{month}_{right[0]}{strike}'
                refs.append(f"INSERT INTO ref_data.futures_option_series VALUES({R},'{sc}','{code}','{underlying}_{month}','{right}',{strike},'candidate','{expiry}',"
                    f"ref_data.close_instant({R},'option_expiry','{code}','{expiry}'),NULL,'{code}_expiry');")
                series.append((sc,code,f'{underlying}_{month}',right,strike,expiry))
    return refs,series

def smile_volatility(smile,futures,strike,years,offset):
    """The synthetic smile's volatility at a strike and time to expiry, plus the series' fixed synthetic offset."""
    base,skew,curve,term=map(Decimal,smile)
    with localcontext() as ctx:
        ctx.prec=60
        k=(strike/futures).ln()
        return base+skew*k+curve*k*k+term*years+offset

def option_settlement(price,futures,strike,right,years,volatility,increment):
    """A synthetic European Black-76 premium at a zero rate, on the clearing increment, above intrinsic plus one increment."""
    with localcontext() as ctx:
        ctx.prec=60
        call,put=price(futures,strike,volatility,years,'1')
    value=(call if right=='call' else put).quantize(Decimal(increment),rounding=ROUND_HALF_EVEN)
    intrinsic=max(futures-strike if right=='call' else strike-futures,Decimal(0))
    if not intrinsic+Decimal(increment)<value<=(futures if right=='call' else strike):
        raise ValueError(f'Synthetic settlement {value} for {right} {strike} on {futures} leaves the solvable range')
    return value

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
    refs.append(source_row(R,'ltd_qualification','manual_entry','operator:source-date-plus-ice-1800-et-v1','Monthly source expiry date and ordinary last-trading hours; year-end dates excluded pending specific notices','2026-10-18'))
    refs.append(source_row(R,'ice_hours','external_document','https://www.ice.com/publicdocs/futures_us/ICE_Futures_US_Regular_Trading_Hours.pdf','Public trading-hour facts and source link only','2026-10-18'))
    refs.append(source_row(R,'execution_qualification','manual_entry','operator:exchange-example-execution-bound-v1','Explicit application execution bound; not an exchange event','2026-10-18'))
    refs.extend(source_row(R,code,*value) for code,value in OPTION_SOURCES.items())
    refs.extend(crude_reference(R))
    # Public specifications name market clocks. Published lot totals are consumed
    # directly; the application eligibility bound is separate from exchange milestones.
    # The preview declares the tzdb release installed on its host; the API refuses any mismatch.
    tzdb=installed_tzdb()
    for clock,zone in CLOCKS.items():
        refs.append(f"INSERT INTO ref_data.clock_rules VALUES({R},'{clock}','iana',NULL,'{zone}','{tzdb}');")
        refs.append(f"INSERT INTO ref_data.calendars VALUES({R},'{clock}_published_peak','{clock}',{64 if clock=='ppt' else 96},'2026-10-01','2027-05-01');")
        for holiday in ['2026-11-26','2026-12-25','2027-01-01']:
            refs.append(f"INSERT INTO ref_data.calendar_days VALUES({R},'{clock}_published_peak','{holiday}',false,'NERC holiday; published lot totals remain authoritative');")
    refs.append(f"INSERT INTO ref_data.calendars VALUES({R},'source_business_days','test_utc',96,'2026-10-13','2026-10-17');")
    # code: name, commodity, clock
    locations={'henry_hub':('Henry Hub','test_gas','cpt'),'chicago':('Chicago Citygate','test_gas','cpt'),'houston_ship_channel':('Houston Ship Channel','test_gas','cpt'),
               'waha':('Waha','test_gas','cpt'),'cushing':('Cushing, Oklahoma','test_crude','cpt'),'pjm_western_hub':('PJM Western Hub','test_power','ept'),
               'ercot_north':('ERCOT North 345KV Hub','test_power','cpt'),'caiso_sp15':('CAISO SP-15','test_power','ppt'),'caiso_np15':('CAISO NP-15','test_power','ppt')}
    for code,(name,commodity,location_clock) in locations.items():
        refs.append(f"INSERT INTO ref_data.locations(catalog_id,revision_id,code,name,type_code,clock_code) VALUES({R},'{code}','{name}','test_hub','{location_clock}');")
        refs.append(f"INSERT INTO ref_data.commodity_locations VALUES({R},'{commodity}','{code}');")
    defs.append(f"INSERT INTO market_data.catalogs VALUES('{catalog}','exchange_futures');")
    defs.append(f"INSERT INTO market_data.revisions VALUES({M},{R},'draft',0);")
    defs.append(f"INSERT INTO market_data.revision_periods VALUES({M},'2026-10-01','2028-01-01');")
    contracts={};series_rows=[];indices=set();delivery={};profiles={}
    for p in PRODUCTS:
        code,name,location,commodity,symbol,size,basis,settlement,tick,source,mark,frequency=p
        q,quote_unit=QUOTE_UNITS[commodity];lot_unit='megawatt' if basis=='per_delivery_hour' else quote_unit
        nymex=code.startswith('nymex_');retrieved='2026-11-01' if code=='nymex_cl' else '2026-10-18'
        exchange,clearing=('nymex','cme_clearing') if nymex else ('ice_futures_us','ice_clear_europe')
        # Selected synthetic settlement-mark grid follows quotation precision;
        # execution uses the screen minimum from Resolution1, not block minima.
        clearing_increment='0.01' if commodity in ('power','crude') else '0.001' if code=='nymex_ng' else '0.0001'
        refs.append(source_row(R,code,'synthetic' if source.startswith('synthetic:') else 'external_document',source,'Public specification facts and link only; no market prices or document text redistributed',retrieved))
        if frequency=='monthly':
            expiry_source=source.replace('.contractSpecs.html','.calendar.html') if nymex else source+'/expiry'
            refs.append(source_row(R,code+'_expiry','external_document',expiry_source,'Bounded factual monthly listing/expiry inputs; no prices or source documents',retrieved))
        if code in POWER_LOTS:refs.append(source_row(R,code+'_lots','external_document',source+'/expiry','Bounded factual dated contract-size inputs; no prices','2026-10-18'))
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
            if not nymex:refs.append(f"INSERT INTO ref_data.product_evidence VALUES({R},'{code}','ice_hours','dates');")
            refs.append(f"INSERT INTO ref_data.product_evidence VALUES({R},'{code}','{LAST_TRADING_CLOSE[code][2]}','dates');")
        if frequency!='monthly':continue
        refs.append(close_time(R,'last_trading',code,LAST_TRADING_CLOSE[code]))
        role=PRICE_ROLES.get(code,'trader_mark');profiles[code]=(location,f'test_{commodity}')
        defs.append(f"INSERT INTO market_data.curves VALUES({M},'{code}',{R},'test_{commodity}','{location}','{code}_quote','{code}_profile','test_future_target','direct','fixture','{role}','{name.replace(chr(39),chr(39)*2)}');")
        contracts[code]=[]
        # Only monthly maturities have confirmed exchange-list records here.
        # Daily product facts remain reference definitions; never invent listings.
        for i,day in enumerate(MONTHS):
            start=date.fromisoformat(day);end=date(start.year+(start.month==12),start.month%12+1,1)
            ltd=NYMEX_LAST_TRADING[code][i] if nymex else (MONTH_ENDS if commodity=='power' else BEFORE_MONTH)[i]
            sc=code+'_'+start.strftime('%Y%m%d');cid=key(sc);sid=key('price_'+sc);delivery[sc]=(start,end)
            refs.append(f"INSERT INTO ref_data.futures_series VALUES({R},'{sc}','{code}','{start.isoformat()}','candidate','{start}','{end}');")
            if quote_kind=='basis':
                refs.append(f"INSERT INTO ref_data.futures_basis_settlements VALUES({R},'{sc}','{index_code}','nymex_ng_{start.strftime('%Y%m%d')}','index_minus_future_v1');")
            refs.append(f"INSERT INTO ref_data.futures_milestones VALUES({R},'{sc}','first_trading','unknown',NULL,NULL,NULL,NULL);")
            # ICE year-end sessions keep unknown cutoffs until specific notices qualify them.
            qualified=nymex or not (ltd[5:7]=='12' and int(ltd[8:10])>=24)
            event=f"ref_data.close_instant({R},'last_trading','{code}','{ltd}')" if qualified else 'NULL'
            last_clock=LAST_TRADING_CLOSE[code][1]
            last_source=code+'_expiry' if nymex else 'ltd_qualification'
            refs.append(f"INSERT INTO ref_data.futures_milestones VALUES({R},'{sc}','last_trading','known','{ltd}',{event},'{last_clock}','{last_source}');")
            if code in POWER_LOTS:
                total=POWER_LOTS[code][i]
                refs.append(f"INSERT INTO ref_data.futures_lot_resolutions VALUES({R},'{sc}','{q}',{total},{total},'published_contract_size_v1','{code}_lots');")
            if not qualified:continue
            contracts[code].append(cid)
            refs.append(f"INSERT INTO ref_data.futures_execution_policies VALUES({R},'{sc}','contract_lot_execution_pnl_v2','explicit_utc_bound_v1','2026-10-01T00:00:00Z',NULL,NULL,'{role}','fixture','execution_qualification');")
            defs.extend([f"INSERT INTO market_data.contracts SELECT {M},'{cid}',{R},'future','{start}','{end}',event_at,'{sc}' FROM ref_data.futures_milestones WHERE (catalog_id,revision_id,series_code,milestone)=({R},'{sc}','last_trading');",
              f"INSERT INTO market_data.curve_members VALUES({M},'{code}','{cid}',{len(contracts[code])-1});",
              f"INSERT INTO market_data.series VALUES({M},'{sid}','{sc}',{R},'{cid}','{code}_quote',NULL);"])
            series_rows.append((sid,sc,code,i,Decimal(mark),commodity,role))
    # Option series name exact underlying series, so they follow every futures series.
    option_refs,option_series=option_reference(R)
    refs.extend(option_refs)
    products={p[0]:p for p in OPTION_PRODUCTS};option_rows=[]
    for code,(_,_,underlying,*_rest) in products.items():
        location,commodity=profiles[underlying];contracts[code]=[]
        defs.append(f"INSERT INTO market_data.curves VALUES({M},'{code}',{R},'{commodity}','{location}','{code}_quote','{underlying}_profile','test_future_target','direct','fixture','daily_settlement','{products[code][1]}');")
    for sc,code,underlying,right,strike,expiry in option_series:
        cid=key(sc);sid=key('price_'+sc);start,end=delivery[underlying];contracts[code].append(cid)
        defs.extend([f"INSERT INTO market_data.contracts SELECT {M},'{cid}',{R},'future_option','{start}','{end}',expiry_at,NULL,code FROM ref_data.futures_option_series WHERE (catalog_id,revision_id,code)=({R},'{sc}');",
          f"INSERT INTO market_data.curve_members VALUES({M},'{code}','{cid}',{len(contracts[code])-1});",
          f"INSERT INTO market_data.series VALUES({M},'{sid}','{sc}',{R},'{cid}','{code}_quote',NULL);"])
        option_rows.append((sid,sc,code,underlying,right,strike,local_instant(expiry,OPTION_EXPIRY_CLOSE[code])))
    # One synthetic USD zero-rate curve, the currency's default discounting for option values and premiums.
    defs.append("INSERT INTO market_data.rate_curves(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,interest_rate_code,source_code,date_basis,name) "
                f"VALUES({M},'usd_rates',{R},'test_interest','fixture','valuation_utc_date','Synthetic USD rates');")
    rate_rows=[]
    for n,(maturity,rate) in enumerate(USD_RATES):
        sid=key('rate_'+maturity);rate_rows.append((sid,rate))
        defs.extend(["INSERT INTO market_data.series(catalog_id,revision_id,id,code,reference_catalog_id,reference_revision_id,contract_id,quote_code,value_specification_code) "
                     f"VALUES({M},'{sid}','usd_rate_{n}',{R},NULL,NULL,'test_zero_rate');",
                     "INSERT INTO market_data.rate_curve_points(catalog_id,revision_id,curve_code,maturity,series_id,ordinal) "
                     f"VALUES({M},'usd_rates','{maturity}','{sid}',{n});"])
    unlisted=SMILE_OFFSETS.keys()-{row[1] for row in option_rows}
    if unlisted:raise ValueError(f'Smile offsets name unlisted series: {sorted(unlisted)}')
    if any(row[1] in SMILE_OFFSETS and (row[1]==MISSING_SETTLEMENT[1] or row[3]==MISSING_SETTLEMENT[2]) for row in option_rows):
        raise ValueError('Smile offsets must stay away from the missing-settlement series and the missing underlying.')
    price=black76(root)
    frames=[];now=datetime.now(timezone.utc).isoformat()
    for n in range(4):
        at=f'2026-10-{13+n:02d}T16:00:00Z';pins=[];settled={}
        def observe(sid,value,role):
            oid=key(f'{sid}:{at}');pins.append(oid)
            observations.append(f"INSERT INTO market_data.observations VALUES('{catalog}','{oid}','{revision}','{sid}',{R},'fixture','synthetic_{oid}','1','{role}','final','{at}','{now}',{value},NULL);")
        for sid,sc,code,i,mark,commodity,role in series_rows:
            month,day=MARK_STEPS[commodity]
            settled[sc]=mark+Decimal(i)*Decimal(month)+Decimal(n)*Decimal(day)
            observe(sid,settled[sc],role)
        for sid,sc,code,underlying,right,strike,expiry_at in option_rows:
            futures=settled[underlying];increment=products[code][4]
            if sc in SMILE_OFFSETS and not (strike<futures if right=='put' else strike>futures):
                raise ValueError(f'Smile offset series {sc} is not out of the money against {futures} at {at}')
            if AT_INTRINSIC==(code,sc.split('_')[2],right,strike):value=max(futures-strike if right=='call' else strike-futures,Decimal(0))
            else:
                years=Decimal((expiry_at-datetime.fromisoformat(at)).total_seconds())/Decimal(365*86400)
                volatility=smile_volatility(products[code][9],futures,strike,years,SMILE_OFFSETS.get(sc,Decimal(0)))
                value=option_settlement(price,futures,strike,right,years,volatility,increment)
            observe(sid,value,'daily_settlement')
        for sid,rate in rate_rows:observe(sid,rate,'parameter')
        frames.append({'at':at,'observationIds':pins,'curveContracts':contracts})
    # A later close of the first day, in time order, that pins every input of that day except one option series' settlement
    # and one option underlying's settlement.
    missing_at,missing_series,missing_underlying=MISSING_SETTLEMENT
    omitted={key(f"{key('price_'+code)}:{frames[0]['at']}") for code in (missing_series,missing_underlying)}
    if not omitted<=set(frames[0]['observationIds']):raise ValueError('The missing-settlement series and underlying must be settled at the first close.')
    frames.insert(1,{'at':missing_at,'observationIds':[o for o in frames[0]['observationIds'] if o not in omitted],'curveContracts':contracts})
    sql='BEGIN;\n'+'\n'.join(ref_statements+refs+[seal]+defs+[f"UPDATE market_data.revisions SET state='sealed' WHERE catalog_id='{catalog}' AND id='{revision}';"]+observations
        +tou_products(catalog,R,{'eastern':'ept','central':'cpt','pacific':'ppt','fixed_est':'synthetic_fixed_est'},now,lambda n:key(f'tou_product_{n}')))+'\nCOMMIT;'
    manifest.update(frames=frames,example={'at':max(f['at'] for f in frames),'curve':'nymex_ng'},schedules=[],
       missingSettlement={'at':missing_at,'series':missing_series,'underlying':missing_underlying},
       identityIssuer='https://exchange-preview.invalid',futuresConvention='contract_lot_execution_pnl_v2')
    # All identity/catalog IDs are independent. Shared public role-template IDs
    # remain unchanged, and runtime login roles are created once by the baseline.
    security='\n'.join(line for line in security.splitlines() if not line.startswith('CREATE ROLE ')).replace('https://preview.invalid','https://exchange-preview.invalid')
    trading=baseline_trading(catalog)
    if trading.count('COMMIT;')!=1:raise ValueError('The baseline trading fixture must end in exactly one COMMIT.')
    trading=trading.replace('COMMIT;',
        "INSERT INTO trading.currency_discount_defaults(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,currency_code,curve_code,"
        f"interpolation_policy,publication_order,effective_at) VALUES('{catalog}','{key('usd_discount_default')}','{revision}',{R},'usd','usd_rates',"
        "'log_discount_geometric_root_to_even_18_v1',1,'2026-10-01 00:00:00+00');\nCOMMIT;")
    return remap(reference),remap(sql),remap(security),remap(trading),json.loads(remap(json.dumps(manifest)))
