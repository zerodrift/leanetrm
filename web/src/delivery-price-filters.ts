export type DeliveryPriceSource={id:string;name:string;commodityKey:string;commodityName:string;provider:string;market:string;marketName:string;locationKey:string;locationName:string;minutes:number;marketClock:string;priceUnit:string;availableDates:string[];availableThrough:Record<string,string>};
export type DeliveryPriceSelection={commodityKey?:string;provider?:string;market?:string;locationKey?:string};
type Choice={value:string;name:string};
function choices(sources:DeliveryPriceSource[],key:(s:DeliveryPriceSource)=>string,label:(s:DeliveryPriceSource)=>string):Choice[]{
 const values=new Map<string,string>();
 for(const source of sources){const value=key(source),name=label(source);if(!value||!name||(values.has(value)&&values.get(value)!==name))throw Error('Delivery-price source details are missing or inconsistent.');values.set(value,name);}
 return [...values].map(([value,name])=>({value,name})).sort((a,b)=>a.name.localeCompare(b.name)||a.value.localeCompare(b.value));
}
export function deliveryPriceChoices(sources:DeliveryPriceSource[],selection:DeliveryPriceSelection){
 sources=sources.filter(s=>s.availableDates.length>0);
 const commodity=sources.filter(s=>s.commodityKey===selection.commodityKey);
 const provider=commodity.filter(s=>s.provider===selection.provider);
 const market=provider.filter(s=>s.market===selection.market);
 const location=market.filter(s=>s.locationKey===selection.locationKey);
 return {
  commodities:choices(sources,s=>s.commodityKey,s=>s.commodityName),
  providers:choices(commodity,s=>s.provider,s=>s.provider),
  markets:choices(provider,s=>s.market,s=>s.marketName),
  locations:choices(market,s=>s.locationKey,s=>s.locationName),
  sources:choices(location,s=>s.id,s=>`${s.name} · ${s.minutes} min`),
 };
}

export function latestDeliverySource(sources:DeliveryPriceSource[]):DeliveryPriceSource|undefined{
 return sources.filter(s=>s.availableDates.length>0).reduce<DeliveryPriceSource|undefined>((latest,source)=>{
  const newest=(s:DeliveryPriceSource)=>[...s.availableDates].sort().at(-1)!;
  return !latest||newest(source)>newest(latest)?source:latest;
 },undefined);
}
