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
