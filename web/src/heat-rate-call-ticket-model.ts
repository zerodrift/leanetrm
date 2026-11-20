import type {FacilityProfile} from './facility-profile.js';
// The heat-rate call option ticket's state: what it offers from the backend's choices for what is entered so far, and the exact booking
// terms a complete entry becomes. Every listed value is the backend's; nothing is chosen for the person, and every number but a count is
// sent as entered for the booking to judge.
export type Choice={value:string;label:string};
export type TollCurve={code:string;name:string;commodity:string;currency:string;priceUnit:string;quantityUnit:string};
export type TollCatalog={facilityProfiles?:FacilityProfile[];definitionRevisionId:string;referenceCatalogId:string;referenceRevisionId:string;curves:TollCurve[];
 capacityQuantities:{code:string;commodity:string;unit:string}[];blocks:{id:string;version:number;name:string;clock:string}[];clocks:string[];calendars:string[];
 intervalMinutes:number[];memberships:string[];
 paymentOptions:{frequencies:Choice[];rules:Choice[];dayBases:Choice[];shortMonths:Choice[];adjustments:Choice[];implementation:string};
 policies:{exerciseStyles:string[];models:string[];dayCounts:string[];exerciseRules:string[]}};
export const tollFields=['facilityProfileId','book','legalEntity','counterparty','reason','direction','capacity','capacityQuantity','powerCurve','fuelCurve','heatRate','strike','deliveryFrom','deliveryTo',
 'clock','intervalMinutes','membership','block','holidayCalendar','exerciseStyle','model','dayCount','exerciseRule','leadBusinessDays','localCutoff','exerciseCalendar',
 'paymentFrequency','paymentRule','paymentNumber','paymentDayBasis','paymentShortMonth','paymentAdjustment','paymentCalendar'] as const;
export type TollField=typeof tollFields[number];
export type TollEntry=Partial<Record<TollField,string>>;

const blockKey=(block:{id:string;version:number})=>JSON.stringify([block.id,block.version]);
const lagRule='days_after_period_end';

/** What the ticket offers for the entry so far, and the units its heat rate, strike and capacity are in once their curves and quantity are chosen. */
export function tollChoices(catalog:TollCatalog,entry:TollEntry){
 const curve=(code:string|undefined)=>catalog.curves.find(c=>c.code===code),power=curve(entry.powerCurve);
 const curveChoice=(c:TollCurve):Choice=>({value:c.code,label:`${c.name} · ${c.priceUnit}`});
 const capacities=power?catalog.capacityQuantities.filter(q=>q.commodity===power.commodity):[];
 const fuels=power?catalog.curves.filter(c=>c.commodity!==power.commodity&&c.currency===power.currency):[];
 const fuel=fuels.find(c=>c.code===entry.fuelCurve),capacity=capacities.find(q=>q.code===entry.capacityQuantity);
 return {
  powerCurves:catalog.curves.filter(c=>catalog.capacityQuantities.some(q=>q.commodity===c.commodity)).map(curveChoice),
  fuelCurves:fuels.map(curveChoice),
  capacityQuantities:capacities.map(q=>({value:q.code,label:q.unit})),
  blocks:entry.membership&&entry.membership!=='all'&&entry.clock?catalog.blocks.filter(b=>b.clock===entry.clock).map(b=>({value:blockKey(b),label:`${b.name} · version ${b.version}`})):[],
  heatRateUnit:power&&fuel?`${fuel.quantityUnit}/${power.quantityUnit}`:null,strikeUnit:power?.priceUnit??null,capacityUnit:capacity?.unit??null,
 };
}

/** The entry with every choice that is no longer offered cleared, never replaced by another. */
export function keepOfferedEntry(catalog:TollCatalog,entry:TollEntry):TollEntry{
 const next={...entry},offered=(list:Choice[],value:string|undefined)=>!value||list.some(c=>c.value===value);
 const shown=tollChoices(catalog,next);
 if(!offered(shown.fuelCurves,next.fuelCurve))next.fuelCurve='';
 if(!offered(shown.capacityQuantities,next.capacityQuantity))next.capacityQuantity='';
 if(!offered(shown.blocks,next.block))next.block='';
 return next;
}

type Body={bookId:string;legalEntityId:string;legalEntityRevision:number;counterpartyId:string;counterpartyRevision:number;reason:string;terms:Record<string,unknown>};
const whole=(text:string)=>/^\d+$/.test(text)?Number(text):null;

/** The booking a complete entry asks for, or the first thing it still lacks, in the order the ticket lists its fields. */
export function tollCommand(catalog:TollCatalog,entry:TollEntry):{ok:true;body:Body}|{ok:false;reason:string}{
 const e=Object.fromEntries(tollFields.map(f=>[f,(entry[f]??'').trim()])) as Record<TollField,string>;
 const no=(reason:string)=>({ok:false as const,reason});
 const block=e.membership!=='all',lag=e.paymentRule===lagRule;
 const required:[boolean,string][]=[
  [!!e.book,'Choose the book.'],[!!e.legalEntity,'Choose the legal entity.'],[!!e.counterparty,'Choose the counterparty.'],
  [!!e.direction,'Choose buy or sell.'],[!!e.capacity,'Enter the capacity.'],[!!e.powerCurve,'Choose the power curve.'],[!!e.capacityQuantity,'Choose the capacity unit.'],
  [!!e.fuelCurve,'Choose the fuel curve.'],[!!e.heatRate,'Enter the heat rate.'],[!!e.strike,'Enter the strike.'],
  [!!e.deliveryFrom,'Enter the first delivery day.'],[!!e.deliveryTo,'Enter the day delivery ends.'],
  [!!e.clock,'Choose the delivery clock.'],[!!e.intervalMinutes,'Choose the delivery interval.'],[!!e.membership,'Choose the delivery hours.'],
  [!block||!!e.block,'Choose the block product.'],[!!e.holidayCalendar,'Choose the delivery holiday calendar.'],
  [!!e.exerciseStyle,'Choose the exercise style.'],[!!e.exerciseRule,'Choose the exercise rule.'],[!!e.leadBusinessDays,'Enter the exercise lead.'],
  [!!e.localCutoff,'Enter the exercise cutoff.'],[!!e.exerciseCalendar,'Choose the exercise calendar.'],
  [!!e.model,'Choose the valuation model.'],[!!e.dayCount,'Choose the day count.'],
  [!!e.paymentFrequency,'Choose the payment frequency.'],[!!e.paymentRule,'Choose the payment rule.'],[!!e.paymentNumber,'Enter the payment days.'],
  [!lag||!!e.paymentDayBasis,'Choose calendar or business days for the payment.'],[lag||!!e.paymentShortMonth,'Choose what a short month does to the payment day.'],
  [!!e.paymentAdjustment,'Choose the payment date adjustment.'],[!!e.reason,'Enter the reason for the booking.']];
 const lacking=required.find(([given])=>!given);if(lacking)return no(lacking[1]);
 if(!/^\d/.test(e.capacity))return no('Enter the capacity as a positive number; buy or sell gives its direction.');
 const lead=whole(e.leadBusinessDays),number=whole(e.paymentNumber),interval=whole(e.intervalMinutes);
 if(lead===null)return no('Enter the exercise lead as a whole number of business days.');
 if(number===null)return no('Enter the payment days as a whole number.');
 if(interval===null)return no('Choose the delivery interval.');
 const [legalEntityId,legalEntityRevision]=JSON.parse(e.legalEntity) as [string,number],[counterpartyId,counterpartyRevision]=JSON.parse(e.counterparty) as [string,number];
 const [touProduct,touVersion]=block?JSON.parse(e.block) as [string,number]:[null,null];
 return {ok:true,body:{bookId:e.book,legalEntityId,legalEntityRevision,counterpartyId,counterpartyRevision,reason:e.reason,terms:{
  ...(e.facilityProfileId?{facilityProfileId:e.facilityProfileId}:{}),definitionRevisionId:catalog.definitionRevisionId,referenceCatalogId:catalog.referenceCatalogId,referenceRevisionId:catalog.referenceRevisionId,
  quantity:e.direction==='sell'?`-${e.capacity}`:e.capacity,capacityQuantity:e.capacityQuantity,powerCurve:e.powerCurve,fuelCurve:e.fuelCurve,heatRate:e.heatRate,strike:e.strike,
  exerciseStyle:e.exerciseStyle,model:e.model,dayCount:e.dayCount,deliveryFrom:e.deliveryFrom,deliveryTo:e.deliveryTo,clock:e.clock,intervalMinutes:interval,
  hours:{membership:e.membership,touProduct,touVersion,calendar:e.holidayCalendar},
  exerciseRule:{implementation:e.exerciseRule,leadBusinessDays:lead,localCutoff:e.localCutoff,calendar:e.exerciseCalendar},
  paymentRule:{frequency:e.paymentFrequency,rule:e.paymentRule,days:lag?number:null,dayOfMonth:lag?null:number,dayBasis:lag?e.paymentDayBasis:null,
   shortMonth:lag?null:e.paymentShortMonth,adjustment:e.paymentAdjustment,calendar:e.paymentCalendar||null,implementation:catalog.paymentOptions.implementation}}}};
}
