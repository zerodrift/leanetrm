import {isAddress,keyId,keyLabel,type MarkEdit,type PasteRequest,type Validation} from './curve-mark-model.js';

type Target={
 matrix:boolean;
 start:PasteRequest['start'];
 label:string;
 current:()=>boolean;
 validate:(paste:PasteRequest)=>Promise<Validation>;
 apply:(value:Validation)=>void;
};

export function checkPasteDraft(value:Validation,edits:MarkEdit[]){
 const merged=new Map(edits.map(edit=>[keyId(edit.key),edit.value]));
 const addressed=new Map(value.addressed.map(change=>[keyId(change.key),change.value]));
 for(const [key,amount] of addressed)merged.set(key,amount);
 for(const cell of value.draft.cells){
  const key=keyId(cell.state.key);
  if(cell.enteredValue!==(merged.get(key)??null)||addressed.has(key)&&(cell.error||cell.proposedValue!==addressed.get(key)))
   throw Error('The addressed paste did not match the validated draft.');
 }
 return value;
}

export function setupMarkPaste(host:HTMLElement,table:(headers:string[],rows:string[][],label:string)=>HTMLElement){
 const dialog=document.createElement('dialog');
 dialog.className='mark-dialog mark-review-dialog';dialog.dataset.mark='paste-dialog';
 dialog.setAttribute('aria-labelledby','mark-paste-title');
 dialog.innerHTML=`
 <div class="mark-dialog-head"><h3 id="mark-paste-title">Paste marks</h3><form method="dialog"><button class="secondary">Close</button></form></div>
 <form class="mark-paste-form">
  <label>Paste area<select name="markPasteMode"><option value="rectangle">Rectangle from selected cell</option><option value="full_matrix">Full correlation matrix</option></select></label>
  <label>Tab-separated values<textarea name="markPasteText" rows="4" spellcheck="false"></textarea></label>
  <p class="mark-muted" data-mark="paste-origin"></p>
  <button type="submit" class="secondary">Preview changes</button>
 </form>
 <p data-mark="paste-status" role="status"></p><div data-mark="paste-details"></div>
 <button type="button" data-mark="paste-apply" disabled>Apply to draft</button>`;
 host.append(dialog);
 const mode=dialog.querySelector('select')!,text=dialog.querySelector('textarea')!;
 const status=dialog.querySelector<HTMLElement>('[data-mark=paste-status]')!;
 const origin=dialog.querySelector<HTMLElement>('[data-mark=paste-origin]')!;
 const details=dialog.querySelector<HTMLElement>('[data-mark=paste-details]')!;
 const apply=dialog.querySelector<HTMLButtonElement>('[data-mark=paste-apply]')!;
 let target:Target|undefined,candidate:Validation|undefined,sequence=0;
 function clear(){
  sequence++;candidate=undefined;apply.disabled=true;details.replaceChildren();status.textContent='';
 }
 function indicate(){
  origin.textContent=mode.value==='rectangle'?`Starting at ${target?.label}. Blank cells keep their existing values.`:
   'Supply the complete square matrix. Matching mirrors share one mark.';
 }
 async function preview(raw:string){
  clear();
  const to=target,serial=sequence;
  if(!to||!to.current())return;
  status.textContent='Checking paste…';
  const current=()=>dialog.open&&to.current()&&serial===sequence;
  try{
   const value=await to.validate(mode.value==='rectangle'?{mode:'rectangle',start:to.start,text:raw}:{mode:'full_matrix',start:null,text:raw});
   if(!current())return;
   const rows=value.addressed.map(({address,key,value})=>[String(address.row+1),String(address.column+1),keyLabel(key),key.month,value]);
   status.textContent=rows.length?`${rows.length} addressed ${rows.length===1?'cell':'cells'} ready to apply.`:'No addressed changes.';
   if(!rows.length)return;
   details.append(table(['Row','Column','Curve / pair','Month','Value'],rows,'Addressed paste changes'));
   candidate=value;apply.disabled=false;
  }catch(error){
   if(!current())return;
   const detail=error instanceof Error&&'details' in error?error.details:undefined;
   const address=detail&&typeof detail==='object'&&'address' in detail?detail.address:null;
   const where=isAddress(address)?`Row ${address.row+1}, column ${address.column+1}. `:'';
   status.textContent=where+(error instanceof Error?error.message:'Paste could not be validated.');
  }
 }
 dialog.querySelector('.mark-paste-form')!.addEventListener('submit',event=>{event.preventDefault();void preview(text.value);});
 text.oninput=clear;
 mode.onchange=()=>{clear();indicate();};
 apply.onclick=()=>{
  if(target?.current()&&candidate){target.apply(candidate);dialog.close();}
 };
 return {
  open(to:Target,raw?:string){
   clear();target=to;
   mode.options[0].disabled=!to.start;mode.options[1].disabled=!to.matrix;mode.value=to.start?'rectangle':'full_matrix';
   text.value=raw??'';indicate();dialog.showModal();text.focus();
   if(raw!==undefined)void preview(raw);
  },
  close(){clear();target=undefined;if(dialog.open)dialog.close();},
 };
}
