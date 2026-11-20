export type FacilityBand={lower:string;upper:string;maximumDailyInjection:string;maximumDailyWithdrawal:string};
export type FacilityProfile={id:string;code:string;revision:number;name:string;kind:'thermal'|'storage';facilityId:string;version:number;clock:string;dayStartMinute:number;
 quantity:string;minimum:string;maximum:string;inputLocation:string;outputLocation:string;inputCurve:string;outputCurve:string;inputAdjustment:string;outputAdjustment:string;ratchets:FacilityBand[]|null;
 hourlyRatchets:{lower:string;upper:string;injectionPerHour:string;withdrawalPerHour:string}[]|null};
export function pricingLabel(location:string,curve:string,adjustment:string){
 return location+' · '+curve+(adjustment==='0'?'':(adjustment.startsWith('-')?' − ':' + ')+adjustment.replace(/^-/,''));
}
