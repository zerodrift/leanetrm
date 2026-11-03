import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage} from './browser-page.mjs';

test('each dock pane can fill the dock and restore its arrangement and unsaved controls',{skip:!browserAvailable},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/app.css"><div id="host" style="width:800px;height:600px"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupDock}=await import('/dock.js');
   const tabs=['summary','grid','details','charts'].map((id,i)=>{const body=document.createElement('section');body.id='pane-'+id;body.innerHTML='<input aria-label="Synthetic '+id+' edit" value="keep '+id+'">';return {id,label:['Summary','Positions','Details','Charts'][i],body};});
   const group=id=>({kind:'group',tabs:[id],active:id});
   window.initial={layout:{kind:'split',direction:'column',ratio:.25,children:[group('summary'),{kind:'split',direction:'row',ratio:.5,children:[group('grid'),{kind:'split',direction:'column',ratio:.5,children:[group('details'),group('charts')]}]}]},collapsed:[]};
   window.layoutChanges=0;
   window.dock=setupDock(document.getElementById('host'),{tabs,initial:initial,rules:{maxDepth:4,fixed:['grid'],ratioRange:[.05,.95]},narrow:()=>false,maximizable:true,changed:()=>layoutChanges++});
  })()`);
  for(const [id,label] of [['summary','Summary'],['grid','Positions'],['details','Details'],['charts','Charts']]){
   assert.equal(await evaluate(`!!document.querySelector('button[aria-label="Maximize ${label}"]')`),true,label);
   await evaluate(`document.querySelector('button[aria-label="Maximize ${label}"]').click()`);
   assert.equal(await evaluate(`document.getElementById('host').dataset.maximized`),id);
   assert.deepEqual(await evaluate(`['summary','grid','details','charts'].filter(id=>document.getElementById('pane-'+id).checkVisibility())`),[id]);
   assert.equal(await evaluate(`document.getElementById('pane-${id}').closest('.dock-group').getBoundingClientRect().height>=590`),true);
   await evaluate(`document.querySelector('button[aria-label="Restore ${label}"]').click()`);
   assert.equal(await evaluate('JSON.stringify(dock.arrangement())===JSON.stringify(initial)&&layoutChanges===0'),true);
   assert.equal(await evaluate(`document.querySelector('#pane-${id} input').value`),'keep '+id);
  }
 });
});
test('a maximized tab keeps a restore control through narrow layouts, empty Details and reset',{skip:!browserAvailable},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/app.css"><div id="host" style="width:600px;height:600px"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupDock}=await import('/dock.js');window.narrow=false;
   const tabs=['grid','details'].map(id=>({id,label:id==='grid'?'Positions':'Details',body:document.createElement('section')}));
   window.initial={layout:{kind:'split',direction:'column',ratio:.6,children:tabs.map(t=>({kind:'group',tabs:[t.id],active:t.id}))},collapsed:[]};
   window.dock=setupDock(document.getElementById('host'),{tabs,initial,rules:{maxDepth:3,fixed:['grid'],ratioRange:[.05,.95]},narrow:()=>narrow,maximizable:true,changed:()=>{}});
   dock.maximize('grid');narrow=true;dock.relayout();document.querySelector('#host-details').click();
  })()`);
  assert.equal(await evaluate("document.querySelector('#host [aria-label=\"Restore Details\"]').checkVisibility()"),true);
  await evaluate('narrow=false;dock.relayout();dock.restore()');
  assert.equal(await evaluate('JSON.stringify(dock.arrangement())===JSON.stringify(initial)'),true);
  await evaluate("dock.maximize('details');dock.collapse('details')");
  assert.equal(await evaluate('dock.maximized()'),null);
  await evaluate("dock.maximize('grid');dock.set(initial)");
  assert.equal(await evaluate('dock.maximized()'),null);
 });
});
test('maximizing Details preserves its form and unsaved guard, and closing restores the other panes',{skip:!browserAvailable},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/app.css"><div id="host" style="width:800px;height:600px"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupDock}=await import('/dock.js'),{setupDetailsPane}=await import('/details-pane.js');
   const grid=document.createElement('section'),details=document.createElement('section');window.edits=false;
   const group=id=>({kind:'group',tabs:[id],active:id});
   window.dock=setupDock(document.getElementById('host'),{tabs:[{id:'grid',label:'Positions',body:grid},{id:'details',label:'Details',body:details}],initial:{layout:{kind:'split',direction:'column',ratio:.5,children:[group('grid'),group('details')]},collapsed:[]},rules:{maxDepth:3,fixed:['grid'],ratioRange:[.05,.95]},narrow:()=>false,maximizable:true,ownCollapse:['details'],changed:()=>{}});
   window.pane=setupDetailsPane(details,{unsaved:()=>edits,busy:()=>false,step:()=>{},collapsed:()=>dock.collapse('details'),expand:()=>dock.reveal('details'),changed:()=>{if(pane.shell.current()===null)dock.collapse('details');}});
   await pane.show({kind:'new',product:'swaps'});pane.shell.form(true);
   window.form=pane.shell.element('form');form.innerHTML='<input value="Synthetic edit">';edits=true;
   document.querySelector('[aria-label="Maximize Details"]').click();
  })()`);
  assert.equal(await evaluate("dock.maximized()==='details'&&pane.shell.element('form')===form&&!form.hidden&&document.querySelector('.side-panel-prompt').hidden"),true);
  await evaluate("window.closing=pane.collapse();true");
  await evaluate("document.querySelector('.side-panel-prompt .secondary').click();closing");
  assert.equal(await evaluate("dock.maximized()==='details'&&form.querySelector('input').value==='Synthetic edit'"),true);
  await evaluate("document.querySelector('[aria-label=\"Restore Details\"]').click()");
  assert.equal(await evaluate("!dock.maximized()&&pane.shell.element('form')===form&&!form.hidden"),true);
  await evaluate("dock.maximize('details');edits=false;pane.shell.reset()");
  assert.equal(await evaluate("!dock.maximized()&&dock.showing('grid')"),true);
 });
});
