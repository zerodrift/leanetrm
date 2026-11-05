import test from 'node:test';
import assert from 'node:assert/strict';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

const options={skip:!browserAvailable,timeout:browserTestTimeout};
const head='10000000-0000-0000-0000-000000000001',old='10000000-0000-0000-0000-000000000002',draft='10000000-0000-0000-0000-000000000003';
const page='<link rel="stylesheet" href="/app.css"><div id="host"><section id="gas-setup-page"></section><section id="crude-setup-page"></section></div>';
const terms={code:'synthetic_binding',deliveryReferenceCatalogId:'refs',deliveryReferenceRevisionId:'r1',commodity:'crude',grade:'grade',facility:'site',custodyPoint:'custody',clock:'utc',definitionRevisionId:'source',curve:'curve',validFrom:'2021-01-01',validTo:'2021-02-01',quantityPolicy:'same_volume'};
const crude={deliveryReferences:{catalogId:'refs',revisionId:'r1',commodities:[{code:'crude',name:'Crude'}],grades:[{code:'grade',commodity_code:'crude'}],locations:[{code:'site'},{code:'custody'}],deliveryPoints:[{commodity_code:'crude',grade_code:'grade',facility_code:'site',custody_point_code:'custody'}],quantities:[],units:[],clocks:[]},sources:[{definitionRevisionId:'source',curve:'curve',name:'Monthly curve'}],clocks:[],quantityPolicies:[{code:'same_volume',label:'Same standard volume'}],versions:[],proposals:[],proposal:null,contextSetups:[{id:head,state:'published',base_setup_id:null,publication_order:2,configuration:terms}],canSave:true,canPublish:true,fieldDescriptions:Object.fromEntries(['binding','code','commodity','grade','facility','custodyPoint','source','clock','policy','from','to','preview-from','preview-to','quantity'].map(k=>[k,'Definition for '+k]))};
const path={pipeline:'pipe',zone:'zone',point:'point',product:'gas',clock:'utc',dayStartMinute:540,groupCode:'group',adjustment:'0'};
const gas={activeSetupId:head,configuration:{paths:[path],groups:[{code:'group',pipeline:'pipe',product:'gas',curve:'curve',adjustment:'0'}],meters:[{code:'meter',pipeline:'pipe',zone:'zone',point:'point',product:'gas',identifier:'M1',quantity:'energy',validFrom:'2021-01-01',validTo:'2021-02-01'}],tariffs:[{code:'fee',pipeline:'pipe',product:'gas',quote:'rate',rate:'0.1',validFrom:'2021-01-01',validTo:'2021-02-01'}]},versions:[],proposals:[],proposal:null,canSave:true,canPublish:true,locations:['pipe','zone','point'].map(code=>({code})),products:[{code:'gas'}],clocks:[{code:'utc'}],quantities:[{code:'energy'}],quotes:[{code:'rate'}],curves:[{code:'curve'}],fieldDescriptions:Object.fromEntries(['tab','row','paths.pipeline','paths.zone','paths.point','paths.product','paths.clock','paths.dayStartMinute','paths.groupCode','paths.adjustment','groups.code','groups.pipeline','groups.product','groups.curve','groups.adjustment','meters.code','meters.pipeline','meters.zone','meters.point','meters.product','meters.identifier','meters.quantity','meters.validFrom','meters.validTo','tariffs.code','tariffs.pipeline','tariffs.product','tariffs.quote','tariffs.rate','tariffs.validFrom','tariffs.validTo','from','to'].map(k=>[k,'Definition for '+k]))};
gas.startingValues={records:{paths:{clock:'utc',dayStartMinute:540,adjustment:'0'},groups:{adjustment:'0'},meters:{validFrom:'2021-01-01',validTo:'2021-02-01'},tariffs:{validFrom:'2021-01-01',validTo:'2021-02-01'}},checkPeriod:{from:'2021-01-01',to:'2021-02-01'}};
crude.sources[0].suggestedDeliveryPeriod={from:'2021-01-01',to:'2021-02-01'};
crude.clocks=[{code:'utc',kind:'fixed_offset',fixed_offset_seconds:0}];

async function init(evaluate,kind,catalog,pending){
 await evaluate(`(async()=>{sessionStorage.clear();window.catalog=${JSON.stringify(catalog)};window.requests=[];window.historical=false;window.snapshot='frame';window.editorErrors=[];${pending?`sessionStorage.setItem('${kind}-setup-command:synthetic',${JSON.stringify(JSON.stringify(pending))});`:''}
 window.editor=(await import('/${kind}-setup-editor.js')).${kind==='gas'?'setupGasConfiguration':'setupCrudeConfiguration'}({api:async(path,body)=>{requests.push({path,body});if(path.includes('/catalog'))return structuredClone(catalog);if(path.endsWith('/preview'))return {presentValue:'80',currency:{code:'USD'},curve:'curve'};${!pending?"if(path.endsWith('/save')){catalog.proposal={id:body.operationId,state:'draft',base_setup_id:body.baseSetupId,configuration:body.configuration};return {setupId:body.operationId,state:'draft',operationId:body.operationId};}":''}return ${JSON.stringify(pending?{setupId:pending.body.setupId??pending.body.operationId,state:pending.path.endsWith('/save')?'draft':'published',operationId:pending.body.operationId}: {valid:true,points:[]})};},act:async work=>{try{await work();}catch(e){editorErrors.push(e.message);}},busy:()=>false,actor:()=>'synthetic',historical:()=>historical,source:()=>${kind==='gas'?"snapshot":"({snapshotId:snapshot})"},marketLabel:()=> 'Header context'});await editor.load();})()`);
}
const helpCheck="[...document.querySelectorAll('#host select')].filter(s=>!s.hidden).every(s=>{const help=document.getElementById(s.getAttribute('aria-describedby'));return help&&help.textContent.length>10&&!help.closest('label');})";

test('gas field definitions follow every category and editing explicitly starts a draft',options,async()=>{
 await withBrowserPage(page,async evaluate=>{
  await init(evaluate,'gas',gas);
  assert.match(await evaluate("document.querySelector('#gs-current').textContent"),/Edit setup for this context/);
  assert.equal(await evaluate("document.querySelector('#gs-field-pipeline').disabled"),true,'a contextual publication is initially read-only');
  await evaluate("(async()=>{document.querySelector('#gs-current').click();await new Promise(r=>setTimeout(r,0));})()");
  assert.equal(await evaluate("document.querySelector('#gs-field-pipeline').disabled"),false);
  for(const category of ['paths','groups','meters','tariffs']){
   await evaluate(`document.querySelector('#gs-tab').value='${category}';document.querySelector('#gs-tab').dispatchEvent(new Event('change'))`);
   assert.equal(await evaluate(helpCheck),true,'every visible dropdown has its own readable definition in '+category);
  }
  assert.match(await evaluate("document.querySelector('#gas-setup-page').textContent"),/header.*As of.*knowledge cutoff/);
  await evaluate("editor.reset();document.querySelector('#gs-row').dispatchEvent(new Event('change'))");
  assert.equal(await evaluate("document.querySelector('#gs-fields').childElementCount"),0,'reset cannot redraw old-context terms');
  assert.equal(await evaluate("document.querySelector('#gs-row').options.length"),0);
  assert.equal(await evaluate("document.querySelector('#gs-active').dataset.setupId??''"),'');
 });
});

test('new gas records use native starting values and sole choices without changing existing terms',options,async()=>{
 await withBrowserPage(page,async evaluate=>{
  await init(evaluate,'gas',gas);
  assert.equal(await evaluate("document.querySelector('#gs-from').value"),'2021-01-01');
  await evaluate("(async()=>{document.querySelector('#gs-current').click();await new Promise(r=>setTimeout(r,0));document.querySelector('#gs-add').click();})()");
  for(const [key,wanted] of [['product','gas'],['clock','utc'],['dayStartMinute','540'],['adjustment','0']])assert.equal(await evaluate(`document.querySelector('#gs-field-${key}').value`),wanted);
  assert.equal(await evaluate("document.querySelector('#gs-field-pipeline').value"),'','several locations still require a choice');
  assert.equal(await evaluate("document.querySelector('#gs-field-groupCode').value"),'','unpriced remains an explicit alternative to the single group');
  await evaluate("document.querySelector('#gs-field-product').value='';document.querySelector('#gs-field-product').dispatchEvent(new Event('input'));document.querySelector('#gs-row').dispatchEvent(new Event('change'))");
  assert.equal(await evaluate("document.querySelector('#gs-field-product').value"),'','redrawing does not undo an explicit clear');
  await evaluate("document.querySelector('#gs-tab').value='tariffs';document.querySelector('#gs-tab').dispatchEvent(new Event('change'));document.querySelector('#gs-add').click()");
  assert.equal(await evaluate("document.querySelector('#gs-field-quote').value"),'rate');
  assert.equal(await evaluate("document.querySelector('#gs-field-rate').value"),'','a tariff rate is not invented');
  assert.equal(await evaluate("document.querySelector('#gs-field-validFrom').value"),'2021-01-01');
  assert.match(await evaluate("document.querySelector('#gs-starting-values').textContent"),/Starting values/);
  await evaluate("(async()=>{for(const id of ['gs-from','gs-to']){document.getElementById(id).value='';document.getElementById(id).dispatchEvent(new Event('input'));}document.querySelector('#gs-save').click();await new Promise(r=>setTimeout(r,0));})()");
  assert.deepEqual(await evaluate('editorErrors'),[]);
  assert.equal(await evaluate("document.querySelector('#gs-from').value"),'','saving a draft does not restore explicitly cleared coverage');
  assert.equal(await evaluate("document.querySelector('#gs-to').value"),'');
 });
});

test('crude opens a sole contextual binding and new forms use sole choices and the native source period',options,async()=>{
 await withBrowserPage(page,async evaluate=>{
  await init(evaluate,'crude',crude);
  assert.equal(await evaluate("document.querySelector('#cs-binding').value"),'synthetic_binding');
  assert.equal(await evaluate("document.querySelector('#cs-grade').disabled"),true,'opening the sole binding does not edit it');
  await evaluate("(async()=>{document.querySelector('#cs-new').click();await new Promise(r=>setTimeout(r,0));})()");
  for(const [key,wanted] of [['commodity','crude'],['grade','grade'],['facility','site'],['custodyPoint','custody'],['clock','utc'],['policy','same_volume'],['from','2021-01-01'],['to','2021-02-01']])assert.equal(await evaluate(`document.querySelector('#cs-${key}').value`),wanted);
  assert.equal(await evaluate("document.querySelector('#cs-code').value"),'');
  assert.equal(await evaluate("document.querySelector('#cs-quantity').value"),'');
  await evaluate("document.querySelector('#cs-from').value='2021-01-03';document.querySelector('#cs-from').dispatchEvent(new Event('input'));document.querySelector('#cs-source').dispatchEvent(new Event('input'))");
  assert.equal(await evaluate("document.querySelector('#cs-from').value"),'2021-01-03','changing a source does not overwrite an entered delivery date');
  assert.match(await evaluate("document.querySelector('#cs-starting-values').textContent"),/Starting values/);
  await evaluate("for(const id of ['cs-from','cs-to']){document.getElementById(id).value='';document.getElementById(id).dispatchEvent(new Event('input'));}document.querySelector('#cs-source').dispatchEvent(new Event('input'))");
  assert.equal(await evaluate("document.querySelector('#cs-from').value"),'','source changes do not undo explicit date clears');
  assert.equal(await evaluate("document.querySelector('#cs-to').value"),'');
 });
});

test('multiple crude bindings and grades remain unselected, and retained drafts are not autofilled',options,async()=>{
 await withBrowserPage(page,async evaluate=>{
  const saved={id:draft,state:'draft',base_setup_id:null,configuration:{...terms,code:'other',grade:'',clock:'',validFrom:'',validTo:''}};
  const c={...crude,contextSetups:[...crude.contextSetups,{...crude.contextSetups[0],id:old,configuration:{...terms,code:'another'}}],proposals:[saved],deliveryReferences:{...crude.deliveryReferences,grades:[...crude.deliveryReferences.grades,{code:'other_grade',commodity_code:'crude'}]}};
  await init(evaluate,'crude',c);
  assert.equal(await evaluate("document.querySelector('#cs-binding').value"),'');
  assert.equal(await evaluate("document.querySelector('#cs-grade').value"),'');
  await evaluate(`(async()=>{document.querySelector('[data-crude-draft="${draft}"]').click();await new Promise(r=>setTimeout(r,0));})()`);
  assert.equal(await evaluate("document.querySelector('#cs-clock').value"),'');
  assert.equal(await evaluate("document.querySelector('#cs-from').value"),'');
 });
});

test('gas retry confirms a stale outcome without opening its old-context terms',options,async()=>{
 await withBrowserPage(page,async evaluate=>{
  const stale={id:draft,state:'draft',base_setup_id:old,configuration:{...gas.configuration,paths:[{...path,adjustment:'999'}]}};
  await init(evaluate,'gas',{...gas,proposal:stale},{path:'gas-setup/save',body:{operationId:draft,baseSetupId:old,configuration:stale.configuration},uncertain:true});
  await evaluate("(async()=>{document.querySelector('#gs-retry').click();await new Promise(r=>setTimeout(r,0));})()");
  assert.equal(await evaluate("document.querySelector('#gs-field-adjustment').value"),'0');
  assert.equal(await evaluate("document.querySelector('#gs-validate').disabled"),true);
  assert.match(await evaluate("document.querySelector('#gs-status').textContent"),/outside.*context/);
  assert.equal(await evaluate("requests.find(r=>r.path==='gas-setup/save').body.baseSetupId"),old,'recovery retains the original command');
 });
});

test('crude shows field definitions and reads a historical header without enabling authoring',options,async()=>{
 await withBrowserPage(page,async(evaluate,command)=>{
  await init(evaluate,'crude',crude);
  assert.equal(await evaluate(helpCheck),true);
  await evaluate("(async()=>{historical=true;editor.sync();document.querySelector('#cs-binding').value='synthetic_binding';document.querySelector('#cs-binding').dispatchEvent(new Event('change'));await new Promise(r=>setTimeout(r,0));})()");
  assert.equal(await evaluate("document.querySelector('#cs-preview').disabled"),false,'retained context permits a read');
  assert.equal(await evaluate("document.querySelector('#cs-preview-from').disabled"),false,'delivery dates remain explicit contract parameters');
  assert.equal(await evaluate("document.querySelector('#cs-revise').disabled"),true);
  await evaluate("(async()=>{document.querySelector('#cs-preview-from').value='2021-01-01T00:00:00';document.querySelector('#cs-preview-to').value='2021-02-01T00:00:00';document.querySelector('#cs-quantity').value='1';document.querySelector('#cs-preview').click();await new Promise(r=>setTimeout(r,0));})()");
  assert.equal(await evaluate("requests.at(-1).body.knowledgeMode"),'context');
  assert.equal(await evaluate("requests.at(-1).body.snapshotId"),'frame');
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  assert.equal(await evaluate('document.documentElement.scrollWidth<=innerWidth'),true);
  await evaluate('editor.reset()');
  assert.equal(await evaluate("document.querySelector('#cs-code').value"),'');
  assert.equal(await evaluate("document.querySelector('#cs-binding').options.length"),0);
  assert.equal(await evaluate("document.querySelector('#cs-quantity').value"),'');
 });
});

for(const state of ['draft','published'])test('crude recovery retains the '+state+' receipt without exposing a superseded binding',options,async()=>{
 await withBrowserPage(page,async evaluate=>{
  const stale={id:old,state,base_setup_id:state==='draft'?null:head,publication_order:1,configuration:{...terms,validFrom:'2019-01-01'}};
  await init(evaluate,'crude',{...crude,proposal:stale},{path:'crude-setup/'+(state==='draft'?'save':'publish'),body:{operationId:old,setupId:old,configuration:stale.configuration},uncertain:true});
  await evaluate("(async()=>{document.querySelector('#cs-retry').click();await new Promise(r=>setTimeout(r,0));})()");
  assert.notEqual(await evaluate("document.querySelector('#cs-from').value"),'2019-01-01');
  assert.equal(await evaluate("document.querySelector('#cs-version').value"),head);
  assert.equal(await evaluate("document.querySelector('#cs-save').disabled"),true);
  assert.match(await evaluate("document.querySelector('#cs-status').textContent"),/outside.*context/);
 });
});

for(const kind of ['gas','crude'])test(kind+' inspection waits for a resolved header context instead of calling the raw catalog',options,async()=>{
 await withBrowserPage(page,async evaluate=>{
  await init(evaluate,kind,kind==='gas'?gas:crude);
  await evaluate("snapshot='';requests=[];editor.reset();editor.load()");
  assert.equal(await evaluate('requests.length'),0);
  assert.match(await evaluate(`document.querySelector('#${kind==='gas'?'gs':'cs'}-status').textContent`),/header/);
  assert.equal(await evaluate(`document.querySelector('#${kind==='gas'?'gs':'cs'}-${kind==='gas'?'current':'new'}').disabled`),true);
 });
});
