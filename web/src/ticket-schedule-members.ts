import type {TicketScheduleEntry} from './ticket-types.js';
import type {TicketSide,TicketState} from './ticket-model.js';

// Authoring cards may describe several native entries. Only the backend names
// those entries; a stale preview cannot restore a card the user has removed.
export function scheduleMembers(state:TicketState,entries?:TicketScheduleEntry[]):(TicketSide&{name?:string})[]{
 if(entries===undefined)return state.sides;
 return entries.flatMap(entry=>{
  const owner=state.sides.find(side=>side.code===entry.owner);
  if(!owner)return [];
  if(!entry.code||!entry.name||!state.description.sides.some(kind=>kind.kind===entry.kind))throw Error('The schedule membership is incomplete.');
  return [{...owner,code:entry.code,kind:entry.kind,name:entry.name}];
 });
}
export function requireScheduleMembers(state:TicketState,entries:TicketScheduleEntry[]|undefined,slots:Record<string,number>|undefined){
 const members=scheduleMembers(state,entries);
 const invalid=()=>Error('The booked Side schedules require their exact native membership slots.');
 if(entries!==undefined&&(members.length!==entries.length||state.sides.some(side=>entries.filter(entry=>entry.owner===side.code).length!==(state.description.sides.find(kind=>kind.kind===side.kind)!.entryCount??1))))throw invalid();
 if(!slots||Object.keys(slots).length!==members.length||members.some(side=>!Number.isInteger(slots[side.code])||slots[side.code]<0)||
  new Set(members.map(side=>side.code)).size!==members.length||new Set(Object.values(slots)).size!==members.length)throw invalid();
 return members;
}
