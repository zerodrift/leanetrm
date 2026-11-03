/** Authorized book/portfolio choices shared by the Blotter and Risk. Membership comes from their bound directory reads. */
export type ScopeBook={id:string;name:string;portfolio_id:string;portfolio:string};
export type BookScope={bookIds:string[];portfolioIds:string[]};

function scopeSelect(select:HTMLSelectElement,o:{label:string;all:()=>string;busy:()=>boolean;available?:(option:HTMLOptionElement)=>boolean}){
 const element=<K extends keyof HTMLElementTagNameMap>(tag:K,className='')=>{const n=document.createElement(tag);n.className=className;return n;};
 const caption=element('span','scope-label');caption.id=select.id+'-label';caption.textContent=o.label;
 const label=select.labels[0];
 if(label?.contains(select)){const field=element('div','scope-field');label.replaceWith(field);field.append(caption,select);}
 else if(label)label.replaceWith(caption);else select.before(caption);
 select.hidden=true;select.tabIndex=-1;select.setAttribute('aria-hidden','true');
 const root=element('details','scope-picker');root.id=select.id+'-picker';
 const summary=element('summary'),value=element('span','scope-value'),count=element('span','scope-count');
 value.id=select.id+'-value';summary.setAttribute('aria-labelledby',caption.id+' '+value.id);summary.append(value,count);
 if(select.hasAttribute('aria-describedby'))summary.setAttribute('aria-describedby',select.getAttribute('aria-describedby')!);
 const panel=element('div','scope-picker-panel');panel.id=select.id+'-choices';summary.setAttribute('aria-controls',panel.id);
 const search=element('input','scope-search');search.type='search';search.placeholder='Search '+o.label.toLowerCase();search.setAttribute('aria-label',search.placeholder);
 const chips=element('div','scope-chips'),all=element('button','scope-all');all.type='button';
 const list=element('div','scope-options');list.setAttribute('role','group');list.setAttribute('aria-label',o.label);
 const empty=element('p','scope-empty');empty.textContent='No matching choices';empty.hidden=true;
 panel.append(search,chips,all,list,empty);root.append(summary,panel);select.after(root);
 let drawn='',selectedStamp='',lastFocus:HTMLElement|undefined;
 const available=(option:HTMLOptionElement)=>!option.disabled&&(o.available?.(option)??true);
 const reason=(option:HTMLOptionElement)=>option.disabled?'No longer available':available(option)?'':'Outside selected portfolios';
 const emit=()=>{select.dispatchEvent(new Event('change',{bubbles:true}));sync();};
 function filter(){
  const query=search.value.trim().toLocaleLowerCase();let visible=0;
  for(const row of list.querySelectorAll<HTMLElement>('.scope-option')){row.hidden=!!query&&!row.dataset.search!.includes(query);if(!row.hidden)visible++;}
  empty.hidden=visible>0;
 }
 function sync(){
  const options=Array.from(select.options),selected=options.filter(option=>option.selected),rows=options.filter(option=>available(option)||option.selected);
  const shape=JSON.stringify(rows.map(option=>[option.value,option.text,reason(option)]));
  if(shape!==drawn){
   drawn=shape;list.replaceChildren(...rows.map(option=>{
    const row=element('label','scope-option'),checkbox=element('input'),text=element('span'),name=element('span'),note=element('small');
    checkbox.type='checkbox';checkbox.value=option.value;name.textContent=option.text;note.textContent=reason(option);note.hidden=!note.textContent;
    text.append(name,note);row.append(checkbox,text);row.dataset.search=option.text.toLocaleLowerCase();
    checkbox.onchange=()=>{const current=Array.from(select.options).find(row=>row.value===checkbox.value);if(current)current.selected=checkbox.checked;emit();};return row;
   }));
  }
  const selection=JSON.stringify(selected.map(option=>[option.value,option.text,reason(option)]));
  if(selection!==selectedStamp){
   selectedStamp=selection;chips.replaceChildren(...selected.map(option=>{
    const chip=element('span','scope-chip'),name=element('span'),remove=element('button');name.textContent=option.text;
    remove.type='button';remove.textContent='×';remove.setAttribute('aria-label','Remove '+option.text);chip.dataset.unavailable=String(!available(option));
    remove.onclick=()=>{summary.focus();const current=Array.from(select.options).find(row=>row.value===option.value);if(current)current.selected=false;emit();};chip.append(name,remove);return chip;
   }));
  }
  value.textContent=selected.length?selected.slice(0,2).map(option=>option.text).join(', ')+(selected.length>2?` +${selected.length-2}`:''):o.all();
  count.textContent=String(selected.length);count.hidden=!selected.length;all.textContent='Use '+o.all().toLowerCase();all.hidden=!selected.length;
  root.dataset.unavailable=String(selected.some(option=>!available(option)));
  const disabled=o.busy()||select.disabled;
  summary.setAttribute('aria-disabled',String(disabled||!options.length));search.disabled=all.disabled=disabled;
  for(const button of chips.querySelectorAll('button'))button.disabled=disabled;
  for(const checkbox of list.querySelectorAll<HTMLInputElement>('input')){
   const option=options.find(option=>option.value===checkbox.value)!;checkbox.checked=option.selected;checkbox.disabled=disabled||!available(option);
  }
  if(!options.length){search.value='';root.open=false;}
  filter();
  if(lastFocus&&!lastFocus.isConnected)lastFocus=undefined;
  if(!disabled&&lastFocus&&document.activeElement===document.body&&!(lastFocus as HTMLInputElement).disabled)lastFocus.focus({preventScroll:true});
 }
 all.onclick=()=>{summary.focus();for(const option of select.options)option.selected=false;emit();};
 search.oninput=filter;
 summary.onclick=event=>{if(summary.getAttribute('aria-disabled')==='true')event.preventDefault();};
 root.addEventListener('keydown',event=>{if(event.key==='Escape'&&root.open){event.preventDefault();event.stopPropagation();root.open=false;summary.focus();}});
 root.addEventListener('focusin',event=>{lastFocus=event.target as HTMLElement;});
 document.addEventListener('pointerdown',event=>{if(!root.contains(event.target as Node)){lastFocus=undefined;root.open=false;}});
 select.addEventListener('change',sync);
 new MutationObserver(sync).observe(select,{childList:true,subtree:true,attributes:true,attributeFilter:['disabled','label']});
 sync();return {sync,element:root};
}

export function setupBookScopePicker(o:{books:HTMLSelectElement;portfolios:HTMLSelectElement;busy:()=>boolean}){
 const portfolios=scopeSelect(o.portfolios,{label:'Portfolios',all:()=> 'All portfolios',busy:o.busy});
 const books=scopeSelect(o.books,{label:'Books',all:()=>o.portfolios.selectedOptions.length?'All included books':'All books',busy:o.busy,
  available:option=>!o.portfolios.selectedOptions.length||Array.from(o.portfolios.selectedOptions).some(portfolio=>portfolio.value===option.dataset.portfolioId)});
 const sync=()=>{portfolios.sync();books.sync();};
 for(const [picker,other] of [[portfolios,books],[books,portfolios]])picker.element.querySelector('summary')!.addEventListener('click',()=>{if(!picker.element.open)other.element.open=false;});
 o.portfolios.addEventListener('change',books.sync);
 new MutationObserver(books.sync).observe(o.portfolios,{childList:true,subtree:true});
 function fill(select:HTMLSelectElement,rows:{id:string;name:string;portfolio_id?:string}[],selected:string[],missing:string){
  select.replaceChildren(...rows.map(row=>{const option=new Option(row.name,row.id,false,selected.includes(row.id));if(row.portfolio_id)option.dataset.portfolioId=row.portfolio_id;return option;}),
   ...selected.filter(id=>!rows.some(row=>row.id===id)).map(id=>{const option=new Option(missing,id,false,true);option.disabled=true;return option;}));
 }
 return {sync,set(directory:readonly ScopeBook[],scope:BookScope){
  fill(o.portfolios,[...new Map(directory.map(book=>[book.portfolio_id,{id:book.portfolio_id,name:book.portfolio}])).values()],scope.portfolioIds,'Unavailable portfolio');
  fill(o.books,directory.map(book=>({id:book.id,name:`${book.portfolio} · ${book.name}`,portfolio_id:book.portfolio_id})),scope.bookIds,'Unavailable book');sync();
 }};
}
