import {renderRegularTable} from './regular-table.js';
import {pricingLabel,type FacilityProfile} from './facility-profile.js';
import type {setupFacilityQuantities} from './facility-quantities-view.js';

type Options=Parameters<typeof setupFacilityQuantities>[0]&{permission:(name:string)=>boolean};
type Version={id:string;code:string;version:number;minimum:string;maximum:string;ports:{id:string;direction:string;point:string;pipeline:string;zone:string;product:string}[]};
type Fuel={point:string;pipeline:string;zone:string;product:string;curve:string};
type Power={code:string;location:string;curve:string};
type Setup={storage:Version[];thermal:Version[];fuel:Fuel[];power:Power[]};
type Directory={profiles:FacilityProfile[];definitionRevisionId:string;working:boolean;setup:Setup|null};
type Meter={day:string;point:string;unit:string;measured:string;allocated:string|null;unallocated:string|null;currentSource:boolean|null};
type Report={meters:Meter[];totals:{stage:string;unit:string;numerator:string;denominator:string}[];trades:{tradeId:string;reference:number;revision:number;product:string;book:string;agreement:string|null;confirmation:string}[]};
function el<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string){const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;}
function field<T extends HTMLElement>(root:HTMLElement,text:string,n:T){const label=el('label',text);label.append(n);root.append(label);return n;}
function input(type:string,value=''){const n=el('input');n.type=type;n.value=value;return n;}
function select<T>(rows:T[],key:(r:T)=>string,label:(r:T)=>string){const n=el('select');for(const r of rows)n.add(new Option(label(r),key(r)));return n;}
function button(text:string,handler:()=>void){const b=el('button',text);b.type='button';b.className='secondary';b.onclick=handler;return b;}

export function setupFacilityTrades(root:HTMLElement,o:Options){
 let generation=0,reportGeneration=0,dirty=false,directory:Directory|undefined,active='',from='',to='';
 const reset=()=>{generation++;directory=undefined;active=from=to='';root.replaceChildren();dirty=false;};
 async function load(){
  const g=++generation,context=o.context();root.replaceChildren();dirty=false;
  const d=await o.api<Directory>('desk/facilities',{context});if(g!==generation)return;directory=d;
  const bar=el('div');bar.className='schedule-fields';root.append(el('h2','Facility trades and volumes'),bar);
  const picker=field(bar,'Facility profile',select(d.profiles,p=>p.id,p=>p.name+' · '+p.kind));if(d.profiles.some(p=>p.id===active))picker.value=active;
  const first=field(bar,'Volume window from',input('date',from||context.effectiveOn.slice(0,7)+'-01')),end=field(bar,'To (exclusive)',input('date',to||context.effectiveOn));
  if(end.value<=first.value){const next=new Date(first.value+'T00:00:00Z');next.setUTCMonth(next.getUTCMonth()+1);end.value=next.toISOString().slice(0,10);}
  const details=el('div'),forms=el('div');root.append(details,forms);
  const table=(title:string,headers:string[],rows:(string|HTMLElement)[][])=>{details.append(el('h3',title));const target=el('div');details.append(target);renderRegularTable(target,'facility-'+title.replaceAll(' ','-'),JSON.stringify([active,from,to,g]),headers,rows,[],o.busy);};
  const run=()=>o.act(report);
  async function report(){
   const request=++reportGeneration,p=d.profiles.find(x=>x.id===picker.value);details.replaceChildren();if(!p){details.append(el('p','No commercial profiles are available for this business date.'));return;}
   active=p.id;from=first.value;to=end.value;if(!from||!to||from>=to)throw Error('Choose an end date after the start date.');
   const result=await o.api<Report>('desk/facility-activity',{context,profileId:p.id,from,to});if(g!==generation||request!==reportGeneration)return;
   details.append(el('p',pricingLabel(p.inputLocation,p.inputCurve,p.inputAdjustment)+'  /  '+pricingLabel(p.outputLocation,p.outputCurve,p.outputAdjustment)));
   if(p.hourlyRatchets)table('Physical ratchets per elapsed hour',['From inventory','To inventory','Injection','Withdrawal'],
    p.hourlyRatchets.map(r=>[r.lower,r.upper,r.injectionPerHour,r.withdrawalPerHour]));
   if(p.ratchets)table('Commercial ratchets per gas day',['From inventory','To inventory','Injection','Withdrawal'],
    p.ratchets.map(r=>[r.lower,r.upper,r.maximumDailyInjection,r.maximumDailyWithdrawal]));
   table('Volume by source',['Measure','Quantity','Unit'],result.totals.map(t=>[t.stage,t.denominator==='1'?t.numerator:t.numerator+'/'+t.denominator,t.unit]));
   if(!result.totals.some(t=>t.stage.startsWith('Measured')))details.append(el('p','No measured volume reported for this window.'));
   if(result.meters.length)table('Meter allocations',['Gas day','Point','Unit','Metered','Allocated','Unallocated','Allocation basis'],result.meters.map(m=>[
    m.day,m.point,m.unit,m.measured,m.allocated??'Unreported',m.unallocated??'Unreported',m.currentSource===null?'Awaiting allocation':m.currentSource?'Current meter':'Earlier meter revision']));
   table('Linked trades',['Trade','Revision','Product','Book','Agreement','Confirmation'],result.trades.map(t=>[
    button(String(t.reference),()=>{const reason=o.tradeInspection();if(reason)void o.act(async()=>{throw Error(reason);});else void o.openTrade(t.tradeId,t.revision);}),
    String(t.revision),t.product.replaceAll('_',' '),t.book,t.agreement??'Unrecorded',t.confirmation]));
   if(d.working&&o.permission('BookTrade')){
    const form=el('form'),id=field(form,'Trade ID',input('text'));id.required=true;const save=el('button','Link trade');save.type='submit';form.append(save);
    form.onsubmit=e=>{e.preventDefault();void o.act(async()=>{await o.api('desk/attribute',{profileId:p.id,tradeId:id.value.trim()});await report();});};details.append(form);
   }
  }
  bar.append(button('Refresh volumes',()=>void run()));picker.onchange=()=>void run();await report();
  if(d.setup)forms.append(button('Configure commercial profile',()=>configure(forms,d,d.setup!)));
 }
 function configure(parent:HTMLElement,d:Directory,setup:Setup){
  const f=el('form');f.className='card';const fields=el('div');fields.className='schedule-fields';f.append(el('h3','Commercial facility profile'),fields);
  const kind=field(fields,'Facility type',select(['storage','thermal'],x=>x,x=>x)),code=field(fields,'Profile code',input('text')),name=field(fields,'Profile name',input('text')),
   date=field(fields,'Effective date (UTC)',input('date',o.context().effectiveOn)),section=el('div');f.append(section);code.required=name.required=true;
  let build:()=>Record<string,unknown>;
  function choices(){
   section.replaceChildren();const storage=kind.value==='storage',assets=storage?setup.storage:setup.thermal;
   const asset=field(section,'Physical version',select(assets,x=>x.id+'/'+x.version,x=>x.code+' · '+x.version)),terms=el('div');section.append(terms);
   function assetTerms(){
    terms.replaceChildren();const v=assets.find(x=>x.id+'/'+x.version===asset.value);if(!v){build=()=>{throw Error('Create the physical facility version first.');};return;}
    if(storage){
     const port=(dir:string)=>select(v.ports.filter(p=>p.direction===dir),p=>p.id,p=>[p.pipeline,p.zone,p.point,p.product].join(' / '));
     const inj=field(terms,'Injection port',port('inject')),out=field(terms,'Withdrawal port',port('withdraw')),rows=el('div');terms.append(rows);
     function add(){const line=el('div');line.className='schedule-fields';for(const [label,val] of [['Lower inventory','0'],['Upper inventory',v!.maximum],['Daily injection',''],['Daily withdrawal','']]){
      const n=field(line,label,input('text',val));n.required=true;}line.append(button('Remove band',()=>line.remove()));rows.append(line);}
     add();terms.append(button('Add commercial band',add));build=()=>({storageVersionId:v.id,injectionPortId:inj.value,withdrawalPortId:out.value,
      ratchets:Array.from(rows.children,line=>{const a=Array.from(line.querySelectorAll('input'),n=>n.value);return {lower:a[0],upper:a[1],maximumDailyInjection:a[2],maximumDailyWithdrawal:a[3]};})});
    }else{
     const fuelKey=(x:Fuel)=>JSON.stringify([x.pipeline,x.zone,x.point,x.product]);
     const inlet=field(terms,'Fuel receipt point',select(setup.fuel,fuelKey,x=>[x.pipeline,x.zone,x.point,x.product].join(' / ')+' · '+x.curve)),power=field(terms,'Power settlement index',select(setup.power,x=>x.code,x=>x.location+' · '+x.curve));
     build=()=>{const a=setup.fuel.find(x=>fuelKey(x)===inlet.value),b=setup.power.find(x=>x.code===power.value);if(!a||!b)throw Error('Choose the fuel point and power index.');
      return {plantId:v.id,plantVersion:v.version,fuelPipeline:a.pipeline,fuelZone:a.zone,fuelPoint:a.point,fuelProduct:a.product,powerLocation:b.location,powerAssignment:b.code};};
    }
   }asset.onchange=assetTerms;assetTerms();
  }kind.onchange=choices;choices();const save=el('button','Save profile');save.type='submit';f.append(save,button('Cancel',()=>{f.remove();dirty=false;}));
  f.oninput=()=>{dirty=true;};f.onsubmit=e=>{e.preventDefault();void o.act(async()=>{
    const record={kind:kind.value,code:code.value,name:name.value,effectiveAt:date.value+'T00:00:00Z',definitionRevisionId:d.definitionRevisionId,
      expectedRevision:Math.max(0,...d.profiles.filter(p=>p.code===code.value).map(p=>p.revision)),...build()};
    await o.api('desk/facility-profile',{operationId:crypto.randomUUID(),record});await load();
  });};parent.replaceChildren(f);
 }
 return {load,sync:()=>{},reset,edited:()=>dirty,unconfirmed:()=>false,refresh:()=>{},leave:()=>!dirty||window.confirm('Discard the unfinished commercial profile?')};
}
