import {PendingCommand,type RequestCommand} from './pending-command.js';
import {node,pair} from './reference-detail.js';
import {askInPanel} from './side-panel.js';
type Row=Record<string,unknown>;
type Revision={catalogId:string;revisionId:string};
type Field={key:string;label:string;kind:'text'|'decimal';nullable:boolean;maxLength:number};
type Version={version:number;table:string;code:string;recordedAt:string;author:string;changes:Record<string,string|null>};
type Catalog={table:string;sourceRevision:Revision;baseRevisionId:string;displayVersion:number;fields:Field[];canEdit:boolean;versions:Version[];historyTruncated:boolean;reason:string;item:Row|null};
type Outcome={revisionId?:string;displayRevisionId?:string;version?:number};
type Options={host:HTMLElement;api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;identity:()=>string;instant:(at:string)=>string;onCommitted:()=>Promise<void>};
// An input Edit put in place of a shown value: the item and field it edits, what it replaced and the value it started from.
type Slot={table:string;code:string;field:Field;element:HTMLElement;replaced:Node[];input:HTMLInputElement;from:string|null};
type Change={table:string;code:string;values:Record<string,string|null>;slots:Slot[]};

export function knownReferenceRejection(code:string|undefined):boolean{
  return !!code&&['reference_edit','reference_conflict','reference_economic_change','reference_limit','historical_read_only','access_denied','request_format','request_too_large','request_token'].includes(code);
}
// A retained commit from browser storage, restored exactly and as uncertain; anything else, the retired save and apply requests
// among them, is discarded. Browser storage never grants authority.
export function restoreReferenceRequest(value:unknown):RequestCommand|undefined{
  if(!value||typeof value!=='object'||Array.isArray(value))return;
  const saved=value as Record<string,unknown>;
  if(saved.path!=='reference/commit'||typeof saved.operationId!=='string'||!saved.operationId||!saved.body||typeof saved.body!=='object'||Array.isArray(saved.body))return;
  return {operationId:saved.operationId,path:saved.path,body:structuredClone(saved.body as Record<string,unknown>),uncertain:true};
}
// What a commit's response confirms: its own new display version, or, when its values were already applied, the
// unchanged version it was based on (D238). Anything else is unconfirmed.
export function commitOutcome(outcome:Outcome|null|undefined,command:RequestCommand):{base:string;version:number}|undefined{
  const {baseRevisionId,expectedVersion}=command.body as {baseRevisionId:string;expectedVersion:number};
  if(!outcome||outcome.revisionId!==outcome.displayRevisionId)return;
  if(outcome.revisionId===command.operationId&&outcome.version===expectedVersion+1)return {base:command.operationId,version:outcome.version};
  if(outcome.revisionId===baseRevisionId&&outcome.version===expectedVersion)return {base:baseRevisionId,version:expectedVersion};
}
const inconsistent=()=>Error('The reference details changed. Refresh before editing.');
const text=(value:unknown)=>value===null||value===undefined?'':String(value);

// Presentation only: Edit turns the shown item's display fields, which the backend's descriptor names, into inputs in place on its page;
// Save lists the changed fields and, once confirmed, commits each changed item in turn (the backend edits one item per operation),
// stopping at the first refusal with the later edits kept unsent in the form. A request whose outcome is unknown is retained, exactly,
// for Retry, and blocks a new edit. Inputs remain strings, including exact optional coordinates.
export function setupReferenceEditor(options:Options){
  const button=(label:string,id:string,className='secondary')=>{const b=node('button',label,className);b.type='button';b.id=id;return b;};
  const pending=new PendingCommand(knownReferenceRejection);
  let generation=0,shown:{table:string;code:string;catalogs:Map<string,Catalog>}|undefined,slots:Slot[]=[],permanent:HTMLElement|undefined;
  // The display version the next commit builds on, which each confirmed commit advances; whether one was made since Edit.
  let chain={base:'',version:0},committed=false,reviewing=false,question:AbortController|undefined;
  const edit=button('Edit','reference-edit',''),history=button('History','reference-history-open'),save=button('Save','reference-edit-save',''),cancel=button('Cancel','reference-edit-cancel');
  const actions=node('div','','item-actions'),reason=node('p','','fine-print'),container=node('div','','reference-editor');
  actions.id='reference-edit-actions';reason.id='reference-edit-reason';actions.append(edit,history,save,cancel);
  const pendingPanel=node('div','','reference-edit-pending'),pendingMessage=node('p'),retry=button('Retry retained request','reference-edit-retry');
  pendingPanel.id='reference-edit-pending';pendingPanel.setAttribute('role','status');pendingPanel.append(pendingMessage,retry);
  const review=node('section','','reference-edit-review'),reviewList=node('ul'),confirm=button('Save and apply','reference-edit-confirm',''),back=button('Back to editing','reference-edit-back');
  review.id='reference-edit-review';review.append(node('h3','Confirm these display changes'),reviewList,node('p','They apply to current curves at once. Codes and financial terms keep their original versions.','fine-print'),confirm,back);
  const historyPanel=node('section','','reference-history'),historyList=node('ol'),earlier=node('p','Earlier versions are not listed.','fine-print');
  historyPanel.id='reference-history';historyPanel.append(node('h3','History'),historyList,earlier);
  // The question asked before a move gives up unsaved edits (askInPanel), above the editor's controls.
  const prompt=node('div','','side-panel-prompt reference-edit-prompt');prompt.id=options.host.id+'-prompt';prompt.setAttribute('role','alertdialog');prompt.setAttribute('aria-label','Unsaved changes');prompt.hidden=true;
  container.append(prompt,actions,reason,pendingPanel,review,historyPanel);
  const storageKey=()=>`openetrm.reference.request.${options.identity()}`;
  function retain(){try{if(pending.command)sessionStorage.setItem(storageKey(),JSON.stringify(pending.command));else sessionStorage.removeItem(storageKey());}catch{/* The exact request remains in memory. */}sync();}
  function restore(){
    pending.clear();
    try{const raw=sessionStorage.getItem(storageKey());if(raw){pending.command=restoreReferenceRequest(JSON.parse(raw));if(!pending.command)sessionStorage.removeItem(storageKey());}}catch{/* Browser storage never grants authority. */}
  }
  const main=()=>shown?.catalogs.get(shown.table);
  const editable=()=>[...shown?.catalogs.values()??[]].some(c=>c.canEdit&&c.fields.length>0);
  const value=(slot:Slot)=>slot.field.nullable&&slot.input.value===''?null:slot.input.value;
  const fieldLabel=(table:string,code:string,key:string)=>{const label=shown?.catalogs.get(table)?.fields.find(f=>f.key===key)?.label??key;return table===shown?.table&&code===shown.code?label:`${label} (${code})`;};
  const edited=()=>slots.some(slot=>value(slot)!==slot.from);
  // Asks whether a move may give up the unsaved edits: false means the person kept them. A retained request is not among them, for it
  // survives a move. A question already open takes focus again rather than asking twice. The page takes focus only while it asks, and
  // focus then returns to the control that asked.
  async function leave(){
    if(!edited())return true;
    if(question){prompt.querySelector<HTMLElement>('button.secondary')?.focus();return false;}
    const asking=question=new AbortController(),trigger=document.activeElement as HTMLElement|null;
    options.host.tabIndex=-1;
    try{return await askInPanel(options.host,{text:'These reference details have unsaved edits. Leaving discards them.',keep:'Keep editing',go:'Discard edits'})(asking.signal);}
    finally{
      if(question===asking)question=undefined;
      options.host.removeAttribute('tabindex');
      if(trigger&&trigger!==document.body&&trigger.isConnected)trigger.focus({preventScroll:true});
    }
  }
  // The changed items in the page's order, each with only its changed fields.
  function changes(){
    const found=new Map<string,Change>();
    for(const slot of slots){
      if(value(slot)===slot.from)continue;
      const key=JSON.stringify([slot.table,slot.code]),change=found.get(key)??{table:slot.table,code:slot.code,values:{},slots:[]};
      change.values[slot.field.key]=value(slot);change.slots.push(slot);found.set(key,change);
    }
    return [...found.values()];
  }
  function startEdit(){
    const owner=shown;if(!owner||!editable()||pending.command)throw Error('These reference details cannot be edited here.');
    for(const element of options.host.querySelectorAll<HTMLElement>('[data-edit-field]')){
      const table=element.dataset.editTable??owner.table,code=element.dataset.editCode??owner.code,catalog=owner.catalogs.get(table),field=catalog?.fields.find(f=>f.key===element.dataset.editField);
      if(!catalog?.canEdit||!field)continue;
      const own=table===owner.table&&code===owner.code,shownValue=own?catalog.item?.[field.key]:element.dataset.editValue;
      if(shownValue!==null&&typeof shownValue!=='string')throw inconsistent();
      const from:string|null=shownValue;
      const input=node('input'),limit=node('small',`Maximum ${field.maxLength} characters.`);
      input.type='text';input.name=field.key;input.id=own?'reference-edit-'+field.key:`reference-edit-${table}-${code}-${field.key}`;input.value=text(from);input.required=!field.nullable;
      input.setAttribute('aria-label',fieldLabel(table,code,field.key));if(field.kind==='decimal')input.inputMode='decimal';
      // Native maxlength counts UTF16 units. The backend validates Unicode scalars.
      limit.id=input.id+'-limit';input.setAttribute('aria-describedby',limit.id);
      slots.push({table,code,field,element,replaced:[...element.childNodes],input,from:field.nullable&&from===''?null:from});element.replaceChildren(input,limit);
    }
    if(main()!.fields.some(f=>!slots.some(s=>s.table===owner.table&&s.code===owner.code&&s.field.key===f.key))){endEdit();throw inconsistent();}
    const code=[...options.host.querySelectorAll('.item-facts div')].find(d=>d.querySelector('dt')?.textContent==='Code')?.querySelector('dd');
    if(code){permanent=node('span','Permanent','permanent');code.append(' ',permanent);}
    chain={base:main()!.baseRevisionId,version:main()!.displayVersion};committed=false;historyPanel.hidden=true;slots[0]?.input.focus();sync();
  }
  // Puts the shown values back in place of the inputs.
  function endEdit(){
    for(const slot of slots)slot.element.replaceChildren(...slot.replaced);
    slots=[];permanent?.previousSibling?.remove();permanent?.remove();permanent=undefined;reviewing=false;reviewList.replaceChildren();sync();
  }
  function showReview(){
    const list=changes();if(!list.length)throw Error('Change a label before saving.');
    reviewList.replaceChildren(...list.flatMap(c=>c.slots.map(s=>node('li',`${fieldLabel(c.table,c.code,s.field.key)}: ${s.from??'Not supplied'} → ${value(s)??'Not supplied'}`))));
    reviewing=true;sync();confirm.focus();
  }
  // Sends the retained request; a confirmed commit advances the chain and becomes the starting value of the inputs it changed.
  async function send(){
    const command=pending.command,identity=options.identity();if(!command)return;
    const wasUncertain=pending.sending();retain();
    let confirmed:{base:string;version:number}|undefined;
    try{
      const outcome=await options.api<Outcome>(command.path,{...command.body,operationId:command.operationId});
      if(identity!==options.identity()||pending.command!==command)return;
      confirmed=commitOutcome(outcome,command);
      if(!confirmed)throw Object.assign(Error('The reference change has an unconfirmed response. Retry its retained request.'),{code:'reference_outcome_unknown'});
    }catch(error){
      if(identity===options.identity()&&pending.command===command){pending.failed((error as {code?:string}).code,wasUncertain);retain();}
      throw error;
    }
    pending.clear();retain();committed=true;chain=confirmed;
    const values=command.body.values as Record<string,string|null>;
    for(const slot of slots)if(slot.table===command.body.table&&slot.code===command.body.code&&slot.field.key in values)slot.from=values[slot.field.key];
  }
  // Once nothing is left to send, the page is read again and shows what was committed.
  async function settle(){if(slots.length&&changes().length){sync();return;}endEdit();await options.onCommitted();}
  async function commitAll(){
    const owner=generation;reviewing=false;
    for(const change of changes()){
      const catalog=shown!.catalogs.get(change.table)!;
      pending.begin('reference/commit',{table:change.table,code:change.code,sourceRevisionId:catalog.sourceRevision.revisionId,baseRevisionId:chain.base,expectedVersion:chain.version,values:change.values});
      await send();if(owner!==generation)return;
    }
    await settle();
  }
  function drawHistory(){
    earlier.hidden=!main()?.historyTruncated;
    historyList.replaceChildren(...(main()?.versions??[]).map(v=>{
      const item=node('li'),when=node('time',options.instant(v.recordedAt)),changed=node('dl');when.dateTime=v.recordedAt;
      for(const [key,next] of Object.entries(v.changes))changed.append(pair(fieldLabel(v.table,v.code,key),next??'Not supplied'));
      item.append(when,` · ${v.author} · display version ${v.version}`,changed);return item;
    }));
  }
  edit.onclick=()=>void options.act(async()=>startEdit());
  cancel.onclick=()=>void options.act(async()=>{endEdit();if(committed)await options.onCommitted();});
  save.onclick=()=>void options.act(async()=>showReview());
  back.onclick=()=>{reviewing=false;sync();save.focus();};
  confirm.onclick=()=>void options.act(commitAll);
  retry.onclick=()=>void options.act(async()=>{await send();if(!pending.command)await settle();});
  history.onclick=()=>{historyPanel.hidden=!historyPanel.hidden;if(!historyPanel.hidden)drawHistory();sync();};
  function sync(){
    const busy=options.busy(),editing=slots.length>0;
    edit.hidden=!editable()||editing;edit.disabled=busy||!!pending.command;
    history.hidden=editing||!main()?.versions.length;history.setAttribute('aria-expanded',String(!historyPanel.hidden));
    save.hidden=cancel.hidden=!editing;save.disabled=cancel.disabled=busy||!!pending.command||reviewing;
    for(const slot of slots)slot.input.disabled=busy||!!pending.command||reviewing;
    review.hidden=!reviewing;confirm.disabled=back.disabled=busy||!!pending.command;
    reason.textContent=shown&&!editable()?main()!.reason:'';reason.hidden=!reason.textContent;
    pendingPanel.hidden=!pending.command;retry.disabled=busy||!pending.command;
    pendingMessage.textContent=pending.command?`A change to ${text(pending.command.body.table)} ${text(pending.command.body.code)} has an unconfirmed outcome. Retry the same retained request to reconcile it; a new change is blocked.`:'';
  }
  function reset(){generation++;question?.abort();endEdit();shown=undefined;historyPanel.hidden=true;historyList.replaceChildren();container.remove();restore();sync();}
  restore();sync();
  return{
    // Reads the item's editor details (with its contracts' for a futures product) and places the controls and display version on its page.
    // `displayVersion` is the one the page's own read showed; editor details of another version mean the page changed in between.
    async show(table:string,code:string,context:unknown,displayVersion:number){
      reset();const request=generation,identity=options.identity();
      const read=(t:string,c?:string)=>options.api<Catalog>('reference/editor?'+new URLSearchParams({table:t,...c===undefined?{}:{code:c}}));
      const [item,series]=await Promise.all([read(table,code),table==='futures_products'?read('futures_series'):undefined]);
      if(request!==generation||identity!==options.identity())return;
      const catalogs=new Map<string,Catalog>([[table,item],...series?[['futures_series',series] as const]:[]]);
      for(const [t,c] of catalogs)
        if(c.table!==t||!Array.isArray(c.fields)||!Array.isArray(c.versions)||typeof c.historyTruncated!=='boolean'||c.displayVersion!==displayVersion||c.fields.some(f=>!Number.isSafeInteger(f.maxLength)||f.maxLength<1)
          ||c.sourceRevision.revisionId!==item.sourceRevision.revisionId||c.baseRevisionId!==item.baseRevisionId||c.displayVersion!==item.displayVersion||context!==null&&c.canEdit)throw inconsistent();
      if(item.fields.length&&text(item.item?.code)!==code)throw inconsistent();
      const facts=options.host.querySelector('.item-facts');if(!facts)throw inconsistent();
      shown={table,code,catalogs};
      facts.append(pair('Display version',item.displayVersion?String(item.displayVersion):'Original'));facts.after(container);sync();
    },
    reset,sync,leave,
    // Display edits not yet saved, or a change whose outcome is unconfirmed.
    unsaved:()=>!!pending.command||edited(),
    // Display edits not yet saved, which a move gives up.
    editing:edited
  };
}
