import {test} from 'node:test';
import assert from 'node:assert/strict';
import {summaryTiles,contractCountText,displayDate,frameOptions,deliveryMonth,deliveryLabel,historyPriceLabel,displayInstant,timeLabels,publicText,publicColumns,quoteUnit,curveName,setCurveNames} from '../dist/market-view.js';
import {dataSections,referenceGroups} from '../dist/data-sections.js';
import {newPreferences,parsePreferences,loadPreferences,savePreferences} from '../dist/preferences.js';
const id='12345678-1234-1234-1234-123456789012';
const frame=(at,name='Initial synthetic inputs',suffix='')=>({id:id+suffix,name,at,observationIds:[]});
const workspace={references:{currencies:[{code:'currency',display_code:'TST'}],units:[{code:'energy',symbol:'E_A'}],quantity_specifications:[{code:'gas_energy',unit_code:'energy'}],quote_specifications:[{code:'quote',quantity_code:'gas_energy',currency_code:'currency',multiplier:'1'}]}};
test('market dates use minutes and retain exact server selection IDs',()=>{const frames=[frame('2020-12-18T16:00:13.123Z'),frame('2020-12-18T16:00:13.123Z','Edited inputs','b')];const opts=frameOptions(frames,'America/Chicago');assert.deepEqual(opts.map(o=>o.value),frames.map(f=>f.id));assert.ok(opts[0].name.includes('10:00 AM'));assert.ok(opts[0].name.endsWith('Original'));assert.ok(opts[1].name.endsWith('Correction 1'));assert.ok(!opts[0].name.includes(':13'));assert.ok(!opts[0].name.includes(id));});
test('changing zone changes labels but never input identities or ordering',()=>{const frames=[frame('2020-12-18T01:00:30Z'),frame('2020-12-18T01:00:30Z','Edited inputs','b')];const before=JSON.stringify(frames),utc=frameOptions(frames,'UTC'),chicago=frameOptions(frames,'America/Chicago');assert.deepEqual(utc.map(o=>o.value),chicago.map(o=>o.value));assert.notEqual(utc[0].name,chicago[0].name);assert.equal(JSON.stringify(frames),before);});
test('fall-back repeated local times show offsets rather than false versions',()=>{const opts=frameOptions([frame('2024-11-03T05:30:00Z'),frame('2024-11-03T06:30:00Z','Initial synthetic inputs','b')],'America/New_York');assert.ok(opts[0].name.includes('1:30 AM'));assert.ok(opts[0].name.endsWith('GMT-4'));assert.ok(opts[1].name.endsWith('GMT-5'));assert.ok(!opts.some(o=>/Original|version|Correction/.test(o.name)));});
test('real versions within a repeated local minute are still separate choices',()=>{const opts=frameOptions([frame('2024-11-03T05:30:00Z'),frame('2024-11-03T05:30:00Z','Edited inputs','b'),frame('2024-11-03T06:30:00Z','Initial synthetic inputs','c')],'America/New_York');assert.match(opts[0].name,/GMT-4 · Original$/);assert.match(opts[1].name,/GMT-4 · Correction 1$/);assert.match(opts[2].name,/GMT-5$/);});
test('history rows can distinguish DST folds without seconds',()=>{const labels=timeLabels(['2024-11-03T05:30:00Z','2024-11-03T06:30:00Z'],'America/New_York');assert.notEqual(labels[0],labels[1]);assert.ok(labels.every(l=>l.includes('1:30 AM')));});
test('seconds labels name the offset only where a repeated clock needs it',()=>{
 assert.deepEqual(timeLabels(['2026-11-01T05:30:00+00:00','2026-11-01T06:30:00+00:00','2026-11-01T07:30:00+00:00'],'America/New_York',true),['Nov 1, 2026, 1:30:00 AM GMT-4','Nov 1, 2026, 1:30:00 AM GMT-5','Nov 1, 2026, 2:30:00 AM']);
 assert.deepEqual(timeLabels(['2026-10-21T02:08:38.123456+00:00','2026-10-21T02:08:41+00:00'],'UTC',true),['Oct 21, 2026, 2:08:38 AM','Oct 21, 2026, 2:08:41 AM']);
 assert.equal(displayInstant('2026-10-21T02:08:38+00:00','UTC',true),'Oct 21, 2026, 2:08:38 AM');
 assert.equal(timeLabels(['2026-10-21T02:08:38+00:00'],'UTC')[0],'Oct 21, 2026, 2:08 AM');});
test('repeated instant labels reuse their formatters and give identical text',()=>{
 const values=['2026-11-01T05:30:00+00:00','2026-11-01T06:30:00+00:00','2026-11-01T07:30:00+00:00'];
 const calls=[[values,'America/New_York',false],[values,'America/New_York',true],[values,'UTC',false],[values,'UTC',true]];
 const first=calls.map(([v,zone,seconds])=>timeLabels(v,zone,seconds));
 const Original=Intl.DateTimeFormat;let built=0;
 Intl.DateTimeFormat=function(...args){built++;return new Original(...args);};
 try{for(let round=0;round<100;round++)calls.forEach(([v,zone,seconds],i)=>{assert.deepEqual(timeLabels(v,zone,seconds),first[i]);assert.equal(displayInstant(v[2],zone,seconds),first[i][2]);});}
 finally{Intl.DateTimeFormat=Original;}
 // Labelling cost must not grow with the number of instants: formatters are built once per zone and precision.
 assert.ok(built<=calls.length+2,`built ${built} formatters`);
 assert.deepEqual(first[1],['Nov 1, 2026, 1:30:00 AM GMT-4','Nov 1, 2026, 1:30:00 AM GMT-5','Nov 1, 2026, 2:30:00 AM']);
 assert.deepEqual(first[2],['Nov 1, 2026, 5:30 AM','Nov 1, 2026, 6:30 AM','Nov 1, 2026, 7:30 AM']);});
test('delivery month stays contractual across user time zones',()=>{assert.equal(deliveryMonth('2021-01-01'),'Jan 2021');assert.match(displayInstant('2021-01-01T00:00:00Z','America/Chicago'),/Dec 31, 2020/);});
test('daily, monthly and BALMO labels identify their exact delivery coverage',()=>{assert.equal(deliveryLabel('2026-01-16','2026-01-17'),'Jan 16, 2026');assert.equal(deliveryLabel('2026-02-01','2026-03-01'),'Feb 2026');assert.equal(deliveryLabel('2026-01-16','2026-02-01'),'Jan 16, 2026 – Jan 31, 2026');assert.throws(()=>deliveryLabel('2026-02-01','2026-02-01'));});
test('missing history prices retain explicit availability rather than displaying zero',()=>{assert.equal(historyPriceLabel({value:'0',unavailableReason:null}),'0');assert.equal(historyPriceLabel({value:null,unavailableReason:'source_expired'}),'Past marking cutoff');assert.equal(historyPriceLabel({value:null,unavailableReason:'missing_observation'}),'No saved price');assert.throws(()=>historyPriceLabel({value:null}));assert.throws(()=>historyPriceLabel({value:'3',unavailableReason:'source_expired'}));});
test('price-unit labels resolve currency and actual unit symbol',()=>{assert.equal(quoteUnit('quote',workspace),'TST / E_A');const copy=structuredClone(workspace);copy.references.quote_specifications[0].multiplier='100';assert.equal(quoteUnit('quote',copy),'TST / E_A × 100');});
test('missing unit relationships raise rather than guess a unit',()=>{const copy=structuredClone(workspace);copy.references.units=[];assert.throws(()=>quoteUnit('quote',copy),/missing or inconsistent/);});
test('all visible text redacts full UUIDs including multiple IDs',()=>{assert.equal(publicText(`Old ${id}, new ${id.toUpperCase()}`),'Old internal record, new internal record');});
test('reference tables omit every ID column and UUID-bearing value',()=>{const rows=[{id,catalog_id:id,revision_id:id,source_revision_id:id,code:'energy',symbol:'E_A',extra:id,other:id}];assert.deepEqual(publicColumns(rows).map(c=>c.key),['code','symbol']);});
test('profile settings survive reload independently per demo user',()=>{const map=new Map(),storage={getItem:k=>map.get(k)??null,setItem:(k,v)=>map.set(k,v)};const prefs={...newPreferences('America/Chicago'),browseBy:'marketType',market:'gas',marketType:'forwards',tab:'inputs'};savePreferences(storage,'viewer',prefs);assert.deepEqual(loadPreferences(storage,'viewer','UTC'),prefs);assert.equal(loadPreferences(storage,'editor','UTC').timeZone,'UTC');});
test('profile schema ignores unrelated fields and contains no market input choices',()=>{const prefs=newPreferences('UTC');assert.deepEqual(parsePreferences(JSON.stringify({...prefs,snapshotId:id,role:'editor',observations:[id]})),prefs);});
test('invalid time zone and unsupported preference versions are rejected',()=>{const prefs=newPreferences('UTC');for(const patch of [{timeZone:'invalid/zone'},{version:2},{browseBy:'prices'},{market:''},{market:7},{market:'x'.repeat(129)},{marketType:'guess'},{tab:'unknown'}])assert.throws(()=>parsePreferences(JSON.stringify({...prefs,...patch})));});
test('unavailable browser storage is surfaced instead of reporting success',()=>{assert.throws(()=>savePreferences({getItem:()=>null,setItem:()=>{throw Error('blocked');}},'viewer',newPreferences('UTC')),/blocked/);});

test('reference data groups business references and conventions as distinct curated sections',()=>{const available=['locations','units','currencies','clock_rules','quote_specifications','commodity_locations','revisions'];assert.deepEqual(dataSections(available).map(g=>g.sections.map(s=>s.value)),[['units','currencies'],['locations'],['clock_rules'],['quote_specifications']]);assert.deepEqual(dataSections(['units']).map(g=>g.name),['Commodities & units']);});
test('location coordinates have plain labels and preserve exact decimal text',()=>{const rows=[{id,latitude:'12.345678',longitude:'-45.678912'}];assert.deepEqual(publicColumns(rows),[{key:'latitude',name:'Latitude (°)'},{key:'longitude',name:'Longitude (°)'}]);assert.equal(publicText(rows[0].latitude),'12.345678');});

test('retired standalone convention catalogs are not menu choices',()=>{assert.ok(!referenceGroups.flatMap(g=>g.sections).some(s=>['day_count_conventions','compounding_conventions','fx_rate_definitions','heat_rate_definitions'].includes(s.value)));});

test('execution admission inspection has source-code labels and no tab of its own',()=>{
  assert.deepEqual(dataSections(['futures_execution_policies']),[]);
  assert.deepEqual(publicColumns([{eligible_from:'2026-01-05T09:00:00Z',qualification_source_code:'source',source_series_code:'source_contract',usage_policy:'sample'}]).map(c=>c.name),['Trading eligibility from','Qualification source','Source contract code','Usage policy']);
});


test('older saved profiles acquire only presentation layout defaults',()=>{const old={version:1,timeZone:'UTC',browseBy:'commodity',commodity:'all',marketType:'all',tab:'prices'};const prefs=parsePreferences(JSON.stringify(old));assert.deepEqual(prefs,newPreferences('UTC'));});
test('collapsed sidebar library and menus persist independently per persona',()=>{const entries=new Map(),storage={getItem:k=>entries.get(k)??null,setItem:(k,v)=>entries.set(k,v)},prefs={...newPreferences('UTC'),sidebarCollapsed:true,libraryCollapsed:true,collapsedMenus:['settings'],collapsedCurveGroups:['commodity|Gas']};savePreferences(storage,'viewer',prefs);assert.deepEqual(loadPreferences(storage,'viewer','UTC'),prefs);assert.deepEqual(loadPreferences(storage,'editor','UTC').collapsedMenus,[]);});
test('menus collapsed in the earlier sidebar are dropped when a saved profile loads',()=>{const load=collapsedMenus=>parsePreferences(JSON.stringify({...newPreferences('UTC'),browseBy:'marketType',collapsedMenus}));assert.deepEqual(load(['market-menu','trading-menu','settings']).collapsedMenus,['settings']);assert.deepEqual(load(['settings-menu']).collapsedMenus,[]);assert.equal(load(['market-menu']).browseBy,'marketType');});
test('invalid and unbounded layout preferences are rejected',()=>{for(const patch of [{sidebarCollapsed:'true'},{libraryCollapsed:1},{collapsedMenus:['unknown']},{collapsedMenus:['markets','markets']},{collapsedCurveGroups:['x'.repeat(129)]},{collapsedCurveGroups:Array.from({length:65},(_,i)=>String(i))}])assert.throws(()=>parsePreferences(JSON.stringify({...newPreferences('UTC'),...patch})));});
test('curve names come from the served directory and are never invented from codes',()=>{setCurveNames([{code:'future',name:'Synthetic futures'},{code:'rates',name:'Synthetic rates'},{code:'split',name:'One'},{code:'split',name:'Two'}]);assert.equal(curveName('future'),'Synthetic futures');assert.equal(curveName('rates'),'Synthetic rates');assert.throws(()=>curveName('missing_curve'),/missing or inconsistent/);assert.throws(()=>curveName('split'),/missing or inconsistent/);setCurveNames([]);assert.throws(()=>curveName('future'),/missing or inconsistent/);});
test('summary tiles and the contract count come from the backend summary, not from the order or number of points',()=>{
 const point=(from,to,value)=>({contractId:from,deliveryFrom:from,deliveryTo:to,value,lineage:[]});
 const futures={curve:'gas',points:[point('2021-03-01','2021-04-01','9.99'),point('2021-02-01','2021-03-01','8.88')],
  summary:{kind:'contracts',contractCount:24,front:{contractId:'k',deliveryFrom:'2021-01-01',deliveryTo:'2021-02-01',price:'3.49'},availability:null}};
 assert.deepEqual(summaryTiles(futures,'Follows this futures contract'),[
  {name:'Contracts',value:'24'},{name:'Front contract',value:deliveryLabel('2021-01-01','2021-02-01')},{name:'Front price',value:'3.49',numeric:true},{name:'Settlement',value:'Follows this futures contract'}]);
 assert.equal(contractCountText(futures),'24 contracts');
 assert.equal(contractCountText({...futures,summary:{...futures.summary,contractCount:1}}),'1 contract');
 const none={curve:'gas',points:[],summary:{kind:'contracts',contractCount:0,front:null,availability:'no_remaining_contracts'}};
 assert.deepEqual(summaryTiles(none,'S'),[{name:'Contracts',value:'None remaining'},{name:'Settlement',value:'S'}]);
 const unpriced={curve:'gas',points:[],summary:{kind:'contracts',contractCount:4,front:null,availability:'no_priced_contract'}};
 assert.deepEqual(summaryTiles(unpriced,'S'),[{name:'Contracts',value:'None priced'},{name:'Settlement',value:'S'}]);
 const rates={kind:'rates',curve:'r',points:[{maturity:'2030-01-01',value:'0',lineage:[]}],summary:{kind:'rates',maturityCount:3,firstMaturity:'2021-06-01',firstZeroRate:'1.25',dayCount:'ACT/365F'}};
 assert.deepEqual(summaryTiles(rates,'ignored'),[{name:'Maturities',value:'3'},{name:'First maturity',value:displayDate('2021-06-01')},{name:'First zero rate (%)',value:'1.25',numeric:true},{name:'Day count',value:'ACT/365F'}]);
 assert.equal(contractCountText(rates),'3 rate samples');
 assert.throws(()=>summaryTiles({curve:'gas',points:[]},'S'),/summary/);
});
