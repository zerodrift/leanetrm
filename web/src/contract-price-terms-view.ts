import {authoringLabel,publicationIndicesFor} from './price-authoring-model.js';
import {authoringSession,button,choices,element,field,group,help,input,resultTable,rules,select,type PriceAuthoringOptions} from './price-authoring-ui.js';

type Terms={settlement:string;publication?:{id:string;version:number}|null;index?:{code:string}|null;observationRule:string;pendingPolicy:string;projectionPolicy?:string|null};
type Contract={id:string;label:string;deliveryFrom:string;deliveryTo:string;expiry:string;kind:string};
type Source={nodeId:string;curveCode:string;commodity:string;quote:string;contracts:Contract[];terms?:{default:Terms;replacements:{contract:{id:string};terms:Terms}[]}|null;mapping?:string;mappings?:{targetContractId:string;sourceContractId:string}[]};
type Curve={id:string;version:number;generation:number;state:string;code:string;name:string;sources:Source[]};
type Publication={id:string;version:number;name:string;implementation:string;indices:{code:string;commodity:string;quote:string;symbol:string;statistic:string}[]};
type Catalog={snapshotId:string;canWrite:boolean;curves:Curve[];publications:Publication[];mappingRules:string[];settlementSources:string[];observationRules:string[];pendingPolicies:string[]};
export type ContractTermsSelection={curveId:string;version:number;nodeId:string;contractId?:string};

export function setupContractPriceTerms(host:HTMLElement,o:PriceAuthoringOptions){
 let catalog:Catalog|undefined,selected:Curve|undefined,source:Source|undefined,shownScope='',shownContract='';
 const session=authoringSession(host,o,'contract-price-terms/save');
 session.validateReceipt((value,command)=>{
  const record=(v:unknown):Record<string,unknown>|undefined=>v!==null&&typeof v==='object'&&!Array.isArray(v)?v as Record<string,unknown>:undefined;
  const receipt=record(value),request=command.body,requested=record(request.terms),definition=record(receipt?.definition);
  if(!receipt||!requested||receipt.id!==request.curveId||receipt.version!==request.version||!Number.isInteger(receipt.generation)||
   !Number.isInteger(request.generation)||(receipt.generation as number)<(request.generation as number)||(receipt.generation as number)>(request.generation as number)+1||!Array.isArray(definition?.nodes))return false;
  const selected=definition.nodes.map(record).find(n=>n?.id===requested.nodeId),terms=record(selected?.contractTerms);
  if(!terms||!Array.isArray(terms.replacements)||!Array.isArray(terms.mappings)||!terms.mappings.length)return false;
  const pairs=terms.mappings.map(m=>({target:record(record(m)?.target)?.id,source:record(record(m)?.source)?.id}));
  if(pairs.some(p=>typeof p.target!=='string'||typeof p.source!=='string')||new Set(pairs.map(p=>p.target)).size!==pairs.length)return false;
  if(requested.mapping==='explicit_contracts_v1'){
   if(!Array.isArray(requested.mappings)||!requested.mappings.length)return false;
   const wanted=requested.mappings.map(record);
   if(wanted.some(p=>typeof p?.targetContractId!=='string'||typeof p?.sourceContractId!=='string')||
    new Set(wanted.map(p=>p?.targetContractId)).size!==wanted.length||
    (requested.scope==='curve'&&pairs.length!==wanted.length)||
    !wanted.every(p=>pairs.some(m=>m.target===p?.targetContractId&&m.source===p?.sourceContractId)))return false;
  }else if(requested.mapping==='same_delivery_period_v1'){
   if(requested.scope==='contract'){
    if(!pairs.some(p=>p.target===requested.contractId&&p.source===requested.contractId))return false;
   }else{
    const exceptions=new Set(terms.replacements.map(r=>record(record(r)?.contract)?.id));
    if(pairs.some(p=>p.target!==p.source&&!exceptions.has(p.target)))return false;
   }
  }
  const applied=record(requested.scope==='contract'?terms.replacements.map(record).find(r=>record(r?.contract)?.id===requested.contractId)?.terms:terms.default);
  if(!applied)return false;
  return ['settlement','observationRule','pendingPolicy'].every(k=>applied[k]===requested[k])&&(applied.projectionPolicy??null)===(requested.projectionPolicy??null)&&
   (record(applied.publication)?.id??null)===(requested.publicationId??null)&&(record(applied.publication)?.version??null)===(requested.publicationVersion??null)&&
   (record(applied.index)?.code??null)===(requested.index??null);
 });
 const head=element('div');head.className='card-heading';const title=element('div');title.append(element('h2','Contract pricing'),help('Choose the inputs for a saved curve, map its contracts, and define where each final price comes from.'));
 const reload=button('Reload',()=>void change(async()=>load()),'contract-terms-reload');head.append(title,reload);host.append(head);
 const status=element('p');status.id='contract-terms-status';status.setAttribute('role','status');host.append(status);
 const curve=select('contract-terms-curve'),node=select('contract-terms-source');host.append(group(field('Saved curve version',curve),field('Input curve',node)));
 const form=element('form');form.id='contract-terms-form';const fields=element('fieldset');fields.className='price-authoring-form';form.append(fields);host.append(form);
 const scope=select('contract-terms-scope');choices(scope,[{value:'curve',label:'All contracts · default rule'},{value:'contract',label:'One contract · exception'}]);
 const contract=select('contract-terms-contract');const contractField=field('Exact contract',contract);
 fields.append(element('h3','Apply this rule to'),group(field('Scope',scope),contractField));
 const mapping=select('contract-terms-mapping');const maps=element('div');maps.id='contract-terms-mappings';
 const addMap=button('Add contract mapping',()=>{mapRow();session.mark();resolved.replaceChildren();sync();},'contract-terms-add-map');
 fields.append(element('h3','Date mapping'),field('Mapping convention',mapping),help('Mappings retain exact contracts. The server checks delivery coverage and which operations support the selected periods.'),maps,addMap);
 const settlement=select('contract-terms-settlement'),publication=select('contract-terms-publication'),index=select('contract-terms-index');
 const publicationFields=group(field('Publication · exact version',publication),field('Index · commodity and quote matched',index));
 const projection=select('contract-terms-projection');choices(projection,[{value:'forward_curve_no_basis_v1',label:'Use this forward curve for a different index · no basis adjustment'}],'',"Use the curve's target index");
 fields.append(element('h3','Final price'),field('Settlement source',settlement),publicationFields,field('Before the index is fixed',projection));
 const observation=select('contract-terms-observation'),pending=select('contract-terms-pending');
 fields.append(group(field('Assessment rule',observation),field('When the final price is missing',pending)),help('Expiry and publication are separate events. A final index fixing replaces the projected price only when that exact final observation is available in the selected context.'));
 const preview=button('Preview resolved terms',()=>void o.act(previewTerms),'contract-terms-preview'),save=element('button','Save pricing terms');save.type='submit';save.id='contract-terms-save';
 fields.append(group(preview,save));
 const resolved=element('div');resolved.id='contract-terms-preview-result';resolved.setAttribute('role','status');host.append(resolved);
 const current=element('div');current.id='contract-terms-current';host.append(element('h3','Saved rules'),current);
 function changed(event:Event){if(event.target===scope||event.target===contract)return;session.mark();resolved.replaceChildren();}
 form.addEventListener('input',changed);form.addEventListener('change',changed);
 form.onsubmit=event=>{event.preventDefault();void o.act(saveTerms);};
 curve.onchange=()=>void change(async()=>{chooseCurve(curve.value);});node.onchange=()=>void change(async()=>{chooseSource(node.value);});
 async function changeScope(){
  const nextScope=scope.value,nextContract=contract.value;scope.value=shownScope;contract.value=shownContract;
  if(o.busy()||!await session.leave())return;
  chooseSource(source?.nodeId??'');scope.value=shownScope=nextScope;contract.value=shownContract=nextContract;fillTerms();renderMappings();sync();
 }
 scope.onchange=()=>void changeScope();contract.onchange=()=>void changeScope();mapping.onchange=()=>{if(scope.value==='contract')renderMappings();sync();};
 settlement.onchange=()=>{publication.value='';index.replaceChildren();projection.value='';sync();};
 publication.onchange=()=>{fillIndices();sync();};
 function key(c:Curve){return `${c.id}:${c.version}`;}
 function allowed(){return !!catalog?.canWrite&&!o.historical()&&selected?.state==='draft';}
 async function change(work:()=>Promise<void>){if(o.busy())return;if(!await session.leave()){if(selected)curve.value=key(selected);if(source)node.value=source.nodeId;return;}await o.act(work);}
 function fillIndices(value=''){
  const p=catalog?.publications.find(p=>`${p.id}:${p.version}`===publication.value);
  choices(index,p&&source?publicationIndicesFor(p,source.commodity,source.quote).map(i=>({value:i.code,label:`${i.symbol} · ${i.statistic} (${i.code})`})):[],value,'Choose the exact index…');
 }
 function fillTerms(){
  const terms=scope.value==='contract'?source?.terms?.replacements.find(r=>r.contract.id===contract.value)?.terms:source?.terms?.default;
  settlement.value=terms?.settlement??'';publication.value=terms?.publication?`${terms.publication.id}:${terms.publication.version}`:'';fillIndices(terms?.index?.code);
  observation.value=terms?.observationRule??'';pending.value=terms?.pendingPolicy??'';projection.value=terms?.projectionPolicy??'';
 }
 function mapRow(value?:{targetContractId:string;sourceContractId:string}){
  const row=element('div');row.className='price-authoring-map';const target=select(''),from=select('');target.dataset.mapping='target';from.dataset.mapping='source';
  const list=source?.contracts.map(c=>({value:c.id,label:`${c.label} · ${c.deliveryFrom} → ${c.deliveryTo}`}))??[];
  choices(target,list,value?.targetContractId,'Choose output contract…');choices(from,list,value?.sourceContractId,'Choose source contract…');
  target.disabled=scope.value==='contract';
  const remove=button('Remove',()=>{row.remove();session.mark();resolved.replaceChildren();});row.append(field('Output delivery',target),element('span','←'),field('Price from',from),remove);maps.append(row);
  remove.hidden=scope.value==='contract';
 }
 function renderMappings(){maps.replaceChildren();if(scope.value==='contract'){if(contract.value&&mapping.value==='explicit_contracts_v1')mapRow(source?.mappings?.find(m=>m.targetContractId===contract.value)??{targetContractId:contract.value,sourceContractId:''});}else source?.mappings?.forEach(mapRow);}
 function chooseSource(id:string){
  source=selected?.sources.find(s=>s.nodeId===id);node.value=source?.nodeId??'';session.clean();resolved.replaceChildren();scope.value=shownScope=shownContract='';
  choices(contract,source?.contracts.map(c=>({value:c.id,label:`${c.label} · ${c.deliveryFrom} → ${c.deliveryTo}`}))??[]);
  rules(mapping,catalog?.mappingRules??[],source?.mapping??'');renderMappings();
  rules(settlement,catalog?.settlementSources??[]);rules(observation,catalog?.observationRules??[]);rules(pending,catalog?.pendingPolicies??[]);
  choices(publication,catalog?.publications.filter(p=>source&&publicationIndicesFor(p,source.commodity,source.quote).length>0).map(p=>({value:`${p.id}:${p.version}`,label:`${p.name} · v${p.version}${p.implementation.includes('placeholder')?' · unavailable':''}`}))??[]);
  fillTerms();renderCurrent();sync();
 }
 function chooseCurve(value:string){selected=catalog?.curves.find(c=>key(c)===value);curve.value=selected?key(selected):'';choices(node,selected?.sources.map(s=>({value:s.nodeId,label:`${s.curveCode} · ${s.nodeId} · ${s.commodity}`}))??[]);chooseSource('');}
 function renderCurrent(){
  const rows:(string|HTMLElement)[][]=[];
  const display=(label:string,t:Terms)=>rows.push([label,authoringLabel(t.settlement),t.index?.code??'—',authoringLabel(t.pendingPolicy)]);
  if(source?.terms){display('All contracts',source.terms.default);source.terms.replacements.forEach(r=>display(source!.contracts.find(c=>c.id===r.contract.id)?.label??r.contract.id,r.terms));}
  if(rows.length)resultTable(current,['Scope','Final price','Index','Missing final'],rows);else current.replaceChildren(help('No pricing terms have been saved for this input. Its existing pricing behavior applies.'));
 }
 function body(){
  if(!selected||!source||!scope.value||scope.value==='contract'&&!contract.value||!mapping.value||!settlement.value||!observation.value||!pending.value)throw Error('Select the curve, scope, mapping convention, settlement source and assessment rules.');
  const p=catalog?.publications.find(p=>`${p.id}:${p.version}`===publication.value);
  if(settlement.value==='published_index_v1'&&(!p||!index.value))throw Error('Choose the exact publication version and compatible index.');
  let mappings=mapping.value==='explicit_contracts_v1'?Array.from(maps.children).map(row=>({targetContractId:row.querySelector<HTMLSelectElement>('[data-mapping="target"]')!.value,sourceContractId:row.querySelector<HTMLSelectElement>('[data-mapping="source"]')!.value})):[];
  if(mappings.some(m=>!m.targetContractId||!m.sourceContractId))throw Error('Choose both contracts for every mapping.');
  return {snapshotId:o.snapshot(),curveId:selected.id,version:selected.version,terms:{nodeId:source.nodeId,scope:scope.value,contractId:scope.value==='contract'?contract.value:null,mapping:mapping.value,mappings,
   settlement:settlement.value,publicationId:settlement.value==='published_index_v1'?p!.id:null,publicationVersion:settlement.value==='published_index_v1'?p!.version:null,index:settlement.value==='published_index_v1'?index.value:null,
   observationRule:observation.value,pendingPolicy:pending.value,projectionPolicy:settlement.value==='published_index_v1'?(projection.value||null):null}};
 }
 async function previewTerms(){
  const request=body(),valid=session.ticket();const response=await o.api<{mapping?:string;mappings?:{targetFrom:string;targetTo:string;sourceFrom:string;sourceTo:string;expiry:string;fixingAt?:string;expectedPublicationAt?:string;settlement:string;index?:string}[];preview?:{columns:string[];rows:(string|null)[][]};message?:string}>('contract-price-terms/preview',request);if(!valid())return;
  resolved.replaceChildren(element('h3','Resolved terms'));
  if(response.mapping)resolved.append(help(`Resolved mapping · ${authoringLabel(response.mapping)}`));
  if(response.mappings){const table=element('div');resultTable(table,['Output delivery','Source delivery','Contract expiry','Fixing boundary','Expected publication','Final price'],response.mappings.map(m=>[`${m.targetFrom} → ${m.targetTo}`,`${m.sourceFrom} → ${m.sourceTo}`,m.expiry,m.fixingAt??'—',m.expectedPublicationAt??'No occurrence linked',m.index??authoringLabel(m.settlement)]));resolved.append(table);}
  if(response.preview?.columns){const table=element('div');resultTable(table,response.preview.columns,response.preview.rows);resolved.append(table);}
  if(!response.mappings&&!response.preview)resolved.append(help(response.message??'The server resolved these pricing terms in the selected context.'));
 }
 async function saveTerms(){if(!allowed())throw Error('Select an editable draft in working data.');const request=body();await session.save({...request,generation:selected!.generation},`${selected!.name} · ${source!.curveCode} pricing terms`);}
 async function load(selection?:ContractTermsSelection){
  const valid=session.ticket(),response=await o.api<Catalog>(`contract-price-terms?snapshotId=${encodeURIComponent(o.snapshot())}`);if(!valid())return;
  const previous=selection??(selected?{curveId:selected.id,version:selected.version,nodeId:source?.nodeId??'',...(scope.value==='contract'&&contract.value?{contractId:contract.value}:{})}:undefined);
  catalog=response;choices(curve,catalog.curves.map(c=>({value:key(c),label:`${c.name} · v${c.version} · ${c.state}`})));
  chooseCurve(previous?`${previous.curveId}:${previous.version}`:'');if(previous&&selected){chooseSource(previous.nodeId);scope.value=shownScope=previous.contractId?'contract':'curve';contract.value=shownContract=previous.contractId??'';fillTerms();renderMappings();}
  status.textContent=selected?`${selected.code} · v${selected.version} · ${selected.state}`:catalog.curves.length?'Choose a saved curve and its input to configure pricing.':'Save a curve with a native input in the Curve builder to configure contract pricing.';sync();
 }
 function sync(){
  fields.disabled=o.busy()||session.blocked()||!source;curve.disabled=o.busy()||session.blocked();node.disabled=o.busy()||session.blocked()||!selected;reload.disabled=o.busy();
  const exception=Array.from(scope.options).find(option=>option.value==='contract')!;exception.disabled=!source?.terms;exception.textContent=source?.terms?'One contract · exception':'One contract · save a default rule first';
  save.disabled=o.busy()||session.blocked()||!source||!allowed();preview.disabled=o.busy()||!source;contractField.hidden=scope.value!=='contract';maps.hidden=mapping.value!=='explicit_contracts_v1';addMap.hidden=maps.hidden||scope.value==='contract';publicationFields.hidden=settlement.value!=='published_index_v1';projection.closest('label')!.hidden=settlement.value!=='published_index_v1';
 }
 session.changed(sync);session.saved(async()=>{await load();status.textContent='Pricing terms saved. The curve retains this exact rule and its contract exceptions.';});
 return {load,sync(){session.sync();},unsaved:session.unsaved,leave:session.leave,reset(){catalog=undefined;selected=undefined;source=undefined;session.reset();choices(curve,[]);choices(node,[]);current.replaceChildren();resolved.replaceChildren();sync();}};
}
