import type {SwingContract,SwingReport} from './ticket-swing.js';
import {termFieldKey} from './ticket-model.js';
import type {TicketStatus} from './deal-ticket.js';
import type {ExerciseGroup,ImbalanceReportRow,TicketScheduleEntry,ScheduleView,TicketLink,TicketPreview,TicketSummary} from './ticket-types.js';
import {requireInstrumentSideGroups,type InstrumentSideGroup} from './instrument-sides.js';
import {requireSideGroupMappings,type TicketFieldGroup} from './ticket-field-groups.js';

// What a preview answer makes of the ticket's schedule panel (docs/deal-ticket.md, Live preview): the schedule it draws, or the last good
// one kept, marked stale, with the refusal's reason on the field it names.
// quantityUnit is the unit the shown schedule's preview resolved, and links its quantity links.
export type SchedulePanel={swingContract?:SwingContract;swingRights?:SwingReport[];knownAt?:string;scheduleEntries?:TicketScheduleEntry[];imbalanceRows?:ImbalanceReportRow[];unassessedDays?:number;groups?:ExerciseGroup[];schedule:ScheduleView|null;quantityUnit:string|null;currency?:string;previous:ScheduleView|null;summaries:TicketSummary[];links:TicketLink[];status:TicketStatus;errors:Record<string,string>;sideGroups?:InstrumentSideGroup[];sideGroupsReason?:string|null};

/** Why an attempt was refused or failed, in the words it gives. */
export const reasonOf=(error:unknown)=>error instanceof Error?error.message:String(error);

/** The field a refusal names, keyed as the ticket keys fields ("<side code>.<field id>", or the field id on the trade), or null. */
export function refusedField(error:unknown):string|null{
 const details=(error as {details?:{side?:unknown;field?:unknown;termRow?:unknown;termSection?:unknown}}).details;
 if(typeof details?.field!=='string')return null;
 if(typeof details.termRow==='number'&&Number.isInteger(details.termRow)&&details.termRow>=0)return termFieldKey(typeof details.termSection==='string'?details.termSection:'exerciseRights',details.termRow,details.field);
 return typeof details.side==='string'?`${details.side}.${details.field}`:details.field;
}

export function previewed(panel:SchedulePanel,outcome:{preview:TicketPreview}|{refused:unknown},options:{sideGroupsRequired?:boolean;retained?:boolean;fieldGroups?:readonly TicketFieldGroup[]}={}):SchedulePanel{
 const kept=(text:string,errors:Record<string,string>):SchedulePanel=>{const {sideGroups,sideGroupsReason,swingRights,swingContract,knownAt,...previous}=panel;return{...previous,status:{kind:panel.schedule?'stale':'refused',text},errors};};
 if('refused' in outcome){
  const reason=reasonOf(outcome.refused),field=refusedField(outcome.refused);
  const previous=kept(`Preview refused: ${reason}`,field?{[field]:reason}:{});
  return panel.groups||panel.schedule?.groups?{...previous,groups:[]}:previous;
 }
 const {schedule,summaries,scheduleRefusal,quantityUnit,currency,links}=outcome.preview;
 const sideGroupsReason=outcome.preview.sideGroupsReason;
 const sideGroups=options.sideGroupsRequired||outcome.preview.sideGroups!==undefined||sideGroupsReason!==undefined?requireInstrumentSideGroups(outcome.preview.sideGroups,options.retained===true,sideGroupsReason):undefined;
 if(sideGroups)requireSideGroupMappings(sideGroups,options.fieldGroups);
 const composition=sideGroups?{sideGroups,...sideGroupsReason!==undefined?{sideGroupsReason}:{}}:{};
 if(typeof quantityUnit!=='string'||!quantityUnit)throw Error('The preview did not resolve the trade’s quantity unit.');
 if(!Array.isArray(links))throw Error('The preview did not return the trade’s quantity links.');
 const reported={...outcome.preview.swingContract?{swingContract:outcome.preview.swingContract,knownAt:outcome.preview.context?.knownAt}:{},...outcome.preview.swingRights?{swingRights:outcome.preview.swingRights,knownAt:outcome.preview.context?.knownAt}:{},...outcome.preview.scheduleEntries?{scheduleEntries:outcome.preview.scheduleEntries}:{},...outcome.preview.imbalanceRows?{imbalanceRows:outcome.preview.imbalanceRows}:{},...outcome.preview.unassessedDays===undefined?{}:{unassessedDays:outcome.preview.unassessedDays}};
 const pricedGroups=Array.isArray(outcome.preview.groups)?{groups:outcome.preview.groups}:{};
 if(schedule)return {schedule,...reported,...pricedGroups,quantityUnit,...currency===undefined?{}:{currency},previous:panel.schedule,summaries:summaries??[],links,...composition,status:{kind:'current',text:'Up to date · previewed after your last change'},errors:{}};
 if(!scheduleRefusal)throw Error('The preview returned neither a schedule nor why it has none.');
 const status={kind:'refused',text:`Schedule refused: ${scheduleRefusal.message}${outcome.preview.swingContract||outcome.preview.swingRights!==undefined?' ('+scheduleRefusal.code+')':''}`} as const;
 if(outcome.preview.swingContract||outcome.preview.swingRights!==undefined)return {schedule:null,previous:null,...reported,quantityUnit,currency,summaries:summaries??[],links,...composition,status,errors:{}};
 if(!options.retained && panel.schedule && outcome.preview.groups!==undefined && !outcome.preview.groups?.length)
  return {...kept(status.text,{}),groups:[],...composition};
 if(pricedGroups.groups)return {schedule:null,previous:null,...reported,...pricedGroups,quantityUnit,...currency===undefined?{}:{currency},summaries:summaries??[],links,...composition,status,errors:{}};
 if(outcome.preview.groups===null)return {schedule:null,previous:null,...reported,quantityUnit,summaries:[],links,...composition,status,errors:{}};
 return {...kept(status.text,{}),...composition};
}
