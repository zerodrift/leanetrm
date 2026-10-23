import {timeLabels} from './market-view.js';
// EffectiveOn is a business date; only the retained instants change display zone.
export function riskContextLabel(job:{asOf?:string;context?:{effectiveOn:string;knownAt?:string}},zone:string){
 const values=[job.asOf,job.context?.knownAt].filter((v):v is string=>!!v);
 const labels=timeLabels(values,zone);let index=0;
 const market=job.asOf?labels[index++]:'unavailable',known=job.context?.knownAt?labels[index]:'unavailable';
 return `Captured market time: ${market} · effective date: ${job.context?.effectiveOn??'unavailable'} · known as of: ${known} · ${zone}. These retained coordinates govern this job.`;
}
