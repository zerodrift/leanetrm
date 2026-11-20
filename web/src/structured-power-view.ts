import {setupStructuredPowerTrades} from './structured-power-trades-view.js';
import {setupOperatingSideCards,type OperatingCompositionCatalog} from './structured-power-sides-view.js';
import {amountText,prepareAmountDisplay} from './amount-display.js';
import {mountFieldGroupLayout,type FieldGroupLayout} from './field-group-layout.js';

import {StructuredPowerPendingStore,comingSoonFields,facilityTerms,operatingParameterFields,structuredPreviewRequest,type OperatingField,type OperatingPriceContract,type StructuredPowerKind,type StructuredPowerPins,type StructuredPowerVersion} from './structured-power-model.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Choice={code:string;label:string};
type QuantityChoice=Choice&{commodityCode:string;unitCode:string;dimensionCode:string};
type QuoteChoice=Choice&{quantityCode:string;currencyCode:string;multiplier:string};
type Catalog=OperatingCompositionCatalog&{valuationFieldGroups:Record<string,FieldGroupLayout>;pins:StructuredPowerPins;locations:Choice[];clocks:Choice[];kinds:(Choice&{intervalMinutes:number[]})[];powerQuantities:QuantityChoice[];energyQuantities:QuantityChoice[];fuelQuantities:QuantityChoice[];rightQuantities:QuantityChoice[];currencies:Choice[];quotes:QuoteChoice[];priceContracts?:OperatingPriceContract[];operatingBookingClockCodes?:string[];paymentImplementation:string;canSave:boolean;canPreview:boolean};
type Fraction={numerator:string;denominator:string};
type Quantity=Fraction&{specification:{code:string}};
type Money=Fraction&{currency:{code:string}};
type Interval={start:string;end:string;operatingDate:string;hourBeginning:number;minute:number;occurrence:number};
type Preview={previewId:string;kind:string;clockFingerprint:string;knownAt:string;intervals:{interval:Interval;quantities:{name:string;quantity:Quantity}[]}[];
 closingState:{at:string;commitment:string|null;elapsedMinutes:number|null;startsUsed:number|null;quantities:{name:string;quantity:Quantity}[]}|null;
 valuation:{measure:string;perspective:string;currency:{code:string};value:Money;sides:{id:string;name:string;coefficient:string;quote:{code:string};grossValue:Money;contribution:Money;rows:{interval:Interval;paymentAt:string;quantity:Quantity;price:Fraction&{unit:unknown};priceUnitLabel:string;amount:Money;presentValue:Money;state:number;evidenceId:string|null;knownAt:string|null}[]}[]}|null;limitations:string[]};
type Receipt={requestId:string;changed:boolean;facility:StructuredPowerVersion};
type Options={api:Api;busy:()=>boolean;act:(work:()=>Promise<void>)=>Promise<void>;source:()=>{snapshotId:string;contextKey:string};identity:()=>string;zone?:()=>string;trading?:{available?:()=>boolean;canHedge?:()=>boolean;openTrade:(tradeId:string,revision:number)=>Promise<void>}};
const prefix='physical/power-facilities/';
const fraction=(value:Fraction)=>value.denominator==='1'?value.numerator:`${value.numerator}/${value.denominator}`;
const label=(value:string)=>value.replace(/_/g,' ');
const decimal=(key:string,title:string):OperatingField=>({key,label:title,type:'decimal'});
const integer=(key:string,title:string):OperatingField=>({key,label:title,type:'integer'});
const choose=(key:string,title:string,choices:readonly [string,string][]):OperatingField=>({key,label:title,type:'choice',choices});
const dynamic:Record<StructuredPowerKind,OperatingField[]>={
 battery:[decimal('at','Opening state at (UTC, ending in Z)'),decimal('storedMwh','Opening stored energy (MWh)'),decimal('cumulativeGridThroughputMwh','Retained cumulative grid throughput (MWh)'),decimal('terminalMinimumMwh','Terminal minimum stored energy (MWh)'),decimal('terminalMaximumMwh','Terminal maximum stored energy (MWh)')],
 thermal:[decimal('at','Opening state at (UTC, ending in Z)'),choose('commitment','Opening commitment',[['offline','Offline'],['online','Online']]),integer('elapsedMinutes','Elapsed time in opening state (minutes)'),decimal('previousOutputMw','Previous output (MW)'),integer('startsUsed','Retained starts in effective period'),decimal('guaranteedHeatRate','Contract guaranteed fuel per output MWh'),decimal('guaranteedNoLoadFuelPerHour','Contract guaranteed no-load fuel per hour'),decimal('guaranteedStartupFuel','Contract guaranteed fuel per start'),choose('terminalPolicy','Terminal obligations',[['require_minimum_times_satisfied','Require all minimum-time obligations satisfied']]),choose('numericalPolicy','Interval-average numerical convention',[['thermal_exact_elapsed_quantity_v1','Exact elapsed-time integration']])],
 solar:[choose('forecastAssumption','Supplied quantities',[['hypothetical_supplied_forecast_v1','Hypothetical supplied forecast; no publication assertion']]),decimal('entitlementNumerator','Entitlement fraction numerator'),decimal('entitlementDenominator','Entitlement fraction denominator'),choose('deliveryBasis','Delivery obligation',[['as_produced','As produced'],['fixed_profile','Fixed profile']]),choose('paymentBasis','Payment quantity',[['produced_only','Produced energy'],['delivery_obligation','Delivery obligation']])]
};

// This workspace only edits transport values. Native grids, feasibility,
// entitlements, units, prices and amounts are evaluated by the backend.
export function setupStructuredPower(host:HTMLElement,o:Options){
 const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=text;return node;};
 const field=(parent:HTMLElement,title:string,control:HTMLElement)=>{const wrap=el('div'),name=el('label',title);name.append(control);wrap.append(name);parent.append(wrap);return control;};
 const select=(parent:HTMLElement,title:string)=>field(parent,title,el('select')) as HTMLSelectElement;
 const input=(parent:HTMLElement,title:string,type='text')=>{const control=el('input');control.type=type;control.required=true;control.autocomplete='off';field(parent,title,control);return control;};
 const options=(control:HTMLSelectElement,values:readonly Choice[],value='')=>{control.replaceChildren(new Option('Choose…',''),...values.map(x=>new Option(x.label,x.code)));control.value=values.some(x=>x.code===value)?value:'';control.required=true;};
 const grid=()=>{const node=el('div');node.className='schedule-fields';return node;};
 const button=(title:string,run:()=>Promise<void>)=>{const node=el('button',title);node.type='button';node.onclick=()=>void o.act(run);return node;};
 const card=(title:string)=>{const node=el('div');node.className='card';node.append(el('h3',title));host.append(node);return node;};
 const note=(parent:HTMLElement,text:string)=>{const node=el('p',text);node.className='field-help';parent.append(node);return node;};
 let tradeWorkspace:ReturnType<typeof setupStructuredPowerTrades>|undefined;
 let catalog:Catalog|undefined,versions:StructuredPowerVersion[]=[],saved:StructuredPowerVersion|undefined,dirty=false,generation=0,newId='';
 const controls=new Map<string,HTMLInputElement|HTMLSelectElement>(),stateControls=new Map<string,HTMLInputElement|HTMLSelectElement>();
 const requestStore=new StructuredPowerPendingStore({getItem:key=>sessionStorage.getItem(key),setItem:(key,value)=>sessionStorage.setItem(key,value),removeItem:key=>sessionStorage.removeItem(key)});
 let pendingIdentity='',pending=requestStore.select(''),previewDirty=false;
 function bindIdentity(){const identity=o.identity();if(identity!==pendingIdentity){requestStore.save(pendingIdentity,pending);pendingIdentity=identity;pending=requestStore.select(identity);}}
  host.setAttribute('data-own-controls','');host.replaceChildren();const directory=card('Operating facilities');note(directory,'Select an existing facility location and save versioned operating parameters. These parameters feed the supplied-plan pricing preview below.');
 const directoryFields=grid();directory.append(directoryFields);const versionSelect=select(directoryFields,'Saved operating facility version');
 const actions=el('div');actions.className='schedule-actions';directory.append(actions);
 const refresh=button('Refresh',async()=>load());const add=button('Add operating parameters',async()=>newFacility());actions.append(refresh,add);
 const saveCard=card('Facility operating parameters'),form=el('form'),common=grid();saveCard.append(form);form.append(common);
 const location=select(common,'Existing facility location'),kind=select(common,'Operating kind'),clock=select(common,'Operating clock'),minutes=select(common,'Native interval');
 const power=select(common,'Power quantity (MW)'),energy=select(common,'Energy quantity (MWh)'),fuel=select(common,'Thermal fuel energy quantity');
 const effectiveFrom=input(common,'Effective from (UTC, ending in Z)'),effectiveTo=input(common,'Effective to (UTC, ending in Z)');
 const parameters=grid();form.append(parameters);const unsupported=el('div');form.append(unsupported);
 const saveButton=el('button','Save operating parameters');saveButton.type='submit';form.append(saveButton);
 const status=note(saveCard,'Load the selected market context to begin.');status.setAttribute('role','status');
 const recovery=el('div');saveCard.append(recovery);
 const statusButton=button('Check outstanding save',async()=>{
  bindIdentity();const owner=pendingIdentity,outstanding=pending,command=outstanding.command;if(!command)return;const receipt=await o.api<Receipt|null>(prefix+'status',{requestId:command.operationId});
  if(receipt){outstanding.clear();requestStore.save(owner,outstanding);if(owner!==o.identity())return;await load();apply(receipt.facility);status.textContent=receipt.changed?'Operating parameters saved.':'The saved parameters already existed.';}
  else status.textContent='No committed receipt is visible. Retry the same save request to resolve its outcome.';sync();
 });
 const retry=button('Retry original save',async()=>sendSave());recovery.append(statusButton,retry);
 const previewCard=card('Supplied-plan pricing preview'),previewForm=el('form');previewCard.append(previewForm);
 note(previewCard,'Plans and state are hypothetical assumptions under the selected market. They do not update physical operation, certify historical forecasts or create a trade.');
 const previewFields=grid();previewForm.append(previewFields);const operatingDate=input(previewFields,'First operating date','date'),firstInterval=input(previewFields,'First native interval (one-based)','number');firstInterval.min='1';firstInterval.step='1';
 const stateFields=grid();previewForm.append(stateFields);
 const plan=el('textarea');plan.required=true;plan.rows=5;plan.spellcheck=false;field(previewForm,'Supplied interval plan',plan);const planHelp=note(previewForm,'Choose an operating kind.');
 const priceEnabled=select(previewForm,'Valuation');options(priceEnabled,[{code:'none',label:'Feasibility and quantities only'},{code:'fixed',label:'Independent side prices; explicitly undiscounted'}]);
 const priceFields=grid();previewForm.append(priceFields);
 const right=select(priceFields,'One complete resource-right quantity for valuation'),currency=select(priceFields,'Valuation currency');
 const perspective=select(priceFields,'Perspective');options(perspective,[{code:'resource_right_holder_v1',label:'Holder of the complete operating right'}]);
 const measure=select(priceFields,'Value measure');options(measure,[{code:'remaining_operating_value_v1',label:'Remaining operating value; excludes historical P&L and opening cost basis'}]);
 const discounting=select(priceFields,'Discounting');options(discounting,[{code:'undiscounted',label:'Explicitly undiscounted'}]);
 const sidesHost=el('div');sidesHost.style.gridColumn='1/-1';priceFields.append(sidesHost);
 const sideCards=setupOperatingSideCards(sidesHost,{profile:()=>tradeWorkspace?.selectedProfile(),catalog:()=>catalog,context:()=>({kind:selectedKind(),energy:energy.value,fuel:fuel.value,right:right.value,currency:currency.value}),changed:()=>{previewDirty=true;clearResult();}});
 const priceControls=new Map(Object.entries({right,currency,perspective,measure,discounting}).map(([id,control])=>[id,control.parentElement!.parentElement!]));
 priceControls.set('sideDefinitions',sidesHost);
 const previewButton=el('button','Preview');previewButton.type='submit';previewForm.append(previewButton);
 const output=card('Preview result');output.hidden=true;
 function sync(){tradeWorkspace?.sync();host.inert=o.busy();saveButton.disabled=!catalog?.canSave||!!pending.command;add.disabled=!catalog?.canSave||!!pending.command;previewButton.disabled=!catalog?.canPreview||!saved||dirty;recovery.hidden=!pending.command;refresh.disabled=false;}
 function clearResult(){generation++;tradeWorkspace?.invalidate();output.hidden=true;output.replaceChildren(el('h3','Preview result'));}
 function selectedKind():StructuredPowerKind|undefined{return ['battery','thermal','solar'].includes(kind.value)?kind.value as StructuredPowerKind:undefined;}
 function renderFields(parent:HTMLElement,map:Map<string,HTMLInputElement|HTMLSelectElement>,fields:readonly OperatingField[],values:Record<string,unknown>={}){
  parent.replaceChildren();map.clear();for(const descriptor of fields){let control:HTMLInputElement|HTMLSelectElement;
   if(descriptor.type==='choice'){control=select(parent,descriptor.label);options(control,(descriptor.choices??[]).map(([code,label])=>({code,label})),String(values[descriptor.key]??''));}
   else{control=input(parent,descriptor.label,descriptor.type==='integer'?'number':'text');if(descriptor.type==='integer')control.step='1';control.value=String(values[descriptor.key]??'');}
   control.required=!descriptor.optional;control.dataset.field=descriptor.key;map.set(descriptor.key,control);
  }
 }
 function capture(map:Map<string,HTMLInputElement|HTMLSelectElement>,fields:readonly OperatingField[]){return Object.fromEntries(fields.map(f=>{const value=map.get(f.key)!.value;return [f.key,f.type==='integer'?Number(value):f.optional&&value===''?null:value];}));}
 function priceChoices(){sideCards.refresh();showPricing();}
 function showPricing(){
  const pricing=priceEnabled.value==='fixed',kind=selectedKind();priceFields.hidden=!pricing;
  if(catalog&&kind){const layout=catalog.valuationFieldGroups?.[kind];if(!layout)throw Error('Reread the operating Side field layout for this selected kind.');mountFieldGroupLayout(priceFields,layout,priceControls);}
  for(const control of [right,currency,perspective,measure,discounting])control.disabled=!pricing;
  sideCards.setEnabled(pricing);
 }
 function kindChanged(values:Record<string,unknown>={}){const k=selectedKind();renderFields(parameters,controls,k?operatingParameterFields(k):[],values);renderFields(stateFields,stateControls,k?dynamic[k]:[]);
  options(minutes,(catalog?.kinds.find(x=>x.code===k)?.intervalMinutes??[]).map(n=>({code:String(n),label:`${n} minutes`})),minutes.value);
  power.parentElement!.parentElement!.hidden=k==='solar';power.disabled=k==='solar';power.required=k!=='solar';
  fuel.parentElement!.parentElement!.hidden=k!=='thermal';fuel.disabled=k!=='thermal';fuel.required=k==='thermal';
  unsupported.replaceChildren();for(const title of k?comingSoonFields(k):[]){const control=el('input');control.disabled=true;control.placeholder='Coming soon';field(unsupported,`${title} · Coming soon`,control);}
  planHelp.textContent=k==='battery'?'One row per interval: charge MW, discharge MW, available power fraction.':k==='thermal'?'One row per interval: online/offline, output MW, available MW, startup instruction UTC (blank except a start).':k==='solar'?'One row per interval: production MWh, required delivery MWh (blank for as-produced). Fixed-profile plans support quantity exposure only; balancing valuation is Coming soon.':'Choose an operating kind.';
  showPricing();clearResult();
 }
 function apply(value:StructuredPowerVersion){saved=value;newId='';const t=value.terms;location.value=t.facilityCode;kind.value=t.kind;clock.value=t.clockCode;power.value=t.powerQuantityCode??'';energy.value=t.energyQuantityCode;fuel.value=t.fuelQuantityCode??'';effectiveFrom.value=value.effectiveFrom;effectiveTo.value=value.effectiveTo;kindChanged((t[t.kind]??{}) as Record<string,unknown>);minutes.value=String(t.intervalMinutes);versionSelect.value=`${value.id}/${value.version}`;dirty=false;tradeWorkspace?.refreshProfiles();priceChoices();sync();status.textContent=`Saved ${label(t.kind)} parameters · version ${value.version}.`;}
 async function newFacility(){bindIdentity();if(pending.command)throw Error('Resolve the outstanding save before adding another operating facility.');saved=undefined;tradeWorkspace?.refreshProfiles();newId=crypto.randomUUID();form.reset();previewForm.reset();sideCards.reset();kindChanged();dirty=true;sync();status.textContent='Choose an existing location and explicit operating parameters. No new location is created.';clearResult();}
 function bodyVersion():StructuredPowerVersion{if(!saved&&!newId)throw Error('Choose a saved version or add operating parameters first.');if(!catalog)throw Error('Load the selected market context.');const k=selectedKind();if(!k)throw Error('Choose an operating kind.');return {id:saved?.id??newId,version:(saved?.version??0)+1,effectiveFrom:effectiveFrom.value,effectiveTo:effectiveTo.value,
  terms:facilityTerms(catalog.pins,k,{facilityCode:location.value,clockCode:clock.value,intervalMinutes:Number(minutes.value),powerQuantityCode:k==='solar'?null:power.value,energyQuantityCode:energy.value,fuelQuantityCode:k==='thermal'?fuel.value:null},capture(controls,operatingParameterFields(k)))};}
 async function sendSave(){bindIdentity();const owner=pendingIdentity,outstanding=pending,command=outstanding.command;if(!command)return;const key=o.source().contextKey;const previous=outstanding.sending();requestStore.save(owner,outstanding);sync();try{const receipt=await o.api<Receipt>(command.path,{...command.body,requestId:command.operationId});outstanding.clear();requestStore.save(owner,outstanding);if(owner!==o.identity()||key!==o.source().contextKey)return;await load();apply(receipt.facility);status.textContent=receipt.changed?'Operating parameters saved.':'The same operating parameters already existed.';}catch(error){outstanding.failed((error as {code?:string}).code,previous);requestStore.save(owner,outstanding);sync();throw error;}}
 form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{bindIdentity();if(!form.reportValidity())return;const source=o.source();pending.begin(prefix+'save',{snapshotId:source.snapshotId,expectedVersion:saved?.version??0,facility:bodyVersion()});await sendSave();});};
 form.oninput=()=>{dirty=true;clearResult();sync();};kind.onchange=()=>{kindChanged();dirty=true;sync();};currency.onchange=()=>{priceChoices();clearResult();};for(const control of [power,energy,fuel])control.onchange=()=>{priceChoices();dirty=true;sync();};right.onchange=()=>{priceChoices();clearResult();};priceEnabled.onchange=()=>{showPricing();clearResult();};;
 function composition(){if(priceEnabled.value==='none')return undefined;if(priceEnabled.value!=='fixed')throw Error('Choose feasibility only or an explicit Side composition.');return {implementation:catalog!.compositionImplementation,perspective:perspective.value,measure:measure.value,currencyCode:currency.value,rightQuantityCode:right.value,discounting:discounting.value!=='undiscounted',sides:sideCards.capture()};}
 function planRequest(){if(!saved||dirty)throw Error('Save the operating parameters before previewing them.');if(!previewForm.reportValidity())throw Error('Complete the supplied plan and explicit pricing choices.');return structuredPreviewRequest({snapshotId:o.source().snapshotId,facilityId:saved.id,facilityVersion:saved.version,kind:saved.terms.kind,operatingDate:operatingDate.value,firstInterval:Number(firstInterval.value),state:capture(stateControls,dynamic[saved.terms.kind]),plan:plan.value,valuation:null,composition:composition()});}
 previewForm.onsubmit=event=>{event.preventDefault();void o.act(async()=>{const source=o.source(),token=++generation,request=planRequest();const result=await o.api<Preview>(prefix+'preview',request);if(token!==generation||source.contextKey!==o.source().contextKey)return;await renderResult(result,token,source.contextKey);});};
 previewForm.oninput=()=>{previewDirty=true;clearResult();};
 function table(parent:HTMLElement,headers:string[],rows:string[][]){const wrap=el('div');wrap.className='table-wrap';const node=el('table'),head=el('thead'),heading=el('tr');for(const title of headers)heading.append(el('th',title));head.append(heading);node.append(head);const body=el('tbody');for(const values of rows){const row=el('tr');for(const text of values)row.append(el('td',text));body.append(row);}node.append(body);wrap.append(node);parent.append(wrap);}
 async function renderResult(result:Preview,token:number,contextKey:string){const value=result.valuation;const display={presentValue:value?fraction(value.value):'',sides:value?.sides.map(side=>({presentValue:fraction(side.grossValue),contribution:fraction(side.contribution),rows:side.rows.map(row=>({quantity:fraction(row.quantity),price:fraction(row.price),cashAmount:fraction(row.amount)}))})),quantities:[...result.intervals.flatMap(row=>row.quantities.map(q=>({quantity:fraction(q.quantity)}))),...(result.closingState?.quantities??[]).map(q=>({quantity:fraction(q.quantity)}))]};
  await prepareAmountDisplay(display,body=>o.api<string[]>('amounts/display',body));
  if(token!==generation||contextKey!==o.source().contextKey)return;
  output.replaceChildren(el('h3','Preview result'));output.hidden=false;
  if(value){output.append(el('p',`Remaining operating value: ${amountText(fraction(value.value),'money')} ${value.currency.code}`));table(output,['Side','Sign','Gross value','Signed contribution'],value.sides.map(side=>[label(side.name),side.coefficient,amountText(fraction(side.grossValue),'money'),amountText(fraction(side.contribution),'money')]));
   for(const side of value.sides){const details=el('details');details.append(el('summary',`${label(side.name)} · ${catalog?.quotes.find(q=>q.code===side.quote.code)?.label??side.quote.code} · gross side schedule`));table(details,['Interval start UTC','Interval end UTC','Value UTC','Quantity','Native quantity','Price','Gross amount'],side.rows.map(row=>[row.interval.start,row.interval.end,row.paymentAt,amountText(fraction(row.quantity),'quantity'),row.quantity.specification.code,amountText(fraction(row.price),'price')+' '+row.priceUnitLabel,amountText(fraction(row.amount),'money')]));output.append(details);}}
  table(output,['Interval start UTC','Interval end UTC','Quantity','Value','Native quantity'],result.intervals.flatMap(row=>row.quantities.map(q=>[row.interval.start,row.interval.end,label(q.name),amountText(fraction(q.quantity),'quantity'),q.quantity.specification.code])));
  if(result.closingState){const state=result.closingState;note(output,`Closing state at ${state.at}${state.commitment?` · ${state.commitment} · elapsed ${state.elapsedMinutes} minutes · starts used ${state.startsUsed}`:''}`);table(output,['Closing state quantity','Value','Native quantity'],state.quantities.map(q=>[label(q.name),amountText(fraction(q.quantity),'quantity'),q.quantity.specification.code]));}
  for(const text of result.limitations)note(output,text);
 }
 versionSelect.onchange=()=>void o.act(async()=>{const selected=versions.find(x=>`${x.id}/${x.version}`===versionSelect.value);if(!selected)return;const source=o.source(),token=++generation;const value=await o.api<StructuredPowerVersion>(prefix+'get',{snapshotId:source.snapshotId,facilityId:selected.id,version:selected.version});if(token===generation&&source.contextKey===o.source().contextKey)apply(value);});
 async function load(){bindIdentity();const source=o.source();if(!source.snapshotId)return;const token=++generation;const [next,rows]=await Promise.all([o.api<Catalog>(prefix+'catalog',{snapshotId:source.snapshotId}),o.api<StructuredPowerVersion[]>(prefix+'list',{snapshotId:source.snapshotId,selectionPolicy:'all_known_versions_v1'})]);if(token!==generation||source.contextKey!==o.source().contextKey)return;
  catalog=next;versions=rows;options(versionSelect,rows.map(row=>({code:`${row.id}/${row.version}`,label:`${next.locations.find(x=>x.code===row.terms.facilityCode)?.label??row.terms.facilityCode} · ${label(row.terms.kind)} · version ${row.version}`})),saved?`${saved.id}/${saved.version}`:'');
  for(const [control,values] of [[location,next.locations],[kind,next.kinds],[clock,next.clocks],[power,next.powerQuantities],[energy,next.energyQuantities],[fuel,next.fuelQuantities],[right,next.rightQuantities],[currency,next.currencies]] as const)options(control,values,control.value);
  kindChanged();priceChoices();if(saved){const selected=rows.find(x=>x.id===saved!.id&&x.version===saved!.version);if(selected)apply(selected);else saved=undefined;}sync();status.textContent=rows.length?'Select an exact saved operating version or add parameters to an existing location.':'No operating parameters are saved under this selected context.';if(!o.trading?.available||o.trading.available())await tradeWorkspace?.load();
 }
 function contextChanged(){tradeWorkspace?.reset();bindIdentity();generation++;catalog=undefined;versions=[];saved=undefined;dirty=false;previewDirty=false;newId='';clearResult();
  form.reset();previewForm.reset();parameters.replaceChildren();controls.clear();stateFields.replaceChildren();stateControls.clear();unsupported.replaceChildren();
  for(const control of [versionSelect,location,kind,clock,minutes,power,energy,fuel,right,currency])control.replaceChildren();
  for(const control of host.querySelectorAll<HTMLInputElement|HTMLTextAreaElement>('input,textarea'))control.value='';
  sideCards.reset();priceEnabled.value='';showPricing();sync();status.textContent='Load operating facilities under the selected context.';}
 if(o.trading){const target=card('Saved operating-right trades');tradeWorkspace=setupStructuredPowerTrades(target,{api:o.api,act:o.act,busy:o.busy,source:o.source,identity:o.identity,zone:o.zone,plan:planRequest,facility:()=>saved,profileChanged:()=>sideCards.refresh(),changed:()=>sync(),available:o.trading.available,canHedge:o.trading.canHedge,admission:()=>({available:!!saved&&!!catalog?.operatingBookingClockCodes?.includes(saved.terms.clockCode),reason:!saved?'Select an exact saved facility for operating-right booking.':'Saved operating-right booking for this clock · Coming soon.'}),committed:()=>{previewDirty=false;},openTrade:o.trading.openTrade});}
 showPricing();sync();return {load,sync,selectedTrade:()=>tradeWorkspace?.selected(),plan:planRequest,reset:contextChanged,contextChanged,unconfirmed:()=>{bindIdentity();return !!pending.command||!!tradeWorkspace?.unconfirmed();},unsaved:()=>{bindIdentity();return !!pending.command||dirty||previewDirty||!!tradeWorkspace?.unsaved();},open:async(locationCode?:string)=>{await load();if(locationCode)location.value=locationCode;host.scrollIntoView({block:'start'});}};
}
