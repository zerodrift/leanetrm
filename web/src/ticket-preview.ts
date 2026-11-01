import type {TicketStatus} from './deal-ticket.js';
import type {ScheduleView,TicketPreview,TicketSummary} from './ticket-types.js';

// What a preview answer makes of the ticket's schedule panel (docs/deal-ticket.md, Live preview): the schedule it draws, or the last good
// one kept, marked stale, with the refusal's reason on the field it names.
export type SchedulePanel={schedule:ScheduleView|null;previous:ScheduleView|null;summaries:TicketSummary[];status:TicketStatus;errors:Record<string,string>};

/** The field a refusal names, keyed as the ticket keys fields ("<side code>.<field id>", or the field id on the trade), or null. */
export function refusedField(error:unknown):string|null{
 const details=(error as {details?:{side?:unknown;field?:unknown}}).details;
 if(typeof details?.field!=='string')return null;
 return typeof details.side==='string'?`${details.side}.${details.field}`:details.field;
}

export function previewed(panel:SchedulePanel,outcome:{preview:TicketPreview}|{refused:unknown}):SchedulePanel{
 const kept=(text:string,errors:Record<string,string>):SchedulePanel=>({...panel,status:{kind:panel.schedule?'stale':'refused',text},errors});
 if('refused' in outcome){
  const reason=outcome.refused instanceof Error?outcome.refused.message:String(outcome.refused),field=refusedField(outcome.refused);
  return kept(`Preview refused: ${reason}`,field?{[field]:reason}:{});
 }
 const {schedule,summaries,scheduleRefusal}=outcome.preview;
 if(schedule)return {schedule,previous:panel.schedule,summaries:summaries??[],status:{kind:'current',text:'Up to date · previewed after your last change'},errors:{}};
 if(!scheduleRefusal)throw Error('The preview returned neither a schedule nor why it has none.');
 return kept(`Schedule refused: ${scheduleRefusal.message}`,{});
}
