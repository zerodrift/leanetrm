import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage,pressKey} from './browser-page.mjs';
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
  await new Promise(resolve=>setTimeout(resolve,100));host.querySelector('.table-open-note button').click();await grid.updated();
  // The grid selects and focuses the row in one step, once it has drawn it: frames later, and a busy machine draws them late. The notice
  // clears in that step, so the row is read once it has cleared; a notice that never clears fails the check below.
  for(let n=0;n<1000&&!host.querySelector('.table-open-note').hidden;n++)await new Promise(resolve=>setTimeout(resolve,10));
  const row=host.querySelector('[data-row-key=a]'),result={note:host.querySelector('.table-open-note').hidden,query:search.value,current:row.getAttribute('aria-current'),focus:document.activeElement===row};grid.destroy();return result;
 })()`);
 assert.deepEqual(result,{note:true,query:'',current:'true',focus:true});
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

test('a grid resize preserves an editable cell and its focus without taking focus back from another control',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {renderDataGrid,emptyGridLayout}=await import('/data-grid.js'),host=document.querySelector('#host');
  const input=document.createElement('input');input.value='1000';input.setAttribute('aria-label','Scheduled quantity');
  const outside=document.createElement('input');outside.setAttribute('aria-label','Another field');document.body.append(outside);
  const grid=renderDataGrid(host,'editable-focus','synthetic',['Day','Quantity'],[['2041-02-01','1000']],[1],()=>false,{keys:['day','quantity'],rowKeys:['day'],layout:emptyGridLayout(),onLayout(){},cells:(_row,column)=>column===1?input:null});${ready}
  input.value='990';input.focus();input.setSelectionRange(1,2);
  if(document.activeElement!==input)throw Error('The quantity did not receive focus before resizing.');
  host.style.width='700px';grid.relayout();await grid.updated();await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));
  const kept={same:host.contains(input),value:input.value,focused:document.activeElement===input,selection:[input.selectionStart,input.selectionEnd]};
  input.focus();host.style.width='650px';grid.relayout();await grid.updated();outside.focus();await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));
  const moved=document.activeElement===outside;outside.blur();await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));
  const released=document.activeElement===document.body;grid.destroy();outside.remove();return {kept,moved,released};
 })()`);
 assert.deepEqual(result,{kept:{same:true,value:'990',focused:true,selection:[1,2]},moved:true,released:true});
}));

// A grid whose quantity cells are entries, as a booked ticket's day quantities are, with the entry of the first row in view at hand. The
// rows are scrolled as by the person's wheel, whose place the grid keeps.
const entryGrid=(days,scrollTop)=>`
  const {renderDataGrid,emptyGridLayout}=await import('/data-grid.js'),host=document.querySelector('#host'),tick=()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));
  const cells=(index,column)=>{if(column!==1)return null;const input=document.createElement('input');input.setAttribute('aria-label','Quantity of day '+(index+1));return input;};
  const rows=Array.from({length:${days}},(_,i)=>['Day '+(i+1),String(i+1)]);
  const grid=renderDataGrid(host,'cell-entry','synthetic',['Day','Quantity'],rows,[1],()=>false,{keys:['day','quantity'],rowKeys:rows.map(row=>row[0]),layout:emptyGridLayout(),onLayout(){},cells});${ready}
  const holder=host.querySelector('.tabulator-tableholder'),search=host.querySelector('.table-search');holder.dispatchEvent(new WheelEvent('wheel',{bubbles:true}));holder.scrollTop=${scrollTop};await tick();
  const top=holder.getBoundingClientRect().top,row=[...holder.querySelectorAll('.tabulator-row[data-row-key]')].find(row=>row.getBoundingClientRect().top>=top),input=row.querySelector('input');`;
// The person types in the entry; the grid's width then changes, which draws its rows again: each row is taken out of the page and the same
// row is put back. `leaving` has the entry answer losing focus by moving it to the row search, as a consumer's own handler can.
// `keyAsDrawn` presses a key the moment the rows are back, before the next frame: it goes to whatever has focus then, as a person's key does.
const typingThroughWidthChange=(days,scrollTop,{leaving=false,keyAsDrawn=false}={})=>`(async()=>{${entryGrid(days,scrollTop)}
  input.focus();input.value='990';if(${leaving})input.onblur=()=>search.focus();await tick();
  let redrawn=false,keyTo=null;new MutationObserver(records=>{
   if(records.some(record=>[...record.removedNodes].includes(row)))redrawn=true;
   if(${keyAsDrawn}&&keyTo===null&&records.some(record=>[...record.addedNodes].includes(row))){const at=document.activeElement;keyTo=at===input?'entry':'elsewhere';at.dispatchEvent(new KeyboardEvent('keydown',{key:'0',bubbles:true}));}
  }).observe(row.parentElement,{childList:true});
  host.style.width='420px';for(let i=0;i<300&&!redrawn;i++)await new Promise(r=>setTimeout(r,10));
  await grid.updated();await tick();await tick();
  const result={redrawn,scrolled:holder.scrollTop>0,drawn:input.isConnected,value:input.value,focus:document.activeElement===input?'entry':document.activeElement===search?'search':'nothing',...(${keyAsDrawn}?{keyTo}:{})};grid.destroy();return result;
 })()`;
// These pages behave as a person's focused window does. A window behind another reports no focus events at all, though its focused
// control still takes what is typed once the person is back.
const behindAnotherWindow=command=>command('Emulation.setFocusEmulationEnabled',{enabled:false});

// Rows the person scrolled are also put back at their place after the change, which draws them once more.
for(const [where,days,scrollTop] of [['a cell',2,0],['a cell of scrolled rows',40,600]])for(const behind of [false,true])
 test(`a control drawn in ${where} keeps focus and its typed value when a change of width draws the rows again${behind?', in a window behind another':''}`,{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async(evaluate,command)=>{
  if(behind)await behindAnotherWindow(command);
  assert.deepEqual(await evaluate(typingThroughWidthChange(days,scrollTop)),{redrawn:true,scrolled:scrollTop>0,drawn:true,value:'990',focus:'entry'});
 }));

// In a focused window the grid also hears the loss and gives focus back on the next frame, which a key can come before.
test('a key pressed the moment the rows are drawn again goes to the cell control that had focus, which keeps it',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 assert.deepEqual(await evaluate(typingThroughWidthChange(2,0,{keyAsDrawn:true})),{redrawn:true,scrolled:false,drawn:true,value:'990',focus:'entry',keyTo:'entry'});
}));

test('focus that moved on from a cell control as its row was drawn again stays where it went',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 assert.deepEqual(await evaluate(typingThroughWidthChange(2,0,{leaving:true})),{redrawn:true,scrolled:false,drawn:true,value:'990',focus:'search'});
}));

// The browser reports a change of text typed in an entry to it once, when the entry loses focus or Enter is pressed in it, and so when a
// redraw takes the entry's row out of the page. `heard` is what the entry was told of. Text a script set, as above, is never reported.
const typedEntry=async(evaluate,command,days,scrollTop,leaving=false)=>{
 await evaluate(`(async()=>{${entryGrid(days,scrollTop)}
  Object.assign(window,{grid,holder,search,row,input,tick,heard:[]});input.onchange=()=>heard.push(input.value);if(${leaving})input.onblur=()=>search.focus();input.focus();
 })()`);
 await command('Input.insertText',{text:'99'});
 assert.equal(await evaluate('input.value'),'99');
};
// The grid's width changes, which draws its rows again: each row is taken out of the page and the same row is put back.
const widthChange=`
  let redrawn=false;new MutationObserver(records=>{if(records.some(record=>[...record.removedNodes].includes(row)))redrawn=true;}).observe(row.parentElement,{childList:true});
  document.querySelector('#host').style.width='420px';for(let i=0;i<300&&!redrawn;i++)await new Promise(r=>setTimeout(r,10));
  await grid.updated();await tick();await tick();`;
const afterWidthChange=`(async()=>{${widthChange}return {redrawn,scrolled:holder.scrollTop>0,drawn:input.isConnected,value:input.value,focus:document.activeElement===input?'entry':document.activeElement===search?'search':'nothing',heard:[...heard]};})()`;

// The person enters what an entry holds by Enter in it, or by moving focus on, as Tab does.
for(const [where,days,scrollTop] of [['a cell',2,0],['a cell of scrolled rows',40,600]])for(const key of ['Enter','Tab'])
 test(`text typed in ${where} is reported to its control when ${key} enters it, not when a change of width draws the rows again`,{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async(evaluate,command)=>{
  await typedEntry(evaluate,command,days,scrollTop);
  assert.deepEqual(await evaluate(afterWidthChange),{redrawn:true,scrolled:scrollTop>0,drawn:true,value:'99',focus:'entry',heard:[]});
  await pressKey(command,key);
  assert.deepEqual(await evaluate('heard'),['99']);
 }));

for(const key of ['Enter','Tab'])
 test(`text typed in a cell on both sides of a redraw is reported to its control once, whole, when ${key} enters it`,{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async(evaluate,command)=>{
  await typedEntry(evaluate,command,2,0);await evaluate(afterWidthChange);
  await command('Input.insertText',{text:'0'});await pressKey(command,key);
  assert.deepEqual(await evaluate('heard'),['990']);
 }));

test('text typed in a cell control that lost focus to another as its row was drawn again is reported to it then',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async(evaluate,command)=>{
 await typedEntry(evaluate,command,2,0,true);
 assert.deepEqual(await evaluate(afterWidthChange),{redrawn:true,scrolled:false,drawn:true,value:'99',focus:'search',heard:['99']});
}));

// Rows the consumer replaces are drawn with new controls: the one typed in is gone for good, and what it held is reported to it once.
test('text typed in a cell control that its rows\' replacement removes is reported to it once',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async(evaluate,command)=>{
 await typedEntry(evaluate,command,2,0);
 assert.deepEqual(await evaluate(`(async()=>{
  await grid.replaceRows([['Day 1','1'],['Day 2','2']],undefined,['Day 1','Day 2']);await tick();
  const replaced={drawn:input.isConnected,heard:[...heard]};window.row=holder.querySelector('.tabulator-row[data-row-key]');${widthChange}
  return {replaced,redrawn,heard};
 })()`),{replaced:{drawn:false,heard:['99']},redrawn:true,heard:['99']});
}));
