import {authoringLabel,publicationVersion} from './price-authoring-model.js';
import {authoringSession,button,choices,element,field,group,help,input,resultTable,rules,select,type PriceAuthoringOptions} from './price-authoring-ui.js';

type Revision={catalogId:string;revisionId:string};
type Reference={revision:Revision;code:string};
type Key={catalogId:string;id:string;version:number};
type Publisher={key:Key;name:string};
type Index={key:Reference;commodity:Reference;location:Reference;quote:Reference;unit:unknown;symbol:string;statistic:string};
type Definition={key:Key;references:Revision;publisher:Publisher;name:string;commodities:Reference[];indices:Index[];calendar:Reference;clock:Reference;implementation:string};
type Choice={reference:Reference;name:string};
type Occurrence={publication:Key;assessmentDate:string;assessmentFrom:string;assessmentTo:string;expectedReleaseAt:string;periods:{from:string;to:string}[]};
type Release={releaseId:string;occurrenceId:string;publication:Key;indexCode:string;stage:string;publishedAt:string;receivedAt:string;value:string;correctsReleaseId?:string|null};
type Catalog={catalogId:string;references:Revision;canEdit:boolean;canIngest:boolean;publishers:Publisher[];publications:Definition[];commodities:Choice[];indices:(Omit<Index,'symbol'|'statistic'>&{name:string})[];calendars:Choice[];clocks:Choice[];sources:Choice[];implementations:string[];occurrences?:{id:string;occurrence:Occurrence}[];releases?:Release[]};

export function setupPricePublications(host:HTMLElement,o:PriceAuthoringOptions){
 let catalog:Catalog|undefined,selected:Definition|undefined,acknowledgedPublication:Key|undefined,newId=crypto.randomUUID(),newPublisherId=crypto.randomUUID();
 const session=authoringSession(host,o,'price-publications/save');
 const head=element('div');head.className='card-heading';const title=element('div');title.append(element('h2','Price publications'),help('Keep the publisher, its publication calendar and its exact commodity indices together. Feed sources remain separate.'));
 const reload=button('Reload',()=>void change(load),'price-publications-reload'),create=button('New publication',()=>void change(async()=>choose('')),'price-publications-new');head.append(title,group(create,reload));host.append(head);
 const library=select('price-publications-library');host.append(field('Saved publication version',library));
 const status=element('p');status.id='price-publications-status';status.setAttribute('role','status');host.append(status);
 const form=element('form');form.id='price-publications-form';const fields=element('fieldset');fields.className='price-authoring-form';form.append(fields);host.append(form);
 const name=input('price-publications-name');name.maxLength=200;name.required=true;
 const publisher=select('price-publications-publisher'),publisherName=input('price-publications-publisher-name');publisherName.maxLength=200;
 const newPublisher=field('New publisher name',publisherName);
 const implementation=select('price-publications-implementation'),calendar=select('price-publications-calendar'),clock=select('price-publications-clock');
 fields.append(group(field('Publication name',name),field('Publisher',publisher),newPublisher),group(field('Methodology',implementation),field('Publication calendar',calendar),field('Market clock',clock)));
 const methodNote=help('Select a methodology. Publication dates and the delivery periods they cover are separate.');fields.append(methodNote);
 const commodities=element('div');commodities.id='price-publications-commodities';commodities.className='price-authoring-checks';fields.append(element('h3','Commodities'),commodities);
 const indexTable=element('div');indexTable.id='price-publications-indices';fields.append(element('h3','Exact indices'),help('Choose the reference index, then its vendor symbol and statistic. The reference fixes commodity, location, unit and currency.'),indexTable);
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
 form.onsubmit=event=>{event.preventDefault();void o.act(savePublication);};
 form.addEventListener('input',()=>{session.mark();resolved.replaceChildren();});form.addEventListener('change',()=>{session.mark();resolved.replaceChildren();});
 previewSection.addEventListener('input',()=>{occurrenceSession.mark();resolved.replaceChildren();});previewSection.addEventListener('change',()=>{occurrenceSession.mark();resolved.replaceChildren();});
 library.onchange=()=>void change(async()=>choose(library.value));publisher.onchange=sync;implementation.onchange=sync;
 commodities.onchange=()=>{syncIndexRows();};
 function key(d:Definition){return `${d.key.id}:${d.key.version}`;}
 async function leave(){
  // Definition and occurrence share one editor. Commit the leave decision for
  // both forms together so keeping one cannot silently discard the other.
  if(!await (session.unsaved()?session.leave():occurrenceSession.leave()))return false;
  session.clean();occurrenceSession.clean();return true;
 }
 async function change(work:()=>Promise<void>){if(o.busy())return;if(!await leave()){library.value=selected?key(selected):'';return;}await o.act(work);}
 function periodRow(value?:{from:string;to:string}){
  const row=element('div');row.className='price-authoring-map';const from=input('','date'),to=input('','date');from.dataset.period='from';to.dataset.period='to';from.value=value?.from??'';to.value=value?.to??'';
  row.append(field('Covered from',from),field('Covered to · exclusive',to),button('Remove',()=>{row.remove();occurrenceSession.mark();resolved.replaceChildren();}));periods.append(row);
 }
 function commodityCodes(){return Array.from(commodities.querySelectorAll<HTMLInputElement>('input:checked')).map(c=>c.value);}
 function renderIndices(){
  const rows=catalog?.indices.map(i=>{
   const include=input('','checkbox');include.value=i.key.code;include.dataset.index=i.key.code;include.dataset.quote=i.quote.code;include.setAttribute('aria-label',`Include ${i.name} in ${i.quote.code}`);
   const prior=selected?.indices.find(member=>member.key.code===i.key.code&&member.quote.code===i.quote.code);include.checked=!!prior;
   const symbol=input(''),statistic=input('');symbol.dataset.symbol=i.key.code;statistic.dataset.statistic=i.key.code;symbol.value=prior?.symbol??'';statistic.value=prior?.statistic??'';symbol.maxLength=statistic.maxLength=200;symbol.setAttribute('aria-label',`${i.name} vendor symbol`);statistic.setAttribute('aria-label',`${i.name} statistic`);
   include.onchange=()=>{if(include.checked)for(const other of indexTable.querySelectorAll<HTMLInputElement>('[data-index]'))if(other!==include&&other.dataset.index===include.dataset.index)other.checked=false;syncIndexRows();};
   return [include,`${i.name} (${i.key.code})`,`${i.commodity.code} · ${i.location.code} · ${i.quote.code}`,symbol,statistic];
  })??[];
  resultTable(indexTable,['Include','Reference index','Commodity · location · quote','Vendor symbol','Statistic'],rows);syncIndexRows();
 }
 function syncIndexRows(){
  const codes=commodityCodes();
  for(const i of catalog?.indices??[]){const checkbox=Array.from(indexTable.querySelectorAll<HTMLInputElement>('[data-index]')).find(c=>c.dataset.index===i.key.code&&c.dataset.quote===i.quote.code)!;const compatible=codes.includes(i.commodity.code);checkbox.disabled=!compatible;checkbox.closest('tr')!.hidden=!compatible;
   if(!compatible)checkbox.checked=false;
   for(const control of checkbox.closest('tr')!.querySelectorAll<HTMLInputElement>('input:not([type="checkbox"])'))control.disabled=!checkbox.checked;
  }
 }
 function choose(value:string){
  selected=catalog?.publications.find(d=>key(d)===value);library.value=selected?key(selected):'';newId=crypto.randomUUID();newPublisherId=crypto.randomUUID();session.clean();occurrenceSession.clean();resolved.replaceChildren();
  name.value=selected?.name??'';
  choices(publisher,[...(catalog?.publishers.map(p=>({value:`${p.key.id}:${p.key.version}`,label:`${p.name} · v${p.key.version}`}))??[]),{value:'new',label:'Add a publisher…'}],selected?`${selected.publisher.key.id}:${selected.publisher.key.version}`:'');publisherName.value='';
  rules(implementation,catalog?.implementations??[],selected?.implementation);
  choices(calendar,catalog?.calendars.map(c=>({value:c.reference.code,label:c.name}))??[],selected?.calendar.code);
  choices(clock,catalog?.clocks.map(c=>({value:c.reference.code,label:c.name}))??[],selected?.clock.code);
  commodities.replaceChildren(...(catalog?.commodities.map(c=>{const checkbox=input('','checkbox');checkbox.value=c.reference.code;checkbox.checked=selected?.commodities.some(r=>r.code===c.reference.code)??false;return field(c.name,checkbox);})??[]));renderIndices();
  periods.replaceChildren();periodRow();assessmentDate.value=assessmentFrom.value=assessmentTo.value=release.value='';
  const occurrences=catalog?.occurrences?.filter(item=>selected&&item.occurrence.publication.id===selected.key.id&&item.occurrence.publication.version===selected.key.version)??[];
  if(occurrences.length)resultTable(savedOccurrences,['Assessment date','Covered periods','Expected release · UTC'],occurrences.map(({occurrence:o})=>[o.assessmentDate,o.periods.map(p=>`${p.from} → ${p.to}`).join(', '),o.expectedReleaseAt]));else savedOccurrences.replaceChildren(help('No publication dates saved for this version.'));
  const received=catalog?.releases?.filter(r=>selected&&r.publication.id===selected.key.id&&r.publication.version===selected.key.version)??[];
  if(received.length)resultTable(releases,['Index','Price','Quote','Stage','Published · UTC','Received · UTC'],received.map(r=>[r.indexCode,r.value,selected?.indices.find(i=>i.key.code===r.indexCode)?.quote.code??'—',r.correctsReleaseId?`${r.stage} · correction`:r.stage,r.publishedAt,r.receivedAt]));else releases.replaceChildren(help('No received prices visible for this version.'));
  status.textContent=selected?`${selected.name} · version ${selected.key.version}. Saving appends a version; existing curves retain their exact references.`:'New publication · select its publisher, commodities and methodology.';sync();
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
 async function load(){
  const valid=session.ticket(),response=await o.api<Catalog>(`price-publications?snapshotId=${encodeURIComponent(o.snapshot())}`);if(!valid())return;
  const draft=occurrenceSession.unsaved()?{assessmentDate:assessmentDate.value,assessmentFrom:assessmentFrom.value,assessmentTo:assessmentTo.value,release:release.value,periods:Array.from(periods.children).map(row=>({from:row.querySelector<HTMLInputElement>('[data-period="from"]')!.value,to:row.querySelector<HTMLInputElement>('[data-period="to"]')!.value}))}:undefined;
  const id=acknowledgedPublication?.id??selected?.key.id??newId;catalog=response;
  choices(library,catalog.publications.map(d=>({value:key(d),label:`${d.publisher.name} / ${d.name} · v${d.key.version}`})),'','New publication…');
  const latest=catalog.publications.filter(d=>d.key.id===id).sort((a,b)=>b.key.version-a.key.version)[0];choose(latest?key(latest):'');acknowledgedPublication=undefined;
  if(draft){assessmentDate.value=draft.assessmentDate;assessmentFrom.value=draft.assessmentFrom;assessmentTo.value=draft.assessmentTo;release.value=draft.release;periods.replaceChildren();draft.periods.forEach(periodRow);occurrenceSession.mark();}
 }
 function sync(){
  const locked=o.busy()||session.blocked()||!catalog;fields.disabled=locked;save.disabled=locked||!catalog?.canEdit||o.historical();library.disabled=o.busy()||session.blocked();create.disabled=o.busy()||session.blocked()||!catalog?.canEdit||o.historical();reload.disabled=o.busy();
  saveOccurrence.disabled=locked||occurrenceSession.blocked()||!selected||session.unsaved()||!catalog?.canEdit||o.historical();preview.disabled=locked||occurrenceSession.blocked();
  for(const control of previewSection.querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select'))control.disabled=o.busy()||occurrenceSession.blocked();
  for(const control of periods.querySelectorAll<HTMLButtonElement>('button'))control.disabled=o.busy()||occurrenceSession.blocked();
  addPeriod.disabled=o.busy()||occurrenceSession.blocked();
  newPublisher.hidden=publisher.value!=='new';methodNote.textContent=implementation.value.includes('placeholder')?'Placeholder only. You can retain the publication and its indices, but it cannot resolve dates or ingest prices until its methodology is implemented.':'The publisher calendar supplies explicit assessment windows, covered periods and expected releases. Actual receipt and corrections are recorded separately.';
 }
 function stable(value:unknown):unknown{return Array.isArray(value)?value.map(stable):value!==null&&typeof value==='object'?Object.fromEntries(Object.entries(value).sort(([a],[b])=>a.localeCompare(b)).map(([k,v])=>[k,stable(v)])):value;}
 function same(a:unknown,b:unknown){return JSON.stringify(stable(a))===JSON.stringify(stable(b));}
 function publicationFacts(d:Definition){return {...d,key:{catalogId:d.key.catalogId,id:d.key.id},commodities:[...d.commodities].sort((a,b)=>a.code.localeCompare(b.code)),indices:[...d.indices].sort((a,b)=>a.key.code.localeCompare(b.key.code))};}
 function instant(value:string){const match=/^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(?:Z|\+00:00)$/.exec(value);if(!match||match[2]?.length===7&&!match[2].endsWith('0'))throw Error('Invalid UTC instant');return match[1]+'.'+(match[2]??'').slice(0,6).padEnd(6,'0')+'Z';}
 function occurrenceFacts(value:Occurrence){return {...value,assessmentFrom:instant(value.assessmentFrom),assessmentTo:instant(value.assessmentTo),expectedReleaseAt:instant(value.expectedReleaseAt)};}
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
 return {load,sync(){session.sync();occurrenceSession.sync();},unsaved:()=>session.unsaved()||occurrenceSession.unsaved(),leave,reset(){catalog=undefined;selected=undefined;acknowledgedPublication=undefined;session.reset();occurrenceSession.reset();choices(library,[]);resolved.replaceChildren();releases.replaceChildren();sync();}};
}
