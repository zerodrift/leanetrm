// The gas storage ticket's state: what it offers from the backend's choices for what is entered so far, the ratchet bands its
// rows describe, and the exact booking terms a complete entry becomes. Every listed value is the backend's; nothing is chosen for
// the person, and every amount is sent as entered for the booking to judge.
import type {RatchetBand} from './gas-storage-charts.js';
export type Choice={value:string;label:string};
export type StorageCurve={code:string;name:string;quote:string;quantity:string;commodity:string;currency:string;priceUnit:string;quantityUnit:string;contracts:{from:string;to:string}[]};
export type StorageCatalog={definitionRevisionId:string;referenceCatalogId:string;referenceRevisionId:string;curves:StorageCurve[];clocks:string[];maximumRatchets:number;maximumDays:number;
 payment:{frequency:string;rule:string;dayBasis:string;adjustment:string;implementation:string;maximumDays:number};
 policies:{ratchetBases:string[];flowPolicies:string[];fuelBases:string[];planSearches:string[]}};
export const storageFields=['book','legalEntity','counterparty','reason','injectionCurve','withdrawalCurve','minimumInventory','maximumInventory','openingInventory',
 'terminalMinimum','terminalMaximum','injectionFuel','withdrawalFuel','ratchetBasis','flowPolicy','fuelBasis','injectionCharge','withdrawalCharge','monthlyCapacityCharge',
 'termFrom','termTo','clock','gasDayStart','paymentDays'] as const;
export type StorageField=typeof storageFields[number];
/** One ratchet band as the ticket asks for it: the inventory it reaches up to and its two daily limits. The last band reaches capacity. */
export type RatchetRow={upTo:string;injection:string;withdrawal:string};
export type StorageEntry={fields:Partial<Record<StorageField,string>>;ratchets:readonly RatchetRow[]};

/** What the ticket offers for the entry so far, and the units its amounts and prices are in once the injection curve is chosen. */
export function storageChoices(catalog:StorageCatalog,entry:StorageEntry){
 const curveChoice=(c:StorageCurve):Choice=>({value:c.code,label:`${c.name} · ${c.priceUnit}`});
 const injection=catalog.curves.find(c=>c.code===entry.fields.injectionCurve);
 return {injectionCurves:catalog.curves.map(curveChoice),withdrawalCurves:injection?catalog.curves.filter(c=>c.quote===injection.quote).map(curveChoice):[],
  unit:injection?.quantityUnit??null,priceUnit:injection?.priceUnit??null};
}
/** The entry with a withdrawal curve that is no longer offered cleared, never replaced by another. */
export function keepOfferedStorage(catalog:StorageCatalog,entry:StorageEntry):StorageEntry{
 const offered=storageChoices(catalog,entry).withdrawalCurves,chosen=entry.fields.withdrawalCurve;
 return !chosen||offered.some(c=>c.value===chosen)?entry:{...entry,fields:{...entry.fields,withdrawalCurve:''}};
}
/** The bands the rows describe: each starts where the one before it ends, the first at the minimum and the last ending at capacity. */
export function ratchetBands(entry:StorageEntry):RatchetBand[]{
 const text=(value:string|undefined)=>(value??'').trim(),rows=entry.ratchets;
 return rows.map((row,i)=>({lower:i===0?text(entry.fields.minimumInventory):text(rows[i-1].upTo),upper:i===rows.length-1?text(entry.fields.maximumInventory):text(row.upTo),
  maximumDailyInjection:text(row.injection),maximumDailyWithdrawal:text(row.withdrawal)}));
}

type Body={bookId:string;legalEntityId:string;legalEntityRevision:number;counterpartyId:string;counterpartyRevision:number;reason:string;terms:Record<string,unknown>};
/** The booking a complete entry asks for, or the first thing it still lacks, in the order the ticket lists its fields. */
export function storageCommand(catalog:StorageCatalog,entry:StorageEntry):{ok:true;body:Body}|{ok:false;reason:string}{
 const e=Object.fromEntries(storageFields.map(f=>[f,(entry.fields[f]??'').trim()])) as Record<StorageField,string>;
 const no=(reason:string)=>({ok:false as const,reason});
 const required:[boolean,string][]=[
  [!!e.injectionCurve,'Choose the injection curve.'],[!!e.withdrawalCurve,'Choose the withdrawal curve.'],
  [!!e.maximumInventory,'Enter the contracted capacity.'],[!!e.minimumInventory,'Enter the minimum inventory.'],
  [!!e.openingInventory,'Enter the opening inventory.'],[!!e.terminalMinimum,'Enter the lowest terminal inventory.'],[!!e.terminalMaximum,'Enter the highest terminal inventory.'],
  [entry.ratchets.length>0,'Add at least one ratchet band.'],[entry.ratchets.length<=catalog.maximumRatchets,`A contract takes at most ${catalog.maximumRatchets} ratchet bands.`],
  [entry.ratchets.every((row,i)=>i===entry.ratchets.length-1||!!row.upTo.trim()),'Enter the inventory each ratchet band reaches up to.'],
  [entry.ratchets.every(row=>!!row.injection.trim()&&!!row.withdrawal.trim()),'Enter each ratchet band\'s daily injection and withdrawal limits.'],
  [!!e.ratchetBasis,'Choose how ratchets apply.'],[!!e.flowPolicy,'Choose the daily flow policy.'],
  [!!e.injectionFuel,'Enter the injection fuel fraction; zero is stated, not assumed.'],[!!e.withdrawalFuel,'Enter the withdrawal fuel fraction; zero is stated, not assumed.'],
  [!!e.fuelBasis,'Choose how fuel is retained.'],
  [!!e.termFrom,'Enter the first gas day.'],[!!e.termTo,'Enter the day the term ends.'],[!!e.clock,'Choose the gas-day clock.'],[!!e.gasDayStart,'Enter when the gas day starts.'],
  [!!e.injectionCharge,'Enter the injection charge; zero is stated, not assumed.'],[!!e.withdrawalCharge,'Enter the withdrawal charge; zero is stated, not assumed.'],
  [!!e.monthlyCapacityCharge,'Enter the monthly capacity charge; zero is stated, not assumed.'],[!!e.paymentDays,'Enter the payment days.'],
  [!!e.book,'Choose the book.'],[!!e.legalEntity,'Choose the legal entity.'],[!!e.counterparty,'Choose the counterparty.'],[!!e.reason,'Enter the reason for the booking.']];
 const lacking=required.find(([given])=>!given);if(lacking)return no(lacking[1]);
 const curve=catalog.curves.find(c=>c.code===e.injectionCurve);if(!curve)return no('Choose the injection curve.');
 const start=/^([01]\d|2[0-3]):([0-5]\d)$/.exec(e.gasDayStart);if(!start)return no('Enter when the gas day starts as hours and minutes.');
 if(!/^\d+$/.test(e.paymentDays))return no('Enter the payment days as a whole number.');
 const [legalEntityId,legalEntityRevision]=JSON.parse(e.legalEntity) as [string,number],[counterpartyId,counterpartyRevision]=JSON.parse(e.counterparty) as [string,number];
 const pay=catalog.payment;
 return {ok:true,body:{bookId:e.book,legalEntityId,legalEntityRevision,counterpartyId,counterpartyRevision,reason:e.reason,terms:{
  definitionRevisionId:catalog.definitionRevisionId,referenceCatalogId:catalog.referenceCatalogId,referenceRevisionId:catalog.referenceRevisionId,
  quantity:curve.quantity,injectionCurve:e.injectionCurve,withdrawalCurve:e.withdrawalCurve,minimumInventory:e.minimumInventory,maximumInventory:e.maximumInventory,
  ratchets:ratchetBands(entry),injectionFuel:e.injectionFuel,withdrawalFuel:e.withdrawalFuel,ratchetBasis:e.ratchetBasis,flowPolicy:e.flowPolicy,fuelBasis:e.fuelBasis,
  openingInventory:e.openingInventory,terminalMinimum:e.terminalMinimum,terminalMaximum:e.terminalMaximum,
  injectionCharge:e.injectionCharge,withdrawalCharge:e.withdrawalCharge,monthlyCapacityCharge:e.monthlyCapacityCharge,
  termFrom:e.termFrom,termTo:e.termTo,clock:e.clock,dayStartMinute:Number(start[1])*60+Number(start[2]),
  paymentRule:{frequency:pay.frequency,rule:pay.rule,days:Number(e.paymentDays),dayOfMonth:null,dayBasis:pay.dayBasis,shortMonth:null,adjustment:pay.adjustment,calendar:null,implementation:pay.implementation}}}};
}
