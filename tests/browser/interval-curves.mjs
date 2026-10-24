import {knownAtAssertion,observeCatalogClock} from './known-at.mjs';

// Interval curves screen: shaped, marked and resolved prices, lineage, aggregates, an unavailable curve, the largest window and the historical refusal.
export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,fitsWidth,errors,url}){
 const table=id=>`[...document.querySelectorAll('#${id} tbody tr')].map(r=>[...r.cells].map(c=>c.textContent))`;
 const values=table('interval-curves-values'),aggregates=table('interval-curves-aggregates');
 const text=id=>`document.querySelector('#${id}').textContent`;
 const show=async(curve,from,to)=>{await set('#interval-curves-select',curve);await set('#interval-curves-from',from);await set('#interval-curves-to',to);await submit('#interval-curves-form');};
 // Timed from an action until the page is idle and the next frame has painted.
 const timed=action=>evaluate(`new Promise(accept=>{const started=performance.now();${action};
  const settled=()=>document.body.getAttribute('aria-busy')==='false'?requestAnimationFrame(()=>setTimeout(()=>accept(Math.round(performance.now()-started)))):setTimeout(settled,20);setTimeout(settled,20);})`);
 await command('Runtime.enable');await command('Page.enable');await command('Page.navigate',{url:url+'/'});
 await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await check('whole-minute and nonzero-second controls preserve the exact server cutoff',`(async()=>{
  const {zonedInput}=await import('/user-time.js'),input=document.createElement('input');input.type='datetime-local';input.step='1';
  const control=zonedInput(input,()=> 'America/New_York',async()=>{throw Error('An unedited cutoff must retain its exact instant.');});
  for(const [instant,display] of [['2026-10-22T17:29:00.123456Z','2026-10-22T13:29'],['2026-10-22T17:29:37.123456Z','2026-10-22T13:29:37']]){
   control.set(instant);
   if(input.value!==display||!${knownAtAssertion('input','instant','America/New_York')}||await control.instant()!==instant)return false;
  }
  input.value='2026-10-22T13:29';
  if(${knownAtAssertion('input',"'2026-10-22T17:29:37Z'",'America/New_York')}||${knownAtAssertion('input',"'2026-10-22T17:29:00Z'",'UTC')})return false;
  input.value='';return !${knownAtAssertion('input',"'2026-10-22T17:29:00Z'",'America/New_York')};
 })()`);
 await observeCatalogClock(evaluate,'/api/interval-curves/catalog');
 await persona('viewer');
 await click('#profile-open');await set('#zone','America/New_York');await submit('#preferences-form');
 // Interval curves read the first market date's pinned marks; it values at 11:00 AM New York time on 15 December 2020.
 await set('#snapshot',await evaluate("[...document.querySelector('#snapshot').options].find(o=>o.value).value"));await wait(idle);
 await check('interval pricing is a view under Curves & prices, not a separate sidebar object',
  "document.querySelector('#curve-price-navigation').contains(document.querySelector('#nav-interval-curves'))&&!document.querySelector('#market-menu').contains(document.querySelector('#nav-interval-curves'))&&!document.querySelector('#curve-price-navigation').hidden");
 await click('#nav-interval-curves');
 await check('the page lists interval curves beside the market date, with Known at from the database clock',
  "!document.querySelector('#interval-curves-page').hidden&&document.querySelector('#page-title').textContent==='Curves & prices'&&!document.querySelector('#snapshot').closest('.date-control').hidden"+
  "&&[...document.querySelectorAll('#interval-curves-select option')].map(o=>o.value).filter(Boolean).join()==='test_power_hourly_marked,test_power_hourly_scaled,test_power_hourly_shaped,test_power_hourly_uncovered'"+
  `&&${knownAtAssertion("document.querySelector('#interval-curves-known')",'window.catalogKnownAt','America/New_York')}&&document.querySelector('#interval-curves-window').disabled&&document.querySelector('#error').hidden`,
  "({page:document.querySelector('#interval-curves-page').hidden,title:document.querySelector('#page-title').textContent,date:document.querySelector('#snapshot').closest('.date-control').hidden,options:[...document.querySelectorAll('#interval-curves-select option')].map(o=>o.value),known:document.querySelector('#interval-curves-known').value,window:document.querySelector('#interval-curves-window').disabled,error:document.querySelector('#error').textContent})");

 await set('#interval-curves-select','test_power_hourly_uncovered');
 await check('a curve that cannot resolve is listed as unavailable with its reason and cannot be read',
  `[...document.querySelectorAll('#interval-curves-select option')].some(o=>o.textContent==='Power Hourly Uncovered · unavailable')`+
  `&&${text('interval-curves-summary')}==='Unavailable · Calendar synthetic_eastern_holidays does not cover the whole delivery period.'&&document.querySelector('#interval-curves-window').disabled`,
  text('interval-curves-summary'));

 await show('test_power_hourly_shaped','2031-11-01T00:00','2031-12-01T00:00');
 await check('a shaped curve names its blocks, reads its shape at Known at and offers actual resolution',
  `${text('interval-curves-summary')}==='Shaped Additive from Power Offpeak Monthly (Off · pjm v1) and Power Peak Monthly (Peak · pjm v1) and Power Offpeak Balance (Off · pjm v1) and Power Peak Balance (Peak · pjm v1) · Synthetic Eastern · 60-minute intervals'`+
  "&&!document.querySelector('#interval-curves-known').disabled&&!document.querySelector('#interval-curves-resolve').disabled",text('interval-curves-summary'));
 await check('shaped November shows every hour with its block, shape revision and chart, the fall-back hour twice',
  `(()=>{const rows=${values},starts=rows.map(r=>r[0]);return rows.length===721&&new Set(starts).size===721&&starts.includes('Nov 2, 2031, 1:00 AM GMT-4')&&starts.includes('Nov 2, 2031, 1:00 AM GMT-5')`+
  "&&rows.every(r=>/^-?\\d+\\.\\d{6}$/.test(r[2]))&&rows.some(r=>r[3]==='Power Peak Monthly')&&rows.some(r=>r[4]==='2')&&!!document.querySelector('#interval-curves-chart canvas')&&document.querySelector('#error').hidden;})()",
  `({rows:${values}.slice(0,3),count:${values}.length,error:${text('error')},note:${text('interval-curves-note')},canvas:!!document.querySelector('#interval-curves-chart canvas')})`);
 await click('#interval-curves-values tbody tr:nth-child(2)');
 await check('choosing a row shows the block mark and shape revision behind its price',
  `${text('interval-curves-lineage')}.startsWith('Nov 1, 2031, 1:00 AM · ')&&${text('interval-curves-lineage')}.endsWith('the Power Offpeak Monthly block mark, shaped by shape revision 1')`,text('interval-curves-lineage'));

 await show('test_power_hourly_shaped','2020-12-15T10:00','2020-12-16T00:00');
 await click('#interval-curves-resolve');await submit('#interval-curves-form');
 await check('a shaped remainder has one unresolved estimate and live balance prices',
  `${values}.length===14&&${values}[0][5]==='Unresolved'&&${values}.slice(1).every(r=>r[5]==='Projected')&&document.querySelector('#error').hidden`,text('error'));
 await click('#interval-curves-values tbody tr:last-child');
 await check('a resolved shaped row names its live balance anchor',`${text('interval-curves-lineage')}.includes('Balance block mark')`,text('interval-curves-lineage'));
 await set('#interval-curves-select','test_power_hourly_marked');
 await check('a marked curve with a due rule offers resolution; Known at and Aggregate wait for it',
  "!document.querySelector('#interval-curves-resolve').disabled&&document.querySelector('#interval-curves-known').disabled&&document.querySelector('#interval-curves-aggregate').disabled&&document.querySelectorAll('#interval-curves-values tbody tr').length===0");
 await show('test_power_hourly_marked','2020-12-14T00:00','2020-12-15T00:00');
 await check('a plain read of delivered hours asks to resolve them',
  `${text('error')}==='This window includes intervals delivered by the market date. Choose Resolve to price them from collected actuals.'&&${values}.length===0`,text('error'));
 await click('#interval-curves-resolve');await submit('#interval-curves-form');
 await check('resolution prices the delivered day from its collected actuals, each with its due time',
  `(()=>{const rows=${values};return rows.length===24&&rows.every(r=>r[3]==='Actual')&&rows[0][2]==='35.000000'&&rows[0][4]==='Dec 14, 2020, 2:00 AM'&&document.querySelector('#error').hidden;})()`,
  `${values}.slice(0,3)`);
 await click('#interval-curves-values tbody tr:nth-child(1)');
 await check('an actual names its provider revision and knowledge time',`${text('interval-curves-lineage')}.includes('35.000000 · the collected actual, provider revision 2, known at ')`,text('interval-curves-lineage'));
 await set('#interval-curves-aggregate','day');await submit('#interval-curves-form');
 await check('a day aggregate keeps its native intervals',`${aggregates}.map(r=>r[0]+'|'+r[3]).join()==='Dec 14, 2020, 12:00 AM|24'&&${values}.length===24`,aggregates);
 await click('#interval-curves-aggregates tbody tr:first-child');
 await check('choosing an aggregate lists its native intervals and prices',
  `${text('interval-curves-lineage')}.startsWith('Dec 14, 2020, 12:00 AM to Dec 15, 2020, 12:00 AM · 24 intervals: Dec 14, 2020, 12:00 AM 35.000000, Dec 14, 2020, 1:00 AM 36.000000')`,
  text('interval-curves-lineage'));

 await set('#interval-curves-aggregate','');
 await set('#interval-curves-from','2020-12-15T10:00');await set('#interval-curves-to','2020-12-16T00:00');await submit('#interval-curves-form');
 await check('an hour ended before its actual is due is unresolved, and the live hours take their marks',
  `(()=>{const rows=${values};return rows.length===14&&rows[0][2]==='Unresolved'&&rows[0][3]==='Unresolved'&&rows.slice(1).every(r=>r[3]==='Projected');})()`,`${values}.slice(0,3)`);
 await click('#interval-curves-values tbody tr:first-child');
 await check('an unresolved hour names when its actual is due',`${text('interval-curves-lineage')}.endsWith('no eligible actual yet; it is due Dec 15, 2020, 12:00 PM')`,text('interval-curves-lineage'));
 await click('#interval-curves-values tbody tr:last-child');
 await check('a live interval names its pinned mark and when its actual is due',`${text('interval-curves-lineage')}.includes('the pinned mark of this interval; its actual is due Dec 16, 2020, 1:00 AM')`,text('interval-curves-lineage'));

 await set('#interval-curves-aggregate','hour');
 await set('#interval-curves-from','2031-11-02T00:00');await set('#interval-curves-to','2031-11-04T00:00');await submit('#interval-curves-form');
 await check('hours live at the market date keep their marks after their actuals arrive, and hour aggregates keep the repeated hour twice',
  `(()=>{const rows=${values},hours=${aggregates};return rows.length===49&&rows.every(r=>r[3]==='Projected')&&rows[1][0]==='Nov 2, 2031, 1:00 AM GMT-4'&&rows[2][0]==='Nov 2, 2031, 1:00 AM GMT-5'`+
  "&&hours.length===49&&hours.every(r=>r[3]==='1')&&hours.filter(r=>r[0].startsWith('Nov 2, 2031, 1:00 AM')).length===2;})()",`({rows:${values}.slice(0,3),hours:${aggregates}.slice(0,3)})`);

 // The largest window the synthetic shaped curve covers: November and December 2031.
 await click('#interval-curves-resolve');await set('#interval-curves-select','test_power_hourly_shaped');
 await set('#interval-curves-from','2031-11-01T00:00');await set('#interval-curves-to','2032-01-01T00:00');
 const shown=await timed("document.querySelector('#interval-curves-form').requestSubmit()");
 const next=await timed("document.querySelector('#interval-curves-values tbody tr:last-child').click()");
 console.log(`MEASURE interval curves shaped November and December window (1,465 hourly intervals): ${shown} ms from Show until painted, ${next} ms to choose its last interval`);
 await check('the largest window renders every hour, and both actions finish within 5 s',
  `${values}.length===1465&&${shown}<5000&&${next}<5000&&${text('interval-curves-lineage')}.startsWith('Dec 31, 2031, 11:00 PM · ')&&document.querySelector('#error').hidden`,text('interval-curves-lineage'));

 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await check('the interval curves page fits a 390 px phone',fitsWidth,
  "[...document.querySelectorAll('body *')].filter(e=>e.getBoundingClientRect().right>document.documentElement.clientWidth+1&&e.getClientRects().length).slice(0,8).map(e=>e.tagName+'#'+e.id+'.'+e.className+':'+Math.round(e.getBoundingClientRect().right))");
 await command('Emulation.clearDeviceMetricsOverride');

 // A historical view first asks for one of its market dates; choosing one reads, and is refused.
 await set('#date-context-on','2020-12-17');await click('#date-context-latest');await click('#apply-view-context');
 await set('#snapshot',await evaluate("[...document.querySelector('#snapshot').options].find(o=>o.value).value"));await wait(idle);
 await check('a historical view refuses interval curves with its own message and lists none',
  `${text('error')}==='Return to working data to read interval curves; their TOU and shape inputs are current definitions.'&&document.querySelectorAll('#interval-curves-select option').length===0`,text('error'));
 await click('#current-view-context');
 await set('#snapshot',await evaluate("[...document.querySelector('#snapshot').options].find(o=>o.value).value"));await wait(idle);
 await check('returning to working data and a market date lists the curves again',"document.querySelectorAll('#interval-curves-select option').length===5&&document.querySelector('#error').hidden",
  "({options:document.querySelectorAll('#interval-curves-select option').length,error:document.querySelector('#error').textContent})");
 await evaluate(`(async()=>{
  const {mountIntervalDataWidget}=await import('/interval-data-widget.js');
  const host=document.createElement('section');host.id='synthetic-interval-widget';
  const chart=document.createElement('div');chart.className='chart';const table=document.createElement('div');host.append(chart,table);document.body.append(host);
  window.widgetCalls=0;
  window.widgetData=()=>({scope:'synthetic-dashboard',title:'Synthetic meter readings',caption:'Explicit synthetic source',headers:['Interval','Value'],rows:[['First',String(window.widgetCalls)]],numeric:[1],
   chart:{title:'Synthetic meter readings',axis:'delivery',unit:'MWh',series:[{name:'Meter',points:[{x:'2031-01-01T00:00:00Z',y:String(window.widgetCalls)}]}]}});
  window.intervalWidget=mountIntervalDataWidget({table,chart,key:'synthetic-dashboard',busy:()=>false,load:async()=>{window.widgetCalls++;return window.widgetData();}});
  window.intervalWidget.show(window.widgetData());window.widgetCanvas=chart.querySelector('canvas');
 })()`);
 await click('#synthetic-interval-widget .interval-widget-controls button');
 await wait('window.widgetCalls===1&&document.querySelector("#synthetic-interval-widget tbody td:last-child").textContent==="1"');
 await check('reusable widget refreshes a supplied source while retaining its chart canvas',
  'document.querySelector("#synthetic-interval-widget canvas")===window.widgetCanvas');
 await set('#synthetic-interval-widget select','5000');
 await wait('window.widgetCalls===2');
 await evaluate('document.querySelector("#synthetic-interval-widget").hidden=true');
 await check('hidden widget suppresses manual refresh too',`(async()=>{await new Promise(r=>setTimeout(r,0));document.querySelector('#synthetic-interval-widget .interval-widget-controls button').click();await new Promise(r=>setTimeout(r,0));return window.widgetCalls===2;})()`);
 await evaluate('window.intervalWidget.dispose();document.querySelector("#synthetic-interval-widget").remove()');
 await check('no uncaught JavaScript exceptions',JSON.stringify(errors.length===0));
}
