import {renderDataGrid} from './data-grid.js';
import {renderChart,clearChart} from './chart-view.js';
import {riskContextLabel} from './risk-time.js';
import {curveName,deliveryLabel,publicText,timeLabels} from './market-view.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Factor={seriesId:string;quote:string;curves:string[];deliveryFrom:string;deliveryTo:string;unit:string;value:string};
type FinancialContext={contextId:string;effectiveOn:string;knownAt?:string;knownThrough:number};
type Catalog={factors:Factor[];bumpConvention:string;stressConvention:string;convention:string;numericalPolicy:string;context:FinancialContext;asOf:string};
type Scope={context:unknown;bookIds:string[];portfolioIds:string[]};
type Shift={seriesId:string;quote:string;size:string;convention:string};
type Request=Scope&{bumps:Shift[];stress:Shift[];convention:string;numericalPolicy:string};
type Submission={requestId:string;request:Request};
type Job={id:string;state:'queued'|'running'|'completed'|'failed'|'cancellation_requested'|'cancelled';error?:{code:string;message:string};result?:Report;context?:FinancialContext;asOf?:string};
type Values={base:string;up:string;down:string;upPnl:string;downPnl:string;delta:string;gamma:string;currency:string;currencyReference:unknown;deltaUnit:string;gammaUnit:string};
type Report={context:{contextId:string};snapshotId:string;heldContracts:number;work:{positionCount:number;uniqueValuations:number;scenarioCount:number;preparationScenarioCount:number;unitPricerCalls:number;rebuiltCurveNodes:number;reusedCurveNodes:number;repeatedInstrumentReferences:number;quantityApplications:number};factors:{factor:Factor;bump:string;totals:{bookId:string;book:string;values:Values}[];positions:{book:string;curve:string;deliveryFrom:string;deliveryTo:string;values:Values}[]}[]};

export function setupRisk(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;canRun:()=>boolean;scope:()=>Scope;onDenied:()=>void}){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const select=get<HTMLSelectElement>('risk-factor'),bump=get<HTMLInputElement>('risk-bump'),stressFactor=get<HTMLSelectElement>('risk-stress-factor'),stressSize=get<HTMLInputElement>('risk-stress-size');
  const currentScopeText=get('risk-scope').textContent;
  let recentJobs:Job[]=[];
  let catalog:Catalog|undefined,scope:Scope|undefined,lastRequest:Request|undefined,stress:Shift[]=[];
  let generation=0,pollTimer:ReturnType<typeof setTimeout>|undefined,job:Job|undefined,pending:Submission|undefined,jobRequest:Request|undefined,resultJobId:string|undefined,reviewingJob=false;
  let grids:ReturnType<typeof renderDataGrid>[]=[];
  const running=()=>!!job&&['queued','running','cancellation_requested'].includes(job.state);
  function stopPolling(){clearTimeout(pollTimer);pollTimer=undefined;}
  function results(){for(const grid of grids)grid.destroy();grids=[];get('risk-results').replaceChildren();get('risk-plots').querySelectorAll<HTMLElement>('.chart').forEach(panel=>clearChart(panel));get('risk-plots').replaceChildren();get('risk-work').textContent='';get('risk-export').hidden=true;lastRequest=undefined;resultJobId=undefined;}
  function clearJob(){stopPolling();job=undefined;pending=undefined;jobRequest=undefined;reviewingJob=false;get('risk-job').hidden=true;get('risk-job-return').hidden=true;get<HTMLSelectElement>('risk-job-history').value='';get('risk-scope').textContent=currentScopeText;}
  function reset(){recentJobs=[];generation++;clearJob();results();catalog=undefined;scope=undefined;stress=[];select.replaceChildren(new Option('Choose a factor…',''));stressFactor.replaceChildren(new Option('Choose a factor…',''));get('risk-job-history').replaceChildren(new Option('Choose a retained job…',''));bump.value='';stressSize.value='';get('risk-unit').textContent='';get('risk-stress-unit').textContent='';get<HTMLDetailsElement>('risk-stress').open=false;renderStress();get('risk-card').hidden=true;}
  function sync(){const locked=options.busy()||running()||!!pending||reviewingJob;get<HTMLFieldSetElement>('risk-fields').disabled=locked;get<HTMLButtonElement>('blotter-risk').disabled=options.busy()||!options.canRun();get('blotter-risk').title=options.canRun()?'Revalue selected books under shared market shocks':'Scenario execution requires the Risk Analyst role.';get<HTMLButtonElement>('risk-export').disabled=options.busy()||(!lastRequest&&!resultJobId);get('risk-results').inert=options.busy();get<HTMLButtonElement>('risk-stress-parent').disabled=locked||!stress.length;get<HTMLButtonElement>('risk-stress-clear').disabled=locked||!stress.length;get<HTMLButtonElement>('risk-stress-add').disabled=locked||stress.length>=16;get<HTMLSelectElement>('risk-job-history').disabled=options.busy()||!!pending;for(const id of ['risk-job-refresh','risk-job-retry','risk-job-cancel','risk-job-return'])get<HTMLButtonElement>(id).disabled=options.busy();}
  function denied(error:unknown){if((error as {code?:string}).code==='access_denied'){reset();options.onDenied();}}
  async function call<T>(path:string,body?:unknown):Promise<T>{try{return await options.api<T>(path,body);}catch(error){results();denied(error);throw error;}}
  function factorLabel(f:Factor){return publicText(`${f.curves.map(curveName).join(' / ')} · ${deliveryLabel(f.deliveryFrom,f.deliveryTo)}`);}
  async function open(){
    reset();const version=generation;scope=structuredClone(options.scope());const loaded=await call<Catalog>('futures/risk/catalog',scope);if(version!==generation)return;catalog=loaded;
    scope={...scope,context:{...(scope.context as Record<string,unknown>),knowledgeMode:'retained',contextId:catalog.context.contextId}};
    for(const control of [select,stressFactor])control.replaceChildren(new Option('Choose a factor…',''),...catalog.factors.map(f=>new Option(factorLabel(f),f.seriesId)));
    const recent=await call<Job[]>('futures/risk/jobs');if(version!==generation)return;
    recentJobs=recent;refresh();
    get('risk-card').hidden=false;sync();get('risk-card').scrollIntoView({block:'start',behavior:'smooth'});
  }
  function refresh(){
    const control=get<HTMLSelectElement>('risk-job-history'),selected=control.value,dates=timeLabels(recentJobs.filter(j=>j.asOf).map(j=>j.asOf!),options.zone());let dateIndex=0;
    control.replaceChildren(new Option('Choose a retained job…',''),...recentJobs.map(j=>new Option(`${j.asOf?dates[dateIndex++]:'Unavailable market time'} · ${j.state.replaceAll('_',' ')}`,j.id)));control.value=selected;
    if(job)get('risk-job-context').textContent=riskContextLabel(job,options.zone());
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
      const partitions=new Map<string,typeof item.totals>();
      for(const row of item.totals){const key=JSON.stringify(row.values.currencyReference);const group=partitions.get(key)??[];group.push(row);partitions.set(key,group);}
      for(const [partitionIndex,totals] of [...partitions.values()].entries()){
        const unit=totals[0].values;
        const title=document.createElement('h4');title.textContent=publicText(`${item.factor.curves.map(curveName).join(' / ')} · ${deliveryLabel(item.factor.deliveryFrom,item.factor.deliveryTo)} · absolute bump ±${item.bump} · unit ${item.factor.unit}`);get('risk-results').append(title);
        const host=document.createElement('div');host.className='table-wrap';get('risk-results').append(host);
        const headers=['Book',`Base execution P&L (${unit.currency})`,`Up execution P&L (${unit.currency})`,`Down execution P&L (${unit.currency})`,`Up change (${unit.currency})`,`Down change (${unit.currency})`,`Delta (${unit.deltaUnit})`,`Gamma (${unit.gammaUnit})`];
        const keys=['book','base','up','down','upPnl','downPnl','delta','gamma'];
        grids.push(renderDataGrid(host,`risk-totals-${factorIndex}-${partitionIndex}`,report.snapshotId+report.context.contextId,headers,totals.map(t=>[t.book,t.values.base,t.values.up,t.values.down,t.values.upPnl,t.values.downPnl,t.values.delta,t.values.gamma]),[1,2,3,4,5,6,7],options.busy,{keys,layout:{order:[],widths:{},sort:[]},onLayout:()=>{}}));
        const panel=document.createElement('div');panel.className='chart';get('risk-plots').append(panel);
        renderChart(panel,{title:'Execution P&L change by book',subtitle:`±${item.bump} quoted points · ${item.factor.unit}`,axis:'category',unit:unit.currency,kind:'bar',series:[
          {name:'Up shock',points:totals.map(t=>({x:t.bookId,label:t.book,y:t.values.upPnl}))},
          {name:'Down shock',points:totals.map(t=>({x:t.bookId,label:t.book,y:t.values.downPnl}))},
        ]});
      }
      const details=document.createElement('details'),summary=document.createElement('summary');summary.textContent='Individual holding results';details.append(summary);
      const rows=document.createElement('div');rows.className='table-wrap';details.append(rows);get('risk-results').append(details);
      // Initialize after opening: the virtual grid needs a visible width.
      let ready=false;details.ontoggle=()=>{if(!details.isConnected||!details.open||ready)return;ready=true;const keys=['book','market','contract','currency','base','upPnl','downPnl','delta','deltaUnit','gamma','gammaUnit'];
        grids.push(renderDataGrid(rows,`risk-positions-${factorIndex}`,report.snapshotId,['Book','Market','Contract','Currency','Base execution P&L','Up change','Down change','Delta','Delta unit','Gamma','Gamma unit'],item.positions.map(p=>[p.book,curveName(p.curve),deliveryLabel(p.deliveryFrom,p.deliveryTo),p.values.currency,p.values.base,p.values.upPnl,p.values.downPnl,p.values.delta,p.values.deltaUnit,p.values.gamma,p.values.gammaUnit]),[4,5,6,7,9],options.busy,{keys,layout:{order:[],widths:{},sort:[]},onLayout:()=>{}}));};
    }
    const w=report.work;get('risk-work').textContent=`${w.positionCount} holdings · ${report.heldContracts} held contracts · ${w.uniqueValuations} unit valuations · ${w.scenarioCount} valuation scenarios · ${w.preparationScenarioCount} preparation builds · ${w.unitPricerCalls} pricer calls · ${w.repeatedInstrumentReferences} additional quantity references · ${w.quantityApplications} quantity applications · ${w.rebuiltCurveNodes} curve nodes evaluated · ${w.reusedCurveNodes} reused. Curve work includes all scenario preparation.`;
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
    return structuredClone({...scope,bumps:[{seriesId:factor.seriesId,quote:factor.quote,size:bump.value,convention:catalog.bumpConvention}],stress,convention:catalog.convention,numericalPolicy:catalog.numericalPolicy});
  }
  function schedulePoll(version:number){stopPolling();pollTimer=setTimeout(()=>void refreshJob(version),750);}
  function showJob(value:Job,version:number){
    if(version!==generation)return;
    job=value;get('risk-job').hidden=false;
    const labels:Record<Job['state'],string>={queued:'Queued',running:'Running',completed:'Completed',failed:'Failed',cancellation_requested:'Cancellation requested',cancelled:'Cancelled'};
    get('risk-job-status').textContent=`Risk job: ${labels[value.state]??value.state}. ${value.state==='cancellation_requested'?'Waiting for the worker to stop. ':''}Uses the financial context captured at submission.${value.error?` ${value.error.message}`:''}`;
    get('risk-job-context').textContent=riskContextLabel(value,options.zone());
    get('risk-job-cancel').hidden=!['queued','running'].includes(value.state);get('risk-job-refresh').hidden=false;get('risk-job-retry').hidden=true;
    if(value.state==='completed'&&value.result){render(value.result);lastRequest=jobRequest;resultJobId=value.id;}
    if(running())schedulePoll(version);else stopPolling();sync();
  }
  async function refreshJob(version=generation){
    if(version!==generation||!job)return;
    if(options.busy()){schedulePoll(version);return;}
    try{const value=await options.api<Job>(`futures/risk/jobs/${job.id}`);showJob(value,version);}
    catch(error){if(version!==generation)return;stopPolling();results();denied(error);if(version!==generation)return;get('risk-job-status').textContent='Job status is unavailable. Refresh to check its retained outcome.';get('risk-job-refresh').hidden=false;sync();}
  }
  async function submitJob(){
    if(!pending)throw Error('There is no retained submission to reconcile.');
    const version=generation;
    try{const value=await options.api<Job>('futures/risk/jobs',pending);if(version!==generation)return;jobRequest=pending.request;pending=undefined;showJob(value,version);}
    catch(error){if(version!==generation)return;denied(error);if(version!==generation)throw error;
      if(!['response_unknown','risk_job_outcome_unknown'].includes((error as {code?:string}).code??'')){pending=undefined;get('risk-job').hidden=true;}
      else{get('risk-job').hidden=false;get('risk-job-status').textContent='Submission outcome is unknown. Reconcile to retry the same request safely.';get('risk-job-retry').hidden=false;get('risk-job-cancel').hidden=true;get('risk-job-refresh').hidden=true;}
      sync();throw error;
    }
  }
  get<HTMLFormElement>('risk-form').onsubmit=event=>{event.preventDefault();const queued=(event as SubmitEvent).submitter?.id==='risk-queue';void options.act(async()=>{
    const payload=request();results();clearJob();const version=++generation;
    if(queued){pending={requestId:crypto.randomUUID(),request:payload};await submitJob();}
    else{const report=await call<Report>('futures/risk',payload);if(version!==generation)return;render(report);lastRequest=payload;sync();}
  });};
  get('risk-job-retry').onclick=()=>void options.act(submitJob);
  get('risk-job-refresh').onclick=()=>void options.act(async()=>{
    if(!job)return;stopPolling();const version=++generation;showJob(await call<Job>(`futures/risk/jobs/${job.id}`),version);
  });
  get('risk-job-cancel').onclick=()=>void options.act(async()=>{
    if(!job)return;stopPolling();const version=++generation;showJob(await call<Job>(`futures/risk/jobs/${job.id}/cancel`,{}),version);
  });
  get('risk-job-history').onchange=()=>void options.act(async()=>{
    const id=get<HTMLSelectElement>('risk-job-history').value;if(!id)return;
    results();clearJob();const version=++generation;reviewingJob=true;get('risk-job-return').hidden=false;get<HTMLSelectElement>('risk-job-history').value=id;
    get('risk-scope').textContent='Retained futures execution P&L and open-price sensitivity. This job uses its captured financial context below.';
    get('risk-scenario').textContent='Viewing a retained job under its captured financial context below. Return to the selected context to calculate another scenario.';
    showJob(await call<Job>(`futures/risk/jobs/${id}`),version);
  });
  get('risk-job-return').onclick=()=>{edit();renderStress();};
  get('risk-export').onclick=()=>void options.act(async()=>{
    if(!lastRequest&&!resultJobId)throw Error('Run the selected shock before exporting.');
    const report=resultJobId?(await call<Job>(`futures/risk/jobs/${resultJobId}`)).result:await call<Report>('futures/risk',lastRequest);
    if(!report)throw Error('The completed job result is unavailable. Refresh its retained outcome.');
    const blob=new Blob([JSON.stringify(report,null,2)+'\n'],{type:'application/json'}),url=URL.createObjectURL(blob),link=document.createElement('a');link.href=url;link.download='leanetrm-risk.json';link.click();URL.revokeObjectURL(url);
  });
  get('risk-close').onclick=reset;
  return {open,reset,sync,refresh};
}
