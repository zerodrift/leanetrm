import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
import {themeAudit} from './theme-audit.mjs';
const browser={skip:!browserAvailable,timeout:browserTestTimeout};
// The mock mirrors a backend report of two option holdings, each priced on its own factor, and one trade's premium cash.
// Both deltas are estimated (4 + 2) and neither gamma is available: value movement 8 = delta 6 + residual 2, and with
// cash 2 the economic P&L is 10. The amount display is not served, so every amount reads as its exact text unless a
// test serves it.
const setup=`(async()=>{window.calls=[];window.actor='risk';window.failSave=false;window.errors=[];const {setupPnlView}=await import('/pnl-view.js');window.setupPnlView=setupPnlView;
 const currency={code:'USD',revision:{catalogId:'catalog',revisionId:'ref'}};window.money=n=>({amount:String(n),currency});
 const amounts=(openingValue,closingValue,cash,explained,unavailableEstimates)=>({openingValue:money(openingValue),closingValue:money(closingValue),valueMovement:money(closingValue-openingValue),cash:money(cash),priorCash:money(0),transfers:money(0),
  economicPnl:money(closingValue-openingValue+cash),explained:money(explained),residual:money(closingValue-openingValue-explained),unavailableEstimates});
 const step=(label,kind,amount,start,unavailableEstimates=0)=>({label,kind,amount:amount===null?null:money(amount),start:money(start),end:money(start+(amount??0)),unavailableEstimates});
 const bridge=[step('Delta','greek',6,0),step('Gamma','greek',null,6,2),step('Cross gamma','greek',0,6),step('Residual','residual',2,6),step('Cash','cash',2,8),
  step('Prior cash corrections','adjustment',0,10),step('Transfers','adjustment',0,10)];
 const greek=(formula,label,axis,amount)=>({formula,label,axes:[axis],amount:amount===null?null:money(amount),unavailableReason:amount===null?'finite_difference_unstable':null});
 const estimates=(axis,delta)=>[greek('central_first_difference_v1','Delta',axis,delta),greek('central_diagonal_second_difference_v1','Gamma',axis,null)];
 const held=(tradeReference,instrumentReference,values,contributions)=>({position:{tradeId:tradeReference,bindingId:instrumentReference,bookId:'book'},tradeReference,instrumentReference,state:'continuing',values,contributions});
 const positions=[held('TR-1','IN-1',amounts(60,65,0,4,1),estimates('price_0',4)),held('TR-1','IN-2',amounts(-3,-3,2,0,0),[]),held('TR-2','IN-3',amounts(43,46,0,2,1),estimates('price_1',2)),
  held('TR-2','IN-4',amounts(-5,-5,0,0,0),[])];
 const factor=(axis,label,opening,closing,change)=>({axis,label,priceTick:'0.01',opening:{price:opening},closing:closing===null?null:{price:closing},change:change===null?null:{price:change},
  unavailableReason:closing===null?'closing_exact_price_unavailable':null});
 const effect=(axis,delta)=>({axis,delta:{amount:money(delta),unavailableEstimates:0},gamma:{amount:null,unavailableEstimates:1}});
 window.report={id:'report',version:'trading_pnl_report_v2',scope:{books:[{id:'book',name:'Gas options'}],tradeSelection:'Selected trades'},
  opening:{at:'2026-01-01T21:00:00Z',context:{knownAt:'2026-11-01T00:00:00Z'}},closing:{at:'2026-01-02T21:00:00Z',context:{knownAt:'2026-11-01T00:00:00Z'}},policies:{coverage:'Selected opening factors',
   timeModes:[{id:'one_calendar_day_model_decay_v1',label:'One-day model decay'},{id:'opening_to_closing_clock_v1',label:'Opening-to-closing time roll'}]},
  factors:[factor('price_0','Gas futures · Dec 2026','3.30','3.33','0.03'),factor('price_1','Crude futures · Dec 2026','60.50','61.25','0.75')],
  totals:[{currency,values:amounts(95,103,2,6,2),bridge,factorEffects:[effect('price_0',4),effect('price_1',2)],greekCoverage:'partial'}],positions,
  time:{mode:'one_calendar_day_model_decay_v1',status:'complete',totals:[{currency,value:money(-1)}],partitions:[]}};
 window.capture={artifact:'protected-result',saveRefusal:null};window.saved={reports:[],more:false};window.stored=undefined;window.selectedFrame='closing';window.display=undefined;
 window.frames=[{id:'opening',at:'2026-01-01T21:00:00Z',label:'Evening close · Jan 1, 2026',session:'evening'},{id:'closing',at:'2026-01-02T21:00:00Z',label:'Evening close · Jan 2, 2026',session:'evening'}];
 window.api=async(path,body)=>{calls.push({path,body});
  if(path==='portfolio/pnl/policies')return {accounting:'accounting',attribution:'greeks',factors:'factors',clockPolicy:'clock',timeModes:[{id:'one_calendar_day_model_decay_v1',label:'One-day model decay'},{id:'opening_to_closing_clock_v1',label:'Opening-to-closing time roll'}],
   clocks:[{code:'UTC',kind:'fixed_offset',fixedOffsetSeconds:0,ianaZone:null,tzdbVersion:null}]};
  if(path==='portfolio/pnl/run')return {report,...capture};if(path==='portfolio/pnl/saved')return saved;if(path.startsWith('portfolio/pnl/saved/'))return stored;
  if(path==='amounts/display'&&display)return body.map(display);
  if(path==='time/inspect')return {candidates:[{instant:body.localTime+'Z',offset:'+00:00'}]};
  if(path==='portfolio/pnl/save'){if(failSave)throw typeof failSave==='string'?Object.assign(Error('refused'),{code:failSave}):Error('connection lost');return {report,name:body.name,resultHash:'hash'};}throw Error(path);};
 window.options={api,act:f=>{window.lastWork=f().catch(e=>errors.push(e.message));return lastWork;},busy:()=>false,canRun:()=>true,identity:()=>actor,zone:()=>'UTC',frames:()=>frames,selected:()=>selectedFrame,
  scope:()=>({bookIds:['book'],portfolioIds:[],tradeIds:null}),knownAt:()=>'2026-11-01T00:00:07.1234567Z',instant:x=>x,changed(){}};
 window.desk=setupPnlView(document.querySelector('#host'),options);await desk.load();desk.sync(true);
 window.field=label=>[...document.querySelectorAll('.pnl-controls label')].find(e=>e.firstChild.textContent===label)?.querySelector('select,input');
 window.button=text=>[...document.querySelectorAll('button')].find(b=>b.textContent===text);
 window.texts=selector=>[...document.querySelectorAll(selector)].map(e=>e.textContent);
 window.rows=selector=>[...document.querySelectorAll(selector)].map(row=>[...row.cells].map(cell=>cell.textContent));
 window.run=async()=>{field('Opening close').value='opening';field('Closing close').value='closing';field('Calendar clock').value='UTC';button('Run P&L').click();await lastWork;};})()`;
const page=body=>withBrowserPage('<div id="host"></div>',body);

test('P&L offers theta modes, sends explicit inputs and presents results before optional Save',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 assert.equal(await evaluate("field('Time effect').value"),'one_calendar_day_model_decay_v1');
 assert.equal(await evaluate("!!button('Save report')"),false);
 await evaluate('run()');
 const sent=await evaluate("calls.find(c=>c.path==='portfolio/pnl/run').body");
 assert.deepEqual(sent.time,{mode:'one_calendar_day_model_decay_v1',policy:'clock',clock:{code:'UTC',kind:'fixed_offset',fixedOffsetSeconds:0,ianaZone:null,tzdbVersion:null}});
 assert.deepEqual([sent.opening.snapshotId,sent.closing.snapshotId,sent.knownAt],['opening','closing','2026-11-01T00:00:07.1234567Z']);
 assert.equal(await evaluate("!!button('Save report')"),true);
 assert.match(await evaluate("document.querySelector('#host').textContent"),/One-day model decay/);
 assert.equal(await evaluate("calls.filter(c=>c.path==='portfolio/pnl/save').length"),0);
}));
test('the page says plainly what it does and what to do before a result exists, under the application\'s own page title',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 assert.deepEqual(await evaluate("texts('.pnl-intro > *, .pnl-empty h3')"),['Compare two closes of the selected Risk scope. The result reconciles value and cash, and shows what opening price Greeks explain.','No result yet']);
}));
test('a run that is refused says so instead of still preparing, and a change of scope clears the status and refills the cutoff',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 await evaluate("const originalApi=options.api;options.api=(path,body)=>path==='portfolio/pnl/run'?Promise.reject(Object.assign(Error('This scope has 23 price factors.'),{code:'pnl_factor_limit'})):originalApi(path,body);run()");
 assert.deepEqual(await evaluate("[document.querySelector('[role=status]').textContent,errors]"),['P&L did not run.',['This scope has 23 price factors.']]);
 await evaluate("desk.reset()");
 assert.deepEqual(await evaluate("[document.querySelector('[role=status]').textContent,field('Known as of · UTC').value]"),['','']);
 await evaluate("desk.sync(true)");
 assert.equal(await evaluate("field('Known as of · UTC').value"),'2026-11-01T00:00:07');
}));
test('closes read as their own labels, and start as the header close and the close before it in its session',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 await evaluate("frames=[{id:'a',at:'2026-01-01T17:00:00Z',label:'Midday close · Jan 1, 2026',session:'midday'},{id:'b',at:'2026-01-01T21:00:00Z',label:'Evening close · Jan 1, 2026',session:'evening'},"+
  "{id:'c',at:'2026-01-02T17:00:00Z',label:'Midday close · Jan 2, 2026',session:'midday'},{id:'d',at:'2026-01-02T21:00:00Z',label:'Evening close · Jan 2, 2026',session:'evening'}];selectedFrame='d';desk.reset();desk.load()");
 assert.deepEqual(await evaluate("[...field('Closing close').options].map(o=>o.text)"),['Choose a close…','Midday close · Jan 1, 2026','Evening close · Jan 1, 2026','Midday close · Jan 2, 2026','Evening close · Jan 2, 2026']);
 assert.deepEqual(await evaluate("[field('Opening close').value,field('Closing close').value]"),['b','d']);
 // A choice the person makes is kept when the closes are read again.
 await evaluate("field('Opening close').value='a';field('Opening close').dispatchEvent(new Event('change'));desk.load()");
 assert.deepEqual(await evaluate("[field('Opening close').value,field('Closing close').value]"),['a','d']);
 // The choice outlives a change of scope, which resets the page.
 await evaluate("desk.reset();desk.load()");
 assert.deepEqual(await evaluate("[field('Opening close').value,field('Closing close').value]"),['a','d']);
 // On a page where nothing was chosen and the header names no close among them, nothing is assumed.
 await evaluate("selectedFrame=undefined;desk=setupPnlView(document.querySelector('#host'),options);desk.load()");
 assert.deepEqual(await evaluate("[field('Opening close').value,field('Closing close').value]"),['','']);
 // Closes the page offered are withdrawn when the header stops naming a close.
 await evaluate("selectedFrame='d';desk.load()");
 assert.deepEqual(await evaluate("[field('Opening close').value,field('Closing close').value]"),['b','d']);
 await evaluate("selectedFrame=undefined;desk.load()");
 assert.deepEqual(await evaluate("[field('Opening close').value,field('Closing close').value]"),['','']);
}));
test('a header close with no earlier close of its convention, and a working snapshot, start from the frame before them',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 await evaluate("frames=[{id:'m1',at:'2026-01-01T17:00:00Z',label:'Midday close · Jan 1, 2026',session:'midday'},{id:'e1',at:'2026-01-01T21:00:00Z',label:'Evening close · Jan 1, 2026',session:'evening'},"+
  "{id:'w1',at:'2026-01-02T12:00:00Z',label:'Working snapshot · Jan 2',session:null},{id:'e2',at:'2026-01-02T21:00:00Z',label:'Evening close · Jan 2, 2026',session:'evening'},"+
  "{id:'w2',at:'2026-01-03T12:00:00Z',label:'Working snapshot · Jan 3',session:null}];selectedFrame='e1';desk.reset();desk.load()");
 assert.deepEqual(await evaluate("[field('Opening close').value,field('Closing close').value]"),['m1','e1']);
 // A working snapshot has no convention to match: the frame before it is a close, not the working snapshot before that.
 await evaluate("selectedFrame='w2';desk.load()");
 assert.deepEqual(await evaluate("[field('Opening close').value,field('Closing close').value]"),['e2','w2']);
}));
test('with no closes to compare the page says where to choose one',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate("frames=[];desk.reset();desk.load()");
 assert.deepEqual(await evaluate("[...field('Opening close').options].map(o=>o.text)"),['No closes to compare']);
 assert.equal(await evaluate("document.querySelector('[role=status]').textContent"),'Choose a market close in the header to list the closes you can compare.');
}));
test('the knowledge cutoff reads as local time, sends the exact header cutoff, and resolves an edited time through the backend',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 assert.deepEqual(await evaluate("[field('Known as of · UTC').type,field('Known as of · UTC').value]"),['datetime-local','2026-11-01T00:00:07']);
 await evaluate("field('Known as of · UTC').value='2026-10-31T12:00:09';field('Known as of · UTC').dispatchEvent(new Event('input'));field('Known as of · UTC').dispatchEvent(new Event('change'))");
 await evaluate('run()');
 assert.deepEqual(await evaluate("calls.find(c=>c.path==='time/inspect').body"),{localTime:'2026-10-31T12:00:09',timeZone:'UTC'});
 assert.equal(await evaluate("calls.find(c=>c.path==='portfolio/pnl/run').body.knownAt"),'2026-10-31T12:00:09Z');
}));
test('amounts and prices read as the shared display formats them, with the exact value kept beside them',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate("display=r=>'['+r.value+(r.tick?' tick '+r.tick:' to '+r.decimalPlaces)+']'");await evaluate('run()');
 assert.deepEqual(await evaluate("[document.querySelector('.pnl-hero .pnl-money').textContent,document.querySelector('.pnl-hero .pnl-money').title]"),['[10 to 3]','10']);
 assert.deepEqual(await evaluate("rows('.pnl-factors tbody tr')[0].slice(1,4)"),['[3.30 tick 0.01]','[3.33 tick 0.01]','[0.03 tick 0.01]']);
 assert.equal(await evaluate("document.querySelector('.pnl-bar-value').textContent"),'[6 to 3]');
}));
test('only the amounts the page shows are sent for display, not the evidence a report carries',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate("display=r=>r.value;report.holdings={opening:[{presentValue:'424242'}]};report.time.partitions=[{status:'complete',clock:{at:'2026-01-02T21:00:00Z'},groups:[{amount:'535353'}]}]");
 await evaluate('run()');
 assert.deepEqual(await evaluate("(()=>{const sent=calls.filter(c=>c.path==='amounts/display').flatMap(c=>c.body.map(r=>r.value));return [sent.includes('10'),sent.includes('424242'),sent.includes('535353')];})()"),[true,false,false]);
}));
test('what moved lists each factor with its prices, its change and what it explains',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate("report.factors.push({axis:'price_2',label:'Power futures · Dec 2026',priceTick:'0.01',opening:{price:'40.00'},closing:null,change:null,unavailableReason:'closing_exact_price_unavailable'});"+
  "report.totals[0].factorEffects.push({axis:'price_2',delta:{amount:null,unavailableEstimates:1},gamma:{amount:null,unavailableEstimates:1}})");
 await evaluate('run()');
 assert.deepEqual(await evaluate("texts('.pnl-factors thead th')"),['Factor','Opening','Closing','Change','Delta effect','Gamma effect']);
 assert.deepEqual(await evaluate("rows('.pnl-factors tbody tr')"),[['Gas futures · Dec 2026','3.30','3.33','0.03','4','Unavailable'],['Crude futures · Dec 2026','60.50','61.25','0.75','2','Unavailable'],
  ['Power futures · Dec 2026','40.00','No closing price','—','Unavailable','Unavailable']]);
}));
test('a Greek effect with no available estimate reads Unavailable in the bridge chart and table, never 0',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 // The bridge ends in its result; adjustments that are exactly zero are listed in the table and not drawn.
 assert.deepEqual(await evaluate("texts('.pnl-bar-label').map((label,n)=>[label,texts('.pnl-bar-value')[n]])"),[['Delta','6'],['Gamma','Unavailable'],['Cross gamma','0'],['Residual','2'],['Cash','2'],['P&L','10']]);
 assert.deepEqual(await evaluate("[...document.querySelectorAll('.pnl-step')].map(step=>step.querySelector('.pnl-bar')?.className??'none')"),
  ['pnl-bar gain','none','pnl-bar flat','pnl-bar gain hollow','pnl-bar gain','pnl-bar total']);
 assert.deepEqual(await evaluate("rows('.pnl-bridge details tbody tr')"),[['Delta','6'],['Gamma','Unavailable · 2 estimates, in Residual'],['Cross gamma','0'],['Residual','2'],['Cash','2'],
  ['Prior cash corrections','0'],['Transfers','0'],['Economic P&L','10']]);
 assert.equal(await evaluate("[...document.querySelectorAll('.pnl-bridge p')].find(p=>p.textContent.startsWith('Partial')).textContent"),
  'Partial price-Greek coverage · 0 factors without a closing price · 2 estimates unavailable. Their effects remain in Residual.');
}));
test('a report in two currencies shows the chosen currency alone: its result, factors, coverage and holdings',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 // One euro holding, priced on a factor with no closing price: nothing of it is explained.
 await evaluate("(()=>{const eur={code:'EUR',revision:{catalogId:'catalog',revisionId:'ref-eur'}},euro=n=>({amount:String(n),currency:eur}),none={amount:null,unavailableEstimates:1};"+
  "const values={openingValue:euro(20),closingValue:euro(27),valueMovement:euro(7),cash:euro(0),priorCash:euro(0),transfers:euro(0),economicPnl:euro(7),explained:null,residual:euro(7),unavailableEstimates:2};"+
  "const step=(label,kind,amount,start,end,unavailableEstimates=0)=>({label,kind,amount:amount===null?null:euro(amount),start:euro(start),end:euro(end),unavailableEstimates});"+
  "report.factors.push({axis:'price_2',label:'Power futures · Dec 2026',priceTick:'0.01',opening:{price:'40.00'},closing:null,change:null,unavailableReason:'closing_exact_price_unavailable'});"+
  "report.totals.push({currency:eur,values,greekCoverage:'partial',factorEffects:[{axis:'price_2',delta:none,gamma:none}],"+
  "bridge:[step('Delta','greek',null,0,0,1),step('Gamma','greek',null,0,0,1),step('Cross gamma','greek',0,0,0),step('Residual','residual',7,0,7),step('Cash','cash',0,7,7)]});"+
  "report.positions.push({position:{tradeId:'TR-3',bindingId:'IN-5',bookId:'book'},tradeReference:'TR-3',instrumentReference:'IN-5',state:'continuing',values,"+
  "contributions:[{formula:'central_first_difference_v1',label:'Delta',axes:['price_2'],amount:null,unavailableReason:'closing_exact_price_unavailable'},"+
  "{formula:'central_diagonal_second_difference_v1',label:'Gamma',axes:['price_2'],amount:null,unavailableReason:'closing_exact_price_unavailable'}]});})()");
 await evaluate('run()');
 const shown=()=>evaluate("({result:document.querySelector('.pnl-hero .pnl-money').textContent+' '+document.querySelector('.pnl-hero small').textContent,count:texts('.pnl-period span')[1],"+
  "factors:rows('.pnl-factors tbody tr').map(r=>r[0]),coverage:[...document.querySelectorAll('.pnl-bridge p')].find(p=>p.textContent.startsWith('Partial')).textContent,"+
  "holdings:rows('.pnl-positions tbody tr').map(r=>r[0]+' '+r[3]),time:texts('.pnl-time > *')[1]})");
 assert.deepEqual(await shown(),{result:'10 USD',count:'4 holdings and cash items',factors:['Gas futures · Dec 2026','Crude futures · Dec 2026'],
  coverage:'Partial price-Greek coverage · 0 factors without a closing price · 2 estimates unavailable. Their effects remain in Residual.',
  holdings:['TR-1 · IN-1 Held','TR-1 · IN-2 Held','TR-2 · IN-3 Held'],time:'-1'});
 await evaluate("(()=>{const chooser=document.querySelector('select[aria-label=\"Report currency\"]');chooser.focus();chooser.value='1';chooser.dispatchEvent(new Event('change'));})()");
 assert.deepEqual(await shown(),{result:'7 EUR',count:'1 holding or cash item',factors:['Power futures · Dec 2026'],
  coverage:'Partial price-Greek coverage · 1 factor without a closing price · 2 estimates unavailable. Their effects remain in Residual.',
  holdings:['TR-3 · IN-5 Held'],time:'No opening exposure in this currency.'});
 assert.deepEqual(await evaluate("rows('.pnl-factors tbody tr')[0]"),['Power futures · Dec 2026','40.00','No closing price','—','Unavailable','Unavailable']);
 // Nothing of the euro holding is explained: that reads Unavailable beside its residual, never 0, and the keyboard stays on the chooser.
 assert.deepEqual(await evaluate("[texts('.pnl-metrics > div').find(t=>t.startsWith('Available Greek effects')),rows('.pnl-positions tbody tr')[0].slice(8),document.activeElement.getAttribute('aria-label')]"),
  ['Available Greek effectsUnavailable',['Unavailable','7'],'Report currency']);
}));
test('a time effect the scope cannot provide says so in words with its reason, and shows no figure or internal code',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 await evaluate("report.policies.timeModes[0].label='One calendar day of decay';report.time={mode:'one_calendar_day_model_decay_v1',status:'unavailable',totals:null,"+
  "partitions:[{status:'unavailable',code:'valuation_time_unsupported',detail:'A held instrument does not support a change of valuation time.',clock:{at:'2026-01-02T21:00:00Z'}}]}");
 await evaluate('run()');
 assert.deepEqual(await evaluate("[texts('.pnl-time > *'),document.querySelectorAll('.pnl-time .pnl-money').length]"),
  [['One calendar day of decay','Time effect unavailable for the complete opening scope.','Frozen opening markets and knowledge. This diagnostic is separate from the period reconciliation.',
   'A held instrument does not support a change of valuation time.'],0]);
}));
test('a partly estimated Greek effect keeps its amount and says how many estimates are missing',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate("report.totals[0].bridge[0].unavailableEstimates=1");await evaluate('run()');
 assert.deepEqual(await evaluate("[document.querySelector('.pnl-bar-label').textContent,document.querySelector('.pnl-bar-value').textContent]"),['Delta · partial','6']);
 assert.deepEqual(await evaluate("rows('.pnl-bridge details tbody tr')[0]"),['Delta','6 · 1 estimate unavailable, in Residual']);
}));
test('an adjustment that is not zero is drawn in the bridge',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate("const b=report.totals[0].bridge;b[5]={...b[5],amount:money(-1),end:money(9)};b[6]={...b[6],start:money(9),end:money(9)};report.totals[0].values.economicPnl=money(9)");await evaluate('run()');
 assert.deepEqual(await evaluate("texts('.pnl-bar-label')"),['Delta','Gamma','Cross gamma','Residual','Cash','Prior cash corrections','P&L']);
}));
test('a bridge of losses hangs from its zero line, and one too large to plot says so and keeps its exact table',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 await evaluate("window.step=(n,amount,start,end)=>{const b=report.totals[0].bridge;b[n]={...b[n],amount:amount===null?null:money(amount),start:money(start),end:money(end)};};"+
  "step(0,-6,0,-6);step(1,null,-6,-6);step(2,0,-6,-6);step(3,-2,-6,-8);step(4,-2,-8,-10);step(5,0,-10,-10);step(6,0,-10,-10);report.totals[0].values.economicPnl=money(-10)");
 await evaluate('run()');
 assert.deepEqual(await evaluate("[[...document.querySelectorAll('.pnl-step')].map(step=>step.querySelector('.pnl-bar')?.className??'none'),document.querySelector('.pnl-waterfall').style.getPropertyValue('--pnl-zero')]"),
  [['pnl-bar loss','none','pnl-bar flat','pnl-bar loss hollow','pnl-bar loss','pnl-bar total'],'100%']);
 await evaluate("const huge='1'+'0'.repeat(400);step(0,huge,0,huge);run()");
 assert.deepEqual(await evaluate("[document.querySelectorAll('.pnl-waterfall').length,[...document.querySelectorAll('.pnl-bridge > p')].at(-1).textContent,rows('.pnl-bridge details tbody tr').length]"),
  [0,'The bridge cannot be plotted at this scale. Exact values remain available below.',8]);
}));
test('holdings name their book and what they are priced on, in plain words, with estimates in a full-width detail row',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 assert.deepEqual(await evaluate("texts('.pnl-positions thead th')"),['Holding','Book','Priced on','Status','Opening PV','Closing PV','Cash','P&L','Price Greeks','Residual']);
 // The premium cash that neither moved nor paid is counted, not listed, until asked for.
 assert.deepEqual(await evaluate("rows('.pnl-positions tbody tr')"),[['TR-1 · IN-1','Gas options','Gas futures · Dec 2026','Held','60','65','0','5','4 · partial','1'],
  ['TR-1 · IN-2','Gas options','—','Held','-3','-3','2','2','0','0'],['TR-2 · IN-3','Gas options','Crude futures · Dec 2026','Held','43','46','0','3','2 · partial','1']]);
 // A holding's partial explanation says how many of its estimates are missing where the person points.
 assert.equal(await evaluate("document.querySelector('.pnl-positions .pnl-partial').title"),'1 estimate unavailable, in Residual');
 await evaluate("button('Show 1 holding with no P&L').click()");
 assert.deepEqual(await evaluate("rows('.pnl-positions tbody tr').map(r=>r[0])"),['TR-1 · IN-1','TR-1 · IN-2','TR-2 · IN-3','TR-2 · IN-4']);
 await evaluate("button('TR-1 · IN-1').click()");
 const detail=await evaluate("(()=>{const row=document.querySelector('.pnl-positions tr.pnl-detail');return {span:row.cells[0].colSpan,cells:row.cells.length,lines:[...row.querySelectorAll(':scope table > tbody > tr')].map(r=>[...r.cells].map(c=>c.textContent)),expanded:button('TR-1 · IN-1').getAttribute('aria-expanded')};})()");
 assert.deepEqual(detail,{span:10,cells:1,lines:[['Delta','Gas futures · Dec 2026','4'],['Gamma','Gas futures · Dec 2026','Estimate did not converge']],expanded:'true'});
 await evaluate("button('TR-1 · IN-1').click()");
 assert.equal(await evaluate("document.querySelectorAll('.pnl-positions tr.pnl-detail').length"),0);
}));
test('ledger cash of a trade held at neither close reads as ledger cash',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 await evaluate("report.positions.push({position:{tradeId:'gone',bindingId:'gone',bookId:'book'},tradeReference:null,instrumentReference:null,state:'cash_only',"+
  "values:{...report.positions[1].values,openingValue:money(0),closingValue:money(0),valueMovement:money(0),cash:money(3),economicPnl:money(3),explained:money(0),residual:money(0),unavailableEstimates:0},contributions:[]})");
 await evaluate('run()');
 assert.deepEqual(await evaluate("rows('.pnl-positions tbody tr').at(-1)"),['Ledger cash','Gas options','—','Cash only','0','0','3','3','0','0']);
}));
test('more than a hundred holdings page a hundred at a time, a paging control never leaves the list, and sorting returns to the first page',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 await evaluate("report.positions=Array.from({length:230},(_,n)=>({...report.positions[0],position:{tradeId:'T'+n,bindingId:'B',bookId:'book'},tradeReference:'TR-'+String(n).padStart(3,'0'),instrumentReference:'IN-1',"+
  "values:{...report.positions[0].values,economicPnl:money(n)}}))");
 await evaluate('run()');
 const pages=()=>evaluate("({rows:document.querySelectorAll('.pnl-positions .table-wrap > table > tbody > tr').length,text:document.querySelector('.pnl-positions .actions span').textContent,"+
  "previous:!button('Previous rows').hidden,next:!button('Next rows').hidden,first:rows('.pnl-positions tbody tr')[0][0]})");
 assert.deepEqual(await pages(),{rows:100,text:'Rows 1–100 of 230',previous:false,next:true,first:'TR-000 · IN-1'});
 // The application re-enables every control when an action ends; a control that does not apply is hidden, and does nothing if pressed.
 await evaluate("button('Previous rows').click()");
 assert.equal((await pages()).text,'Rows 1–100 of 230');
 await evaluate("button('Next rows').click();button('Next rows').click()");
 assert.deepEqual(await pages(),{rows:30,text:'Rows 201–230 of 230',previous:true,next:false,first:'TR-200 · IN-1'});
 await evaluate("button('Next rows').click()");
 assert.equal((await pages()).text,'Rows 201–230 of 230');
 await evaluate("button('P&L').click()");
 assert.deepEqual(await pages(),{rows:100,text:'Rows 1–100 of 230',previous:false,next:true,first:'TR-229 · IN-1'});
}));
test('sorting and opening a holding leave the keyboard on the control that was used',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 await evaluate("button('P&L').focus();button('P&L').click()");
 assert.deepEqual(await evaluate("[document.activeElement===button('P&L'),button('P&L').closest('th').getAttribute('aria-sort')]"),[true,'descending']);
 await evaluate("button('TR-2 · IN-3').focus();button('TR-2 · IN-3').click()");
 assert.deepEqual(await evaluate("[document.activeElement.textContent,document.activeElement.getAttribute('aria-expanded')]"),['TR-2 · IN-3','true']);
}));
test('holdings sort by an amount column, largest first and then smallest first',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 const order=()=>evaluate("rows('.pnl-positions tbody tr').map(r=>r[7])");
 await evaluate("button('P&L').click()");
 assert.deepEqual(await order(),['5','3','2']);
 assert.equal(await evaluate("button('P&L').closest('th').getAttribute('aria-sort')"),'descending');
 await evaluate("button('P&L').click()");
 assert.deepEqual(await order(),['2','3','5']);
 assert.equal(await evaluate("button('P&L').closest('th').getAttribute('aria-sort')"),'ascending');
}));
test('saving a result leaves the holdings as the person sorted and opened them',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 await evaluate("button('P&L').click();button('TR-2 · IN-3').click()");
 await evaluate("document.querySelector('.pnl-save input').value='Daily P&L';button('Save report').click();lastWork");
 assert.equal(await evaluate("document.querySelector('.pnl-save').textContent"),'Saved · Daily P&L');
 assert.deepEqual(await evaluate("[button('P&L').closest('th').getAttribute('aria-sort'),button('TR-2 · IN-3').getAttribute('aria-expanded'),document.querySelectorAll('.pnl-positions tr.pnl-detail').length]"),
  ['descending','true',1]);
}));
test('a save that succeeded stays reported as saved when the saved reports cannot be read',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 await evaluate("const originalApi=options.api;options.api=(path,body)=>path==='portfolio/pnl/saved'?Promise.reject(Error('The saved reports could not be read.')):originalApi(path,body);"+
  "document.querySelector('.pnl-save input').value='Daily P&L';button('Save report').click();lastWork");
 assert.deepEqual(await evaluate("[document.querySelector('.pnl-save').textContent,document.querySelector('[role=status]').textContent,desk.unconfirmed(),errors]"),
  ['Saved · Daily P&L','Saved the exact displayed result.',false,['The saved reports could not be read.']]);
}));
const kept="{id:'report',name:'First name',savedAt:'2026-01-03T08:00:00Z',openingAt:'2026-01-01T21:00:00Z',closingAt:'2026-01-02T21:00:00Z',books:['Gas options'],tradeSelection:'Selected trades'}";
test('discarding an unconfirmed save reads the saved reports again, and a result they hold reads as saved under its name',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 // The answer was lost, but the save had reached the server.
 await evaluate("document.querySelector('.pnl-save input').value='First name';failSave=true;button('Save report').click();lastWork");
 await evaluate(`saved={reports:[${kept}],more:false};button('Discard unconfirmed save').click();lastWork`);
 assert.deepEqual(await evaluate("[document.querySelector('.pnl-save').textContent,document.querySelector('[role=status]').textContent,texts('.pnl-saved-link'),!!button('Save report')]"),
  ['Saved · First name','This result is already saved as “First name”.',['First name'],false]);
}));
test('a save refused because the result is already saved under another name says so and shows that name',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 await evaluate(`saved={reports:[${kept}],more:false};document.querySelector('.pnl-save input').value='Second name';failSave='pnl_save_conflict';button('Save report').click();lastWork`);
 assert.deepEqual(await evaluate("[document.querySelector('.pnl-save').textContent,document.querySelector('[role=status]').textContent,desk.unconfirmed()]"),
  ['Saved · First name','This result is already saved as “First name”.',false]);
}));
test('a save answered after the scope changed draws nothing and stays unconfirmed for retry',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 await evaluate("const originalApi=options.api;options.api=(path,body)=>path==='portfolio/pnl/save'?new Promise(resolve=>{window.releaseSave=()=>originalApi(path,body).then(resolve);}):originalApi(path,body);"+
  "document.querySelector('.pnl-save input').value='Daily P&L';button('Save report').click();void 0");
 await evaluate("desk.reset();releaseSave();lastWork");
 assert.deepEqual(await evaluate("[document.querySelector('#host').textContent.includes('Economic P&L'),desk.unconfirmed(),!!button('Retry save')]"),[false,true,true]);
}));
test('a saved report whose amounts are still being prepared when the scope changes is not drawn',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 await evaluate(`saved={reports:[${kept}],more:false};stored={report,name:'First name'};desk.load()`);
 await evaluate("const originalApi=options.api;options.api=(path,body)=>path==='amounts/display'?new Promise(resolve=>{window.releaseDisplay=()=>resolve(body.map(r=>r.value));}):originalApi(path,body);"+
  "document.querySelector('.pnl-saved-link').click();new Promise(ready=>{const waiting=setInterval(()=>{if(window.releaseDisplay){clearInterval(waiting);ready(true);}},10);})");
 await evaluate("desk.reset();releaseDisplay();lastWork");
 assert.equal(await evaluate("document.querySelector('#host').textContent.includes('Economic P&L')"),false);
}));
test('uncertain Save survives remount and retries exact request and opaque result',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 await evaluate("document.querySelector('.pnl-save input').value='Daily P&L';failSave=true;button('Save report').click();lastWork");
 // The name of an unconfirmed save can be read, not changed; the application may re-enable controls, so it is not left to disabling.
 assert.deepEqual(await evaluate("[desk.unconfirmed(),document.querySelector('.pnl-save input').readOnly]"),[true,true]);
 await evaluate("desk=setupPnlView(document.querySelector('#host'),options);desk.load()");
 assert.equal(await evaluate("!!button('Retry save')"),true);
 await evaluate("failSave=false;button('Retry save').click();lastWork");
 const commands=await evaluate("calls.filter(c=>c.path==='portfolio/pnl/save').map(c=>c.body)");
 assert.equal(commands.length,2);assert.deepEqual(commands[0],commands[1]);assert.equal(commands[0].artifact,'protected-result');
 assert.equal(await evaluate('desk.unconfirmed()'),false);
}));
test('a definitive unsaved result releases recovery after an uncertain save and permits another run',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 await evaluate("document.querySelector('.pnl-save input').value='Daily P&L';failSave=true;button('Save report').click();lastWork");
 assert.equal(await evaluate('desk.unconfirmed()'),true);
 await evaluate("failSave='pnl_unsaved';button('Retry save').click();lastWork");
 assert.equal(await evaluate('desk.unconfirmed()'),false);
 assert.match(await evaluate("document.querySelector('#host').textContent"),/Not saved/);
 await evaluate('run()');
 assert.equal(await evaluate("calls.filter(c=>c.path==='portfolio/pnl/run').length"),2);
 assert.equal(await evaluate("!!button('Save report')"),true);
}));
test('a save the server refuses outright leaves nothing outstanding, and the report can be saved again',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 await evaluate("document.querySelector('.pnl-save input').value='Daily P&L';failSave='access_denied';button('Save report').click();lastWork");
 assert.equal(await evaluate('desk.unconfirmed()'),false);
 assert.equal(await evaluate("document.querySelector('[role=status]').textContent"),'The save was refused and nothing was saved.');
 assert.deepEqual(await evaluate("[!!button('Save report'),!!button('Retry save'),document.querySelector('.pnl-save input').readOnly]"),[true,false,false]);
}));
test('a refusal of a retry proves nothing about the first attempt, and the person can discard the unconfirmed save',browser,()=>page(async evaluate=>{
 await evaluate(setup);await evaluate('run()');
 await evaluate("document.querySelector('.pnl-save input').value='Daily P&L';failSave=true;button('Save report').click();lastWork");
 await evaluate("failSave='access_denied';button('Retry save').click();lastWork");
 assert.equal(await evaluate('desk.unconfirmed()'),true);
 await evaluate("button('Discard unconfirmed save').click()");
 assert.equal(await evaluate('desk.unconfirmed()'),false);
 assert.equal(await evaluate("document.querySelector('[role=status]').textContent"),'Discarded the unconfirmed save. If it reached the server, the report is listed under Saved P&L reports.');
 assert.equal(await evaluate("!!button('Save report')"),true);
}));
test('a result that cannot be kept for saving is presented unsaved with its reason',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 await evaluate("capture={artifact:null,saveRefusal:{code:'pnl_result_capacity',message:'This result is too large to save. Choose a smaller scope to keep a copy.'}}");
 await evaluate('run()');
 assert.deepEqual(await evaluate('errors'),[]);
 assert.equal(await evaluate("document.querySelector('.pnl-hero .pnl-money').textContent"),'10');
 assert.equal(await evaluate("document.querySelector('.pnl-save').textContent"),'Not saved · This result is too large to save. Choose a smaller scope to keep a copy.');
 assert.equal(await evaluate("[...document.querySelectorAll('button')].some(b=>/save/i.test(b.textContent))"),false);
 assert.equal(await evaluate('desk.unconfirmed()'),false);
}));
test('saved reports say when they were saved, which closes they compare and their scope',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 await evaluate("saved={reports:[{id:'kept',name:'Daily P&L',savedAt:'2026-01-03T08:00:00Z',openingAt:'2026-01-01T21:00:00Z',closingAt:'2026-01-02T21:00:00Z',books:['Gas options','Crude options'],tradeSelection:'Selected trades'}],more:true};"+
  "stored={report,name:'Daily P&L'};desk.load()");
 assert.deepEqual(await evaluate("texts('.pnl-saved')"),['Daily P&LSaved 2026-01-03T08:00:00Z · 2026-01-01T21:00:00Z → 2026-01-02T21:00:00Z · Gas options · Crude options · Selected trades']);
 assert.equal(await evaluate("document.querySelector('.pnl-library-more').textContent"),'Showing the 100 most recently saved reports. Older reports are not listed.');
 await evaluate("document.querySelector('.pnl-saved-link').click();lastWork");
 assert.equal(await evaluate("document.querySelector('.pnl-save').textContent"),'Saved · Daily P&L');
}));
test('a saved report in an earlier format says so and leaves the page as it was',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 await evaluate("saved={reports:[{id:'earlier',name:'Earlier report',savedAt:'2026-01-03T08:00:00Z',openingAt:'2026-01-01T21:00:00Z',closingAt:'2026-01-02T21:00:00Z',books:['Gas options'],tradeSelection:'Selected trades'}],more:false};"+
  "stored={report:{...report,version:'trading_pnl_report_v1'},name:'Earlier report'};desk.load()");
 await evaluate("document.querySelector('.pnl-saved-link').click();lastWork");
 assert.deepEqual(await evaluate('errors'),['This report was saved in an earlier format and cannot be shown.']);
 assert.equal(await evaluate("document.querySelector('.pnl-empty h3').textContent"),'No result yet');
}));

// A reset is the workspace's source/scope generation fence, including A→B→A.
test('a delayed run cannot return after source or scope changes',browser,()=>page(async evaluate=>{
 await evaluate(setup);
 await evaluate("const originalApi=options.api;options.api=(path,body)=>path==='portfolio/pnl/run'?new Promise(resolve=>{window.releaseRun=()=>originalApi(path,body).then(resolve);}):originalApi(path,body);void run()");
 await evaluate("desk.reset();desk.reset();releaseRun();lastWork");
 assert.equal(await evaluate("!!button('Save report')"),false);
 assert.equal(await evaluate("document.querySelector('#host').textContent.includes('Economic P&L')"),false);
}));

const styled='<meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/app.css"><div id="host"></div>';
test('on a phone the page never scrolls sideways, and the bridge and the holdings scroll inside their own frames with no figure over another',browser,()=>withBrowserPage(styled,async(evaluate,command)=>{
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await evaluate(setup);await evaluate("report.totals[0].bridge[0]={...report.totals[0].bridge[0],amount:money('1234567.891'),end:money('1234567.891')}");await evaluate('run()');
 const layout=await evaluate("(()=>{const figures=[...document.querySelectorAll('.pnl-bar-value')].map(e=>e.getBoundingClientRect());"+
  "return {viewport:document.documentElement.clientWidth,width:document.documentElement.scrollWidth,table:getComputedStyle(document.querySelector('.pnl-positions .table-wrap')).overflowX,"+
  "chart:getComputedStyle(document.querySelector('.pnl-waterfall').parentElement).overflowX,apart:figures.every((box,n)=>n===0||box.left>=figures[n-1].right)};})()");
 assert.deepEqual(layout,{viewport:layout.viewport,width:layout.viewport,table:'auto',chart:'auto',apart:true});
}));
// The page beside the application's sidebar in a 1440-wide window, with references, factor names and six-figure amounts as long as
// real ones, grouped as the shared display groups them.
test('at a desk width every holdings column shows without scrolling, a long factor name wrapping onto two lines at most',browser,()=>withBrowserPage(styled,async(evaluate,command)=>{
 await command('Emulation.setDeviceMetricsOverride',{width:1150,height:900,deviceScaleFactor:1,mobile:false});
 await evaluate(setup);
 await evaluate("display=r=>Number(r.value).toLocaleString('en-US',{minimumFractionDigits:3,maximumFractionDigits:3});"+
  "report.factors[0].label='NYMEX Henry Hub Natural Gas · NG · Feb 2027';report.factors[1].label='NYMEX Light Sweet Crude Oil · CL · Mar 2027';report.scope.books[0].name='Book A';"+
  "for(const [n,p] of report.positions.entries()){p.tradeReference='TR-00002'+n;p.instrumentReference='IN-00002'+n;"+
  "Object.assign(p.values,{openingValue:money('118193.476'),closingValue:money('120965.943'),economicPnl:money('-1172.926'),explained:money('-1095.475'),residual:money('-2967.870')});}");
 await evaluate('run()');
 const layout=await evaluate("(()=>{const wrap=document.querySelector('.pnl-positions .table-wrap'),names=[...wrap.querySelectorAll('.pnl-factor-name')];"+
  "const lines=name=>{const text=document.createRange();text.selectNodeContents(name);return new Set([...text.getClientRects()].map(box=>Math.round(box.top))).size;};"+
  "return {fits:wrap.scrollWidth<=wrap.clientWidth,names:names.length,lines:Math.max(...names.map(lines))};})()");
 assert.deepEqual(layout,{fits:true,names:2,lines:2});
}));

test('P&L empty and completed views use the shared dark theme with readable text',browser,()=>withBrowserPage('<link rel="stylesheet" href="/app.css"><div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 assert.deepEqual(await evaluate(`(${themeAudit})(document.querySelector('#host'))`),{grounds:[],text:[]});
 await evaluate('run()');await evaluate("button('Show 1 holding with no P&L').click();button('TR-1 · IN-1').click()");
 assert.deepEqual(await evaluate(`(${themeAudit})(document.querySelector('#host'))`),{grounds:[],text:[]});
}));
