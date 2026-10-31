// Every supported booking type enters through the same trade panel. Product forms
// retain their own explicit terms, native validation and recovery protocols.
import {publicText} from './market-view.js';
import {modeLabel,type PanelMode,type SidePanel} from './side-panel.js';

export type NewProduct='futures'|'swaps'|'ppa'|'gas';
export type NewMode=Extract<PanelMode,{kind:'new'}>;
/** The products a new trade can be of, in the order the switch lists them: none is chosen for the person. */
export const newTradeProducts:readonly (readonly [NewProduct,string])[]=[['futures','Future'],['swaps','Swap'],['ppa','PPA'],['gas','Physical gas']];
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
/**
 * Why booking is not offered before any catalog has been read, when the page has only what it knows of the view and the account to go on: a
 * changed context not yet applied, a view that is not working data, which says which it is, a role without the permission, and otherwise
 * what the person has yet to choose. Once a catalog is read the backend's own words stand (`bookingAllowed`).
 */
export function bookingFallback(state:{contextDirty:boolean;view:ReadView;canBook:boolean}):string{
  if(state.contextDirty)return 'Apply the changed historical context first.';
  if(state.view!=='working')return `${state.view==='live'?'Live':'Historical'} view is read-only. Return to working data to book or amend.`;
  return state.canBook?'Choose an As of date to book a trade.':'Your current role does not have Book Trade permission.';
}

const about:Readonly<Record<NewProduct,string>>={futures:'an exchange contract with a defined expiry',swaps:'fixed or floating prices on independent sides',ppa:'power delivery and cash components on independent schedules',gas:'physical delivery, cash and fees on independent sides'};
// A line on the product chosen, or on both while none is: the person is told what each is, and none is chosen for them.
const aboutLine=(product:NewProduct|null)=>product===null?'Choose an instrument type. Each ticket keeps its own delivery, pricing and lifecycle terms.':`${about[product][0].toUpperCase()}${about[product].slice(1)}.`;
const loading:Readonly<Record<NewProduct,string>>={futures:'Loading the futures contracts…',swaps:'Loading the swap choices…',ppa:'Loading PPA choices…',gas:'Loading physical gas choices…'};
const chooseText='Choose an instrument type to enter a new trade.';

function el<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
  const element=document.createElement(tag);if(className)element.className=className;if(text!==undefined)element.textContent=publicText(text);return element;
}

/**
 * The panel's head and body for a new trade: the title, the switch of its product, a line on the product and the selected product form.
 * `choose` receives the selected product. The form of the product is the page's to put in the
 * panel's form part.
 */
export function setupNewTrade(shell:SidePanel,o:{choose:(product:NewProduct)=>void;busy:()=>boolean}){
  const head=shell.element('head'),body=shell.element('body');
  const root=el('div','new-trade-head'),title=el('h2','side-panel-title'),switcher=el('div','product-switch'),note=el('p','field-help side-panel-note');
  const buttons=new Map<NewProduct,HTMLButtonElement>(newTradeProducts.map(([product,label])=>{
    const button=el('button',undefined,label);button.type='button';button.id=`new-trade-${product}`;button.onclick=()=>o.choose(product);return [product,button] as const;
  }));
  title.id='side-panel-title';switcher.id='new-trade-product';switcher.setAttribute('role','group');switcher.setAttribute('aria-label','Product');switcher.append(...buttons.values());
  note.id='new-trade-note';
  root.append(title,switcher,note);
  function sync(){for(const button of buttons.values())button.disabled=o.busy();}
  return {
    /**
     * Draws the head for a mode, with the switch showing its product, and the body with what the panel says until the form of that product
     * is in it: that it is being loaded, or that a product has still to be chosen.
     */
    show(mode:NewMode,state:'choose'|'loading'){
      const label=modeLabel(mode);
      if(head.firstElementChild!==root)head.replaceChildren(root);
      title.textContent=label;
      for(const [product,button] of buttons)button.setAttribute('aria-pressed',String(mode.product===product));
      note.textContent=aboutLine(mode.product);
      body.setAttribute('role','group');body.setAttribute('aria-label',label);
      const status=el('p','side-panel-status',state==='loading'&&mode.product!==null?loading[mode.product]:chooseText);status.setAttribute('role','status');
      shell.element('view').replaceChildren(status);
      sync();
    },
    sync,
  };
}
