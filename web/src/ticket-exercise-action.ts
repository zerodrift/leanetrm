import {actorTransport} from './pending-command.js';
import {carryReadReferences} from './read-references.js';
import {reasonOf} from './ticket-preview.js';
import type {ExerciseAction,TicketChoice} from './ticket-types.js';

type WorkingContext=Record<string,unknown>;
type ExerciseCommand={context:WorkingContext;tradeId:string;instrumentId:string;expectedRevision:number;operationId:string;
 elections:{right:number;fromDate:string}[]};
type ExerciseReceipt={operationId:string;tradeId:string;instrumentId:string;elections:{right:number;fromDate:string;revisionId:string}[]};
type Api=<T>(path:string,body:unknown,signal?:AbortSignal)=>Promise<T>;

// Keep the command or its cleared state across same-tab remounts when storage fails.
const transports=new Map<string,ReturnType<typeof actorTransport<ExerciseCommand>>>();
function exerciseTransport(identity:string){
 let transport=transports.get(identity);
 if(!transport)transports.set(identity,transport=actorTransport<ExerciseCommand>(()=>`deal-ticket-exercise:${identity}`,()=>sessionStorage));
 return transport;
}

function element<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string){
 const node=document.createElement(tag);if(text!==undefined)node.textContent=text;return node;
}
function button(label:string,className:string,run:()=>void){
 const control=element('button',label);control.type='button';control.className=className;control.onclick=run;return control;
}
function matches(command:ExerciseCommand,receipt:ExerciseReceipt){
 return receipt.operationId===command.operationId&&receipt.tradeId===command.tradeId&&receipt.instrumentId===command.instrumentId&&
  receipt.elections?.length===command.elections.length&&command.elections.every((election,index)=>{
   const recorded=receipt.elections[index];
   return recorded?.right===election.right&&recorded.fromDate===election.fromDate&&!!recorded.revisionId;
  });
}
const definite=(error:unknown)=>{
 const {status,code}=error as {status?:number;code?:string};
 return status===403||status===422||code==='historical_read_only'||code==='request_token';
};

export function setupExerciseAction(o:{api:Api;context:()=>WorkingContext;current:()=>boolean;signal:AbortSignal;owner:string;tradeId:string;bindingId:string;revision:number;writable:boolean;reread:()=>Promise<void>}){
 const node=element('section');node.className='ticket-exercise-controls';
 const identity=JSON.stringify([o.owner,o.tradeId,o.bindingId,o.revision,o.context()]);
 const transport=o.writable?exerciseTransport(identity):undefined;
 let action:ExerciseAction|undefined,working:WorkingContext|undefined;
 let prepared=transport?.read()??undefined;
 let form:HTMLFormElement|undefined,open=false,sending=false,stale=false,said='';
 const current=()=>o.current()&&action?.allowed===true&&o.writable&&JSON.stringify(o.context())===JSON.stringify(working);
 const keep=(command:ExerciseCommand|undefined)=>{prepared=command;transport?.write(command);};
 const path=(selected:string,expected:string)=>{
  if(selected!==`/api/gas/exercise/${expected}`)throw Error('Reread the exact exercise operation for this trade.');
  return selected.slice('/api/'.length);
 };
 function show(next:ExerciseAction|undefined,context:WorkingContext){
  action=next;working=context;form=undefined;open=false;
  draw();
 }
 async function reread(){
  sending=true;draw();
  try{await o.reread();stale=false;}
  catch(error){said=`The trade could not be refreshed. ${reasonOf(error)}`;stale=true;}
  finally{sending=false;if(o.current())draw();}
 }
 async function record(command:ExerciseCommand,uncertain:boolean){
  if(!current()||!action)return;
  sending=true;said='';draw();
  let saved:ExerciseReceipt;
  try{saved=await o.api<ExerciseReceipt>(path(action.recordPath,'record'),command);}
  catch(error){
   if(!o.current())return;
   sending=false;
   if((error as {code?:string}).code==='exercise_election_conflict'){
    keep(undefined);stale=true;form=undefined;open=false;
    said='The exercise election changed. Refresh the trade to choose from the current instructions.';
   }else{
    if(!uncertain&&definite(error))keep(undefined);
    said=prepared?`The exercise record is unconfirmed. ${reasonOf(error)}`:`Exercise refused. ${reasonOf(error)}`;
   }
   draw();return;
  }
  if(!o.current())return;
  sending=false;
  if(!matches(command,saved)){said='The record response could not be reconciled. Retry the original exercise record.';draw();return;}
  keep(undefined);form=undefined;open=false;said='Exercise election recorded.';
  await reread();
 }
 async function prepare(values:FormData){
  const selection=action,context=working;
  if(!current()||!selection?.readContextId||!context||sending||prepared||stale)return;
  const operationId=crypto.randomUUID();
  const request=carryReadReferences({context,readContextId:selection.readContextId,tradeId:selection.tradeId,instrumentId:selection.instrumentId,
   expectedRevision:selection.expectedRevision,operationId,right:Number(values.get('right')),
   firstPeriod:values.get('firstPeriod'),lastPeriod:values.get('lastPeriod'),outcome:values.get('outcome'),
   evidence:values.get('evidence'),effectiveAt:values.get('effectiveAt')},selection);
  sending=true;said='';draw();
  try{
   const {command}=await o.api<{command:ExerciseCommand}>(path(selection.preparePath,'prepare'),request,o.signal);
   if(!current()||selection!==action)return;
   if(command?.operationId!==operationId||command.tradeId!==selection.tradeId||command.instrumentId!==selection.instrumentId||
    command.expectedRevision!==selection.expectedRevision||!Array.isArray(command.elections)||!command.elections.length)
    throw Error('The prepared command does not match the selected trade and request.');
   keep(command);
  }catch(error){if(o.current())said=`Exercise preparation refused. ${reasonOf(error)}`;}
  finally{sending=false;if(o.current())draw();}
  if(prepared)await record(prepared,false);
 }
 function formFor(selected:ExerciseAction){
  const editor=element('form'),fields=element('div');editor.className='ticket-record ticket-exercise-action';fields.className='ticket-record-fields';
  editor.append(fields);
  function field<T extends HTMLInputElement|HTMLSelectElement>(name:string,label:string,control:T){
   const row=element('label');row.className='ticket-field';
   const title=element('span',label);title.className='ticket-label';
   control.name=name;control.className='ticket-control';control.required=true;row.append(title,control);fields.append(row);return control;
  }
  function options(control:HTMLSelectElement,choices:TicketChoice[]){
   control.replaceChildren(new Option('Choose…',''),...choices.map(({label,value})=>new Option(label,value)));
  }
  const right=field('right','Exercise right',element('select'));
  options(right,[...new Set(selected.periods.map(p=>p.right))].map(value=>({value:String(value),label:`Exercise right ${value+1}`})));
  const first=field('firstPeriod','First group start',element('select')),last=field('lastPeriod','Last group start',element('select'));
  function periods(){
   const choices=selected.periods.filter(p=>String(p.right)===right.value).map(p=>{
    const outcome=p.previousOutcome===null?'withdrawal':selected.outcomes.find(o=>o.value===p.previousOutcome)?.label??p.previousOutcome;
    const head=p.previousId?` · known ${outcome} effective ${p.previousEffectiveAt}`:'';
    return {value:p.from,label:`${p.from} · deadline ${p.exerciseAt}${head}`};
   });
   for(const control of [first,last]){options(control,choices);control.disabled=!right.value;}
  }
  right.onchange=periods;periods();
  options(field('outcome','Outcome',element('select')),selected.outcomes);
  const effective=field('effectiveAt','Effective instant · UTC',element('input'));effective.placeholder='YYYY-MM-DDTHH:mm:ssZ';
  field('evidence','Evidence',element('input'));
  const submit=element('button',selected.label);submit.type='submit';submit.className='ticket-submit-exercise';
  editor.append(submit,button('Cancel','secondary',()=>{form=undefined;open=false;said='';draw();}));
  editor.onsubmit=event=>{event.preventDefault();void prepare(new FormData(editor));};
  return editor;
 }
 function draw(){
  node.replaceChildren();
  const selected=action;if(!selected)return;
  const trigger=button(selected.label,'ticket-action ticket-record-exercise',()=>{open=true;said='';draw();form?.querySelector('select')?.focus();});
  trigger.disabled=!current()||open||sending||!!prepared||stale;
  node.append(trigger);
  const note=said||(!current()?selected.reason??'Exercise recording is read only in this view.':'');
  if(note){const text=element('p',note);text.className='side-panel-status';text.setAttribute('role','status');node.append(text);}
  if(!current())return;
  if(stale){
   const refresh=button('Refresh exercise elections','secondary ticket-refresh-exercise',()=>void reread());
   refresh.disabled=sending;node.append(refresh);
  }else if(prepared){
   const command=prepared,retry=button('Retry the original exercise record','secondary ticket-retry-exercise',()=>void record(command,true));
   retry.disabled=sending;node.append(retry);
  }else if(open){
   form??=formFor(selected);form.inert=sending;node.append(form);
  }
 }
 // Keep the conflict and its refresh control mounted while the Blotter notices the competing publication.
 return {select:show,mount:(host:HTMLElement)=>{if(action)host.append(node);},unsaved:()=>open||!!prepared||stale,pending:()=>sending||!!prepared};
}
