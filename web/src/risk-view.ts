import {riskTotalGroups,type RiskValues,type RiskTotal} from './risk-report.js';
import {PendingCommand} from './pending-command.js';
import {renderDataGrid} from './data-grid.js';
import {renderChart,clearChart} from './chart-view.js';
import {curveName,deliveryLabel,publicText} from './market-view.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Factor={seriesId:string;quote:string;curves:string[];deliveryFrom:string;deliveryTo:string;unit:string;value:string};
type FinancialContext={contextId:string;effectiveOn:string;knownAt?:string;knownThrough:number};
type Catalog={pricingPolicy:unknown;measures:{measure:string;measureLabel:string}[];factors:Factor[];bumpConvention:string;stressConvention:string;convention:string;numericalPolicy:string;context:FinancialContext;asOf:string};
type Scope={context:unknown;bookIds:string[];portfolioIds:string[]};
type Shift={seriesId:string;quote:string;size:string;convention:string};
type Request=Scope&{pricingPolicy:unknown;bumps:Shift[];stress:Shift[];convention:string;numericalPolicy:string};
type PendingJob={recovery:PendingCommand;context:FinancialContext;asOf:string};
type Job={id:string;requestId?:string;state:'queued'|'running'|'completed'|'failed'|'cancellation_requested'|'cancelled';error?:{code:string;message:string};result?:Report;context?:FinancialContext;asOf?:string};
type Position={book:string;tradeReference:string;instrumentReference:string;instrumentType:string;measure:string;measureLabel:string;values:RiskValues};
type Report={displayPolicy:unknown;scenario:{depth:number;isBase:boolean};context:{contextId:string};snapshotId:string;heldInstruments:number;work:{positionCount:number;uniqueValuations:number;scenarioCount:number;preparationScenarioCount:number;unitPricerCalls:number;rebuiltCurveNodes:number;reusedCurveNodes:number;repeatedInstrumentReferences:number;quantityApplications:number};factors:{factor:Factor;bump:string;totals:RiskTotal[];positions:Position[]}[]};

export function setupRisk(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;canRun:()=>boolean;profile:()=>string;scope:()=>Scope;onDenied:()=>void}){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const select=get<HTMLSelectElement>('risk-factor'),bump=get<HTMLInputElement>('risk-bump'),stressFactor=get<HTMLSelectElement>('risk-stress-factor'),stressSize=get<HTMLInputElement>('risk-stress-size');
  const currentScopeText=get('risk-scope').textContent;
  let catalog:Catalog|undefined,scope:Scope|undefined,lastRequest:Request|undefined,stress:Shift[]=[];
  const submissions=new Map<string,PendingJob>();
  const knownRejections=new Set(['request_format','request_too_large','request_token','access_denied','risk_context','risk_job_context','risk_job_conflict','risk_factor','risk_bump','risk_stress','risk_position','risk_convention','risk_policy','risk_detail','numeric_format','numeric_range','futures_context','portfolio_context','portfolio_policy','swap_risk_model','blotter_view']);
  let generation=0,pollTimer:ReturnType<typeof setTimeout>|undefined,job:Job|undefined,pending:PendingJob|undefined,jobRequest:Request|undefined,resultJobId:string|undefined,reviewingJob=false;
  let grids:ReturnType<typeof renderDataGrid>[]=[];
  const running=()=>!!job&&['queued','running','cancellation_requested'].includes(job.state);
  function stopPolling(){clearTimeout(pollTimer);pollTimer=undefined;}
  function results(){for(const grid of grids)grid.destroy();grids=[];get('risk-results').replaceChildren();get('risk-plots').querySelectorAll<HTMLElement>('.chart').forEach(panel=>clearChart(panel));get('risk-plots').replaceChildren();get('risk-work').textContent='';get('risk-export').hidden=true;lastRequest=undefined;resultJobId=undefined;}
  function clearJob(){stopPolling();job=undefined;pending=undefined;jobRequest=undefined;reviewingJob=false;get('risk-job').hidden=true;for(const id of ['risk-job-context','risk-job-scenario','risk-job-status'])get(id).textContent='';get('risk-job-return').hidden=true;get<HTMLSelectElement>('risk-job-history').value='';get('risk-scope').textContent=currentScopeText;}
  function reset(){generation++;clearJob();results();catalog=undefined;scope=undefined;stress=[];select.replaceChildren(new Option('Choose a factor…',''));stressFactor.replaceChildren(new Option('Choose a factor…',''));get('risk-job-history').replaceChildren(new Option('Choose a retained job…',''));bump.value='';stressSize.value='';get('risk-unit').textContent='';get('risk-stress-unit').textContent='';get<HTMLDetailsElement>('risk-stress').open=false;renderStress();get('risk-card').hidden=true;}
  function sync(){const locked=options.busy()||running()||!!pending||reviewingJob;get('risk-form').hidden=reviewingJob||!!pending;get<HTMLFieldSetElement>('risk-fields').disabled=locked;get<HTMLButtonElement>('blotter-risk').disabled=options.busy()||!options.canRun();get('blotter-risk').title=options.canRun()?'Revalue selected books under shared market shocks':'Scenario execution requires the Risk Analyst role.';get<HTMLButtonElement>('risk-export').disabled=options.busy()||(!lastRequest&&!resultJobId);get('risk-results').inert=options.busy();get<HTMLButtonElement>('risk-stress-parent').disabled=locked||!stress.length;get<HTMLButtonElement>('risk-stress-clear').disabled=locked||!stress.length;get<HTMLButtonElement>('risk-stress-add').disabled=locked||stress.length>=16;get<HTMLSelectElement>('risk-job-history').disabled=options.busy();for(const id of ['risk-job-refresh','risk-job-retry','risk-job-cancel','risk-job-return','risk-job-discard'])get<HTMLButtonElement>(id).disabled=options.busy();}
  function denied(error:unknown){if((error as {code?:string}).code==='access_denied'){reset();options.onDenied();}}
  async function call<T>(path:string,body?:unknown):Promise<T>{try{return await options.api<T>(path,body);}catch(error){results();denied(error);throw error;}}
  function factorLabel(f:Factor){return publicText(`${f.curves.map(curveName).join(' / ')} · ${deliveryLabel(f.deliveryFrom,f.deliveryTo)}`);}
  async function recentJobs(version:number){
    const recent=await call<Job[]>('portfolio/risk/jobs');if(version!==generation)return false;
    get('risk-job-history').replaceChildren(new Option('Choose a retained job…',''),...recent.map(j=>new Option(`${j.asOf??'Unavailable market time'} · ${j.state.replaceAll('_',' ')}`,j.id)));
    const retained=pending,command=retained?.recovery.command,owner=options.profile();
    const match=command&&recent.find(j=>j.requestId===command.operationId);
    if(!retained||!command||!match)return false;
    const receipt=await call<Job>(`portfolio/risk/jobs/${match.id}`);if(version!==generation)return false;
    if(receipt.id!==match.id||receipt.requestId!==command.operationId)throw Error('The retained job receipt does not match the unconfirmed request.');
    if(submissions.get(owner)===retained)submissions.delete(owner);
    jobRequest=command.body.request as Request;pending=undefined;reviewingJob=true;showJob(receipt,version);return true;
  }
  async function open(){
    reset();const version=generation;pending=submissions.get(options.profile());
    if(pending){reviewingJob=true;showPending();get('risk-card').hidden=false;sync();await recentJobs(version);return;}
    scope=structuredClone(options.scope());const loaded=await call<Catalog>('portfolio/risk/catalog',scope);if(version!==generation)return;catalog=loaded;
    scope={...scope,context:{...(scope.context as Record<string,unknown>),knowledgeMode:'retained',contextId:catalog.context.contextId}};
    for(const control of [select,stressFactor])control.replaceChildren(new Option('Choose a factor…',''),...catalog.factors.map(f=>new Option(factorLabel(f),f.seriesId)));
    await recentJobs(version);if(version!==generation)return;
    get('risk-card').hidden=false;sync();get('risk-card').scrollIntoView({block:'start',behavior:'smooth'});
  }
  function renderStress(){
    get('risk-stress-stack').replaceChildren(...stress.map((shift,index)=>{const item=document.createElement('li'),factor=catalog?.factors.find(f=>f.seriesId===shift.seriesId);item.textContent=`Level ${index+1} · ${factor?factorLabel(factor):''} · add ${shift.size} ${factor?.unit??''} to parent`;return item;}));
    get('risk-scenario').textContent=stress.length?`Risk around ${stress.length} nested ${stress.length===1?'stress':'stresses'}. Base, up and down values all include this stress stack.`:'Risk around the selected unchanged market.';
    sync();
  }
  function edit(){generation++;clearJob();results();sync();}
  function render(report:Report){
    results();
    for(const [factorIndex,item] of report.factors.entries()){
      for(const [partitionIndex,totals] of riskTotalGroups(item.totals).entries()){
        const first=totals[0],unit=first.values;
        const title=document.createElement('h4');title.textContent=publicText(`${first.measureLabel} · ${item.factor.curves.map(curveName).join(' / ')} · ${deliveryLabel(item.factor.deliveryFrom,item.factor.deliveryTo)} · absolute bump ±${item.bump} · unit ${item.factor.unit}`);get('risk-results').append(title);
        const host=document.createElement('div');host.className='table-wrap';get('risk-results').append(host);
        const headers=['Book',`Base ${first.measureLabel} (${unit.currency})`,`Up ${first.measureLabel} (${unit.currency})`,`Down ${first.measureLabel} (${unit.currency})`,`Up change (${unit.currency})`,`Down change (${unit.currency})`,`Delta (${unit.deltaUnit})`,`Gamma (${unit.gammaUnit})`];
        const keys=['book','base','up','down','upPnl','downPnl','delta','gamma'];
        const amounts=(value:RiskValues['display'])=>[value.base,value.up,value.down,value.upPnl,value.downPnl,value.delta,value.gamma];
        grids.push(renderDataGrid(host,`risk-totals-${factorIndex}-${partitionIndex}`,report.snapshotId+report.context.contextId,headers,totals.map(t=>[t.book,...amounts(t.values.display)]),[1,2,3,4,5,6,7],options.busy,{keys,cellTitles:totals.map(t=>[t.book,...amounts(t.values)]),layout:{order:[],widths:{},sort:[]},onLayout:()=>{}}));
        const panel=document.createElement('div');panel.className='chart';get('risk-plots').append(panel);
        renderChart(panel,{title:`${first.measureLabel} change by book`,subtitle:`±${item.bump} quoted points · ${item.factor.unit} · displayed amounts`,axis:'category',unit:unit.currency,kind:'bar',series:[
          {name:'Up shock',points:totals.map(t=>({x:t.bookId,label:t.book,y:t.values.display.upPnl}))},
          {name:'Down shock',points:totals.map(t=>({x:t.bookId,label:t.book,y:t.values.display.downPnl}))},
        ]});
      }
      const details=document.createElement('details'),summary=document.createElement('summary');summary.textContent='Individual holding results';details.append(summary);
      const rows=document.createElement('div');rows.className='table-wrap';details.append(rows);get('risk-results').append(details);
      // Initialize after opening: the virtual grid needs a visible width.
      let ready=false;details.ontoggle=()=>{if(!details.isConnected||!details.open||ready)return;ready=true;const keys=['book','trade','instrument','instrumentType','measure','currency','base','upPnl','downPnl','delta','deltaUnit','gamma','gammaUnit'];
        const cells=(p:Position,display:boolean)=>{const v=display?p.values.display:p.values;return [p.book,p.tradeReference,p.instrumentReference,p.instrumentType,p.measureLabel,p.values.currency,v.base,v.upPnl,v.downPnl,v.delta,p.values.deltaUnit,v.gamma,p.values.gammaUnit];};
        grids.push(renderDataGrid(rows,`risk-positions-${factorIndex}`,report.snapshotId,['Book','Trade','Instrument','Instrument type','Measure','Currency','Base value','Up change','Down change','Delta','Delta unit','Gamma','Gamma unit'],item.positions.map(p=>cells(p,true)),[6,7,8,9,11],options.busy,{keys,cellTitles:item.positions.map(p=>cells(p,false)),layout:{order:[],widths:{},sort:[]},onLayout:()=>{}}));};

    }
    const w=report.work;get('risk-work').textContent=`${w.positionCount} holdings · ${report.heldInstruments} held instruments · ${w.uniqueValuations} unit valuations · ${w.scenarioCount} valuation scenarios · ${w.preparationScenarioCount} preparation builds · ${w.unitPricerCalls} pricer calls · ${w.repeatedInstrumentReferences} additional quantity references · ${w.quantityApplications} quantity applications · ${w.rebuiltCurveNodes} curve nodes evaluated · ${w.reusedCurveNodes} reused. Curve work includes all scenario preparation. Tables and charts use backend display precision; sorting compares displayed amounts. Hover over a value or download for exact amounts.`;
    get('risk-export').hidden=false;
  }
  select.onchange=()=>{edit();const factor=catalog?.factors.find(f=>f.seriesId===select.value);get('risk-unit').textContent=factor?`Observed input ${factor.value} · bump unit: ${factor.unit}. Absolute symmetric up/down around the selected scenario.`:'';};
  bump.oninput=edit;
  stressFactor.onchange=()=>{const factor=catalog?.factors.find(f=>f.seriesId===stressFactor.value);get('risk-stress-unit').textContent=factor?`Observed input ${factor.value} · shift unit: ${factor.unit}. Add to the parent scenario's quote.`:'';};
  get('risk-stress-add').onclick=()=>void options.act(async()=>{
    if(!catalog||!stressFactor.value||!stressSize.value)throw Error('Choose a dated quote and enter a signed absolute shift.');
    const factor=catalog.factors.find(f=>f.seriesId===stressFactor.value);if(!factor)throw Error('Choose an available dated quote.');
    if(stress.length>=16)throw Error('A scenario supports at most sixteen nested stresses.');
    edit();stress=[...stress,{seriesId:factor.seriesId,quote:factor.quote,size:stressSize.value,convention:catalog.stressConvention}];stressSize.value='';renderStress();
  });
  get('risk-stress-parent').onclick=()=>{edit();stress=stress.slice(0,-1);renderStress();};
  get('risk-stress-clear').onclick=()=>{edit();stress=[];renderStress();};
  function request():Request{
    if(!catalog||!scope)throw Error('Open shocks from the current blotter first.');
    const factor=catalog.factors.find(f=>f.seriesId===select.value);if(!factor)throw Error('Choose an exact dated quote input.');
    return structuredClone({...scope,bumps:[{seriesId:factor.seriesId,quote:factor.quote,size:bump.value,convention:catalog.bumpConvention}],stress,pricingPolicy:catalog.pricingPolicy,convention:catalog.convention,numericalPolicy:catalog.numericalPolicy});
  }
  function contextLabel(context:FinancialContext|undefined,asOf:string|undefined){
    return `Captured market time: ${asOf??'unavailable'} · effective date: ${context?.effectiveOn??'unavailable'} · known as of: ${context?.knownAt??'unavailable'}. These retained coordinates govern this job.`;
  }
  function showPending(){
    if(!pending)return;
    get('risk-job').hidden=false;get('risk-job-context').textContent=contextLabel(pending.context,pending.asOf);get('risk-job-scenario').textContent='';
    get('risk-scope').textContent='An unconfirmed risk submission uses its original captured financial context below.';
    get('risk-job-status').textContent='Submission outcome is unknown. Reconcile to retry the same request safely. Inspect recent jobs before discarding this request; it may already have committed.';
    get('risk-job-retry').hidden=false;get('risk-job-discard').hidden=false;get('risk-job-cancel').hidden=true;get('risk-job-refresh').hidden=true;sync();
  }
  function schedulePoll(version:number){stopPolling();pollTimer=setTimeout(()=>void refreshJob(version),750);}
  function showJob(value:Job,version:number){
    if(version!==generation)return;
    job=value;get('risk-job').hidden=false;get('risk-job-discard').hidden=true;get('risk-job-return').hidden=!reviewingJob;get('risk-scope').textContent=reviewingJob?'Retained portfolio risk, separated by valuation measure and currency. This job uses its captured financial context below.':currentScopeText;
    const labels:Record<Job['state'],string>={queued:'Queued',running:'Running',completed:'Completed',failed:'Failed',cancellation_requested:'Cancellation requested',cancelled:'Cancelled'};
    get('risk-job-status').textContent=`Risk job: ${labels[value.state]??value.state}. ${value.state==='cancellation_requested'?'Waiting for the worker to stop. ':''}Uses the financial context captured at submission.${value.error?` ${value.error.message}`:''}`;
    get('risk-job-context').textContent=contextLabel(value.context,value.asOf);
    get('risk-job-scenario').textContent=value.result?(value.result.scenario.isBase?'Retained scenario: original market.':`Retained scenario: ${value.result.scenario.depth} nested quote stresses. Exact stress inputs are included in the download.`):'';
    get('risk-job-cancel').hidden=!['queued','running'].includes(value.state);get('risk-job-refresh').hidden=false;get('risk-job-retry').hidden=true;
    if(value.state==='completed'&&value.result){render(value.result);lastRequest=jobRequest;resultJobId=value.id;}
    if(running())schedulePoll(version);else stopPolling();sync();
  }
  async function refreshJob(version=generation){
    if(version!==generation||!job)return;
    if(options.busy()){schedulePoll(version);return;}
    try{const value=await options.api<Job>(`portfolio/risk/jobs/${job.id}`);showJob(value,version);}
    catch(error){if(version!==generation)return;stopPolling();results();denied(error);if(version!==generation)return;get('risk-job-status').textContent='Job status is unavailable. Refresh to check its retained outcome.';get('risk-job-refresh').hidden=false;sync();}
  }
  async function submitJob(){
    const retained=pending,owner=options.profile(),command=retained?.recovery.command;
    if(!retained||!command)throw Error('There is no retained submission to reconcile.');
    const version=generation,previouslyUncertain=retained.recovery.sending();
    const submission={requestId:command.operationId,request:command.body.request as Request};
    try{
      const value=await options.api<Job>('portfolio/risk/jobs',submission);
      if(submissions.get(owner)===retained)submissions.delete(owner);
      if(version!==generation)return;jobRequest=submission.request;pending=undefined;showJob(value,version);
    }
    catch(error){
      retained.recovery.failed((error as {code?:string}).code,previouslyUncertain);
      if(!retained.recovery.command&&submissions.get(owner)===retained)submissions.delete(owner);
      if(version!==generation)return;denied(error);if(version!==generation)throw error;
      pending=retained.recovery.command?retained:undefined;
      if(pending){showPending();if((error as {code?:string}).code==='risk_job_conflict'&&await recentJobs(version))return;}else{clearJob();sync();}
      throw error;
    }
  }
  get<HTMLFormElement>('risk-form').onsubmit=event=>{event.preventDefault();const queued=(event as SubmitEvent).submitter?.id==='risk-queue';void options.act(async()=>{
    const payload=request();results();clearJob();const version=++generation;
    if(queued){const recovery=new PendingCommand(code=>!!code&&knownRejections.has(code));recovery.begin('portfolio/risk/jobs',{request:payload});pending={recovery,context:catalog!.context,asOf:catalog!.asOf};submissions.set(options.profile(),pending);await submitJob();}
    else{const report=await call<Report>('portfolio/risk',payload);if(version!==generation)return;render(report);lastRequest=payload;sync();}
  });};
  get('risk-job-retry').onclick=()=>void options.act(submitJob);
  get('risk-job-discard').onclick=()=>void options.act(async()=>{submissions.delete(options.profile());await open();});
  get('risk-job-refresh').onclick=()=>void options.act(async()=>{
    if(!job)return;stopPolling();const version=++generation;showJob(await call<Job>(`portfolio/risk/jobs/${job.id}`),version);
  });
  get('risk-job-cancel').onclick=()=>void options.act(async()=>{
    if(!job)return;stopPolling();const version=++generation;showJob(await call<Job>(`portfolio/risk/jobs/${job.id}/cancel`,{}),version);
  });
  get('risk-job-history').onchange=()=>void options.act(async()=>{
    const id=get<HTMLSelectElement>('risk-job-history').value;if(!id)return;
    results();clearJob();const version=++generation;reviewingJob=true;get('risk-job-return').hidden=false;get<HTMLSelectElement>('risk-job-history').value=id;
    get('risk-scope').textContent='Retained portfolio risk, separated by valuation measure and currency. This job uses its captured financial context below.';
    get('risk-scenario').textContent='Viewing a retained job under its captured financial context below. Return to the selected context to calculate another scenario.';
    showJob(await call<Job>(`portfolio/risk/jobs/${id}`),version);
  });
  get('risk-job-return').onclick=()=>{if(!catalog||submissions.has(options.profile()))void options.act(open);else{edit();renderStress();}};
  get('risk-export').onclick=()=>void options.act(async()=>{
    const version=generation;
    if(!lastRequest&&!resultJobId)throw Error('Run the selected shock before exporting.');
    const report=resultJobId?(await call<Job>(`portfolio/risk/jobs/${resultJobId}`)).result:await call<Report>('portfolio/risk',lastRequest);
    if(version!==generation)return;
    if(!report)throw Error('The completed job result is unavailable. Refresh its retained outcome.');
    const blob=new Blob([JSON.stringify(report,null,2)+'\n'],{type:'application/json'}),url=URL.createObjectURL(blob),link=document.createElement('a');link.href=url;link.download='leanetrm-risk.json';link.click();URL.revokeObjectURL(url);
  });
  get('risk-close').onclick=reset;
  return {open,reset,sync};
}
