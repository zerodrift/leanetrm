import test from 'node:test';
import assert from 'node:assert/strict';
import {writeFile} from 'node:fs/promises';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

// Exercise the production editor with synthetic catalog/API answers, in a disposable browser.
// The fixture captures requests; it deliberately supplies no local pricing implementation.
test('Objects edits retained graph operations, exact contract mappings and published references through the normal API',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/app.css"><div id="host" style="width:1400px"></div>',async(evaluate,command)=>{
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
  await evaluate(`(async()=>{
   const {setupCustomCurves}=await import('/custom-curves-view.js');
   window.definition={code:'synthetic',name:'Synthetic object fixture',output:'out',nodes:[
    {id:'gas',kind:'source',parameters:{curve:'gas'},inputs:{},contractWeights:[]},
    {id:'set',kind:'select',parameters:{count:'2'},inputs:{source:'gas'}},
    {id:'dates',kind:'dates',parameters:{mode:'context'},inputs:{}},
    {id:'sample',kind:'sample',parameters:{policy:'strict'},inputs:{selection:'set',dates:'dates'}},
    {id:'out',kind:'output',parameters:{},inputs:{values:'sample'}}]};
   const exactContract={revision:{catalogId:'contract-catalog',revisionId:'contract-revision'},id:'contract-jan-exact'};
   window.retainedTerms={default:{settlement:'published_index_v1',publication:{catalogId:'publication-catalog',id:'gas-publication',version:2},index:{revision:{catalogId:'reference-catalog',revisionId:'reference-revision'},code:'gas-index'},observationRule:'single_assessment_v1',pendingPolicy:'require_final_v1',projectionPolicy:null},replacements:[],mappingRule:'same_delivery_period_v1',mappings:[{target:exactContract,source:exactContract}]};
   definition.nodes[0].contractTerms=structuredClone(retainedTerms);
   const op=(kind,label,inputPorts,outputType,fields=[],dynamicInputs)=>({kind,label,inputPorts,outputType,fields,...(dynamicInputs?{dynamicInputs}:{})});
   window.reference={catalogId:'catalog-exact',curveId:'published-parent',version:4,outputNode:'published-out'};
   window.catalog={kindsVersion:'curve_kinds_v1',canEdit:true,canPublish:false,recipes:[],contextRecipes:[],contextSchedules:[],contextSourceStatus:[],schedules:[],calendars:[],formulaSupported:false,
    sources:[{code:'gas',revisionId:'active-version',name:'Synthetic gas',kind:'market_settlement',contracts:[{id:'contract-jan-exact',label:'January',deliveryFrom:'2027-01-01',deliveryTo:'2027-02-01'}]},{code:'power',revisionId:'active-version',name:'Synthetic power',kind:'market_settlement',contracts:[]}],
    publishedSources:[{label:'Synthetic published parent with a long exact version and output label',kind:'custom',reference}],contextSources:[{label:'Synthetic published parent with a long exact version and output label',kind:'custom',reference}],
    templates:[{code:'fixture',name:'Synthetic fixture',definition}],operators:[
     op('source','Source curve',[],'curve',[{key:'curve',label:'Curve',type:'curve'}]),
     op('select','Select contracts',[{name:'source',type:'curve'}],'selection',[{key:'count',label:'Count',type:'integer'}]),
     op('dates','Date mapping',[],'dates',[{key:'mode',label:'Mode',type:'select',options:[{value:'context',label:'Context date'},{value:'explicit',label:'Explicit date'}]}]),
     op('sample','Sample prices',[{name:'selection',type:'selection'},{name:'dates',type:'dates'}],'panel',[{key:'policy',label:'Observation policy',type:'text'}]),
     op('output','Curve output',[{name:'values',type:'panel'}],'curve_result'),
     op('weighted_contracts','Contract mappings',[],'selection',[{key:'weights',label:'Exact mappings',type:'contract_weights'}],{type:'curve',label:'Source',prefix:'source_'}),
     op('published_source','Published curve',[],'panel',[{key:'reference',label:'Published output',type:'published_source'},{key:'selection',label:'Selection',type:'select',options:[{value:'all_output_points_v1',label:'All output points'}]}])]};
   window.requests=[];window.failures=[];window.terms=[];window.running=Promise.resolve();window.busyState=false;
   const api=async(path,body)=>{requests.push({path,body:structuredClone(body)});if(path.startsWith('custom-curves?'))return structuredClone(catalog);
    if(path==='custom-curves/preview'){if(window.awaiting)throw Object.assign(Error('Synthetic awaiting publication'),{code:'awaiting_publication'});return {columns:['Label','Price'],rows:[['Server supplied fixture','7.125']],summary:{points:1,unavailablePoints:0,firstPeriod:null,lastPeriod:null,unit:'Synthetic'}};}
    if(path.startsWith('contract-price-terms/status?'))return {snapshotId:'synthetic-close',curveId:'saved-exact',version:1,rows:[{nodeId:'gas',curveCode:'gas',targetContractId:'contract-jan-exact',sourceContractId:'contract-jan-exact',deliveryFrom:'2027-01-01',deliveryTo:'2027-02-01',expiry:'2026-12-29T00:00:00Z',state:'awaiting_publication',publicationId:'gas-publication',publicationVersion:2,index:'gas-index',occurrenceId:'january',expectedPublicationAt:'2027-01-04T15:00:00Z',publishedAt:null,releaseId:null,fixingPrice:null,selectedAsDependency:false,price:null,pricingError:'Awaiting final'}]};
    if(path==='custom-curves/save'){const recipe={id:'saved-exact',version:1,generation:1,state:'draft',kind:'custom',definition:body.definition};catalog.recipes=[recipe];catalog.contextRecipes=[recipe];return {...structuredClone(recipe),...(window.wrongDefinition?{definition:{...recipe.definition,name:'Unrelated curve'}}:{})};}throw Error('Unexpected API '+path);};
   window.editor=setupCustomCurves(document.querySelector('#host'),{headingSlot:document.body.appendChild(document.createElement('div')),api,act:work=>{running=work().catch(e=>failures.push(String(e)));return running;},busy:()=>busyState,zone:()=> 'UTC',identity:()=> 'synthetic-objects-test',source:()=>({snapshotId:'synthetic-close'}),historical:()=>false,narrow:()=>false,openContractTerms:selection=>terms.push(selection),assistant:{available:()=>false,draft:()=>{}}});
   await editor.load();document.querySelector('[data-template=fixture]').click();document.querySelector('#custom-curves-dock-graph').click();
  })()`);
  assert.equal(await evaluate("document.querySelector('#custom-curves-mode-objects').getAttribute('aria-pressed')"),'true');
  assert.equal(await evaluate("document.querySelector('#custom-curves-market')"),null,'ordinary catalogue has no population selector');
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.curve-object-card')].map(c=>c.dataset.objectId)"),['gas','set','out']);
  assert.equal(await evaluate("(async()=>{try{await editor.openSource('gas','old-version');return false;}catch(error){return String(error).includes('exact curve version');}})()"),true,'a stale source link cannot silently rebind its code to another version');
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.curve-object-card')].map(c=>c.dataset.objectId)"),['gas','set','out']);
  assert.equal(await evaluate("document.querySelectorAll('.curve-object-operation').length"),2);
  assert.equal(await evaluate("document.querySelector('[data-object-id=gas] .curve-object-terms').disabled"),true,'terms require an exact saved version');
  assert.equal(await evaluate("document.querySelector('[data-object-id=gas]').textContent.includes('exact contract mappings')"),false,'normalized empty fields do not label a source as a contract mapping');
  assert.equal(await evaluate("getComputedStyle(document.querySelector('.curve-object-operation')).color===getComputedStyle(document.querySelector('.curve-object-title')).color"),true,'connection labels have readable foreground color');
  assert.equal(await evaluate("(()=>{const board=document.querySelector('#custom-curves-objects').getBoundingClientRect();return [...document.querySelectorAll('.curve-object-card')].every(c=>c.getBoundingClientRect().right<=board.right);})()"),true,'a three-object graph fits the desktop board');
  if(process.env.CURVE_OBJECT_SCREENSHOT){await evaluate('new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))');const shot=await command('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});await writeFile(process.env.CURVE_OBJECT_SCREENSHOT,Buffer.from(shot.data,'base64'));}
  await evaluate("[...document.querySelectorAll('.curve-object-operation')].find(b=>b.getAttribute('aria-label').includes('from gas to set')).click()");
  await evaluate("document.querySelector('#custom-curves-mode-visual').click()");
  assert.equal(await evaluate("document.querySelector('#custom-curves-inspector h3').textContent"),'Selected operation');
  await evaluate("document.querySelector('#custom-curves-mode-objects').click()");
  await evaluate("(()=>{const input=document.querySelector('#custom-curve-property-count');input.value='3';input.dispatchEvent(new Event('change'));document.querySelector('#custom-curves-preview').click();return running;})()");
  assert.equal(await evaluate("requests.find(r=>r.path==='custom-curves/preview').body.definition.nodes.find(n=>n.id==='set').parameters.count"),'3');
  assert.deepEqual(await evaluate('failures'),[]);
  assert.equal(await evaluate("document.querySelector('#custom-curves-values').textContent.includes('7.125')"),true,'the view displays the API answer');
  await evaluate("window.wrongDefinition=true;document.querySelector('#custom-curves-save').click();running");
  await evaluate('editor.sync()');
  assert.equal(await evaluate("document.querySelector('#custom-curves-retry').closest('section,div').hidden"),false,'a different curve definition cannot acknowledge the command');
  assert.equal(await evaluate("document.querySelector('#custom-curves-save').disabled"),true,'an uncorrelated receipt retains the original pending command');
  await evaluate("window.wrongDefinition=false;document.querySelector('#custom-curves-retry').click();running");
  await evaluate('editor.sync()');
  const savedRequests=await evaluate("requests.filter(r=>r.path==='custom-curves/save').map(r=>r.body)");
  assert.equal(savedRequests.length,2);assert.deepEqual(savedRequests[1],savedRequests[0],'the exact curve definition and operation id survive retry');
  assert.equal(await evaluate("document.querySelector('#custom-curves-market')"),null);
  assert.equal(await evaluate('failures.length'),1);await evaluate('failures.length=0');
  assert.equal(await evaluate("requests.find(r=>r.path==='custom-curves/save').body.definition.nodes.find(n=>n.id==='set').parameters.count"),'3');
  assert.equal(await evaluate("requests.find(r=>r.path==='custom-curves/save').body.snapshotId"),'synthetic-close');
  assert.equal(await evaluate("JSON.stringify(requests.find(r=>r.path==='custom-curves/save').body.definition.nodes.find(n=>n.id==='gas').contractTerms)===JSON.stringify(retainedTerms)"),true,'normal graph save retains exact publication terms and mapping identities');
  await evaluate("window.awaiting=true;document.querySelector('#custom-curves-preview').click();running");
  assert.equal(await evaluate("document.querySelector('#custom-curves-values').textContent.includes('Awaiting publication')"),true,'a clean saved graph retains its exact pending contract row');
  assert.equal(await evaluate("document.querySelector('#custom-curves-chart').hidden"),true,'saved price states do not reserve a blank valuation chart above their rows');
  assert.equal(await evaluate('failures.length'),1);await evaluate('failures.length=0;window.awaiting=false');
  await evaluate("document.querySelector('[data-object-id=gas] .curve-object-terms').click();document.querySelector('[data-contract-terms=contract-jan-exact]').click()");
  assert.deepEqual(await evaluate('terms'),[{curveId:'saved-exact',version:1,nodeId:'gas'},{curveId:'saved-exact',version:1,nodeId:'gas',contractId:'contract-jan-exact'}]);
  await evaluate('busyState=true;editor.sync()');
  assert.equal(await evaluate("[...document.querySelectorAll('#custom-curves-objects button')].every(b=>b.disabled)"),true,'busy synchronization disables all object actions');
  await evaluate('busyState=false;editor.sync()');
  assert.equal(await evaluate("[...document.querySelectorAll('#custom-curves-objects button')].some(b=>b.disabled)"),false,'actions recover after the page request ends');
  await evaluate("document.querySelector('#custom-curves-dock-graph').click();new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
  await evaluate("document.querySelector('[data-object-id=gas] .curve-object-title').focus()");
  assert.equal(await evaluate("document.activeElement?.closest('[data-object-id]')?.dataset.objectId"),'gas');
  await evaluate("document.querySelector('.custom-curve-centre').style.width='800px';new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
  assert.equal(await evaluate("document.activeElement?.closest('[data-object-id]')?.dataset.objectId"),'gas','resizing the object canvas preserves its focused control');
  await evaluate("document.querySelector('.custom-curve-centre').style.width='';new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
  await evaluate("document.querySelector('#custom-curves-dock-graph').click();[...document.querySelectorAll('#custom-curves-operators button')].find(b=>b.textContent==='Contract mappings').click();document.querySelector('[data-contract-id=contract-jan-exact]').click()");
  assert.equal(await evaluate("document.querySelector('#custom-curves-object-target').options.length"),2);
  assert.equal(await evaluate("document.activeElement?.id"),'custom-curves-object-target','opening a connection moves keyboard focus to its explicit target');
  await command('Input.dispatchKeyEvent',{type:'keyDown',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
  assert.equal(await evaluate("document.querySelector('#custom-curves-object-target')===null"),true,'Escape removes connection controls');
  assert.equal(await evaluate("document.activeElement?.closest('[data-object-id]')?.dataset.objectId"),'gas','Escape returns focus to the source');
  await evaluate("document.querySelector('[data-contract-id=contract-jan-exact]').click()");
  await evaluate("document.querySelector('#custom-curves-object-target').value='0';document.querySelector('#custom-curves-object-connect').click()");
  assert.equal(await evaluate("document.querySelector('[aria-label=\"Contract for connection 1\"]').value"),'contract-jan-exact');
  assert.equal(await evaluate("document.querySelector('[aria-label=\"Weight for connection 1\"]').value"),'','picking a contract never chooses financial weight');
  assert.equal(await evaluate("document.querySelector('#custom-curves-preview').disabled"),true,'incomplete exact mappings cannot be previewed');
  await evaluate(`(()=>{for(const [label,value] of [['Output position','1'],['Weight','-0.200000000000000001']]){const input=document.querySelector('[aria-label="'+label+' for connection 1"]');input.value=value;input.dispatchEvent(new Event('input'));}document.querySelector('#custom-curves-preview').click();return running;})()`);
  assert.deepEqual(await evaluate("requests.filter(r=>r.path==='custom-curves/preview').at(-1).body.definition.nodes.find(n=>n.kind==='weighted_contracts').contractWeights"),[{outputPosition:1,sourcePort:'source_1',contractId:'contract-jan-exact',weight:'-0.200000000000000001'}]);
  await evaluate("document.querySelector('#custom-curves-dock-graph').click();document.querySelector('#custom-curves-add-published').value='0';[...document.querySelectorAll('#custom-curves-operators button')].find(b=>b.textContent==='Reuse published curve').click()");
  assert.equal(await evaluate("(()=>{const title=document.querySelector('[data-object-id=published_source_1] .curve-object-title');return title.scrollWidth<=title.clientWidth;})()"),true,'long published names wrap within their object card');
  assert.equal(await evaluate("document.querySelector('#custom-curve-property-selection').value"),'','published selection policy must be explicit');
  await evaluate("document.querySelector('#custom-curve-property-selection').value='0';document.querySelector('#custom-curve-property-selection').dispatchEvent(new Event('change'));document.querySelector('#custom-curves-preview').click();running");
  assert.deepEqual(await evaluate("requests.filter(r=>r.path==='custom-curves/preview').at(-1).body.definition.nodes.find(n=>n.kind==='published_source').publishedSource"),{catalogId:'catalog-exact',curveId:'published-parent',version:4,outputNode:'published-out'});
  assert.deepEqual(await evaluate('failures'),[]);
 });
});
