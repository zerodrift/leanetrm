import {sideSession} from './side-session.mjs';

// The synthetic crude formula fixture (tools/crude_formula_fixture.py) at its 2026-12-01 12:00 close. The browser adds the contract
// observation operation to a draft, enters its settings and terms exactly as typed, and reads back what the backend prices and keeps.
// Every choice offered comes from the custom-curve catalog; the expected price is the fixture's stated value.
const TERMS=[{group:'average',weight:'1',position:'1',lessPosition:'',period:'calendar_month_of_delivery_v1'},
 {group:'roll',weight:'2',position:'1',lessPosition:'2',period:'trade_month_of_delivery_contract_v1'},
 {group:'roll',weight:'1',position:'1',lessPosition:'3',period:'trade_month_of_delivery_contract_v1'}];
const SENT=[{ordinal:0,group:'average',weight:'1',position:1,lessPosition:null,period:'calendar_month_of_delivery_v1'},
 {ordinal:1,group:'roll',weight:'2',position:1,lessPosition:2,period:'trade_month_of_delivery_contract_v1'},
 {ordinal:2,group:'roll',weight:'1',position:1,lessPosition:3,period:'trade_month_of_delivery_contract_v1'}];
const SETTINGS={implementation:'contract_observation_terms_v1',days:'business_days_v1',calendar:'synthetic_crude_days',roll:'contract_expiry_roll_v1',
 sampling:'context_utc_time',corrections:'exact_context_frame_history_latest_visible'};

export async function run({command,evaluate,wait,click,set,persona,check,idle,url}){
 const actor=await sideSession(url,'editor'),workspace=await actor.call('workspace');
 const frame=workspace.frames.find(f=>workspace.initialFrameIds.includes(f.id)&&f.at.startsWith('2026-12-01T'));
 if(!frame?.close?.session)throw Error('The synthetic crude fixture has no 2026-12-01 session close');
 const catalog=await actor.call('custom-curves?snapshotId='+frame.id),operator=catalog.operators.find(o=>o.kind==='contract_observations');
 if(!operator)throw Error('The catalog does not offer the contract observation operation');
 // The draft the browser completes: the differential's context sample in a combination that feeds the output.
 const draft=await actor.call('custom-curves/save',{requestId:crypto.randomUUID(),snapshotId:frame.id,layout:{},definition:{code:'browser_crude_formula',name:'Synthetic crude formula',output:'output',nodes:[
  {id:'differential',kind:'source',parameters:{curve:'synthetic_crude_differential'},inputs:{}},
  {id:'select',kind:'select',parameters:{mode:'positions',first:'1',count:'1',binding:'context'},inputs:{source:'differential'}},
  {id:'dates',kind:'dates',parameters:{mode:'context',sampling:'context_utc_time',corrections:'exact_context_frame_history_latest_visible'},inputs:{}},
  {id:'sample',kind:'sample',parameters:{},inputs:{selection:'select',dates:'dates'}},
  {id:'total',kind:'weighted_panels',parameters:{aggregation:'signed_sum_v1',alignment:'exact_delivery_intervals_v1'},inputs:{differential:'sample'},panelWeights:[{sourcePort:'differential',weight:'1'}]},
  {id:'output',kind:'output',parameters:{},inputs:{values:'total'}}]}});
 const node='contract_observations_1',card=id=>`#custom-curves-objects [data-object-id="${id}"]`,term=(field,n)=>`[aria-label="${field} for term ${n}"]`;
 const last=path=>`window.observationRequests.filter(r=>r.path==='/api/custom-curves/${path}').at(-1)`;
 const detail=`({error:document.querySelector('#error')?.textContent,refusal:document.querySelector('#custom-curves-refusal')?.textContent,state:document.querySelector('#custom-curves-state')?.textContent,requests:window.observationRequests.slice(-2)})`;
 const observed=`.body.definition.nodes.find(n=>n.kind==='contract_observations')`;
 // A setting is chosen by the label the catalog gives its value.
 async function choose(key,value){
  const field=operator.fields.find(f=>f.key===key),label=field.type==='calendar'?catalog.calendars.find(c=>c.code===value)?.name??value:field.options.find(o=>o.value===value)?.label;
  const selector='#custom-curve-property-'+key,index=await evaluate(`[...document.querySelector(${JSON.stringify(selector)}).options].find(o=>o.textContent===${JSON.stringify(label)})?.value`);
  if(index===undefined)throw Error('Missing offered choice '+key+' · '+label);await set(selector,index);
 }
 async function selectObject(id){await click(card(id)+' .curve-object-title');}
 const readBack=`JSON.stringify([...document.querySelectorAll('[data-term-row]')].map(row=>['Group','Weight','Position','Less position','Period'].map(f=>row.querySelector('[aria-label^="'+f+' for term"]').value)))`;

 await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.observationRequests=[];{const send=window.fetch;window.fetch=async(...args)=>{const response=await send(...args),path=new URL(String(args[0]),location.href).pathname;if(path.startsWith('/api/custom-curves/'))window.observationRequests.push({path,body:args[1]?.body?JSON.parse(args[1].body):null,status:response.status,data:await response.clone().json()});return response;};}`});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('editor');
 // The fixture's 12:00 closes are recorded under their own session.
 await set('#close-session',frame.close.session);await wait(idle);await set('#snapshot',frame.id);await wait(idle);
 await check('the fixture close is selected',`document.querySelector('#snapshot').value===${JSON.stringify(frame.id)}`,"[...document.querySelector('#snapshot').options].map(o=>o.value+' '+o.textContent)");
 await click('#nav-custom-curves');
 // The library already holds the HTTP check's published observation formula; every saved curve is listed with its kind.
 await check('the library lists the draft beside the saved observation curve',`!!document.querySelector('#custom-curves-library [data-recipe-edit="${draft.id}|${draft.version}"]')`,"document.querySelector('#error')?.textContent");
 await click(`#custom-curves-library [data-recipe-edit="${draft.id}|${draft.version}"]`);await click('#custom-curves-dock-graph');await click('#custom-curves-mode-objects');

 await set('#custom-curves-add-source','synthetic_crude_future');await click('#custom-curves-add-source-button');
 const add=await evaluate(`(()=>{const b=[...document.querySelectorAll('#custom-curves-operators button')].find(b=>b.textContent===${JSON.stringify(operator.label)});if(!b)return false;b.click();return true;})()`);
 if(!add)throw Error('The palette does not offer '+operator.label);await wait(idle);
 await check('the operation is added as a result object under the catalog’s label',`document.querySelector(${JSON.stringify(card(node))})?.dataset.role==='result'&&document.querySelector('#custom-curves-inspector h4')?.textContent===${JSON.stringify(operator.label)}`);
 await check('its settings are offered without a chosen value and its terms start empty',`['implementation','days','roll','sampling','corrections'].every(k=>document.querySelector('#custom-curve-property-'+k).value==='')&&document.querySelector('#custom-curve-property-targetFrom').value===''&&!document.querySelector('#custom-curve-property-calendar')&&document.querySelectorAll('[data-term-row]').length===0`);
 await set('#custom-curves-properties [data-input-port=source]','source_1');
 await choose('implementation',SETTINGS.implementation);await set('#custom-curve-property-targetFrom','2027-01-01');await set('#custom-curve-property-targetCount','1');
 await choose('days',SETTINGS.days);
 await check('business days offer the calendar choice',"!!document.querySelector('#custom-curve-property-calendar')");
 await choose('calendar',SETTINGS.calendar);for(const key of ['roll','sampling','corrections'])await choose(key,SETTINGS[key]);
 await click('#custom-curve-add-term');
 await check('a new term has no chosen value and its periods are the catalog’s',`['Group','Weight','Position','Less position','Period'].every(f=>document.querySelector('[aria-label="'+f+' for term 1"]').value==='')&&JSON.stringify([...document.querySelector(${JSON.stringify(term('Period',1))}).options].filter(o=>o.value).map(o=>[o.value,o.textContent]))===${JSON.stringify(JSON.stringify(operator.fields.find(f=>f.key==='observationTerms').options.map(o=>[o.value,o.label])))}`);
 await click('#custom-curve-add-term');await click('#custom-curve-add-term');
 for(const [i,row] of TERMS.entries())for(const [field,value] of [['Group',row.group],['Weight',row.weight],['Position',row.position],['Less position',row.lessPosition],['Period',row.period]])await set(term(field,i+1),value);
 await selectObject('total');await click('#custom-curve-add-source');await set('#custom-curves-properties [data-input-port=source_1]',node);await set('[aria-label="Weight for source_1"]','1');
 await click('#custom-curves-preview');
 await check('preview sends the typed terms with an explicit empty second position and shows the backend price 68.46',
  `(()=>{const r=${last('preview')},n=r${observed};return r.status===200&&JSON.stringify(n.observationTerms)===${JSON.stringify(JSON.stringify(SENT))}&&n.inputs.source==='source_1'&&r.data.rows.length===1&&document.querySelector('#custom-curves-values').textContent.includes('68.46');})()`,detail);
 await click('#custom-curves-save');
 await check('the saved draft is an observation formula whose terms are the ones sent',`(()=>{const r=${last('save')};return r.status===200&&r.data.kind==='observation_formula'&&JSON.stringify(r.data.definition.nodes.find(n=>n.kind==='contract_observations').observationTerms)===${JSON.stringify(JSON.stringify(SENT))};})()`,detail);
 await check('the library lists the saved curve under its kind',`document.querySelector('#custom-curves-library [data-recipe="${draft.id}|${draft.version}"]')?.getAttribute('aria-label').includes('Observation formula')&&document.querySelector('#custom-curves-kind-observation_formula')?.textContent==='Observation formula'`);

 await click('#custom-curves-reload');await click('#custom-curves-dock-graph');await click('#custom-curves-mode-objects');await selectObject(node);
 await check('reload reads back every term and setting unchanged',`${readBack}===${JSON.stringify(JSON.stringify(TERMS.map(t=>[t.group,t.weight,t.position,t.lessPosition,t.period])))}&&document.querySelector('#custom-curve-property-targetFrom').value==='2027-01-01'&&document.querySelector('#custom-curve-property-calendar').selectedOptions[0].textContent===${JSON.stringify(catalog.calendars.find(c=>c.code===SETTINGS.calendar)?.name??SETTINGS.calendar)}`,readBack);

 await set(term('Weight',2),'');
 await check('clearing a weight disables nothing else',`!document.querySelector('#custom-curves-preview').disabled&&!document.querySelector('#custom-curves-save').disabled&&[...document.querySelectorAll('#custom-curves-properties input,#custom-curves-properties select')].every(n=>!n.disabled)`);
 await click('#custom-curves-preview');
 await check('the backend refuses the cleared weight and its message is shown beside the operation',`(()=>{const r=${last('preview')},n=r${observed};return r.status===422&&r.data.code==='recipe_definition'&&n.observationTerms[1].weight===''&&!document.querySelector('#error').hidden&&document.querySelector('#custom-curves-refusal').textContent===r.data.message&&document.querySelector('#custom-curves-inspector').contains(document.querySelector('#custom-curves-refusal'));})()`,detail);

 // A new term whose position is not yet entered is sent with an explicit null; the backend names that term's position.
 await set(term('Weight',2),'2');await click('#custom-curve-add-term');
 for(const [field,value] of [['Group','roll'],['Weight','1'],['Period','trade_month_of_delivery_contract_v1']])await set(term(field,4),value);
 await click('#custom-curves-preview');
 await check('the backend refuses the blank position of the new term and its message is shown beside the operation',`(()=>{const r=${last('preview')},n=r${observed};return r.status===422&&r.data.code==='recipe_definition'&&n.observationTerms.length===4&&n.observationTerms[3].position===null&&r.data.message.startsWith('Observation term 3 position:')&&!document.querySelector('#error').hidden&&document.querySelector('#custom-curves-refusal').textContent===r.data.message&&document.querySelector('#custom-curves-inspector').contains(document.querySelector('#custom-curves-refusal'));})()`,detail);
}
