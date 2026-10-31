import type {FactsContext} from './position-facts.js';
import {validTimeZone} from './preferences.js';

export type PositionWindowSelection={snapshotId:string;context:FactsContext;asOf:string;tradeId:string;bindingId:string;revision:number;
 instrumentType:'future'|'financial_swap'|'heat_rate'|'ppa'|'physical_gas';zone:string;example:'baseline'|'exchange'};
const invalid='This trade window needs its exact position and retained market context. Open it again from the Blotter.';
const fields=['snapshot','context','effective','known','asOf','trade','binding','revision','type','zone','example'];
export function positionWindowNames(value:unknown):{code:string;name:string}[]{
 if(!Array.isArray(value)||value.some(row=>!row||typeof row.code!=='string'||!row.code||typeof row.name!=='string'||!row.name))
  throw Error('The retained trade read did not return its authorized market names.');
 return value;
}
const guid=(value:string)=>/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value)&&!/^0{8}(?:-0{4}){3}-0{12}$/.test(value);
const instant=(value:string)=>/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value)&&Number.isFinite(Date.parse(value));
export function requirePositionWindowSelection(selection:PositionWindowSelection):PositionWindowSelection{
 if(!selection||!selection.context||![selection.snapshotId,selection.context.contextId,selection.tradeId,selection.bindingId].every(v=>typeof v==='string'&&guid(v))||
   !/^\d{4}-\d{2}-\d{2}$/.test(selection.context.effectiveOn)||!instant(selection.context.knownAt)||!instant(selection.asOf)||
   selection.asOf.slice(0,10)!==selection.context.effectiveOn||!Number.isInteger(selection.revision)||selection.revision<1||selection.revision>256||
   !['future','financial_swap','heat_rate','ppa','physical_gas'].includes(selection.instrumentType)||!validTimeZone(selection.zone)||!['baseline','exchange'].includes(selection.example))throw Error(invalid);
 return selection;
}

/** Fragment references are not credentials and are never sent in an HTTP referrer. */
export function positionWindowUrl(source:string,selection:PositionWindowSelection):string{
 requirePositionWindowSelection(selection);const url=new URL('/position.html',source);
 if(!['http:','https:'].includes(url.protocol)||url.username||url.password)throw Error(invalid);
 url.hash=new URLSearchParams({snapshot:selection.snapshotId,context:selection.context.contextId,effective:selection.context.effectiveOn,known:selection.context.knownAt,
  asOf:selection.asOf,trade:selection.tradeId,binding:selection.bindingId,revision:String(selection.revision),type:selection.instrumentType,zone:selection.zone,example:selection.example}).toString();
 return url.href;
}

export function readPositionWindowUrl(source:string):PositionWindowSelection{
 const url=new URL(source),values=new URLSearchParams(url.hash.slice(1));
 if(url.search||values.size!==fields.length||[...values.keys()].some(key=>!fields.includes(key))||fields.some(key=>values.getAll(key).length!==1))throw Error(invalid);
 const value=(key:string)=>values.get(key)!;
 if(!/^[1-9]\d{0,2}$/.test(value('revision')))throw Error(invalid);
 return requirePositionWindowSelection({snapshotId:value('snapshot'),context:{contextId:value('context'),effectiveOn:value('effective'),knownAt:value('known')},
  asOf:value('asOf'),tradeId:value('trade'),bindingId:value('binding'),revision:Number(value('revision')),instrumentType:value('type') as PositionWindowSelection['instrumentType'],
  zone:value('zone'),example:value('example') as PositionWindowSelection['example']});
}

export function positionWindowRequest(selection:PositionWindowSelection){
 requirePositionWindowSelection(selection);
 return {context:{snapshotId:selection.snapshotId,convention:'trading_workspace_v1',knowledgeMode:'retained' as const,contextId:selection.context.contextId},
  tradeId:selection.tradeId,bindingId:selection.bindingId,revision:selection.revision,instrumentType:selection.instrumentType};
}

export function openPositionWindow(source:string,selection:PositionWindowSelection,open:(url:string,target:string,features:string)=>unknown){
 // With noopener, browsers may return null even after opening the popup.
 // Do not misreport that return value as a blocked or failed trade read.
 open(positionWindowUrl(source,selection),'_blank','popup=yes,noopener,noreferrer,width=1180,height=860,resizable=yes,scrollbars=yes');
}
