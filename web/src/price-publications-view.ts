import {authoringLabel,latestPublications,publicationVersion,utcMicrosecondText} from './price-authoring-model.js';
import {authoringSession,button,choices,element,field,group,help,input,resultTable,rules,select,type PriceAuthoringOptions} from './price-authoring-ui.js';
import {renderDataGrid,emptyGridLayout} from './data-grid.js';
import {formatReferenceAddress} from './reference-address.js';
import {setupPowerPublicationPreview} from './power-publication-preview.js';

type Revision={catalogId:string;revisionId:string};
type Reference={revision:Revision;code:string};
type Key={catalogId:string;id:string;version:number};
type Publisher={key:Key;name:string};
type Index={key:Reference;commodity:Reference;location:Reference;quote:Reference;unit:unknown;unitLabel?:string;symbol:string;statistic:string};
type Definition={key:Key;references:Revision;publisher:Publisher;name:string;commodities:Reference[];indices:Index[];calendar:Reference;clock:Reference;implementation:string};
type Choice={reference:Reference;name:string};
type Occurrence={publication:Key;assessmentDate:string;assessmentFrom:string;assessmentTo:string;expectedReleaseAt:string;periods:{from:string;to:string}[]};
type Release={releaseId:string;occurrenceId:string;publication:Key;indexCode:string;stage:string;publishedAt:string;receivedAt:string;value:string;correctsReleaseId?:string|null};
type Catalog={catalogId:string;references:Revision;canEdit:boolean;canIngest:boolean;publishers:Publisher[];publications:Definition[];commodities:Choice[];indices:(Omit<Index,'symbol'|'statistic'>&{name:string})[];calendars:Choice[];clocks:Choice[];sources:Choice[];implementations:string[];occurrences?:{id:string;occurrence:Occurrence}[];releases?:Release[]};

export function setupPricePublications(host:HTMLElement,o:PriceAuthoringOptions){
 let catalog:Catalog|undefined,selected:Definition|undefined,acknowledgedPublication:Key|undefined,newId=crypto.randomUUID(),newPublisherId=crypto.randomUUID();
 let choiceGeneration=0;
 let libraryGrid:ReturnType<typeof renderDataGrid>|undefined,libraryCatalog:Catalog|undefined,libraryLayout=emptyGridLayout();
 // The publisher whose publications are listed, or every publisher's; and the grid of publishers, drawn once for a catalogue.
 let publisherShown:string|undefined,libraryPublisher:string|undefined,publisherGrid:ReturnType<typeof renderDataGrid>|undefined,publisherCatalog:Catalog|undefined,publisherLayout=emptyGridLayout();
 let libraryFilters:{query:string;columns:(string|null)[]}|undefined;
 let libraryFocus:{key:string;generation:number}|undefined;
 const session=authoringSession(host,o,'price-publications/save');
 const head=element('div');head.className='card-heading';const title=element('div');title.append(help('A publisher comes first, then its publications and the indexes each one publishes. Choose a publisher to list its publications. Feed sources remain separate.'));
 const reload=button('Reload',()=>void change(()=>load()),'price-publications-reload'),create=button('New publication',()=>void change(async()=>{await choose('');}),'price-publications-new');head.append(title,group(create,reload));host.append(head);
 const publishers=element('fieldset');publishers.id='price-publications-publishers';publishers.className='publication-library';
 const publisherRows=element('div');publisherRows.className='table-workspace';publishers.append(element('legend','Publishers'),publisherRows);host.append(publishers);
 const library=element('fieldset');library.id='price-publications-library';library.className='publication-library';
 const libraryRows=element('div'),libraryTitle=element('legend');libraryRows.id='price-publications-list';
 libraryRows.className='table-workspace';library.append(libraryTitle,libraryRows);host.append(library);
 const status=element('p');status.id='price-publications-status';status.setAttribute('role','status');host.append(status);
 const form=element('form');form.id='price-publications-form';const fields=element('fieldset');fields.className='price-authoring-form';form.append(fields);host.append(form);
 const name=input('price-publications-name');name.maxLength=200;name.required=true;
 const publisher=select('price-publications-publisher'),publisherName=input('price-publications-publisher-name');publisherName.maxLength=200;
 const newPublisher=field('New publisher name',publisherName);
 const implementation=select('price-publications-implementation'),calendar=select('price-publications-calendar'),clock=select('price-publications-clock');
 fields.append(group(field('Publication name',name),field('Publisher',publisher),newPublisher),group(field('Methodology',implementation),field('Publication calendar',calendar),field('Market clock',clock)));
 const methodNote=help('Select a methodology. Publication dates and the delivery periods they cover are separate.');fields.append(methodNote);
 const commodities=element('div');commodities.id='price-publications-commodities';commodities.className='price-authoring-checks';fields.append(element('h3','Commodities'),commodities);
 const indexTable=element('div');indexTable.id='price-publications-indices';fields.append(element('h3','Indexes it publishes'),help('Tick each reference index this publication publishes, then give its vendor symbol and statistic. The reference index fixes commodity, location, unit and currency.'),indexTable);
 const save=element('button','Save publication version');save.type='submit';save.id='price-publications-save';fields.append(group(save));
 const previewSection=element('section');previewSection.id='price-publications-occurrence';previewSection.className='price-authoring-preview';
 const occurrenceSession=authoringSession(previewSection,o,'price-publications/occurrences/save');
 previewSection.append(element('h3','Publication dates'),help('Enter a resolved publisher-calendar occurrence. These dates are checked by the server; saving a definition does not invent holidays or release times.'));
 const assessmentDate=input('price-publications-assessment-date','date'),assessmentFrom=input('price-publications-assessment-from'),assessmentTo=input('price-publications-assessment-to'),release=input('price-publications-release');
 [assessmentFrom,assessmentTo,release].forEach(i=>{i.placeholder='YYYY-MM-DDTHH:mm:ssZ';i.spellcheck=false;});
 const periods=element('div');periods.id='price-publications-periods';const addPeriod=button('Add covered period',()=>{periodRow();occurrenceSession.mark();resolved.replaceChildren();},'price-publications-add-period');
 const preview=button('Check coverage and release',()=>void o.act(previewOccurrence),'price-publications-preview');
 const saveOccurrence=button('Save publication date',()=>void o.act(async()=>{
  if(!selected||session.unsaved()||!catalog?.canEdit||o.historical())throw Error('Save the publication definition first, then add dates to that exact version.');
  await occurrenceSession.save({snapshotId:o.snapshot(),id:crypto.randomUUID(),occurrence:occurrence(selected.key)},`${selected.name} · ${assessmentDate.value} publication date`);
 }),'price-publications-save-occurrence');
 const savedOccurrences=element('div');savedOccurrences.id='price-publications-occurrences';
 previewSection.append(group(field('Assessment label date',assessmentDate),field('Assessment opens · UTC',assessmentFrom),field('Assessment closes · UTC',assessmentTo),field('Expected release · UTC',release)),help('Period ends are exclusive. A weekend package lists each covered flow day. A monthly index lists the complete delivery month.'),periods,group(addPeriod,preview,saveOccurrence),savedOccurrences);host.append(previewSection);
 const resolved=element('div');resolved.id='price-publications-preview-result';resolved.setAttribute('role','status');host.append(resolved);
 const releases=element('div');releases.id='price-publications-releases';host.append(element('h3','Received prices'),help('Recent releases visible in the selected market and knowledge context. Each correction retains its original observation history.'),releases);
 const capturedHost=element('section');capturedHost.id='price-publications-power';host.append(capturedHost);
 const unpublished=element('section'),unpublishedRows=element('div');unpublished.id='price-publications-unpublished';unpublished.hidden=true;
 unpublished.append(element('h3','Reference indexes no publication publishes'),help('These indexes are in the reference catalogue and are not yet in a saved publication. Add one to the publication that publishes it, above.'),unpublishedRows);host.append(unpublished);
 const captured=setupPowerPublicationPreview(capturedHost,o,()=>!session.unsaved()&&!session.blocked()?selected:undefined);
 form.onsubmit=event=>{event.preventDefault();void o.act(savePublication);};
 form.addEventListener('input',()=>{session.mark();resolved.replaceChildren();});form.addEventListener('change',()=>{session.mark();resolved.replaceChildren();});
 previewSection.addEventListener('input',()=>{occurrenceSession.mark();resolved.replaceChildren();});previewSection.addEventListener('change',()=>{occurrenceSession.mark();resolved.replaceChildren();});
 publisher.onchange=sync;implementation.onchange=sync;
 commodities.onchange=()=>{syncIndexRows();};
 function key(d:Definition){return `${d.key.id}:${d.key.version}`;}
 function renderLibrary(){
  renderPublishers();
  if(!catalog){libraryGrid?.destroy();libraryGrid=undefined;libraryCatalog=undefined;libraryRows.replaceChildren();renderUnpublished();syncLibrary();return;}
  if(libraryGrid&&libraryCatalog===catalog&&libraryPublisher===publisherShown){syncLibrary();return;}
  if(libraryGrid&&catalog)libraryFilters=libraryGrid.state();
  libraryGrid?.destroy();libraryGrid=undefined;libraryCatalog=catalog;libraryPublisher=publisherShown;renderUnpublished();
  const publications=listed();
  libraryTitle.textContent=publisherShown===undefined?'Publications of every publisher':`Publications of ${publisherList().find(p=>p.id===publisherShown)!.name}`;
  if(!Object.keys(libraryLayout.widths).length)libraryLayout.widths={publication:Math.max(200,Math.round(libraryRows.clientWidth*.32)),publisher:Math.max(140,Math.round(libraryRows.clientWidth*.2)),indexes:Math.max(200,Math.round(libraryRows.clientWidth*.36)),version:90};
  const cells=publications.map(d=>[d.name,d.publisher.name,d.indices.length?d.indices.map(i=>indexName(i.key.code,i.quote.code)).join(', '):'None yet',String(d.key.version)]);
  libraryGrid=renderDataGrid(libraryRows,'publication-library',o.snapshot(),['Publication','Publisher','Indexes it publishes','Version'],cells,[3],()=>o.busy()||session.blocked(),{
   cellTitles:cells,
   keys:['publication','publisher','indexes','version'],layout:libraryLayout,onLayout:value=>{libraryLayout=value;},initial:libraryFilters,pageSize:5,stretch:true,
   cells:(index,column)=>{if(column!==0)return null;const text=element('span',publications[index].name);text.dataset.publicationKey=key(publications[index]);text.title=publications[index].name;return text;},
   onRowOpen:index=>{const focused=libraryRows.contains(document.activeElement);return change(()=>choose(key(publications[index]),focused));},
   onReady:syncLibrary,
  });
  syncLibrary();
  if(selected){const index=publications.findIndex(d=>key(d)===key(selected!));if(index>=0)libraryGrid.revealRow(index);}
 }
 // The publisher a publication's form opens with: its own, or for a new one the publisher whose publications are listed.
 function publisherChoice(){
  const of=selected?.publisher.key??publisherList().find(p=>p.id===publisherShown)?.key;
  return of?`${of.id}:${of.version}`:'';
 }
 // The publications listed: those of the publisher shown, or every publisher's.
 const listed=()=>(catalog?.publications??[]).filter(d=>publisherShown===undefined||d.publisher.key.id===publisherShown);
 // Each publisher once, under its latest name, with its publications and the distinct indexes their latest versions publish.
 function publisherList(){
  const latest=new Map<string,Publisher>();
  for(const p of catalog?.publishers??[])if((latest.get(p.key.id)?.key.version??0)<p.key.version)latest.set(p.key.id,p);
  return [...latest.values()].map(p=>{
   const own=latestPublications(catalog!.publications).filter(d=>d.publisher.key.id===p.key.id);
   return {id:p.key.id,key:p.key,name:p.name,publications:own.length,indexes:new Set(own.flatMap(d=>d.indices.map(i=>`${i.key.code}:${i.quote.code}`))).size};
  });
 }
 function renderPublishers(){
  if(publisherGrid&&publisherCatalog===catalog){syncPublishers();return;}
  publisherGrid?.destroy();publisherGrid=undefined;publisherCatalog=catalog;publisherRows.replaceChildren();
  if(!catalog){publisherShown=undefined;return;}
  const list=publisherList();
  if(!list.some(p=>p.id===publisherShown))publisherShown=undefined;
  if(!Object.keys(publisherLayout.widths).length)publisherLayout.widths={publisher:Math.max(240,Math.round(publisherRows.clientWidth*.6)),publications:120,indexes:120};
  const cells=list.map(p=>[p.name,String(p.publications),String(p.indexes)]);
  publisherGrid=renderDataGrid(publisherRows,'publication-publishers',o.snapshot(),['Publisher','Publications','Indexes'],cells,[1,2],()=>o.busy()||session.blocked(),{
   cellTitles:cells,keys:['publisher','publications','indexes'],layout:publisherLayout,onLayout:value=>{publisherLayout=value;},pageSize:5,stretch:true,
   // Choosing a publisher lists its publications; choosing it again lists every publisher's. The editor keeps a publication that is
   // still listed, and otherwise starts a new one under the publisher chosen.
   onRowOpen:index=>change(async()=>{
    publisherShown=publisherShown===list[index].id?undefined:list[index].id;
    if(selected&&(publisherShown===undefined||selected.publisher.key.id===publisherShown))renderLibrary();else await choose('');
   }),
   onReady:syncPublishers,
  });
  syncPublishers();
 }
 function syncPublishers(){
  publishers.disabled=o.busy()||session.blocked()||!catalog;publisherGrid?.syncPagination();
  const index=publisherList().findIndex(p=>p.id===publisherShown);publisherGrid?.selectRow(index<0?null:index);
 }
 // A reference index by its name, as the catalogue lists it; one the catalogue no longer lists keeps its code.
 const indexName=(code:string,quote:string)=>catalog?.indices.find(i=>i.key.code===code&&i.quote.code===quote)?.name??code;
 function indexLink(i:{key:Reference;name:string}){const link=element('a',`${i.name} (${i.key.code})`);link.href=formatReferenceAddress({table:'price_index_definitions',code:i.key.code});return link;}
 // Reference indexes that no saved publication publishes, so that each one is seen from this page.
 function renderUnpublished(){
  const published=new Set(latestPublications(catalog?.publications??[]).flatMap(d=>d.indices.map(i=>`${i.key.code}:${i.quote.code}`))),rest=catalog?.indices.filter(i=>!published.has(`${i.key.code}:${i.quote.code}`))??[];
  unpublished.hidden=!rest.length;
  resultTable(unpublishedRows,['Reference index','Commodity · location · quote'],rest.map(i=>[indexLink(i),`${i.commodity.code} · ${i.location.code} · ${i.unitLabel}`]));
 }
 function syncLibrary(){
  library.disabled=o.busy()||session.blocked()||!catalog;
  libraryGrid?.syncPagination();
  library.dataset.selectedPublication=selected?key(selected):'';
  const index=selected?listed().findIndex(d=>key(d)===key(selected!)):-1;
  libraryGrid?.selectRow(index<0?null:index);
  if(libraryFocus&&!library.disabled){
   if(libraryFocus.generation===choiceGeneration&&selected&&libraryFocus.key===key(selected)&&index>=0)
    libraryGrid?.focusRow(index);
   libraryFocus=undefined;
  }
 }
 async function leave(){
  // Definition and occurrence share one editor. Commit the leave decision for
  // both forms together so keeping one cannot silently discard the other.
  if(!await (session.unsaved()?session.leave():occurrenceSession.leave()))return false;
  session.clean();occurrenceSession.clean();return true;
 }
 async function change(work:()=>Promise<void|boolean>){if(o.busy()||!await leave())return false;let accepted=false;await o.act(async()=>{accepted=await work()!==false;});return accepted;}
 function periodRow(value?:{from:string;to:string}){
  const row=element('div');row.className='price-authoring-map';const from=input('','date'),to=input('','date');from.dataset.period='from';to.dataset.period='to';from.value=value?.from??'';to.value=value?.to??'';
  row.append(field('Covered from',from),field('Covered to · exclusive',to),button('Remove',()=>{row.remove();occurrenceSession.mark();resolved.replaceChildren();}));periods.append(row);
 }
 function commodityCodes(){return Array.from(commodities.querySelectorAll<HTMLInputElement>('input:checked')).map(c=>c.value);}
 function renderIndices(){
  const member=(i:{key:Reference;quote:Reference})=>!!selected?.indices.some(m=>m.key.code===i.key.code&&m.quote.code===i.quote.code);
  const rows=[...catalog?.indices??[]].sort((a,b)=>Number(member(b))-Number(member(a))).map(i=>{
   const include=input('','checkbox');include.value=i.key.code;include.dataset.index=i.key.code;include.dataset.quote=i.quote.code;include.setAttribute('aria-label',`Include ${i.name} in ${i.quote.code}`);
   const prior=selected?.indices.find(member=>member.key.code===i.key.code&&member.quote.code===i.quote.code);include.checked=!!prior;
   const symbol=input(''),statistic=input('');symbol.dataset.symbol=i.key.code;statistic.dataset.statistic=i.key.code;symbol.value=prior?.symbol??'';statistic.value=prior?.statistic??'';symbol.maxLength=statistic.maxLength=200;symbol.setAttribute('aria-label',`${i.name} vendor symbol`);statistic.setAttribute('aria-label',`${i.name} statistic`);
   include.onchange=()=>{if(include.checked)for(const other of indexTable.querySelectorAll<HTMLInputElement>('[data-index]'))if(other!==include&&other.dataset.index===include.dataset.index)other.checked=false;syncIndexRows();};
   return [include,indexLink(i),`${i.commodity.code} · ${i.location.code} · ${i.unitLabel}`,symbol,statistic];
  });
  resultTable(indexTable,['Publishes','Reference index','Commodity · location · quote','Vendor symbol','Statistic'],rows);syncIndexRows();
 }
 function syncIndexRows(){
  const codes=commodityCodes();
  for(const i of catalog?.indices??[]){const checkbox=Array.from(indexTable.querySelectorAll<HTMLInputElement>('[data-index]')).find(c=>c.dataset.index===i.key.code&&c.dataset.quote===i.quote.code)!;const compatible=codes.includes(i.commodity.code);checkbox.disabled=!compatible;checkbox.closest('tr')!.hidden=!compatible;
   if(!compatible)checkbox.checked=false;
   for(const control of checkbox.closest('tr')!.querySelectorAll<HTMLInputElement>('input:not([type="checkbox"])'))control.disabled=!checkbox.checked;
  }
 }
 async function choose(value:string,focusLibrary=false){
  const generation=++choiceGeneration,target=catalog?.publications.find(d=>key(d)===value);
  if(target&&catalog&&!same(target.references,catalog.references))
   throw Error('The publication requires the exact reference source of the current catalogue. Reload the library.');
  selected=catalog?.publications.find(d=>key(d)===value);renderLibrary();newId=crypto.randomUUID();newPublisherId=crypto.randomUUID();session.clean();occurrenceSession.clean();resolved.replaceChildren();captured.reset();
  libraryFocus=focusLibrary&&selected?{key:key(selected),generation}:undefined;
  name.value=selected?.name??'';
  choices(publisher,[...(catalog?.publishers.map(p=>({value:`${p.key.id}:${p.key.version}`,label:`${p.name} · v${p.key.version}`}))??[]),{value:'new',label:'Add a publisher…'}],publisherChoice());publisherName.value='';
  rules(implementation,catalog?.implementations??[],selected?.implementation);
  choices(calendar,catalog?.calendars.map(c=>({value:c.reference.code,label:c.name}))??[],selected?.calendar.code);
  choices(clock,catalog?.clocks.map(c=>({value:c.reference.code,label:c.name}))??[],selected?.clock.code);
  commodities.replaceChildren(...(catalog?.commodities.map(c=>{const checkbox=input('','checkbox');checkbox.value=c.reference.code;checkbox.checked=selected?.commodities.some(r=>r.code===c.reference.code)??false;return field(c.name,checkbox);})??[]));renderIndices();
  periods.replaceChildren();periodRow();assessmentDate.value=assessmentFrom.value=assessmentTo.value=release.value='';
  const occurrences=catalog?.occurrences?.filter(item=>selected&&item.occurrence.publication.id===selected.key.id&&item.occurrence.publication.version===selected.key.version)??[];
  if(occurrences.length)resultTable(savedOccurrences,['Assessment date','Covered periods','Expected release · UTC'],occurrences.map(({occurrence:o})=>[o.assessmentDate,o.periods.map(p=>`${p.from} → ${p.to}`).join(', '),o.expectedReleaseAt]));else savedOccurrences.replaceChildren(help('No publication dates saved for this version.'));
  const received=catalog?.releases?.filter(r=>selected&&r.publication.id===selected.key.id&&r.publication.version===selected.key.version)??[];
  if(received.length)resultTable(releases,['Index','Price','Price unit','Stage','Published · UTC','Received · UTC'],received.map(r=>[r.indexCode,r.value,catalog?.indices.find(i=>i.key.code===r.indexCode&&i.quote.code===selected?.indices.find(s=>s.key.code===r.indexCode)?.quote.code)?.unitLabel??'—',r.correctsReleaseId?`${r.stage} · correction`:r.stage,r.publishedAt,r.receivedAt]));else releases.replaceChildren(help('No received prices visible for this version.'));
  status.textContent=selected?`${selected.name} · version ${selected.key.version}. Saving appends a version; existing curves retain their exact references.`:'New publication · select its publisher, commodities and methodology.';sync();return true;
 }
 function definition():Definition{
  if(!catalog||!name.value.trim()||!publisher.value||!implementation.value||!calendar.value||!clock.value||!commodityCodes().length)throw Error('Name the publication and select its publisher, methodology, calendar, clock and commodities.');
  const publisherValue=publisher.value==='new'?{key:{catalogId:catalog.catalogId,id:newPublisherId,version:1},name:publisherName.value.trim()}:catalog.publishers.find(p=>`${p.key.id}:${p.key.version}`===publisher.value)!;
  if(!publisherValue?.name)throw Error('Enter the publisher name.');
  const included=Array.from(indexTable.querySelectorAll<HTMLInputElement>('[data-index]:checked'));
  const indices=included.map(checkbox=>{const i=catalog!.indices.find(i=>i.key.code===checkbox.dataset.index&&i.quote.code===checkbox.dataset.quote)!;const row=checkbox.closest('tr')!;const symbol=row.querySelector<HTMLInputElement>('[data-symbol]')!.value.trim(),statistic=row.querySelector<HTMLInputElement>('[data-statistic]')!.value.trim();if(!symbol||!statistic)throw Error('Enter a vendor symbol and statistic for every selected index.');return {key:i.key,commodity:i.commodity,location:i.location,quote:i.quote,unit:i.unit,symbol,statistic};});
  return {key:selected?publicationVersion(selected).key:{catalogId:catalog.catalogId,id:newId,version:1},references:catalog.references,publisher:publisherValue,name:name.value.trim(),implementation:implementation.value,
   commodities:catalog.commodities.filter(c=>commodityCodes().includes(c.reference.code)).map(c=>c.reference),indices,calendar:catalog.calendars.find(c=>c.reference.code===calendar.value)!.reference,clock:catalog.clocks.find(c=>c.reference.code===clock.value)!.reference};
 }
 async function savePublication(){if(!catalog?.canEdit||o.historical())throw Error('Return to working data with publication editing permission.');const d=definition();await session.save({snapshotId:o.snapshot(),expectedVersion:selected?.key.version??0,definition:d},d.name);}
 function occurrence(publication:Key):Occurrence{
  if(!assessmentDate.value||![assessmentFrom,assessmentTo,release].every(i=>i.value.endsWith('Z')))throw Error('Enter the assessment date and explicit UTC instants ending in Z.');
  return {publication,assessmentDate:assessmentDate.value,assessmentFrom:assessmentFrom.value,assessmentTo:assessmentTo.value,expectedReleaseAt:release.value,periods:Array.from(periods.children).map(row=>({from:row.querySelector<HTMLInputElement>('[data-period="from"]')!.value,to:row.querySelector<HTMLInputElement>('[data-period="to"]')!.value}))};
 }
 async function previewOccurrence(){
  const d=selected&&!session.unsaved()?selected:definition(),valid=session.ticket(),occurrenceValid=occurrenceSession.ticket();
  const response=await o.api<{coveredPeriods?:{from:string;to:string}[];expectedReleaseAt?:string}>('price-publications/preview',{snapshotId:o.snapshot(),definition:d,occurrence:occurrence(d.key)});if(!valid()||!occurrenceValid())return;
  resolved.replaceChildren(element('h3','Publication check'));
  if(response.coveredPeriods){const table=element('div');resultTable(table,['Covered from','Covered to · exclusive'],response.coveredPeriods.map(p=>[p.from,p.to]));resolved.append(table);}
  if(response.expectedReleaseAt)resolved.append(help(`Expected release · ${response.expectedReleaseAt}`));
  const details=element('details');details.append(element('summary','Resolved schedule and fixing bindings'),element('pre',JSON.stringify(response,null,2)));resolved.append(details);
 }
 // Reads the catalogue again. Asked for a publication, it opens that one at its latest version, whichever publisher was listed.
 async function load(open?:string){
  const valid=session.ticket(),response=await o.api<Catalog>(`price-publications?snapshotId=${encodeURIComponent(o.snapshot())}`);if(!valid())return;
  const draft=occurrenceSession.unsaved()?{assessmentDate:assessmentDate.value,assessmentFrom:assessmentFrom.value,assessmentTo:assessmentTo.value,release:release.value,periods:Array.from(periods.children).map(row=>({from:row.querySelector<HTMLInputElement>('[data-period="from"]')!.value,to:row.querySelector<HTMLInputElement>('[data-period="to"]')!.value}))}:undefined;
  const id=open??acknowledgedPublication?.id??selected?.key.id??newId;catalog=response;
  if(open!==undefined){publisherShown=undefined;if(!catalog.publications.some(d=>d.key.id===open))throw Error('That publication is not visible in this view.');}
  const latest=catalog.publications.filter(d=>d.key.id===id).sort((a,b)=>b.key.version-a.key.version)[0];if(!await choose(latest?key(latest):''))return;acknowledgedPublication=undefined;
  if(draft){assessmentDate.value=draft.assessmentDate;assessmentFrom.value=draft.assessmentFrom;assessmentTo.value=draft.assessmentTo;release.value=draft.release;periods.replaceChildren();draft.periods.forEach(periodRow);occurrenceSession.mark();}
 }
 function sync(){
  captured.sync();
  const locked=o.busy()||session.blocked()||!catalog;fields.disabled=locked;save.disabled=locked||!catalog?.canEdit||o.historical();create.disabled=o.busy()||session.blocked()||!catalog?.canEdit||o.historical();reload.disabled=o.busy();syncPublishers();syncLibrary();
  saveOccurrence.disabled=locked||occurrenceSession.blocked()||!selected||session.unsaved()||!catalog?.canEdit||o.historical();preview.disabled=locked||occurrenceSession.blocked();
  for(const control of previewSection.querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select'))control.disabled=o.busy()||occurrenceSession.blocked();
  for(const control of periods.querySelectorAll<HTMLButtonElement>('button'))control.disabled=o.busy()||occurrenceSession.blocked();
  addPeriod.disabled=o.busy()||occurrenceSession.blocked();
  const hourly=implementation.value==='power_captured_hourly_v1';previewSection.hidden=hourly;
  newPublisher.hidden=publisher.value!=='new';methodNote.textContent=hourly?'Uses captured hourly LMPs and corrections under the curve’s saved policy. Captured prices carry no official final-release assertion. Inspect the hourly capture below.':implementation.value.includes('placeholder')?'Placeholder only. You can retain the publication and its indices, but it cannot resolve dates or ingest prices until its methodology is implemented.':'The publisher calendar supplies explicit assessment windows, covered periods and expected releases. Actual receipt and corrections are recorded separately.';
 }
 function stable(value:unknown):unknown{return Array.isArray(value)?value.map(stable):value!==null&&typeof value==='object'?Object.fromEntries(Object.entries(value).sort(([a],[b])=>a.localeCompare(b)).map(([k,v])=>[k,stable(v)])):value;}
 function same(a:unknown,b:unknown){return JSON.stringify(stable(a))===JSON.stringify(stable(b));}
 function publicationFacts(d:Definition){return {...d,key:{catalogId:d.key.catalogId,id:d.key.id},commodities:[...d.commodities].sort((a,b)=>a.code.localeCompare(b.code)),indices:[...d.indices].sort((a,b)=>a.key.code.localeCompare(b.key.code))};}
 function occurrenceFacts(value:Occurrence){return {...value,assessmentFrom:utcMicrosecondText(value.assessmentFrom),assessmentTo:utcMicrosecondText(value.assessmentTo),expectedReleaseAt:utcMicrosecondText(value.expectedReleaseAt)};}
 session.validateReceipt((value,command)=>{try{
  const response=value as Definition,requested=command.body.definition as Definition;
  // The server may reuse any existing head with identical complete facts,
  // including one newer than a stale editor's requested next version.
  if(!Number.isInteger(response.key.version)||response.key.version<1||!same(publicationFacts(response),publicationFacts(requested)))return false;
  return true;
 }catch{return false;}});
 occurrenceSession.validateReceipt((value,command)=>{try{
  const response=value as {id:string;occurrence:Occurrence};
  // A repeated same-fact save may legitimately name the existing occurrence ID.
  if(!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(response.id)||response.id==='00000000-0000-0000-0000-000000000000'||!same(occurrenceFacts(response.occurrence),occurrenceFacts(command.body.occurrence as Occurrence)))return false;
  return true;
 }catch{return false;}});
 session.changed(sync);session.saved(async value=>{acknowledgedPublication=(value as Definition).key;await load();status.textContent='Publication version saved. Existing curve references keep their original version.';});
 occurrenceSession.changed(sync);occurrenceSession.saved(async value=>{acknowledgedPublication=(value as {occurrence:Occurrence}).occurrence.publication;await load();status.textContent='Publication date saved for this exact version.';});
 return {load,sync(){session.sync();occurrenceSession.sync();},unsaved:()=>session.unsaved()||occurrenceSession.unsaved(),leave,reset(){choiceGeneration++;catalog=undefined;selected=undefined;libraryGrid?.destroy();libraryGrid=undefined;libraryCatalog=undefined;libraryFilters=undefined;libraryLayout=emptyGridLayout();libraryFocus=undefined;publisherShown=undefined;publisherLayout=emptyGridLayout();acknowledgedPublication=undefined;session.reset();occurrenceSession.reset();renderLibrary();resolved.replaceChildren();releases.replaceChildren();sync();}};
}
