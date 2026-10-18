import {amountText,prepareAmountDisplay,displayPrecision,initialDisplayPrecision,type DisplayPrecision} from './amount-display.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Selection={role:string;revision:number;format:DisplayPrecision};
export function dealFormatEditor(host:HTMLElement,o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;changed:()=>Promise<void>}){
 let generation=0,tradeId='',canSave=false,rows:Selection[]=[];
 const advanced=document.createElement('details'),summary=document.createElement('summary'),fieldset=document.createElement('fieldset'),title=document.createElement('legend'),note=document.createElement('p'),controls=document.createElement('div'),reload=document.createElement('button'),status=document.createElement('p');
 advanced.className='advanced-settings';summary.textContent='Advanced settings';title.textContent='Money display';note.className='field-help';note.textContent='Decimal places for final money amounts. Shared for this deal; exact calculations and exports are unchanged.';
 reload.type='button';reload.className='secondary';reload.textContent='Reload settings';status.setAttribute('role','status');fieldset.append(title,note,controls,reload,status);advanced.append(summary,fieldset);host.replaceChildren(advanced);host.hidden=true;
 let context:Record<string,unknown>={};let roles:string[]=[];
 const policy=(role='trade')=>rows.find(r=>r.role===role)?.format??initialDisplayPrecision();
 function render(){
  controls.replaceChildren();
  for(const row of rows.filter(r=>roles.includes(r.role))){const section=document.createElement('section'),heading=document.createElement('h4'),fields=document.createElement('div'),save=document.createElement('button');heading.textContent=row.role==='trade'?'Trade totals':row.role==='fixed'?'Fixed side':'Floating side';fields.className='schedule-fields';
   const wrap=document.createElement('div'),name=document.createElement('label'),input=document.createElement('input');input.id=host.id+'-'+row.role+'-money';input.type='number';input.min='0';input.max='18';input.required=true;input.value=String(row.format.money);name.htmlFor=input.id;name.textContent='Money decimal places';wrap.append(name,input);fields.append(wrap);
   save.type='button';save.className='secondary';save.textContent='Save '+heading.textContent.toLowerCase()+' display';save.dataset.role=row.role;save.onclick=()=>void o.act(async()=>{
    const started=generation;status.textContent='';
    if(input.value===''||!input.checkValidity())throw Error('Choose 0–18 decimal places.');
    const format=displayPrecision({...row.format,money:Number(input.value)});
    try{const result=await o.api<Selection>('trade-formats/save',{tradeId,role:row.role,expectedRevision:row.revision,format});if(started!==generation)return;row.format=result.format;row.revision=result.revision;await o.changed();if(started!==generation)return;status.textContent='Money display saved for this deal.';render();}
    catch(error){if(started===generation)status.textContent='Reload settings to check the current saved values before trying again.';throw error;}
   });section.append(heading,fields,save);controls.append(section);
  }sync();
 }
 function sync(){fieldset.disabled=o.busy()||!tradeId;controls.querySelectorAll<HTMLInputElement|HTMLButtonElement>('input,button').forEach(n=>n.disabled=o.busy()||!canSave);reload.disabled=o.busy()||!tradeId;}
 async function load(id:string,selected:Record<string,unknown>,editable:boolean,parts:string[]){const started=++generation;if(tradeId!==id)advanced.open=false;tradeId=id;context=selected;canSave=editable;roles=parts;rows=[];host.hidden=true;status.textContent='';const response=await o.api<{roles:Selection[]}>('trade-formats/read',{tradeId,context});if(started!==generation)return;rows=response.roles;render();host.hidden=false;}
 reload.onclick=()=>void o.act(async()=>{await load(tradeId,context,canSave,roles);await o.changed();});
 return{load,sync,policy,reset(){generation++;tradeId='';advanced.open=false;rows=[];host.hidden=true;controls.replaceChildren();status.textContent='';},
  async prepare(payload:unknown,role='trade',tick?:string){await prepareAmountDisplay(payload,body=>o.api<string[]>('amounts/display',body),tick,policy(role));},
  text(value:string,kind:keyof DisplayPrecision,role='trade',tick?:string){return amountText(value,kind,tick,policy(role));}};
}
