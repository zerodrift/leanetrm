import type {DeliveryPeriod} from './swap-selection.js';
/** Exact backend plans are presented and transported; dates and weights are never calculated here. */
export type ReferenceKey={revision:{catalogId:string;revisionId:string};code:string};
export type SamplingChoice={resetFrequency:string;averagingMethod:string;label:string;requiresObservationCalendar:boolean;requiresQuantityWeights:boolean};
export type ObservationRow={from:string;to:string;quantitySpecification:ReferenceKey};
export type ObservationSchedule={implementation:'side_observation_schedule_v1';calendar:ReferenceKey|null;quantities:{from:string;to:string;quantity:{value:string;specification:ReferenceKey}}[]|null};
export type ObservationOptions={deliveryPeriods:DeliveryPeriod[];context:unknown;asOf:string;indices:{definitionRevisionId:string;curve:string;index:string}[];samplings:SamplingChoice[];selectedSampling?:SamplingChoice|null;
 observationCalendars:{reference:ReferenceKey;name:string;from:string;to:string}[];observations:ObservationRow[]|null;
 availability:{code:string;message:string}|null;customProjectionUnavailableReason:string|null;qualifiedCustomProjection?:import('./swap-projections.js').CustomProjection|null;
 choices:{definitionRevisionId:string;curve:string;index:string;resetFrequency:string;averagingMethod:string;available:boolean;reasons:{code:string;message:string}[]}[]};
type ObservationEvidence={price:string;exactPrice:unknown;cashContribution:string;fixingState:string;expectedPublication:string|null;fixing:unknown;lookup:unknown;lineage:unknown};
export type ObservationInspection={implementation:string;resetFrequency:string;averagingMethod:string;averaging:string;denominator:string;deliveryFrom:string;deliveryTo:string;mappingRule:string;calendar:ReferenceKey|null;
 observations:(ObservationEvidence&{ordinal:number;from:string;to:string;isGuard:boolean;averagingQuantity:{amount:string;specification:ReferenceKey}|null;weight:string})[];
 applications:(ObservationEvidence&{paymentOrdinal:number;targetFrom:string;targetTo:string;observationOrdinal:number;weight:string;mappingRule:string})[]};

export function observationScheduleInput(choice:Pick<SamplingChoice,'requiresObservationCalendar'|'requiresQuantityWeights'>,
 calendar:ReferenceKey|null,rows:readonly ObservationRow[]|null,values:readonly string[]):ObservationSchedule|undefined{
 if(!choice.requiresObservationCalendar&&!choice.requiresQuantityWeights)return undefined;
 if(choice.requiresObservationCalendar&&!calendar)throw Error('Choose an exact observation calendar.');
 if(choice.requiresQuantityWeights&&(!rows?.length||rows.length!==values.length))throw Error('Resolve the observation intervals before entering quantity weights.');
 if(choice.requiresQuantityWeights&&values.some(value=>!value.trim()))throw Error('Enter a positive quantity for every planned observation.');
 return {implementation:'side_observation_schedule_v1',calendar:choice.requiresObservationCalendar?structuredClone(calendar):null,
  quantities:choice.requiresQuantityWeights?rows!.map((row,index)=>({from:row.from,to:row.to,quantity:{value:values[index],specification:structuredClone(row.quantitySpecification)}})):null};
}
