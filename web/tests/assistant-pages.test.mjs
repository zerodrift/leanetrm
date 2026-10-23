import {test} from 'node:test';
import assert from 'node:assert/strict';
test('a proposed curve waits for explicit review on an idle unchanged page',async()=>{
 const {targetAction}=await import('../dist/assistant-pages.js');
 assert.equal(targetAction({live:true,changesContext:false,unsaved:false,proposal:true}),'go');
});
import {pageReport,lookingAt,askKnowledge,changesContext,targetAction,NavigationHistory,stepLine,endingLine,usageLine,consentText,grantExpiry,webPage,agentChoice,preselect,conversationChoice,panelMessage,keptAsk} from '../dist/assistant-pages.js';

const book='d1000000-0000-4000-8000-000000000001',portfolio='d2000000-0000-4000-8000-000000000002',trade='71000000-0000-4000-8000-000000000009';
const context='c1000000-0000-4000-8000-000000000001',frame='f1000000-0000-4000-8000-000000000001',other='f2000000-0000-4000-8000-000000000002';
const blotter={mode:'trades',bookIds:[book],portfolioIds:[],groupBy:['contract'],measures:['lots']};

test('Curves & prices reports the directory with no curve, the curve on Prices and Dependencies, and its observations on Inputs',()=>{
  assert.deepEqual(pageReport({page:'market',curve:null,tab:'prices'}),{page:'curves',selection:{view:'curves',curve:null}});
  assert.deepEqual(pageReport({page:'market',curve:'future',tab:'prices'}),{page:'curves',selection:{view:'curve',curve:'future'}});
  assert.deepEqual(pageReport({page:'market',curve:'future',tab:'dependencies'}),{page:'curves',selection:{view:'curve',curve:'future'}});
  assert.deepEqual(pageReport({page:'market',curve:'future',tab:'inputs'}),{page:'curves',selection:{view:'observations',curve:'future'}});
});

test('Power TOUs reports power hours once an As of frame is chosen, and TOU products before',()=>{
  assert.deepEqual(pageReport({page:'power',frame:true}),{page:'power_tous',selection:{view:'power_hours'}});
  assert.deepEqual(pageReport({page:'power',frame:false}),{page:'power_tous',selection:{view:'tou_products'}});
});

test('every other page reports the view it shows in its read tool input form, and a page the assistant does not read an empty selection',()=>{
  assert.deepEqual(pageReport({page:'configuration'}),{page:'contract_definitions',selection:{view:'contract_definitions',curve:null}});
  assert.deepEqual(pageReport({page:'schedules'}),{page:'schedules',selection:{view:'library'}});
  assert.deepEqual(pageReport({page:'reference',table:'units',search:'mega'}),{page:'reference_data',selection:{table:'units',search:'mega'}});
  assert.deepEqual(pageReport({page:'parties',kind:'counterparty'}),{page:'parties',selection:{kind:'counterparty'}});
  // Trade rows name no grouping; the Blotter keeps its pivot choices for when totals are shown again.
  assert.deepEqual(pageReport({page:'futures',risk:false,trade:null,view:blotter}),{page:'blotter',selection:{view:'blotter',mode:'trades',bookIds:[book],portfolioIds:[],
    groupBy:[],measures:[],tradeId:null,instrumentType:null,revision:null}});
  assert.deepEqual(pageReport({page:'futures',risk:false,trade:null,view:{...blotter,mode:'pivot',portfolioIds:[portfolio]}}).selection,{view:'blotter',mode:'pivot',
    bookIds:[book],portfolioIds:[portfolio],groupBy:['contract'],measures:['lots'],tradeId:null,instrumentType:null,revision:null});
  assert.deepEqual(pageReport({page:'futures',risk:false,trade:{tradeId:trade,instrumentType:'future',revision:2,reference:'TR-000009'},view:blotter}),
    {page:'blotter',selection:{view:'trade',mode:null,bookIds:null,portfolioIds:null,groupBy:null,measures:null,tradeId:trade,instrumentType:'future',revision:2}});
  assert.deepEqual(pageReport({page:'futures',risk:true,trade:null,view:blotter}),{page:'blotter_risk',selection:{}});
  for(const [page,id] of [['delivery','delivery_prices'],['custom-curves','custom_curves'],['interval-series','interval_series'],['interval-curves','interval_curves'],
    ['jobs','jobs'],['admin','users_access'],['ai','settings_ai']])assert.deepEqual(pageReport({page}),{page:id,selection:{}});
});

test('custom curves describes the saved library as a supported page without sending unsaved edits',()=>{
 const report=pageReport({page:'custom-curves'});
 assert.deepEqual(report,{page:'custom_curves',selection:{}});
 const view=lookingAt(report,{mode:'current',frameId:frame,frameLabel:'18 Dec 2020'}, {curve:String,table:String});
 assert.equal(view.supported,true);assert.equal(view.askable,true);
 assert.equal(view.shows,'Published curve ingredients and saved curve library. Unsaved edits are not sent.');
 assert.equal(webPage('custom_curves'),'custom-curves');
});

test('an ask pins working data only at a chosen As of frame, and a historical view at its own context, with or without a frame',()=>{
  assert.deepEqual(askKnowledge({mode:'current',frameId:frame,frameLabel:'18 Dec 2020'}),{mode:'current',contextId:null,frameId:frame});
  assert.equal(askKnowledge({mode:'current',frameId:null,frameLabel:null}),undefined);
  assert.deepEqual(askKnowledge({mode:'retained',contextId:context,effectiveOn:'2020-12-18',knownAtLabel:'18 Dec 2020 18:00',frameId:null,frameLabel:null}),
    {mode:'retained',contextId:context,frameId:null});
});

test('Looking at names the page, what it shows and the context the next question will pin, or that the page is not read',()=>{
  const names={curve:code=>code==='future'?'Synthetic future':code,table:code=>code};
  const working={mode:'current',frameId:frame,frameLabel:'18 Dec 2020 16:00'};
  assert.deepEqual(lookingAt(pageReport({page:'market',curve:'future',tab:'inputs'}),working,names),
    {page:'Curves & prices',shows:'Inputs of Synthetic future',context:'Working data · As of 18 Dec 2020 16:00',supported:true,askable:true});
  assert.deepEqual(lookingAt(pageReport({page:'futures',risk:false,trade:{tradeId:trade,instrumentType:'future',revision:2,reference:'TR-000009'},view:blotter}),working,names,
    {trade:'TR-000009'}).shows,'Trade TR-000009, revision 2');
  assert.deepEqual(lookingAt(pageReport({page:'jobs'}),working,names),
    {page:'Jobs & Infrastructure',shows:'The assistant does not read this page.',context:'Working data · As of 18 Dec 2020 16:00',supported:false,askable:true});
  assert.deepEqual(lookingAt(pageReport({page:'schedules'}),{mode:'current',frameId:null,frameLabel:null},names),
    {page:'Schedules',shows:'Schedule library',context:'Working data · choose an As of date to ask about it',supported:true,askable:false});
  assert.equal(lookingAt(pageReport({page:'reference',table:'units',search:'mega'}),
    {mode:'retained',contextId:context,effectiveOn:'2020-12-18',knownAtLabel:'18 Dec 2020 18:00',frameId:null,frameLabel:null},names).context,
    'Historical view · 2020-12-18, as known at 18 Dec 2020 18:00 · no As of date chosen');
});

test('Looking at says what each read page shows in words, never with an identifier',()=>{
  const working={mode:'current',frameId:frame,frameLabel:'18 Dec 2020 16:00'},names={curve:code=>code==='future'?'Synthetic future':code,table:code=>code==='units'?'Units':code};
  const shows=state=>lookingAt(pageReport(state),working,names).shows;
  assert.equal(shows({page:'market',curve:null,tab:'prices'}),'Curve directory');
  assert.equal(shows({page:'market',curve:'future',tab:'dependencies'}),'Prices of Synthetic future');
  assert.equal(shows({page:'configuration'}),'Contract date definitions');
  assert.equal(shows({page:'power',frame:false}),'TOU products');
  assert.equal(shows({page:'power',frame:true}),'TOU products and power hours');
  assert.equal(shows({page:'reference',table:'units',search:''}),'Units');
  assert.equal(shows({page:'reference',table:'units',search:'mega'}),'Units · rows matching “mega”');
  // Before Reference data shows a table its report has none to name, so nothing can be asked from it yet.
  assert.deepEqual(lookingAt(pageReport({page:'reference',table:'',search:''}),working,names),
    {page:'Reference data',shows:'No table is shown yet',context:'Working data · As of 18 Dec 2020 16:00',supported:true,askable:false});
  assert.equal(shows({page:'parties',kind:'legal_entity'}),'Legal entities');
  assert.equal(shows({page:'parties',kind:'counterparty'}),'Counterparties');
  assert.equal(shows({page:'futures',risk:false,trade:null,view:blotter}),'Trade rows · 1 book');
  assert.equal(shows({page:'futures',risk:false,trade:null,view:{...blotter,bookIds:[]}}),'Trade rows · every book you can read');
  assert.equal(shows({page:'futures',risk:false,trade:null,view:{...blotter,mode:'pivot',bookIds:[],portfolioIds:[portfolio,book],groupBy:['contract','instrumentType']}}),
    'Totals by contract and instrument type · 2 portfolios');
  assert.deepEqual(lookingAt(pageReport({page:'futures',risk:true,trade:null,view:blotter}),working,names),
    {page:'Blotter',shows:'The assistant does not read risk results.',context:'Working data · As of 18 Dec 2020 16:00',supported:false,askable:true});
  // A historical selection that is not yet applied pins nothing the page shows, so nothing can be asked.
  assert.deepEqual(lookingAt(pageReport({page:'schedules'}),{mode:'unapplied'},names),
    {page:'Schedules',shows:'Schedule library',context:'Historical selection not applied · apply it to ask about it',supported:true,askable:false});
  assert.equal(askKnowledge({mode:'unapplied'}),undefined);
  assert.equal(changesContext({mode:'current',contextId:context,frameId:frame},{mode:'unapplied'}),true);
});

test('a target changes context when the pinned knowledge or As of frame differs from what the page has selected',()=>{
  const working={mode:'current',frameId:frame,frameLabel:''},historical={mode:'retained',contextId:context,effectiveOn:'2020-12-18',knownAtLabel:'',frameId:frame,frameLabel:''};
  const captured='c3000000-0000-4000-8000-000000000003';
  assert.equal(changesContext({mode:'current',contextId:captured,frameId:frame},working),false);
  assert.equal(changesContext({mode:'current',contextId:captured,frameId:other},working),true);
  assert.equal(changesContext({mode:'current',contextId:captured,frameId:frame},historical),true);
  assert.equal(changesContext({mode:'retained',contextId:context,frameId:frame},historical),false);
  assert.equal(changesContext({mode:'retained',contextId:captured,frameId:frame},historical),true);
  assert.equal(changesContext({mode:'retained',contextId:context,frameId:null},historical),true);
  assert.equal(changesContext({mode:'retained',contextId:context,frameId:frame},working),true);
});

test('only a live target that keeps the context and leaves nothing unsaved moves the page; any other waits for Go',()=>{
  assert.equal(targetAction({live:true,changesContext:false,unsaved:false}),'move');
  for(const held of [{live:false,changesContext:false,unsaved:false},{live:true,changesContext:true,unsaved:false},{live:true,changesContext:false,unsaved:true}])
    assert.equal(targetAction(held),'go');
});

test("Back restores the place a move left, for that move's notice only, and nothing after the history is cleared",()=>{
  const history=new NavigationHistory();
  const left={page:'market',report:pageReport({page:'market',curve:'future',tab:'prices'}),context:{mode:'current',frameId:frame,frameLabel:''}};
  const later={...left,report:pageReport({page:'schedules'})};
  const first=history.record(left),second=history.record(later);
  assert.notEqual(first,second);
  assert.deepEqual(history.origin(first),left);assert.deepEqual(history.origin(second),later);
  assert.equal(history.origin('elsewhere'),undefined);
  history.clear();
  assert.equal(history.origin(first),undefined);
});

test('a target opens the web page its report names',()=>{
  assert.deepEqual(['curves','contract_definitions','schedules','power_tous','reference_data','parties','blotter'].map(webPage),
    ['market','configuration','schedules','power','reference','parties','futures']);
  assert.throws(()=>webPage('settings_ai'),/page is inconsistent/);
});

test('step lines name the tool and, for a refusal or failure, say why in plain words, never with an identifier',()=>{
  assert.equal(stepLine({ordinal:1,tool:'read_market_data',code:null}),'Read market data');
  assert.equal(stepLine({ordinal:2,tool:'read_trades',code:'result_too_large'}),'Read trades · the result was larger than one read returns');
  assert.equal(stepLine({ordinal:3,tool:'navigate',code:'tool_arguments_invalid'}),'Navigate · the request was not one this tool accepts');
  assert.equal(stepLine({ordinal:4,tool:'read_current_page',code:'some_new_refusal'}),'Read the page · the tool refused this request');
  assert.equal(stepLine({ordinal:6,tool:'read_trades',code:'tool_failed'}),'Read trades · the read failed');
  assert.equal(stepLine({withheld:2}),'An earlier answer was left out: what it read can no longer be read for you');
  assert.throws(()=>stepLine({ordinal:5,tool:'delete_trades',code:null}),/page is inconsistent/);
});

test('an ending says why an answer stopped; a completed answer needs no line',()=>{
  assert.equal(endingLine({state:'completed',code:null,incomplete:false}),null);
  assert.equal(endingLine({state:'failed',code:'limit_elapsed',incomplete:true}),'The answer ran out of time.');
  assert.equal(endingLine({state:'failed',code:'some_new_failure',incomplete:false}),'The answer could not finish.');
  assert.equal(endingLine({state:'cancelled',code:null,incomplete:true}),'You stopped this answer.');
  assert.equal(endingLine({state:'cancelled',code:'session_ended',incomplete:false}),'The answer stopped because your session changed.');
  assert.equal(endingLine({state:'interrupted',code:null,incomplete:false}),'The answer was interrupted when the assistant restarted.');
  for(const code of ['limit_input_tokens','context_window_exceeded','limit_input_unverifiable'])
    assert.equal(endingLine({state:'failed',code,incomplete:false}),'This conversation is too long to continue. Start a new conversation.');
  assert.equal(endingLine({state:'cancelled',code:'access_changed',incomplete:false}),'The answer stopped because your grant to this agent ended.');
});

test('the usage line shows tokens by dimension and each currency\'s exact cost, unknown and unpriced as such',()=>{
  const counted=count=>({count,unknownCalls:0});
  const usage={calls:2,uncachedInput:counted(1234),cacheRead:counted(0),cacheWrite:counted(0),output:counted(56),reasoning:counted(12),
    cost:[{currency:{catalogId:context,revisionId:frame,code:'usd'},amount:'0.004125',unpricedCalls:0}]};
  // The currency is named by its reference code, exactly as the usage payload carries it.
  assert.equal(usageLine(usage),'Tokens: 1,234 input · 0 cache read · 0 cache write · 56 output (12 reasoning) · Cost: usd 0.004125');
  assert.equal(usageLine({...usage,output:{count:56,unknownCalls:1},cost:[{...usage.cost[0],amount:null,unpricedCalls:1}]}),
    'Tokens: 1,234 input · 0 cache read · 0 cache write · unknown output (12 reasoning) · Cost: usd unpriced');
  assert.equal(usageLine({...usage,cost:[...usage.cost,{currency:{catalogId:context,revisionId:other,code:'eur'},amount:'1.5',unpricedCalls:0}]}).split(' · Cost: ')[1],
    'usd 0.004125, eur 1.5');
  assert.equal(usageLine({...usage,calls:0,cost:[]}),'No model calls');
  assert.throws(()=>usageLine({...usage,cost:[]}),/page is inconsistent/);
});

test('consent names the agent, provider, model, reads and the date its grant would end in the person\'s zone',()=>{
  const agent={name:'Page guide',model:{vendor:'anthropic',identifier:'model-one',displayName:'Model one'},permissions:['ReadTradeParties','ReadMarketData','ReadTrades'],grantDays:28};
  const vendors=[{code:'anthropic',name:'Anthropic'}];
  assert.equal(consentText(agent,vendors,'2026-11-17'),'Allow Page guide, which sends your questions and the data it reads to '
    +'Anthropic (Model one) through the vendor account your administrators configured, whose owner may be able to see them, to read trades, trade '
    +'parties and market data on your behalf until 2026-11-17?');
  assert.throws(()=>consentText({...agent,model:{...agent.model,vendor:'unknown'}},vendors,'2026-11-17'),/page is inconsistent/);
  // A grant runs its days from the instant it is given, so the date is that instant's in the zone, not today's date plus days:
  // from 23:30 the evening before the spring change, 28 days later is already the next local day.
  assert.equal(grantExpiry(new Date('2026-10-20T12:00:00Z'),28,'UTC'),'2026-11-17');
  assert.equal(grantExpiry(new Date('2027-03-02T04:30:00Z'),28,'America/New_York'),'2027-03-30');
  assert.equal(grantExpiry(new Date('2026-12-20T12:00:00Z'),14,'Asia/Tokyo'),'2027-01-03');
});

test('the picker names each usable version with its provider, model and a Draft label, and preselects only a listed choice',()=>{
  const vendors=[{code:'anthropic',name:'Anthropic'}],model={id:context,version:1,vendor:'anthropic',identifier:'model-one',displayName:'Model one'};
  const published={id:book,name:'Page guide',version:2,draft:false,model,permissions:[],grantDays:28,grant:null},draft={...published,version:3,draft:true};
  assert.equal(agentChoice(published,vendors),'Page guide · version 2 · Anthropic Model one');
  assert.equal(agentChoice(draft,vendors),'Page guide · version 3 · Anthropic Model one · Draft');
  assert.throws(()=>agentChoice({...published,model:{...model,vendor:'elsewhere'}},vendors),/page is inconsistent/);
  assert.equal(preselect([published,draft],JSON.stringify({agentId:book,version:3})),draft);
  for(const saved of [null,'',JSON.stringify({agentId:book,version:4}),JSON.stringify({agentId:portfolio,version:2}),'not json',JSON.stringify({agentId:book,version:2,extra:1})])
    assert.equal(preselect([published,draft],saved),undefined);
});

test('a conversation reopens with its agent: the version its last question used while listed, or the only listed version of that agent',()=>{
  const model={id:context,version:1,vendor:'anthropic',identifier:'model-one',displayName:'Model one'};
  const v2={id:book,name:'Page guide',version:2,draft:false,model,permissions:[],grantDays:28,grant:null},v3={...v2,version:3,draft:true},other={...v2,id:portfolio,name:'Other'};
  assert.equal(conversationChoice([v2,v3,other],book,3),v3);
  assert.equal(conversationChoice([v2,other],book,3),v2);
  // Two listed versions and neither the one it last used: the person chooses.
  assert.equal(conversationChoice([v2,{...v3,version:4},other],book,3),undefined);
  assert.equal(conversationChoice([other],book,3),undefined);
});

test('the panel says why an ask, a grant or a read was refused in plain words, and leaves any other code to the page',()=>{
  assert.equal(panelMessage('turn_active'),'Wait for your current answer to finish, or stop it, before asking again.');
  assert.equal(panelMessage('snapshot_missing'),'Choose an As of date to ask about working data.');
  assert.equal(panelMessage('model_disabled'),panelMessage('credential_unavailable'));
  assert.equal(panelMessage('session_ended'),'Your session changed. Reload the page to continue.');
  assert.equal(panelMessage('access_changed'),'This agent no longer has the access an earlier answer in this conversation used, so it cannot continue it. '
    +'Start a new conversation, or ask an administrator to restore the agent\'s access.');
  assert.equal(panelMessage('some_page_code'),undefined);
  assert.equal(panelMessage(undefined),undefined);
});

test('a kept ask is read back from browser storage only in the exact form the panel stores, as a request whose outcome is unknown',()=>{
  const body={conversationId:null,agentId:book,version:2,question:'What moved?',pageReport:{page:'curves',selection:{view:'curves',curve:null}},
    knowledge:{mode:'current',contextId:null,frameId:frame}};
  const stored={operationId:trade,path:'assistant/turns/ask',body};
  assert.deepEqual(keptAsk(JSON.stringify(stored)),{...stored,uncertain:true});
  assert.deepEqual(keptAsk(JSON.stringify({...stored,body:{...body,conversationId:context}})).body.conversationId,context);
  for(const bad of [null,'','not json','[]',JSON.stringify({...stored,path:'assistant/grants/grant'}),JSON.stringify({...stored,operationId:'x'}),
    JSON.stringify({...stored,extra:1}),JSON.stringify({...stored,body:{...body,question:''}}),JSON.stringify({...stored,body:{...body,version:0}}),
    JSON.stringify({...stored,body:{...body,agentId:'../../x'}}),JSON.stringify({...stored,body:{...body,conversationId:'../x'}}),
    JSON.stringify({...stored,body:{...body,more:1}}),JSON.stringify({...stored,body:{...body,pageReport:{page:'curves'}}})])
    assert.equal(keptAsk(bad),undefined,bad);
});

test('PPA is an explicit unsupported assistant page without a financial read target',()=>{
 const report=pageReport({page:'ppa'});
 assert.deepEqual(report,{page:'ppa',selection:{}});
 const looking=lookingAt(report,{mode:'current',frameId:frame,frameLabel:'Synthetic frame'},{curve:x=>x,table:x=>x});
 assert.equal(looking.page,'PPA bookings');assert.equal(looking.supported,false);
 assert.equal(looking.shows,'The assistant does not read this page.');
 assert.throws(()=>webPage('ppa'));
});
