import {renderRegularTable} from './regular-table.js';
import {displayInstant,friendly,timeLabels} from './market-view.js';
import {zonedInput} from './user-time.js';
import {PendingCommand,type RequestCommand} from './pending-command.js';
import {csvSegments} from './interval-series-csv.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Series={id:string;code:string;name:string;kind:string;specification:string;clock:string;intervalMinutes:number;head:number};
type Catalog={knownAt:string;series:Series[];choices:{clocks:string[];resolutions:number[];specifications:Record<string,string[]>};canDefine:boolean;canRevise:boolean};
type Receipt={requestId:string;seriesId:string;revision:number;intervals:number};
type Reading={series:{id:string};knownAt:string;head:number;rows:{start:string;end:string;value:string;revision:number}[]};
type ChangeLog={series:{id:string};revisions:{revision:number;actor:string;reason:string;knownFrom:string;intervals:number;intervalsInWindow:number}[];
  rows:{start:string;end:string;revision:number;previousRevision:number|null;previousValue:string|null;value:string|null;actor:string;reason:string;knownFrom:string}[]};
type Shown={scope:string;current:boolean;reading:Reading;log:ChangeLog};
type Staged={start:string;label:string;withdraw:boolean;value:string};
type Holder={path:string;pending:PendingCommand;suffix:string;panel:string;retry:string;discard:string;done:(receipt:Receipt,command:RequestCommand)=>Promise<void>};

// Every refusal the series routes make before committing, a malformed number included; only an unknown outcome leaves a request outstanding.
const rejected=(code?:string)=>!!code&&code!=='interval_series_outcome_unknown'&&(code.startsWith('interval_series_')||['access_denied','request_format','request_token','request_size','numeric_format','numeric_range'].includes(code));

export function setupIntervalSeries(o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;identity:()=>string}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const field=(id:string)=>get<HTMLInputElement>(id),choice=(id:string)=>get<HTMLSelectElement>(id);
 const from=zonedInput(field('interval-series-from'),o.zone,o.api),to=zonedInput(field('interval-series-to'),o.zone,o.api),known=zonedInput(field('interval-series-known'),o.zone,o.api);
 // A definition and a revision each keep their own outstanding request.
 const definition:Holder={path:'interval-series/define',pending:new PendingCommand(rejected),suffix:'.define',panel:'interval-series-define-pending',retry:'interval-series-define-retry',discard:'interval-series-define-discard',done:defined};
 const revision:Holder={path:'interval-series/revise',pending:new PendingCommand(rejected),suffix:'',panel:'interval-series-pending',retry:'interval-series-retry',discard:'interval-series-discard',done:revised};
 const holders=[definition,revision];
 let catalog:Catalog|undefined,generation=0,latest:{seriesId:string;head:number}|undefined,shown:Shown|undefined,staged:Staged|undefined;
 const series=()=>catalog?.series.find(s=>s.id===choice('interval-series-select').value);
 const requireSeries=()=>{const selected=series();if(!selected)throw Error('Choose a series.');return selected;};
 // The head of the latest read at the catalog's own Known at; an earlier Known at never supplies the expected revision.
 const expectedRevision=(s:Series)=>latest?.seriesId===s.id?latest.head:s.head;
 const windowComplete=()=>!!(field('interval-series-from').value&&field('interval-series-to').value&&field('interval-series-known').value);

 const storageKey=(h:Holder)=>`openetrm.interval-series.request.${o.identity()}${h.suffix}`;
 function store(h:Holder){try{if(h.pending.command)sessionStorage.setItem(storageKey(h),JSON.stringify(h.pending.command));else sessionStorage.removeItem(storageKey(h));}catch{/* The exact request remains in memory for this visit. */}}
 function restore(h:Holder){
  try{
   const saved=JSON.parse(sessionStorage.getItem(storageKey(h))??'null');
   if(saved&&saved.path===h.path&&typeof saved.operationId==='string'&&saved.body&&typeof saved.body==='object')h.pending.command={operationId:saved.operationId,path:saved.path,body:saved.body,uncertain:true};
  }catch{/* An unreadable copy is ignored; a request still held in memory stays outstanding. */}
 }

 function choose(id:string,placeholder:string,options:[string,string][]){
  const control=choice(id),value=control.value;
  control.replaceChildren(new Option(placeholder,''),...options.map(([code,label])=>new Option(label,code)));
  control.value=options.some(([code])=>code===value)?value:'';
 }
 function specifications(){choose('interval-series-specification','Choose a specification…',(catalog?.choices.specifications[choice('interval-series-kind').value]??[]).map(code=>[code,friendly(code)]));}
 function describe(){
  const selected=series();
  get('interval-series-summary').textContent=selected?`${friendly(selected.kind)} · ${friendly(selected.specification)} · ${friendly(selected.clock)} · ${selected.intervalMinutes}-minute intervals · latest revision ${expectedRevision(selected)}`:'';
 }
 function fillCatalog(selectedId:string|undefined){
  if(!catalog)return;
  choose('interval-series-select',catalog.series.length?'Choose a series…':'No series defined yet',catalog.series.map(s=>[s.id,`${s.name} · ${s.code}`]));
  if(selectedId&&catalog.series.some(s=>s.id===selectedId))choice('interval-series-select').value=selectedId;
  choose('interval-series-kind','Choose a kind…',Object.keys(catalog.choices.specifications).map(kind=>[kind,friendly(kind)]));
  choose('interval-series-clock','Choose a clock…',catalog.choices.clocks.map(code=>[code,friendly(code)]));
  choose('interval-series-minutes','Choose a resolution…',catalog.choices.resolutions.map(minutes=>[String(minutes),`${minutes} minutes`]));
  specifications();
  get('interval-series-define').hidden=!catalog.canDefine;get('interval-series-revise-card').hidden=!catalog.canRevise;
  describe();
 }
 // Reloading pins a new Known at, which sees every revision committed so far, this user's own included.
 async function fetchCatalog(selectedId=series()?.id){
  const started=++generation;
  const next=await o.api<Catalog>('interval-series');
  if(started!==generation)return false;
  catalog=next;latest=undefined;clearResults();fillCatalog(selectedId);known.set(next.knownAt);sync();
  return true;
 }

 // Forget the catalog and everything read through it; typed entries and outstanding requests stay with their owners.
 function unload(){
  generation++;catalog=undefined;latest=undefined;clearResults();
  for(const id of ['interval-series-select','interval-series-kind','interval-series-specification','interval-series-clock','interval-series-minutes'])choice(id).replaceChildren();
  get('interval-series-summary').textContent='';
  get('interval-series-define').hidden=true;get('interval-series-revise-card').hidden=true;
  sync();
 }
 function clearResults(){
  shown=undefined;staged=undefined;get('interval-series-change-form').hidden=true;
  for(const id of ['interval-series-values','interval-series-revisions','interval-series-changes'])get(id).replaceChildren();
  get('interval-series-values-note').textContent='';
 }
 function rowActions(start:string,value:string,label:string){
  const cell=document.createElement('div');
  for(const [name,withdraw] of [['Correct',false],['Withdraw',true]] as const){
   const button=document.createElement('button');button.type='button';button.textContent=name;button.dataset.intervalAction=name.toLowerCase();
   button.setAttribute('aria-label',`${name} the interval starting ${label}`);
   button.onclick=()=>{if(!o.busy()&&!revision.pending.command)stage({start,label,withdraw,value});};
   cell.append(button);
  }
  return cell;
 }
 function render(){
  describe();
  if(!shown)return;
  const {reading,log,current,scope}=shown,zone=o.zone(),editable=!!catalog?.canRevise&&current;
  const count=reading.rows.length,labels=timeLabels([...reading.rows.map(r=>r.start),...reading.rows.map(r=>r.end)],zone);
  // Knowledge times show seconds, with an offset where a repeated clock makes two of them alike. A revision has one Known from, shared by its change rows.
  const known=timeLabels([reading.knownAt,...log.revisions.map(r=>r.knownFrom)],zone,true),knownFrom=new Map(log.revisions.map((r,i)=>[r.revision,known[i+1]]));
  get('interval-series-values-note').textContent=`${count} ${count===1?'interval has':'intervals have'} a value · latest revision ${reading.head} known at ${known[0]}${catalog?.canRevise&&!current?' · Reload to correct current values.':''}`;
  renderRegularTable(get('interval-series-values'),'interval-series-values',scope,['Start','End','Value','Revision',...(editable?['']:[])],
   reading.rows.map((r,i)=>[labels[i],labels[count+i],r.value,String(r.revision),...(editable?[rowActions(r.start,r.value,labels[i])]:[])]),[2,3],o.busy);
  renderRegularTable(get('interval-series-revisions'),'interval-series-revisions',scope,['Revision','Changed by','Reason','Known from','Intervals','In this window'],
   log.revisions.map((r,i)=>[String(r.revision),r.actor,r.reason,known[i+1],String(r.intervals),String(r.intervalsInWindow)]),[0,4,5],o.busy);
  const changed=log.rows.length,spans=timeLabels([...log.rows.map(r=>r.start),...log.rows.map(r=>r.end)],zone);
  renderRegularTable(get('interval-series-changes'),'interval-series-changes',scope,['Start','End','Revision','Previous value','New value','Changed by','Reason','Known from'],
   log.rows.map((r,i)=>[spans[i],spans[changed+i],String(r.revision),r.previousRevision===null?'None':r.previousValue??'Withdrawn',r.value??'Withdrawn',r.actor,r.reason,knownFrom.get(r.revision)??displayInstant(r.knownFrom,zone,true)]),[2,3,4],o.busy);
  sync();
 }
 async function read(){
  const selected=requireSeries(),owner=catalog,started=generation;
  const request={seriesId:selected.id,from:await from.instant(),to:await to.instant(),knownAt:await known.instant()};
  const reading=await o.api<Reading>('interval-series/values',request);
  if(catalog!==owner||started!==generation)return;
  const log=await o.api<ChangeLog>('interval-series/changes',request);
  if(catalog!==owner||started!==generation)return;
  if(reading.series?.id!==selected.id||log.series?.id!==selected.id||!Number.isInteger(reading.head)||!Array.isArray(reading.rows)||!Array.isArray(log.rows)||!Array.isArray(log.revisions))
   throw Error('The series values are missing or inconsistent. Reload and try again.');
  const current=request.knownAt===catalog!.knownAt;
  if(current)latest={seriesId:selected.id,head:reading.head};
  shown={scope:JSON.stringify([o.identity(),selected.id,request.from,request.to,request.knownAt]),current,reading,log};
  render();
 }

 function stage(change:Staged){
  staged=change;
  const form=get('interval-series-change-form'),value=field('interval-series-change-value');
  form.hidden=false;value.required=!change.withdraw;value.value=change.withdraw?'':change.value;
  get('interval-series-change-title').textContent=`${change.withdraw?'Withdraw':'Correct'} the interval starting ${change.label}`;
  get('interval-series-change-field').hidden=change.withdraw;
  get('interval-series-change-save').textContent=change.withdraw?'Withdraw interval':'Save correction';
  form.scrollIntoView({block:'nearest'});
 }
 // A confirmed command is already committed: later failures to reload never retain it.
 async function send(h:Holder,command:RequestCommand){
  const started=generation,owner=o.identity(),uncertain=h.pending.sending();
  const live=()=>started===generation&&owner===o.identity()&&h.pending.command===command;
  store(h);
  let receipt:Receipt;
  try{
   receipt=await o.api<Receipt>(command.path,{...command.body,requestId:command.operationId});
   if(receipt?.requestId!==command.operationId||typeof receipt.seriesId!=='string'||!Number.isInteger(receipt.revision)||!Number.isInteger(receipt.intervals)||(command.body.seriesId!==undefined&&receipt.seriesId!==command.body.seriesId))
    throw Error('The save receipt does not match the request. Check the change log before retrying.');
  }catch(error){
   if(!live())return;
   h.pending.failed((error as {code?:string}).code,uncertain);store(h);sync();throw error;
  }
  if(!live())return;
  h.pending.clear();store(h);await h.done(receipt,command);
 }
 async function revised(receipt:Receipt,command:RequestCommand){
  for(const id of ['interval-series-reason','interval-series-csv','interval-series-file'])field(id).value='';
  get('interval-series-saved').textContent=`Saved revision ${receipt.revision} · ${receipt.intervals} ${receipt.intervals===1?'interval':'intervals'}.`;
  if(await fetchCatalog(command.body.seriesId as string)&&windowComplete())await read();
 }
 async function defined(receipt:Receipt,command:RequestCommand){
  for(const id of ['interval-series-code','interval-series-name'])field(id).value='';
  for(const id of ['interval-series-kind','interval-series-specification','interval-series-clock','interval-series-minutes'])choice(id).value='';
  get('interval-series-defined').textContent=`Defined ${command.body.code}. Import values to give it its first revision.`;
  await fetchCatalog(receipt.seriesId);
 }
 async function revise(selected:Series,segments:{start:string;values:(string|null)[]}[]){
  get('interval-series-saved').textContent='';
  const command=revision.pending.begin('interval-series/revise',{seriesId:selected.id,expectedRevision:expectedRevision(selected),reason:field('interval-series-reason').value,segments});
  store(revision);sync();await send(revision,command);
 }

 function sync(){
  const busy=o.busy(),loaded=!!catalog,reviseLost=!!revision.pending.command;
  choice('interval-series-select').disabled=busy||!loaded||reviseLost;get<HTMLButtonElement>('interval-series-refresh').disabled=busy||!loaded;
  get<HTMLFieldSetElement>('interval-series-define-fields').disabled=busy||!catalog?.canDefine||!!definition.pending.command;
  choice('interval-series-specification').disabled=busy||!choice('interval-series-kind').value;
  get<HTMLFieldSetElement>('interval-series-window').disabled=busy||!series();
  get<HTMLFieldSetElement>('interval-series-revise-fields').disabled=busy||!catalog?.canRevise||!series()||reviseLost;
  for(const h of holders)get(h.panel).hidden=!loaded||!h.pending.command;
  // An outstanding definition must not hide inside the collapsed form.
  if(definition.pending.command)get<HTMLDetailsElement>('interval-series-define').open=true;
  get('interval-series-values').querySelectorAll<HTMLButtonElement>('button[data-interval-action]').forEach(b=>{b.disabled=busy||reviseLost;});
 }

 choice('interval-series-select').onchange=()=>{latest=undefined;clearResults();describe();sync();};
 choice('interval-series-kind').onchange=()=>{specifications();sync();};
 get('interval-series-refresh').onclick=()=>void o.act(async()=>{if(await fetchCatalog()&&windowComplete())await read();});
 get<HTMLFormElement>('interval-series-form').onsubmit=event=>{event.preventDefault();if(o.busy())return;void o.act(async()=>{clearResults();await read();});};
 get<HTMLFormElement>('interval-series-define-form').onsubmit=event=>{event.preventDefault();if(o.busy())return;void o.act(async()=>{
  get('interval-series-defined').textContent='';
  const command=definition.pending.begin('interval-series/define',{code:field('interval-series-code').value,name:field('interval-series-name').value,kind:choice('interval-series-kind').value,
   specification:choice('interval-series-specification').value,clock:choice('interval-series-clock').value,intervalMinutes:Number(choice('interval-series-minutes').value)});
  store(definition);sync();await send(definition,command);
 });};
 // The reason is checked before the action starts: a busy page disables every control, and a disabled control is never validated.
 get<HTMLFormElement>('interval-series-import-form').onsubmit=event=>{event.preventDefault();if(o.busy()||!field('interval-series-reason').reportValidity())return;void o.act(async()=>{
  const selected=requireSeries();
  await revise(selected,csvSegments(field('interval-series-csv').value,selected.intervalMinutes));
 });};
 get<HTMLFormElement>('interval-series-change-form').onsubmit=event=>{event.preventDefault();if(o.busy()||!staged||!field('interval-series-reason').reportValidity())return;const change=staged;void o.act(async()=>{
  await revise(requireSeries(),[{start:change.start,values:[change.withdraw?null:field('interval-series-change-value').value]}]);
 });};
 get('interval-series-change-cancel').onclick=()=>{if(!o.busy()){staged=undefined;get('interval-series-change-form').hidden=true;}};
 field('interval-series-file').onchange=()=>{
  const input=field('interval-series-file'),file=input.files?.[0];
  // The text area is the source of truth; clearing the chooser lets the same file be chosen again.
  if(file)void o.act(async()=>{field('interval-series-csv').value=await file.text();input.value='';});
 };
 for(const h of holders){
  get(h.retry).onclick=()=>void o.act(async()=>{if(h.pending.command)await send(h,h.pending.command);});
  get(h.discard).onclick=()=>{if(!o.busy()){h.pending.clear();store(h);sync();}};
 }

 return{
  sync,
  async load(){
   // A failed read leaves an empty page, never the previous visit's catalog.
   const selectedId=series()?.id;unload();
   if(!await fetchCatalog(selectedId))return;
   holders.forEach(restore);
   // An outstanding save is reviewed against its own series.
   const lost=revision.pending.command?.body.seriesId;
   if(typeof lost==='string'&&catalog?.series.some(s=>s.id===lost)){choice('interval-series-select').value=lost;describe();}
   sync();
  },
  reset(){
   unload();for(const h of holders)h.pending.clear();
   from.clear();to.clear();known.clear();
   for(const id of ['interval-series-code','interval-series-name','interval-series-reason','interval-series-csv','interval-series-file','interval-series-change-value'])field(id).value='';
   for(const id of ['interval-series-defined','interval-series-saved'])get(id).textContent='';
  },
  refresh(){from.refresh();to.refresh();known.refresh();staged=undefined;get('interval-series-change-form').hidden=true;render();},
 };
}
