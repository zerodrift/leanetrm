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
 // A press on a row whose rows also open by a double click opens it once a second press can no longer follow.
 await wait('window.__opened.length>0',2000).catch(()=>undefined);
 await check('pointer click after virtual scrolling opens the displayed source row',`JSON.stringify(window.__opened)===${JSON.stringify(JSON.stringify([visible.index]))}`);
 await evaluate("(()=>{const h=document.querySelector('#gesture-grid .tabulator-tableholder').getBoundingClientRect();[...document.querySelectorAll('#gesture-grid .tabulator-row')].find(n=>{const r=n.getBoundingClientRect();return r.top>h.top+10&&r.bottom<h.bottom-10;}).focus();})()");
 await command('Input.dispatchKeyEvent',{type:'keyDown',key:'Enter',code:'Enter',windowsVirtualKeyCode:13});await command('Input.dispatchKeyEvent',{type:'keyUp',key:'Enter',code:'Enter',windowsVirtualKeyCode:13});
 await check('keyboard opening remains available after scrolling and selection',`JSON.stringify(window.__opened)===${JSON.stringify(JSON.stringify([visible.index,visible.index]))}`);
 const doubleClick=async (point,modifiers=0)=>{for(const clickCount of [1,2]){await command('Input.dispatchMouseEvent',{type:'mousePressed',x:point.x,y:point.y,button:'left',clickCount,modifiers});await command('Input.dispatchMouseEvent',{type:'mouseReleased',x:point.x,y:point.y,button:'left',clickCount,modifiers});}};
 await doubleClick(visible);await wait('window.__opened.length>2',2000).catch(()=>undefined);
 await check('a native row double click opens the separate view once without duplicate inline opens or word selection',
  `JSON.stringify(window.__opened)===${JSON.stringify(JSON.stringify([visible.index,visible.index,visible.index]))}&&JSON.stringify(window.__doubleOpened)===${JSON.stringify(JSON.stringify([visible.index]))}&&getSelection().isCollapsed`,
  "({single:window.__opened,double:window.__doubleOpened,selection:getSelection().toString()})");
 const control=await evaluate("(()=>{const n=[...document.querySelectorAll('#gesture-grid .tabulator-row')].find(r=>Number(r.querySelector('[tabulator-field=c0]').textContent.slice(3))==="+visible.index+");const button=document.createElement('button');button.textContent='Synthetic row action';n.querySelector('[tabulator-field=c1]').append(button);const r=button.getBoundingClientRect();return{x:r.left+r.width/2,y:r.top+r.height/2};})()");
 await doubleClick(control);await evaluate('new Promise(r=>setTimeout(r,500))');
 await check('double clicking a row control does not open either position view',`window.__opened.length===3&&window.__doubleOpened.length===1`);
 await doubleClick(visible,2);await evaluate('new Promise(r=>setTimeout(r,500))');
 await check('a modified row double click does not open either position view','window.__opened.length===3&&window.__doubleOpened.length===1');
 await evaluate("getSelection().removeAllRanges();const n=[...document.querySelectorAll('#gesture-grid .tabulator-row')].find(r=>Number(r.querySelector('[tabulator-field=c0]').textContent.slice(3))==="+visible.index+").querySelector('[tabulator-field=c0]');const r=document.createRange();r.selectNodeContents(n);getSelection().addRange(r)");
 await doubleClick(visible);await evaluate('new Promise(r=>setTimeout(r,500))');
 await check('clearing previously selected row text does not also open a position view','window.__opened.length===3&&window.__doubleOpened.length===1');
 await evaluate('getSelection().removeAllRanges()');
 const different=await evaluate("(()=>{const h=document.querySelector('#gesture-grid .tabulator-tableholder').getBoundingClientRect();const n=[...document.querySelectorAll('#gesture-grid .tabulator-row')].find(n=>{const r=n.getBoundingClientRect();return Number(n.querySelector('[tabulator-field=c0]').textContent.slice(3))!=="+visible.index+"&&r.top>h.top+10&&r.bottom<h.bottom-10;});const r=n.querySelector('[tabulator-field=c0]').getBoundingClientRect();return {x:r.left+12,y:r.top+r.height/2};})()");
 for(const [point,clickCount] of [[visible,1],[different,2]]){await command('Input.dispatchMouseEvent',{type:'mousePressed',x:point.x,y:point.y,button:'left',clickCount});await command('Input.dispatchMouseEvent',{type:'mouseReleased',x:point.x,y:point.y,button:'left',clickCount});}
 await wait('window.__opened.length>3',2000).catch(()=>undefined);await evaluate('new Promise(r=>setTimeout(r,500))');
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
 await nativePositionOpen({command,evaluate,wait,check,idle,url});
}

// A row press opens its position elsewhere on the page: the row stays under the pointer, a double click opens the position's own window
// once, and a position opened from elsewhere brings its row into view.
export async function nativePositionOpen({command,evaluate,wait,check,idle,url}){
 const frames='new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))';
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);
 await evaluate(`(async()=>{
  const [{renderDataGrid,emptyGridLayout},{setupDeskPanel}]=await Promise.all([import('/data-grid.js'),import('/desk-panel.js')]);
  const host=document.createElement('section');host.id='native-position-grid';host.className='table-workspace';host.style='position:fixed;inset:24px;z-index:100;background:white;padding:16px';document.body.append(host);
  const held=Array.from({length:100},(_,index)=>({tradeId:'synthetic-trade-'+index,bindingId:'synthetic-binding-'+index,revision:'1'}));
  // The details a position opens in are elsewhere on the page: this fixture records which position they show.
  let grid,mode=null;const shell={current:()=>mode,show:async next=>{mode=next;return true;},reset(){mode=null;}};
  const position={show(at){window.__nativePosition.shown=at.index;},update(){},clear(){window.__nativePosition.shown=null;}};
  const controller=setupDeskPanel({shell,position,holdings:()=>held,input:index=>({holding:held[index],index}),grid:()=>grid,notice(){}});
  window.__nativePosition={host,controller,double:[],shown:null};
  grid=renderDataGrid(host,'native-position','synthetic',['Trade'],held.map((_,index)=>['Synthetic position '+index]),[],()=>false,{keys:['trade'],layout:emptyGridLayout(),onLayout(){},onRowOpen:index=>controller.open(index,undefined,false),onRowDoubleOpen:index=>window.__nativePosition.double.push(index)});
  window.__nativePosition.grid=grid;
 })()`);
 await wait('window.__nativePosition.grid.ready()');await evaluate(frames);
 await evaluate('void window.__nativePosition.controller.open(0,undefined,false)');await wait("window.__nativePosition.shown===0");
 await evaluate("(()=>{const filter=document.querySelector('#native-position-grid select[aria-label=\"Filter Trade\"]');filter.value='1';filter.dispatchEvent(new Event('change',{bubbles:true}));})()");await evaluate(frames);
 await check('filtering out the open position retains a notice while another position is visible',"!document.querySelector('#native-position-grid .table-open-note').hidden&&window.__nativePosition.grid.shownRows().join()==='1'");
 const point=await evaluate("(()=>{const n=document.querySelector('#native-position-grid .tabulator-row [tabulator-field=c0]'),r=n.getBoundingClientRect(),h=document.querySelector('#native-position-grid .tabulator-tableholder');window.__nativePosition.before={top:r.top,scroll:h.scrollTop};return{x:r.left+35,y:r.top+r.height/2};})()");
 for(const clickCount of [1,2]){await command('Input.dispatchMouseEvent',{type:'mousePressed',...point,button:'left',clickCount});await command('Input.dispatchMouseEvent',{type:'mouseReleased',...point,button:'left',clickCount});}
 await wait("!!document.querySelector('#native-position-grid .open-row')",2000).catch(()=>undefined);await evaluate(frames);
 await check('a native double click opens the row, leaves its cells under the pointer, and opens its own window once',"(()=>{const h=document.querySelector('#native-position-grid .tabulator-tableholder'),r=document.querySelector('#native-position-grid .open-row > [tabulator-field=c0]').getBoundingClientRect();return h.scrollTop===window.__nativePosition.before.scroll&&Math.abs(r.top-window.__nativePosition.before.top)<2&&window.__nativePosition.double.join()==='1'&&getSelection().isCollapsed;})()","({before:window.__nativePosition.before,scroll:document.querySelector('#native-position-grid .tabulator-tableholder').scrollTop,double:window.__nativePosition.double,selected:getSelection().toString()})");
 await evaluate("(()=>{const filter=document.querySelector('#native-position-grid select[aria-label=\"Filter Trade\"]');filter.value='';filter.dispatchEvent(new Event('change',{bubbles:true}));})()");await evaluate(frames);
 await evaluate('void window.__nativePosition.controller.open(90)');
 await wait("document.querySelector('#native-position-grid .open-row [tabulator-field=c0]')?.textContent==='Synthetic position 90'&&document.querySelector('#native-position-grid .tabulator-tableholder').scrollTop>2000");await evaluate(frames);
 await check('a programmatic offscreen position brings its row into the viewport',"(()=>{const r=document.querySelector('#native-position-grid .open-row').getBoundingClientRect(),h=document.querySelector('#native-position-grid .tabulator-tableholder').getBoundingClientRect();return window.__nativePosition.shown===90&&r.top>=h.top-1&&r.bottom<=h.bottom+1;})()");
 await evaluate('window.__nativePosition.grid.destroy();window.__nativePosition.host.remove();delete window.__nativePosition');
}
