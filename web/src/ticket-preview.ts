import {exerciseFieldKey} from './ticket-model.js';
import type {TicketStatus} from './deal-ticket.js';
import type {ScheduleView,TicketLink,TicketPreview,TicketSummary} from './ticket-types.js';
import {requireInstrumentSideGroups,type InstrumentSideGroup} from './instrument-sides.js';
import {requireSideGroupMappings,type TicketFieldGroup} from './ticket-field-groups.js';

// What a preview answer makes of the ticket's schedule panel (docs/deal-ticket.md, Live preview): the schedule it draws, or the last good
// one kept, marked stale, with the refusal's reason on the field it names.
// quantityUnit is the unit the shown schedule's preview resolved, and links its quantity links.
export type SchedulePanel={schedule:ScheduleView|null;quantityUnit:string|null;currency?:string;previous:ScheduleView|null;summaries:TicketSummary[];links:TicketLink[];status:TicketStatus;errors:Record<string,string>;sideGroups?:InstrumentSideGroup[];sideGroupsReason?:string|null};

/** Why an attempt was refused or failed, in the words it gives. */
export const reasonOf=(error:unknown)=>error instanceof Error?error.message:String(error);

/** The field a refusal names, keyed as the ticket keys fields ("<side code>.<field id>", or the field id on the trade), or null. */
export function refusedField(error:unknown):string|null{
 const details=(error as {details?:{side?:unknown;field?:unknown;termRow?:unknown}}).details;
 if(typeof details?.field!=='string')return null;
 if(typeof details.termRow==='number'&&Number.isInteger(details.termRow)&&details.termRow>=0)return exerciseFieldKey(details.termRow,details.field);
 return typeof details.side==='string'?`${details.side}.${details.field}`:details.field;
}

export function previewed(panel:SchedulePanel,outcome:{preview:TicketPreview}|{refused:unknown},options:{sideGroupsRequired?:boolean;retained?:boolean;fieldGroups?:readonly TicketFieldGroup[]}={}):SchedulePanel{
 const kept=(text:string,errors:Record<string,string>):SchedulePanel=>{const {sideGroups,sideGroupsReason,...previous}=panel;return{...previous,status:{kind:panel.schedule?'stale':'refused',text},errors};};
 if('refused' in outcome){
  const reason=reasonOf(outcome.refused),field=refusedField(outcome.refused);
  return kept(`Preview refused: ${reason}`,field?{[field]:reason}:{});
 }
 const {schedule,summaries,scheduleRefusal,quantityUnit,currency,links}=outcome.preview;
 const sideGroupsReason=outcome.preview.sideGroupsReason;
 const sideGroups=options.sideGroupsRequired||outcome.preview.sideGroups!==undefined||sideGroupsReason!==undefined?requireInstrumentSideGroups(outcome.preview.sideGroups,options.retained===true,sideGroupsReason):undefined;
 if(sideGroups)requireSideGroupMappings(sideGroups,options.fieldGroups);
 const composition=sideGroups?{sideGroups,...sideGroupsReason!==undefined?{sideGroupsReason}:{}}:{};
 if(typeof quantityUnit!=='string'||!quantityUnit)throw Error('The preview did not resolve the trade’s quantity unit.');
 if(!Array.isArray(links))throw Error('The preview did not return the trade’s quantity links.');
 if(schedule)return {schedule,quantityUnit,...currency===undefined?{}:{currency},previous:panel.schedule,summaries:summaries??[],links,...composition,status:{kind:'current',text:'Up to date · previewed after your last change'},errors:{}};
 if(!scheduleRefusal)throw Error('The preview returned neither a schedule nor why it has none.');
 const status={kind:'refused',text:`Schedule refused: ${scheduleRefusal.message}`} as const;
 if(options.retained&&outcome.preview.groups===null)return {schedule:null,previous:null,quantityUnit,summaries:[],links,...composition,status,errors:{}};
 return {...kept(status.text,{}),...composition};
}
