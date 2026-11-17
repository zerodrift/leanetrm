// Every supported booking type enters through the same trade panel. Product forms
// retain their own explicit terms, native validation and recovery protocols.
import {publicText} from './market-view.js';
import {modeLabel,type PanelMode,type SidePanel} from './side-panel.js';

export type NewMode=Extract<PanelMode,{kind:'new'}>;
export type NewProduct=NonNullable<NewMode['product']>;
/** What holds New trade back while a request of the person's has no known outcome: it is settled before another is entered. */
export const retainedBlock='A trade request is retained. Reconcile or retry it before creating another.';
const noReason='Booking is not available in this view, and the backend did not say why.';

type Control={enabled:true;reason:null}|{enabled:false;reason:string};
/** Whether a catalog the backend returned allows booking, and in the backend's own words why not when it does not. */
export function bookingAllowed(catalog:{canBook:boolean;bookingUnavailableReason?:string|null}):Control{
  return catalog.canBook?{enabled:true,reason:null}:{enabled:false,reason:catalog.bookingUnavailableReason??noReason};
}

/**
 * Whether New trade is offered, and why not when it is not. A retained request comes first, since it has to be settled whatever else is
 * true; then the backend's own answer for this view and account; and before any read, what the page needs.
 */
export function newTradeControl(state:{catalog:{canBook:boolean;bookingUnavailableReason?:string|null}|undefined;retained:boolean;fallback:string}):Control{
  if(state.retained)return {enabled:false,reason:retainedBlock};
  return state.catalog===undefined?{enabled:false,reason:state.fallback}:bookingAllowed(state.catalog);
}

/** The view a person is in, as the page knows it before it has read anything: working data, a historical context or a live selection. */
export type ReadView='working'|'historical'|'live';
/** Why nothing is written while a changed historical context is not yet applied. */
export const unappliedContext='Apply the changed historical context first.';
/**
 * Why booking is not offered before any catalog has been read, when the page has only what it knows of the view and the account to go on: a
 * changed context not yet applied, a view that is not working data, which says which it is, a role without the permission, and otherwise
 * what the person has yet to choose. Once a catalog is read the backend's own words stand (`bookingAllowed`).
 */
export function bookingFallback(state:{contextDirty:boolean;view:ReadView;canBook:boolean}):string{
  if(state.contextDirty)return unappliedContext;
  if(state.view!=='working')return `${state.view==='live'?'Live':'Historical'} view is read-only. Return to working data to book or amend.`;
  return state.canBook?'Choose an As of date to book a trade.':'Your current role does not have Book Trade permission.';
}

const about:Readonly<Record<NewProduct,string>>={rec:'qualified certificates with exact generation vintage, independent physical market value and consideration',futures:'an exchange contract with a defined expiry',options:'a listed option on an exchange futures contract, with its premium paid up front',swaps:'fixed or floating prices on independent sides',ppa:'power delivery and cash components on independent schedules',gas:'physical delivery, cash and fees on independent sides',transport:'one whole Transport rights package with an independently selected fixed fee. The agreement initially has no adopted rights value; after booking, select a published value for the whole package in Lifecycle','park-loan':'a gas Park or Loan service with explicitly selected title and fee terms',crude:'fixed standard-volume crude delivery with independent cash and handling fees',lng:'a future LNG cargo with separate loading and discharge terms, Incoterms and fixed consideration'};
const loading:Readonly<Record<NewProduct,string>>={rec:'Loading qualified REC choices…',futures:'Loading the futures contracts…',options:'Loading the listed options…',swaps:'Loading the swap choices…',ppa:'Loading PPA choices…',gas:'Loading physical gas choices…',transport:'Loading Transport choices…','park-loan':'Loading Park and loan choices…',crude:'Loading physical crude choices…',lng:'Loading physical LNG choices…'};
const chooseText='Choose an instrument type to enter a new trade.';

/** One entry of the chooser: an instrument a ticket opens for, a page another instrument is entered on, or one not yet offered. */
export type InstrumentEntry=
  |{kind:'product';product:NewProduct;name:string}
  |{kind:'link';id:'operating-right';name:string;about:string;page:'physical-power'}
  |{kind:'soon';id:'storage';name:string;about:string};
export type InstrumentGroupId='financial'|'power'|'gas-lng'|'oil-liquids'|'structured';
export type InstrumentGroup={id:InstrumentGroupId;name:string;entries:readonly InstrumentEntry[]};
const product=(key:NewProduct,name:string):InstrumentEntry=>({kind:'product',product:key,name});
/** The instruments of a new trade in their groups, in the order the chooser lists them: none is chosen for the person. */
export const instrumentGroups:readonly InstrumentGroup[]=[
  {id:'financial',name:'Financial',entries:[product('futures','Future'),product('options','Futures option'),product('swaps','Swap')]},
  {id:'power',name:'Power',entries:[product('ppa','PPA'),product('rec','REC forward')]},
  {id:'gas-lng',name:'Gas & LNG',entries:[product('gas','Physical gas'),product('park-loan','Park and loan'),product('lng','Physical LNG')]},
  {id:'oil-liquids',name:'Oil & liquids',entries:[product('crude','Physical crude')]},
  {id:'structured',name:'Structured',entries:[
    {kind:'link',id:'operating-right',name:'Operating right',about:'Thermal toll, battery or solar output right · entered on Operating facilities',page:'physical-power'},
    product('transport','Transport'),
    {kind:'soon',id:'storage',name:'Storage',about:'Will book a storage contract: injections and withdrawals against its capacity and inventory.'}]},
];
const names=new Map(instrumentGroups.flatMap(group=>group.entries.flatMap(entry=>entry.kind==='product'?[[entry.product,entry.name] as const]:[])));
/** Every product once, in the chooser's order. */
export const newProducts:readonly NewProduct[]=[...names.keys()];
export const productName=(key:NewProduct):string=>names.get(key)!;
/** The product's one-line description as a sentence. */
export const productAbout=(key:NewProduct):string=>`${about[key][0].toUpperCase()}${about[key].slice(1)}.`;
/**
 * The groups a window draws: a product it cannot open, a link to a page the account's sidebar does not list and, where the window shows
 * none, the entries not yet offered are left out, and a group left with nothing is not returned.
 */
export function chooserGroups(o:{opens:(product:NewProduct)=>boolean;links:(page:string)=>boolean;soon:boolean}):readonly InstrumentGroup[]{
  const shown=(entry:InstrumentEntry)=>entry.kind==='product'?o.opens(entry.product):entry.kind==='link'?o.links(entry.page):o.soon;
  return instrumentGroups.map(group=>({...group,entries:group.entries.filter(shown)})).filter(group=>group.entries.length>0);
}

function el<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
  const element=document.createElement(tag);if(className)element.className=className;if(text!==undefined)element.textContent=publicText(text);return element;
}

/**
 * The panel's head and body for a new trade: the title, the chooser of its instrument, a line on the instrument and the selected form.
 * `choose` receives the selected product and `link` the page an entry is entered on instead. `groups` are the entries this window
 * offers, and `offered` whether booking is: where it is not, the chooser is not drawn, as the page says why. The form of the product is
 * the page's to put in the panel's form part.
 */
export function setupNewTrade(shell:SidePanel,o:{choose:(product:NewProduct)=>void;link:(page:string)=>void;groups:()=>readonly InstrumentGroup[];offered:()=>boolean;busy:()=>boolean}){
  const head=shell.element('head'),body=shell.element('body');
  const root=el('div','new-trade-head'),title=el('h2','side-panel-title'),chooser=el('div','instrument-chooser'),note=el('p','field-help side-panel-note');
  title.id='side-panel-title';chooser.id='new-trade-product';chooser.setAttribute('role','group');chooser.setAttribute('aria-label','Instrument');chooser.dataset.state='choose';
  note.id='new-trade-note';root.append(title,chooser,note);
  let products=new Map<NewProduct,HTMLButtonElement>(),links:HTMLButtonElement[]=[],drawn:string|undefined,chosen:NewProduct|null=null;
  function tile(entry:InstrumentEntry){
    const button=el('button','instrument');button.type='button';
    button.append(el('span','instrument-name',entry.name));
    if(entry.kind==='product'){
      button.id=`new-trade-${entry.product}`;button.onclick=()=>o.choose(entry.product);button.append(el('span','instrument-about',productAbout(entry.product)));products.set(entry.product,button);
    }else if(entry.kind==='link'){
      const mark=el('span','instrument-mark','↗');mark.setAttribute('aria-hidden','true');
      button.id=`new-trade-${entry.id}`;button.classList.add('instrument-link');button.onclick=()=>o.link(entry.page);button.append(mark,el('span','instrument-about',entry.about));links.push(button);
    }else{
      // Not yet offered: disabled for good, which the page's busy sweep leaves as drawn.
      button.classList.add('ticket-soon');button.dataset.ownControls='';button.disabled=true;button.setAttribute('aria-disabled','true');button.title=entry.about;button.append(el('span','ticket-soon-tag','Coming soon'));
    }
    return button;
  }
  // The entries are drawn again only when the window offers others: a press, and focus, stay on the button they were on.
  function draw(){
    const groups=o.groups(),key=JSON.stringify(groups.map(group=>[group.id,group.entries.map(entry=>entry.kind==='product'?entry.product:entry.id)]));
    if(key===drawn)return;
    drawn=key;products=new Map();links=[];
    chooser.replaceChildren(...groups.map(group=>{
      const section=el('section','instrument-group'),entries=el('div','instrument-entries');section.dataset.group=group.id;
      entries.append(...group.entries.map(tile));section.append(el('h3','instrument-group-name',group.name),entries);return section;
    }));
  }
  function sync(){
    draw();chooser.hidden=!o.offered();
    for(const [key,button] of products){button.disabled=o.busy();button.setAttribute('aria-pressed',String(key===chosen));}
    for(const button of links)button.disabled=o.busy();
  }
  const say=(text:string)=>{const status=el('p','side-panel-status',text);status.setAttribute('role','status');shell.element('view').replaceChildren(status);};
  return {
    /**
     * Draws the head for a mode, with the chooser showing its product, and the body with what the panel says until the form of that product
     * is in it: that it is being loaded, or that a product has still to be chosen.
     */
    show(mode:NewMode,state:'choose'|'loading'){
      const label=modeLabel(mode);
      if(head.firstElementChild!==root)head.replaceChildren(root);
      title.textContent=label;chosen=mode.product;chooser.dataset.state=chosen===null?'choose':'chosen';
      note.textContent=chosen===null?'Choose an instrument type. Each ticket keeps its own delivery, pricing and lifecycle terms.':productAbout(chosen);
      body.setAttribute('role','group');body.setAttribute('aria-label',label);
      say(state==='loading'&&chosen!==null?loading[chosen]:chooseText);
      sync();
    },
    /** Says that a product is being read while the panel still shows the choice of one: the next `show` replaces it. */
    loading(key:NewProduct){say(loading[key]);},
    sync,
  };
}
