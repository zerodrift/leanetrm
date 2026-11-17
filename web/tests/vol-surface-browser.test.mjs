import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const page='<link rel="stylesheet" href="/app.css"><section id="vol-surfaces-page"></section>';
const fixture=`
 const {setupVolSurfaces}=await import('/vol-surface-view.js');
 const model='smile_quadratic_log_moneyness_v1',calls=[],errors=[];
 const clock={code:'UTC',kind:'fixed_offset',fixedOffsetSeconds:0,ianaZone:null,tzdbVersion:null};
 let legacy=true,job=null,lose=false,hold=null,release,readReplies=[],historical=false,failLibrary=false;
 let context={snapshotId:'s',convention:'futures_option_premium_v1',knowledgeMode:'current'};
 const axes=['server_first','server_second'].map(id=>({id,label:id,unit:'server unit',preset:{name:'Server preset',points:[{id:'zero',value:'0',label:'Server zero'}]}}));
 addEventListener('unhandledrejection',e=>errors.push(String(e.reason)));addEventListener('error',e=>errors.push(String(e.error)));
 const sensitivities={offered:{policy:'option_sensitivities_layers_v1'},clocks:[clock],clockRequired:true};
 const api=async(path,body)=>{
  calls.push({path,body});
  if(path.startsWith('reference/catalog'))return {definitionRevisionId:'d',rows:[{code:'p',name:'Synthetic product'}],hasMore:false};
  if(path==='vol-surfaces/library'){
   if(failLibrary)throw Error('Synthetic library unavailable');
   return {definitionRevisionId:'d',snapshotId:'s',model,product:body.product??null,offset:0,pageSize:48,surfaces:[],hasMore:false,canFit:true};
  }
  if(path==='vol-surfaces/axes')return {definitionRevisionId:'d',snapshotId:'s',model,initialAxis:axes[0].id,axes,requiresRefit:legacy,canFit:true,job,sensitivities};
  if(path==='vol-surfaces/read')return {definitionRevisionId:'d',snapshotId:'s',model,state:'notFitted',revision:null,surface:null,canFit:true,job,sensitivities,...await readReplies.shift()};
  if(path==='vol-surfaces/fit'){
   if(hold)await hold;job={id:'job',state:'queued',errorCode:null};
   if(lose){lose=false;throw new TypeError('Failed to fetch');}
   return {jobId:'job',state:'queued'};
  }
  throw Error('Unexpected '+path);
 };
 const view=setupVolSurfaces({api,context:()=>context,historical:()=>historical,zone:()=>'UTC',
  frames:{here:()=>({at:'2026-10-16T16:00:00Z',close:null,closeId:null}),sessionName:s=>s,offered:()=>null,open:()=>{}},chain:()=>{}});
 const $=s=>document.querySelector(s),tick=()=>new Promise(r=>setTimeout(r,0));
 const selectClock=()=>{const c=$('#vol-fit-clock');c.value='UTC';c.dispatchEvent(new Event('change'));};
 const completed={id:'job',state:'completed',errorCode:null};
 const fitted={state:'fitted',job:completed,revision:{id:'fit',knownFrom:'2026-11-17T12:00:00Z',inputsRevised:false},
  surface:{modelId:model,strikeAxis:[],expiries:[],series:[],sensitivities:null,projection:{
   numericalPolicy:'synthetic',normalImplementation:'synthetic',tolerance:'0.01',axes:[...axes,{...axes[0],id:'strike_v1',label:'Strike'}],
   x:{...axes[0],ticks:axes[0].preset.points,reversed:false},depth:{label:'Time',unit:'years',ticks:[]},height:{label:'Volatility',unit:'%',ticks:[]}}}};
 const until=async predicate=>{for(let tries=0;tries<160&&!predicate();tries++)await new Promise(r=>setTimeout(r,25));};
`;
const browser=(name,body,expected)=>test(name,{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 const result=await evaluate(`(async()=>{${fixture}${body}})()`);
 assert.deepEqual(result,expected,await evaluate("document.querySelector('#vol-surface-error')?.textContent"));
}));

browser('legacy surfaces expose a working clock and an explicit refit; lost acknowledgement retries the same command',`
 await view.open('p');
 const initial=[$('#vol-surfaces-page').dataset.state,$('#vol-fit-retry').hidden,!$('#vol-fit').hidden];
 selectClock();lose=true;$('#vol-fit').click();await tick();await tick();
 const lost=[!$('#vol-fit-retry').hidden,$('#vol-fit').hidden,calls.find(c=>c.path==='vol-surfaces/fit').body.sensitivities.clock.code];
 $('#vol-fit-retry').click();await tick();await tick();
 const fits=calls.filter(c=>c.path==='vol-surfaces/fit'),same=JSON.stringify(fits[0].body)===JSON.stringify(fits[1].body);
 const polling=$('#vol-fit-status').textContent;
 view.leave();return {initial,lost,same,polling,errors};
`,{initial:['requiresRefit',true,true],lost:[true,true,'UTC'],same:true,polling:'Calibrating retained axis inputs…',errors:[]});

browser('an axis edit while fit admission waits preserves the fit status read in the same product and context',`
 legacy=false;await view.open('p');selectClock();
 hold=new Promise(r=>release=r);$('#vol-fit-here').click();await tick();
 const axis=$('#vol-axis');axis.value='server_second';axis.dispatchEvent(new Event('change'));await tick();await tick();
 const before=calls.filter(c=>c.path==='vol-surfaces/read').length;
 release();await tick();await tick();await tick();
 const result={refreshed:calls.filter(c=>c.path==='vol-surfaces/read').length>before,axis:calls.filter(c=>c.path==='vol-surfaces/read').at(-1).body.axis,
  active:/queued/.test($('#vol-fit-status').textContent),errors};view.leave();return result;
`,{refreshed:true,axis:'server_second',active:true,errors:[]});

browser('a fit completing after the surface cutoff gets one fresh read before its final result is shown',`
 legacy=false;readReplies=[{job:{...completed,state:'running'}},{job:completed},fitted];
 await view.open('p');await until(()=>$('#vol-surfaces-page').dataset.state==='fitted');
 const result={state:$('#vol-surfaces-page').dataset.state,reads:calls.filter(c=>c.path==='vol-surfaces/read').length,errors};
 await new Promise(r=>setTimeout(r,1800));
 result.readsAfter=calls.filter(c=>c.path==='vol-surfaces/read').length;view.leave();return result;
`,{state:'fitted',reads:3,readsAfter:3,errors:[]});

browser('a completed job with no surface after the fresh read stays explicitly not fitted and stops reading',`
 legacy=false;readReplies=[{job:{...completed,state:'running'}},{job:completed},{job:completed}];
 await view.open('p');await until(()=>calls.filter(c=>c.path==='vol-surfaces/read').length===3);
 await new Promise(r=>setTimeout(r,1800));
 const result={state:$('#vol-surfaces-page').dataset.state,reads:calls.filter(c=>c.path==='vol-surfaces/read').length,errors};view.leave();return result;
`,{state:'notFitted',reads:3,errors:[]});

browser('completion while hidden waits for visibility, then reconciles once',`
 legacy=false;let releaseRead;
 readReplies=[{job:{...completed,state:'running'}},new Promise(r=>releaseRead=r),fitted];
 await view.open('p');await until(()=>calls.filter(c=>c.path==='vol-surfaces/read').length===2);
 Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));
 releaseRead({job:completed});await tick();await tick();
 const whileHidden=calls.filter(c=>c.path==='vol-surfaces/read').length;
 delete document.hidden;document.dispatchEvent(new Event('visibilitychange'));
 await until(()=>$('#vol-surfaces-page').dataset.state==='fitted');
 const result={whileHidden,state:$('#vol-surfaces-page').dataset.state,reads:calls.filter(c=>c.path==='vol-surfaces/read').length,errors};view.leave();return result;
`,{whileHidden:2,state:'fitted',reads:3,errors:[]});

browser('a historical surface keeps its retained cutoff when the operational job is completed',`
 legacy=false;historical=true;context={...context,knowledgeMode:'retained',contextId:'historical'};
 readReplies=[{job:completed},fitted];await view.open('p');
 await new Promise(r=>setTimeout(r,1800));
 const reads=calls.filter(c=>c.path==='vol-surfaces/read');
 const result={state:$('#vol-surfaces-page').dataset.state,reads:reads.length,context:reads[0].body.context,errors};view.leave();return result;
`,{state:'notFitted',reads:1,context:{snapshotId:'s',convention:'futures_option_premium_v1',knowledgeMode:'retained',contextId:'historical'},errors:[]});

browser('leaving during the final completion read discards its answer',`
 legacy=false;let releaseRead;readReplies=[{job:completed},new Promise(r=>releaseRead=r)];
 const opening=view.open('p');await until(()=>calls.filter(c=>c.path==='vol-surfaces/read').length===2);
 view.leave();releaseRead(fitted);await opening;await tick();
 return {restored:$('#vol-surfaces-page').dataset.state==='fitted',controlsHidden:$('#vol-axis-controls').hidden,reads:calls.filter(c=>c.path==='vol-surfaces/read').length,errors};
`,{restored:false,controlsHidden:true,reads:2,errors:[]});

browser('a completion library failure remains visible after the final surface read',`
 legacy=false;let releaseRead;
 readReplies=[{job:{...completed,state:'running'}},{job:completed},new Promise(r=>releaseRead=r)];
 await view.open('p');failLibrary=true;
 await until(()=>calls.filter(c=>c.path==='vol-surfaces/read').length===3);await tick();await tick();
 releaseRead(fitted);await tick();await tick();
 const result={state:$('#vol-surfaces-page').dataset.state,error:$('#vol-surface-error').textContent,errors};view.leave();return result;
`,{state:'error',error:'Synthetic library unavailable',errors:[]});

for(const action of ['leave','reset'])browser(`a fit answer after ${action} does not reread a discarded view`,`
 legacy=false;await view.open('p');selectClock();
 hold=new Promise(r=>release=r);$('#vol-fit-here').click();await tick();
 view.${action}();const before=calls.filter(c=>c.path==='vol-surfaces/read').length;
 release();await tick();await tick();await tick();
 return {extraReads:calls.filter(c=>c.path==='vol-surfaces/read').length-before,controlsHidden:$('#vol-axis-controls').hidden,errors};
`,{extraReads:0,controlsHidden:true,errors:[]});

test('a covered singleton is visible without any market stalk', {skip:!browserAvailable,timeout:browserTestTimeout},()=>
 withBrowserPage('<link rel="stylesheet" href="/app.css"><canvas style="width:800px"></canvas>',async evaluate=>{
  assert.equal(await evaluate(`(async()=>{
   const {drawSurface3d}=await import('/vol-surface-3d.js');
   const canvas=document.querySelector('canvas'),ctx=canvas.getContext('2d');let dots=0;
   const arc=ctx.arc.bind(ctx);ctx.arc=(...args)=>{dots++;arc(...args);};
   const tick=(value,label)=>({id:value,value,label});
   const surface={expiries:[{expiryAt:'2027-01-01',status:'fitted',yearFraction:'0.25',timeLabel:'0.25',nodes:[
    {pointId:'0',axisValue:'0',label:'ATM',logMoneyness:'0',strike:'100',fitted:'0.4',covered:true,reason:null}]}],series:[],
    projection:{x:{id:'log_moneyness_v1',label:'Log-moneyness',unit:'ln(K/F)',reversed:false,ticks:[tick('0','ATM')]},
     depth:{label:'Time to expiry',unit:'years'},height:{label:'Volatility',unit:'%',ticks:[tick('0.3','30'),tick('0.5','50')]}}};
   const drawing=drawSurface3d(canvas,surface,{current:()=>null,subscribe:()=>()=>{},set:()=>{}});
   drawing.dispose();return dots>0;
  })()`),true);
 }));
