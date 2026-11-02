import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserAvailable,withBrowserPage} from './browser-page.mjs';

// Real shell and assistant, synthetic identity and API. No application or account is attached.
const page=(await readFile(new URL('../src/index.html',import.meta.url),'utf8'))
 .replace('<script type="module" src="/app.js"></script>','').replace('</body>','<div id="host" hidden></div></body>');
const settle="new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))";
async function fixture(work){
 await withBrowserPage(page,async(evaluate,command)=>{
  const viewport=async width=>{await command('Emulation.setDeviceMetricsOverride',{width,height:1000,deviceScaleFactor:1,mobile:false});await evaluate(settle);};
  await viewport(1440);
  await evaluate(`(async()=>{
   const {setupAssistant}=await import('/assistant-panel.js');
   window.assistant=setupAssistant({api:async()=>({agents:[],vendors:[]}),root:'/api',identity:()=> 'synthetic-layout',zone:()=> 'UTC',
    available:()=>true,pageBusy:()=>false,act:async work=>work(),place:()=>({report:{page:'curves',selection:{view:'curves',curve:null}},context:{mode:'current',frameId:null,frameLabel:null}}),
    names:()=>({}),trade:()=>undefined,unsaved:()=>false,open:async()=>{},go:async()=>{},restore:async()=>{}});
   assistant.draft('Synthetic layout question');
   window.press=key=>document.getElementById('assistant-resize').dispatchEvent(new KeyboardEvent('keydown',{key,bubbles:true}));
   window.geometry=()=>{const main=document.querySelector('main'),panel=document.getElementById('assistant-panel');return {
    main:main.getBoundingClientRect().width,panel:panel.getBoundingClientRect().width,margin:parseFloat(getComputedStyle(main).marginRight),
    max:Number(document.getElementById('assistant-resize').getAttribute('aria-valuemax')),saved:localStorage.getItem('openetrm.assistant.width')};};
  })()`);
  await work(evaluate,viewport,command);
 });
}

test('the widest docked assistant preserves a usable workspace and follows navigation width',{skip:!browserAvailable},async()=>{
 await fixture(async(evaluate,viewport,command)=>{
  await evaluate("press('End')");
  const expanded=await evaluate('geometry()');
  assert.ok(expanded.main>=640,`workspace squeezed to ${expanded.main}px`);
  assert.equal(expanded.margin,expanded.panel);
  const edge=1440-expanded.panel;
  await command('Input.dispatchMouseEvent',{type:'mousePressed',x:edge,y:400,button:'left',clickCount:1});
  await command('Input.dispatchMouseEvent',{type:'mouseMoved',x:1000,y:400,button:'left',buttons:1});
  await evaluate(settle);
  assert.equal((await evaluate('geometry()')).panel,440,'dragging remains at the pointer between frames');
  await command('Input.dispatchMouseEvent',{type:'mouseReleased',x:1000,y:400,button:'left',clickCount:1});
  assert.equal((await evaluate('geometry()')).saved,'440','releasing remembers the dragged width');
  await command('Input.dispatchMouseEvent',{type:'mousePressed',x:1000,y:400,button:'left',clickCount:1});
  await command('Input.dispatchMouseEvent',{type:'mouseMoved',x:1100,y:400,button:'left',buttons:1});
  await evaluate(settle);
  assert.equal((await evaluate('geometry()')).panel,340);
  await evaluate("document.getElementById('assistant-resize').dispatchEvent(new PointerEvent('pointercancel',{bubbles:true}))");
  await command('Input.dispatchMouseEvent',{type:'mouseReleased',x:1100,y:400,button:'left',clickCount:1});
  const canceled=await evaluate('geometry()');
  assert.equal(canceled.panel,440,'canceling a drag restores the previous width');
  assert.equal(canceled.saved,'440','canceling a drag preserves the preference');
  await evaluate("press('End')");
  await evaluate("document.body.classList.add('sidebar-collapsed');"+settle);
  assert.ok((await evaluate('geometry()')).max>expanded.max,'collapsed navigation gives the assistant more room');
  await evaluate("press('End')");
  assert.ok((await evaluate('geometry()')).main>=640);
  await viewport(1280);
  assert.ok((await evaluate('geometry()')).main>=640);
 });
});

test('a narrow window overlays the assistant and restores the chosen dock width when space returns',{skip:!browserAvailable},async()=>{
 await fixture(async(evaluate,viewport)=>{
  await evaluate("press('End')");
  const original=await evaluate('geometry()');
  await viewport(1024);
  const narrow=await evaluate('geometry()');
  assert.equal(narrow.margin,0,'the assistant overlays instead of squeezing the workspace');
  assert.ok(narrow.main>=640);
  assert.equal(narrow.saved,original.saved,'temporary window constraints do not replace the preference');
  assert.equal(await evaluate("getComputedStyle(document.getElementById('context-dock')).display"),'none');
  assert.equal(await evaluate("getComputedStyle(document.getElementById('assistant-launcher')).display"),'none');
  assert.equal(await evaluate("document.elementFromPoint(innerWidth-40,innerHeight-40).closest('#assistant-panel')!==null"),true,'the overlay stays above page controls');
  await viewport(1440);
  assert.equal((await evaluate('geometry()')).panel,original.panel);
  for(const width of [760,390,320]){
   await viewport(width);
   // A full-width sheet fills the usable viewport, excluding any reserved scrollbar.
   assert.equal((await evaluate('geometry()')).panel,await evaluate('document.documentElement.clientWidth'));
   const overflow=await evaluate(`(()=>{const panel=document.getElementById('assistant-panel'),bounds=panel.getBoundingClientRect();return [...panel.querySelectorAll('button,select,textarea')].filter(n=>n.getClientRects().length).filter(n=>{const r=n.getBoundingClientRect();return r.left<bounds.left+3||r.right>bounds.right-3;}).map(n=>n.id);})()`);
   assert.deepEqual(overflow,[],`controls and focus rings fit at ${width}px`);
  }
 });
});

test('a reserved scrollbar preserves workspace room and keeps the resize edge at the pointer',{skip:!browserAvailable},async()=>{
 await fixture(async(evaluate,viewport,command)=>{
  await evaluate(`(()=>{
   const style=document.createElement('style');style.textContent='html{overflow-y:auto}::-webkit-scrollbar{width:15px;height:15px}';document.head.append(style);
   for(const section of document.querySelectorAll('main>section'))section.hidden=true;
   document.querySelector('main').style.height='500px';
  })();`+settle);
  await evaluate("press('End')");
  const original=await evaluate('geometry()');
  await evaluate("document.querySelector('main').style.height='2400px';"+settle);
  assert.ok(await evaluate('document.documentElement.clientWidth<innerWidth'),'the fixture reserves a real scrollbar gutter');
  const tall=await evaluate('geometry()');
  assert.ok(tall.main>=640,`adding page content squeezed the workspace to ${tall.main}px`);
  assert.equal(tall.saved,original.saved,'automatic clamping preserves the chosen width');
  await evaluate("document.querySelector('main').style.height='500px';"+settle);
  assert.equal((await evaluate('geometry()')).panel,original.panel,'removing the scrollbar restores the preference');
  await evaluate("document.querySelector('main').style.height='2400px';"+settle);
  const right=await evaluate("document.getElementById('assistant-panel').getBoundingClientRect().right");
  const edge=right-(await evaluate('geometry()')).panel;
  await command('Input.dispatchMouseEvent',{type:'mousePressed',x:edge,y:400,button:'left',clickCount:1});
  await command('Input.dispatchMouseEvent',{type:'mouseMoved',x:1000,y:400,button:'left',buttons:1});
  await evaluate(settle);
  assert.equal((await evaluate('geometry()')).panel,right-1000,'the drag uses the drawn right edge and stays at the pointer');
  await command('Input.dispatchMouseEvent',{type:'mouseReleased',x:1000,y:400,button:'left',clickCount:1});
  assert.equal((await evaluate('geometry()')).saved,String(right-1000));
 });
});

test('scrollbar changes at the docking boundary settle into a stable overlay',{skip:!browserAvailable},async()=>{
 await fixture(async(evaluate,viewport)=>{
  await viewport(1191);
  await evaluate(`(()=>{
   window.geometryErrors=[];window.addEventListener('error',event=>geometryErrors.push(event.message));
   const style=document.createElement('style');style.textContent='html{overflow-y:auto}::-webkit-scrollbar{width:15px;height:15px}#responsive-content{height:500px}@container workspace (max-width:700px){#responsive-content{height:2400px}}';document.head.append(style);
   for(const section of document.querySelectorAll('main>section'))section.hidden=true;
   const content=document.createElement('div');content.id='responsive-content';document.querySelector('main').append(content);
  })();`+settle);
  const states=await evaluate(`(async()=>{
   const states=[];for(let i=0;i<8;i++){
    await new Promise(r=>requestAnimationFrame(r));
    states.push({overlay:document.body.classList.contains('assistant-overlay'),main:document.querySelector('main').getBoundingClientRect().width});
   }return states;
  })()`);
  assert.ok(states.every(state=>state.overlay),`the overlay must stop flipping at the scrollbar boundary: ${JSON.stringify(states)}`);
  assert.ok(states.every(state=>state.main>=640));
  assert.equal(new Set(states.map(state=>state.main)).size,1,'the workspace width stops changing between frames');
  assert.deepEqual(await evaluate('geometryErrors'),[],'the transition produces no unhandled browser errors');
 });
});
