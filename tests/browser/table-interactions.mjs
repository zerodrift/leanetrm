// Exercise real pointer gestures: HTMLElement.click does not model selection or scrolling.
export async function run({command,evaluate,wait,check,shot,idle,url}){
 const frames='new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))';
 await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);
 await evaluate(`(async()=>{const {renderDataGrid,emptyGridLayout}=await import('/data-grid.js');const host=document.createElement('div');host.id='gesture-grid';host.className='table-workspace';host.style='position:fixed;inset:24px;z-index:100;background:white;padding:16px';document.body.append(host);window.__opened=[];window.__doubleOpened=[];window.__grid=renderDataGrid(host,'gestures','synthetic',['Trade ID','Description','Exact amount'],Array.from({length:100},(_,i)=>['TR-'+i,'Selectable synthetic position '+i,'100000000000000000.000000000000000001']),[2],()=>false,{keys:['id','description','amount'],layout:{...emptyGridLayout(),widths:{id:400,description:700,amount:500}},onLayout:()=>{},onRowOpen:i=>window.__opened.push(i),onRowDoubleOpen:i=>window.__doubleOpened.push(i)});})()`);
 await wait('window.__grid.ready()');
 const cell=await evaluate("(()=>{const n=document.querySelector('#gesture-grid .tabulator-row [tabulator-field=c1]'),r=n.getBoundingClientRect();return {x:r.left+12,y:r.top+r.height/2};})()");
 await command('Input.dispatchMouseEvent',{type:'mousePressed',...cell,button:'left',clickCount:1});
 for(let d=10;d<=110;d+=10)await command('Input.dispatchMouseEvent',{type:'mouseMoved',x:cell.x+d,y:cell.y,buttons:1});
 await command('Input.dispatchMouseEvent',{type:'mouseReleased',x:cell.x+110,y:cell.y,button:'left',clickCount:1});
 await check('selecting row text does not open an editor','window.__opened.length===0');
 await evaluate('getSelection().removeAllRanges()');
 const holder=await evaluate("(()=>{const r=document.querySelector('#gesture-grid .tabulator-tableholder').getBoundingClientRect();return {x:r.left+150,y:r.top+100};})()");
 await command('Input.dispatchMouseEvent',{type:'mouseMoved',...holder});await command('Input.dispatchMouseEvent',{type:'mouseWheel',...holder,deltaX:0,deltaY:650});
 await wait("document.querySelector('#gesture-grid .tabulator-tableholder').scrollTop>300");
 await check('wheel scrolling retains rows without activating them',"window.__opened.length===0&&document.querySelectorAll('#gesture-grid .tabulator-row').length>0");
 const visible=await evaluate("(()=>{const h=document.querySelector('#gesture-grid .tabulator-tableholder').getBoundingClientRect();const n=[...document.querySelectorAll('#gesture-grid .tabulator-row')].find(n=>{const r=n.getBoundingClientRect();return r.top>h.top+10&&r.bottom<h.bottom-10;});const r=n.querySelector('[tabulator-field=c0]').getBoundingClientRect();return {x:r.left+12,y:r.top+r.height/2,index:Number(n.querySelector('[tabulator-field=c0]').textContent.slice(3))};})()");
 await command('Input.dispatchMouseEvent',{type:'mousePressed',x:visible.x,y:visible.y,button:'left',clickCount:1});await command('Input.dispatchMouseEvent',{type:'mouseReleased',x:visible.x,y:visible.y,button:'left',clickCount:1});
 await check('pointer click after virtual scrolling opens the displayed source row',`JSON.stringify(window.__opened)===${JSON.stringify(JSON.stringify([visible.index]))}`);
 await evaluate("(()=>{const h=document.querySelector('#gesture-grid .tabulator-tableholder').getBoundingClientRect();[...document.querySelectorAll('#gesture-grid .tabulator-row')].find(n=>{const r=n.getBoundingClientRect();return r.top>h.top+10&&r.bottom<h.bottom-10;}).focus();})()");
 await command('Input.dispatchKeyEvent',{type:'keyDown',key:'Enter',code:'Enter',windowsVirtualKeyCode:13});await command('Input.dispatchKeyEvent',{type:'keyUp',key:'Enter',code:'Enter',windowsVirtualKeyCode:13});
 await check('keyboard opening remains available after scrolling and selection',`JSON.stringify(window.__opened)===${JSON.stringify(JSON.stringify([visible.index,visible.index]))}`);
 const doubleClick=async (point,modifiers=0)=>{for(const clickCount of [1,2]){await command('Input.dispatchMouseEvent',{type:'mousePressed',x:point.x,y:point.y,button:'left',clickCount,modifiers});await command('Input.dispatchMouseEvent',{type:'mouseReleased',x:point.x,y:point.y,button:'left',clickCount,modifiers});}};
 await doubleClick(visible);
 await check('a native row double click opens the separate view once without duplicate inline opens or word selection',
  `JSON.stringify(window.__opened)===${JSON.stringify(JSON.stringify([visible.index,visible.index,visible.index]))}&&JSON.stringify(window.__doubleOpened)===${JSON.stringify(JSON.stringify([visible.index]))}&&getSelection().isCollapsed`,
  "({single:window.__opened,double:window.__doubleOpened,selection:getSelection().toString()})");
 const control=await evaluate("(()=>{const n=[...document.querySelectorAll('#gesture-grid .tabulator-row')].find(r=>Number(r.querySelector('[tabulator-field=c0]').textContent.slice(3))==="+visible.index+");const button=document.createElement('button');button.textContent='Synthetic row action';n.querySelector('[tabulator-field=c1]').append(button);const r=button.getBoundingClientRect();return{x:r.left+r.width/2,y:r.top+r.height/2};})()");
 await doubleClick(control);
 await check('double clicking a row control does not open either position view',`window.__opened.length===3&&window.__doubleOpened.length===1`);
 await doubleClick(visible,2);
 await check('a modified row double click does not open either position view','window.__opened.length===3&&window.__doubleOpened.length===1');
 await evaluate("getSelection().removeAllRanges();const n=[...document.querySelectorAll('#gesture-grid .tabulator-row')].find(r=>Number(r.querySelector('[tabulator-field=c0]').textContent.slice(3))==="+visible.index+").querySelector('[tabulator-field=c0]');const r=document.createRange();r.selectNodeContents(n);getSelection().addRange(r)");
 await doubleClick(visible);
 await check('clearing previously selected row text does not also open a position view','window.__opened.length===3&&window.__doubleOpened.length===1');
 await evaluate('getSelection().removeAllRanges()');
 const different=await evaluate("(()=>{const h=document.querySelector('#gesture-grid .tabulator-tableholder').getBoundingClientRect();const n=[...document.querySelectorAll('#gesture-grid .tabulator-row')].find(n=>{const r=n.getBoundingClientRect();return Number(n.querySelector('[tabulator-field=c0]').textContent.slice(3))!=="+visible.index+"&&r.top>h.top+10&&r.bottom<h.bottom-10;});const r=n.querySelector('[tabulator-field=c0]').getBoundingClientRect();return {x:r.left+12,y:r.top+r.height/2};})()");
 for(const [point,clickCount] of [[visible,1],[different,2]]){await command('Input.dispatchMouseEvent',{type:'mousePressed',x:point.x,y:point.y,button:'left',clickCount});await command('Input.dispatchMouseEvent',{type:'mouseReleased',x:point.x,y:point.y,button:'left',clickCount});}
 await check('a second click on a different virtual row cannot open that row in a separate view','window.__opened.length===4&&window.__doubleOpened.length===1');
 await evaluate('getSelection().removeAllRanges()');

 await evaluate("document.querySelector('#gesture-grid .tabulator-tableholder').scrollLeft=180");
 await wait("document.querySelector('#gesture-grid .tabulator-tableholder').scrollLeft>0");
 await evaluate(frames);
 await check('horizontal scrolling keeps column headers aligned with cells',"(()=>{const c=document.querySelector('#gesture-grid .tabulator-row [tabulator-field=c1]').getBoundingClientRect(),h=document.querySelector('#gesture-grid [role=columnheader][tabulator-field=c1]').getBoundingClientRect();return Math.abs(c.left-h.left)<2;})()");
 await evaluate("document.querySelector('#gesture-grid .column-filter-toggle').click();const f=document.querySelector('#gesture-grid input[aria-label=\"Filter Exact amount\"]');f.value='> 100000000000000000';f.dispatchEvent(new Event('input',{bubbles:true}));");
 await check('numeric grid filters compare exact amounts beyond browser number precision',"document.querySelector('#gesture-grid .table-row-count').textContent==='100 of 100 rows'&&window.__grid.visibleRows().length===100");
 await evaluate("(()=>{const f=document.querySelector('#gesture-grid input[aria-label=\"Filter Exact amount\"]');f.value='<= 100000000000000000';f.dispatchEvent(new Event('input',{bubbles:true}));})();");
 await check('numeric filters retain exact inequalities in exports and filter state',"document.querySelector('#gesture-grid .table-row-count').textContent==='0 of 100 rows'&&window.__grid.visibleRows().length===0&&window.__grid.state().columns[2]==='<= 100000000000000000'");
 await shot('table-gestures');
 await evaluate("window.__grid.destroy();document.querySelector('#gesture-grid').remove()");
 // A page scrollbar or window resize redraws the grid at another width after a person resizes a column.
 await evaluate(`(async()=>{const {renderDataGrid,emptyGridLayout}=await import('/data-grid.js');const host=document.createElement('div');host.id='resize-grid';host.className='table-workspace';host.style='position:fixed;left:24px;top:24px;width:900px;z-index:100;background:white;padding:16px';document.body.append(host);window.__grid=renderDataGrid(host,'resize','synthetic',['Book','Market'],[['Synthetic book','Synthetic market']],[],()=>false,{keys:['book','market'],layout:emptyGridLayout(),onLayout:()=>{}});})()`);
 await wait('window.__grid.ready()');await evaluate(frames);
 const handle=await evaluate("(()=>{const h=document.querySelector('#resize-grid [role=columnheader][tabulator-field=c0]'),r=document.querySelector('#resize-grid [role=columnheader][tabulator-field=c0] + .tabulator-col-resize-handle').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+12,width:Math.round(h.getBoundingClientRect().width)};})()");
 await command('Input.dispatchMouseEvent',{type:'mousePressed',x:handle.x,y:handle.y,button:'left',clickCount:1});await command('Input.dispatchMouseEvent',{type:'mouseMoved',x:handle.x+50,y:handle.y,button:'left',buttons:1});await command('Input.dispatchMouseEvent',{type:'mouseReleased',x:handle.x+50,y:handle.y,button:'left',clickCount:1});
 await evaluate("document.querySelector('#resize-grid').style.width='885px'");await evaluate(frames);
 await check('a resized column keeps its width when the grid redraws at another width',`(()=>{const w=Math.round(document.querySelector('#resize-grid [role=columnheader][tabulator-field=c0]').getBoundingClientRect().width);return w===${handle.width+50}&&window.__grid.layout().widths.book===w;})()`,"({header:Math.round(document.querySelector('#resize-grid [role=columnheader][tabulator-field=c0]').getBoundingClientRect().width),layout:window.__grid.layout().widths})");
 await evaluate("window.__grid.destroy();document.querySelector('#resize-grid').remove()");
 await detailShrink({command,evaluate,wait,check,idle,url});
 await expandedGridGeometry({command,evaluate,wait,check,idle,url});
 await nativePositionOpen({command,evaluate,wait,check,idle,url});
}

// A prompt at the foot of an expanded editor can scroll a virtual row far past
// its ordinary cells. Returning to the short read view must keep that position.
export async function detailShrink({command,evaluate,wait,check,idle,url}){
 const frames='new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))';
 await command('Runtime.enable');await command('Page.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1800,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);
 await evaluate(`(async()=>{
  const [{renderDataGrid,emptyGridLayout},{setupPositionExpansion}]=await Promise.all([import('/data-grid.js'),import('/position-expansion.js')]);
  const host=document.createElement('section');host.id='detail-shrink-grid';host.className='table-workspace';host.style='position:fixed;inset:24px;z-index:100;background:white;padding:16px';document.body.append(host);
  const side={current:()=>null,show:async()=>true,reset(){},leave:async()=>true,ask:ask=>ask(new AbortController().signal),element(){throw Error('No side-panel mode belongs to this position fixture.');},form(){},reveal(){},focusForm(){},place(){}};
  let grid;const expansion=setupPositionExpansion(side,{unsaved:()=>true,busy:()=>false,changed(){},closed(){},resized:keepVisible=>grid?.resizeDetail(keepVisible)});
  const head=expansion.position.element('head');head.textContent='Synthetic position 1';head.style.height='180px';
  const form=expansion.position.element('form'),read=expansion.position.element('view');
  read.textContent='Retained terms of synthetic position 1';read.style.height='100px';
  for(let i=0;i<80;i++){const label=document.createElement('label'),input=document.createElement('input');label.textContent='Synthetic editor field '+i;label.style.display='block';label.append(input);form.append(label);}
  window.__shrink={expansion,host};
  grid=renderDataGrid(host,'detail-shrink','synthetic',['Position'],Array.from({length:100},(_,i)=>['Synthetic position '+i]),[],()=>false,
   {keys:['position'],layout:emptyGridLayout(),onLayout(){},onRowOpen:async index=>{await expansion.shell.show({kind:'position',tradeId:'synthetic-'+index,bindingId:'binding-'+index,revision:1});grid.selectRow(index);grid.detailRow(index,expansion.host);expansion.position.form(true);}});
  window.__shrink.grid=grid;
 })()`);
 await wait('window.__shrink.grid.ready()');
 await evaluate("document.querySelectorAll('#detail-shrink-grid .tabulator-row')[1].click()");
 await wait("document.querySelector('#position-details')?.isConnected&&document.querySelector('#position-details-form').hidden===false");
 await evaluate("void window.__shrink.expansion.ask({text:'The synthetic editor has unsaved changes.',keep:'Keep editing',go:'Discard changes'}).then(discard=>{if(discard)window.__shrink.expansion.position.form(false);})");
 await wait("document.querySelector('#position-details-prompt')?.hidden===false");
 await check('the unsaved-edit answer is visible after the virtual grid scrolls below the position cells',"(()=>{const h=document.querySelector('#detail-shrink-grid .tabulator-tableholder'),p=document.querySelector('#position-details-prompt'),r=h.getBoundingClientRect(),b=p.querySelector('button').getBoundingClientRect();return h.scrollTop>500&&b.top>=r.top&&b.bottom<=r.bottom;})()","({scroll:document.querySelector('#detail-shrink-grid .tabulator-tableholder').scrollTop,detail:document.querySelector('#position-details')?.getBoundingClientRect().toJSON(),prompt:document.querySelector('#position-details-prompt')?.getBoundingClientRect().toJSON()})");
 await evaluate("document.querySelector('#position-details-prompt button:last-child').click()");
 await evaluate(frames);
 await wait("document.querySelector('#position-details')?.isConnected&&document.querySelector('#position-details-form').hidden&&document.querySelector('#position-details-view').getClientRects().length>0");
 await check('discarding a scrolled editor keeps its same expanded position and short retained read visible',"(()=>{const p=document.querySelector('#position-details'),r=p.closest('.tabulator-row'),h=document.querySelector('#detail-shrink-grid .tabulator-tableholder').getBoundingClientRect(),b=p.getBoundingClientRect();return r.querySelector('[tabulator-field=c0]').textContent==='Synthetic position 1'&&r.getAttribute('aria-expanded')==='true'&&document.querySelectorAll('#detail-shrink-grid .open-row').length===1&&b.top<h.bottom&&b.bottom>h.top&&p.textContent.includes('Retained terms of synthetic position 1');})()");
 // A close in the next action must cancel the queued positioning work.
 await evaluate("window.__shrink.expansion.position.form(true);window.__shrink.expansion.host.focus()");await evaluate(frames);
 await evaluate("(()=>{const s=window.__shrink;s.expansion.position.form(false);s.expansion.shell.reset();s.grid.detailRow(null,s.expansion.host);document.querySelector('#detail-shrink-grid .table-search').focus();document.querySelector('#detail-shrink-grid .tabulator-tableholder').scrollTop=1700;})()");
 await evaluate(frames);
 await check('a closed position cannot reclaim focus or scroll the later viewport back to its old row',
  "!document.querySelector('#position-details')&&document.activeElement===document.querySelector('#detail-shrink-grid .table-search')&&document.querySelector('#detail-shrink-grid .tabulator-tableholder').scrollTop>=1600");
 await evaluate("window.__shrink.grid.destroy();window.__shrink.host.remove();delete window.__shrink");
}

export async function expandedGridGeometry({command,evaluate,wait,check,idle,url}){
 const frames='new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))';
 await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);
 await evaluate(`(async()=>{
  const [{renderDataGrid,emptyGridLayout},{setupPositionExpansion},{renderRegularTable}]=await Promise.all([import('/data-grid.js'),import('/position-expansion.js'),import('/regular-table.js')]);
  const host=document.createElement('section');host.id='expanded-geometry-grid';host.className='table-workspace';host.style='position:fixed;left:24px;top:24px;width:900px;z-index:100;background:white;padding:16px';document.body.append(host);
  const side={current:()=>null,show:async()=>true,reset(){},leave:async()=>true,ask:ask=>ask(new AbortController().signal),element(){throw Error('This fixture has only an inline position.');},form(){},reveal(){},focusForm(){},place(){}};
  let grid;const expansion=setupPositionExpansion(side,{unsaved:()=>false,busy:()=>false,changed(){},closed(){grid.selectRow(null);grid.detailRow(null,expansion.host);},resized:keepVisible=>grid?.resizeDetail(keepVisible)});
  expansion.position.element('head').textContent='Synthetic retained position';
  const wrap=document.createElement('div');wrap.className='table-wrap';wrap.id='geometry-schedule';
  renderRegularTable(wrap,'geometry-schedule','synthetic',Array.from({length:12},(_,i)=>'Schedule field '+i),Array.from({length:40},(_,row)=>Array.from({length:12},(_,column)=>'Exact synthetic '+row+'/'+column)),[],()=>false);wrap.querySelector('table').style.minWidth='2800px';expansion.position.element('view').append(wrap);
  grid=renderDataGrid(host,'expanded-geometry','synthetic',['Trade','Market','Exact amount'],Array.from({length:100},(_,i)=>['Synthetic position '+i,'Synthetic market '+i,'100.125']),[2],()=>false,
   {keys:['trade','market','amount'],layout:{...emptyGridLayout(),widths:{trade:420,market:650,amount:800}},onLayout(){},onRowOpen:async index=>{await expansion.shell.show({kind:'position',tradeId:'synthetic-'+index,bindingId:'binding-'+index,revision:1});grid.selectRow(index);grid.detailRow(index,expansion.host);expansion.position.form(false);}});
  window.__geometry={grid,expansion,host};
 })()`);
 await wait('window.__geometry.grid.ready()');await evaluate(frames);
 await evaluate("window.__geometry.baseline=document.querySelector('#expanded-geometry-grid .tabulator-tableholder').scrollHeight;document.querySelectorAll('#expanded-geometry-grid .tabulator-row')[2].click()");
 await wait("document.querySelector('#position-details')?.isConnected");await evaluate(frames);
 const aligned="(()=>{const row=document.querySelector('#expanded-geometry-grid .open-row')??document.querySelector('#expanded-geometry-grid .tabulator-row');return [0,1,2].every(i=>{const a=row.querySelector('[tabulator-field=c'+i+']').getBoundingClientRect(),b=document.querySelector('#expanded-geometry-grid [role=columnheader][tabulator-field=c'+i+']').getBoundingClientRect();return Math.abs(a.left-b.left)<2&&Math.abs(a.right-b.right)<2;});})()";
 const columns="(()=>{const row=document.querySelector('#expanded-geometry-grid .open-row');return {scroll:document.querySelector('#expanded-geometry-grid .tabulator-tableholder').scrollLeft,columns:[0,1,2].map(i=>({cell:row.querySelector('[tabulator-field=c'+i+']').getBoundingClientRect().toJSON(),header:document.querySelector('#expanded-geometry-grid [role=columnheader][tabulator-field=c'+i+']').getBoundingClientRect().toJSON()}))};})()";
 for(const left of [0,280,900,80]){
  await evaluate(`document.querySelector('#expanded-geometry-grid .tabulator-tableholder').scrollLeft=${left}`);await evaluate(frames);
  await check('expanded position columns align at both edges after horizontal scroll '+left,aligned,columns);
 }
 for(const giveUp of [[2],[]]){
  await evaluate(`window.__geometry.grid.fitColumns(${JSON.stringify(giveUp)})`);await evaluate(frames);
  await check('expanded headers and cells stay aligned after responsive redraw '+JSON.stringify(giveUp),aligned,columns);
 }
 await evaluate("document.querySelector('#geometry-schedule .regular-table-scroll').scrollLeft=1100");await evaluate(frames);
 await check('a wide native schedule scrolls its headers and body together without shifting the position grid',`(()=>{const t=document.querySelector('#geometry-schedule .regular-table-scroll');return t.scrollLeft>0&&[...t.querySelectorAll('thead tr:first-child th')].every((th,i)=>{const a=th.getBoundingClientRect(),b=t.querySelector('tbody tr').cells[i].getBoundingClientRect();return Math.abs(a.left-b.left)<2&&Math.abs(a.right-b.right)<2;});})()&&${aligned}`,columns);
 await evaluate("(()=>{const h=document.querySelector('#expanded-geometry-grid .tabulator-tableholder');h.scrollLeft=280;h.scrollTop=h.scrollHeight;})()");await evaluate(frames);
 await check('the expanded source row can leave the virtual buffer while later positions are inspected',"!document.querySelector('#position-details')&&window.__geometry.expansion.shell.current()?.kind==='position'");
 await evaluate("window.__geometry.expansion.host.querySelector('.position-close').click()");await wait("window.__geometry.expansion.shell.current()===null");await evaluate(frames);
 await check('collapsing an expanded position returns the virtual table to its ordinary row height',"(()=>{const h=document.querySelector('#expanded-geometry-grid .tabulator-tableholder');return Math.abs(h.scrollHeight-window.__geometry.baseline)<=70&&window.__geometry.grid.shownRows().length===100;})()","({baseline:window.__geometry.baseline,height:document.querySelector('#expanded-geometry-grid .tabulator-tableholder').scrollHeight,rows:[...document.querySelectorAll('#expanded-geometry-grid .tabulator-row')].map(r=>[r.textContent,r.getBoundingClientRect().height])})");
 await check('collapsing the position preserves horizontal scroll and header alignment',"document.querySelector('#expanded-geometry-grid .tabulator-tableholder').scrollLeft===280&&"+aligned);
 for(const portion of [0,.25,.5,.75,1]){
  await evaluate(`(()=>{const h=document.querySelector('#expanded-geometry-grid .tabulator-tableholder');h.scrollTop=(h.scrollHeight-h.clientHeight)*${portion};})()`);await evaluate(frames);
  await check('all positions remain reachable without a blank virtual viewport after collapse '+portion,"(()=>{const h=document.querySelector('#expanded-geometry-grid .tabulator-tableholder'),b=h.getBoundingClientRect(),rows=[...h.querySelectorAll('.tabulator-row')].map(r=>r.getBoundingClientRect()).filter(r=>r.bottom>b.top&&r.top<b.bottom);return rows.length>=5&&Math.min(...rows.map(r=>r.top))<=b.top+40&&Math.max(...rows.map(r=>r.bottom))>=b.bottom-40;})()","(()=>{const h=document.querySelector('#expanded-geometry-grid .tabulator-tableholder');return {scroll:h.scrollTop,height:h.scrollHeight,viewport:h.clientHeight,rows:[...h.querySelectorAll('.tabulator-row')].map(r=>({text:r.textContent,box:r.getBoundingClientRect().toJSON()}))};})()");
 }
 await check('the last retained position is visible at the end of the collapsed table',"[...document.querySelectorAll('#expanded-geometry-grid .tabulator-row [tabulator-field=c0]')].some(n=>n.textContent==='Synthetic position 99')");
 await evaluate("window.__geometry.grid.destroy();window.__geometry.host.remove();delete window.__geometry");
}


export async function nativePositionOpen({command,evaluate,wait,check,idle,url}){
 const frames='new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))';
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);
 await evaluate(`(async()=>{
  const [{renderDataGrid,emptyGridLayout},{setupPositionExpansion},{setupDeskPanel}]=await Promise.all([import('/data-grid.js'),import('/position-expansion.js'),import('/desk-panel.js')]);
  const host=document.createElement('section');host.id='native-position-grid';host.className='table-workspace';host.style='position:fixed;inset:24px;z-index:100;background:white;padding:16px';document.body.append(host);
  const side={current:()=>null,show:async()=>true,reset(){},leave:async()=>true,ask:ask=>ask(new AbortController().signal),element(){throw Error('This fixture has only an inline position.');},form(){},reveal(){},focusForm(){},place(){}};
  const held=Array.from({length:100},(_,index)=>({tradeId:'synthetic-trade-'+index,bindingId:'synthetic-binding-'+index,revision:'1'}));
  let grid,controller;const expansion=setupPositionExpansion(side,{unsaved:()=>false,busy:()=>false,changed(){},closed(){controller.closed();},resized:keepVisible=>grid?.resizeDetail(keepVisible)});
  expansion.position.element('view').style.height='1400px';
  const position={show(at){expansion.position.element('head').textContent='Synthetic position '+at.index;expansion.position.element('view').textContent='Retained synthetic schedule '+at.index;expansion.position.form(false);},update(){},clear(){}};
  controller=setupDeskPanel({shell:expansion.shell,position,holdings:()=>held,input:index=>({holding:held[index],index}),grid:()=>({...grid,selectRow:index=>{grid.selectRow(index);grid.detailRow(index,expansion.host);}}),notice(){}});
  window.__nativePosition={host,expansion,controller,double:[]};
  grid=renderDataGrid(host,'native-position','synthetic',['Trade'],held.map((_,index)=>['Synthetic position '+index]),[],()=>false,{keys:['trade'],layout:emptyGridLayout(),onLayout(){},onRowOpen:index=>void controller.open(index,undefined,false),onRowDoubleOpen:index=>window.__nativePosition.double.push(index)});
  window.__nativePosition.grid=grid;
 })()`);
 await wait('window.__nativePosition.grid.ready()');await evaluate(frames);
 await evaluate('void window.__nativePosition.controller.open(0,undefined,false)');await wait("document.querySelector('#position-details')?.textContent.includes('Retained synthetic schedule 0')");
 await evaluate("(()=>{const filter=document.querySelector('#native-position-grid select[aria-label=\"Filter Trade\"]');filter.value='1';filter.dispatchEvent(new Event('change',{bubbles:true}));})()");await evaluate(frames);
 await check('filtering the expanded position retains a notice while another position is visible',"!document.querySelector('#native-position-grid .table-open-note').hidden&&window.__nativePosition.grid.shownRows().join()==='1'");
 const point=await evaluate("(()=>{const n=document.querySelector('#native-position-grid .tabulator-row [tabulator-field=c0]'),r=n.getBoundingClientRect(),h=document.querySelector('#native-position-grid .tabulator-tableholder');window.__nativePosition.before={top:r.top,scroll:h.scrollTop};return{x:r.left+35,y:r.top+r.height/2};})()");
 for(const clickCount of [1,2]){await command('Input.dispatchMouseEvent',{type:'mousePressed',...point,button:'left',clickCount});await command('Input.dispatchMouseEvent',{type:'mouseReleased',...point,button:'left',clickCount});}
 await evaluate(frames);
 await check('opening a native row leaves its ordinary cells under the pointer for a second click',"(()=>{const h=document.querySelector('#native-position-grid .tabulator-tableholder'),r=document.querySelector('#native-position-grid .open-row > [tabulator-field=c0]').getBoundingClientRect();return h.scrollTop===window.__nativePosition.before.scroll&&Math.abs(r.top-window.__nativePosition.before.top)<2&&window.__nativePosition.double.join()==='1'&&getSelection().isCollapsed;})()","({before:window.__nativePosition.before,scroll:document.querySelector('#native-position-grid .tabulator-tableholder').scrollTop,double:window.__nativePosition.double,selected:getSelection().toString()})");
 await evaluate("(()=>{const filter=document.querySelector('#native-position-grid select[aria-label=\"Filter Trade\"]');filter.value='';filter.dispatchEvent(new Event('change',{bubbles:true}));})()");await evaluate(frames);
 await evaluate('void window.__nativePosition.controller.open(90)');
 await wait("document.querySelector('#native-position-grid .open-row [tabulator-field=c0]')?.textContent==='Synthetic position 90'&&document.querySelector('#native-position-grid .tabulator-tableholder').scrollTop>2000");await evaluate(frames);
 await check('a programmatic offscreen position still brings its retained detail into the viewport',"(()=>{const p=document.querySelector('#position-details'),r=p.getBoundingClientRect(),h=document.querySelector('#native-position-grid .tabulator-tableholder').getBoundingClientRect();return p.isConnected&&p.textContent.includes('Retained synthetic schedule 90')&&r.top<h.bottom&&r.bottom>h.top;})()");
 await evaluate('window.__nativePosition.grid.destroy();window.__nativePosition.host.remove();delete window.__nativePosition');
}
