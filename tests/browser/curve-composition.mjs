import {sideSession} from './side-session.mjs';

// Guided controls transport explicit choices; the managed graph supplies every price.
export async function run({command,evaluate,wait,click,set,persona,check,idle,url,fitsWidth,shot}){
 // A view of the curve panel, opening the panel first where the page had no room to open it by itself.
 const openView=async view=>{await evaluate("document.querySelector('#custom-curves-panel').hidden&&document.querySelector('#custom-curves-toggle-panel').click()");await click('#custom-curves-panel-tabs-'+view);};
 const actor=await sideSession(url,'editor'),directory=await actor.call('workspace/navigation');
 const frame=directory.frames.find(f=>f.at.startsWith('2020-12-18T16:00'));
 const template=(await actor.call('custom-curves')).templates.find(t=>t.code==='ng_prompt').definition;
 async function publish(code,source){
  await actor.call('profile',{profile:'editor'});const definition=structuredClone(template);definition.code=code;definition.name='Synthetic '+code;
  definition.nodes.find(n=>n.kind==='source').parameters.curve=source;
  const saved=await actor.call('custom-curves/save',{requestId:crypto.randomUUID(),snapshotId:frame.id,definition,layout:{}});
  await actor.call('profile',{profile:'approver'});return actor.call('custom-curves/publish',{requestId:crypto.randomUUID(),id:saved.id,version:saved.version,generation:saved.generation});
 }
 const first=await publish('browser_blend_base','base_forward'),second=await publish('browser_blend_parent','local_gas');
 const parentPrices=await Promise.all([first,second].map(p=>actor.call('custom-curves/preview',{snapshotId:frame.id,definition:p.definition})));
 const [a,b]=parentPrices.map(p=>p.points[0].price);const numerator=7n*BigInt(a.numerator)*BigInt(b.denominator)+3n*BigInt(b.numerator)*BigInt(a.denominator),denominator=10n*BigInt(a.denominator)*BigInt(b.denominator);
 await command('Runtime.enable');await command('Page.enable');
 await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.blendReads=[];{const send=window.fetch;window.fetch=async(...args)=>{const response=await send(...args);if(String(args[0]).includes('/custom-curves'))window.blendReads.push({path:String(args[0]),body:args[1]?.body?JSON.parse(args[1].body):null,status:response.status,data:await response.clone().json()});return response;};}`});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await persona('editor');const selectedClose=await evaluate("[...document.querySelector('#snapshot').options].find(o=>o.textContent.includes('Dec 18')&&o.textContent.includes('4:00')).value");await set('#snapshot',selectedClose);await wait(idle);await click('#nav-custom-curves');await openView('combine');
 await check('Combine curves opens as a mode of the curve panel',"(()=>{const panel=document.querySelector('#custom-curves-panel'),blend=document.querySelector('#curve-blend');return !panel.hidden&&panel.contains(blend)&&!blend.hidden&&blend.localName!=='details'&&document.querySelector('#custom-curves-panel-tabs-combine').getAttribute('aria-selected')==='true';})()");
 await check('a guided curve combination starts with explicit blank policy and ingredient choices',
  "document.querySelector('#curve-blend-aggregation').value===''&&document.querySelector('#curve-blend-alignment').value===''&&document.querySelector('.curve-blend-source').value===''&&document.querySelector('.curve-blend-weight').value===''");
 await set('#custom-curves-code','browser_combined');await set('#custom-curves-name','Synthetic combined curve');
 const choose=async(index,code)=>{const selector=`.curve-blend-row:nth-child(${index}) .curve-blend-source`;const value=await evaluate(`[...document.querySelector(${JSON.stringify(selector)}).options].find(o=>o.textContent.includes(${JSON.stringify(code)})).value`);await set(selector,value);};
 await choose(1,first.definition.code);await set('.curve-blend-weight','7');await click('#curve-blend-add');await choose(2,second.definition.code);await set('.curve-blend-row:nth-child(2) .curve-blend-weight','3');
 await set('#curve-blend-aggregation','normalized_weighted_mean_v1');await set('#curve-blend-alignment','exact_delivery_intervals_v1');await click('#curve-blend-build');
 await check('building installs backend graph, exact references and a readable explanation',"document.querySelectorAll('.custom-curve-node').length===4&&document.querySelector('#custom-curves-template-help').textContent.includes('7 × Synthetic browser_blend_base')&&document.querySelector('#curve-blend').hidden&&document.querySelector('#custom-curves-panel-tabs-operation').getAttribute('aria-selected')==='true'","({reads:window.blendReads.filter(r=>r.path.endsWith('/compose')).map(r=>({status:r.status,data:r.data})),error:document.querySelector('#error').textContent,help:document.querySelector('#custom-curves-template-help').textContent,nodes:document.querySelectorAll('.custom-curve-node').length,open:!document.querySelector('#curve-blend').hidden})");
 await click('#custom-curves-preview');
 await check('the computed combination displays the backend price and connected primitive evidence',`(()=>{const r=window.blendReads.filter(r=>r.path.endsWith('/preview')).at(-1),p=r.data.points?.[0]?.price;return r.status===200&&BigInt(p.numerator)*${denominator}n===${numerator}n*BigInt(p.denominator)&&r.data.dependencies.length>0&&!document.querySelector('#custom-curves-result').hidden;})()`,"(()=>{const r=window.blendReads.filter(r=>r.path.endsWith('/preview')).at(-1);return {status:r?.status,data:r?.data,error:document.querySelector('#error').textContent};})()");
 await openView('combine');await set('.curve-blend-weight','6');
 await check('editing proposed ingredients clears the old preview and generated description',"document.querySelector('#custom-curves-result').hidden&&document.querySelector('#custom-curves-template-help').textContent===''&&document.querySelector('#custom-curves-save').disabled&&document.querySelector('#custom-curves-preview').disabled");
 await click('#custom-curves-panel-tabs-details');
 await check('unbuilt ingredient changes name the way back to Combine curves from another view',"document.querySelector('#curve-blend').hidden&&document.querySelector('#custom-curves-state #custom-curves-open-combine')?.localName==='button'");
 await click('#custom-curves-open-combine');
 await check('the status opens the panel on Combine curves',"!document.querySelector('#custom-curves-panel').hidden&&!document.querySelector('#curve-blend').hidden&&document.querySelector('#custom-curves-panel-tabs-combine').getAttribute('aria-selected')==='true'");
 await set('.curve-blend-weight','7');await click('#curve-blend-build');await click('#custom-curves-save');
 await check('the ordinary save retains typed graph weights',"(()=>{const r=window.blendReads.filter(r=>r.path.endsWith('/save')).at(-1);return r.status===200&&r.data.definition.nodes.find(n=>n.kind==='weighted_panels').panelWeights.map(w=>w.weight).join()==='7,3';})()");
 const saved=await evaluate("window.blendReads.filter(r=>r.path.endsWith('/save')).at(-1).data");
 await openView('combine');await set('.curve-blend-weight','8');await click('#curve-blend-build');await click('#custom-curves-save');
 await check('replacing a selected graph preserves the saved identity and advances its generation',`(()=>{const r=window.blendReads.filter(r=>r.path.endsWith('/save')).at(-1);return r.status===200&&r.body.id===${JSON.stringify(saved.id)}&&r.data.generation===${saved.generation+1};})()`);
 await click('.custom-curve-node[data-node-id=curve_2] p');await click('#custom-curve-remove-node');await click('#custom-curves-preview');
 await check('removing a source prunes its hidden weight and leaves a valid one-input combination',`(()=>{const r=window.blendReads.filter(r=>r.path.endsWith('/preview')).at(-1);return r.status===200&&r.body.definition.nodes.find(n=>n.kind==='weighted_panels').panelWeights.length===1&&r.data.rows[0][1]===${JSON.stringify(parentPrices[0].rows[0][1])};})()`);
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await check('guided composition fits a narrow viewport',fitsWidth);await shot('curve-composition');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await openView('combine');await set('.curve-blend-weight','9');
 await evaluate(`{const send=window.fetch;window.fetch=async(...args)=>{const response=await send(...args);if(String(args[0])!=='/api/custom-curves'||!window.withoutCombination)return response;const data=await response.clone().json();data.operators=data.operators.filter(op=>op.kind!=='weighted_panels');return new Response(JSON.stringify(data),{status:response.status,headers:response.headers});};window.withoutCombination=true;}`);
 await click('#custom-curves-reload');
 await check('a catalog without combining drops the unbuilt ingredients and the Combine view',"document.querySelector('#custom-curves-panel-tabs-combine').hidden&&document.querySelector('#curve-blend').hidden&&document.querySelector('.curve-blend-weight').value===''&&!document.querySelector('#custom-curves-open-combine')&&!document.querySelector('#custom-curves-preview').disabled");
 await evaluate('window.withoutCombination=false');
 await persona('viewer');await click('#nav-custom-curves');
 await check('identity changes clear ingredient choices and keep authoring controls read-only',"document.querySelectorAll('.curve-blend-row').length===1&&document.querySelector('.curve-blend-weight').value===''&&document.querySelector('#curve-blend-build').disabled");
}
