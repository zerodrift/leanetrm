// Exercise real pointer gestures: HTMLElement.click does not model selection or scrolling.
export async function run({command,evaluate,wait,check,shot,idle,url}){
 const frames='new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))';
 await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);
 await evaluate(`(async()=>{const {renderDataGrid,emptyGridLayout}=await import('/data-grid.js');const host=document.createElement('div');host.id='gesture-grid';host.className='table-workspace';host.style='position:fixed;inset:24px;z-index:100;background:white;padding:16px';document.body.append(host);window.__opened=[];window.__grid=renderDataGrid(host,'gestures','synthetic',['Trade ID','Description','Exact amount'],Array.from({length:100},(_,i)=>['TR-'+i,'Selectable synthetic position '+i,'100000000000000000.000000000000000001']),[2],()=>false,{keys:['id','description','amount'],layout:{...emptyGridLayout(),widths:{id:400,description:700,amount:500}},onLayout:()=>{},onRowOpen:i=>window.__opened.push(i)});})()`);
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
}

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
