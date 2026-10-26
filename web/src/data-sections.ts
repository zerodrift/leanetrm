// Presentation organization only. Every read still uses the same scoped API/RLS.
export const referenceSections=[
  {value:'units',name:'Units',consumer:'Used by typed quantities, unit prices, futures lots and power entry.'},
  {value:'unit_conversions',name:'Unit conversions',consumer:'Used by the quantity converter for exact compatible unit ratios.'},
  {value:'currencies',name:'Currencies',consumer:'Used by money, quotes, discount rates and currency-separated trade totals.'},
  {value:'commodities',name:'Commodities',consumer:'Used to validate compatible units and filter futures, curves and trade entry.'},
  {value:'locations',name:'Locations',consumer:'Used by exact curve, futures-product and settlement-index location references.'},
  {value:'location_types',name:'Location types',consumer:'Used to validate each location’s explicit business type.'},
  {value:'exchanges',name:'Exchanges',consumer:'Used by futures-product venue references.'},
  {value:'clearing_houses',name:'Clearing houses',consumer:'Used by futures-product clearing references and exchange compatibility checks.'},
  {value:'calendars',name:'Calendars',consumer:'Used by business-day schedules, payment dates and power settlement coverage.'},
  {value:'calendar_days',name:'Calendar dates',consumer:'Used by schedules and power calculations for explicit holiday and business-day overrides.'},
  {value:'futures_products',name:'Futures products',consumer:'Used by futures entry and valuation for contract economics and quote/lot conventions.'},
  {value:'futures_series',name:'Futures contracts',consumer:'Used by curves, instruments, trade holdings and dated risk factors.'},
  {value:'futures_milestones',name:'Contract dates',consumer:'Used for trading eligibility, expiry selection and retained contract schedules.'},
  {value:'price_index_definitions',name:'Price indexes',consumer:'Used by forward settlement targets, floating swaps and the power preview.'},
  {value:'futures_execution_policies',name:'Trading eligibility',consumer:'Used for exact trading eligibility and lifecycle checks against the selected price source.'},
  {value:'interest_rate_definitions',name:'Interest rates',consumer:'Used by zero-rate curves, discount factors and discounted swaps.'},
  {value:'sources',name:'Data sources',consumer:'Used to match observation sources and retain product evidence and provenance.'}
];
export const setupSections=[
  {value:'clock_rules',name:'Market clocks',consumer:'Used by contract dates, calendars, schedules, TOUs and elapsed-hour power integration.'},
  {value:'delivery_profiles',name:'Delivery patterns',consumer:'Used by declared constituent and delivery mappings in composed curves.'},
  {value:'settlement_targets',name:'Settlement rules',consumer:'Used for explicit futures/index settlement targets and swap/power construction.'},
  {value:'quote_specifications',name:'Price conventions',consumer:'Used to validate price units, multipliers, domains and clearing grids.'},
  {value:'quantity_specifications',name:'Quantity definitions',consumer:'Used by amounts, unit conversion and side quantities for exact commodity/unit/basis identity.'},
  {value:'measurement_bases',name:'Measurement bases',consumer:'Used by compatible quantity/price arithmetic and declared conversion-ratio orientation.'},
  {value:'value_specifications',name:'Numeric input types',consumer:'Used by numeric series and typed multiplication in scale/add curves.'}
];
export const sectionGroups=[
  {name:'Reference data',description:'Units, currencies, locations and contract details.',sections:referenceSections},
  {name:'Pricing & delivery conventions',description:'Clocks, delivery patterns, settlement rules and price conventions.',sections:setupSections}
];
export function dataSections(available:string[]){return sectionGroups.map(group=>({...group,sections:group.sections.filter(section=>available.includes(section.value))})).filter(group=>group.sections.length);}
