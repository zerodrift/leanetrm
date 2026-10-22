import {test} from 'node:test';
import assert from 'node:assert/strict';
import {knownUsageRejection,usageMessage,groupingName,usageRequest,readableReport,sectionLabels,memberLabel,usageTable,usageSummary,dailyChart,withoutCostNote,
  usageCsv,exportRows,reportBounds,exportPending} from '../dist/usage-settings.js';

const amount=(value,display=value)=>({amount:value,display});
const tokens=(counts,unknown)=>Object.fromEntries(['uncachedInput','cacheRead','cacheWrite','output','reasoning'].map((d,i)=>[d,{count:counts[i],unknownCalls:unknown[i]}]));
const turns=(changes={})=>({completed:2,failed:1,cancelled:0,interrupted:0,unfinished:1,other:2,answersPricedCost:amount('0.01123086'),answersWithoutCost:0,
  costPerAnswer:amount('0.005615430000000000','0.00561543'),otherPricedCost:amount('0.0003702'),...changes});
const figures=(changes={})=>({calls:{total:6,priced:4,withoutCost:2,unpriced:0,unknown:1,unrecognized:0,open:1},tokens:tokens(['5900','200','0','500','1234567'],[1,1,1,2,0]),
  pricedCost:amount('0.01160106'),pricedShare:'95.43',superseded:{calls:2,cost:amount('0.00802286')},turns:turns(),...changes});
const vendors=[{code:'anthropic',name:'Anthropic'}];
const usd={catalogId:'c1',revisionId:'r1',code:'usd'};
const label={...usd,displayCode:'USD',name:'US Dollar',catalogCode:'synthetic_reference',revisionKey:'initial_v1'};
const tests={member:{kind:'tests'},...figures({calls:{total:2,priced:2,withoutCost:0,unpriced:0,unknown:0,unrecognized:0,open:0},
  tokens:tokens(['50','0','0','50','0'],[0,0,0,0,0]),pricedCost:amount('1234.5','1,234.5'),pricedShare:'4.57',superseded:{calls:0,cost:null},turns:null})};
const section=(changes={})=>({currency:usd,label,total:figures({pricedShare:'100.00'}),rows:[{member:{kind:'agent',id:'a1',name:'Page guide'},...figures()},tests],
  daily:[{date:'2019-03-09',pricedCost:amount('0.003208'),calls:2,withoutCost:1},{date:'2019-03-10',pricedCost:null,calls:1,withoutCost:1},
    {date:'2019-03-11',pricedCost:amount('0'),calls:0,withoutCost:0}],...changes});
const report=(changes={})=>({reportFormat:'usage_report_v1',zone:'America/New_York',from:'2019-03-09',to:'2019-03-12',utcFrom:'2019-03-09T05:00:00+00:00',
  utcTo:'2019-03-12T04:00:00+00:00',by:'agent',generatedAt:'2026-10-21T12:00:00.123456+00:00',costMethod:'token_cost_v1',displayMethod:'token_cost_display_v1',
  ratioMethod:'token_cost_report_v1',days:[{date:'2019-03-09',from:'2019-03-09T05:00:00+00:00',to:'2019-03-10T05:00:00+00:00'},
    {date:'2019-03-10',from:'2019-03-10T05:00:00+00:00',to:'2019-03-11T04:00:00+00:00'},{date:'2019-03-11',from:'2019-03-11T04:00:00+00:00',to:'2019-03-12T04:00:00+00:00'}],
  currencies:[section()],vendors,...changes});

test('a report names its zone, its first day, the day after its last and its grouping, each as entered and none defaulted',()=>{
  assert.deepEqual(usageRequest({zone:'America/New_York',from:'2019-03-09',to:'2019-03-12',by:'agent'}),{zone:'America/New_York',from:'2019-03-09',to:'2019-03-12',by:'agent'});
  assert.throws(()=>usageRequest({zone:'',from:'2019-03-09',to:'2019-03-12',by:'agent'}),/Choose a time zone/);
  assert.throws(()=>usageRequest({zone:'UTC',from:'',to:'2019-03-12',by:'agent'}),/first day/);
  assert.throws(()=>usageRequest({zone:'UTC',from:'2019-03-09',to:'',by:'agent'}),/day after the last/);
  assert.throws(()=>usageRequest({zone:'UTC',from:'2019-03-09',to:'2019-03-12',by:''}),/model, user or agent/);
});

test('only a report in the format this page reads is shown or exported; any other is refused by name',()=>{
  const read=report();
  assert.equal(readableReport(read),read);
  for(const format of ['usage_report_v2',undefined,''])
    assert.throws(()=>readableReport(report({reportFormat:format})),/format this page does not read/);
  assert.throws(()=>readableReport(null),/format this page does not read/);
});

test('groupings come from a closed set, and an export is released only after a refusal that recorded nothing',()=>{
  assert.deepEqual(['model','user','agent'].map(groupingName),['Model','User','Agent']);
  assert.throws(()=>groupingName('currency'),/inconsistent/);
  for(const code of ['report_zone_invalid','report_range_invalid','model_admin_input','model_admin_conflict','access_denied','request_format','request_too_large','request_token'])
    assert.ok(knownUsageRejection(code),code);
  for(const code of [undefined,'','model_admin_outcome_unknown','request_cancelled','preview_error'])assert.ok(!knownUsageRejection(code),code);
});

test('the codes Usage shares with the model commands are stated in its own terms',()=>{
  assert.match(usageMessage('model_admin_input'),/model, user or agent/);
  assert.match(usageMessage('model_admin_conflict'),/request identity was already used/);
  assert.match(usageMessage('model_admin_outcome_unknown'),/Retry/);
  for(const code of [undefined,'report_zone_invalid','report_range_invalid','access_denied'])assert.equal(usageMessage(code),undefined);
});

test("a currency is labelled by the reader's scoped read as Models & prices labels it, naming its reference only where two totals would read the same",()=>{
  const alternate={currency:{...usd,revisionId:'r2'},label:{...label,revisionId:'r2',revisionKey:'usage_alternate'}};
  const euro={currency:{...usd,code:'eur'},label:null};
  assert.deepEqual(sectionLabels([section(),euro]),['USD · US Dollar','Currency not readable · eur']);
  assert.deepEqual(sectionLabels([section(),alternate]),['USD · US Dollar (synthetic_reference initial_v1 usd)','USD · US Dollar (synthetic_reference usage_alternate usd)']);
  // A reader who cannot read a reference sees no name for its revision, only which of the unreadable totals sharing its
  // code it is, in the report's order; the CSV carries each exact reference.
  assert.deepEqual(sectionLabels([section({label:null}),{...alternate,label:null}]),['Currency not readable · usd (reference 1 of 2)','Currency not readable · usd (reference 2 of 2)']);
  assert.deepEqual(sectionLabels([section(),{...alternate,label:null}]),['USD · US Dollar','Currency not readable · usd']);
});

test('a row names its member and nothing it holds; provider tests are their own row',()=>{
  assert.equal(memberLabel({kind:'model',id:'m1',vendor:'anthropic',identifier:'model-one',name:'Model one'},vendors),'Model one · model-one (Anthropic)');
  assert.equal(memberLabel({kind:'user',id:'u1',name:'Synthetic Ana',login:'synthetic_ana'},vendors),'Synthetic Ana (synthetic_ana)');
  assert.equal(memberLabel({kind:'agent',id:'a1',name:'Page guide'},vendors),'Page guide');
  assert.equal(memberLabel({kind:'tests'},vendors),'Provider tests');
  assert.throws(()=>memberLabel({kind:'model',id:'m1',vendor:'unlisted',identifier:'model-one',name:'Model one'},vendors),/inconsistent/);
  assert.throws(()=>memberLabel({kind:'currency'},vendors),/inconsistent/);
  assert.throws(()=>memberLabel(null,vendors),/inconsistent/);
});

test("each row shows the cost of its priced calls beside the calls without one, that cost's share, answers and their cost per answer, the other turns' priced cost, turns by state, tokens by dimension and the amounts apart",()=>{
  const {headers,rows,numeric}=usageTable(report(),section());
  assert.deepEqual(headers,['Agent','Calls','Cost of priced calls','Calls without a cost','Share of priced cost','Answers','Cost per answer',
    "Other turns' cost of priced calls",'Failed','Cancelled','Interrupted','Unfinished','Uncached input','Cache read','Cache write','Output','Reasoning','Unpriced',
    'Usage unknown','Usage not recognized','In progress','Superseded price']);
  assert.deepEqual(rows[0],['Page guide','6','0.01160106','2','95.43%','2','0.00561543','0.0003702','1','0','0','1','5,900 · 1 call unknown','200 · 1 call unknown',
    '0 · 1 call unknown','500 · 2 calls unknown','1,234,567','0','1','0','1','0.00802286 · 2 calls']);
  // Provider tests answer no question, so they have no turns.
  assert.deepEqual(rows[1],['Provider tests','2','1,234.5','0','4.57%','','','','','','','','50','0','0','50','0','0','0','0','0','None']);
  assert.deepEqual(numeric,Array.from({length:21},(_,i)=>i+1));
  assert.equal(usageTable(report({by:'user'}),section()).headers[0],'User');
});

test('a missing amount never shows as zero: no priced call, no answer, an answer without a cost and unknown tokens each say so',()=>{
  const unpriced={member:{kind:'agent',id:'a2',name:'Helper'},...figures({pricedCost:null,pricedShare:null,superseded:{calls:0,cost:null},
    tokens:tokens([null,'0','10','100','0'],[2,1,1,1,1]),turns:turns({completed:1,failed:0,cancelled:1,unfinished:0,other:1,answersPricedCost:null,answersWithoutCost:1,
      costPerAnswer:null,otherPricedCost:null})})};
  const quiet={member:{kind:'agent',id:'a3',name:'Quiet'},...figures({turns:turns({completed:0,failed:0,unfinished:0,other:0,answersPricedCost:null,costPerAnswer:null,
    otherPricedCost:null})})};
  const [helper,idle]=usageTable(report(),section({rows:[unpriced,quiet]})).rows;
  assert.deepEqual([helper[2],helper[4],helper[6],helper[7],helper[12],helper[21]],['No priced calls','','1 answer without a cost','No priced calls','Unknown','None']);
  assert.deepEqual([idle[5],idle[6],idle[7]],['0','No answers','No other turns']);
  // Whether there are other turns is the server's count, which the page takes as given.
  const counted={member:{kind:'agent',id:'a4',name:'Counted'},...figures({turns:turns({failed:0,unfinished:0,other:3,otherPricedCost:null})})};
  assert.equal(usageTable(report(),section({rows:[counted]})).rows[0][7],'No priced calls');
});

test("a currency's summary states its calls by usage state, the cost of its priced calls beside those without one, tokens, answers, the other turns' priced cost on its own line, turns by state and superseded prices",()=>{
  assert.deepEqual(usageSummary(section()),['6 calls: 4 priced, 0 unpriced, 1 with usage unknown, 0 with usage not recognized, 1 in progress.',
    'Cost of priced calls: 0.01160106; calls without a cost: 2.',
    'Tokens: uncached input 5,900 · 1 call unknown, cache read 200 · 1 call unknown, cache write 0 · 1 call unknown, output 500 · 2 calls unknown, reasoning 1,234,567.',
    'Answers: 2; cost per answer: 0.00561543.',"Other turns' cost of priced calls: 0.0003702.",'Turns: 2 completed, 1 failed, 0 cancelled, 0 interrupted, 1 unfinished.',
    'Priced by a superseded revision: 0.00802286 · 2 calls.']);
  const unknown=usageSummary(section({total:figures({calls:{total:1,priced:0,withoutCost:1,unpriced:0,unknown:1,unrecognized:0,open:0},pricedCost:null,
    turns:turns({other:0,otherPricedCost:null})})}));
  assert.deepEqual([unknown[0],unknown[1],unknown[4]],['1 call: 0 priced, 0 unpriced, 1 with usage unknown, 0 with usage not recognized, 0 in progress.',
    'Cost of priced calls: none; calls without a cost: 1.','Other turns: none.']);
});

test("the daily chart plots each day's exact cost of priced calls, a day with calls but no priced call has no bar, and the note names those days",()=>{
  const spec=dailyChart(section(),'USD · US Dollar');
  assert.deepEqual([spec.title,spec.subtitle,spec.axis,spec.kind,spec.unit],['Daily cost of priced calls','USD · US Dollar','category','bar','USD · US Dollar']);
  assert.deepEqual(spec.series.map(s=>s.points),[[{x:'2019-03-09',y:'0.003208'},{x:'2019-03-10',y:null},{x:'2019-03-11',y:'0'}]]);
  assert.equal(withoutCostNote(section()),'Calls without a cost: 2019-03-09 (1), 2019-03-10 (1). Each day shows the cost of its priced calls only.');
  assert.equal(withoutCostNote(section({daily:[{date:'2019-03-09',pricedCost:amount('1'),calls:1,withoutCost:0}]})),'');
});

test('the CSV carries its export identity, the zone, range, UTC bounds, format and methods, exact amounts, counts and tokens, the exact currency reference and ratios as report strings',()=>{
  const formula={member:{kind:'agent',id:'a1',name:'=Synthetic formula'},...figures({pricedCost:null,pricedShare:null})};
  const lines=usageCsv(report({currencies:[section({rows:[formula,tests]})]}),'o1').split('\r\n');
  for(const line of ['"Usage report"','"Export ID","o1"','"Report format","usage_report_v1"','"Time zone","America/New_York"','"From","2019-03-09"',
    '"To, not included","2019-03-12"','"UTC from","2019-03-09T05:00:00+00:00"','"UTC to","2019-03-12T04:00:00+00:00"','"Grouped by","Agent"',
    '"Generated","2026-10-21T12:00:00.123456+00:00"','"Cost method","token_cost_v1"','"Ratio method","token_cost_report_v1"'])assert.ok(lines.includes(line),line);
  const header=lines.findIndex(line=>line.startsWith('"Currency","Currency catalog","Currency revision","Currency code","Row","Member","Member ID","Calls"'));
  assert.ok(header>0);
  const cells=line=>line.slice(1,-1).split('","');
  const columns=cells(lines[header]),[member,provider,whole]=lines.slice(header+1,header+4).map(cells),at=name=>columns.indexOf(name);
  // A member name a spreadsheet could read as a formula is apostrophe-prefixed; a cost that does not exist is blank, never zero.
  assert.deepEqual([member[at('Member')],member[at('Member ID')],member[at('Row')],member[at('Cost of priced calls')],member[at('Calls without a cost')],
    member[at('Share of priced cost %')]],["'=Synthetic formula",'a1','Agent','','2','']);
  assert.deepEqual([member[at('Currency')],member[at('Currency catalog')],member[at('Currency revision')],member[at('Currency code')]],['USD · US Dollar','c1','r1','usd']);
  assert.deepEqual([member[at('Cost per answer')],member[at("Answers' cost of priced calls")],member[at('Other turns')],member[at("Other turns' cost of priced calls")],
    member[at('Superseded-price cost')]],['0.005615430000000000','0.01123086','2','0.0003702','0.00802286']);
  assert.deepEqual([member[at('Uncached input tokens')],member[at('Uncached input unknown calls')],member[at('Reasoning tokens')],member[at('Calls in progress')]],
    ['5900','1','1234567','1']);
  assert.deepEqual([provider[at('Row')],provider[at('Member')],provider[at('Cost of priced calls')],provider[at('Answers')]],['Provider tests','Provider tests','1234.5','']);
  assert.deepEqual([whole[at('Row')],whole[at('Cost of priced calls')],whole[at('Share of priced cost %')],whole[at('Answers')]],['Total','0.01160106','100.00','2']);
  // Each day carries its UTC bounds, the exact cost of its priced calls (blank when no call that day has one), its calls and those without a cost.
  const days=lines.indexOf('"Currency","Currency catalog","Currency revision","Currency code","Date","UTC from","UTC to","Cost of priced calls","Calls","Calls without a cost"');
  assert.ok(days>header);
  assert.deepEqual(lines.slice(days+1,days+4),['"USD · US Dollar","c1","r1","usd","2019-03-09","2019-03-09T05:00:00+00:00","2019-03-10T05:00:00+00:00","0.003208","2","1"',
    '"USD · US Dollar","c1","r1","usd","2019-03-10","2019-03-10T05:00:00+00:00","2019-03-11T04:00:00+00:00","","1","1"',
    '"USD · US Dollar","c1","r1","usd","2019-03-11","2019-03-11T04:00:00+00:00","2019-03-12T04:00:00+00:00","0","0","0"']);
  assert.equal(lines.at(-1),'');
});

test('the export audit lists when, by whom, the zone, the range and the grouping, and a retained export says what it would export',()=>{
  assert.deepEqual(exportRows([{operationId:'o1',at:'2026-10-21T12:00:00+00:00',actor:'Synthetic administrator',zone:'UTC',from:'2019-03-09',to:'2019-03-12',by:'user'}],
    at=>`at ${at}`),[['at 2026-10-21T12:00:00+00:00','Synthetic administrator','UTC','2019-03-09','2019-03-12','User']]);
  assert.equal(reportBounds(report(),at=>`at ${at}`),'America/New_York · 2019-03-09 up to 2019-03-12, not included · UTC 2019-03-09T05:00:00+00:00 to '
    +'2019-03-12T04:00:00+00:00 · by agent · generated at 2026-10-21T12:00:00.123456+00:00');
  assert.match(exportPending({zone:'UTC',from:'2019-03-09',to:'2019-03-12',by:'model'}),
    /^An export of usage in UTC from 2019-03-09 up to 2019-03-12, by model, has no confirmed outcome\. Retry sends the same request/);
  // The grouping is named by its label, so one this page does not offer cannot be described.
  assert.throws(()=>exportPending({zone:'UTC',from:'2019-03-09',to:'2019-03-12',by:'currency'}),/inconsistent/);
});
