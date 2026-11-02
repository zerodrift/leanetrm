import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserAvailable,withBrowserPage} from './browser-page.mjs';

// Draw the real page and presentation modules without attaching an application,
// account or database. The API double supplies only a synthetic dashboard layout
// and an empty curve catalog; these checks concern the room left for controls.
const page=(await readFile(new URL('../src/index.html',import.meta.url),'utf8'))
 .replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,'')
 .replace('</body>','<div id="host" hidden></div></body>');

async function fixture(work){
 await withBrowserPage(page,async(evaluate,command)=>{
  await evaluate(`(async()=>{
   await Promise.all([...document.querySelectorAll('link[rel=stylesheet]')].map(link=>link.sheet?Promise.resolve():new Promise((resolve,reject)=>{
    link.addEventListener('load',resolve,{once:true});link.addEventListener('error',()=>reject(Error('Layout stylesheet did not load: '+link.href)),{once:true});
   })));
   const {setupScheduleWorkspace}=await import('/schedule-workspace.js');
   const {setupCustomCurves}=await import('/custom-curves-view.js');
   const {setupDashboard}=await import('/dashboard-view.js');
   const {setupMarketCalendar}=await import('/market-calendar.js');
   const {createAvailableDatePicker}=await import('/available-date-picker.js');
   setupMarketCalendar(document.getElementById('market-date-calendar'),{zone:()=> 'UTC',busy:()=>false,enabled:()=>false,
    selected:()=> '',current:()=>undefined,select:()=>{},open:()=>{},newer:()=>{}}).reset();
   for(const [name,label] of [['from','Delivery date from'],['through','Delivery date to']])createAvailableDatePicker(document.getElementById('delivery-price-'+name+'-calendar'),
    {id:'delivery-price-'+name+'-picker',label,busy:()=>false,onChange:()=>{}}).set(['2031-11-01'],'2031-11-01');
   window.scheduleWorkspace=setupScheduleWorkspace();
   const custom=setupCustomCurves(document.getElementById('custom-curves-page'),{
    api:async()=>({kindsVersion:'curve_kinds_v1',canEdit:true,canPublish:true,operators:[],templates:[],sources:[],calendars:[],schedules:[],recipes:[]}),
    act:work=>work(),busy:()=>false,zone:()=> 'UTC',identity:()=> 'synthetic-layout',source:()=>({snapshotId:'synthetic'}),historical:()=>false,
    narrow:()=>document.querySelector('main').clientWidth<900,assistant:{available:()=>true,draft:()=>{}}
   });
   await custom.load();
   const widgets=['latest_levels','price_chart'].map((kind,index)=>({id:'synthetic-'+index,kind,
    config:kind==='latest_levels'?{curves:['synthetic']}:{curve:'synthetic',contractId:'synthetic-contract'},
    board:{x:index*6,y:0,w:6,h:4},morning:{section:'prices_positions',order:index}}));
   const dashboard=setupDashboard({api:async path=>{
    if(path!=='dashboard/layout')throw Error('Unexpected layout fixture API: '+path);
    return {version:'synthetic-v1',layout:{version:1,arrangement:'board',widgets}};
   },act:work=>work(),busy:()=>false,historical:()=>false,market:()=>{throw Error('No market observations in this layout fixture.');}});
   await dashboard.load();
   for(const select of document.querySelectorAll('select'))if(!select.options.length)select.add(new Option('Synthetic choice','synthetic'));
   document.getElementById('curve-title').textContent='Synthetic power forward curve';
   document.getElementById('curve-power').hidden=false;document.getElementById('unit').textContent='USD / MWh';
   document.getElementById('curve-detail').hidden=false;document.getElementById('market-board').hidden=true;
   document.getElementById('dates-editor').hidden=false;document.getElementById('schedule-inspector').open=true;
   document.getElementById('job-editor').hidden=false;document.getElementById('job-editor').open=true;document.getElementById('job-create').hidden=false;
   document.getElementById('unit-converter').hidden=false;
   window.showLayoutPage=async id=>{
    for(const section of document.querySelectorAll('main>section'))section.hidden=section.id!==id;
    const temporal=!['jobs-page','delivery-page'].includes(id);
    document.querySelector('#snapshot').closest('.date-control').hidden=!temporal;
    document.getElementById('date-context').hidden=!temporal;
    await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
   };
   window.layoutBox=selector=>{const element=document.querySelector(selector),box=element.getBoundingClientRect();return {left:box.left,right:box.right,top:box.top,bottom:box.bottom,width:box.width,client:element.clientWidth,scroll:element.scrollWidth};};
   window.layoutVisible=element=>{
    // Closed details can retain layout boxes while their popover is not painted.
    for(let parent=element.parentElement;parent;parent=parent.parentElement)
     if(parent instanceof HTMLDetailsElement&&!parent.open&&!parent.querySelector(':scope>summary')?.contains(element))return false;
    return element.checkVisibility();
   };
   window.layoutOverflow=(scope,selector='button,input,select,textarea,summary')=>{
    const root=document.querySelector(scope),bounds=root.getBoundingClientRect();
    return [...root.querySelectorAll(selector)].filter(element=>layoutVisible(element)&&!element.closest('.table-wrap,.regular-table-scroll'))
     .map(element=>({id:element.id||element.textContent.trim().slice(0,60),box:element.getBoundingClientRect()}))
     .filter(({box})=>box.left<bounds.left-1||box.right>bounds.right+1).map(({id,box})=>({id,left:box.left,right:box.right}));
   };
   await document.fonts.ready;
  })()`);
  const size=async(width,assistant=0)=>{
   await command('Emulation.setDeviceMetricsOverride',{width,height:1100,deviceScaleFactor:1,mobile:false});
   await evaluate(`document.body.classList.toggle('assistant-open',${assistant>0});document.documentElement.style.setProperty('--assistant-width','${assistant}px');new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))`);
  };
  await work({evaluate,command,size});
 });
}

test('market controls use the available workspace width beside a wide assistant',{skip:!browserAvailable,timeout:30000},async()=>{
 await fixture(async({evaluate,size})=>{
  await size(1440,592); // 560 content pixels with the expanded navigation.
  await evaluate("showLayoutPage('market-page')");
  const [workspace,content]=await evaluate("['.market-layout','.market-content'].map(layoutBox)");
  assert.ok(workspace.width>=550&&workspace.width<=570,JSON.stringify(workspace));
  assert.ok(content.width>=workspace.width-1,JSON.stringify({workspace,content}));
  assert.deepEqual(await evaluate("layoutOverflow('.market-content')"),[]);
 });
});

test('schedule editors fill the available workspace and place the calendar below',{skip:!browserAvailable,timeout:30000},async()=>{
 await fixture(async({evaluate,size})=>{
  await size(1440,592);
  await evaluate("showLayoutPage('schedules-page')");
  const [workspace,editor,calendar]=await evaluate("['.schedule-workspace-content','.schedule-workspace-editor','.schedule-calendar'].map(layoutBox)");
  assert.ok(editor.width>=workspace.width-1,JSON.stringify({workspace,editor,calendar}));
  assert.ok(calendar.top>=editor.bottom-1,JSON.stringify({editor,calendar}));
  assert.deepEqual(await evaluate("layoutOverflow('.schedule-workspace-editor')"),[]);
  await evaluate("scheduleWorkspace.show('contracts')");
  assert.deepEqual(await evaluate("layoutOverflow('.schedule-workspace-editor')"),[]);
 });
});

test('curve-builder libraries leave usable editor widths beside the assistant',{skip:!browserAvailable,timeout:30000},async()=>{
 await fixture(async({evaluate,size})=>{
  await size(1440,592);
  await evaluate("showLayoutPage('custom-curves-page')");
  const [desk,main]=await evaluate("['.custom-curve-desk','.custom-curve-main'].map(layoutBox)");
  assert.ok(main.width>=desk.width-1,JSON.stringify({desk,main}));
  assert.deepEqual(await evaluate("layoutOverflow('.custom-curve-main')"),[]);
 });
});

test('sticky schedule actions stay clear of the floating date and context controls',{skip:!browserAvailable,timeout:30000},async()=>{
 await fixture(async({evaluate,size})=>{
  await size(1440,576); // The widest dock preserves a 640-pixel main area.
  await evaluate("showLayoutPage('schedules-page')");
  const overlaps=await evaluate(`(()=>{
   const actions=[...document.querySelectorAll('#dates-form>.schedule-actions button')];
   const dock=[...document.querySelectorAll('#context-dock button,#context-dock summary')].filter(layoutVisible);
   return actions.flatMap(action=>dock.flatMap(control=>{
    const a=action.getBoundingClientRect(),d=control.getBoundingClientRect(),width=Math.min(a.right,d.right)-Math.max(a.left,d.left),height=Math.min(a.bottom,d.bottom)-Math.max(a.top,d.top);
    return width>0&&height>0?[{action:action.id||action.textContent,control:control.id||control.tagName,width,height}]:[];
   }));
  })()`);
  assert.deepEqual(overlaps,[]);
 });
});

test('job date inputs stay inside a narrow workspace',{skip:!browserAvailable,timeout:30000},async()=>{
 await fixture(async({evaluate,size})=>{
  await size(1440,720); // The previous maximum-width dock left 432 content pixels.
  await evaluate("showLayoutPage('jobs-page')");
  assert.deepEqual(await evaluate("layoutOverflow('#job-create')"),[]);
 });
});

test('delivery filters do not create an overflowing implicit second column',{skip:!browserAvailable,timeout:30000},async()=>{
 await fixture(async({evaluate,size})=>{
  await size(1100,660); // Exercise the one-column filter fallback as well.
  await evaluate("showLayoutPage('delivery-page')");
  assert.deepEqual(await evaluate("layoutOverflow('.delivery-filter-fields')"),[]);
  const filters=await evaluate("layoutBox('.delivery-filter-fields')");
  assert.ok(filters.scroll<=filters.client+1,JSON.stringify(filters));
 });
});

test('dashboard widgets stack before their controls need horizontal scrolling',{skip:!browserAvailable,timeout:30000},async()=>{
 await fixture(async({evaluate,size})=>{
  await size(1440,592);
  await evaluate("showLayoutPage('dashboard-page');for(const details of document.querySelectorAll('.dashboard-arrange'))details.open=true");
  const board=await evaluate("layoutBox('.dashboard-board')");
  const cards=await evaluate("[...document.querySelectorAll('.dashboard-widget')].map(card=>{const box=card.getBoundingClientRect();return {width:box.width,top:box.top,bottom:box.bottom,scroll:card.scrollWidth,client:card.clientWidth};})");
  assert.equal(cards.length,2);
  for(const card of cards){assert.ok(card.width>=board.width-1,JSON.stringify({board,card}));assert.ok(card.scroll<=card.client+1,JSON.stringify(card));}
  assert.ok(cards[1].top>=cards[0].bottom-1,JSON.stringify(cards));
  assert.deepEqual(await evaluate("layoutOverflow('.dashboard-widget')"),[]);
  const actions=await evaluate("[...document.querySelectorAll('.dashboard-widget-actions')].map(group=>({arrange:group.querySelector('.dashboard-arrange-buttons').getBoundingClientRect().bottom,remove:group.querySelector(':scope>button').getBoundingClientRect().bottom}))");
  for(const row of actions)assert.ok(Math.abs(row.arrange-row.remove)<=1,JSON.stringify(row));
 });
});

test('phone schedule, reference and delivery controls fit without the assistant',{skip:!browserAvailable,timeout:30000},async()=>{
 await fixture(async({evaluate,size})=>{
  await size(375);
  for(const [page,scope] of [['schedules-page','.schedule-workspace-editor'],['reference-page','#reference-page'],['delivery-page','.delivery-filter-fields']]){
   await evaluate(`showLayoutPage('${page}')`);
   assert.deepEqual(await evaluate(`layoutOverflow('${scope}')`),[],page);
  }
 });
});
