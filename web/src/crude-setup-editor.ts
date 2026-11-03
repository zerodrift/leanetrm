import {crudeChoices,type CrudeReferenceData} from './crude-selection.js';
type Configuration={code:string;deliveryReferenceCatalogId:string;deliveryReferenceRevisionId:string;commodity:string;grade:string;facility:string;custodyPoint:string;clock:string;definitionRevisionId:string;curve:string;validFrom:string;validTo:string;quantityPolicy:string};
type Version={id:string;state:'draft'|'published';base_setup_id:string|null;configuration:Configuration};
type Catalog={deliveryReferences:CrudeReferenceData;sources:{definitionRevisionId:string;curve:string;name:string}[];clocks:{code:string}[];quantityPolicies:{code:string;label:string}[];versions:Version[];canSave:boolean;canPublish:boolean};
type Pending={path:string;body:Record<string,unknown>;uncertain:boolean};

export function setupCrudeConfiguration(o:{api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;actor:()=>string;historical:()=>boolean;source:()=>{snapshotId:string};marketLabel:()=>string}){
 const root=document.getElementById('crude-setup-page')!;
 root.innerHTML=`<section class="card"><h2>Crude delivery setup</h2><p>Associate an exact grade, facility and custody point with an existing monthly assessment curve. Publication retains the source curve and price inputs.</p>
 <div class="gas-setup-toolbar"><label>Saved version<select id="cs-version"></select></label><button id="cs-open" type="button" class="secondary">Open version</button><button id="cs-new" type="button" class="secondary">New binding</button><button id="cs-revise" type="button" class="secondary">Revise selected publication</button></div>
 <form id="cs-fields" class="schedule-fields"><label>Binding code<input id="cs-code" required pattern="[a-z][a-z0-9_.-]{0,63}"></label><label>Commodity<select id="cs-commodity" required></select></label><label>Grade<select id="cs-grade" required></select></label><label>Facility<select id="cs-facility" required></select></label><label>Custody point<select id="cs-custodyPoint" required></select></label><label>Pricing source<select id="cs-source" required></select></label><label>Delivery clock<select id="cs-clock" required></select></label><label>Quantity relationship<select id="cs-policy" required></select></label><label>Valid from<input id="cs-from" type="date" required></label><label>Valid to · exclusive<input id="cs-to" type="date" required></label></form>
 <p id="cs-coverage" class="field-help"></p><p id="cs-version-state" role="status"></p><div class="schedule-actions"><button id="cs-save" type="button">Save draft</button><button id="cs-validate" type="button">Validate saved version</button><button id="cs-publish" type="button">Publish validated version</button><button id="cs-retry" type="button" hidden>Retry original change</button></div><p id="cs-status" role="status"></p></section>
 <section class="card"><h3>Preview selected delivery mark</h3><p>Inspect the undiscounted physical delivery value using this published binding. Cash consideration and fees are separate obligations. Published bindings are also available on new crude tickets. Booking retains the selected version.</p><p id="cs-market"></p><div class="schedule-fields"><label>Delivery from · UTC<input id="cs-preview-from" type="datetime-local" step="1"></label><label>Delivery to · exclusive UTC<input id="cs-preview-to" type="datetime-local" step="1"></label><label>Total standard volume<input id="cs-quantity" inputmode="decimal"></label></div><button id="cs-preview" type="button">Preview delivery value</button><p id="cs-value" role="status"></p></section>`;
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const value=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id).value;
 const sourceKey=(p:{definitionRevisionId:string;curve:string})=>JSON.stringify([p.definitionRevisionId,p.curve]);
 let catalog:Catalog|undefined,selected:Version|undefined,base:string|null=null,dirty=false,valid=false,epoch=0,pending:Pending|undefined;
 const storageKey=()=>`crude-setup-command:${o.actor()}`;
 function retain(){try{if(pending)sessionStorage.setItem(storageKey(),JSON.stringify(pending));else sessionStorage.removeItem(storageKey());}catch{/* The exact command remains in memory. */}}
 const definiteRefusals=new Set(['access_denied','historical_read_only','crude_setup_conflict','crude_setup_input','crude_setup_compatibility','crude_setup_coverage','crude_setup_market_scope','request_invalid','request_too_large']);
 function restore(){try{const p=JSON.parse(sessionStorage.getItem(storageKey())??'null');if(['crude-setup/save','crude-setup/publish'].includes(p?.path)&&typeof p?.body?.operationId==='string')pending={path:p.path,body:p.body,uncertain:true};}catch{/* Stored transport state grants no authority. */}}
 function sync(){
  const locked=o.busy()||o.historical()||!!pending;
  for(const field of root.querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select'))field.disabled=locked;
  for(const field of get('cs-fields').querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select'))field.disabled=locked||!catalog?.canSave||selected?.state==='published';
  get<HTMLInputElement>('cs-code').disabled=locked||!catalog?.canSave||!!base||selected?.state==='published';
  get<HTMLButtonElement>('cs-save').disabled=locked||!catalog?.canSave||!dirty;
  get<HTMLButtonElement>('cs-validate').disabled=locked||!selected||dirty;
  get<HTMLButtonElement>('cs-publish').disabled=locked||!catalog?.canPublish||selected?.state!=='draft'||dirty||!valid;
  get<HTMLButtonElement>('cs-preview').disabled=locked||selected?.state!=='published'||dirty;
  get<HTMLButtonElement>('cs-open').disabled=locked||!value('cs-version');
  get<HTMLButtonElement>('cs-new').disabled=locked||!catalog?.canSave;
  get<HTMLButtonElement>('cs-revise').disabled=locked||!catalog?.canSave||selected?.state!=='published';
  get('cs-retry').hidden=!pending;get<HTMLButtonElement>('cs-retry').disabled=o.busy()||o.historical();
 }
 function edited(){dirty=true;valid=false;get('cs-value').textContent='';get('cs-status').textContent='Save and validate these changes.';sync();}
 function open(version?:Version,revise=false){
  selected=revise?undefined:version;base=revise?version!.id:version?.base_setup_id??null;dirty=revise;valid=false;
  const c=version?.configuration;
  const sameReferences=!c||c.deliveryReferenceCatalogId===catalog?.deliveryReferences.catalogId&&c.deliveryReferenceRevisionId===catalog?.deliveryReferences.revisionId;
  get<HTMLInputElement>('cs-code').value=c?.code??'';
  for(const key of ['commodity','grade','facility','custodyPoint'] as const)get<HTMLSelectElement>('cs-'+key).value=sameReferences?c?.[key]??'':'';
  get<HTMLSelectElement>('cs-source').value=c?sourceKey(c):'';
  for(const [id,key] of [['cs-clock','clock'],['cs-policy','quantityPolicy'],['cs-from','validFrom'],['cs-to','validTo']] as const)get<HTMLInputElement|HTMLSelectElement>(id).value=c?.[key]??'';
  if(!sameReferences)get<HTMLSelectElement>('cs-clock').value='';
  get('cs-version-state').textContent=version?`${revise?'Revision of':version.state} · ${version.id}`:'New binding · choose every term explicitly.';
  get('cs-status').textContent=sameReferences?'':`Saved delivery references: ${c!.deliveryReferenceCatalogId} / ${c!.deliveryReferenceRevisionId}; ${c!.commodity}, ${c!.grade}, ${c!.facility}, ${c!.custodyPoint}, ${c!.clock}. Revising into the current masters requires choosing each physical field again.`;get('cs-value').textContent='';sync();
 }
 async function refresh(id?:string){
  const e=epoch,actor=o.actor(),c=await o.api<Catalog>('crude-setup/catalog');if(e!==epoch||actor!==o.actor())return;
  catalog=c;
  get<HTMLSelectElement>('cs-version').replaceChildren(new Option('Choose a saved version…',''),...c.versions.map(v=>new Option(`${v.configuration.code} · ${v.state} · ${v.id}`,v.id)));
  const physicalChoices=crudeChoices(c.deliveryReferences);
  for(const key of ['commodity','grade','facility','custodyPoint'] as const)
   get<HTMLSelectElement>('cs-'+key).replaceChildren(new Option('Choose…',''),...physicalChoices[key].map(r=>new Option(r.label,r.value)));
  get<HTMLSelectElement>('cs-source').replaceChildren(new Option('Choose existing pricing source…',''),...c.sources.map(s=>new Option(`${s.name} · ${s.curve} · ${s.definitionRevisionId}`,sourceKey(s))));
  get<HTMLSelectElement>('cs-clock').replaceChildren(new Option('Choose delivery clock…',''),...c.clocks.map(k=>new Option(k.code,k.code)));
  get<HTMLSelectElement>('cs-policy').replaceChildren(new Option('Choose quantity relationship…',''),...c.quantityPolicies.map(p=>new Option(p.label,p.code)));
  get('cs-coverage').textContent='Choose each physical reference separately. Saving validates the maintained delivery relationship; publication configures the pricing source for new trades.';
  get('cs-market').textContent=o.marketLabel();
  const v=c.versions.find(v=>v.id===id);get<HTMLSelectElement>('cs-version').value=v?.id??'';open(v);
 }
 function configuration():Configuration{
  if(!get<HTMLFormElement>('cs-fields').reportValidity())throw Error('Complete every delivery and pricing term.');
  const refs=catalog?.deliveryReferences,s=catalog?.sources.find(s=>sourceKey(s)===value('cs-source'));
  if(!refs||!s)throw Error('Choose visible physical references and the configuration pricing source.');
  return{code:value('cs-code'),deliveryReferenceCatalogId:refs.catalogId,deliveryReferenceRevisionId:refs.revisionId,commodity:value('cs-commodity'),grade:value('cs-grade'),facility:value('cs-facility'),custodyPoint:value('cs-custodyPoint'),clock:value('cs-clock'),definitionRevisionId:s.definitionRevisionId,curve:s.curve,validFrom:value('cs-from'),validTo:value('cs-to'),quantityPolicy:value('cs-policy')};
 }
 async function send(){
  if(!pending)return;const command=pending,e=epoch,actor=o.actor(),uncertain=command.uncertain;command.uncertain=true;retain();
  try{
   const result=await o.api<{setupId:string;state:string;operationId:string}>(command.path,command.body);
   if(result.operationId!==command.body.operationId||typeof result.setupId!=='string'||! /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(result.setupId)||!['draft','published'].includes(result.state)||(command.path==='crude-setup/publish'&&(result.setupId!==command.body.setupId||result.state!=='published')))throw Error('The original response is unconfirmed. Retry the original change.');
   if(e!==epoch||actor!==o.actor())return;pending=undefined;retain();dirty=false;await refresh(result.setupId);
   if(e===epoch&&actor===o.actor())get('cs-status').textContent=result.state==='published'?'Published version confirmed. New tickets derive pricing from the physical parameters; saved trades retain their selected version.':'Draft saved. Validate this exact version before publication.';
  }catch(error){if(e===epoch&&actor===o.actor()&&!uncertain&&definiteRefusals.has((error as {code?:string}).code??'')){pending=undefined;retain();}throw error;}finally{sync();}
 }
 async function mutate(path:string,body:Record<string,unknown>){if(pending)throw Error('Retry the original change first.');pending={path,body:structuredClone({...body,operationId:crypto.randomUUID()}),uncertain:false};retain();await send();}
 function reset(){epoch++;catalog=undefined;selected=undefined;pending=undefined;dirty=false;valid=false;base=null;restore();get('cs-status').textContent=pending?'A previous change awaits confirmation. Retry its original request.':'';sync();}
 async function load(){reset();if(o.historical())throw Error('Return to working data to configure crude delivery.');await refresh();}
 for(const input of get('cs-fields').querySelectorAll('input,select'))input.addEventListener('input',edited);
 get('cs-version').onchange=sync;
 get('cs-open').onclick=()=>void leave().then(go=>{if(go)open(catalog?.versions.find(v=>v.id===value('cs-version')));});
 get('cs-new').onclick=()=>void leave().then(go=>{if(go)open();});
 get('cs-revise').onclick=()=>void leave().then(go=>{if(go&&selected?.state==='published')open(selected,true);});
 get('cs-save').onclick=()=>void o.act(()=>mutate('crude-setup/save',{baseSetupId:base,configuration:configuration()}));
 get('cs-retry').onclick=()=>void o.act(send);
 get('cs-validate').onclick=()=>void o.act(async()=>{
  const id=selected?.id,e=epoch,actor=o.actor();if(!id||dirty)throw Error('Save this version first.');valid=false;
  const result=await o.api<{valid:boolean}>('crude-setup/validate',{setupId:id});
  if(e!==epoch||actor!==o.actor()||selected?.id!==id)return;valid=result.valid;get('cs-status').textContent=result.valid?'Delivery relationships, quantity equivalence and monthly source coverage are valid. Prices are checked at preview time.':'Correct the setup before publication.';sync();
 });
 get('cs-publish').onclick=()=>void o.act(async()=>{if(!selected||!valid||dirty)throw Error('Validate this exact saved version first.');await mutate('crude-setup/publish',{setupId:selected.id});});
 get('cs-preview').onclick=()=>void o.act(async()=>{
  const id=selected?.id,e=epoch,actor=o.actor();if(!id||dirty)throw Error('Choose a published binding.');
  const from=value('cs-preview-from'),to=value('cs-preview-to'),quantity=value('cs-quantity');if(!from||!to||!quantity)throw Error('Choose delivery instants and total standard volume.');
  const utc=(s:string)=>s+(s.length===16?':00Z':'Z');
  const result=await o.api<{presentValue:string;currency:{code:string};curve:string}>('crude-setup/preview',{...o.source(),setupId:id,knowledgeMode:'current',deliveryFrom:utc(from),deliveryTo:utc(to),quantity});
  if(e===epoch&&actor===o.actor()&&selected?.id===id)get('cs-value').textContent=`Delivery value: ${result.presentValue} ${result.currency.code} · ${result.curve}`;
 });
 for(const id of ['cs-preview-from','cs-preview-to','cs-quantity'])get(id).oninput=()=>{get('cs-value').textContent='';};
 const dialog=document.createElement('dialog');dialog.id='cs-leave';dialog.innerHTML='<p>Discard unsaved crude setup changes?</p><div class="schedule-actions"><button type="button" id="cs-keep">Keep editing</button><button type="button" id="cs-discard">Discard changes</button></div>';root.append(dialog);
 let leaving:((go:boolean)=>void)|undefined;
 function finish(go:boolean){if(go)dirty=false;dialog.close();const done=leaving;leaving=undefined;done?.(go);}
 get('cs-keep').onclick=()=>finish(false);get('cs-discard').onclick=()=>finish(true);dialog.oncancel=e=>{e.preventDefault();finish(false);};
 function leave():Promise<boolean>{if(!dirty)return Promise.resolve(true);if(leaving)return Promise.resolve(false);dialog.showModal();return new Promise(resolve=>{leaving=resolve;});}
 return{load,reset,sync,leave,unsaved:()=>dirty||!!pending};
}
