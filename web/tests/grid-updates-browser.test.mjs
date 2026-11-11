import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
const page='<link rel="stylesheet" href="/vendor/tabulator.css"><link rel="stylesheet" href="/app.css"><div id="host" style="width:900px"></div>';
const ready="while(!grid.ready())await new Promise(r=>setTimeout(r,10));await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));";

test('presentation filters resolve the exact native column control without applying a filter',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {renderDataGrid,emptyGridLayout}=await import('/data-grid.js'),host=document.querySelector('#host');
  const grid=renderDataGrid(host,'presentation-filter','synthetic',['Book','Quantity'],[['Synthetic A','1'],['Synthetic B','2']],[1],()=>false,{keys:['book','quantity'],rowKeys:['a','b'],layout:emptyGridLayout(),onLayout(){}});${ready}
  const before=grid.state(),control=grid.presentationFilter('book'),result={label:control.getAttribute('aria-label'),kind:control.tagName,visible:control.getClientRects().length>0,before,after:grid.state(),unknown:grid.presentationFilter('missing')};grid.destroy();return result;
 })()`);
 assert.equal(result.label,'Filter Book');assert.equal(result.kind,'SELECT');assert.equal(result.visible,true);assert.deepEqual(result.before,result.after);assert.equal(result.unknown,null);
}));

test('revealing an open row clears its lost-selection notice and restores selection and focus',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {renderDataGrid,emptyGridLayout}=await import('/data-grid.js'),host=document.querySelector('#host');
  const grid=renderDataGrid(host,'reveal','synthetic',['Trade','Amount'],[['A','1'],['B','2']],[1],()=>false,{keys:['id','amount'],rowKeys:['a','b'],layout:emptyGridLayout(),onLayout(){},onRowOpen:()=>true});${ready}
  grid.selectRow(0);host.querySelector('[data-row-key=a]').focus();const search=host.querySelector('.table-search');search.value='B';search.dispatchEvent(new Event('input'));await grid.updated();
  await new Promise(resolve=>setTimeout(resolve,100));host.querySelector('.table-open-note button').click();await grid.updated();await new Promise(resolve=>setTimeout(resolve,100));
  const row=host.querySelector('[data-row-key=a]'),result={note:host.querySelector('.table-open-note').hidden,query:search.value,current:row.getAttribute('aria-current'),focus:document.activeElement===row};grid.destroy();return result;
 })()`);
 assert.deepEqual(result,{note:true,query:'',current:'true',focus:true});
}));

// A resize draws the rows again, detaching them without a focus event: a control the person is typing in keeps focus and its text.
test('a cell control keeps focus and its unsent text when a resize draws the rows again',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {renderDataGrid,emptyGridLayout}=await import('/data-grid.js'),host=document.querySelector('#host');
  const cells=(r,c)=>{if(r!==0||c!==1)return null;const input=document.createElement('input');input.className='synthetic-entry';return input;};
  const grid=renderDataGrid(host,'entry','synthetic',['Day','Quantity'],[['D1','1'],['D2','2']],[1],()=>false,{keys:['day','quantity'],rowKeys:['a','b'],layout:emptyGridLayout(),onLayout(){},cells});${ready}
  const input=host.querySelector('.synthetic-entry');input.value='990';input.focus();
  host.style.width='600px';await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));await grid.updated();
  const result={same:input===host.querySelector('.synthetic-entry'),value:input.value,focused:document.activeElement===input};grid.destroy();return result;
 })()`);
 assert.deepEqual(result,{same:true,value:'990',focused:true});
}));

test('incremental search reconciles changed explanatory text even when every raw cell is unchanged',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {renderDataGrid,emptyGridLayout}=await import('/data-grid.js'),host=document.querySelector('#host');
  const values=[['A','1'],['B','2']],options={keys:['id','amount'],rowKeys:['a','b'],initial:{query:'selected',columns:[]},layout:emptyGridLayout(),onLayout(){},patchValues:true,searchText:['selected','other']};
  const grid=renderDataGrid(host,'words','synthetic',['Trade','Amount'],values,[1],()=>false,options);${ready}
  await grid.reconfigure(values,{...options,searchText:['other','selected']});
  const keys=[...host.querySelectorAll('.tabulator-row[data-row-key]')].map(row=>row.dataset.rowKey);grid.destroy();return keys;
 })()`);
 assert.deepEqual(result,['b']);
}));

test('incremental values refresh changed SVG geometry and revision tooltips while retaining unchanged cells',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {renderDataGrid,emptyGridLayout}=await import('/data-grid.js');
  const host=document.querySelector('#host');let values=[['A','10'],['B','20']];
  const options=(revisions,widths)=>({keys:['id','amount'],rowKeys:['a','b'],layout:emptyGridLayout(),onLayout(){},patchValues:true,cells:(row,column)=>{
   const span=document.createElement('span');span.textContent=values[row][column];if(column===0)span.title='Revision '+revisions[row];else{const svg=document.createElementNS('http://www.w3.org/2000/svg','svg'),rect=document.createElementNS(svg.namespaceURI,'rect');rect.setAttribute('width',String(widths[row]));svg.append(rect);span.append(svg);}return span;}});
  const grid=renderDataGrid(host,'updates','synthetic',['Trade','Amount'],values,[1],()=>false,options([1,1],[10,20]));${ready}
  const retained=host.querySelector('[data-row-key=b] [tabulator-field=c0] span');values=[['A','10'],['B','40']];
  await grid.reconfigure(values,options([2,1],[5,20]));
  const result={width:host.querySelector('[data-row-key=a] rect').getAttribute('width'),revision:host.querySelector('[data-row-key=a] [tabulator-field=c0] span').title,kept:host.querySelector('[data-row-key=b] [tabulator-field=c0] span')===retained};grid.destroy();return result;
 })()`);
 assert.deepEqual(result,{width:'5',revision:'Revision 2',kept:true});
}));

test('incremental values reconcile numeric filters and sort order without replacing a surviving cell',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {renderDataGrid,emptyGridLayout}=await import('/data-grid.js');const host=document.querySelector('#host');
  const options={keys:['id','amount'],rowKeys:['a','b','c'],initial:{query:'',columns:[null,'>1']},layout:{...emptyGridLayout(),sort:[{key:'amount',direction:'asc'}]},onLayout(){},patchValues:true};
  const grid=renderDataGrid(host,'updates','synthetic',['Trade','Amount'],[['A','1'],['B','2'],['C','3']],[1],()=>false,options);${ready}
  const kept=host.querySelector('[data-row-key=c] [tabulator-field=c0] span');
  await grid.reconfigure([['A','4'],['B','0'],['C','3']],options);await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));
  const result={rows:[...host.querySelectorAll('.tabulator-row[data-row-key]')].filter(r=>r.getClientRects().length).map(r=>r.dataset.rowKey),kept:host.querySelector('[data-row-key=c] [tabulator-field=c0] span')===kept};grid.destroy();return result;
 })()`);
 assert.deepEqual(result,{rows:['c','a'],kept:true});
}));

test('hiding and resizing a standing grid preserves its row anchor, horizontal position and selection',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {renderDataGrid,emptyGridLayout}=await import('/data-grid.js'),host=document.querySelector('#host');host.style.height='350px';
  const grid=renderDataGrid(host,'retained-place','synthetic',['Trade','Description'],Array.from({length:200},(_,i)=>['Trade '+i,'Synthetic row '+i]),[],()=>false,{keys:['trade','description'],rowKeys:Array.from({length:200},(_,i)=>'row-'+i),fill:true,layout:{...emptyGridLayout(),widths:{trade:600,description:600}},onLayout(){}});${ready}
  const tick=()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))),holder=()=>host.querySelector('.tabulator-tableholder');
  const place=()=>{const h=holder(),top=h.getBoundingClientRect().top,row=[...h.querySelectorAll('.tabulator-row[data-row-key]')].find(r=>r.getBoundingClientRect().top>=top-.5);return {key:row?.dataset.rowKey,offset:row?row.getBoundingClientRect().top-top:0,left:h.scrollLeft};};
  holder().scrollTop=1600;holder().scrollLeft=120;grid.select('row-60');await tick();await tick();const before=place();
  host.style.display='none';await tick();host.style.width='1000px';await tick();host.style.display='block';host.style.visibility='hidden';holder().scrollTop=0;holder().scrollLeft=0;await tick();await tick();host.style.visibility='visible';grid.relayout();await grid.updated();await tick();await tick();const after=place(),selected=grid.selected();grid.destroy();return {before,after,selected};
 })()`);
 assert.equal(result.after.key,result.before.key);assert.ok(Math.abs(result.after.offset-result.before.offset)<2,JSON.stringify(result));assert.equal(result.after.left,result.before.left);assert.equal(result.selected,'row-60');
}));
