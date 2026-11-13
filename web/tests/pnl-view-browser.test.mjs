import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
import {themeAudit} from './theme-audit.mjs';
const browser={skip:!browserAvailable,timeout:browserTestTimeout};
const setup=`(async()=>{window.calls=[];window.actor='risk';window.failSave=false;window.errors=[];const {setupPnlView}=await import('/pnl-view.js');window.setupPnlView=setupPnlView;
 const currency={code:'USD',revision:{catalogId:'catalog',revisionId:'ref'}};const money=n=>({numerator:String(n),denominator:'1',display:String(n),currency});
 const values={openingValue:money(100),closingValue:money(108),valueMovement:money(8),cash:money(2),priorCash:money(0),transfers:money(0),economicPnl:money(10),explained:money(6),residual:money(2),bridge:[{label:'Delta',amount:money(6),start:money(0),end:money(6)}],greekCoverage:'selected_price_factors',unavailableEstimates:0};
 const report={id:'report',version:'trading_pnl_report_v1',scope:{books:[{id:'book',name:'Gas options'}],tradeSelection:'Selected trades'},opening:{name:'Opening',at:'2026-01-01T21:00:00Z',context:{knownAt:'2026-11-01T00:00:00Z'}},closing:{name:'Closing',at:'2026-01-02T21:00:00Z',context:{knownAt:'2026-11-01T00:00:00Z'}},policies:{coverage:'Selected opening factors'},totals:[{currency,values}],unavailableFactors:[],positions:[],time:{mode:'one_calendar_day_model_decay_v1',status:'complete',totals:[{currency,value:money(-1)}],partitions:[]}};
 window.api=async(path,body)=>{calls.push({path,body});if(path==='portfolio/pnl/policies')return {accounting:'accounting',attribution:'greeks',factors:'factors',clockPolicy:'clock',timeModes:[{id:'one_calendar_day_model_decay_v1',label:'One-day model decay'},{id:'opening_to_closing_clock_v1',label:'Opening-to-closing time roll'}],clocks:[{code:'UTC',kind:'fixed_offset',fixedOffsetSeconds:0,ianaZone:null,tzdbVersion:null}]};if(path==='portfolio/pnl/run')return {report,artifact:'protected-result'};if(path==='portfolio/pnl/saved')return [];if(path==='portfolio/pnl/save'){if(failSave)throw Error('connection lost');return {report,name:body.name,resultHash:'hash'};}throw Error(path);};
 window.options={api,act:f=>{window.lastWork=f().catch(e=>errors.push(e.message));return lastWork;},busy:()=>false,canRun:()=>true,identity:()=>actor,frames:()=>[{id:'opening',name:'Opening',at:'2026-01-01T21:00:00Z'},{id:'closing',name:'Closing',at:'2026-01-02T21:00:00Z'}],scope:()=>({bookIds:['book'],portfolioIds:[],tradeIds:null}),knownAt:()=> '2026-11-01T00:00:00Z',instant:x=>x,changed(){}};
 window.desk=setupPnlView(document.querySelector('#host'),options);await desk.load();desk.sync(true);
 window.field=label=>[...document.querySelectorAll('.pnl-controls label')].find(e=>e.firstChild.textContent===label)?.querySelector('select,input');
 window.run=async()=>{field('Opening close').value='opening';field('Closing close').value='closing';field('Calendar clock').value='UTC';[...document.querySelectorAll('button')].find(b=>b.textContent==='Run P&L').click();await lastWork;};})()`;

test('P&L offers theta modes, sends explicit inputs and presents results before optional Save',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 assert.equal(await evaluate("field('Time effect').value"),'one_calendar_day_model_decay_v1');
 assert.equal(await evaluate("[...document.querySelectorAll('button')].some(b=>b.textContent==='Save report')"),false);
 await evaluate('run()');
 assert.deepEqual((await evaluate("calls.find(c=>c.path==='portfolio/pnl/run').body")).time,{mode:'one_calendar_day_model_decay_v1',policy:'clock',clock:{code:'UTC',kind:'fixed_offset',fixedOffsetSeconds:0,ianaZone:null,tzdbVersion:null}});
 assert.equal(await evaluate("[...document.querySelectorAll('button')].some(b=>b.textContent==='Save report')"),true);
 assert.match(await evaluate("document.querySelector('#host').textContent"),/One-day model decay/);
 assert.equal(await evaluate("calls.filter(c=>c.path==='portfolio/pnl/save').length"),0);
}));
test('uncertain Save survives remount and retries exact request and opaque result',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 await evaluate("document.querySelector('.pnl-save input').value='Daily P&L';failSave=true;[...document.querySelectorAll('button')].find(b=>b.textContent==='Save report').click();lastWork");
 assert.equal(await evaluate('desk.unconfirmed()'),true);
 await evaluate("desk=setupPnlView(document.querySelector('#host'),options);desk.load()");
 assert.equal(await evaluate("[...document.querySelectorAll('button')].some(b=>b.textContent==='Retry save')"),true);
 await evaluate("failSave=false;[...document.querySelectorAll('button')].find(b=>b.textContent==='Retry save').click();lastWork");
 const commands=await evaluate("calls.filter(c=>c.path==='portfolio/pnl/save').map(c=>c.body)");
 assert.equal(commands.length,2);assert.deepEqual(commands[0],commands[1]);assert.equal(commands[0].artifact,'protected-result');
 assert.equal(await evaluate('desk.unconfirmed()'),false);
}));

test('a definitive unsaved result releases recovery after an uncertain save and permits another run',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 await evaluate("document.querySelector('.pnl-save input').value='Daily P&L';failSave=true;[...document.querySelectorAll('button')].find(b=>b.textContent==='Save report').click();lastWork");
 assert.equal(await evaluate('desk.unconfirmed()'),true);
 await evaluate("const originalApi=options.api;options.api=async(path,body)=>{if(path==='portfolio/pnl/save')throw Object.assign(Error('The result was not saved and is no longer available. Run again.'),{code:'pnl_unsaved'});return originalApi(path,body);};[...document.querySelectorAll('button')].find(b=>b.textContent==='Retry save').click();lastWork");
 assert.equal(await evaluate('desk.unconfirmed()'),false);
 assert.match(await evaluate("document.querySelector('#host').textContent"),/Not saved/);
 await evaluate('run()');
 assert.equal(await evaluate("calls.filter(c=>c.path==='portfolio/pnl/run').length"),2);
 assert.equal(await evaluate("[...document.querySelectorAll('button')].some(b=>b.textContent==='Save report')"),true);
}));

// A reset is the workspace's source/scope generation fence, including A→B→A.
test('a delayed run cannot return after source or scope changes',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("const originalApi=options.api;options.api=(path,body)=>path==='portfolio/pnl/run'?new Promise(resolve=>{window.releaseRun=()=>originalApi(path,body).then(resolve);}):originalApi(path,body);void run()");
 await evaluate("desk.reset();desk.reset();releaseRun();lastWork");
 assert.equal(await evaluate("[...document.querySelectorAll('button')].some(b=>b.textContent==='Save report')"),false);
 assert.equal(await evaluate("document.querySelector('#host').textContent.includes('ECONOMIC P&L')"),false);
}));

test('narrow P&L keeps position columns reachable inside a horizontal table scroll',browser,()=>withBrowserPage('<meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/app.css"><div id="host"></div>',async(evaluate,command)=>{
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await evaluate(setup);await evaluate('run()');
 const layout=await evaluate("(()=>{const table=document.querySelector('.pnl-section:last-of-type .table-wrap');return {viewport:document.documentElement.clientWidth,width:document.documentElement.scrollWidth,overflow:getComputedStyle(table).overflowX};})()");
 assert.equal(layout.width,layout.viewport);assert.equal(layout.overflow,'auto');
}));

test('P&L empty and completed views use the shared dark theme with readable text',browser,()=>withBrowserPage('<link rel="stylesheet" href="/app.css"><div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 assert.deepEqual(await evaluate(`(${themeAudit})(document.querySelector('#host'))`),{grounds:[],text:[]});
 await evaluate('run()');
 assert.deepEqual(await evaluate(`(${themeAudit})(document.querySelector('#host'))`),{grounds:[],text:[]});
}));
