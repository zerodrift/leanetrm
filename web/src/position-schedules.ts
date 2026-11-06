import type {Holding} from './desk-model.js';
import type {DetailRead} from './position-panel.js';
import {displayInstant} from './market-view.js';
import {mountSideScheduleInspection,type SchedulePosition} from './side-schedule-inspection.js';

type Api=<T>(path:string,body:unknown,signal?:AbortSignal)=>Promise<T>;
// A product may supply an integrated editor. All other position views receive
// the same lazy, authorized Side inspector, including newly registered products.
export function withSideSchedules(read:DetailRead,api:Api,context:Record<string,unknown>,h:Pick<Holding,'tradeId'|'bindingId'|'revision'|'instrumentType'>,onDenied:()=>void):DetailRead{
 const position:SchedulePosition={context:{...context,convention:'trading_workspace_v1'},tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision),instrumentType:h.instrumentType};
 return {...read,view:zone=>{
  const view=read.view(zone);
  return {...view,schedules:view.schedules??(host=>mountSideScheduleInspection(host,{api,position,instant:at=>displayInstant(at,zone),onDenied}))};
 }};
}
export function genericPositionView(h:Pick<Holding,'tradeReference'|'instrumentReference'|'instrumentType'>):DetailRead{
 return {amend:{allowed:false,reason:'This position has no registered amendment editor.'},view:()=>({
  terms:[{title:'Instrument',rows:[['Trade',h.tradeReference],['Instrument',h.instrumentReference],['Type',h.instrumentType]]}],history:[]
 })};
}
