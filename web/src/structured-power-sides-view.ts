import {operatingPrice,type OperatingPriceContract,type StructuredPowerKind} from './structured-power-model.js';

export type OperatingSideChoice={kind:StructuredPowerKind;code:string;label:string;quantityKind:'energy'|'fuel'|'right';allowedKinds:readonly ('payment'|'delivery')[]};
export type OperatingCompositionCatalog={sideChoices?:OperatingSideChoice[];maximumSides?:number;compositionImplementation?:string;paymentImplementation:string;quotes:{code:string;label:string;quantityCode:string;currencyCode:string}[];priceContracts?:OperatingPriceContract[]};
type Context={kind:StructuredPowerKind|undefined;energy:string;fuel:string;right:string;currency:string};
type Options={profile?:()=>{inputCurve:string;outputCurve:string}|undefined;catalog:()=>OperatingCompositionCatalog|undefined;context:()=>Context;changed:()=>void};

// Cards edit ordered transport terms. Source eligibility, quantities, dates,
// prices and valuation remain managed business operations.
export function setupOperatingSideCards(host:HTMLElement,o:Options){
 const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
 const field=(parent:HTMLElement,title:string,control:HTMLInputElement|HTMLSelectElement)=>{const wrap=el('div'),label=el('label',title);label.append(control);wrap.append(label);parent.append(wrap);control.required=true;return control;};
 const select=(parent:HTMLElement,title:string)=>field(parent,title,el('select')) as HTMLSelectElement;
 const input=(parent:HTMLElement,title:string)=>field(parent,title,el('input')) as HTMLInputElement;
 const choices=(n:HTMLSelectElement,rows:{value:string;label:string}[])=>{const selected=n.value;n.replaceChildren(new Option('Choose…',''),...rows.map(r=>new Option(r.label,r.value)));n.value=rows.some(r=>r.value===selected)?selected:'';};
 const button=(title:string,work:()=>void)=>{const n=el('button',title);n.type='button';n.onclick=()=>{work();o.changed();};return n;};
 const key=(c:Pick<OperatingPriceContract,'curveCode'|'contractId'>)=>JSON.stringify([c.curveCode,c.contractId]);
 let nextKey=0,enabled=false;
 type Card=ReturnType<typeof create>;const cards:Card[]=[];
 host.append(el('h4','Independent Sides'));const help=el('p','Add each delivery or payment Side explicitly. Repeated quantity sources are permitted; every Side keeps its own price and payment rule.');help.className='field-help';host.append(help);
 const limit=el('p');limit.className='field-help';host.append(limit);const list=el('div');host.append(list);const actions=el('div');actions.className='schedule-actions';host.append(actions);
 const addButton=button('Add Side',()=>add());actions.append(addButton);
 const preset=button('Use explicit payment Side preset',()=>{
  const kind=o.context().kind;
  const sources=kind==='battery'?[['discharging_energy','Discharging energy','1'],['charging_energy','Charging energy','-1']]:kind==='thermal'?[['energy_output','Energy output','1'],['guaranteed_fuel','Guaranteed fuel','-1']]:kind==='solar'?[['payment_energy','Payment energy','1']]:[];
  if(!sources.length)return;
  cards.splice(0);for(const [source,name,coefficient]of sources){const c=add();c.name.value=name;c.source.value=source;c.refresh();c.coefficient.value=coefficient;c.kind.value='payment';c.refresh();}render();
 });actions.append(preset);
 const presetNote=el('p','The preset replaces the cards with the displayed payment Sides. Prices and payment terms remain unselected.');presetNote.className='field-help';host.append(presetNote);
 function render(){const maximum=o.catalog()?.maximumSides,canAdd=enabled&&maximum!==undefined&&cards.length<maximum;limit.textContent=maximum===undefined?'Load the supported Side limit.':`Maximum ${maximum} Sides · ${cards.length} entered.`;addButton.disabled=!canAdd;list.replaceChildren(...cards.map(c=>c.root));for(const [i,c]of cards.entries()){c.up.disabled=!enabled||i===0;c.down.disabled=!enabled||i===cards.length-1;c.duplicate.disabled=!canAdd;}}
 function add(after?:Card,copy?:Card){const c=create();const index=after?cards.indexOf(after)+1:cards.length;cards.splice(index,0,c);c.refresh();if(copy){for(const name of ['name','source','coefficient'] as const)c[name].value=copy[name].value;c.refresh();c.kind.value=copy.kind.value;c.mode.value=copy.mode.value;c.refresh();for(const name of ['quote','price','contract','payment','days'] as const)c[name].value=copy[name].value;}render();c.refresh();return c;}

 function create(){
  const root=el('fieldset');root.dataset.operatingSide='';root.className='card';root.append(el('legend','Side'));const fields=el('div');fields.className='schedule-fields';root.append(fields);
  const sideKey=input(fields,'Side key');sideKey.value=`side_${++nextKey}`;const name=input(fields,'Side name'),source=select(fields,'Quantity source'),coefficient=select(fields,'Sign'),kind=select(fields,'Side kind'),mode=select(fields,'Pricing'),quote=select(fields,'Fixed-price quote'),price=input(fields,'Fixed price'),contract=select(fields,'Market contract');
  choices(coefficient,[{value:'1',label:'Positive contribution (+1)'},{value:'-1',label:'Negative contribution (−1)'}]);
  choices(kind,[]);
  const payment=select(fields,'Monthly payment convention'),days=input(fields,'Payment days after delivery month');days.type='number';days.min='0';days.max='366';days.step='1';
  choices(payment,[{value:'monthly_calendar_unadjusted',label:'Calendar days after delivery month; unadjusted, no external calendar'}]);
  const actions=el('div');actions.className='schedule-actions';root.append(actions);
  const up=button('Move Side up',()=>move(-1)),down=button('Move Side down',()=>move(1));
  const duplicate=button('Duplicate Side',()=>add(card,card));
  const card={root,duplicate,sideKey,name,source,coefficient,kind,mode,quote,price,contract,payment,days,up,down,refresh};
  actions.append(up,down,duplicate,button('Remove Side',()=>{cards.splice(cards.indexOf(card),1);render();}));
  function move(offset:number){const i=cards.indexOf(card),next=i+offset;if(next<0||next>=cards.length)return;cards.splice(i,1);cards.splice(next,0,card);render();}
  function visible(control:HTMLInputElement|HTMLSelectElement,show:boolean){control.parentElement!.parentElement!.hidden=!show;control.disabled=!enabled||!show;}
  function refresh(){
   const catalog=o.catalog(),context=o.context(),sources=catalog?.sideChoices?.filter(s=>s.kind===context.kind)??[];
   choices(source,sources.map(s=>({value:s.code,label:s.label})));
   const selected=sources.find(s=>s.code===source.value);choices(kind,(selected?.allowedKinds??[]).map(value=>({value,label:value==='payment'?'Payment Side':'Delivery Side'})));
   const quantityKind=selected?.quantityKind,quantity=quantityKind?context[quantityKind]:undefined;
   const matches=(row:{quantityCode:string;currencyCode:string})=>row.quantityCode===quantity&&(!context.currency||row.currencyCode===context.currency);
   const profile=context.kind==='thermal'?o.profile?.():undefined;
   const curve=quantityKind==='fuel'?profile?.inputCurve:quantityKind==='energy'?profile?.outputCurve:undefined;
   const contracts=(catalog?.priceContracts??[]).filter(row=>matches(row)&&(!curve||row.curveCode===curve));
   choices(quote,(catalog?.quotes??[]).filter(matches).map(q=>({value:q.code,label:q.label})));
   choices(contract,contracts.map(c=>({value:key(c),label:c.label})));
   choices(mode,[{value:'fixed',label:'Fixed agreed value'},...(contracts.length?[{value:'selected_contract_flat_v1',label:'Selected market contract applied flat across supplied intervals'}]:[])]);
   for(const n of root.querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select'))n.disabled=!enabled;
   visible(quote,mode.value==='fixed');visible(price,mode.value==='fixed');visible(contract,mode.value==='selected_contract_flat_v1');visible(payment,kind.value==='payment');visible(days,kind.value==='payment');
  }
  root.oninput=()=>o.changed();root.onchange=()=>{refresh();o.changed();};return card;
 }
 function refresh(){const kind=o.context().kind;preset.textContent=kind==='battery'?'Use battery sale/purchase payment preset':kind==='thermal'?'Use thermal output/guaranteed-fuel payment preset':kind==='solar'?'Use solar payment-energy preset':'Use explicit payment Side preset';preset.disabled=!enabled||!kind||o.catalog()?.maximumSides===undefined;for(const c of cards)c.refresh();render();}
 function capture(){const catalog=o.catalog();if(!catalog?.compositionImplementation)throw Error('Load the explicit operating Side composition contract.');return cards.map(c=>({key:c.sideKey.value,name:c.name.value,quantitySource:c.source.value,coefficient:c.coefficient.value,kind:c.kind.value,price:operatingPrice(c.mode.value,c.quote.value,c.price.value,(catalog.priceContracts??[]).find(p=>key(p)===c.contract.value)??null),paymentRule:c.kind.value==='payment'?{frequency:'monthly',rule:'days_after_period_end',days:Number(c.days.value),dayOfMonth:null,dayBasis:'calendar_days',shortMonth:null,adjustment:'unadjusted',calendar:null,implementation:catalog.paymentImplementation}:null}));}
 function setEnabled(value:boolean){enabled=value;for(const button of actions.querySelectorAll('button'))button.disabled=!enabled;refresh();}
 function reset(){cards.splice(0);nextKey=0;render();}
 return {capture,refresh,setEnabled,reset};
}
