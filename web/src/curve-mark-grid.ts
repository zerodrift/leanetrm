import {correlationInk} from './curve-mark-charts.js';
import {addressId,cellPresentation,keyId,keyLabel,moveCell,type Address,type CellKey,type LayoutCell,type Setup} from './curve-mark-model.js';

function element<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string){
 const node=document.createElement(tag);
 if(text!==undefined)node.textContent=text;
 return node;
}
type GridOptions={
 mayEdit:(cell:LayoutCell)=>boolean;
 selected:(cell:LayoutCell)=>void;
 edited:(key:CellKey,value:string)=>void;
 editing:()=>void;
 paste:(text:string)=>void;
};

/** The server's display addresses determine ordering and canonical mirror identities. */
export function setupMarkGrid(host:HTMLElement,o:GridOptions){
 let buttons=new Map<string,HTMLButtonElement>(),openers=new Map<string,()=>void>(),cells:LayoutCell[]=[],active:Address|undefined;
 let deferred:Parameters<typeof draw>|undefined;
 let editor:{input:HTMLInputElement;key:CellKey;address:Address;touched:boolean}|undefined;
 const matches=(a:Address,b:Address|undefined)=>!!b&&addressId(a)===addressId(b);
 const buttonAt=(at:Address)=>buttons.get(addressId(at));
 function selected(cell:LayoutCell){
  if(active){const previous=buttonAt(active);previous?.setAttribute('aria-pressed','false');}
  active=cell.address;
  buttonAt(active)?.setAttribute('aria-pressed','true');
  o.selected(cell);
 }
 host.onpaste=event=>{
  if(!event.clipboardData?.types.includes('text/plain'))return;
  event.preventDefault();
  const id=event.target instanceof Element?event.target.closest<HTMLElement>('[data-mark-address]')?.dataset.markAddress:undefined;
  const cell=cells.find(item=>addressId(item.address)===id);
  if(!cell||!o.mayEdit(cell))return;
  selected(cell);
  o.paste(event.clipboardData.getData('text/plain'));
 };
 function finish(commit:boolean,following?:Address,refocus=true){
  if(!editor)return;
  const {input,key,address,touched}=editor;
  editor=undefined;
  input.replaceWith(buttonAt(address)!);
  if(commit&&touched)o.edited(key,input.value);
  else o.editing();
  // A touched edit can draw newer state before the deferred draw.
  if(deferred)draw(...deferred);
  if(refocus)buttonAt(following??address)?.focus();
 }
 function start(cell:LayoutCell,presented:string,initial?:string){
  if(!cell.key||!o.mayEdit(cell))return;
  if(editor)finish(true,undefined,false);
  const button=buttonAt(cell.address);
  if(!button)return;
  const input=element('input');
  input.type='text';input.inputMode='decimal';input.autocomplete='off';input.spellcheck=false;
  input.className='mark-cell-input';input.setAttribute('aria-label',`Edit ${button.getAttribute('aria-label')}`);
  input.value=initial??presented;
  editor={input,key:cell.key,address:cell.address,touched:initial!==undefined};
  button.replaceWith(input);
  o.editing();
  input.focus();
  if(initial===undefined)input.select();
  input.oninput=()=>{if(editor?.input===input)editor.touched=true;};
  input.onblur=event=>{
   if(editor?.input!==input)return;
   const target=event.relatedTarget;
   const next=cells.find(cell=>buttonAt(cell.address)===target)?.address;
   finish(true,undefined,false);
   if(next&&target instanceof HTMLElement&&!target.isConnected)buttonAt(next)?.focus();
  };
  input.onkeydown=event=>{
   if(event.key==='Escape'){event.preventDefault();event.stopPropagation();finish(false);return;}
   const direction=event.key==='Enter'?'down':event.key==='Tab'?(event.shiftKey?'previous':'next'):undefined;
   if(direction){
    const following=moveCell(cells.map(item=>item.address),cell.address,direction);
    if(direction==='down'||following)event.preventDefault();
    event.stopPropagation();
    finish(true,following);
   }
  };
 }
 function draw(setup:Setup|undefined,edits:ReadonlyMap<string,string>,pending:ReadonlyMap<string,string>,preserve=true){
  if(editor){deferred=[setup,edits,pending,preserve];return;}
  deferred=undefined;
  const focus=host.contains(document.activeElement),scrollLeft=host.scrollLeft,scrollTop=host.scrollTop;
  const wanted=preserve?active:undefined;
  host.replaceChildren();buttons=new Map();openers=new Map();cells=[];active=undefined;
  if(!setup?.layout||!setup.view||!setup.draft)return;
  const {layout,view,draft}=setup,matrix=view.kind==='correlation'&&view.mode==='grid',strip=view.mode==='strip';
  const data=new Map(draft.cells.map(cell=>[keyId(cell.state.key),cell]));
  const names=new Map(setup.curves.map(curve=>[curve.code,curve.name]));
  const label=(code:string)=>names.get(code)??code;
  const table=element('table');table.className='mark-grid';table.style.setProperty('--mark-columns',String(layout.columnCount));
  const caption=element('caption',strip?`${keyLabel(layout.cells[0].key!)} monthly strip`:matrix?`Correlation matrix · ${view.fromMonth}`:'Volatility grid · decimal fractions');
  caption.className='sr-only';table.append(caption);
  const widths=element('colgroup');widths.append(element('col'),...Array.from({length:layout.columnCount},()=>element('col')));table.append(widths);
  const head=table.createTHead().insertRow();
  function heading(row:HTMLTableRowElement,text:string,scope:'row'|'col',code?:string){
   const cell=element('th',text);cell.scope=scope;cell.title=code?`${text} · ${code}`:text;row.append(cell);return cell;
  }
  heading(head,strip?'Delivery month':matrix?'Curve pair':'Curve / month','col');
  for(let column=0;column<layout.columnCount;column++){
   const code=view.curves[column];
   heading(head,strip?(view.kind==='volatility'?'Volatility σ':'Correlation ρ'):matrix?label(code):layout.cells[column].key!.month,'col',matrix?code:undefined);
  }
  const body=table.createTBody();
  for(let row=0;row<layout.rowCount;row++){
   const line=body.insertRow(),code=view.curves[row];
   const rowHeader=heading(line,strip?layout.cells[row].key!.month:label(code),'row',strip?undefined:code);
   if(!strip&&label(code)!==code)rowHeader.append(element('small',code));
   for(let column=0;column<layout.columnCount;column++){
    const display=layout.cells[row*layout.columnCount+column],td=line.insertCell(),key=display.key;
    td.dataset.markAddress=addressId(display.address);
    if(!key){
     td.className='mark-diagonal';td.title='Same curve; no mark';
     td.append(element('span','Same curve; no mark'));continue;
    }
    const cell=data.get(keyId(key))!,presented=cellPresentation(cell,edits.get(keyId(key)),pending.get(keyId(key)));
    const button=element('button');button.type='button';button.className='mark-cell';
    button.dataset.markCell=addressId(display.address);button.dataset.state=presented.state;button.dataset.editable=String(o.mayEdit(display));
    if(!display.editable)button.dataset.mirror='';
    button.setAttribute('aria-pressed',String(matches(display.address,wanted)));
    const state=element('span',presented.label);state.className='mark-cell-state';
    const amount=element('span',presented.value||'∅');amount.className='mark-cell-amount';
    button.append(amount,state);
    const detail=[keyLabel(key),key.month,presented.label,presented.value||'Empty input'];
    if(!display.editable)detail.push('Mirror');
    else if(cell.state.editBlock)detail.push(cell.state.editBlock.message);
    if(cell.state.selected)detail.push(`Published revision ${cell.state.selected.revision}`);
    if(presented.state==='invalid'&&cell.error)detail.push(cell.error.message);
    button.setAttribute('aria-label',detail.join(' · '));
    button.title=[...detail,setup.convention].join('\n');
    if(matrix&&['published','draft'].includes(presented.state))button.style.setProperty('--mark-heat',correlationInk(presented.value));
    const original=presented.state==='missing'?'':presented.value;
    button.onfocus=()=>selected(display);
    button.onclick=()=>selected(display);
    const edit=()=>start(display,original);
    button.ondblclick=edit;openers.set(addressId(display.address),edit);
    button.onkeydown=event=>{
     if(event.ctrlKey||event.metaKey||event.altKey)return;
     if(event.key==='Enter'||event.key==='F2'){event.preventDefault();if(o.mayEdit(display))start(display,original);else o.selected(display);return;}
     if(event.key==='Tab'){
      const next=moveCell(cells.map(item=>item.address),display.address,event.shiftKey?'previous':'next');
      if(next){event.preventDefault();buttonAt(next)?.focus();}
      return;
     }
     if(event.key.length===1&&event.key!==' '&&o.mayEdit(display)){event.preventDefault();start(display,original,event.key);}
    };
    buttons.set(addressId(display.address),button);cells.push(display);td.append(button);
   }
  }
  host.append(table);
  active=cells.find(cell=>matches(cell.address,wanted))?.address;
  if(preserve){host.scrollTop=scrollTop;host.scrollLeft=scrollLeft;}
  if(active&&focus)buttonAt(active)?.focus({preventScroll:true});
 }
 return {draw,active:()=>editor!==undefined,commit:()=>finish(true,undefined,false),cancel:()=>finish(false,undefined,false),
  edit:(address:Address)=>openers.get(addressId(address))?.(),focus:(address:Address)=>buttonAt(address)?.focus()};
}
