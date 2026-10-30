// The Add widget control of the Dashboard: configures a Latest levels, Price chart or Positions widget only from the curves the navigation of the
// context offers and, for a chart, the contracts the close states for the chosen curve, and for Positions the readable books and the catalog's price units. It builds the widget's
// content (saving a new risk definition first); saving the layout is the page's.
import {readCurveShapes} from './curve-shapes.js';
import {contractChoices,readableCurves,type DashboardMarket} from './dashboard-levels.js';
import {curveName} from './market-view.js';
import type {setupPositions} from './dashboard-positions.js';
import type {WidgetContent} from './dashboard-layout.js';

// The most curves one Latest levels widget names (the layout's limit).
const maxCurves=12;
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
const el=<K extends keyof HTMLElementTagNameMap>(tag:K,className='',text='')=>{const node=document.createElement(tag);node.className=className;node.textContent=text;return node;};

export function setupAddWidget(host:HTMLElement,options:{market:()=>DashboardMarket;api:Api;add:(content:WidgetContent)=>Promise<void>;positions:ReturnType<ReturnType<typeof setupPositions>['configurator']>}){
  const details=el('details','dashboard-add'),form=el('form','dashboard-add-form'),status=el('p','field-help');status.setAttribute('role','status');
  const kind=el('select','dashboard-edit'),curves=el('fieldset','dashboard-add-curves'),curve=el('select','dashboard-edit'),contract=el('select','dashboard-edit');
  const submit=el('button','dashboard-edit','Add');submit.type='submit';
  kind.setAttribute('aria-label','Widget kind');curve.setAttribute('aria-label','Curve');contract.setAttribute('aria-label','Contract');
  kind.add(new Option('Latest levels','latest_levels'));kind.add(new Option('Price chart','price_chart'));kind.add(new Option('Positions by month','positions'));
  curves.append(el('legend','',`Curves (1 to ${maxCurves})`));
  const label=(text:string,control:HTMLElement)=>{const row=el('label','',text);row.append(control);return row;};
  const chartFields=[label('Curve',curve),label('Contract',contract)];
  form.append(label('Widget',kind),curves,...chartFields,options.positions.element,submit,status);
  details.append(el('summary','','Add widget'),form);host.replaceChildren(details);

  let reading=0;
  const levels=()=>kind.value==='latest_levels',chart=()=>kind.value==='price_chart';
  const checked=()=>[...curves.querySelectorAll<HTMLInputElement>('input:checked')];
  function showKind(){curves.hidden=!levels();for(const field of chartFields)field.hidden=!chart();const positions=kind.value==='positions';options.positions.element.hidden=!positions;if(positions)void options.positions.refresh();}
  async function loadContracts(){
    const current=++reading;contract.replaceChildren();
    if(!curve.value)return;
    status.textContent='Reading the curve’s contracts…';
    try{
      const market=options.market(),shape=(await readCurveShapes(options.api,{snapshotId:market.snapshotId,context:market.navigation.context??undefined},[curve.value])).get(curve.value);
      if(current!==reading)return;
      if(shape===undefined||typeof shape==='string'){status.textContent=shape??'The curve read did not return this curve.';return;}
      const choices=contractChoices(shape);
      for(const choice of choices)contract.add(new Option(choice.label,choice.contractId));
      status.textContent=choices.length?'':'This close states no contracts for the curve.';
    }catch(error){if(current===reading)status.textContent=error instanceof Error?error.message:'The contracts could not be read.';}
  }
  /** Offers the curves of the navigation now on screen; called when the page loads or the context refreshes. */
  function refresh(){
    try{
      const offered=readableCurves(options.market().navigation),kept=new Set(checked().map(c=>c.value)),chosen=curve.value;
      curves.replaceChildren(curves.firstElementChild!,...offered.map(c=>{
        const box=el('input','dashboard-edit');box.type='checkbox';box.value=c.code;box.checked=kept.has(c.code);
        const row=el('label','dashboard-add-curve');row.append(box,document.createTextNode(` ${curveName(c.code)}`));return row;
      }));
      curve.replaceChildren(...offered.map(c=>new Option(curveName(c.code),c.code,false,c.code===chosen)));
      status.textContent='';void loadContracts();if(kind.value==='positions')void options.positions.refresh();
    }catch(error){status.textContent=error instanceof Error?error.message:'The curves could not be listed.';}
  }
  kind.onchange=showKind;curve.onchange=()=>void loadContracts();
  form.onsubmit=event=>{
    event.preventDefault();
    if(kind.value==='positions'){
      void options.positions.settle().then(options.add).then(()=>{details.open=false;status.textContent='';},error=>{status.textContent=error instanceof Error?error.message:'The widget could not be added.';});
      return;
    }
    const chosen=checked().map(c=>c.value);
    if(levels()){
      if(!chosen.length||chosen.length>maxCurves){status.textContent=`Choose 1 to ${maxCurves} curves.`;return;}
      void options.add({kind:'latest_levels',config:{curves:chosen}});
    }else{
      if(!curve.value||!contract.value){status.textContent='Choose a curve and one of its contracts.';return;}
      void options.add({kind:'price_chart',config:{curve:curve.value,contractId:contract.value}});
    }
    details.open=false;
  };
  showKind();
  return {refresh};
}
