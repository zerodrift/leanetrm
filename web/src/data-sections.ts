// Presentation organization only. Every read still uses the same scoped API/RLS.
// Contracts, contract dates and trading eligibility are read inside their product, not as tabs.
export const foldedIntoProduct=['futures_series','futures_milestones','futures_execution_policies'] as const;
// Categories whose rows have no code of their own, and so no page of their own.
export const keylessCategories=['unit_conversions','calendar_days'] as const;
export const referenceGroups=[
  {name:'Markets & contracts',sections:[
    {value:'futures_products',name:'Futures',consumer:'Used by futures entry and valuation for contract economics and quote/lot conventions.'},
    {value:'price_index_definitions',name:'Price indexes',consumer:'Used by forward settlement targets, floating swaps and the power preview.'},
    {value:'exchanges',name:'Exchanges',consumer:'Used by futures-product venue references.'},
    {value:'clearing_houses',name:'Clearing houses',consumer:'Used by futures-product clearing references and exchange compatibility checks.'},
    {value:'interest_rate_definitions',name:'Interest rates',consumer:'Used by zero-rate curves, discount factors and discounted swaps.'},
    {value:'sources',name:'Data sources',consumer:'Used to match observation sources and retain product evidence and provenance.'}
  ]},
  {name:'Commodities & units',sections:[
    {value:'commodities',name:'Commodities',consumer:'Used to validate compatible units and filter futures, curves and trade entry.'},
    {value:'units',name:'Units',consumer:'Used by typed quantities, unit prices, futures lots and power entry.'},
    {value:'unit_conversions',name:'Unit conversions',consumer:'Used by the quantity converter for exact compatible unit ratios.'},
    {value:'currencies',name:'Currencies',consumer:'Used by money, quotes, discount rates and currency-separated trade totals.'},
    {value:'measurement_bases',name:'Measurement bases',consumer:'Used by compatible quantity/price arithmetic and declared conversion-ratio orientation.'},
    {value:'quantity_specifications',name:'Quantity definitions',consumer:'Used by amounts, unit conversion and side quantities for exact commodity/unit/basis identity.'},
    {value:'value_specifications',name:'Numeric input types',consumer:'Used by numeric series and typed multiplication in scale/add curves.'}
  ]},
  {name:'Locations',sections:[
    {value:'locations',name:'Locations',consumer:'Used by exact curve, futures-product and settlement-index location references.'},
    {value:'location_types',name:'Location types',consumer:'Used to validate each location’s explicit business type.'}
  ]},
  {name:'Calendars',sections:[
    {value:'calendars',name:'Calendars',consumer:'Used by business-day schedules, payment dates and power settlement coverage.'},
    {value:'calendar_days',name:'Calendar dates',consumer:'Used by schedules and power calculations for explicit holiday and business-day overrides.'},
    {value:'clock_rules',name:'Market clocks',consumer:'Used by contract dates, calendars, schedules, TOUs and elapsed-hour power integration.'}
  ]},
  {name:'Conventions',sections:[
    {value:'quote_specifications',name:'Price conventions',consumer:'Used to validate price units, multipliers, domains and clearing grids.'},
    {value:'settlement_targets',name:'Settlement rules',consumer:'Used for explicit futures/index settlement targets and swap/power construction.'},
    {value:'delivery_profiles',name:'Delivery patterns',consumer:'Used by declared constituent and delivery mappings in composed curves.'}
  ]}
] as const;
export function dataSections(available:string[]){return referenceGroups.map(group=>({...group,sections:group.sections.filter(section=>available.includes(section.value))})).filter(group=>group.sections.length);}
