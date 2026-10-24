// The side panel's position view (blotter-desk.md, Side panel): what one position is, worth and priced at, its market's curve
// with the position's delivery and price on it, its terms, and its revisions. What is shown is selected from the rows and
// reads the desk already holds; amounts stay the backend's exact strings in the backend's display text.
import {clearChart,renderChart,type ChartPoint,type ChartSpec} from './chart-view.js';
import {curvePoints,minus,quantityText,signOf,signedText,type CurveShape,type DeskRow,type Format,type Holding,type HoldingFormats} from './desk-model.js';
import {deliveryLabel,publicText} from './market-view.js';
import {setupTabs,type SidePanel} from './side-panel.js';

export const positionTabs=[['curve','Curve'],['terms','Terms'],['risk','Risk'],['history','History']] as const;
export type PositionTab=typeof positionTabs[number][0];
export const riskEmpty="A risk run's results for this position will show here.";

/** An exact amount with its display text and sign. */
export type Signed={text:string;exact:string;sign:'positive'|'negative'|'zero'};
const signed=(exact:string,format:Format):Signed=>({text:signedText(exact,format),exact,sign:signOf(exact)});

export type PositionSummary={
  pill:DeskRow['pill'];reference:string;revision:string;book:string;market:string;commodity:{code:string;name:string};subtitle:string;
  /** The position's value with its measure, or why the close cannot value it. */
  value:{label:string;currency:string}&({amount:Signed;unavailable:null}|{amount:null;unavailable:string});
  /**
   * The backend's mark less the price the position was done at, or nothing where the backend has none. A price move has its sign in
   * its text and no tone: which way it cuts depends on the position's direction, which the browser does not combine into a gain.
   */
  markMinusPrice:{text:string;exact:string;label:string;unit:string}|null;
  facts:{label:string;text:string;unit:string|null}[];
};
/** The settlement state as the Blotter's trade columns state it. */
const settlementText=(settlement:string)=>publicText(settlement==='unprocessed'?'Unprocessed':settlement);

/**
 * The header, value and facts of a position, from its workspace row and the desk's own row for it. A position the close
 * cannot value says why in place of its value and has no mark less price: a row that has both is refused.
 */
export function positionSummary(h:Holding,row:DeskRow,formats:HoldingFormats):PositionSummary{
  if(row.unavailable!==null&&h.markMinusPrice!==null)throw Error('The Blotter returned a mark minus price for a position it cannot value.');
  const label=row.measure==='PV'?'Present value':'Execution P&L';
  const value:PositionSummary['value']=row.unavailable===null?{label,currency:row.currency,amount:signed(row.value,formats.money),unavailable:null}:{label,currency:row.currency,amount:null,unavailable:row.unavailable};
  const markMinusPrice=h.markMinusPrice===null?null:{text:signedText(h.markMinusPrice,formats.markMinusPrice),exact:h.markMinusPrice,label:`Mark − ${row.priceLabel.toLowerCase()}`,unit:row.priceUnit};
  const facts:PositionSummary['facts']=[
    {label:`${row.priceLabel} price`,text:row.price===null?'None':minus(formats.price(row.price)),unit:row.price===null?null:row.priceUnit},
    ...(row.mark!==null?[{label:'Mark',text:minus(formats.mark(row.mark)),unit:row.priceUnit}]:row.markNote!==null?[{label:'Mark',text:row.markNote,unit:null}]:[]),
    {label:'Quantity',text:quantityText(row,formats.quantity),unit:row.unit},
    ...(typeof h.settlement==='string'?[{label:'Settlement',text:settlementText(h.settlement),unit:null}]:[]),
  ];
  return {pill:row.pill,reference:row.reference,revision:row.revision,book:h.book,market:row.market,commodity:h.commodity,
    subtitle:`${row.delivery} · ${row.side} · ${quantityText(row,formats.quantity)} ${row.unit}`,value,markMinusPrice,facts};
}

export type CurveView={state:'loading'}|{state:'unavailable';reason:string}|{state:'chart';spec:ChartSpec;note:string|null};
const otherMarket='The curve shape belongs to another market than the position.';
const ownShape=(shape:CurveShape,h:Holding)=>{if(shape.curve!==h.curve)throw Error(otherMarket);};
/**
 * The position's market curve at the close, from the shape the desk read for it: the contracts of the position's delivery
 * period marked, and its price on the same axis when they share a unit. A shape still being read is loading; one that is
 * not there says why, in the row's own words. The price is never drawn against a curve in another unit.
 */
export function curveView(shape:CurveShape|string|undefined,h:Holding,row:DeskRow,formats:HoldingFormats):CurveView{
  if(shape===undefined)return {state:'loading'};
  if(typeof shape==='string')return {state:'unavailable',reason:shape};
  ownShape(shape,h);
  const sparkline=curvePoints(shape,row);
  if(sparkline.state==='unavailable')return {state:'unavailable',reason:sparkline.reason};
  const unit=shape.unit!,drawn=shape.points!;
  const points:ChartPoint[]=drawn.map(p=>({x:p.contractId,label:deliveryLabel(p.deliveryFrom,p.deliveryTo),y:p.price}));
  const spec:ChartSpec={title:`${row.market} · forward curve`,axis:'delivery',unit,kind:'line',series:[{name:'Curve at this close',points}],marked:sparkline.marks.map(i=>points[i].x)};
  if(spec.marked!.length)spec.subtitle=`Marked: delivery ${row.delivery}`;
  const notes:string[]=[];
  if(row.price===null)notes.push('This position has no fixed price to draw.');
  else if(row.priceUnit!==unit)notes.push(`The ${row.priceLabel.toLowerCase()} price is in ${row.priceUnit} and the curve in ${unit}, so the price is not drawn on it.`);
  else spec.reference={value:row.price,label:`${row.priceLabel} ${minus(formats.price(row.price))} ${row.priceUnit}`};
  if(!sparkline.marks.length)notes.push("This position's delivery is not on the curve at this close.");
  return {state:'chart',spec,note:notes.length?notes.join(' '):null};
}

/**
 * The market and contract Open curve selects: a future is in its own contract; a swap is in the one contract of its delivery
 * period, when the curve shows exactly one, and otherwise names the curve alone.
 */
export function openCurveTarget(h:Holding,row:DeskRow,shape:CurveShape|string|undefined):{curve:string;contractId:string|null}{
  if(typeof shape==='object')ownShape(shape,h);
  if(row.pill==='FUT')return {curve:h.curve,contractId:h.contractId};
  if(typeof shape!=='object')return {curve:h.curve,contractId:null};
  const sparkline=curvePoints(shape,row);
  return {curve:h.curve,contractId:sparkline.state==='drawn'&&sparkline.marks.length===1?shape.points![sparkline.marks[0]].contractId:null};
}

/** What the Terms and History tabs show of one trade revision, drawn from its editor read: titled lists of exact facts. */
export type TermSection={title:string;rows:readonly (readonly [string,string])[]};
export type DetailView={terms:TermSection[];history:TermSection[]};
/** Whether the editor can amend the revision, and why not when it cannot. */
export type AmendState={allowed:true;reason:null}|{allowed:false;reason:string};
/** An editor read of a trade revision: its amendment state, and the views drawn from it in the person's time zone. */
export type DetailRead={amend:AmendState;view:(zone:string)=>DetailView};

/**
 * Why Amend is not offered: first that the account cannot amend, then why the revision cannot be, once the editor has been read.
 * Nothing when it can be.
 */
export function amendNote(accountCanAmend:boolean,revision:AmendState|undefined):string|null{
  if(!accountCanAmend)return 'This account cannot amend trades in this market context.';
  return revision&&!revision.allowed?`This position cannot be amended: ${revision.reason}`:null;
}
/**
 * The head's amendment control: Amend while there is nothing against it, none, with the reason in view, when there is, and Close
 * editor for as long as the form is open, which can always be closed.
 */
export function amendControl(state:{accountCanAmend:boolean;revision:AmendState|undefined;editing:boolean}):{label:'Amend'|'Close editor'|null;note:string|null}{
  if(state.editing)return {label:'Close editor',note:null};
  const note=amendNote(state.accountCanAmend,state.revision);
  return note===null?{label:'Amend',note:null}:{label:null,note};
}
/** What Open curve says beside its button when there is no curve to open: for a market Curves & prices does not list. */
export const curveNote=(listed:boolean)=>listed?null:"Curves & prices does not list this position's market, so there is no curve to open.";

/** What the panel draws a position from: the workspace row and its desk row, how its amounts read, its market's curve shape and its commodity's color. */
export type PositionInput={holding:Holding;row:DeskRow;formats:HoldingFormats;shape:CurveShape|string|undefined;color:string};

function el<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
  const element=document.createElement(tag);if(className)element.className=className;if(text!==undefined)element.textContent=publicText(text);return element;
}
const failure=(error:unknown,fallback:string)=>error instanceof Error?error.message:fallback;
type Detail={key:string}&({state:'loading'}|{state:'ready';read:DetailRead}|{state:'error';message:string});

/**
 * The position view over the shell's head, tab strip and tab panel. The Terms and History tabs read the trade revision once, when
 * first shown, in the market context of the desk; a read made in another context, or of another position, is dropped. Amend
 * opens the position's amendment, whose form Terms shows in place of its read view for as long as it is open.
 */
export function setupPositionPanel(shell:SidePanel,o:{
  zone:()=>string;busy:()=>boolean;canAmend:()=>boolean;
  /** Whether Curves & prices lists a market: a position in one it does not list has no curve to open. */
  curveListed:(curve:string)=>boolean;
  /** The market context the desk reads in, as a key: a trade read belongs to one. */
  context:()=>string;
  read:(h:Holding,formats:HoldingFormats)=>Promise<DetailRead>;
  /**
   * The amendment of a position, whose form the page puts in the shell's form part: `open` starts it, `shown` says whether the form of
   * a position is open, and `close` ends it, asking first when it holds edits.
   */
  amendment:{open:(h:Holding)=>void;shown:(h:Holding)=>boolean;close:()=>Promise<void>};
  openCurve:(curve:string,contractId:string|null)=>void;onDenied:()=>void;
}){
  let input:PositionInput|undefined,tab:PositionTab='curve',detail:Detail|undefined,generation=0,chart:HTMLElement|undefined,tabs:ReturnType<typeof setupTabs>|undefined,actions:{amend:HTMLButtonElement;open:HTMLButtonElement;note:HTMLElement;openNote:HTMLElement}|undefined;
  // Whether the last drawing found the position's amendment open, and whether it put its form in the tab panel.
  let amending=false,formShown=false,reading:{key:string;done:Promise<unknown>}|undefined;
  const view=()=>shell.element('view');
  const key=(at:PositionInput)=>JSON.stringify([at.holding.tradeId,at.holding.revision,o.context()]);
  function status(text:string,className='side-panel-status'){const p=el('p',className,text);p.setAttribute('role','status');return p;}
  function drawHead(at:PositionInput,now:PositionSummary){
    const kicker=el('div','side-panel-kicker');
    kicker.append(el('span',`desk-pill desk-pill-${now.pill.toLowerCase()}`,now.pill),el('span','side-panel-reference',now.reference),el('span','side-panel-meta',`Revision ${now.revision} · ${now.book}`));
    const title=el('h2','side-panel-title'),dot=el('i','commodity-dot');dot.style.background=at.color;dot.title=publicText(now.commodity.name);title.id='side-panel-title';title.append(dot,el('span',undefined,now.market));
    const value=el('div','side-panel-value'),figure=el('div','side-panel-figure');figure.append(el('span','side-panel-label',now.value.label));
    if(now.value.amount!==null){
      const amount=el('p','kpi-value'),text=el('span','kpi-figure',now.value.amount.text);amount.dataset.sign=now.value.amount.sign;amount.title=publicText(`${now.value.amount.exact} ${now.value.currency}`);
      amount.append(text,' ',el('small',undefined,now.value.currency));figure.append(amount);
    }else figure.append(el('p','desk-chip','Not valued'),el('p','side-panel-reason',now.value.unavailable));
    value.append(figure);
    if(now.markMinusPrice){
      const delta=el('div','side-panel-delta'),amount=el('b',undefined,now.markMinusPrice.text);
      delta.title=publicText(`${now.markMinusPrice.exact} ${now.markMinusPrice.unit}`);delta.append(el('span','side-panel-label',now.markMinusPrice.label),amount,el('small',undefined,now.markMinusPrice.unit));value.append(delta);
    }
    const row=el('div','side-panel-actions'),amend=el('button',undefined,'Amend'),open=el('button','secondary','Open curve'),note=el('p','field-help side-panel-note'),openNote=el('p','field-help side-panel-note');
    amend.type=open.type='button';amend.id='side-panel-amend';open.id='side-panel-open-curve';note.id='side-panel-amend-note';openNote.id='side-panel-open-curve-note';note.hidden=openNote.hidden=true;
    amend.onclick=()=>{if(o.amendment.shown(at.holding))void o.amendment.close().then(sync);else o.amendment.open(at.holding);};
    open.onclick=()=>{const target=openCurveTarget(at.holding,at.row,at.shape);o.openCurve(target.curve,target.contractId);};
    row.append(amend,open);actions={amend,open,note,openNote};
    shell.element('head').replaceChildren(kicker,title,el('p','side-panel-sub',now.subtitle),value,row,note,openNote);
  }
  function drawCurve(host:HTMLElement,at:PositionInput,now:PositionSummary){
    const view=curveView(at.shape,at.holding,at.row,at.formats);
    if(view.state==='loading')host.append(status('Loading curve shape…'));
    else if(view.state==='unavailable')host.append(status(view.reason));
    else{
      const plot=el('div','chart side-panel-chart');host.append(plot);
      try{renderChart(plot,view.spec);chart=plot;}catch(error){plot.remove();host.append(status(failure(error,'The curve could not be drawn.'),'side-panel-status side-panel-error'));}
      if(view.note)host.append(el('p','field-help side-panel-note',view.note));
    }
    const facts=el('dl','side-panel-facts');
    for(const fact of now.facts){
      const item=el('div','side-panel-fact'),text=el('dd');text.append(el('b',undefined,fact.text),...(fact.unit?[' ',el('small',undefined,fact.unit)]:[]));
      item.append(el('dt',undefined,fact.label),text);facts.append(item);
    }
    host.append(facts);
  }
  // Terms and History are lists of titled sections of label and value rows, which fit the panel's width.
  function drawSections(host:HTMLElement,sections:readonly TermSection[]){
    for(const section of sections){
      const list=el('dl','side-terms-list');
      for(const [label,value] of section.rows){const row=el('div','side-term');row.append(el('dt',undefined,label),el('dd',undefined,value));list.append(row);}
      const wrap=el('section','side-panel-section');wrap.append(el('h3',undefined,section.title),list);host.append(wrap);
    }
  }
  function drawDetail(host:HTMLElement,at:PositionInput){
    if(detail?.key!==key(at))void load();
    if(!detail||detail.state==='loading')return void host.append(status('Loading…'));
    if(detail.state==='error'){
      const retry=el('button','secondary','Retry');retry.type='button';retry.id='side-panel-retry';retry.onclick=()=>void load();
      return void host.append(status(detail.message,'side-panel-status side-panel-error'),retry);
    }
    const read=detail.read.view(o.zone());
    drawSections(host,tab==='terms'?read.terms:read.history);
  }
  // The first control of the amendment's form, which takes focus when the form opens.
  function focusForm(){shell.element('form').querySelector<HTMLElement>('select:enabled,input:enabled:not([type=hidden]),textarea:enabled')?.focus({preventScroll:true});}
  function drawBody(at:PositionInput,now:PositionSummary){
    const host=view();
    if(chart){clearChart(chart);chart=undefined;}
    host.replaceChildren();
    // The amendment's form is the Terms tab's content while it is open: the other tabs keep it, and its edits, out of sight.
    const form=tab==='terms'&&amending,opened=form&&!formShown;
    formShown=form;shell.form(form);
    if(form){if(opened){shell.reveal();focusForm();}return;}
    if(tab==='curve')drawCurve(host,at,now);
    else if(tab==='risk')host.append(status(riskEmpty));
    else drawDetail(host,at);
  }
  // Keyboard focus. A control of the panel that has it when the page goes busy is disabled with the rest of the page, and the browser
  // drops focus a moment later without a word; a redraw replaces the control that has it. So the id of the control that has focus is
  // kept while the page is busy or the panel is drawn again, and focus goes back to the control of that id once it is enabled, if
  // nothing else has taken focus by then.
  const controlParts=()=>[shell.element('head'),shell.element('tabs'),shell.element('body')];
  let held:string|undefined;
  const focusLost=()=>{const active=document.activeElement;return active===null||active===document.body||active===document.documentElement;};
  // The control of the panel that has keyboard focus now.
  function focused(){
    const active=document.activeElement;
    return active instanceof HTMLElement&&active.id&&controlParts().some(part=>part.contains(active))?active.id:undefined;
  }
  function refocus(){
    if(held===undefined)return;
    const control=document.getElementById(held);
    // A control that is disabled while the page is busy is waited for; one that is gone, or focus that has moved, ends the wait.
    if(control&&controlParts().some(part=>part.contains(control))&&focusLost()){
      if((control as HTMLButtonElement).disabled)return;
      control.focus({preventScroll:true});
    }
    held=undefined;
  }
  // A position the panel cannot draw says so in place of its content: the page never keeps a half-drawn panel.
  function paint(){
    if(!input)return;
    const keep=focused()??held;
    try{const now=positionSummary(input.holding,input.row,input.formats);amending=o.amendment.shown(input.holding);drawHead(input,now);drawBody(input,now);}
    catch(error){
      actions=undefined;formShown=false;if(chart){clearChart(chart);chart=undefined;}
      shell.form(false);shell.element('head').replaceChildren();view().replaceChildren(status(failure(error,'The position could not be shown.'),'side-panel-status side-panel-error'));
    }
    sync();
    held=keep;refocus();
  }
  // Reads the trade revision of the position and draws it. Resolves to why it could not be read, or to nothing; it never rejects.
  function load():Promise<unknown>{
    const at=input!,started=++generation,loading:Detail={key:key(at),state:'loading'};
    detail=loading;
    const done=(async()=>{
      try{
        const read=await o.read(at.holding,at.formats);
        if(started!==generation)return;
        detail={key:loading.key,state:'ready',read};
      }catch(error){
        if(started!==generation)return;
        if((error as {code?:string}).code==='access_denied'){o.onDenied();return error;}
        detail={key:loading.key,state:'error',message:failure(error,'The trade could not be read.')};paint();return error;
      }
      paint();
    })();
    reading={key:loading.key,done};
    return done;
  }
  function sync(){
    if(!input||!actions)return;
    const at=input,editing=o.amendment.shown(at.holding);
    // The amendment opened or closed, by the panel's own button or by the page: Terms shows its form, or its read view again.
    if(editing!==amending){if(editing){tab='terms';tabs?.mark('terms');}paint();return;}
    const revision=detail?.state==='ready'&&detail.key===key(at)?detail.read.amend:undefined;
    const control=amendControl({accountCanAmend:o.canAmend(),revision,editing});
    if(o.busy())held=focused()??held;
    actions.amend.hidden=control.label===null;if(control.label!==null)actions.amend.textContent=control.label;
    actions.amend.disabled=o.busy();
    actions.note.hidden=control.note===null;actions.note.textContent=control.note===null?'':publicText(control.note);
    const listed=o.curveListed(at.holding.curve);
    actions.open.disabled=o.busy()||!listed;say(actions.openNote,actions.open,curveNote(listed));
    if(!o.busy())refocus();
  }
  // Shows a reason beside a button, and points the button at it for assistive technology; nothing shown and nothing pointed at for none.
  function say(note:HTMLElement,control:HTMLElement,reason:string|null){
    note.hidden=reason===null;note.textContent=reason===null?'':publicText(reason);
    if(reason===null)control.removeAttribute('aria-describedby');else control.setAttribute('aria-describedby',note.id);
  }
  return {
    /** Shows a position the shell has just opened: on the tab it names, the Curve tab unless told otherwise, with nothing read of it yet. */
    show(next:PositionInput,start:PositionTab='curve'){
      input=next;tab=start;detail=undefined;held=undefined;generation++;
      tabs=setupTabs(shell.element('tabs'),positionTabs,{label:'Position details',panel:shell.element('body'),selected:start,select:id=>{tab=id as PositionTab;paint();shell.element('body').scrollTop=0;}});
      paint();
    },
    /** Draws the same position again from a newer read of the desk. */
    update(next:PositionInput){input=next;paint();},
    /** Reads again what the panel has read of its position, in the market context the desk reads in now, and says why it cannot. */
    async reload(){
      if(!input||detail===undefined)return;
      const failed=await(detail.state==='loading'&&reading?.key===key(input)?reading.done:load());
      if(failed)throw failed;
    },
    /** Forgets the position: the panel is closed or left, or the reads it was drawn from no longer stand. */
    clear(){
      input=undefined;actions=undefined;detail=undefined;held=undefined;tabs=undefined;amending=formShown=false;generation++;
      if(chart){clearChart(chart);chart=undefined;}
      view().replaceChildren();shell.element('head').replaceChildren();shell.element('tabs').replaceChildren();shell.element('body').removeAttribute('aria-labelledby');
    },
    sync,
  };
}
