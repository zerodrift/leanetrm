import {displayDate,displayInstant,publicText} from './market-view.js';
import {renderRegularTable} from './regular-table.js';

type Choice={field:string;code:string;label:string;supported:boolean;targetKind:string|null};
type Quote={commodity:string;currency:string;unit:string;multiplier:string};
type Profile={clock:string;timeZone:string;intervalMinutes:number};
type Target={kind:string;index:string|null;secondIndex:string|null};
type Product={commodity:string|null;location:string|null;currency:string|null;unit:string|null;multiplier:string|null};
type References={commodityLocations:{commodity:string;location:string}[];quotes:Record<string,Quote>;profiles:Record<string,Profile>;
  targets:Record<string,Target>;futuresProducts:Record<string,Product>;sources:string[]};
type Catalog={choices:Choice[];maximumContracts:number;references:References};
type Mapping={id:string;version:number;state:string;definition:{code:string;name:string;contracts:unknown[]}};
type Receipt={expression:string};
type Position={position:number;label:string;start:string;end:string;lastTradingAt:string;futuresSeries:string|null;startReference:Receipt;endReference:Receipt;lastTradingReference:Receipt};
type Preview={code:string;name:string;mapping:string;context:{effectiveOn:string};terms:{currency:string;unit:string;multiplier:string;timeZone:string;intervalMinutes:number;
  settlementKind:string;settlementIndex:string|null};contracts:Position[]};
type Api=<T>(path:string,body?:unknown)=>Promise<T>;

const comingSoon=' · Coming soon';

/**
 * Define a curve on Curves & prices (D318): a curve names its reference terms, an exact published date mapping and how many of its
 * positions it carries. The server validates the definition and resolves its rolling contract window at the header's selected close;
 * this view only presents.
 */
export function setupCurveDefinitionEditor(api:Api,run:(work:()=>Promise<void>)=>Promise<void>,close:()=>string,zone:()=>string,isBusy:()=>boolean,leave:()=>void){
  const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=publicText(text);return n;};
  const element=el('div');element.className='card curve-definition-editor';
  const form=el('form'),result=el('div'),title=el('h3'),summary=el('p'),table=el('div');
  summary.className='muted';table.className='table-wrap';result.id='curve-definition-result';result.hidden=true;result.append(title,summary,table);
  const fields=new Map<string,HTMLInputElement|HTMLSelectElement>();
  let catalog:Catalog|undefined,mappings:Mapping[]=[],shown:Preview|undefined,generation=0;

  function field(id:string,label:string,control:HTMLInputElement|HTMLSelectElement,help?:string){
    control.id='curve-definition-'+id;control.required=true;fields.set(id,control);
    const row=el('div'),caption=el('label',label);caption.htmlFor=control.id;row.append(caption,control);
    if(help){const note=el('p',help);note.className='field-help';row.append(note);}
    return row;
  }
  const section=(heading:string,...rows:HTMLElement[])=>{const part=el('fieldset'),legend=el('legend',heading);part.className='curve-definition-section';part.append(legend,...rows);return part;};
  const input=(attributes:Record<string,string>)=>{const n=el('input');for(const [k,v] of Object.entries(attributes))n.setAttribute(k,v);return n;};
  const value=(id:string)=>fields.get(id)!.value;
  function options(id:string,items:{value:string;label:string;disabled?:boolean;group?:string}[]){
    const control=fields.get(id) as HTMLSelectElement,previous=control.value,groups=new Map<string,HTMLOptGroupElement>();
    control.replaceChildren(Object.assign(el('option','Choose…'),{value:''}));
    for(const item of items){
      const option=Object.assign(el('option',item.label),{value:item.value,disabled:!!item.disabled});
      if(!item.group){control.append(option);continue;}
      let group=groups.get(item.group);if(!group){group=Object.assign(el('optgroup'),{label:item.group});groups.set(item.group,group);control.append(group);}
      group.append(option);
    }
    control.value=items.some(i=>i.value===previous&&!i.disabled)?previous:'';
  }
  const choices=(name:string)=>(catalog?.choices??[]).filter(c=>c.field===name).map(c=>({value:c.code,label:c.label+(c.supported?'':comingSoon),disabled:!c.supported}));

  form.append(
    section('Identity',field('code','Curve code',input({maxlength:'64',pattern:'[a-z][a-z0-9_.\\-]{0,63}',placeholder:'hub_a_fwd'})),field('name','Curve name',input({maxlength:'80'}))),
    section('What it prices',field('location','Commodity at location',el('select')),field('kind','Contract kind',el('select')),
      field('product','Futures product',el('select'),'Futures only. Each contract must match exactly one series of this product by delivery period.')),
    section('Quote and delivery',field('quote','Quote · currency and unit',el('select')),field('profile','Delivery profile · time zone and interval',el('select'))),
    section('Contracts',field('mapping','Date mapping · published version',el('select'),'Contracts roll with the mapping: each position resolves against the context date.'),
      field('count','Number of contracts',input({type:'number',min:'1',step:'1'}))),
    section('Settlement',field('target','Settles to',el('select'))),
    section('Pricing',field('rule','Price rule',el('select')),field('role','Mark role',el('select')),field('source','Mark source',el('select')),
      field('missing','If a contract has no price',el('select')),field('interpolation','Interpolation',el('select'))));
  const actions=el('div'),preview=el('button','Preview contracts'),save=el('button','Save draft'+comingSoon);
  actions.className='schedule-actions';preview.type='submit';save.type='button';save.id='curve-definition-save';save.className='secondary';save.disabled=true;save.dataset.ownControls='';
  actions.append(preview,save);form.append(actions);
  const intro=el('p','Define a curve from a published date mapping (Settings › Schedules & date mappings). Preview resolves its contracts at the close selected above; saving and publishing curve definitions are coming soon.');
  const back=el('button','‹ All markets');back.type='button';back.className='text-button';back.id='curve-definition-back';back.onclick=leave;
  intro.className='muted';element.append(back,el('h2','Define a curve'),intro,form,result);

  // Only a futures curve names a futures product; the field is hidden, not disabled, since busy work re-enables page controls.
  const kindChanged=()=>{const future=value('kind')==='future',product=fields.get('product')!;product.parentElement!.hidden=!future;product.required=future;if(!future)product.value='';};
  function render(){
    if(!catalog)return;const r=catalog.references;
    options('location',r.commodityLocations.map(p=>({value:p.commodity+'|'+p.location,label:`${p.commodity} at ${p.location}`})));
    options('kind',choices('contractKind'));
    options('product',Object.entries(r.futuresProducts).map(([code,p])=>({value:code,label:`${code} · ${p.commodity??'no commodity'} at ${p.location??'no location'}`})));
    options('quote',Object.entries(r.quotes).map(([code,q])=>({value:code,label:`${code} · ${q.currency} per ${q.multiplier==='1'?'':q.multiplier+' '}${q.unit}`})));
    options('profile',Object.entries(r.profiles).map(([code,p])=>({value:code,label:`${code} · ${p.timeZone}, ${p.intervalMinutes} min`})));
    options('mapping',mappings.map(m=>({value:m.id+'|'+m.version,label:`${m.definition.name} (${m.definition.code}) v${m.version} · ${m.definition.contracts.length} contracts`})));
    // Targets are grouped by the served settlement case their kind selects; a case no target kind expresses is shown, disabled, so it stays visible.
    const settlements=catalog.choices.filter(c=>c.field==='settlement');
    options('target',[...Object.entries(r.targets).map(([code,t])=>{const kind=settlements.find(c=>c.targetKind===t.kind);
      return {value:code,label:code+(t.index?` · ${t.index}${t.secondIndex?' minus '+t.secondIndex:''}`:'')+(kind?.supported===false?comingSoon:''),disabled:kind?.supported===false,group:kind?.label??t.kind};}),
      ...settlements.filter(c=>!c.targetKind).map(c=>({value:'',label:c.label+comingSoon,disabled:true,group:'Coming soon'}))]);
    fields.get('count')!.setAttribute('max',String(catalog.maximumContracts));
    options('rule',choices('priceRule'));options('role',choices('markRole'));options('source',r.sources.map(s=>({value:s,label:s})));
    options('missing',choices('missingPrice'));options('interpolation',choices('interpolation'));kindChanged();
  }
  const clear=()=>{shown=undefined;result.hidden=true;};
  form.addEventListener('input',clear);form.addEventListener('change',clear);fields.get('kind')!.addEventListener('change',kindChanged);

  function draft(){
    const [commodity,location]=value('location').split('|'),[mappingId,mappingVersion]=value('mapping').split('|');
    return {code:value('code'),name:value('name'),commodity:commodity||null,location:location||null,quote:value('quote')||null,profile:value('profile')||null,
      contractKind:value('kind')||null,futuresProduct:value('product')||null,
      contracts:mappingId?{mappingId,mappingVersion:Number(mappingVersion),count:Number(value('count'))}:null,
      settlementTarget:value('target')||null,priceRule:value('rule')||null,markRole:value('role')||null,markSource:value('source')||null,
      missingPrice:value('missing')||null,interpolation:value('interpolation')||null};
  }
  function show(p:Preview){
    shown=p;
    const t=p.terms;title.textContent=publicText(`${p.name} · ${p.contracts.length} contracts from ${p.mapping}`);
    summary.textContent=publicText(`${t.currency} per ${t.multiplier==='1'?'':t.multiplier+' '}${t.unit} · ${t.timeZone} · settles to ${t.settlementIndex??'exchange settlement'} · context ${displayDate(p.context.effectiveOn)} · end dates excluded`);
    renderRegularTable(table,'curve-definition-contracts',JSON.stringify(p),['Position','Contract','Delivery start','Delivery end',`Last trading · ${zone()}`,'Futures series','Start reference'],
      p.contracts.map(c=>[String(c.position),c.label,displayDate(c.start),displayDate(c.end),displayInstant(c.lastTradingAt,zone()),c.futuresSeries??'—',c.startReference.expression]),[0],isBusy);
    result.hidden=false;
  }
  form.onsubmit=event=>{event.preventDefault();void run(async()=>{clear();const started=generation;const read=await api<Preview>('curve-definitions/preview',{definition:draft(),snapshotId:close()});if(started===generation)show(read);});};
  async function load(){
    const started=++generation;
    const [read,library]=await Promise.all([api<Catalog>('curve-definitions/catalog'),api<{contractDefinitions:Mapping[]}>('date-schedules')]);
    if(started!==generation)return;
    catalog=read;mappings=library.contractDefinitions.filter(m=>m.state==='published');render();
  }

  return {element,load,
    // Reads the catalog whenever the editor opens, so a mapping published meanwhile is listed.
    shown(){void run(load);},
    // A changed close or market context makes the shown window and the pinned choices stale; the next opening reads them again.
    contextChanged(){generation++;clear();},
    reset(){generation++;catalog=undefined;mappings=[];form.reset();clear();},
    refresh(){if(shown)show(shown);}};
}
