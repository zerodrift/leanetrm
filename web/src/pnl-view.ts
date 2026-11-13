import {node,table} from './risk-report-elements.js';
import {plotNumber} from './plot-number.js';
import {currencyKey} from './currency-labels.js';
import {PendingCommand,actorTransport,type RequestCommand} from './pending-command.js';

type Currency={code:string;revision:{catalogId:string;revisionId:string}};
type Money={numerator:string;denominator:string;display:string;currency:Currency};
type Step={label:string;amount:Money;start:Money;end:Money};
type Values={openingValue:Money;closingValue:Money;valueMovement:Money;cash:Money;priorCash:Money;transfers:Money;economicPnl:Money;explained:Money;residual:Money;bridge:Step[];greekCoverage:string;unavailableEstimates:number};
type ClockChoice={code:string;kind:string;fixedOffsetSeconds:number|null;ianaZone:string|null;tzdbVersion:string|null};
type Policies={accounting:string;attribution:string;factors:string;clockPolicy:string;timeModes:{id:string;label:string}[];clocks:ClockChoice[]};
const currencyId=(c:Currency)=>currencyKey({code:c.code,...c.revision});
type Report={id:string;version:string;scope:{books:{id:string;name:string}[];tradeSelection:string};opening:{name:string;at:string;context:{knownAt:string}};closing:{name:string;at:string;context:{knownAt:string}};
 time:{mode:string;status:string;totals:{currency:Currency;value:Money}[]|null;partitions:{status:string;code?:string;detail?:string;clock:{at:string;calendar?:{zone:string}}}[]};
 policies:{coverage:string};totals:{currency:Currency;values:Values}[];unavailableFactors:unknown[];
 positions:{position:{tradeId:string;bindingId:string;bookId:string};tradeReference:string;instrumentReference:string;state:string;values:Values;contributions:{formula:string;axes:string[];amount:Money|null;unavailableReason:string|null}[]}[]};
type Frame={id:string;name:string;at:string};
type Options={api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;canRun:()=>boolean;identity:()=>string;
 frames:()=>Frame[];scope:()=>{bookIds:string[];portfolioIds:string[];tradeIds:string[]|null};knownAt:()=>string;instant:(at:string)=>string;changed:()=>void};
const exact=(m:Money)=>m.denominator==='1'?m.numerator:`${m.numerator}/${m.denominator}`;
const number=(m:Money)=>plotNumber(exact(m));
const sign=(m:Money)=>m.numerator.startsWith('-')?'loss':/^0+$/.test(m.numerator)?'flat':'gain';

/** Presents backend reconciliation values. Only chart geometry uses JavaScript numbers. */
export function setupPnlView(host:HTMLElement,o:Options){
 const title=node('div'),eyebrow=node('p','PERFORMANCE ATTRIBUTION'),heading=node('h2','Every move, explained.'),intro=node('p','Compare two closes. Reconcile remaining value and cash, then see what opening price Greeks explain.');
 title.className='pnl-intro';eyebrow.className='pnl-eyebrow';title.append(eyebrow,heading,intro);
 const controls=node('div'),opening=node('select'),closing=node('select'),known=node('input'),timeMode=node('select'),clock=node('select'),run=node('button','Run P&L'),note=node('p');
 controls.className='pnl-controls';run.type='button';note.setAttribute('role','status');known.type='text';known.placeholder='Exact UTC knowledge cutoff';known.spellcheck=false;
 for(const [label,input] of [['Opening close',opening],['Closing close',closing],['Known as of · UTC',known],['Time effect',timeMode],['Calendar clock',clock]] as const){const field=node('label',label);field.append(input);controls.append(field);}controls.append(run);
 const result=node('div'),library=node('details'),libraryBody=node('div');library.append(node('summary','Saved P&L reports'),libraryBody);library.className='pnl-library';
 host.classList.add('pnl-view');host.replaceChildren(title,controls,note,result,library);
 let epoch=0,owner=o.identity(),current:{report:Report;artifact?:string;name?:string;saveUnavailable?:boolean}|undefined,currency=0,policies:Policies|undefined;
 const pending=new PendingCommand(code=>!!code&&['pnl_artifact','pnl_save','request_format'].includes(code));
 const transport=actorTransport<RequestCommand>(()=>'openetrm-pnl-save:'+o.identity(),()=>sessionStorage);
 function restore(){const saved=transport.read();pending.command=saved?.path==='portfolio/pnl/save'&&typeof saved.operationId==='string'&&typeof saved.body?.artifact==='string'&&typeof saved.body?.name==='string'?saved:undefined;}
 restore();
 const guarded=(version:number)=>version===epoch&&owner===o.identity();
 const setNote=(text:string)=>{note.textContent=text;};
 function amount(m:Money){const text=node('span',m.display);text.className='pnl-money '+sign(m);text.title=exact(m);return text;}
 function waterfall(steps:Step[]){
  const wrap=node('div');wrap.className='pnl-waterfall';const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.setAttribute('viewBox','0 0 980 270');svg.setAttribute('role','img');svg.setAttribute('aria-label','P&L attribution bridge; exact values follow in the bridge table.');
  const values=steps.flatMap(s=>[number(s.start),number(s.end)]),low=Math.min(0,...values),high=Math.max(0,...values),span=high-low||1,y=(v:number)=>220-(v-low)/span*175;
  const element=(tag:string,attributes:Record<string,string>,text?:string)=>{const el=document.createElementNS(svg.namespaceURI,tag);for(const [k,v] of Object.entries(attributes))el.setAttribute(k,v);if(text!==undefined)el.textContent=text;return el;};
  svg.append(element('line',{x1:'30',x2:'950',y1:String(y(0)),y2:String(y(0)),class:'pnl-zero'}));
  steps.forEach((s,n)=>{const x=44+n*130,a=y(number(s.start)),b=y(number(s.end));const bar=element('rect',{x:String(x),y:String(Math.min(a,b)),width:'82',height:String(Math.max(2,Math.abs(a-b))),rx:'4',class:'pnl-bar '+sign(s.amount)});bar.append(element('title',{},`${s.label}: ${s.amount.display} (${exact(s.amount)})`));svg.append(bar,
   element('text',{x:String(x+41),y:String(Math.min(a,b)-10),'text-anchor':'middle',class:'pnl-bar-value'},s.amount.display),
   element('text',{x:String(x+41),y:'251','text-anchor':'middle',class:'pnl-bar-label'},s.label));
   if(n<steps.length-1)svg.append(element('line',{x1:String(x+83),x2:String(x+129),y1:String(b),y2:String(b),class:'pnl-connector'}));});
  wrap.append(svg);return wrap;
 }
 function draw(){
  result.replaceChildren();if(!current&&pending.command){const recovery=node('div');recovery.className='pnl-save';recovery.append(node('p',`Save of “${String(pending.command.body.name)}” is unconfirmed.`));const retry=node('button','Retry save');retry.type='button';retry.disabled=o.busy();retry.onclick=()=>void o.act(()=>sendSave(pending.command!));recovery.append(retry);result.append(recovery);return;}if(!current){const empty=node('div');empty.className='pnl-empty';empty.append(node('span','Δ  Γ  ×'),node('h3','From close to clarity.'),node('p','Choose two closes and run the selected Risk scope. Results stay unsaved until you choose to keep them.'));result.append(empty);return;}
  const report=current.report;if(report.version!=='trading_pnl_report_v1'||!report.totals.length)throw Error('The backend P&L result is incomplete.');
  const total=report.totals[currency]??report.totals[0],values=total.values,hero=node('div');hero.className='pnl-hero';
  const context=node('div');context.append(node('p','ECONOMIC P&L'),amount(values.economicPnl),node('small',total.currency.code.toUpperCase()));
  const period=node('div');period.className='pnl-period';period.append(node('span',report.scope.books.map(b=>b.name).join(' · ')+' · '+report.scope.tradeSelection),node('strong',`${o.instant(report.opening.at)} → ${o.instant(report.closing.at)}`),node('span',`${report.positions.length} held / cash contributions`),node('span',`Known as of ${o.instant(report.opening.context.knownAt)}`));hero.append(context,period);
  if(report.totals.length>1){const chooser=node('select');chooser.setAttribute('aria-label','Report currency');for(const [n,t] of report.totals.entries())chooser.add(new Option(t.currency.code.toUpperCase(),String(n),false,n===currency));chooser.onchange=()=>{currency=Number(chooser.value);draw();};hero.append(chooser);}result.append(hero);
  const metrics=node('div');metrics.className='pnl-metrics';for(const [label,money] of [['PV movement',values.valueMovement],['Period cash',values.cash],['Available Greek effects',values.explained],['Residual',values.residual]] as const){const card=node('div');card.append(node('span',label),amount(money));metrics.append(card);}result.append(metrics);
  const time=node('section');time.className='pnl-section';const timeLabel=report.time.mode==='one_calendar_day_model_decay_v1'?'One-day model decay':'Opening-to-closing time roll';time.append(node('h3',timeLabel));
  if(report.time.status==='complete'){const value=report.time.totals?.find(t=>currencyId(t.currency)===currencyId(total.currency));time.append(value?amount(value.value):node('span','No opening exposure in this currency.'));}else time.append(node('p','Time effect unavailable for the complete opening scope.'));
  time.append(node('p','Frozen opening markets and knowledge. This diagnostic is separate from the period reconciliation.'));
  for(const part of report.time.partitions){const text=part.status==='complete'?`Value at ${o.instant(part.clock.at)}${part.clock.calendar?' · '+part.clock.calendar.zone:''}`:`${part.code}: ${part.detail}`;time.append(node('small',text));}result.append(time);
  const bridge=node('section');bridge.className='pnl-section';bridge.append(node('h3','What drove the result'),node('p',report.policies.coverage));
  if(values.greekCoverage==='partial')bridge.append(node('p',`Partial price-Greek coverage · ${report.unavailableFactors.length} unavailable factors · ${values.unavailableEstimates} unstable estimates. Their effects remain in residual.`));
  try{bridge.append(waterfall(values.bridge));}catch{bridge.append(node('p','The bridge cannot be plotted at this scale. Exact values remain available below.'));}
  const bridgeTable=node('details');bridgeTable.append(node('summary','Exact reconciliation'),table(['Contribution','Amount'],values.bridge.map(s=>[s.label,amount(s.amount)])));bridge.append(bridgeTable);result.append(bridge);
  const positions=node('section');positions.className='pnl-section';positions.append(node('h3','Positions & cash'));
  positions.append(table(['Trade / binding','Change','Opening PV','Closing PV','Cash','P&L','Price Greeks','Residual'],report.positions.filter(p=>currencyId(p.values.economicPnl.currency)===currencyId(total.currency)).map(p=>{
   const label=node('details');label.append(node('summary',`${p.tradeReference??'Cash'} · ${p.instrumentReference??'Binding'}`));label.append(table(['Greek','Axes','Contribution / coverage'],p.contributions.map(c=>[c.formula,c.axes.join(', '),c.amount?amount(c.amount):c.unavailableReason??'Unavailable'])));
   return[label,p.state,amount(p.values.openingValue),amount(p.values.closingValue),amount(p.values.cash),amount(p.values.economicPnl),amount(p.values.explained),amount(p.values.residual)];})));result.append(positions);
  const save=node('div');save.className='pnl-save';if(current.artifact){const name=node('input');name.type='text';name.maxLength=80;name.placeholder='Name this report';name.setAttribute('aria-label','Report name');name.value=typeof pending.command?.body.name==='string'?pending.command.body.name:'';const button=node('button',pending.command?'Retry save':'Save report');button.type='button';button.disabled=o.busy();
   name.disabled=!!pending.command;
   button.onclick=()=>void o.act(async()=>{if(!name.value.trim())throw Error('Name the report before saving.');await sendSave(pending.begin('portfolio/pnl/save',{name:name.value.trim(),artifact:current!.artifact}));});save.append(node('span','Keep this exact result'),name,button);
  }else save.append(node('span',current.saveUnavailable?'Not saved · Run again to create a savable result.':`Saved · ${current.name??'P&L report'}`));result.append(save);
 }
 async function sendSave(command:RequestCommand){const version=epoch,retry=pending.sending();transport.write(command);o.changed();
  try{const saved=await o.api<{report:Report;name:string}>('portfolio/pnl/save',{requestId:command.operationId,...command.body});if(!guarded(version))return;pending.clear();transport.write(undefined);current={report:saved.report,name:saved.name};setNote('Saved the exact displayed result.');draw();await readLibrary();}
  catch(error){if(guarded(version)){const code=(error as {code?:string}).code;
   if(code==='pnl_unsaved'){pending.clear();if(current)current={report:current.report,saveUnavailable:true};setNote('The result was not saved and is no longer available to save. Run the report again.');}
   else{pending.failed(code,retry);setNote('Save was not confirmed. Retry preserves the original request and result.');}
   transport.write(pending.command);draw();}throw error;}finally{o.changed();}}
 async function loadPolicies(){const version=epoch;const loaded=await o.api<Policies>('portfolio/pnl/policies');if(!guarded(version))return;policies=loaded;const selected=timeMode.value;timeMode.replaceChildren();for(const mode of loaded.timeModes)timeMode.add(new Option(mode.label,mode.id));timeMode.value=loaded.timeModes.some(m=>m.id===selected)?selected:'one_calendar_day_model_decay_v1';const selectedClock=clock.value;clock.replaceChildren(new Option('Choose a clock…',''));for(const choice of loaded.clocks)clock.add(new Option(choice.code,choice.code));clock.value=selectedClock;clock.disabled=o.busy()||timeMode.value!=='one_calendar_day_model_decay_v1';}
 timeMode.onchange=()=>{clock.disabled=o.busy()||timeMode.value!=='one_calendar_day_model_decay_v1';};
 async function readLibrary(){const version=epoch;const reports=await o.api<{id:string;name:string;savedAt:string}[]>('portfolio/pnl/saved');if(!guarded(version))return;libraryBody.replaceChildren();for(const saved of reports){const open=node('button',saved.name);open.className='pnl-saved-link';open.type='button';open.onclick=()=>void o.act(async()=>{if(pending.command)throw Error('Reconcile the outstanding save before opening another report.');const version=epoch;const loaded=await o.api<{report:Report;name:string}>('portfolio/pnl/saved/'+encodeURIComponent(saved.id));if(!guarded(version))return;current=loaded;currency=0;draw();});libraryBody.append(open);}if(!reports.length)libraryBody.append(node('p','No saved P&L reports.'));}
 run.onclick=()=>void o.act(async()=>{if(pending.command)throw Error('Reconcile the outstanding save before running another report.');if(!opening.value||!closing.value||!known.value)throw Error('Choose both closes and an explicit knowledge cutoff.');const version=++epoch;setNote('Preparing both closes and repricing shared factor scenarios…');
  if(!policies)await loadPolicies();if(!guarded(version))return;
  const calendar=policies!.clocks.find(c=>c.code===clock.value)??null;if(timeMode.value==='one_calendar_day_model_decay_v1'&&!calendar)throw Error('Choose the calendar clock explicitly.');
  const endpoint=(id:string)=>({snapshotId:id,convention:'trading_workspace_v1',knowledgeMode:'at'});
  const response=await o.api<{report:Report;artifact:string}>('portfolio/pnl/run',{...o.scope(),opening:endpoint(opening.value),closing:endpoint(closing.value),knownAt:known.value,
   accountingPolicy:policies!.accounting,attributionPolicy:policies!.attribution,factorPolicy:policies!.factors,time:{mode:timeMode.value,policy:policies!.clockPolicy,clock:timeMode.value==='one_calendar_day_model_decay_v1'?calendar:null}});if(!guarded(version))return;current=response;currency=0;setNote('Reconciled. Inspect the result, then save it if you want to keep it.');draw();});
 function inputsChanged(){epoch++;current=undefined;draw();setNote('Inputs changed. Run P&L for this selection.');}
 for(const field of [opening,closing,known])field.onchange=inputsChanged;
 const timeChange=timeMode.onchange;timeMode.onchange=event=>{timeChange?.call(timeMode,event);inputsChanged();};clock.onchange=inputsChanged;
 function refreshFrames(){const before=[opening.value,closing.value];const frames=o.frames().slice().sort((a,b)=>a.at.localeCompare(b.at));for(const [index,select] of [opening,closing].entries()){select.replaceChildren(new Option('Choose a close…',''));for(const frame of frames)select.add(new Option(frame.name,frame.id));select.value=frames.some(f=>f.id===before[index])?before[index]:'';}if(!known.value)try{known.value=o.knownAt();}catch{/* No readable scope yet. */}}
 return{async load(){refreshFrames();draw();await Promise.all([loadPolicies(),readLibrary()]);},sync(available:boolean){if(owner!==o.identity()){owner=o.identity();epoch++;current=undefined;pending.clear();restore();known.value='';draw();}run.disabled=o.busy()||!o.canRun()||!available;for(const field of [opening,closing,known,timeMode])field.disabled=o.busy();clock.disabled=o.busy()||timeMode.value!=='one_calendar_day_model_decay_v1';},reset(){epoch++;current=undefined;known.value='';if(owner!==o.identity()){owner=o.identity();pending.clear();restore();}draw();},unconfirmed:()=>!!pending.command};
}
