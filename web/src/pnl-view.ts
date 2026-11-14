import {node} from './risk-report-elements.js';
import {prepareAmountDisplay} from './amount-display.js';
import {PendingCommand,actorTransport,type RequestCommand} from './pending-command.js';
import {zonedInput} from './user-time.js';
import {drawReport} from './pnl-report.js';
import {reportVersion,type Report} from './pnl-model.js';

type ClockChoice={code:string;kind:string;fixedOffsetSeconds:number|null;ianaZone:string|null;tzdbVersion:string|null};
// The price bump of the opening estimates and the agreement they must reach, as a request states them.
type BumpSelection={ticks:number;priceSteps:number|null;absoluteTolerance:string;deltaRelativeTolerance:string;gammaRelativeTolerance:string};
type Bounds={min:number;max:number};
type Policies={accounting:string;attribution:string;factors:string;factorBump:{offered:BumpSelection;ticks:Bounds;priceSteps:number[]};clockPolicy:string;
 timeModes:{id:string;label:string}[];clocks:ClockChoice[]};
// A close the person can compare, labelled as the header names it, with its close convention when it is a recorded close.
type Frame={id:string;at:string;label:string;session:string|null};
type SavedEntry={id:string;name:string;savedAt:string;openingAt:string;closingAt:string;books:string[];tradeSelection:string};
type Shown={report:Report;artifact?:string;name?:string;unsaved?:string};
type Options={api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;canRun:()=>boolean;identity:()=>string;zone:()=>string;
 frames:()=>Frame[];selected:()=>string|undefined;scope:()=>{bookIds:string[];portfolioIds:string[];tradeIds:string[]|null};knownAt:()=>string;instant:(at:string)=>string;changed:()=>void};
const oneDay='one_calendar_day_model_decay_v1';
// What the server answers when it refuses a save outright. Such a refusal of a first attempt leaves nothing saved.
const refused=(code:string|undefined)=>!!code&&(code.startsWith('pnl_')||code==='access_denied'||code==='request_format');

/** The trading P&L page: two closes, one knowledge cutoff, Run, and an optional Save of the exact result. */
export function setupPnlView(host:HTMLElement,o:Options){
 const title=node('div'),controls=node('div'),opening=node('select'),closing=node('select'),known=node('input'),timeMode=node('select'),clock=node('select'),run=node('button','Run P&L'),note=node('p');
 // The application's page title names the page; the card says what it does.
 title.className='pnl-intro';title.append(node('p','Compare two closes of the selected Risk scope. The result reconciles value and cash, and shows what opening price Greeks explain.'));
 controls.className='pnl-controls';run.type='button';note.setAttribute('role','status');known.type='datetime-local';known.step='1';
 const knownName=()=>'Known as of · '+o.zone(),knownField=node('label',knownName());
 for(const [label,input] of [['Opening close',opening],['Closing close',closing],[knownField,known],['Time effect',timeMode],['Calendar clock',clock]] as const){
  const field=typeof label==='string'?node('label',label):label;field.append(input);controls.append(field);
  if(input===opening||input===closing)field.className='pnl-close';else if(input===known)field.className='pnl-known';
 }
 controls.append(run);
 // The price bump is an advanced setting: closed until opened, filled with what the backend offers, and sent as selected.
 const advanced=node('details'),selection=node('summary'),bumpFields=node('div'),basis=node('select'),steps=node('select'),ticks=node('input'),absolute=node('input'),deltaAgreement=node('input'),gammaAgreement=node('input'),
  agreements=[absolute,deltaAgreement,gammaAgreement],ticksName=node('label','Trading ticks'),offer=node('button','Restore offered values');
 advanced.className='pnl-advanced';bumpFields.className='pnl-controls';offer.type='button';offer.className='secondary';
 basis.append(new Option('Price steps the pricing models declare','steps'),new Option('Trading ticks','ticks'));
 ticks.type='number';ticks.step='1';
 for(const agreement of agreements)agreement.inputMode='decimal';
 for(const [label,input] of [['Price bump',basis],['Price steps',steps],[ticksName,ticks],['Agreement · absolute tolerance',absolute],
  ['Delta agreement · relative tolerance',deltaAgreement],['Gamma agreement · relative tolerance',gammaAgreement]] as const){
  const field=typeof label==='string'?node('label',label):label;field.append(input);bumpFields.append(field);
 }
 bumpFields.append(offer);
 advanced.append(selection,node('p','How far each price is moved to estimate delta and gamma, and how closely the estimates at half, full and double that move must agree to be used. '+
  'A pricing model may declare a price step: the smallest price move it resolves.'),bumpFields);
 // The cutoff reads as local time. Untouched, it stays the exact instant it was given; edited, the backend resolves it.
 const knownAt=zonedInput(known,o.zone,o.api);
 const result=node('div'),library=node('details'),libraryBody=node('div');library.append(node('summary','Saved P&L reports'),libraryBody);library.className='pnl-library';
 host.classList.add('pnl-view');host.replaceChildren(title,controls,advanced,note,result,library);
 let epoch=0,owner=o.identity(),current:Shown|undefined,currency=0,policies:Policies|undefined,chosen=false,bumpChosen=false;
 const pending=new PendingCommand(refused);
 const transport=actorTransport<RequestCommand>(()=>'openetrm-pnl-save:'+o.identity(),()=>sessionStorage);
 function restore(){
  const saved=transport.read();
  pending.command=saved?.path==='portfolio/pnl/save'&&typeof saved.operationId==='string'&&typeof saved.body?.artifact==='string'&&typeof saved.body?.name==='string'?saved:undefined;
 }
 restore();
 const guarded=(version:number)=>version===epoch&&owner===o.identity();
 const setNote=(text:string)=>{note.textContent=text;};
 // The amounts the page shows are shown as the shared amount display formats them; the evidence a report carries is not sent
 // for display. A report whose display cannot be prepared is still drawn, with its exact amounts: formatting never hides a result.
 const prepared=async(report:Report)=>{
  const shown={totals:report.totals,positions:report.positions,factors:report.factors,time:{totals:report.time.totals}};
  await prepareAmountDisplay(shown,request=>o.api<string[]>('amounts/display',request)).catch(()=>undefined);return report;
 };
 const action=(text:string,work:()=>void,secondary=false)=>{const control=node('button',text);control.type='button';control.disabled=o.busy();if(secondary)control.className='secondary';control.onclick=work;return control;};
 // The person may give up an unconfirmed save. Whether it reached the server is read from the saved reports, which are read again.
 const discard=()=>action('Discard unconfirmed save',()=>{
  pending.clear();transport.write(undefined);setNote('Discarded the unconfirmed save. If it reached the server, the report is listed under Saved P&L reports.');drawSave();o.changed();
  void o.act(readLibrary);},true);
 // The bar under a result: its name and Save while it can be kept, and otherwise what became of it.
 function saveBar(shown:Shown){
  const save=node('div');save.className='pnl-save';
  if(shown.artifact){
   const name=node('input');name.type='text';name.maxLength=80;name.placeholder='Name this report';name.setAttribute('aria-label','Report name');
   name.value=typeof pending.command?.body.name==='string'?pending.command.body.name:'';
   // The name of an unconfirmed save is fixed. It is read-only rather than disabled: the application re-enables every control when an action ends.
   name.readOnly=!!pending.command;
   const keep=action(pending.command?'Retry save':'Save report',()=>void o.act(async()=>{
    if(!name.value.trim())throw Error('Name the report before saving.');
    await sendSave(pending.begin('portfolio/pnl/save',{name:name.value.trim(),artifact:shown.artifact}));}));
   save.append(node('span','Keep this exact result'),name,keep);if(pending.command)save.append(discard());
  }else save.append(node('span',shown.name===undefined?`Not saved · ${shown.unsaved}`:`Saved · ${shown.name}`));
  return save;
 }
 function draw(){
  result.replaceChildren();
  if(!current&&pending.command){
   const recovery=node('div');recovery.className='pnl-save';
   recovery.append(node('p',`Save of “${String(pending.command.body.name)}” is unconfirmed.`),action('Retry save',()=>void o.act(()=>sendSave(pending.command!))),discard());
   result.append(recovery);return;
  }
  if(!current){
   const empty=node('div');empty.className='pnl-empty';
   empty.append(node('h3','No result yet'),node('p','Choose two closes and run the selected Risk scope. A result stays unsaved until you choose to keep it.'));
   result.append(empty);return;
  }
  const report=current.report;if(report.version!==reportVersion||!report.totals.length)throw Error('The backend P&L result is incomplete.');
  // Choosing a currency draws the report again; the keyboard stays on the chooser.
  drawReport(result,report,{instant:o.instant,currency,chooseCurrency:index=>{currency=index;draw();result.querySelector<HTMLElement>('.pnl-hero select')?.focus();}});
  result.append(saveBar(current));
 }
 // What happens to a save changes the bar alone: the result above it stays as the person sorted and opened it.
 function drawSave(){const bar=result.querySelector(':scope > .pnl-save');if(current&&bar)bar.replaceWith(saveBar(current));else draw();}
 async function sendSave(command:RequestCommand){
  const version=epoch,retry=pending.sending();transport.write(command);o.changed();
  try{
   const saved=await o.api<{report:Report;name:string}>('portfolio/pnl/save',{requestId:command.operationId,...command.body});if(!guarded(version))return;
   pending.clear();transport.write(undefined);
   if(current?.report.id===saved.report.id){current={report:current.report,name:saved.name};drawSave();}
   else{const report=await prepared(saved.report);if(!guarded(version))return;current={report,name:saved.name};draw();}
   setNote('Saved the exact displayed result.');
  }catch(error){
   if(guarded(version)){
    const code=(error as {code?:string}).code;
    if(code==='pnl_unsaved'){
     pending.clear();if(current)current={report:current.report,unsaved:'Run again to create a savable result.'};
     setNote('The result was not saved and is no longer available to save. Run the report again.');
    }else{
     pending.failed(code,retry);
     setNote(pending.command?'Save was not confirmed. Retry preserves the original request and result.':
      code==='pnl_save_conflict'?'The save was refused: this result is already saved under another name.':'The save was refused and nothing was saved.');
    }
    transport.write(pending.command);drawSave();
    // A definite refusal can mean the result is already kept: the saved reports say whether it is.
    if(!pending.command)await readLibrary().catch(()=>undefined);
   }
   throw error;
  }finally{o.changed();}
  // The save is confirmed whether or not the saved reports can then be read.
  await readLibrary();
 }
 const clockNeeded=()=>timeMode.value===oneDay;
 // The closed section still says what will be sent.
 function describeBump(){
  const byStep=basis.value==='steps',count=(value:string,unit:string)=>`${value} ${unit}${value==='1'?'':'s'}`;
  steps.parentElement!.hidden=!byStep;ticksName.firstChild!.textContent=byStep?'Trading ticks · where no model declares a step':'Trading ticks';
  selection.textContent=policies?'Advanced settings · price bump of '+(byStep?`${count(steps.value,'price step')} where a model declares one, otherwise `:'')+count(ticks.value,'tick')+
   ` · agreement within ${absolute.value.trim()} + ${deltaAgreement.value.trim()} of a delta, ${gammaAgreement.value.trim()} of a gamma`:'Advanced settings';
 }
 // Until the person changes it, the selection is what the backend offers. Its bounds are the backend's.
 function offerBump(){
  const {offered,ticks:tickBounds,priceSteps:stepCounts}=policies!.factorBump,stepCount=steps.value;
  steps.replaceChildren(...stepCounts.map(count=>new Option(String(count))));steps.value=stepCount;ticks.min=String(tickBounds.min);ticks.max=String(tickBounds.max);
  if(!bumpChosen){
   basis.value=offered.priceSteps===null?'ticks':'steps';steps.value=String(offered.priceSteps??stepCounts[0]);ticks.value=String(offered.ticks);
   absolute.value=offered.absoluteTolerance;deltaAgreement.value=offered.deltaRelativeTolerance;gammaAgreement.value=offered.gammaRelativeTolerance;
  }
  describeBump();
 }
 // What the request states. The backend decides what a selection means; the page only refuses to send one it cannot read.
 function selectedBump():BumpSelection{
  const tickCount=Number(ticks.value);
  if(!ticks.value.trim()||!Number.isInteger(tickCount)||tickCount<Number(ticks.min)||tickCount>Number(ticks.max)||agreements.some(agreement=>!agreement.value.trim()))
   throw Error(`Enter whole trading ticks from ${ticks.min} to ${ticks.max} and the three agreement tolerances.`);
  // A count chosen earlier that the backend no longer offers leaves none chosen.
  if(basis.value==='steps'&&!steps.value)throw Error('Choose the number of price steps.');
  return{ticks:tickCount,priceSteps:basis.value==='steps'?Number(steps.value):null,absoluteTolerance:absolute.value.trim(),deltaRelativeTolerance:deltaAgreement.value.trim(),gammaRelativeTolerance:gammaAgreement.value.trim()};
 }
 async function loadPolicies(){
  const version=epoch,loaded=await o.api<Policies>('portfolio/pnl/policies');if(!guarded(version))return;
  policies=loaded;offerBump();const mode=timeMode.value,selectedClock=clock.value;
  timeMode.replaceChildren(...loaded.timeModes.map(m=>new Option(m.label,m.id)));timeMode.value=loaded.timeModes.some(m=>m.id===mode)?mode:oneDay;
  clock.replaceChildren(new Option('Choose a clock…',''),...loaded.clocks.map(c=>new Option(c.code,c.code)));clock.value=selectedClock;clock.disabled=o.busy()||!clockNeeded();
 }
 async function readLibrary(){
  const version=epoch,listed=await o.api<{reports:SavedEntry[];more:boolean}>('portfolio/pnl/saved');if(!guarded(version))return;
  libraryBody.replaceChildren();
  for(const saved of listed.reports){
   const entry=node('div'),open=node('button',saved.name);entry.className='pnl-saved';open.className='pnl-saved-link';open.type='button';
   open.onclick=()=>void o.act(async()=>{
    if(pending.command)throw Error('Reconcile the outstanding save before opening another report.');
    const version=epoch,loaded=await o.api<{report:Report;name:string}>('portfolio/pnl/saved/'+encodeURIComponent(saved.id));if(!guarded(version))return;
    if(loaded.report.version!==reportVersion)throw Error('This report was saved in an earlier format and cannot be shown.');
    const report=await prepared(loaded.report);if(!guarded(version))return;
    current={report,name:loaded.name};currency=0;draw();});
   entry.append(open,node('small',`Saved ${o.instant(saved.savedAt)} · ${o.instant(saved.openingAt)} → ${o.instant(saved.closingAt)} · ${saved.books.join(' · ')} · ${saved.tradeSelection}`));
   libraryBody.append(entry);
  }
  if(!listed.reports.length)libraryBody.append(node('p','No saved P&L reports.'));
  if(listed.more){const more=node('p','Showing the 100 most recently saved reports. Older reports are not listed.');more.className='pnl-library-more';libraryBody.append(more);}
  // The saved reports are the record of what is kept: an unsaved result on screen that they hold reads as saved, under its name.
  const held=current&&current.name===undefined&&!pending.command?listed.reports.find(saved=>saved.id===current!.report.id):undefined;
  if(held){current={report:current!.report,name:held.name};setNote(`This result is already saved as “${held.name}”.`);drawSave();}
 }
 run.onclick=()=>void o.act(async()=>{
  if(pending.command)throw Error('Reconcile the outstanding save before running another report.');
  if(!opening.value||!closing.value||!known.value)throw Error('Choose both closes and an explicit knowledge cutoff.');
  const version=++epoch;setNote('Preparing both closes and repricing shared factor scenarios…');
  try{
   // What the backend offers is shown before it is sent: a run pressed before it arrived shows it and stops.
   const unseen=!policies;if(unseen)await loadPolicies();if(!guarded(version))return;
   if(unseen)throw Error('The settings the backend offers are now shown, Advanced settings included. Check them, then run again.');
   const calendar=policies!.clocks.find(c=>c.code===clock.value)??null;if(clockNeeded()&&!calendar)throw Error('Choose the calendar clock explicitly.');
   const factorBump=selectedBump();
   const cutoff=await knownAt.instant();if(!guarded(version))return;
   const endpoint=(id:string)=>({snapshotId:id,convention:'trading_workspace_v1',knowledgeMode:'at'});
   const response=await o.api<{report:Report;artifact:string|null;saveRefusal:{code:string;message:string}|null}>('portfolio/pnl/run',{...o.scope(),opening:endpoint(opening.value),closing:endpoint(closing.value),
    knownAt:cutoff,accountingPolicy:policies!.accounting,attributionPolicy:policies!.attribution,factorPolicy:policies!.factors,factorBump,
    time:{mode:timeMode.value,policy:policies!.clockPolicy,clock:clockNeeded()?calendar:null}});
   if(!guarded(version))return;
   // The result is presented whether or not the backend could keep it for a later Save.
   if(!response.artifact&&!response.saveRefusal)throw Error('The backend P&L result is incomplete.');
   const report=await prepared(response.report);if(!guarded(version))return;
   current=response.artifact?{report,artifact:response.artifact}:{report,unsaved:response.saveRefusal!.message};currency=0;
   setNote(current.artifact?'Reconciled. Inspect the result, then save it if you want to keep it.':'Reconciled. This result cannot be saved.');draw();
  }catch(error){if(guarded(version))setNote('P&L did not run.');throw error;}
 });
 function inputsChanged(){epoch++;current=undefined;draw();setNote('Inputs changed. Run P&L for this selection.');}
 for(const field of [opening,closing])field.onchange=()=>{chosen=true;inputsChanged();};
 known.onchange=clock.onchange=inputsChanged;timeMode.onchange=()=>{clock.disabled=o.busy()||!clockNeeded();inputsChanged();};
 for(const field of [basis,steps,ticks,...agreements])field.onchange=()=>{bumpChosen=true;describeBump();inputsChanged();};
 // A selection is one person's: it is dropped for what the backend offers when they ask, and when another person takes over.
 function forgetBump(){bumpChosen=false;if(policies)offerBump();}
 offer.onclick=()=>{forgetBump();inputsChanged();};
 describeBump();
 function refreshFrames(){
  const frames=o.frames().slice().sort((a,b)=>a.at.localeCompare(b.at)),before=[opening.value,closing.value];
  for(const [index,select] of [opening,closing].entries()){
   select.replaceChildren(new Option(frames.length?'Choose a close…':'No closes to compare',''),...frames.map(f=>new Option(f.label,f.id)));
   select.value=frames.some(f=>f.id===before[index])?before[index]:'';
  }
  // Until the person chooses, the header's close is compared with what came before it: the close before it of its own close
  // convention, where it has a convention and such a close exists, and otherwise the frame before it of any kind. Both show in
  // the pickers and can be changed before Run. When the header names no close among them, nothing is offered.
  if(!chosen){
   const end=frames.find(f=>f.id===o.selected()),earlier=end?frames.filter(f=>f.at<end.at):[];
   const start=(end?.session?earlier.filter(f=>f.session===end.session).at(-1):undefined)??earlier.at(-1);
   closing.value=end?.id??'';opening.value=start?.id??'';
  }
  if(!frames.length)setNote('Choose a market close in the header to list the closes you can compare.');
  followKnowledge();
 }
 // An empty cutoff takes the Risk scope's own knowledge cutoff, once that scope can be read.
 function followKnowledge(){if(!known.value){const cutoff=o.knownAt();if(cutoff)knownAt.set(cutoff);}}
 return{
  async load(){refreshFrames();draw();await Promise.all([loadPolicies(),readLibrary()]);},
  sync(available:boolean){
   if(owner!==o.identity()){owner=o.identity();epoch++;current=undefined;pending.clear();restore();knownAt.clear();chosen=false;forgetBump();draw();}
   knownField.firstChild!.textContent=knownName();knownAt.refresh();if(available)followKnowledge();
   run.disabled=o.busy()||!o.canRun()||!available;for(const field of [opening,closing,known,timeMode,basis,steps,ticks,...agreements,offer])field.disabled=o.busy();
   clock.disabled=o.busy()||!clockNeeded();
  },
  // A change of scope or market context. The closes a person chose stay chosen; ones they did not follow the header again.
  reset(){epoch++;current=undefined;knownAt.clear();setNote('');if(owner!==o.identity()){owner=o.identity();pending.clear();restore();chosen=false;forgetBump();}draw();},
  // The display precision changed: the report on screen is formatted again.
  async refresh(){const version=epoch,shown=current;if(!shown)return;await prepared(shown.report);if(guarded(version)&&current===shown)draw();},
  unconfirmed:()=>!!pending.command};
}
