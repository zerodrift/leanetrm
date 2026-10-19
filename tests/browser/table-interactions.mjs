// Exercise real pointer gestures: HTMLElement.click does not model selection or scrolling.
export async function run({command,evaluate,wait,check,shot,idle,url}){
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
 await evaluate('new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))');
 await check('horizontal scrolling keeps column headers aligned with cells',"(()=>{const c=document.querySelector('#gesture-grid .tabulator-row [tabulator-field=c1]').getBoundingClientRect(),h=document.querySelector('#gesture-grid [role=columnheader][tabulator-field=c1]').getBoundingClientRect();return Math.abs(c.left-h.left)<2;})()");
 await shot('table-gestures');
 await evaluate("window.__grid.destroy();document.querySelector('#gesture-grid').remove()");
}
