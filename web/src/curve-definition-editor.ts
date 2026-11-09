import {displayDate,displayInstant,publicText} from './market-view.js';
import {renderRegularTable} from './regular-table.js';

type Choice={field:string;code:string;label:string;supported:boolean;targetKind:string|null};
type Quote={commodity:string;currency:string;unit:string;multiplier:string};
type Profile={clock:string;timeZone:string;intervalMinutes:number};
type Target={kind:string;index:string|null;secondIndex:string|null};
type Product={commodity:string|null;location:string|null;currency:string|null;unit:string|null;multiplier:string|null};
type References={commodityLocations:{commodity:string;location:string}[];quotes:Record<string,Quote>;profiles:Record<string,Profile>;
  targets:Record<string,Target>;futuresProducts:Record<string,Product>;sources:string[];currencies:string[]};
type Catalog={choices:Choice[];maximumContracts:number;references:References};
type Row=Record<string,string>;
type Mapping={id:string;version:number;generation:number;state:'draft'|'published';definition:{code:string;name:string;contracts:Row[]|null;dates?:Row[]|null}};
type Window={mappingId:string;mappingVersion:number;count:number};
type Draft={code:string;name:string;commodity:string|null;location:string|null;quote:string|null;profile:string|null;contractKind:string|null;futuresProduct:string|null;
  contracts:Window|null;settlementTarget:string|null;priceRule:string|null;markRole:string|null;markSource:string|null;missingPrice:string|null;interpolation:string|null;
  currency:string|null;markUnit:string|null;dayCount:string|null;compounding:string|null;extrapolation:string|null};
type Saved={id:string;version:number;generation:number;state:'draft'|'published';definition:Draft;mappingGeneration:number};
type Library={definitions:Saved[];canEdit:boolean;canPublish:boolean};
type Receipt={expression:string};
type Position={position:number;label:string;start:string;end:string;lastTradingAt:string;futuresSeries:string|null;startReference:Receipt};
type Maturity={position:number;label:string;maturity:string;days:number;reference:Receipt};
type Preview={code:string;name:string;mapping:string;context:{effectiveOn:string};terms:{currency:string;unit:string;multiplier:string;timeZone:string;intervalMinutes:number;
  settlementKind:string;settlementIndex:string|null};contracts?:Position[];maturities?:Maturity[];valuationDate?:string};
type CommandReceipt={id:string;version:number;generation:number;state:'draft'|'published'};
type Api=<T>(path:string,body?:unknown)=>Promise<T>;

const comingSoon=' · Coming soon';
// Fields an interest-rate curve does not take: they are hidden, not disabled, since busy work re-enables page controls.
const CONTRACT_FIELDS=['location','product','quote','profile','target','rule','role','missing','interpolation','count'];
// An interest-rate curve's own currency, mark unit and conventions (D359), each required with no default.
const RATE_FIELDS=['currency','unit','daycount','compounding','rate-interpolation','extrapolation'];
const ROW_FIELDS={dates:[['label','Label'],['date','Date · date or reference']],
  contracts:[['label','Contract'],['start','Start · date or reference'],['end','End · date or reference'],['lastTrading','Last trading · date or reference'],['cutoffUtc','Last trading time · UTC']]} as const;

/**
 * Curve definitions in the Reference Data console (D318, D357, D364): saved curves are listed and opened here, and only here are they changed. A curve
 * names its reference terms and an exact mapping version; its contracts or tenors are that mapping's rows, edited in place and saved as
 * the mapping's draft with the curve. The server validates, resolves at the header's selected close and publishes; this view only presents.
 */
export function setupCurveDefinitionEditor(api:Api,run:(work:()=>Promise<void>)=>Promise<void>,close:()=>string,zone:()=>string,isBusy:()=>boolean,authoringUnavailable:()=>string|null=()=>null){
  const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=publicText(text);return n;};
  const element=el('div');element.className='card curve-definition-editor';
  const form=el('form'),result=el('div'),title=el('h3'),summary=el('p'),table=el('div');
  summary.className='muted';table.className='table-wrap';result.id='curve-definition-result';result.hidden=true;result.append(title,summary,table);
  const fields=new Map<string,HTMLInputElement|HTMLSelectElement>();
  let catalog:Catalog|undefined,mappings:Mapping[]=[],library:Library={definitions:[],canEdit:false,canPublish:false};
  let opened:Saved|undefined,shown:Preview|undefined,rowsEdited=false,pending:{path:string;body:string;requestId:string}|undefined,generation=0;

  function field(id:string,label:string,control:HTMLInputElement|HTMLSelectElement,help?:string){
    control.id='curve-definition-'+id;control.required=true;fields.set(id,control);
    const row=el('div'),caption=el('label',label);caption.htmlFor=control.id;row.append(caption,control);
    if(help){const note=el('p',help);note.className='field-help';row.append(note);}
    return row;
  }
  const section=(heading:string,...rows:HTMLElement[])=>{const part=el('fieldset'),legend=el('legend',heading);part.className='curve-definition-section';part.append(legend,...rows);return part;};
  const input=(attributes:Record<string,string>)=>{const n=el('input');for(const [k,v] of Object.entries(attributes))n.setAttribute(k,v);return n;};
  const value=(id:string)=>fields.get(id)!.value;
  const set=(id:string,text:string|null|undefined)=>{fields.get(id)!.value=text??'';};
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

  const list=el('div'),rows=el('tbody'),head=el('thead'),add=el('button','Add row'),newer=el('div'),stale=el('div'),status=el('p');
  list.id='curve-definition-list';list.className='muted';
  rows.id='curve-definition-rows';add.type='button';add.id='curve-definition-add';add.className='secondary';newer.id='curve-definition-newer';newer.className='profile-note';newer.hidden=true;stale.id='curve-definition-stale';stale.className='profile-note';stale.hidden=true;
  status.id='curve-definition-status';status.setAttribute('role','status');
  const rowTable=el('table');rowTable.className='contract-date-inputs';rowTable.append(head,rows);const rowScroll=el('div');rowScroll.className='regular-table-scroll';rowScroll.append(rowTable);
  form.append(
    section('Identity',field('code','Curve code',input({maxlength:'64',pattern:'[a-z][a-z0-9_.\\-]{0,63}',placeholder:'hub_a_fwd'})),field('name','Curve name',input({maxlength:'80'})),
      field('kind','Curve kind',el('select'))),
    section('What it prices',field('location','Commodity at location',el('select')),
      field('product','Futures product',el('select'),'Futures only. Each contract must match exactly one series of this product by delivery period.'),
      field('currency','Currency',el('select')),field('unit','Marks quoted in',el('select'),'Each tenor is marked as a spot zero rate in this unit.')),
    section('Quote and delivery',field('quote','Quote · currency and unit',el('select')),field('profile','Delivery profile · time zone and interval',el('select'))),
    section('Contracts or tenors',field('mapping','Date mapping · version',el('select'),'Each row resolves against the close selected above, so the curve rolls with it.'),
      newer,stale,rowScroll,add,field('count','Number of contracts',input({type:'number',min:'1',step:'1'}))),
    section('Settlement',field('target','Settles to',el('select'))),
    section('Pricing',field('rule','Price rule',el('select')),field('role','Mark role',el('select')),field('source','Mark source',el('select')),
      field('missing','If a contract has no price',el('select')),field('interpolation','Interpolation',el('select'))),
    section('Rate conventions',field('daycount','Day count',el('select')),field('compounding','Compounding',el('select')),
      field('rate-interpolation','Between tenors',el('select')),field('extrapolation','After the last tenor',el('select'),'Before the first tenor, the as-of date discounts at exactly 1.')));
  const actions=el('div'),preview=el('button','Preview'),save=el('button','Save draft'),publish=el('button','Publish');
  actions.className='schedule-actions';preview.type='submit';save.type='button';save.id='curve-definition-save';save.className='secondary';
  publish.type='button';publish.id='curve-definition-publish';publish.hidden=true;
  actions.append(preview,save,publish);form.append(actions,status);
  const intro=el('p','A curve changes only here, through a new version of its definition. Contracts and tenors come from a date mapping (Reference Data › Schedules & date mappings) and can be edited below; preview resolves them at the close selected above.');
  const fresh=el('button','New definition');fresh.type='button';fresh.id='curve-definition-new';fresh.className='secondary';
  intro.className='muted';element.append(el('h2','Curve definitions'),intro,list,fresh,form,result);

  const isRate=()=>value('kind')==='interest_rate';
  const mappingType=()=>isRate()?'dates':'contracts';
  const selectedMapping=()=>{const [id,version]=value('mapping').split('|');return mappings.find(m=>m.id===id&&m.version===Number(version));};
  function kindChanged(){
    const rate=isRate(),future=value('kind')==='future';
    for(const id of CONTRACT_FIELDS){const control=fields.get(id)!;control.closest('div')!.hidden=rate;control.required=!rate;}
    const product=fields.get('product')!;product.closest('div')!.hidden=!future;product.required=future;if(!future)product.value='';
    for(const id of RATE_FIELDS){const control=fields.get(id)!;control.closest('div')!.hidden=!rate;control.required=rate;if(!rate)control.value='';}
    for(const section of form.querySelectorAll<HTMLFieldSetElement>('.curve-definition-section'))
      section.hidden=[...section.querySelectorAll(':scope>div')].every(div=>(div as HTMLElement).hidden);
    options('mapping',mappings.filter(m=>!!m.definition.dates===rate).map(m=>({value:m.id+'|'+m.version,
      label:`${m.definition.name} (${m.definition.code}) v${m.version} · ${m.state} · ${(m.definition.dates??m.definition.contracts??[]).length} ${rate?'dates':'contracts'}`})));
    showRows(selectedMapping());
  }
  // The mapping's rows, editable in place; edited rows are saved as that mapping's draft with the curve.
  function showRows(mapping:Mapping|undefined){
    const columns=ROW_FIELDS[mappingType()],headings=el('tr');for(const [,label] of columns)headings.append(el('th',label));headings.append(el('th'));head.replaceChildren(headings);
    rows.replaceChildren();rowsEdited=false;for(const row of mapping?.definition.dates??mapping?.definition.contracts??[])addRow(row);
    add.textContent=isRate()?'Add tenor':'Add contract';add.disabled=!mapping;newerOffer();
  }
  function addRow(row?:Row){
    if(rows.children.length>=120)throw Error('A mapping has at most 120 rows.');
    const line=el('tr');
    for(const [name] of ROW_FIELDS[mappingType()]){const cell=el('td'),n=el('input');n.required=true;n.value=row?.[name]??'';n.dataset.field=name;n.setAttribute('aria-label',name);
      if(name==='cutoffUtc'){n.type='time';n.step='60';}cell.append(n);line.append(cell);}
    const cell=el('td'),remove=el('button','Remove');remove.type='button';remove.className='text-button';remove.onclick=()=>{if(!isBusy()){line.remove();edited();}};cell.append(remove);line.append(cell);rows.append(line);
  }
  const rowValues=()=>Array.from(rows.children,line=>Object.fromEntries(Array.from(line.querySelectorAll<HTMLInputElement>('input[data-field]'),n=>[n.dataset.field!,n.value])));
  function edited(){rowsEdited=true;clear();}
  add.onclick=()=>void run(async()=>{addRow();edited();});
  rows.addEventListener('input',edited);
  // A newer published version of the opened curve's mapping is offered, never adopted silently.
  function newerOffer(){
    const pinned=opened?.definition.contracts,current=selectedMapping();
    const latest=pinned&&current?.id===pinned.mappingId&&current.version===pinned.mappingVersion
      ?mappings.filter(m=>m.id===pinned.mappingId&&m.state==='published'&&m.version>pinned.mappingVersion).sort((a,b)=>b.version-a.version)[0]:undefined;
    newer.hidden=!latest;if(!latest)return;
    const use=el('button',`Use version ${latest.version}`);use.type='button';use.className='secondary';
    use.onclick=()=>{set('mapping',latest.id+'|'+latest.version);showRows(latest);newer.hidden=true;clear();};
    newer.replaceChildren(el('p',`Version ${latest.version} of ${latest.definition.code} is published. This curve uses version ${pinned!.mappingVersion}.`),use);
  }

  function render(){
    if(!catalog)return;const r=catalog.references;
    options('kind',choices('contractKind'));
    options('location',r.commodityLocations.map(p=>({value:p.commodity+'|'+p.location,label:`${p.commodity} at ${p.location}`})));
    options('product',Object.entries(r.futuresProducts).map(([code,p])=>({value:code,label:`${code} · ${p.commodity??'no commodity'} at ${p.location??'no location'}`})));
    options('currency',r.currencies.map(code=>({value:code,label:code})));options('unit',choices('rate.markUnit'));options('daycount',choices('rate.dayCount'));
    options('compounding',choices('rate.compounding'));options('rate-interpolation',choices('rate.interpolation'));options('extrapolation',choices('rate.extrapolation'));
    options('quote',Object.entries(r.quotes).map(([code,q])=>({value:code,label:`${code} · ${q.currency} per ${q.multiplier==='1'?'':q.multiplier+' '}${q.unit}`})));
    options('profile',Object.entries(r.profiles).map(([code,p])=>({value:code,label:`${code} · ${p.timeZone}, ${p.intervalMinutes} min`})));
    // Targets are grouped by the served settlement case their kind selects; a case no target kind expresses is shown, disabled, so it stays visible.
    const settlements=catalog.choices.filter(c=>c.field==='settlement');
    options('target',[...Object.entries(r.targets).map(([code,t])=>{const kind=settlements.find(c=>c.targetKind===t.kind);
      return {value:code,label:code+(t.index?` · ${t.index}${t.secondIndex?' minus '+t.secondIndex:''}`:'')+(kind?.supported===false?comingSoon:''),disabled:kind?.supported===false,group:kind?.label??t.kind};}),
      ...settlements.filter(c=>!c.targetKind).map(c=>({value:'',label:c.label+comingSoon,disabled:true,group:'Coming soon'}))]);
    fields.get('count')!.setAttribute('max',String(catalog.maximumContracts));
    options('rule',choices('priceRule'));options('role',choices('markRole'));options('source',r.sources.map(s=>({value:s,label:s})));
    options('missing',choices('missingPrice'));options('interpolation',choices('interpolation'));kindChanged();
    renderList();
  }
  function renderList(){
    const latest=library.definitions.filter(d=>!library.definitions.some(other=>other.id===d.id&&other.version>d.version));
    list.replaceChildren();if(!latest.length)list.textContent=library.canEdit?'No curve definitions saved yet.':'No curve definitions have been published yet.';
    for(const version of latest.sort((a,b)=>a.definition.code.localeCompare(b.definition.code))){
      const row=el('div'),label=el('div'),openButton=el('button','Open');row.className='shortcut-saved-row';openButton.type='button';openButton.className='secondary';
      label.append(el('strong',`${version.definition.name} · ${version.definition.code}`),el('span',`Version ${version.version} · ${version.state} · ${version.definition.contractKind==='interest_rate'?'interest rate':version.definition.contractKind}`));
      openButton.onclick=()=>{if(!isBusy())open(version);};row.append(label,openButton);list.append(row);
    }
    save.hidden=!library.canEdit||!!authoringUnavailable();publish.hidden=!library.canPublish||!!authoringUnavailable()||opened?.state!=='draft';
  }
  function open(version:Saved){
    opened=version;const d=version.definition;clear();status.textContent=authoringUnavailable()??(!library.canEdit?'Editing requires market-definition authoring permission.':'');
    set('code',d.code);set('name',d.name);set('kind',d.contractKind);(fields.get('code') as HTMLInputElement).readOnly=true;
    set('location',d.commodity&&d.location?d.commodity+'|'+d.location:'');set('product',d.futuresProduct);
    set('quote',d.quote);set('profile',d.profile);set('target',d.settlementTarget);set('rule',d.priceRule);set('role',d.markRole);set('source',d.markSource);
    set('missing',d.missingPrice);set('interpolation',d.contractKind==='interest_rate'?'':d.interpolation);set('count',d.contracts?String(d.contracts.count):'');
    set('currency',d.currency);set('unit',d.markUnit);set('daycount',d.dayCount);set('compounding',d.compounding);set('extrapolation',d.extrapolation);
    set('rate-interpolation',d.contractKind==='interest_rate'?d.interpolation:'');
    kindChanged();if(d.contracts)set('mapping',d.contracts.mappingId+'|'+d.contracts.mappingVersion);showRows(selectedMapping());staleRows();renderList();
  }
  // The opened draft pinned its mapping draft at one generation; rows changed since then (in Schedules & date mappings) are shown but adopted only on request.
  function staleRows(){
    const pinned=opened?.definition.contracts,current=selectedMapping();
    stale.hidden=!(opened&&pinned&&current?.id===pinned.mappingId&&current.version===pinned.mappingVersion&&current.generation!==opened.mappingGeneration);
    if(stale.hidden)return;
    const use=el('button','Use the changed rows');use.type='button';use.className='secondary';use.onclick=()=>{stale.hidden=true;clear();};
    stale.replaceChildren(el('p',`The rows of ${current!.definition.code} v${current!.version} changed in Schedules & date mappings after this curve was saved. Review them before saving the curve.`),use);
  }
  function startNew(){opened=undefined;stale.hidden=true;form.reset();(fields.get('code') as HTMLInputElement).readOnly=false;status.textContent='';clear();kindChanged();renderList();}
  fresh.onclick=()=>{if(!isBusy())startNew();};
  const clear=()=>{shown=undefined;result.hidden=true;};
  form.addEventListener('input',clear);form.addEventListener('change',clear);
  fields.get('kind')!.addEventListener('change',kindChanged);
  fields.get('mapping')!.addEventListener('change',()=>{showRows(selectedMapping());stale.hidden=true;});

  function draft():Draft{
    const [commodity,location]=value('location').split('|'),mapping=selectedMapping(),rate=isRate();
    const optional=(id:string)=>rate?null:value(id)||null,rated=(id:string)=>rate?value(id)||null:null;
    return {code:value('code'),name:value('name'),commodity:rate?null:commodity||null,location:rate?null:location||null,quote:optional('quote'),profile:optional('profile'),
      contractKind:value('kind')||null,futuresProduct:value('product')||null,
      contracts:mapping?{mappingId:mapping.id,mappingVersion:mapping.version,count:rate?rows.children.length:Number(value('count'))}:null,
      settlementTarget:optional('target'),priceRule:optional('rule'),markRole:optional('role'),markSource:value('source')||null,
      missingPrice:optional('missing'),interpolation:rate?value('rate-interpolation')||null:optional('interpolation'),
      currency:rated('currency'),markUnit:rated('unit'),dayCount:rated('daycount'),compounding:rated('compounding'),extrapolation:rated('extrapolation')};
  }
  // Edited rows travel with the curve as its mapping's next draft; unedited rows are the stored version itself.
  function mappingEdit(){
    const mapping=selectedMapping();if(!rowsEdited||!mapping)return null;const shown=rowValues();
    return {id:mapping.id,version:mapping.version,generation:mapping.generation,expectedState:mapping.state,code:mapping.definition.code,name:mapping.definition.name,
      contracts:mapping.definition.dates?null:shown,dates:mapping.definition.dates?shown:null};
  }
  function show(p:Preview){
    shown=p;
    if(p.maturities){
      title.textContent=publicText(`${p.name} · ${p.maturities.length} tenors from ${p.mapping}`);
      summary.textContent=publicText(`${p.terms.currency} · spot zero rates · valued ${displayDate(p.valuationDate!)} · context ${displayDate(p.context.effectiveOn)}`);
      renderRegularTable(table,'curve-definition-tenors',JSON.stringify(p),['Position','Tenor','Maturity','Days','Reference'],
        p.maturities.map(m=>[String(m.position),m.label,displayDate(m.maturity),String(m.days),m.reference.expression]),[0,3],isBusy);
    }else{
      const t=p.terms,contracts=p.contracts??[];title.textContent=publicText(`${p.name} · ${contracts.length} contracts from ${p.mapping}`);
      summary.textContent=publicText(`${t.currency} per ${t.multiplier==='1'?'':t.multiplier+' '}${t.unit} · ${t.timeZone} · settles to ${t.settlementIndex??'exchange settlement'} · context ${displayDate(p.context.effectiveOn)} · end dates excluded`);
      renderRegularTable(table,'curve-definition-contracts',JSON.stringify(p),['Position','Contract','Delivery start','Delivery end',`Last trading · ${zone()}`,'Futures series','Start reference'],
        contracts.map(c=>[String(c.position),c.label,displayDate(c.start),displayDate(c.end),displayInstant(c.lastTradingAt,zone()),c.futuresSeries??'—',c.startReference.expression]),[0],isBusy);
    }
    result.hidden=false;
  }
  form.onsubmit=event=>{event.preventDefault();void run(async()=>{clear();const started=generation;
    const read=await api<Preview>('curve-definitions/preview',{definition:draft(),snapshotId:close(),mapping:mappingEdit()});if(started===generation)show(read);});};
  // A command keeps its request identity until it succeeds, so retrying the same change cannot apply it twice.
  async function command(path:string,body:object){
    const text=JSON.stringify(body);if(pending?.path!==path||pending.body!==text)pending={path,body:text,requestId:crypto.randomUUID()};
    const receipt=await api<CommandReceipt>(path,{...body,requestId:pending.requestId});pending=undefined;
    await load();const saved=library.definitions.find(d=>d.id===receipt.id&&d.version===receipt.version);if(saved)open(saved);
    status.textContent=`${receipt.state==='published'?'Published':'Saved draft'} · version ${receipt.version}.`;
  }
  save.onclick=()=>{if(isBusy()||authoringUnavailable()||!library.canEdit||!form.reportValidity())return;void run(async()=>{
    if(!stale.hidden)throw Error('The mapping rows changed after this curve was saved. Review them, then choose to use them before saving.');
    await command('curve-definitions/save',{definition:draft(),snapshotId:close(),mapping:mappingEdit(),
    id:opened?.id??null,version:opened?.version??null,generation:opened?.generation??null,expectedState:opened?.state??null});});};
  publish.onclick=()=>{if(isBusy()||authoringUnavailable()||!library.canPublish||!opened)return;const target=opened;void run(()=>command('curve-definitions/publish',{id:target.id,version:target.version,generation:target.generation}));};
  async function load(){
    const started=++generation;
    const [read,dates,saved]=await Promise.all([api<Catalog>('curve-definitions/catalog'),api<{contractDefinitions:Mapping[]}>('date-schedules'),api<Library>('curve-definitions')]);
    if(started!==generation)return;
    catalog=read;library=saved;
    mappings=dates.contractDefinitions.slice().sort((a,b)=>a.definition.code.localeCompare(b.definition.code)||b.version-a.version);
    const kept=opened&&library.definitions.find(d=>d.id===opened!.id&&d.version===opened!.version);render();if(kept)open(kept);else if(opened)startNew();
  }

  return {element,load,
    // A rendered curve's link to its definition (D359): opens the definition, at the version that curve pins.
    async openDefinition(id:string,version:number,reason:string|null=null){await load();const saved=library.definitions.find(d=>d.id===id&&d.version===version);if(!saved)throw Error('This curve\u2019s definition is not available to you.');open(saved);if(reason)status.textContent=reason;},
    // Reads the catalog whenever the editor opens, so a mapping published meanwhile is listed.
    shown(){void run(load);},
    // A changed close or market context makes the shown window and the pinned choices stale; the next opening reads them again.
    contextChanged(){generation++;clear();},
    reset(){generation++;catalog=undefined;mappings=[];library={definitions:[],canEdit:false,canPublish:false};opened=undefined;pending=undefined;form.reset();clear();},
    refresh(){if(shown)show(shown);}};
}
