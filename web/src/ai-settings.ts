import {renderRegularTable} from './regular-table.js';
import {displayInstant,publicText} from './market-view.js';
import {PendingCommand,type RequestCommand} from './pending-command.js';
import {zonedInput} from './user-time.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
export type CurrencyReference={catalogId:string;revisionId:string;code:string};
type Currency=CurrencyReference&{displayCode:string;name:string;catalogCode:string;revisionKey:string};
type Dimension='uncachedInput'|'cacheRead'|'cacheWrite'|'output';
type Rates=Record<Dimension,string|null>;
type Price={sequence:number;kind:string;currency:CurrencyReference|null;rates:Rates|null;tier:{threshold:number;rates:Rates}|null;effectiveFrom:string;effectiveTo:string|null;recordedAt:string;recordedBy:string};
type Segment={from:string;to:string|null;sequence:number|null;priced:boolean};
type Capability={outputLimit:number;reasoningEfforts:string[];acceptedServed:string[]}&Record<`${Fact}Source`|`${Fact}CheckedOn`,string>;
type Model={id:string;vendor:string;identifier:string;displayName:string;version:number;priceSequence:number;cacheReadRequired:boolean;prices:Price[];schedule:Segment[];
  enabled:boolean;qualified:boolean;capability:(Capability&{version:number;mapping:string;recordedBy:string;recordedAt:string})|null};
type Audit={at:string;actor:string;action:string;vendor:string;identifier?:string};
type Models={asOf:string;models:Model[];audit:Audit[];currencies:Currency[]};
type Term='retention'|'training'|'ownerAccess';
type Terms={version:number;accountType:string;recordedBy:string;recordedAt:string}&Record<Term|`${Term}Source`|`${Term}CheckedOn`,string>;
type Count={count:number|null;source:'reported'|'not_billed'|'unknown'};
type Spent='uncachedInput'|'cacheRead'|'cacheWrite'|'output'|'reasoning';
export type TestState={identifier:string;capabilityVersion:number;reasoning:string|null;credentialVersion:number;termsVersion:number;priceSequence:number;testedBy:string;
  startedAt:string;closedAt:string|null;outcome:string|null;served:string|null;usage:({status:string}&Record<Spent,Count>)|null;
  cost:{status:string;amount:string|null;currency:CurrencyReference}|null;operationId:string};
type Endpoint={url:string;check:boolean};
// The directory states whether its last test ran under the key and terms attested now.
type LastTest=TestState&{underCurrentAttestation:boolean};
type Provider={vendor:string;name:string;protocol:string;endpoint:Endpoint|null;mapping:{mapping:string;confirmedOn:string;efforts:string[]}|null;version:number;enabled:boolean;qualified:boolean;
  credential:{version:number;lastFour:string;setBy:string;setAt:string}|null;terms:Terms|null;attestation:{attestedBy:string;attestedAt:string}|null;lastTest:LastTest|null};
type Providers={providers:Provider[];audit:Audit[]};
export type PriceEntry={currency:CurrencyReference|undefined;cacheReadRequired:boolean;rates:Record<Dimension,{value:string;notOffered:boolean}>;tier:string;threshold:string;tierRates:Record<Dimension,string>};

const dimensions:Dimension[]=['uncachedInput','cacheRead','cacheWrite','output'];
const names:Record<Dimension,string>={uncachedInput:'uncached input',cacheRead:'cache read',cacheWrite:'cache write',output:'output'};
const changes:Record<string,string>={add_model:'Model added',rename_model:'Display name changed',add_price:'Price added',withdraw_price:'Price withdrawn',replace_key:'Key replaced',
  record_terms:'Terms recorded',attest:'Attested',enable_provider:'Enabled',disable_provider:'Disabled',
  edit_model:'Capabilities recorded',enable_model:'Enabled',disable_model:'Disabled'};
// Refusals that prove a first attempt wrote nothing, so its retained request can be released.
const rejections=['model_admin_input','model_admin_conflict','model_identifier_invalid','currency_unavailable','model_unqualified','model_provider_unqualified',
  'model_provider_disabled','model_mapping_unconfirmed','access_denied','request_format','request_too_large','request_token'];
export const knownAiRejection=(code:string|undefined)=>!!code&&rejections.includes(code);
// The ranges every entry keeps.
export const entryRanges='Check the entries: rates are plain decimals per million tokens, below 10^12 with at most 12 decimal places, a tier offers the same rates as its base set, and an interval ends after it starts; a display name has 1 to 80 characters and terms text 1 to 1,000, each with no space at either end and no control, line-separator or text-direction formatting characters; an output limit is 1 to 10,000,000 tokens, efforts are among those offered, and served identifiers are at most 16, each once, never the model’s own; each source is an https link naming a host, and each checked date has begun; a key has 8 to 512 visible ASCII characters.';
// Commands that send what the administrator typed. Only an entry outside its range makes one of their requests the
// server cannot read or one larger than it accepts, so the ranges explain those refusals; other commands keep the
// general message.
const entered=['models/add','models/rename','models/price','models/edit','providers/terms','providers/key'].map(path=>'settings/ai/'+path);
export function aiRefusal(error:unknown,path:string){
  const code=(error as {code?:string}).code;
  return entered.includes(path)&&(code==='request_format'||code==='request_too_large')?Object.assign(Error(entryRanges),{code}):error;
}
const modelPaths=['add','rename','price','withdraw','edit','enable','disable'].map(path=>'settings/ai/models/'+path);
const providerPaths=['terms','attest','enable','disable'].map(path=>'settings/ai/providers/'+path),keyPath='settings/ai/providers/key',testPath='settings/ai/providers/test';
// A test refused for these opened nothing. One in progress or whose answer was lost is retained, and so is one refused
// access_denied, which the answer can be after the test was recorded and sent.
const testRejections=['model_unpriced','parameter_required','credential_unavailable','agent_runtime_unavailable'];
const inconsistent=()=>Error('The AI settings directory is inconsistent. Refresh AI settings.');
const grouped=(count:number)=>count.toLocaleString('en-US');

export const currencyKey=(c:CurrencyReference)=>JSON.stringify([c.catalogId,c.revisionId,c.code]);
// A label names its catalog, revision and code only when two choices would otherwise read the same.
export function currencyLabels(choices:Currency[]){
  const plain=(c:Currency)=>`${c.displayCode} · ${c.name}`,counts=new Map<string,number>();
  for(const c of choices)counts.set(plain(c),(counts.get(plain(c))??0)+1);
  return new Map(choices.map(c=>[currencyKey(c),counts.get(plain(c))!>1?`${plain(c)} (${c.catalogCode} ${c.revisionKey} ${c.code})`:plain(c)]));
}
// Uncached input and output are always offered and cache write may never be; the directory says whether
// cache read must be. Nothing blank is read as "not offered", and rates travel exactly as entered.
export function priceTerms(entry:PriceEntry){
  if(!entry.currency)throw Error('Choose a currency.');
  const optional=(d:Dimension)=>d==='cacheWrite'||d==='cacheRead'&&!entry.cacheReadRequired;
  const rates=Object.fromEntries(dimensions.map(d=>{
    const {value,notOffered}=entry.rates[d];if(optional(d)&&notOffered)return [d,null];
    if(!value)throw Error(optional(d)?`Enter the ${names[d]} rate or mark it not offered.`:`Enter the ${names[d]} rate.`);
    return [d,value];
  })) as Rates;
  if(entry.tier!=='none'&&entry.tier!=='tier')throw Error('Choose whether this price has a long-prompt tier.');
  if(entry.tier==='none')return {currency:entry.currency,rates,tier:null};
  const threshold=/^[0-9]+$/.test(entry.threshold)?Number(entry.threshold):NaN;
  if(!Number.isSafeInteger(threshold))throw Error('Enter the tier threshold as a whole number of tokens.');
  // A tier prices exactly the dimensions the base set offers.
  const tierRates=Object.fromEntries(dimensions.map(d=>{
    if(rates[d]===null)return [d,null];const value=entry.tierRates[d];
    if(!value)throw Error(`Enter the tier's ${names[d]} rate.`);return [d,value];
  })) as Rates;
  return {currency:entry.currency,rates,tier:{threshold,rates:tierRates}};
}
export function endChoice(choice:string,subject:'price'|'withdrawal'){
  if(choice==='open')return true;if(choice==='ends')return false;
  throw Error(`Choose whether the ${subject} is open-ended or ends at a set time.`);
}
export function aiChange(action:string){if(!Object.hasOwn(changes,action))throw inconsistent();return changes[action];}
export function stateLabel(state:{enabled:boolean;qualified:boolean}){
  return state.enabled?(state.qualified?'Enabled':'Enabled · no longer qualified'):(state.qualified?'Disabled · qualified':'Disabled · not qualified');
}
export type Fact='outputLimit'|'reasoning'|'identifierStability'|'acceptedServed'|'tools'|'streaming';
export const facts:Fact[]=['outputLimit','reasoning','identifierStability','acceptedServed','tools','streaming'];
export const factNames:Record<Fact,string>={outputLimit:'the output limit',reasoning:'the reasoning efforts',identifierStability:"the identifier's stability",
  acceptedServed:'the served identifiers',tools:'tool calling',streaming:'streaming'};
const factLabels:Record<Fact,string>={outputLimit:'Output limit',reasoning:'Reasoning efforts',identifierStability:'Identifier',acceptedServed:'Served identifiers',
  tools:'Tool calling',streaming:'Streaming'};
export type CapabilityEntry={outputLimit:string;efforts:string[];served:string;onlyOwn:boolean;sources:Record<Fact,string>;checkedOn:Record<Fact,string>};
// Every fact is entered with the published source it came from and the date that source was checked;
// nothing blank is inferred. Model identifiers hold no spaces or commas, so either separates them.
export function capabilityRequest(entry:CapabilityEntry){
  const outputLimit=/^[0-9]+$/.test(entry.outputLimit)?Number(entry.outputLimit):NaN;
  if(!Number.isSafeInteger(outputLimit))throw Error('Enter the output limit as a whole number of tokens.');
  if(!entry.efforts.length)throw Error('Choose the reasoning efforts the model accepts.');
  const served=entry.served.split(/[\s,]+/).filter(Boolean);
  if(entry.onlyOwn&&served.length)throw Error('Either list the other identifiers its vendor may report as served, or choose that it reports only its own.');
  if(!entry.onlyOwn&&!served.length)throw Error('Enter the other identifiers its vendor may report as served, or choose that it reports only its own.');
  for(const fact of facts){
    if(!entry.sources[fact])throw Error(`Enter the source for ${factNames[fact]}.`);
    if(!entry.checkedOn[fact])throw Error(`Enter the date the source for ${factNames[fact]} was checked.`);
  }
  return {outputLimit,reasoningEfforts:entry.efforts,acceptedServed:served,
    ...Object.fromEntries(facts.flatMap(fact=>[[fact+'Source',entry.sources[fact]],[fact+'CheckedOn',entry.checkedOn[fact]]]))};
}
export type TermsEntry={accountType:string;retention:string;retentionSource:string;retentionCheckedOn:string;training:string;trainingSource:string;trainingCheckedOn:string;
  ownerAccess:string;ownerAccessSource:string;ownerAccessCheckedOn:string};
const termNames=[['retention','retention','Retention'],['training','training-use','Training use'],['ownerAccess','owner-access','Owner access']] as const;
// Terms are copied from the vendor's published documents and sent exactly as entered.
export function termsRequest(entry:TermsEntry):TermsEntry{
  if(!entry.accountType)throw Error('Enter the account type the terms apply to.');
  for(const [term,name] of termNames){
    if(!entry[term])throw Error(`Enter the ${name} terms.`);
    if(!entry[`${term}Source`])throw Error(`Enter the source of the ${name} terms.`);
    if(!entry[`${term}CheckedOn`])throw Error(`Enter the date the ${name} terms were checked.`);
  }
  return {...entry};
}
// What Attest affirms (§6.3), naming the key and terms versions it binds; two keys can share their last four.
// An attested pair is not offered for attestation again, and the statement says why.
export function attestationStatement(provider:{name:string;credential:{version:number;lastFour:string}|null;terms:{version:number;accountType:string}|null;attestation?:unknown}){
  const {name,credential,terms}=provider;
  if(!credential||!terms)return `Attest once ${name} has a key and recorded terms.`;
  const statement=`I attest that the ${name} account whose current key, version ${credential.version}, ends in ${credential.lastFour} is of the account type `
    +`“${terms.accountType}” that terms version ${terms.version} applies to, that it does not permit training on submitted content, and that it has no data-sharing opt-in.`;
  return provider.attestation?`${statement} This key and these terms are attested; a new key or new terms need a new attestation.`:statement;
}
export function capabilityRows(capability:Capability){
  const values:Record<Fact,string>={outputLimit:`${grouped(capability.outputLimit)} tokens`,reasoning:capability.reasoningEfforts.join(', '),identifierStability:'Not repointed',
    acceptedServed:capability.acceptedServed.length?capability.acceptedServed.join(', '):'Only its own',tools:'Confirmed',streaming:'Confirmed'};
  return facts.map(fact=>[factLabels[fact],values[fact],capability[`${fact}Source`],capability[`${fact}CheckedOn`]]);
}
export function segmentLabel(segment:{sequence:number|null;priced:boolean},prices:{sequence:number;kind:string;currency:CurrencyReference|null}[],labels:Map<string,string>){
  if(segment.sequence===null){if(segment.priced)throw inconsistent();return 'No price';}
  const price=prices.find(p=>p.sequence===segment.sequence);
  if(!price||segment.priced!==(price.kind==='rates'))throw inconsistent();
  return segment.priced?`Revision ${price.sequence} · ${labels.get(currencyKey(price.currency!))??'currency not readable'}`:`No price · revision ${price.sequence}`;
}
const outcomes:Record<string,string>={succeeded:'Succeeded',interrupted:'Interrupted',limit_elapsed:'Time limit elapsed',request_cancelled:'Cancelled',
  usage_unrecognized:'Usage not recognized',model_refused:'Refused by the model',model_mismatch:'Served by another model',context_window_exceeded:'Context window exceeded',
  model_provider_authentication:'Key not accepted',model_provider_rate_limited:'Rate limited',model_provider_overloaded:'Provider overloaded',
  model_provider_invalid_request:'Request not accepted',model_provider_transport:'Transport failure',model_provider_response_too_large:'Response too large'};
const spent:[Spent,string][]=[['uncachedInput','Uncached input'],['cacheRead','Cache read'],['cacheWrite','Cache write'],['output','Output'],['reasoning','Reasoning']];
const unpricedCosts:Record<string,string>={usage_unknown:'No cost: usage is unknown',usage_unrecognized:'No cost: usage was not recognized',
  cost_unpriced:'No cost: tokens were used in a dimension the pinned price does not offer'};
export function testOutcome(outcome:string|null){
  if(outcome===null)return 'No outcome recorded yet';if(!Object.hasOwn(outcomes,outcome))throw inconsistent();return outcomes[outcome];
}
// A test's pins, then, once it is closed, its usage by dimension and its exact cost as the server computed it.
export function testRows(test:TestState,currency:(c:CurrencyReference)=>string,instant:(at:string)=>string){
  if(test.reasoning===null)throw inconsistent();
  const rows=[['Outcome',testOutcome(test.outcome)],['Model',test.identifier],['Capability version',`Version ${test.capabilityVersion}`],['Reasoning effort',test.reasoning],
    ['Key and terms',`Key version ${test.credentialVersion} · terms version ${test.termsVersion}`],['Price revision',`Revision ${test.priceSequence}`],['Tested by',test.testedBy],
    ['Started',instant(test.startedAt)]];
  if(test.outcome===null)return rows;
  if(!test.closedAt||!test.usage||!test.cost)throw inconsistent();
  const {usage,cost}=test,count=({count,source}:Count)=>{
    if(source==='reported'&&count!==null)return `${grouped(count)} tokens`;if(source==='not_billed')return 'Not billed';if(source==='unknown')return 'Unknown';
    throw inconsistent();
  };
  if(cost.status!=='priced'&&!Object.hasOwn(unpricedCosts,cost.status))throw inconsistent();
  return [...rows,['Closed',instant(test.closedAt)],['Served as',test.served??'Not reported'],...spent.map(([d,name])=>[name,count(usage[d])]),
    ['Cost',cost.status==='priced'?`${cost.amount} ${currency(cost.currency)}`:unpricedCosts[cost.status]]];
}
function underCurrentAttestation(test:LastTest){if(typeof test.underCurrentAttestation!=='boolean')throw inconsistent();return test.underCurrentAttestation;}
export function lastTestLabel(test:LastTest|null,instant:(at:string)=>string){
  if(!test)return 'Not tested';
  const when=test.closedAt?`${testOutcome(test.outcome)} · ${instant(test.closedAt)}`:`${testOutcome(null)} · started ${instant(test.startedAt)}`;
  return underCurrentAttestation(test)?when:`${when} · earlier key or terms`;
}
// A provider tests only under its current key and terms once they are attested.
export function lastTestSummary(test:LastTest|null,attested:boolean){
  if(!test)return 'This provider has not been tested.';
  if(underCurrentAttestation(test))return '';
  return 'This test ran under an earlier key or terms. '+(attested?'Test again to check the current ones.':'Attest the current key and terms, then test again to check them.');
}
export function endpointLabel(endpoint:Endpoint|null){return endpoint?(endpoint.check?`${endpoint.url} (check endpoint)`:endpoint.url):'No adapter in this release';}
// A provider's test offers its own models with recorded capabilities; the server refuses any other.
export function testableModels<M extends {vendor:string;capability:unknown}>(models:M[],vendor:string){return models.filter(m=>m.vendor===vendor&&!!m.capability);}
// A test names its model and one of the efforts its capability version records; the server decides the rest.
export function testRequest(entry:{modelId:string;reasoning:string}){
  if(!entry.modelId)throw Error('Choose the model to test.');
  if(!entry.reasoning)throw Error('Choose the reasoning effort.');
  return {modelId:entry.modelId,reasoning:entry.reasoning};
}

// Settings › AI (docs/ai-assistant.md §9): Models & prices and Providers. Both tabs read both
// directories, since adding a vendor's first model creates its provider and a model's capabilities
// draw on the usage mapping its provider's adapter applies.
export function setupAiSettings(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;identity:()=>string}){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const input=(id:string)=>get<HTMLInputElement>(id);
  const text=(id:string)=>get<HTMLInputElement|HTMLTextAreaElement>(id).value;
  const form=(id:string)=>get<HTMLFormElement>(id);
  const choice=(name:string)=>document.querySelector<HTMLInputElement>(`input[name=${name}]:checked`)?.value??'';
  const instant=(at:string)=>displayInstant(at,options.zone());
  const [priceFrom,priceTo,withdrawFrom,withdrawTo]=['ai-price-from','ai-price-to','ai-withdraw-from','ai-withdraw-to'].map(id=>zonedInput(input(id),options.zone,options.api));
  const pending=new PendingCommand(knownAiRejection),providerPending=new PendingCommand(knownAiRejection);
  const testPending=new PendingCommand(code=>code!=='access_denied'&&(knownAiRejection(code)||!!code&&testRejections.includes(code)));
  // The key itself is never part of the retained request (§5.2); a retry asks for it again.
  const keyPending=new PendingCommand(code=>knownAiRejection(code)||code==='credential_unavailable');
  let models:Models|undefined,providers:Providers|undefined,labels=new Map<string,string>(),selected:string|undefined,selectedVendor:string|undefined,keyVendor:string|undefined,generation=0;
  // The key and terms the attestation statement names; a confirmation never carries over to another pair.
  let statementPair='';
  const storage=new Map<PendingCommand,()=>string>([[pending,()=>`openetrm.ai.models.request.${options.identity()}`],
    [providerPending,()=>`openetrm.ai.providers.request.${options.identity()}`],[keyPending,()=>`openetrm.ai.key.request.${options.identity()}`],
    [testPending,()=>`openetrm.ai.test.request.${options.identity()}`]]);

  // Each retained request owns its slot, so one command never rewrites another's.
  function store(holder:PendingCommand){
    const key=storage.get(holder)!();
    try{if(holder.command)sessionStorage.setItem(key,JSON.stringify(holder.command));else sessionStorage.removeItem(key);}catch{/* The in-memory request is retained for this visit. */}
  }
  function stored(holder:PendingCommand,valid:(value:RequestCommand)=>boolean):RequestCommand|undefined{
    try{
      const value=JSON.parse(sessionStorage.getItem(storage.get(holder)!())??'null');
      return value&&typeof value.operationId==='string'&&value.body&&typeof value.body==='object'&&valid(value)?{operationId:value.operationId,path:value.path,body:value.body,uncertain:true}:undefined;
    }catch{return undefined;/* Invalid browser state cannot grant authority. */}
  }
  function vendorName(code:string){const provider=providers?.providers.find(p=>p.vendor===code);if(!provider)throw inconsistent();return provider.name;}
  const model=()=>models?.models.find(m=>m.id===selected);
  const provider=()=>providers?.providers.find(p=>p.vendor===selectedVendor);
  function current(){const found=model();if(!found)throw Error('Choose a model again after refreshing AI settings.');return found;}
  function currentProvider(){const found=provider();if(!found)throw Error('Choose a provider again after refreshing AI settings.');return found;}
  function button(label:string,work:()=>void,className='secondary'){const b=document.createElement('button');b.type='button';b.className=className;b.textContent=publicText(label);b.onclick=()=>{if(!options.busy())work();};return b;}
  function fillChoices(id:string,first:string,choices:{value:string;label:string}[]){
    const node=get<HTMLSelectElement>(id),chosen=node.value,blank=new Option(first,'');
    node.replaceChildren(blank,...choices.map(c=>new Option(publicText(c.label),c.value)));node.value=choices.some(c=>c.value===chosen)?chosen:'';
  }
  const rateCells=(rates:Rates)=>dimensions.map(d=>rates[d]??'Not offered');
  const hosts=['ai-model-table','ai-model-audit','ai-price-history','ai-price-schedule','ai-capability-table','ai-provider-table','ai-terms-table','ai-test-table','ai-provider-audit'];
  const efforts=()=>[...get('ai-capability-efforts').querySelectorAll<HTMLInputElement>('input:checked')].map(box=>box.value);
  // One source and checked date per fact.
  get('ai-capability-sources').replaceChildren(...facts.map(fact=>{
    const row=document.createElement('tr'),name=document.createElement('th'),source=document.createElement('input'),checked=document.createElement('input');
    name.scope='row';name.textContent=factLabels[fact];
    // A text field keeps what was typed, spaces included; the server decides whether it is a source.
    source.id='ai-source-'+fact;source.type='text';source.inputMode='url';source.autocomplete='off';source.spellcheck=false;source.setAttribute('aria-label',`Source for ${factNames[fact]}`);
    checked.id='ai-checked-'+fact;checked.type='date';checked.setAttribute('aria-label',`Date the source for ${factNames[fact]} was checked`);
    row.append(name,...[source,checked].map(control=>{const cell=document.createElement('td');cell.append(control);return cell;}));
    return row;
  }));

  function clearDirectories(){
    models=undefined;providers=undefined;labels=new Map();for(const id of hosts)get(id).replaceChildren();
    get('ai-model-card').hidden=true;get('ai-provider-card').hidden=true;keyVendor=undefined;input('ai-key-value').value='';get('ai-model-asof').textContent='';
  }
  function resetPrices(){
    for(const id of ['ai-price-form','ai-withdraw-form'])form(id).reset();
    for(const zoned of [priceFrom,priceTo,withdrawFrom,withdrawTo])zoned.clear();
  }
  function resetCapabilities(){form('ai-capability-form').reset();get('ai-capability-efforts').replaceChildren();}
  function resetModelForms(){resetPrices();resetCapabilities();input('ai-rename-name').value=model()?.displayName??'';}
  function select(id:string){if(!models?.models.some(m=>m.id===id))return;selected=id;resetModelForms();render();sync();get('ai-model-card').scrollIntoView({block:'nearest'});}
  function selectProvider(vendor:string){
    if(!providers?.providers.some(p=>p.vendor===vendor))return;
    selectedVendor=vendor;for(const id of ['ai-terms-form','ai-attest-form'])form(id).reset();render();sync();get('ai-provider-card').scrollIntoView({block:'nearest'});
  }
  function fillTestEfforts(){
    const chosen=models?.models.find(m=>m.id===get<HTMLSelectElement>('ai-test-model').value);
    fillChoices('ai-test-effort','Choose an effort…',(chosen?.capability?.reasoningEfforts??[]).map(effort=>({value:effort,label:effort})));
  }
  function openKey(vendor:string){if(keyPending.command)return;keyVendor=vendor;input('ai-key-value').value='';sync();input('ai-key-value').focus();}

  function render(){
    if(!models||!providers)return;
    get('ai-model-asof').textContent=`Prices in force at ${instant(models.asOf)} · times in ${options.zone()}.`;
    renderRegularTable(get('ai-model-table'),'ai-models',options.identity(),['Provider','Model','Identifier','State','In force','Uncached input','Cache read','Cache write','Output','Long-prompt tier','Next change'],models.models.map(m=>{
      const [now,next]=m.schedule;if(!now)throw inconsistent();
      const price=now.priced?m.prices.find(p=>p.sequence===now.sequence):undefined;
      // The name opens the model, so the action stays in view however wide the table scrolls.
      return [vendorName(m.vendor),button(m.displayName,()=>select(m.id),'text-button'),m.identifier,stateLabel(m),segmentLabel(now,m.prices,labels),
        ...(price?[...rateCells(price.rates!),price.tier?`Above ${grouped(price.tier.threshold)} tokens`:'No tier']:['','','','','']),
        next?`${instant(next.from)} · ${segmentLabel(next,m.prices,labels)}`:'None scheduled'];
    }),[5,6,7,8],options.busy);
    renderRegularTable(get('ai-model-audit'),'ai-model-audit',options.identity(),['Saved','By','Model','Change'],models.audit.map(a=>[instant(a.at),a.actor,`${vendorName(a.vendor)} · ${a.identifier}`,aiChange(a.action)]),[],options.busy);
    const shown=model();get('ai-model-card').hidden=!shown;
    if(shown){
      get('ai-model-title').textContent=publicText(shown.displayName);get('ai-model-facts').textContent=publicText(`${vendorName(shown.vendor)} · ${shown.identifier} · the identifier cannot change.`);
      const scope=JSON.stringify([options.identity(),shown.id]);
      renderRegularTable(get('ai-price-history'),'ai-price-history',scope,['Revision','Applies to','Currency','Uncached input','Cache read','Cache write','Output','Effective from','Effective to','Recorded','By'],shown.prices.flatMap(p=>{
        const interval=[instant(p.effectiveFrom),p.effectiveTo?instant(p.effectiveTo):'Open-ended',instant(p.recordedAt),p.recordedBy];
        if(p.kind==='withdrawal')return [[String(p.sequence),'Withdrawal · no price','','','','','',...interval]];
        const currency=labels.get(currencyKey(p.currency!))??'Not readable';
        return [[String(p.sequence),p.tier?`Prompts up to ${grouped(p.tier.threshold)} tokens`:'All prompts',currency,...rateCells(p.rates!),...interval],
          ...(p.tier?[[String(p.sequence),`Prompts over ${grouped(p.tier.threshold)} tokens`,currency,...rateCells(p.tier.rates),...interval]]:[])];
      }),[3,4,5,6],options.busy);
      renderRegularTable(get('ai-price-schedule'),'ai-price-schedule',scope,['From','Until','Price'],shown.schedule.map(s=>[instant(s.from),s.to?instant(s.to):'Open-ended',segmentLabel(s,shown.prices,labels)]),[],options.busy);
      fillChoices('ai-price-currency','Choose a currency…',models.currencies.map(c=>({value:currencyKey(c),label:labels.get(currencyKey(c))!})));
      get('ai-price-currency-note').hidden=models.currencies.length>0;
      input('ai-none-cacheRead').closest('label')!.hidden=shown.cacheReadRequired;
      const capability=shown.capability,mapping=providers.providers.find(p=>p.vendor===shown.vendor)?.mapping;
      get('ai-capability-summary').textContent=publicText(`${stateLabel(shown)}. `+(capability
        ?`Version ${capability.version}, validated under usage mapping ${capability.mapping}, recorded by ${capability.recordedBy} at ${instant(capability.recordedAt)}.`
        :'No capabilities are recorded, so the model cannot be enabled.'));
      renderRegularTable(get('ai-capability-table'),'ai-capability',scope,['Fact','Value','Source','Checked on'],capability?capabilityRows(capability):[],[],options.busy);
      // The efforts offered are those the vendor's applied mapping states; choices survive a refresh.
      const chosen=new Set(efforts());
      get('ai-capability-efforts').replaceChildren(...(mapping?.efforts??[]).map(effort=>{
        const label=document.createElement('label'),box=document.createElement('input');box.type='checkbox';box.value=effort;box.checked=chosen.has(effort);label.append(box,' '+effort);return label;
      }));
      const note=get('ai-capability-note');note.hidden=!!mapping;
      note.textContent=mapping?'':`This release confirms no usage mapping for ${vendorName(shown.vendor)}, so its models’ capabilities cannot be recorded.`;
      if(mapping)form('ai-capability-form').removeAttribute('aria-describedby');else form('ai-capability-form').setAttribute('aria-describedby','ai-capability-note');
    }
    renderRegularTable(get('ai-provider-table'),'ai-providers',options.identity(),['Provider','Protocol','State','Usage mapping','Terms','Attestation','Key','Key set by','Key set','Endpoint','Last test'],providers.providers.map(p=>[
      button(p.name,()=>selectProvider(p.vendor),'text-button'),p.protocol,stateLabel(p),p.mapping?`${p.mapping.mapping} · confirmed ${p.mapping.confirmedOn}`:'None confirmed',
      p.terms?`Version ${p.terms.version} · ${p.terms.accountType}`:'Not recorded',p.attestation?`${p.attestation.attestedBy} · ${instant(p.attestation.attestedAt)}`:'Not attested',
      p.credential?`Ends in ${p.credential.lastFour}`:'Not set',p.credential?.setBy??'',p.credential?instant(p.credential.setAt):'',endpointLabel(p.endpoint),
      lastTestLabel(p.lastTest,instant)]),[],options.busy);
    renderRegularTable(get('ai-provider-audit'),'ai-provider-audit',options.identity(),['Saved','By','Provider','Change'],providers.audit.map(a=>[instant(a.at),a.actor,vendorName(a.vendor),aiChange(a.action)]),[],options.busy);
    const vendor=provider();get('ai-provider-card').hidden=!vendor;
    if(vendor){
      get('ai-provider-title').textContent=vendor.name;
      get('ai-provider-facts').textContent=publicText(`${vendor.protocol} · ${stateLabel(vendor)} · `
        +`${vendor.mapping?`usage mapping ${vendor.mapping.mapping}, confirmed ${vendor.mapping.confirmedOn}`:'this release confirms no usage mapping'} · `
        +(vendor.credential?`key ends in ${vendor.credential.lastFour}`:'no key set')+' · '+(vendor.endpoint?`endpoint ${endpointLabel(vendor.endpoint)}`:'no adapter in this release'));
      const terms=vendor.terms;
      get('ai-terms-summary').textContent=publicText(terms?`Version ${terms.version} for “${terms.accountType}”, recorded by ${terms.recordedBy} at ${instant(terms.recordedAt)}.`:'No terms are recorded.');
      renderRegularTable(get('ai-terms-table'),'ai-terms',JSON.stringify([options.identity(),vendor.vendor]),['Terms','Text','Source','Checked on'],
        terms?termNames.map(([term,,label])=>[label,terms[term],terms[`${term}Source`],terms[`${term}CheckedOn`]]):[],[],options.busy);
      get('ai-attest-statement').textContent=publicText(attestationStatement(vendor));
      const pair=JSON.stringify([vendor.vendor,vendor.credential?.version??null,terms?.version??null]);
      if(pair!==statementPair){input('ai-attest-confirm').checked=false;statementPair=pair;}
      const choices=testableModels(models.models,vendor.vendor);
      fillChoices('ai-test-model','Choose a model…',choices.map(m=>({value:m.id,label:`${m.displayName} · ${m.identifier}`})));fillTestEfforts();
      const note=get('ai-test-note');note.hidden=!!vendor.endpoint&&choices.length>0;
      note.textContent=vendor.endpoint?'Record a model’s capabilities to test this provider.':`No adapter for ${vendor.name} ships in this release, so it cannot be tested.`;
      const last=vendor.lastTest;
      get('ai-test-summary').textContent=lastTestSummary(last,!!vendor.attestation);
      renderRegularTable(get('ai-test-table'),'ai-test',JSON.stringify([options.identity(),vendor.vendor]),['Test','Value'],
        last?testRows(last,c=>labels.get(currencyKey(c))??'currency not readable',instant):[],[],options.busy);
    }
    fillChoices('ai-model-vendor','Choose a provider…',providers.providers.map(p=>({value:p.vendor,label:p.name})));
  }
  function sync(){
    // Without a current directory, only a retained request may be resent.
    const busy=options.busy(),missing=!models||!providers,held=!!pending.command||missing,providerHeld=!!providerPending.command||missing,noCurrency=!models?.currencies.length;
    const shown=model(),vendor=provider(),unmapped=!!shown&&!providers?.providers.find(p=>p.vendor===shown.vendor)?.mapping;
    // Attest is offered only for a key and terms not yet attested together.
    const unattestable=!vendor?.credential||!vendor.terms||!!vendor.attestation;
    const controls=(id:string)=>form(id).querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLTextAreaElement|HTMLButtonElement>('input,select,textarea,button');
    for(const [id,blocked] of [['ai-model-form',held],['ai-rename-form',held],['ai-price-form',held||noCurrency],['ai-withdraw-form',held],['ai-capability-form',held||unmapped],
      ['ai-terms-form',providerHeld],['ai-attest-form',providerHeld||unattestable],['ai-test-form',!!testPending.command||missing]] as const)
      for(const node of controls(id))node.disabled=busy||blocked;
    if(!busy&&!held&&!noCurrency){
      const tier=choice('ai-price-tier')==='tier';
      for(const d of dimensions){const none=d==='cacheRead'||d==='cacheWrite'?input('ai-none-'+d):undefined,off=!!none&&none.checked&&!none.closest('label')!.hidden;input('ai-rate-'+d).disabled=off;input('ai-tier-'+d).disabled=off||!tier;}
      input('ai-tier-threshold').disabled=!tier;input('ai-price-to').disabled=choice('ai-price-end')!=='ends';
    }
    if(!busy&&!held)input('ai-withdraw-to').disabled=choice('ai-withdraw-end')!=='ends';
    // Enable and Disable offer the change the current state allows; the server decides whether it may happen.
    for(const [id,offered,blocked] of [['ai-model-enable',!shown?.enabled,held],['ai-model-disable',!!shown?.enabled,held],
      ['ai-provider-enable',!vendor?.enabled,providerHeld],['ai-provider-disable',!!vendor?.enabled,providerHeld]] as const){
      const action=get<HTMLButtonElement>(id);action.hidden=!offered;action.disabled=busy||blocked;
    }
    get<HTMLButtonElement>('ai-provider-key').disabled=busy||!!keyPending.command;
    get('ai-models-pending').hidden=!pending.command;
    get('ai-providers-pending').hidden=!providerPending.command;
    get('ai-test-pending').hidden=!testPending.command;
    // The retained test names its model, with its vendor, when the directory lists it, never by its internal identifier.
    const retained=testPending.command?.body,tested=models?.models.find(m=>m.id===retained?.modelId);
    get('ai-test-pending-text').textContent=retained?`A test of ${tested?`${tested.displayName} (${vendorName(tested.vendor)} · ${tested.identifier})`:'a model these settings cannot list now'} at reasoning effort ${String(retained.reasoning)} `
      +'has no confirmed outcome. Retry sends the same request: it reads the test’s outcome if the test was recorded, and otherwise runs it once. Discarding the request leaves any recorded test in place.':'';
    const keyed=String(keyPending.command?.body.vendor??keyVendor??''),name=providers?.providers.find(p=>p.vendor===keyed)?.name??keyed;
    get('ai-key-form').hidden=!keyed;get('ai-key-title').textContent=keyed?`Replace the ${name} key`:'';
    get('ai-key-pending').hidden=!keyPending.command;
    get('ai-key-pending-text').textContent=keyPending.command?`A key replacement for ${name} is unconfirmed. The providers table shows the current key once it loads. To retry, enter the same key and choose Retry; a different key is refused. Discard the request once the table shows your key.`:'';
    get<HTMLButtonElement>('ai-key-submit').disabled=busy||!!keyPending.command;get<HTMLButtonElement>('ai-key-cancel').hidden=!!keyPending.command;
  }
  async function load(){
    const request=++generation,identity=options.identity();
    clearDirectories();
    const [nextModels,nextProviders]=await Promise.all([options.api<Models>('settings/ai/models'),options.api<Providers>('settings/ai/providers')]);
    if(request!==generation||identity!==options.identity())return;
    models=nextModels;providers=nextProviders;labels=currencyLabels(models.currencies);
    render();sync();
  }
  // A confirmed change clears only the form that made it.
  const resets:Record<string,(command:RequestCommand)=>void>={
    'settings/ai/models/add':()=>form('ai-model-form').reset(),
    'settings/ai/models/rename':command=>{input('ai-rename-name').value=String(command.body.displayName);},
    'settings/ai/models/price':resetPrices,'settings/ai/models/withdraw':resetPrices,'settings/ai/models/edit':resetCapabilities,
    'settings/ai/providers/terms':()=>form('ai-terms-form').reset(),'settings/ai/providers/attest':()=>form('ai-attest-form').reset()};
  // Once sent, a request keeps its identity until it is confirmed, refused before any write, or discarded.
  async function send(holder:PendingCommand,extra:Record<string,unknown>={}){
    const command=holder.command,identity=options.identity();if(!command)return;
    const previouslyUncertain=holder.sending();store(holder);
    try{
      try{await options.api(command.path,{...command.body,operationId:command.operationId,...extra});}
      finally{if(holder===keyPending)input('ai-key-value').value='';}
      if(identity!==options.identity()||holder.command!==command)return;
    }catch(error){
      if(identity!==options.identity()||holder.command!==command)return;
      const code=(error as {code?:string}).code;
      holder.failed(code,previouslyUncertain);store(holder);
      // After an unconfirmed outcome the page shows what was actually saved; a denied read stays cleared.
      if(code==='access_denied')clearDirectories();else if(holder.command)await load().catch(()=>undefined);
      sync();throw aiRefusal(error,command.path);
    }
    holder.clear();store(holder);
    if(holder===keyPending)keyVendor=undefined;else resets[command.path]?.(command);
    await load();
  }
  async function change(holder:PendingCommand,path:string,body:Record<string,unknown>){
    if(holder.command)throw Error('Retry or discard the retained request before making another change.');
    if(!models||!providers)throw Error('Refresh AI settings to confirm your current access before making a change.');
    holder.begin(path,body);store(holder);await send(holder);
  }
  async function interval(from:typeof priceFrom,to:typeof priceTo,fromId:string,end:string,subject:'price'|'withdrawal'){
    if(!input(fromId).value)throw Error(`Enter when the ${subject} takes effect.`);
    const open=endChoice(choice(end),subject);
    return {effectiveFrom:await from.instant(),effectiveTo:open?null:await to.instant()};
  }

  get('ai-tab-models').onclick=()=>{if(!options.busy())show('models');};
  get('ai-tab-providers').onclick=()=>{if(!options.busy())show('providers');};
  for(const id of ['ai-models-refresh','ai-providers-refresh'])get(id).onclick=()=>void options.act(load);
  for(const id of ['ai-price-form','ai-withdraw-form'])form(id).addEventListener('change',sync);
  // A dimension marked not offered has no rate in either set, so nothing typed there lingers unsent.
  for(const d of ['cacheRead','cacheWrite'])input('ai-none-'+d).addEventListener('change',()=>{if(input('ai-none-'+d).checked)for(const id of ['ai-rate-'+d,'ai-tier-'+d])input(id).value='';});
  form('ai-model-form').onsubmit=event=>{event.preventDefault();void options.act(()=>change(pending,'settings/ai/models/add',
    {vendor:get<HTMLSelectElement>('ai-model-vendor').value,identifier:input('ai-model-identifier').value,displayName:input('ai-model-name').value}));};
  form('ai-rename-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{const m=current();
    await change(pending,'settings/ai/models/rename',{modelId:m.id,expectedVersion:m.version,displayName:input('ai-rename-name').value});});};
  form('ai-price-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
    const m=current(),chosen=models!.currencies.find(c=>currencyKey(c)===get<HTMLSelectElement>('ai-price-currency').value);
    const terms=priceTerms({currency:chosen&&{catalogId:chosen.catalogId,revisionId:chosen.revisionId,code:chosen.code},cacheReadRequired:m.cacheReadRequired,
      rates:Object.fromEntries(dimensions.map(d=>[d,{value:input('ai-rate-'+d).value,notOffered:d==='cacheRead'||d==='cacheWrite'?input('ai-none-'+d).checked:false}])) as PriceEntry['rates'],
      tier:choice('ai-price-tier'),threshold:input('ai-tier-threshold').value,tierRates:Object.fromEntries(dimensions.map(d=>[d,input('ai-tier-'+d).value])) as PriceEntry['tierRates']});
    const when=await interval(priceFrom,priceTo,'ai-price-from','ai-price-end','price');
    await change(pending,'settings/ai/models/price',{modelId:m.id,expectedSequence:m.priceSequence,...terms,...when});
  });};
  form('ai-withdraw-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
    const m=current(),when=await interval(withdrawFrom,withdrawTo,'ai-withdraw-from','ai-withdraw-end','withdrawal');
    await change(pending,'settings/ai/models/withdraw',{modelId:m.id,expectedSequence:m.priceSequence,...when});
  });};
  form('ai-capability-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
    const m=current(),byFact=(prefix:string)=>Object.fromEntries(facts.map(fact=>[fact,input(prefix+fact).value])) as Record<Fact,string>;
    const request=capabilityRequest({outputLimit:input('ai-capability-limit').value,efforts:efforts(),served:text('ai-capability-served'),onlyOwn:input('ai-capability-only-own').checked,
      sources:byFact('ai-source-'),checkedOn:byFact('ai-checked-')});
    await change(pending,'settings/ai/models/edit',{modelId:m.id,expectedVersion:m.version,...request});
  });};
  // A state change hides the control that made it, so once the action settles focus moves to the one now
  // offered, or stays on the one used when nothing changed.
  const counterpart={enable:'disable',disable:'enable'} as const;
  function handOff(next:string,used:string){[next,used].map(id=>get<HTMLButtonElement>(id)).find(b=>!b.hidden&&!b.disabled)?.focus();}
  for(const path of ['enable','disable'] as const)get('ai-model-'+path).onclick=()=>void options.act(async()=>{
    const m=current();await change(pending,'settings/ai/models/'+path,{modelId:m.id,expectedVersion:m.version});
  }).then(()=>handOff('ai-model-'+counterpart[path],'ai-model-'+path));
  get('ai-models-retry').onclick=()=>void options.act(()=>send(pending));
  get('ai-models-discard').onclick=()=>{if(options.busy())return;pending.clear();store(pending);sync();};
  form('ai-terms-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
    const p=currentProvider(),request=termsRequest({accountType:text('ai-terms-account'),
      retention:text('ai-terms-retention'),retentionSource:text('ai-terms-retention-source'),retentionCheckedOn:text('ai-terms-retention-on'),
      training:text('ai-terms-training'),trainingSource:text('ai-terms-training-source'),trainingCheckedOn:text('ai-terms-training-on'),
      ownerAccess:text('ai-terms-owner'),ownerAccessSource:text('ai-terms-owner-source'),ownerAccessCheckedOn:text('ai-terms-owner-on')});
    await change(providerPending,'settings/ai/providers/terms',{vendor:p.vendor,expectedVersion:p.version,...request});
  });};
  form('ai-attest-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
    const p=currentProvider();
    if(!input('ai-attest-confirm').checked)throw Error('Confirm the attestation statement before attesting.');
    await change(providerPending,'settings/ai/providers/attest',{vendor:p.vendor,expectedVersion:p.version});
  });};
  for(const path of ['enable','disable'] as const)get('ai-provider-'+path).onclick=()=>void options.act(async()=>{
    const p=currentProvider();await change(providerPending,'settings/ai/providers/'+path,{vendor:p.vendor,expectedVersion:p.version});
  }).then(()=>handOff('ai-provider-'+counterpart[path],'ai-provider-'+path));
  get('ai-provider-key').onclick=()=>{const p=provider();if(p&&!options.busy())openKey(p.vendor);};
  get('ai-test-model').addEventListener('change',fillTestEfforts);
  form('ai-test-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
    currentProvider();
    await change(testPending,testPath,testRequest({modelId:get<HTMLSelectElement>('ai-test-model').value,reasoning:get<HTMLSelectElement>('ai-test-effort').value}));
  });};
  get('ai-test-retry').onclick=()=>void options.act(()=>send(testPending));
  get('ai-test-discard').onclick=()=>{if(options.busy())return;testPending.clear();store(testPending);sync();};
  get('ai-providers-retry').onclick=()=>void options.act(()=>send(providerPending));
  get('ai-providers-discard').onclick=()=>{if(options.busy())return;providerPending.clear();store(providerPending);sync();};
  form('ai-key-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
    if(keyPending.command)throw Error('Retry or discard the unconfirmed key replacement first.');
    const target=providers?.providers.find(p=>p.vendor===keyVendor);if(!target)throw Error('Choose a provider again after refreshing AI settings.');
    const key=input('ai-key-value').value;if(!key)throw Error('Enter the key.');
    keyPending.begin(keyPath,{vendor:target.vendor,expectedVersion:target.version});store(keyPending);await send(keyPending,{key});
  });};
  get('ai-key-retry').onclick=()=>void options.act(async()=>{
    const key=input('ai-key-value').value;if(!key)throw Error('Enter the same key again to retry the replacement.');
    await send(keyPending,{key});
  });
  get('ai-key-discard').onclick=()=>{if(options.busy())return;keyPending.clear();store(keyPending);keyVendor=undefined;input('ai-key-value').value='';sync();};
  get('ai-key-cancel').onclick=()=>{if(options.busy())return;keyVendor=undefined;input('ai-key-value').value='';sync();};
  function show(tab:'models'|'providers'){
    for(const name of ['models','providers']){const active=name===tab,b=get('ai-tab-'+name);get('ai-'+name).hidden=!active;b.classList.toggle('active',active);b.setAttribute('aria-selected',String(active));}
  }
  return {load,sync,
    refresh(){for(const zoned of [priceFrom,priceTo,withdrawFrom,withdrawTo])zoned.refresh();render();sync();},
    reset(){
      generation++;clearDirectories();selected=undefined;selectedVendor=undefined;statementPair='';
      for(const id of ['ai-model-form','ai-rename-form','ai-key-form','ai-terms-form','ai-attest-form','ai-test-form'])form(id).reset();resetModelForms();show('models');
      // This identity's unconfirmed requests stay offered whether or not its directories can be read.
      pending.command=stored(pending,c=>modelPaths.includes(c.path));
      providerPending.command=stored(providerPending,c=>providerPaths.includes(c.path));
      keyPending.command=stored(keyPending,c=>c.path===keyPath&&Object.keys(c.body).sort().join()==='expectedVersion,vendor');
      testPending.command=stored(testPending,c=>c.path===testPath&&Object.keys(c.body).sort().join()==='modelId,reasoning');
      sync();
    }};
}
