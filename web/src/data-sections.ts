// Presentation organization only. Every read still uses the same scoped API/RLS.
export const referenceSections=[
  {value:'units',name:'Units'},{value:'unit_conversions',name:'Unit conversions'},
  {value:'currencies',name:'Currencies'},{value:'commodities',name:'Commodities'},
  {value:'locations',name:'Locations'},{value:'location_types',name:'Location types'},
  {value:'exchanges',name:'Exchanges'},{value:'clearing_houses',name:'Clearing houses'},
  {value:'calendars',name:'Calendars'},{value:'calendar_days',name:'Calendar dates'},
  {value:'futures_products',name:'Futures products'},{value:'futures_series',name:'Futures contracts'},
  {value:'futures_milestones',name:'Contract dates'},{value:'price_index_definitions',name:'Price indexes'},
  {value:'interest_rate_definitions',name:'Interest rates'},
  {value:'sources',name:'Data sources'}
];
export const setupSections=[
  {value:'clock_rules',name:'Market clocks'},
  {value:'delivery_profiles',name:'Delivery patterns'},
  {value:'settlement_targets',name:'Settlement rules'},
  {value:'quote_specifications',name:'Price conventions'},
  {value:'quantity_specifications',name:'Quantity definitions'},
  {value:'measurement_bases',name:'Measurement bases'},
  {value:'value_specifications',name:'Numeric input types'},
  {value:'cash_rounding_conventions',name:'Cash rounding'}
];
export const sectionGroups=[
  {name:'Reference data',description:'Units, currencies, locations and contract details.',sections:referenceSections},
  {name:'Pricing & delivery conventions',description:'Clocks, delivery patterns, settlement rules and price conventions.',sections:setupSections}
];
export function dataSections(available:string[]){return sectionGroups.map(group=>({...group,sections:group.sections.filter(section=>available.includes(section.value))})).filter(group=>group.sections.length);}
// Supplemental reference catalogs replace matching tables, exactly as the viewer does.
export function populatedDataSections(references:Record<string,readonly unknown[]>,supplement:Record<string,readonly unknown[]>={}){
 const tables={...references,...supplement};
 return dataSections(Object.keys(tables).filter(key=>tables[key].length>0));
}
