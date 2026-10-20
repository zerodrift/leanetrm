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
type Model={id:string;vendor:string;identifier:string;displayName:string;version:number;priceSequence:number;cacheReadRequired:boolean;prices:Price[];schedule:Segment[]};
type Audit={at:string;actor:string;action:string;vendor:string;identifier?:string};
type Models={asOf:string;models:Model[];audit:Audit[];currencies:Currency[]};
type Provider={vendor:string;name:string;protocol:string;qualified:boolean;version:number;credential:{lastFour:string;setBy:string;setAt:string}|null};
type Providers={providers:Provider[];audit:Audit[]};
export type PriceEntry={currency:CurrencyReference|undefined;cacheReadRequired:boolean;rates:Record<Dimension,{value:string;notOffered:boolean}>;tier:string;threshold:string;tierRates:Record<Dimension,string>};

const dimensions:Dimension[]=['uncachedInput','cacheRead','cacheWrite','output'];
const names:Record<Dimension,string>={uncachedInput:'uncached input',cacheRead:'cache read',cacheWrite:'cache write',output:'output'};
const changes:Record<string,string>={add_model:'Model added',rename_model:'Display name changed',add_price:'Price added',withdraw_price:'Price withdrawn',replace_key:'Key replaced',
  record_terms:'Terms recorded',attest:'Attested',enable_provider:'Enabled',disable_provider:'Disabled'};
// Refusals that prove a first attempt wrote nothing, so its retained request can be released.
const rejections=['model_admin_input','model_admin_conflict','model_identifier_invalid','currency_unavailable','access_denied','request_format','request_token'];
const modelPaths=['settings/ai/models/add','settings/ai/models/rename','settings/ai/models/price','settings/ai/models/withdraw'],keyPath='settings/ai/providers/key';
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
export function segmentLabel(segment:{sequence:number|null;priced:boolean},prices:{sequence:number;kind:string;currency:CurrencyReference|null}[],labels:Map<string,string>){
  if(segment.sequence===null){if(segment.priced)throw inconsistent();return 'No price';}
  const price=prices.find(p=>p.sequence===segment.sequence);
  if(!price||segment.priced!==(price.kind==='rates'))throw inconsistent();
  return segment.priced?`Revision ${price.sequence} · ${labels.get(currencyKey(price.currency!))??'currency not readable'}`:`No price · revision ${price.sequence}`;
}

// Settings › AI (docs/ai-assistant.md §9): Models & prices and Providers. Both tabs read both
// directories, since adding a vendor's first model creates its provider.
export function setupAiSettings(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;identity:()=>string}){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const input=(id:string)=>get<HTMLInputElement>(id);
  const form=(id:string)=>get<HTMLFormElement>(id);
  const choice=(name:string)=>document.querySelector<HTMLInputElement>(`input[name=${name}]:checked`)?.value??'';
  const instant=(at:string)=>displayInstant(at,options.zone());
  const [priceFrom,priceTo,withdrawFrom,withdrawTo]=['ai-price-from','ai-price-to','ai-withdraw-from','ai-withdraw-to'].map(id=>zonedInput(input(id),options.zone,options.api));
  const pending=new PendingCommand(code=>!!code&&rejections.includes(code));
  // The key itself is never part of the retained request (§5.2); a retry asks for it again.
  const keyPending=new PendingCommand(code=>!!code&&(rejections.includes(code)||code==='credential_unavailable'));
  let models:Models|undefined,providers:Providers|undefined,labels=new Map<string,string>(),selected:string|undefined,keyVendor:string|undefined,generation=0;
  const storage={models:()=>`openetrm.ai.models.request.${options.identity()}`,key:()=>`openetrm.ai.key.request.${options.identity()}`};

  // Each retained request owns its slot, so one command never rewrites the other's.
  function store(holder:PendingCommand){
    const key=holder===pending?storage.models():storage.key();
    try{if(holder.command)sessionStorage.setItem(key,JSON.stringify(holder.command));else sessionStorage.removeItem(key);}catch{/* The in-memory request is retained for this visit. */}
  }
  function stored(key:string,valid:(value:RequestCommand)=>boolean):RequestCommand|undefined{
    try{
      const value=JSON.parse(sessionStorage.getItem(key)??'null');
      return value&&typeof value.operationId==='string'&&value.body&&typeof value.body==='object'&&valid(value)?{operationId:value.operationId,path:value.path,body:value.body,uncertain:true}:undefined;
    }catch{return undefined;/* Invalid browser state cannot grant authority. */}
  }
  function vendorName(code:string){const provider=providers?.providers.find(p=>p.vendor===code);if(!provider)throw inconsistent();return provider.name;}
  const model=()=>models?.models.find(m=>m.id===selected);
  function current(){const found=model();if(!found)throw Error('Choose a model again after refreshing AI settings.');return found;}
  function button(label:string,work:()=>void,className='secondary'){const b=document.createElement('button');b.type='button';b.className=className;b.textContent=publicText(label);b.onclick=()=>{if(!options.busy())work();};return b;}
  function fillChoices(id:string,first:string,choices:{value:string;label:string}[]){
    const node=get<HTMLSelectElement>(id),chosen=node.value,blank=new Option(first,'');
    node.replaceChildren(blank,...choices.map(c=>new Option(publicText(c.label),c.value)));node.value=choices.some(c=>c.value===chosen)?chosen:'';
  }
  const rateCells=(rates:Rates)=>dimensions.map(d=>rates[d]??'Not offered');
  const hosts=['ai-model-table','ai-model-audit','ai-price-history','ai-price-schedule','ai-provider-table','ai-provider-audit'];

  function clearDirectories(){
    models=undefined;providers=undefined;labels=new Map();for(const id of hosts)get(id).replaceChildren();
    get('ai-model-card').hidden=true;keyVendor=undefined;input('ai-key-value').value='';get('ai-model-asof').textContent='';
  }
  function resetPrices(){
    for(const id of ['ai-price-form','ai-withdraw-form'])form(id).reset();
    for(const zoned of [priceFrom,priceTo,withdrawFrom,withdrawTo])zoned.clear();
  }
  function resetModelForms(){resetPrices();input('ai-rename-name').value=model()?.displayName??'';}
  function select(id:string){if(!models?.models.some(m=>m.id===id))return;selected=id;resetModelForms();render();sync();get('ai-model-card').scrollIntoView({block:'nearest'});}
  function openKey(vendor:string){if(keyPending.command)return;keyVendor=vendor;input('ai-key-value').value='';sync();input('ai-key-value').focus();}

  function render(){
    if(!models||!providers)return;
    get('ai-model-asof').textContent=`Prices in force at ${instant(models.asOf)} · times in ${options.zone()}.`;
    renderRegularTable(get('ai-model-table'),'ai-models',options.identity(),['Provider','Model','Identifier','In force','Uncached input','Cache read','Cache write','Output','Long-prompt tier','Next change'],models.models.map(m=>{
      const [now,next]=m.schedule;if(!now)throw inconsistent();
      const price=now.priced?m.prices.find(p=>p.sequence===now.sequence):undefined;
      // The name opens the model, so the action stays in view however wide the table scrolls.
      return [vendorName(m.vendor),button(m.displayName,()=>select(m.id),'text-button'),m.identifier,segmentLabel(now,m.prices,labels),...(price?[...rateCells(price.rates!),price.tier?`Above ${grouped(price.tier.threshold)} tokens`:'No tier']:['','','','','']),
        next?`${instant(next.from)} · ${segmentLabel(next,m.prices,labels)}`:'None scheduled'];
    }),[4,5,6,7],options.busy);
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
    }
    renderRegularTable(get('ai-provider-table'),'ai-providers',options.identity(),['Provider','Protocol','Qualification','Key','Key set by','Key set',''],providers.providers.map(p=>[
      p.name,p.protocol,p.qualified?'Qualified':'Not qualified',p.credential?`Ends in ${p.credential.lastFour}`:'Not set',p.credential?.setBy??'',p.credential?instant(p.credential.setAt):'',button('Replace key',()=>openKey(p.vendor))]),[],options.busy);
    renderRegularTable(get('ai-provider-audit'),'ai-provider-audit',options.identity(),['Saved','By','Provider','Change'],providers.audit.map(a=>[instant(a.at),a.actor,vendorName(a.vendor),aiChange(a.action)]),[],options.busy);
    fillChoices('ai-model-vendor','Choose a provider…',providers.providers.map(p=>({value:p.vendor,label:p.name})));
  }
  function sync(){
    // Without a current directory, only a retained request may be resent.
    const busy=options.busy(),held=!!pending.command||!models||!providers,noCurrency=!models?.currencies.length;
    const controls=(id:string)=>form(id).querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button');
    for(const [id,blocked] of [['ai-model-form',held],['ai-rename-form',held],['ai-price-form',held||noCurrency],['ai-withdraw-form',held]] as const)
      for(const node of controls(id))node.disabled=busy||blocked;
    if(!busy&&!held&&!noCurrency){
      const tier=choice('ai-price-tier')==='tier';
      for(const d of dimensions){const none=d==='cacheRead'||d==='cacheWrite'?input('ai-none-'+d):undefined,off=!!none&&none.checked&&!none.closest('label')!.hidden;input('ai-rate-'+d).disabled=off;input('ai-tier-'+d).disabled=off||!tier;}
      input('ai-tier-threshold').disabled=!tier;input('ai-price-to').disabled=choice('ai-price-end')!=='ends';
    }
    if(!busy&&!held)input('ai-withdraw-to').disabled=choice('ai-withdraw-end')!=='ends';
    get('ai-models-pending').hidden=!pending.command;
    const vendor=String(keyPending.command?.body.vendor??keyVendor??''),name=providers?.providers.find(p=>p.vendor===vendor)?.name??vendor;
    get('ai-key-form').hidden=!vendor;get('ai-key-title').textContent=vendor?`Replace the ${name} key`:'';
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
      sync();throw error;
    }
    holder.clear();store(holder);
    if(holder===keyPending)keyVendor=undefined;
    else if(command.path.endsWith('/add'))form('ai-model-form').reset();
    else if(command.path.endsWith('/rename'))input('ai-rename-name').value=String(command.body.displayName);
    else resetPrices();
    await load();
  }
  async function change(path:string,body:Record<string,unknown>){
    if(pending.command)throw Error('Retry or discard the retained request before making another change.');
    if(!models||!providers)throw Error('Refresh AI settings to confirm your current access before making a change.');
    pending.begin(path,body);store(pending);await send(pending);
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
  form('ai-model-form').onsubmit=event=>{event.preventDefault();void options.act(()=>change('settings/ai/models/add',
    {vendor:get<HTMLSelectElement>('ai-model-vendor').value,identifier:input('ai-model-identifier').value,displayName:input('ai-model-name').value}));};
  form('ai-rename-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{const m=current();
    await change('settings/ai/models/rename',{modelId:m.id,expectedVersion:m.version,displayName:input('ai-rename-name').value});});};
  form('ai-price-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
    const m=current(),chosen=models!.currencies.find(c=>currencyKey(c)===get<HTMLSelectElement>('ai-price-currency').value);
    const terms=priceTerms({currency:chosen&&{catalogId:chosen.catalogId,revisionId:chosen.revisionId,code:chosen.code},cacheReadRequired:m.cacheReadRequired,
      rates:Object.fromEntries(dimensions.map(d=>[d,{value:input('ai-rate-'+d).value,notOffered:d==='cacheRead'||d==='cacheWrite'?input('ai-none-'+d).checked:false}])) as PriceEntry['rates'],
      tier:choice('ai-price-tier'),threshold:input('ai-tier-threshold').value,tierRates:Object.fromEntries(dimensions.map(d=>[d,input('ai-tier-'+d).value])) as PriceEntry['tierRates']});
    const when=await interval(priceFrom,priceTo,'ai-price-from','ai-price-end','price');
    await change('settings/ai/models/price',{modelId:m.id,expectedSequence:m.priceSequence,...terms,...when});
  });};
  form('ai-withdraw-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
    const m=current(),when=await interval(withdrawFrom,withdrawTo,'ai-withdraw-from','ai-withdraw-end','withdrawal');
    await change('settings/ai/models/withdraw',{modelId:m.id,expectedSequence:m.priceSequence,...when});
  });};
  get('ai-models-retry').onclick=()=>void options.act(()=>send(pending));
  get('ai-models-discard').onclick=()=>{if(options.busy())return;pending.clear();store(pending);sync();};
  form('ai-key-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
    if(keyPending.command)throw Error('Retry or discard the unconfirmed key replacement first.');
    const provider=providers?.providers.find(p=>p.vendor===keyVendor);if(!provider)throw Error('Choose a provider again after refreshing AI settings.');
    const key=input('ai-key-value').value;if(!key)throw Error('Enter the key.');
    keyPending.begin(keyPath,{vendor:provider.vendor,expectedVersion:provider.version});store(keyPending);await send(keyPending,{key});
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
      generation++;clearDirectories();selected=undefined;for(const id of ['ai-model-form','ai-rename-form','ai-key-form'])form(id).reset();resetModelForms();show('models');
      // This identity's unconfirmed requests stay offered whether or not its directories can be read.
      pending.command=stored(storage.models(),c=>modelPaths.includes(c.path));
      keyPending.command=stored(storage.key(),c=>c.path===keyPath&&Object.keys(c.body).sort().join()==='expectedVersion,vendor');
      sync();
    }};
}
