// The side panel's New trade mode (blotter-desk.md, Side panel): a Future | Swap switch, the form of the product chosen, and the way
// to PPA bookings, which are booked on their own page. Whether a new trade can be started at all is the backend's to say, from the
// catalog it returned for this view and account; this module only words it.
import {publicText} from './market-view.js';
import {modeLabel,type PanelMode,type SidePanel} from './side-panel.js';

export type NewProduct='futures'|'swaps';
export type NewMode=Extract<PanelMode,{kind:'new'}>;
/** The products a new trade can be of, in the order the switch lists them: none is chosen for the person. */
export const newTradeProducts:readonly (readonly [NewProduct,string])[]=[['futures','Future'],['swaps','Swap']];
/** What holds New trade back while a request of the person's has no known outcome: it is settled before another is entered. */
export const retainedBlock='A trade request is retained. Reconcile or retry it before creating another.';
/** What a form says of its choices and its price when a live correction changed the inputs they came from and they could not be read again. */
export const newTradeStale='The choices and the price below are from the inputs before the last live correction: they could not be refreshed.';
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

const about:Readonly<Record<NewProduct,string>>={futures:'an exchange contract with a defined expiry',swaps:'fixed or floating prices on independent sides'};
// A line on the product chosen, or on both while none is: the person is told what each is, and none is chosen for them.
const aboutLine=(product:NewProduct|null)=>product===null?`Future: ${about.futures}. Swap: ${about.swaps}.`:`${about[product][0].toUpperCase()}${about[product].slice(1)}.`;
const loading:Readonly<Record<NewProduct,string>>={futures:'Loading the futures contracts…',swaps:'Loading the swap choices…'};
const chooseText='Choose Future or Swap to enter a new trade.';

function el<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
  const element=document.createElement(tag);if(className)element.className=className;if(text!==undefined)element.textContent=publicText(text);return element;
}

/**
 * The panel's head and body for a new trade: the title, the switch of its product, a line on the product, the way to PPA bookings and
 * the note that its choices are from before a live correction. `choose` hears the product the person chose, `ppa` the wish to book a PPA.
 * The form of the product is the page's to put in the panel's form part.
 */
export function setupNewTrade(shell:SidePanel,o:{choose:(product:NewProduct)=>void;ppa:()=>void;busy:()=>boolean}){
  const head=shell.element('head'),body=shell.element('body');
  const root=el('div','new-trade-head'),title=el('h2','side-panel-title'),switcher=el('div','product-switch'),note=el('p','field-help side-panel-note'),stale=el('p','field-help side-panel-note new-trade-stale',newTradeStale);
  const buttons=new Map<NewProduct,HTMLButtonElement>(newTradeProducts.map(([product,label])=>{
    const button=el('button',undefined,label);button.type='button';button.id=`new-trade-${product}`;button.onclick=()=>o.choose(product);return [product,button] as const;
  }));
  title.id='side-panel-title';switcher.id='new-trade-product';switcher.setAttribute('role','group');switcher.setAttribute('aria-label','Product');switcher.append(...buttons.values());
  note.id='new-trade-note';stale.id='new-trade-stale';stale.setAttribute('role','status');stale.hidden=true;
  const ppaLine=el('p','field-help side-panel-note'),ppa=el('button','text-button','Open PPA bookings');ppa.type='button';ppa.id='new-trade-ppa';ppa.onclick=()=>o.ppa();
  ppaLine.append('Booking a power purchase agreement? ',ppa);
  root.append(title,switcher,note,stale,ppaLine);
  function sync(){for(const button of [...buttons.values(),ppa])button.disabled=o.busy();}
  return {
    /**
     * Draws the head for a mode, with the switch showing its product, and the body with what the panel says until the form of that product
     * is in it: that it is being loaded, or that a product has still to be chosen.
     */
    show(mode:NewMode,state:'choose'|'loading'){
      const label=modeLabel(mode);
      if(head.firstElementChild!==root)head.replaceChildren(root);
      title.textContent=label;stale.hidden=true;
      for(const [product,button] of buttons)button.setAttribute('aria-pressed',String(mode.product===product));
      note.textContent=aboutLine(mode.product);
      body.setAttribute('role','group');body.setAttribute('aria-label',label);
      const status=el('p','side-panel-status',state==='loading'&&mode.product!==null?loading[mode.product]:chooseText);status.setAttribute('role','status');
      shell.element('view').replaceChildren(status);
      sync();
    },
    sync,
    /** Says, or stops saying, that the form's choices and price are from before a live correction. */
    stale(on:boolean){stale.hidden=!on;},
  };
}
