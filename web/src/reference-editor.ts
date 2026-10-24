import {trackEdits} from './edit-tracking.js';
type Row=Record<string,unknown>;
type Revision={catalogId:string;revisionId:string};
type Page={table:string;referenceRevision:Revision;rows:readonly Row[];context?:unknown};
type Field={key:string;label:string;kind:'text'|'decimal';nullable:boolean;maxLength:number};
type Version={revision_id:string;base_revision_id:string;table_name:string;item_code:string;changes:Record<string,string|null>;state:string;recorded_at:string};
type Catalog={table:string;sourceRevision:Revision;baseRevisionId:string;displayVersion:number;fields:Field[];canEdit:boolean;canApply:boolean;versions:Version[];reason:string;display:{sourceRevision:Revision;references:Record<string,Row[]>}|null;item:Row|null};
type Pending={path:'reference/save'|'reference/apply';body:Record<string,unknown>;uncertain:boolean};
type Options={host:HTMLElement;api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;identity:()=>string;instant:(at:string)=>string;onApplied:()=>Promise<void>;onDenied?:()=>void};

export function knownReferenceRejection(code:string|undefined):boolean{
  return !!code&&['reference_edit','reference_conflict','reference_economic_change','reference_limit','historical_read_only','access_denied','request_format','request_too_large','request_token'].includes(code);
}
// Presentation only: the backend supplies fields, rights, retained versions and
// activation proof. Inputs remain strings, including exact optional coordinates.
export function setupReferenceEditor(options:Options){
  const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const result=document.createElement(tag);if(text!==undefined)result.textContent=text;return result;};
  let page:Page|undefined,catalog:Catalog|undefined,scope='',generation=0,selectedVersion:Version|undefined,pending:Pending|undefined;
  const host=options.host,open=node('button','Edit display details'),content=node('div'),status=node('p'),pendingPanel=node('div');
  open.type='button';open.className='secondary';content.hidden=true;status.setAttribute('role','status');
  const rowLabel=node('label','Reference item'),rows=node('select'),form=node('form'),inputs=node('div'),save=node('button','Save new version');
  rows.id='reference-edit-row';rowLabel.htmlFor=rows.id;save.type='submit';save.id='reference-edit-save';
  const versionLabel=node('label','Saved versions'),versions=node('select'),review=node('div'),apply=node('button','Apply display changes to current curves');
  versions.id='reference-edit-version';versionLabel.htmlFor=versions.id;apply.type='button';apply.id='reference-edit-apply';
  const retry=node('button','Retry retained request'),pendingMessage=node('p');retry.type='button';retry.id='reference-edit-retry';
  const edits=trackEdits(form);
  pendingPanel.append(pendingMessage,retry);form.append(rowLabel,rows,inputs,save);content.append(status,form,versionLabel,versions,review,apply);host.replaceChildren(open,pendingPanel,content);
  const storageKey=()=>`openetrm.reference.request.${options.identity()}`;
  function retain(){try{if(pending)sessionStorage.setItem(storageKey(),JSON.stringify(pending));else sessionStorage.removeItem(storageKey());}catch{/* The exact request remains in memory. */}sync();}
  function restore(){pending=undefined;try{const raw=sessionStorage.getItem(storageKey());if(raw){const p=JSON.parse(raw);if(['reference/save','reference/apply'].includes(p.path)&&p.body&&typeof p.body.operationId==='string')pending={...p,uncertain:true};}}catch{/* Browser storage never grants authority. */}}
  function text(value:unknown){return value===null||value===undefined?'':String(value);}
  function selectedRow(){return page?.rows.find(r=>text(r.code)===rows.value);}
  function drawFields(){
    inputs.replaceChildren();edits.clear();const row=catalog?.item;if(!row||!catalog||text(row.code)!==rows.value)return;
    if(catalog.fields.some(field=>!Number.isSafeInteger(field.maxLength)||field.maxLength<1))throw Error('Reference field limits are unavailable. Reload reference details before editing.');
    for(const field of catalog.fields){const wrapper=node('div'),label=node('label',field.label),input=node('input');input.name=field.key;input.id='reference-edit-'+field.key;label.htmlFor=input.id;
      input.type='text';input.value=text(row[field.key]);input.required=!field.nullable;if(field.kind==='decimal')input.inputMode='decimal';
      // Native maxlength counts UTF16 units. The backend validates Unicode scalars.
      const limit=node('small',`Maximum ${field.maxLength} characters.`);limit.id=input.id+'-limit';input.setAttribute('aria-describedby',limit.id);wrapper.append(label,input,limit);inputs.append(wrapper);}
    sync();
  }
  function drawReview(){
    review.replaceChildren();selectedVersion=catalog?.versions.find(v=>v.revision_id===versions.value);
    if(selectedVersion&&catalog){review.append(node('p',`Saved ${options.instant(selectedVersion.recorded_at)} · ${selectedVersion.state}. Financial definitions keep their original versions.`));const list=node('dl');
      for(const [key,value]of Object.entries(selectedVersion.changes)){list.append(node('dt',catalog.fields.find(f=>f.key===key)?.label??key),node('dd',value===null?'Not supplied':value));}review.append(list);}
    sync();
  }
  function draw(){
    if(!catalog||!page)return;
    status.textContent=catalog.reason;form.hidden=!catalog.fields.length;versionLabel.hidden=versions.hidden=!catalog.versions.length;
    const oldCode=rows.value;rows.replaceChildren(new Option('Choose a reference item…',''),...page.rows.filter(r=>typeof r.code==='string').map(r=>new Option(`${text(r.name??r.symbol??r.series_label??r.description??r.code)} (${text(r.code)})`,text(r.code))));
    rows.value=[...rows.options].some(o=>o.value===oldCode)?oldCode:'';
    const previous=versions.value;versions.replaceChildren(new Option('Choose a saved version to review…',''),...catalog.versions.map(v=>new Option(`${v.item_code} · ${options.instant(v.recorded_at)} · ${v.state}`,v.revision_id)));
    versions.value=[...versions.options].some(o=>o.value===previous)?previous:'';
    drawFields();drawReview();
  }
  async function load(code=rows.value){
    const owner=page,identity=options.identity(),request=++generation;if(!owner)return;
    catalog=undefined;content.hidden=false;status.textContent='Loading display-edit permissions and versions…';form.hidden=true;versions.hidden=true;versionLabel.hidden=true;review.replaceChildren();sync();
    try{const next=await options.api<Catalog>('reference/editor?'+new URLSearchParams({table:owner.table,...code?{code}:{}}));
      if(request!==generation||identity!==options.identity())return;
      if(next.table!==owner.table||next.sourceRevision.catalogId!==owner.referenceRevision.catalogId||next.sourceRevision.revisionId!==owner.referenceRevision.revisionId||!Array.isArray(next.fields)||!Array.isArray(next.versions))throw Error('The reference version changed. Refresh before editing.');
      catalog=next;draw();
    }catch(error){if(request!==generation||identity!==options.identity())return;catalog=undefined;form.hidden=true;review.replaceChildren();versions.replaceChildren();status.textContent='Reference editing is unavailable. Refresh the page before trying again.';if((error as {code?:string}).code==='access_denied')options.onDenied?.();sync();throw error;}
  }
  async function send(){
    const command=pending,identity=options.identity();if(!command)return;
    const wasUncertain=command.uncertain;command.uncertain=true;retain();
    let outcome:{revisionId?:string;state?:string;displayRevisionId?:string;version?:number};
    try{
      outcome=await options.api<typeof outcome>(command.path,command.body);
      if(identity!==options.identity()||pending!==command)return;
      const confirmed=command.path==='reference/save'
        ?outcome.revisionId===command.body.operationId&&outcome.state==='draft'
        :outcome.displayRevisionId===command.body.revisionId&&typeof command.body.expectedVersion==='number'&&outcome.version===command.body.expectedVersion+1;
      if(!confirmed)throw Object.assign(Error('The reference change has an unconfirmed response. Retry its retained request.'),{code:'reference_outcome_unknown'});
    }catch(error){
      if(identity!==options.identity()||pending!==command)return;const code=(error as {code?:string}).code;
      if(!wasUncertain&&knownReferenceRejection(code)){pending=undefined;retain();}
      else{command.uncertain=true;retain();}
      if(code==='access_denied'){catalog=undefined;content.hidden=true;options.onDenied?.();}sync();throw error;
    }
    // The write is confirmed. A failed presentation refresh must remain visible
    // without converting that known result back into an uncertain mutation.
    pending=undefined;retain();
    if(command.path==='reference/apply')await options.onApplied();
    else{await load();if(outcome.revisionId&&catalog){versions.value=outcome.revisionId;drawReview();}}
  }
  async function mutate(path:Pending['path'],body:Record<string,unknown>){if(pending)throw Error('Retry the retained reference request before starting another change.');pending={path,body:{...body,operationId:crypto.randomUUID()},uncertain:false};retain();await send();}
  open.onclick=()=>void options.act(()=>load());rows.onchange=()=>void options.act(()=>load(rows.value));versions.onchange=drawReview;retry.onclick=()=>void options.act(send);
  form.onsubmit=event=>{event.preventDefault();void options.act(async()=>{if(!catalog?.canEdit||!page||!selectedRow()||text(catalog.item?.code)!==rows.value)throw Error('Choose an editable reference item.');const values:Record<string,string|null>={};for(const field of catalog.fields){const value=(inputs.querySelector(`[name="${field.key}"]`)as HTMLInputElement).value;values[field.key]=field.nullable&&value===''?null:value;}
    await mutate('reference/save',{table:page.table,code:rows.value,sourceRevisionId:catalog.sourceRevision.revisionId,baseRevisionId:catalog.baseRevisionId,values});});};
  apply.onclick=()=>void options.act(async()=>{if(!catalog?.canApply||!page||!selectedVersion)throw Error('Review an available saved metadata version first.');await mutate('reference/apply',{table:page.table,sourceRevisionId:catalog.sourceRevision.revisionId,revisionId:selectedVersion.revision_id,expectedVersion:catalog.displayVersion});});
  function sync(){
    open.disabled=options.busy()||!page||!!pending;pendingPanel.hidden=!pending;pendingMessage.textContent=pending?'A reference change has an unconfirmed outcome. Retry the same retained request to reconcile it; a new change is blocked.':'';retry.disabled=options.busy()||!pending;
    rows.disabled=options.busy()||!!pending||!catalog?.canEdit;save.disabled=options.busy()||!!pending||!catalog?.canEdit||!selectedRow()||!catalog?.item;
    for(const input of inputs.querySelectorAll<HTMLInputElement>('input'))input.disabled=options.busy()||!!pending||!catalog?.canEdit;
    versions.disabled=options.busy()||!!pending;apply.hidden=!selectedVersion;apply.disabled=options.busy()||!!pending||!catalog?.canApply;
  }
  restore();sync();
  return{setPage(next:Page|undefined){const nextScope=next?JSON.stringify([options.identity(),next.table,next.referenceRevision,next.context??null]):'';const same=nextScope===scope&&!!next;page=next;if(same){sync();return;}scope=nextScope;generation++;catalog=undefined;selectedVersion=undefined;content.hidden=true;inputs.replaceChildren();edits.clear();rows.replaceChildren();versions.replaceChildren();review.replaceChildren();sync();},
    reset(){generation++;scope='';page=undefined;catalog=undefined;selectedVersion=undefined;content.hidden=true;inputs.replaceChildren();edits.clear();rows.replaceChildren();versions.replaceChildren();review.replaceChildren();restore();sync();},sync,
    // Display edits not yet saved, or a change whose outcome is unconfirmed.
    unsaved:()=>!!pending||edits.edited()&&!content.hidden};
}
