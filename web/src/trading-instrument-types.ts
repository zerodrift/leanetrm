// Closed transport identities shared by position facts, page selections and
// owned-window references. Product valuation remains in the managed backend.
export const tradingInstrumentTypes=['future','financial_swap','heat_rate','ppa','physical_gas','physical_gas_transport','physical_gas_park_loan','physical_gas_park_custody','physical_crude','physical_lng','futures_option','operating_right','rec_forward','heat_rate_call'] as const;
export type TradingInstrumentType=typeof tradingInstrumentTypes[number];
