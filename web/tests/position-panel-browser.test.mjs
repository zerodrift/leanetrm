import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
const fixture=JSON.parse(readFileSync(new URL('./fixtures/desk.json',import.meta.url),'utf8'));

test('an option action retains its widget and one command while its current write permission is checked',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"><div id="head"></div><div id="tabs"></div><div id="body"><div id="view"></div></div></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {setupPositionPanel}=await import('/position-panel.js'),{readOptionView}=await import('/futures-option-view.js'),{positionRows}=await import('/desk-model.js'),{setCurveNames}=await import('/market-view.js');
  const fixture=${JSON.stringify(fixture)};setCurveNames(fixture.curveNames);const holding={...fixture.holdings[0],executedAt:'2026-11-04T00:00:00Z',booking:{tradeDate:'2026-11-04'},component:'option',actions:{exercise:{allowed:true,reason:null},assignment:{allowed:false,reason:'Synthetic rule'},instruction:{allowed:false,reason:'Synthetic rule'}}},row=positionRows([fixture.holdings[0]])[0];
  let permissionReads=0,release,calls=0;const writes=()=>++permissionReads===1?{enabled:true,reason:null}:new Promise(resolve=>release=resolve);
  const api=async(path)=>{if(path==='options/holding')return {tradeId:holding.tradeId,lots:'2',openLots:'2',events:[]};if(path==='options/exercise'){calls++;return {kind:'exercise',lots:'1',remainingLots:'1',futuresTradeId:null};}throw Error('Unexpected '+path);};
  const read=await readOptionView(api,{snapshotId:'synthetic'},holding,{writes,actor:()=> 'synthetic',recorded(){},surface:null});
  const formats=Object.fromEntries(['money','quantity','price','mark','markMinusPrice'].map(k=>[k,v=>v])),input={holding,row,formats,color:'#123456',risk:{state:'none'}};
  const panel=setupPositionPanel({element:part=>document.querySelector('#'+part),form(){},reveal(){},focusForm(){}},{zone:()=> 'UTC',busy:()=>false,canAmend:()=>false,curveListed:()=>false,context:()=> 'same',read:async()=>read,amendment:{shown:()=>false,open(){},close:async()=>{}},onDenied(){throw Error('Unexpected denial');}});
  const tick=()=>new Promise(resolve=>setTimeout(resolve,0));panel.show(input,'terms');await tick();const widget=document.querySelector('.option-lifecycle');
  if(!document.querySelector('#option-exercise-lots'))throw Error(document.querySelector('#view').textContent);
  document.querySelector('#option-exercise-lots').value='1';const editing=panel.updatesBlocked();document.querySelector('#option-exercise').click();await tick();panel.update(input);
  const held=panel.updatesBlocked(),retained=widget===document.querySelector('.option-lifecycle'),disabled=document.querySelector('#option-exercise').disabled;
  document.querySelector('#tabs-schedules').click();const ownTab=document.querySelector('#view').textContent.includes('No side schedule view is available')&&!document.querySelector('.option-lifecycle');
  document.querySelector('#tabs-terms').click();const restored=document.querySelector('#option-exercise').disabled&&!document.querySelector('#option-lifecycle-retry').hidden;
  release({enabled:true,reason:null});await tick();return {editing,held,retained,disabled,calls,ownTab,restored};
 })()`);
 assert.deepEqual(result,{editing:true,held:true,retained:true,disabled:true,calls:1,ownTab:true,restored:true});
}));

test('a completed optional curve cell retains its row identity for later visible refresh demand',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{const {deskCells}=await import('/desk-view.js'),{positionRows}=await import('/desk-model.js'),{setCurveNames}=await import('/market-view.js');const fixture=${JSON.stringify(fixture)};setCurveNames(fixture.curveNames);const rows=positionRows([fixture.holdings[0]]),cell=deskCells(rows,new Map([[rows[0].commodity,'#123456']]),{},()=> 'Synthetic unavailable shape',['curve'])(0,0);return {row:cell.dataset.shapeRow,shape:!!cell.querySelector('svg.sparkline')};})()`);
 assert.deepEqual(result,{row:'0',shape:true});
}));

test('equal schedules materialize after an authorized context change and retain their nodes on an unchanged repaint',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"><div id="head"></div><div id="tabs"></div><div id="body"><div id="view"></div></div></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {setupPositionPanel}=await import('/position-panel.js'),{positionRows}=await import('/desk-model.js'),{setCurveNames}=await import('/market-view.js');
  const fixture=${JSON.stringify(fixture)};setCurveNames(fixture.curveNames);const holding=fixture.holdings[0],row=positionRows([holding])[0],pending=[];
  const formats=Object.fromEntries(['money','quantity','price','mark','markMinusPrice'].map(k=>[k,v=>v]));let context='first',draws=0;
  const input={holding,row,formats,color:'#123456',risk:{state:'none'}},read={view:()=>({terms:[],history:[],scheduleKey:'equal-authorized-content',schedules:host=>{draws++;host.textContent='Payment schedule';return()=>{};}})};
  const panel=setupPositionPanel({element:part=>document.querySelector('#'+part),form(){},reveal(){},focusForm(){}},{zone:()=> 'UTC',busy:()=>false,canAmend:()=>false,curveListed:()=>false,context:()=>context,read:()=>new Promise(resolve=>pending.push(resolve)),amendment:{shown:()=>false,open(){},close:async()=>{}},onDenied(){throw Error('Unexpected denial');}});
  const tick=()=>new Promise(resolve=>setTimeout(resolve,0));panel.show(input,'schedules');pending.shift()(read);await tick();
  context='second';panel.update(input);pending.shift()(read);await tick();const retained=document.querySelector('.position-side-schedules');panel.update(input);
  return {text:document.querySelector('#view').textContent,draws,retained:retained===document.querySelector('.position-side-schedules')};
 })()`);
 assert.deepEqual(result,{text:'Payment schedule',draws:2,retained:true});
}));

for(const protectedWidget of [false,true])test(protectedWidget?'a pending operational widget survives a context replacement until its command releases it':'an unchanged read-only section keeps its node while a newer context is read',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"><div id="head"></div><div id="tabs"></div><div id="body"><div id="view"></div></div></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {setupPositionPanel}=await import('/position-panel.js'),{positionRows}=await import('/desk-model.js'),{setCurveNames}=await import('/market-view.js');
  const fixture=${JSON.stringify(fixture)};setCurveNames(fixture.curveNames);const holding=fixture.holdings[0],row=positionRows([holding])[0],requests=[];
  const formats=Object.fromEntries(['money','quantity','price','mark','markMinusPrice'].map(k=>[k,v=>v]));let context='first',blocked=${protectedWidget};
  const input={holding,row,formats,color:'#123456',risk:{state:'none'}},native=at=>({updatesBlocked:()=>blocked,view:()=>({terms:[{title:'Retained terms',rows:[['Trade','Synthetic contract']]}],history:[],actions:[{id:'manage',label:'Manage',run(){}}],...(${protectedWidget}?{termsContent:host=>{const entry=document.createElement('input');entry.id='operational';entry.value=at;host.append(entry);return()=>entry.remove();}}:{})})});
  const panel=setupPositionPanel({element:part=>document.querySelector('#'+part),form(){},reveal(){},focusForm(){}},{zone:()=> 'UTC',busy:()=>false,canAmend:()=>false,curveListed:()=>false,context:()=>context,read:()=>new Promise(resolve=>requests.push({at:context,resolve:()=>resolve(native(context))})),amendment:{shown:()=>false,open(){},close:async()=>{}},onDenied(){throw Error('Unexpected denial');}});
  const tick=()=>new Promise(resolve=>setTimeout(resolve,0));panel.show(input,'terms');requests[0].resolve();await tick();
  const section=document.querySelector('#view .side-panel-section'),entry=document.querySelector('#operational');if(entry)entry.value='Keep this entered note';
  context='second';panel.update(input);await tick();
  const during={same:section===document.querySelector('#view .side-panel-section'),retained:!entry||entry===document.querySelector('#operational')&&entry.value==='Keep this entered note',reads:requests.length,refreshing:document.querySelector('#view').textContent.includes('Refreshing position details')};
  if(${protectedWidget}){blocked=false;panel.update(input);await tick();}
  requests[1]?.resolve();await tick();
  return {during,reads:requests.map(r=>r.at),same:section===document.querySelector('#view .side-panel-section'),refreshing:document.querySelector('#view').textContent.includes('Refreshing position details'),widget:document.querySelector('#operational')?.value??null};
 })()`);
 assert.deepEqual(result.during,{same:true,retained:true,reads:protectedWidget?1:2,refreshing:!protectedWidget});
 assert.deepEqual(result.reads,['first','second']);
 assert.equal(result.same,true);assert.equal(result.refreshing,false);
 assert.equal(result.widget,protectedWidget?'second':null);
}));

test('operational schedule edits keep their widget until the shared leave guard allows a tab change',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"><div id="head"></div><div id="tabs"></div><div id="body"><div id="view"></div></div></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {setupPositionPanel}=await import('/position-panel.js'),{positionRows}=await import('/desk-model.js'),{setCurveNames}=await import('/market-view.js');
  const fixture=${JSON.stringify(fixture)};setCurveNames(fixture.curveNames);const holding=fixture.holdings[0],row=positionRows([holding])[0];let answer,asks=0,destroyed=0;
  const read={unsaved:()=>true,updatesBlocked:()=>true,view:()=>({terms:[],history:[],schedules:host=>{host.textContent='Synthetic edited schedule';return()=>destroyed++;}})};
  const panel=setupPositionPanel({element:part=>document.querySelector('#'+part),form(){},reveal(){},focusForm(){}},{zone:()=> 'UTC',busy:()=>false,leave:()=>{asks++;return new Promise(r=>answer=r);},canAmend:()=>false,curveListed:()=>false,context:()=> 'same',read:async()=>read,amendment:{shown:()=>false,open(){},close:async()=>{}},onDenied(){}});
  const formats=Object.fromEntries(['money','quantity','price','mark','markMinusPrice'].map(k=>[k,v=>v])),tick=()=>new Promise(r=>setTimeout(r,0));
  panel.show({holding,row,formats,color:'#123456',risk:{state:'none'}},'schedules');await tick();const widget=document.querySelector('.position-side-schedules');
  document.querySelector('#tabs-terms').click();const held=widget===document.querySelector('.position-side-schedules')&&document.querySelector('#tabs-schedules').getAttribute('aria-selected')==='true';
  answer(false);await tick();const kept=widget===document.querySelector('.position-side-schedules');document.querySelector('#tabs-terms').click();answer(true);await tick();
  return {asks,held,kept,destroyed,selected:document.querySelector('#tabs-terms').getAttribute('aria-selected')};
 })()`);
 assert.deepEqual(result,{asks:2,held:true,kept:true,destroyed:1,selected:'true'});

}));
