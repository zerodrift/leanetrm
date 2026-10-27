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

export type DeliveryOpening={sourceId:string;fromDate:string;throughDate:string;policy:string};
/**
 * The range the page opens on: the catalog's opening, chosen by the backend under newest_available_delivery_day_v1, or nothing when
 * no source has a day. An opening that the catalog's own sources cannot show is refused, never replaced by another choice.
 */
export function deliveryOpening(sources:DeliveryPriceSource[],opening:DeliveryOpening|null){
 if(opening===null)return null;
 const source=sources.find(s=>s.id===opening.sourceId);
 if(opening.policy!=='newest_available_delivery_day_v1'||!source||![opening.fromDate,opening.throughDate].every(date=>source.availableDates.includes(date))||
  opening.throughDate<opening.fromDate||opening.throughDate>(source.availableThrough[opening.fromDate]??''))
  throw Error('The delivery-price catalog names an opening it cannot show. Refresh the page.');
 return {source,fromDate:opening.fromDate,throughDate:opening.throughDate};
}
