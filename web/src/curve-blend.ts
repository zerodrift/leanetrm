import {type PublishedSourceReference} from './custom-curve-model.js';

type Choice={value:unknown;label:string};
type Source={label:string;reference:PublishedSourceReference};
type Request={aggregation:string;alignment:string;inputs:{source:PublishedSourceReference;weight:string}[]};

/** Presentation of explicit server choices, shown as Combine curves in Curve builder's Configuration tab. Alignment, normalization and
 * executable graph construction are performed only by the managed backend. */
export function setupCurveBlend(build:(request:Request)=>void,edited:()=>void){
 const section=document.createElement('section');section.id='curve-blend';section.className='custom-curve-view';
 const heading=document.createElement('h3');heading.textContent='Combine published curves';
 const help=document.createElement('p');help.className='field-help';help.textContent='Choose curves and a weight for each matching delivery period. Dependencies keep their exact published versions. Build a draft graph, then preview its prices. Inputs must already have identical delivery periods and units, with one value per period.';
 const form=document.createElement('form'),choices=document.createElement('div'),rows=document.createElement('div');choices.className='schedule-fields';rows.id='curve-blend-inputs';
 const aggregation=document.createElement('select'),alignment=document.createElement('select');aggregation.id='curve-blend-aggregation';alignment.id='curve-blend-alignment';aggregation.required=alignment.required=true;
 const label=(text:string,input:HTMLElement)=>{const item=document.createElement('label');item.textContent=text;item.append(input);return item;};
 choices.append(label('How should prices combine?',aggregation),label('How should delivery periods match?',alignment));
 const add=document.createElement('button'),submit=document.createElement('button'),discard=document.createElement('button');discard.type='button';discard.id='curve-blend-discard';discard.textContent='Discard ingredient edits';discard.className='secondary';add.type='button';submit.type='submit';add.id='curve-blend-add';submit.id='curve-blend-build';add.textContent='Add a curve';submit.textContent='Build draft graph';add.className='secondary';
 form.append(choices,rows,add,submit,discard);section.append(heading,help,form);
 let sources:Source[]=[],dirty=false;
 const fill=(select:HTMLSelectElement,items:Choice[])=>{const retained=select.value;select.replaceChildren(new Option('Choose…',''),...items.map(item=>new Option(item.label,String(item.value))));select.value=retained;};
 const controls: {row:HTMLElement;source:HTMLSelectElement;weight:HTMLInputElement}[]=[];
 const changed=()=>{dirty=true;edited();};
 function sourceOptions(control:HTMLSelectElement){control.replaceChildren(new Option('Choose a published curve…',''),...sources.map((s,i)=>new Option(s.label,String(i))));}
 function append(){
  const row=document.createElement('div');row.className='schedule-fields curve-blend-row';const source=document.createElement('select'),weight=document.createElement('input'),remove=document.createElement('button');
  source.className='curve-blend-source';weight.className='curve-blend-weight';source.required=weight.required=true;weight.type='text';weight.inputMode='decimal';weight.placeholder='Enter a weight';remove.type='button';remove.className='text-button';remove.textContent='Remove';
  sourceOptions(source);const control={row,source,weight};controls.push(control);
  remove.onclick=()=>{controls.splice(controls.indexOf(control),1);row.remove();changed();};
  row.append(label('Published curve and version',source),label('Weight',weight),remove);rows.append(row);
 }
 add.onclick=()=>{if(controls.length>=30)return;append();changed();};discard.onclick=()=>{reset();edited();};form.oninput=changed;form.onchange=changed;
 form.onsubmit=event=>{event.preventDefault();if(!form.reportValidity())return;build({aggregation:aggregation.value,alignment:alignment.value,
  inputs:controls.map(c=>({source:structuredClone(sources[Number(c.source.value)].reference),weight:c.weight.value}))});};
 function reset(){rows.replaceChildren();controls.length=0;aggregation.value=alignment.value='';append();dirty=false;}
 append();
 return {element:section,dirty:()=>dirty,
  catalog(next:Source[],aggregationChoices:Choice[],alignmentChoices:Choice[]){
   const previous=controls.map(c=>c.source.value===''?undefined:sources[Number(c.source.value)]?.reference);sources=next;
   controls.forEach((c,i)=>{sourceOptions(c.source);const index=previous[i]?sources.findIndex(s=>JSON.stringify(s.reference)===JSON.stringify(previous[i])):-1;c.source.value=index<0?'':String(index);});
   fill(aggregation,aggregationChoices);fill(alignment,alignmentChoices);
  },
  reset,
  clean(){dirty=false;},
  sync(busy:boolean,editable:boolean,replacing:boolean){submit.textContent=replacing?'Replace current draft graph':'Build draft graph';for(const input of form.querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button'))input.disabled=busy||!editable;add.disabled ||=controls.length>=30;discard.disabled ||=!dirty;},
 };
}
