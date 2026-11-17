import {zonedInput} from './user-time.js';

export type OperationTarget={handler:string;scopeId:string;bindingId:string};
export type OperationChoice={id:string;label:string;value?:unknown};
export type OperationField={name:string;label:string;kind:'text'|'integer'|'instant'|'select'|'reference'|'object'|'array';nullable:boolean;
 choices?:OperationChoice[];fields?:OperationField[];members?:string[];minItems?:number;memberLabel?:string;variants?:{id:string;label:string;target:OperationTarget;parentLabel?:string;action?:string}[]};
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
export type OperationFieldsOptions={api:Api;zone:()=>string;current:()=>boolean;changed:()=>void;act:(work:()=>Promise<void>)=>Promise<void>};
const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const value=document.createElement(tag);if(text!==undefined)value.textContent=text;return value;};

// The backend supplies closed product fields and exact opaque reference choices.
// This editor resolves user clocks and transports observations; it derives no money.
export function mountOperationFields(root:HTMLElement,fields:OperationField[],o:OperationFieldsOptions,variantFields?:(target:OperationTarget,action?:string)=>Promise<OperationField[]>){
 function requireFields(items:OperationField[]){
  if(!Array.isArray(items))throw Error('Reread the complete operation field metadata.');
  const names=new Set<string>();
  for(const field of items){
   if(!field||typeof field.name!=='string'||!field.name||typeof field.label!=='string'||typeof field.nullable!=='boolean'||
    !['text','integer','instant','select','reference','object','array'].includes(field.kind))throw Error('Reread the supported operation field metadata.');
   if(field.variants!==undefined){
    if(field.kind!=='array'||!variantFields||!Array.isArray(field.variants)||new Set(field.variants.map(v=>v.id)).size!==field.variants.length||
     field.variants.some(v=>!v.id||!v.label||!v.target?.handler||!v.target.scopeId||!v.target.bindingId||
      v.action!==undefined&&(typeof v.action!=='string'||!v.action.trim())))throw Error('Reread the exact operation member choices.');
   }else if(['object','array'].includes(field.kind)){if(!Array.isArray(field.fields)||!field.fields.length)throw Error('Reread the nested operation field metadata.');requireFields(field.fields);}
   if(field.minItems!==undefined&&(field.kind!=='array'||!Number.isSafeInteger(field.minItems)||field.minItems<0))throw Error('Reread the operation entry requirements.');
   if(field.memberLabel!==undefined&&(field.kind!=='array'||typeof field.memberLabel!=='string'||!field.memberLabel.trim()))throw Error('Reread the operation member labels.');
   if(['select','reference'].includes(field.kind)&&!Array.isArray(field.choices))throw Error('Reread the exact operation field choices.');
   if(field.kind==='reference'&&(!Array.isArray(field.members)||!field.members.length))throw Error('Reread the exact operation reference field members.');
   for(const member of field.kind==='reference'?field.members!:[field.name]){if(!member||names.has(member))throw Error('Reread the distinct closed operation field metadata.');names.add(member);}
  }
 }
 requireFields(fields);
 let version=0,dirty=false;const clocks:{input:HTMLInputElement;control:ReturnType<typeof zonedInput>}[]=[];
 const references:{label:string;input:HTMLSelectElement}[]=[];
 const parents:{input:HTMLSelectElement;choices:NonNullable<OperationField['variants']>}[]=[];
 function changed(){version++;dirty=true;o.changed();}
 const select=(host:HTMLElement,name:string,options:{id:string;label:string}[])=>{const input=node('select');input.name=name;input.required=true;input.append(new Option('Choose…',''),...options.map(c=>new Option(c.label,c.id)));host.append(input);return input;};
 function build(host:HTMLElement,items:OperationField[]):()=>Promise<Record<string,unknown>>{
  const readers=items.map(field=>{
   const label=node('label',field.label),box=node('div');box.className='operation-field gas-lifecycle-field';label.append(box);host.append(label);
   if(field.kind==='object'){
    const mode=field.nullable?select(box,field.name,[{id:'none',label:'Not applicable'},{id:'terms',label:'Supply these terms'}]):undefined;
    const body=node('div');body.className='schedule-fields';box.append(body);let read:(()=>Promise<Record<string,unknown>>)|undefined;
    const render=()=>{body.replaceChildren();read=mode&&mode.value!=='terms'?undefined:build(body,field.fields!);};render();if(mode)mode.onchange=()=>{render();changed();};
    return async()=>{if(mode&&!mode.value)throw Error('Choose '+field.label+'.');return {[field.name]:mode?.value==='none'?null:await read!()};};
   }
   if(field.kind==='array'){
    const minimum=field.minItems??0;
    const mode=select(box,field.name,[...minimum===0?[{id:'empty',label:'No entries'}]:[],{id:'entries',label:'Select entries'}]),body=node('div'),add=node('button','Add entry');add.type='button';box.append(body,add);
    const rows:{host:HTMLElement;read:()=>Promise<Record<string,unknown>>;member?:HTMLSelectElement}[]=[];
    const render=()=>{body.replaceChildren();rows.length=0;add.hidden=mode.value!=='entries';};render();mode.onchange=()=>{render();changed();};
    add.onclick=()=>{
     const host=node('fieldset'),remove=node('button','Remove entry');host.dataset.operationEntry=field.name;host.dataset.gasEntry=field.name;remove.type='button';
     const row:{host:HTMLElement;read:()=>Promise<Record<string,unknown>>;member?:HTMLSelectElement}={host,read:async()=>{throw Error('Choose the observed member and wait for its exact source fields.');}};
     if(field.variants){
      const caption=field.memberLabel??'Observed performance';
      const queryLabel=node('label',field.memberLabel?'Search '+caption.toLocaleLowerCase():'Search observed performances'),query=node('input');query.type='search';query.autocomplete='off';queryLabel.append(query);host.append(queryLabel);
      const memberLabel=node('label',caption),member=select(memberLabel,field.name+'-member',field.variants);host.append(memberLabel);row.member=member;references.push({label:field.label,input:member});
      parents.push({input:member,choices:field.variants});
      const content=node('div');content.className='schedule-fields';host.append(content);let generation=0;
      const filter=()=>{for(const option of member.options)option.hidden=!!option.value&&option.value!==member.value&&!option.text.toLocaleLowerCase().includes(query.value.toLocaleLowerCase());};query.oninput=filter;
      member.onchange=()=>{
       const current=++generation,choice=field.variants!.find(v=>v.id===member.value);content.replaceChildren();row.read=async()=>{throw Error('Choose the observed member and wait for its exact source fields.');};changed();if(!choice)return;
       content.append(node('p','Reading this '+caption.toLocaleLowerCase()+'’s exact source fields…'));
       void o.act(async()=>{
        try{const loaded=await variantFields!(choice.target,choice.action);if(!o.current()||!host.isConnected||current!==generation||member.value!==choice.id)return;
         requireFields(loaded);content.replaceChildren();row.read=build(content,loaded);changed();
        }catch(error){if(o.current()&&host.isConnected&&current===generation)content.replaceChildren(node('p',error instanceof Error?error.message:'The exact '+caption.toLocaleLowerCase()+' source is unavailable.'));throw error;}
       }).catch(()=>undefined);
      };
     }else row.read=build(host,field.fields!);
     rows.push(row);remove.onclick=()=>{rows.splice(rows.indexOf(row),1);host.remove();changed();};host.append(remove);body.append(host);changed();
    };
    return async()=>{
     if(!mode.value||mode.value==='entries'&&rows.length<Math.max(1,minimum))throw Error('Choose '+field.label+' and add its entries.');
     if(field.variants&&new Set(rows.map(r=>r.member!.value)).size!==rows.length)throw Error('Select each '+(field.memberLabel??'observed performance').toLocaleLowerCase()+' only once.');
     return{[field.name]:mode.value==='empty'?[]:await Promise.all(rows.map(row=>row.read()))};
    };
   }
   if(field.kind==='select'||field.kind==='reference'){
    const choices=field.choices!,input=select(box,field.name,[...(field.nullable?[{id:'@null',label:'Not applicable'}]:[]),...choices]);
    references.push({label:field.label,input});
    return async()=>{
     if(input.value==='@null'&&field.nullable)return field.kind==='reference'?Object.fromEntries(field.members!.map(key=>[key,null])):{[field.name]:null};
     const choice=choices.find(c=>c.id===input.value);if(!choice)throw Error('Choose '+field.label+'.');
     const value=structuredClone(choice.value===undefined?choice.id:choice.value);
     if(field.kind==='reference'){
      if(!value||typeof value!=='object'||Array.isArray(value)||!field.members||Object.keys(value).length!==field.members.length||field.members.some(key=>!Object.hasOwn(value,key)))throw Error('Reread the exact '+field.label+' reference.');
      return value as Record<string,unknown>;
     }
     return{[field.name]:value};
    };
   }
   const mode=field.nullable?select(box,field.name+'-disposition',[{id:'none',label:'Not applicable'},{id:'value',label:'Supply a value'}]):undefined;
   const input=node('input');input.name=field.name;input.required=true;input.type=field.kind==='instant'?'datetime-local':field.kind==='integer'?'number':'text';input.autocomplete='off';
   if(field.kind==='integer')input.step='1';if(field.kind==='instant')input.step='1';box.append(input);
   const time=field.kind==='instant'?zonedInput(input,o.zone,o.api):undefined;if(time){clocks.push({input,control:time});box.append(node('small',o.zone()));}
   if(mode){input.hidden=input.disabled=true;mode.onchange=()=>{input.hidden=input.disabled=mode.value!=='value';changed();};}
   return async()=>{
    if(mode&&!mode.value)throw Error('Choose '+field.label+'.');if(mode?.value==='none')return{[field.name]:null};
    const text=input.value;if(!text.trim())throw Error('Enter '+field.label+'.');
    if(time)return{[field.name]:await time.instant()};
    if(field.kind==='integer'){const value=Number(text);if(!Number.isSafeInteger(value))throw Error('Enter an integer '+field.label+'.');return{[field.name]:value};}
    return{[field.name]:text};
   };
  });
  return async()=>Object.assign(Object.create(null),...await Promise.all(readers.map(read=>read())));
 }
 const read=build(root,fields);root.addEventListener('input',changed);root.addEventListener('change',changed);
 return{dirty:()=>dirty,references:()=>[...references.filter(r=>r.input.isConnected&&r.input.value).map(r=>({label:r.label,value:r.input.selectedOptions[0].text,key:r.input.value})),
  ...parents.filter(r=>r.input.isConnected&&r.input.value).flatMap(r=>{const choice=r.choices.find(c=>c.id===r.input.value);return choice?.parentLabel?[{label:'Related position',value:choice.parentLabel,key:choice.target.scopeId}]:[];})],refresh(){for(const time of clocks)if(time.input.isConnected)time.control.refresh();},async read(){
  const before=version,zone=o.zone();const value=await read();if(!o.current()||before!==version||zone!==o.zone())throw Error('The operation draft or current source changed. Review its current inputs again.');return value;
 }};
}
