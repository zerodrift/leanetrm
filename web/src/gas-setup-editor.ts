type Row=Record<string,string|number|null>;
type Configuration={paths:Row[];groups:Row[];meters:Row[];tariffs:Row[]};
type Version={id:string;state:string;base_setup_id:string|null;configuration:Configuration};
type Master={code:string;name?:string;symbol?:string};
type Catalog={activeSetupId:string|null;configuration:Configuration;versions:Version[];canSave:boolean;canPublish:boolean;locations:Master[];products:Master[];clocks:Master[];quantities:Master[];quotes:Master[];curves:Master[]};
type Tab=keyof Configuration;
type Field={key:string;label:string;master?:'locations'|'products'|'clocks'|'quantities'|'quotes'|'curves'|'groups';kind?:'date'|'number';nullable?:boolean};
const fields:Record<Tab,Field[]>={
 paths:[{key:'pipeline',label:'Pipeline',master:'locations'},{key:'zone',label:'Zone',master:'locations'},{key:'point',label:'Delivery point',master:'locations'},{key:'product',label:'Product',master:'products'},{key:'clock',label:'Gas-day clock',master:'clocks'},{key:'dayStartMinute',label:'Gas-day start · minute of local day',kind:'number'},{key:'groupCode',label:'Valuation group',master:'groups',nullable:true},{key:'adjustment',label:'Point price adjustment · curve quote units'}],
 groups:[{key:'code',label:'Group code'},{key:'pipeline',label:'Pipeline',master:'locations'},{key:'product',label:'Product',master:'products'},{key:'curve',label:'Shared curve',master:'curves'},{key:'adjustment',label:'Group price adjustment · curve quote units'}],
 meters:[{key:'code',label:'Assignment code'},{key:'pipeline',label:'Pipeline',master:'locations'},{key:'zone',label:'Zone',master:'locations'},{key:'point',label:'Delivery point',master:'locations'},{key:'product',label:'Product',master:'products'},{key:'identifier',label:'Pipeline meter identifier'},{key:'quantity',label:'Native quantity specification',master:'quantities'},{key:'validFrom',label:'Valid from · operating day',kind:'date'},{key:'validTo',label:'Valid to · exclusive',kind:'date'}],
 tariffs:[{key:'code',label:'Tariff code'},{key:'pipeline',label:'Pipeline',master:'locations'},{key:'product',label:'Product',master:'products'},{key:'quote',label:'Per-unit rate quote',master:'quotes'},{key:'rate',label:'Rate · quote units'},{key:'validFrom',label:'Valid from · operating day',kind:'date'},{key:'validTo',label:'Valid to · exclusive',kind:'date'}]
};
export function setupGasConfiguration(o:{api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;actor:()=>string;historical:()=>boolean}){
 const root=document.getElementById('gas-setup-page')!;
 root.innerHTML=`<section class="card"><h2>Gas delivery setup</h2><p>Manage delivery-point relationships, shared pricing groups, dated meters and per-unit tariffs using existing master references. Publishing creates an immutable version for new bookings. Saved trades retain their original setup.</p><p id="gs-active" role="status"></p>
 <div class="gas-setup-toolbar"><label>Saved version<select id="gs-version"></select></label><button id="gs-open" type="button" class="secondary">Open version</button><button id="gs-current" type="button" class="secondary">Start from current setup</button></div>
 <label class="gas-setup-category">Category<select id="gs-tab"><option value="paths">Delivery points</option><option value="groups">Shared pricing groups</option><option value="meters">Meter assignments</option><option value="tariffs">Usage tariffs</option></select></label>
 <p class="field-help">Price adjustments change physical MTM. Tariffs create separate contractual charges. Meter assignments identify contractual schedules; they do not create measurements or allocations.</p>
 <div class="gas-setup-toolbar"><label>Record<select id="gs-row"></select></label><button id="gs-add" type="button" class="secondary">Add record</button><button id="gs-remove" type="button" class="secondary">Remove record</button></div><form id="gs-fields" class="schedule-fields"></form>
 <div class="schedule-actions"><button id="gs-save" type="button">Save draft version</button><button id="gs-retry" type="button" hidden>Retry original change</button></div><p id="gs-status" role="status"></p></section>
 <section class="card"><h3>Validate and preview a saved version</h3><div class="schedule-fields"><label>Delivery from<input id="gs-from" type="date"></label><label>Delivery to · exclusive<input id="gs-to" type="date"></label></div><p class="field-help">Choose up to 31 operating days to inspect the resolved curve, index, adjustments and meter coverage. Unpriced points and missing meters remain visible.</p><div class="schedule-actions"><button id="gs-validate" type="button">Validate saved version</button><button id="gs-publish" type="button" disabled>Publish validated version</button></div><p id="gs-validation" role="status"></p><div id="gs-preview" class="table-wrap"></div></section>`;
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const value=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id).value;
 let catalog:Catalog|undefined,configuration:Configuration={paths:[],groups:[],meters:[],tariffs:[]},base:string|null=null,draft:string|undefined,dirty=false,valid=false,epoch=0;
 let pending:{path:string;body:Record<string,unknown>;uncertain:boolean}|undefined;
 const key=()=>`gas-setup-command:${o.actor()}`;
 function retain(){try{if(pending)sessionStorage.setItem(key(),JSON.stringify(pending));else sessionStorage.removeItem(key());}catch{/* Exact command stays in memory. */}}
 function restore(){try{const p=JSON.parse(sessionStorage.getItem(key())??'null');if(['gas-setup/save','gas-setup/publish'].includes(p?.path)&&typeof p?.body?.operationId==='string')pending={path:p.path,body:p.body,uncertain:true};}catch{/* Local transport state grants no authority. */}}
 const tab=()=>value('gs-tab') as Tab;
 const rowIndex=()=>Number(value('gs-row'));
 function sync(){
  const locked=o.busy()||!!pending||o.historical();
  for(const n of root.querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select'))n.disabled=locked;
  for(const n of get('gs-fields').querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select'))n.disabled=locked||!catalog?.canSave;
  get<HTMLButtonElement>('gs-save').disabled=locked||!catalog?.canSave||!dirty;
  for(const id of ['gs-add','gs-remove'])get<HTMLButtonElement>(id).disabled=locked||!catalog?.canSave;
  get<HTMLButtonElement>('gs-open').disabled=locked||!value('gs-version');get<HTMLButtonElement>('gs-current').disabled=locked;
  get<HTMLButtonElement>('gs-validate').disabled=locked||!draft||dirty||!(catalog?.canSave||catalog?.canPublish);
  get<HTMLButtonElement>('gs-publish').disabled=locked||!draft||dirty||!valid||!catalog?.canPublish;
  get('gs-retry').hidden=!pending;get<HTMLButtonElement>('gs-retry').disabled=o.busy()||o.historical();
 }
 function invalidate(){dirty=true;valid=false;draft=undefined;get('gs-validation').textContent='Changes need a saved draft and validation.';get('gs-preview').replaceChildren();sync();}
 function drawFields(){
  const row=configuration[tab()][rowIndex()];get('gs-fields').replaceChildren();if(!row){sync();return;}
  for(const f of fields[tab()]){
   const label=document.createElement('label');label.textContent=f.label;let input:HTMLInputElement|HTMLSelectElement;
   if(f.master){const select=document.createElement('select');const rows:Master[]=f.master==='groups'?configuration.groups.map(r=>({code:String(r.code??''),name:String(r.code??'')})):catalog?.[f.master]??[];select.replaceChildren(new Option(f.nullable?'Unpriced point · no group':'Choose…',''),...rows.filter(r=>r.code).map(r=>new Option(`${r.name??r.symbol??r.code} · ${r.code}`,r.code)));input=select;}
   else{const text=document.createElement('input');text.type=f.kind??'text';if(f.kind==='number')text.step='1';input=text;}
   input.id=`gs-field-${f.key}`;input.value=String(row[f.key]??'');input.required=!f.nullable;
   input.oninput=()=>{row[f.key]=f.nullable&&!input.value?null:input.value;invalidate();};label.append(input);get('gs-fields').append(label);
  }
  sync();
 }
 function drawRows(selected=0){const rows=configuration[tab()];get<HTMLSelectElement>('gs-row').replaceChildren(...rows.map((r,n)=>new Option(tab()==='paths'?`${r.pipeline??'Pipeline'} / ${r.zone??'Zone'} / ${r.point??'Point'} · ${r.product??'Product'}`:String(r.code??'New record'),String(n))));get<HTMLSelectElement>('gs-row').value=rows.length?String(Math.min(selected,rows.length-1)):'';drawFields();}
 function openConfiguration(c:Configuration,b:string|null,id?:string){configuration=structuredClone(c);base=b;draft=id;dirty=false;valid=false;get('gs-validation').textContent='';get('gs-preview').replaceChildren();drawRows();}
 function reset(){catalog=undefined;pending=undefined;dirty=false;valid=false;draft=undefined;epoch++;restore();get('gs-fields').replaceChildren();get('gs-preview').replaceChildren();get('gs-validation').textContent='';get('gs-status').textContent=pending?'A previous change awaits confirmation. Retry its original request.':'';sync();}
 async function refresh(){const e=epoch,c=await o.api<Catalog>('gas-setup/catalog');if(e!==epoch)return;catalog=c;get('gs-active').textContent=c.activeSetupId?`Published setup: ${c.activeSetupId}`:'Current setup: imported definitions';get<HTMLSelectElement>('gs-version').replaceChildren(new Option('Choose a saved version…',''),...c.versions.map(v=>new Option(`${v.state} · ${v.id}`,v.id)));openConfiguration(c.configuration,c.activeSetupId);}
 async function load(){reset();if(o.historical())throw Error('Return to working data to configure gas delivery setup.');await refresh();}
 async function send(){
  if(!pending)return;const command=pending,actor=o.actor(),e=epoch,uncertain=command.uncertain;command.uncertain=true;retain();
  try{
   const result=await o.api<{setupId:string;state:string}>(command.path,command.body);const expected=command.path==='gas-setup/save'?command.body.operationId:command.body.setupId;
   if(result.setupId!==expected||result.state!==(command.path==='gas-setup/save'?'draft':'published'))throw Error('The original change has an unconfirmed response. Retry it.');
   if(actor!==o.actor()||e!==epoch)return;pending=undefined;retain();dirty=false;await refresh();
   if(command.path==='gas-setup/save'){const saved=catalog?.versions.find(v=>v.id===result.setupId);if(saved){get<HTMLSelectElement>('gs-version').value=saved.id;openConfiguration(saved.configuration,saved.base_setup_id,saved.id);}get('gs-status').textContent='Draft saved. Validate its contents and delivery-period coverage before publication.';}
   else get('gs-status').textContent='Version published. New gas tickets use this setup; saved trades keep their pinned version.';
  }catch(error){if(actor===o.actor()&&e===epoch){const code=(error as {code?:string}).code;if(!uncertain&&code&&(code==='access_denied'||code.startsWith('gas_setup_')||code==='historical_read_only')){pending=undefined;retain();}}throw error;}finally{sync();}
 }
 async function mutate(path:string,body:Record<string,unknown>){if(pending)throw Error('Retry the original change before starting another.');pending={path,body:{...body,operationId:crypto.randomUUID()},uncertain:false};retain();await send();}
 get('gs-version').onchange=()=>sync();get('gs-tab').onchange=()=>drawRows();get('gs-row').onchange=drawFields;
 get('gs-add').onclick=()=>{configuration[tab()].push(Object.fromEntries(fields[tab()].map(f=>[f.key,f.nullable?null:''])));invalidate();drawRows(configuration[tab()].length-1);};
 get('gs-remove').onclick=()=>{configuration[tab()].splice(rowIndex(),1);invalidate();drawRows();};
 get('gs-open').onclick=()=>void leave().then(go=>{if(!go)return;const v=catalog?.versions.find(v=>v.id===value('gs-version'));if(v)openConfiguration(v.configuration,v.base_setup_id,v.id);});
 get('gs-current').onclick=()=>void leave().then(go=>{if(go&&catalog)openConfiguration(catalog.configuration,catalog.activeSetupId);});
 get('gs-save').onclick=()=>void o.act(()=>mutate('gas-setup/save',{baseSetupId:base,configuration}));get('gs-retry').onclick=()=>void o.act(send);
 get('gs-validate').onclick=()=>void o.act(async()=>{
  const e=epoch,id=draft;if(!id||dirty)throw Error('Save this draft first.');valid=false;
  const result=await o.api<{valid:boolean;message?:string;points?:{pipeline:string;zone:string;point:string;product:string;group:string|null;curve:string|null;index:string|null;adjustment:string|null;groupAdjustment:string|null;pointAdjustment:string|null;quote:string|null;missingMeterDays:number}[]}>('gas-setup/validate',{setupId:id,from:value('gs-from'),to:value('gs-to')});
  if(e!==epoch||draft!==id)return;valid=result.valid;get('gs-validation').textContent=result.valid?'Validation passed. Review the resolved pricing and meter coverage.':result.message??'The setup needs correction.';get('gs-preview').replaceChildren();
  if(result.points){const table=document.createElement('table'),header=table.createTHead().insertRow();for(const name of ['Pipeline','Zone','Point','Product','Group','Curve','Index','Quote','Group adjustment','Point adjustment','Combined adjustment','Unmetered days']){const th=document.createElement('th');th.textContent=name;header.append(th);}const body=table.createTBody();for(const p of result.points){const row=body.insertRow();for(const v of [p.pipeline,p.zone,p.point,p.product,p.group??'Unpriced',p.curve??'',p.index??'',p.quote??'',p.groupAdjustment??'',p.pointAdjustment??'',p.adjustment??'',String(p.missingMeterDays)])row.insertCell().textContent=v;}get('gs-preview').append(table);}sync();
 });
 for(const id of ['gs-from','gs-to'])get(id).oninput=()=>{valid=false;get('gs-preview').replaceChildren();get('gs-validation').textContent='Validate the selected delivery period.';sync();};
 get('gs-publish').onclick=()=>void o.act(async()=>{if(!draft||!valid||dirty)throw Error('Validate this exact saved version first.');await mutate('gas-setup/publish',{setupId:draft});});
 const prompt=document.createElement('dialog');prompt.id='gs-leave';prompt.innerHTML='<p>Discard unsaved gas setup changes?</p><div class="schedule-actions"><button type="button" id="gs-keep">Keep editing</button><button type="button" id="gs-discard">Discard changes</button></div>';root.append(prompt);
 let leaving:((go:boolean)=>void)|undefined;
 function finish(go:boolean){if(go)dirty=false;prompt.close();const done=leaving;leaving=undefined;done?.(go);}
 get('gs-keep').onclick=()=>finish(false);get('gs-discard').onclick=()=>finish(true);prompt.oncancel=e=>{e.preventDefault();finish(false);};
 function leave():Promise<boolean>{if(!dirty)return Promise.resolve(true);if(leaving)return Promise.resolve(false);prompt.showModal();return new Promise(resolve=>{leaving=resolve;});}
 return{load,reset,sync,leave,unsaved:()=>dirty||!!pending};
}
