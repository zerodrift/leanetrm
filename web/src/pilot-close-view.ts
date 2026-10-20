import {zonedInput} from './user-time.js';

type Choice={value:string;label:string};
type Policy={curveNumericalPolicy:string;riskNumericalPolicy:string;futuresConvention:string;swapConvention:string;rateNumericalPolicy:string;discountInterpolationPolicy:string;bumpConvention:string;bumpBasis:string;bumpTicks:number};
const policies=['generator','tickBasis','parameterPolicy','frozenPolicy','membershipPolicy','calendarPolicy','closePolicy'] as const;
type Catalog={operation:string;operationName:string;canCreate:boolean;warning:string;
 priorCloses:{id:string;name:string;at:string;kind:string}[];anchorFrames:{id:string;name:string;at:string}[];
 books:{id:string;name:string}[];curves:{code:string;name:string;leafCurves:string[]}[];sources:{code:string;name:string}[];
 policies:Record<typeof policies[number],Choice[]>;valuationPolicyChoices:{value:string;label:string;policy:Policy}[];
 scheduleChoices:{value:string;label:string;intervalSeconds:number|null}[];schedulePolicy:{timeZone:string;misfire:string;overlap:string};
 fields:{seed:{minimum:number;maximum:number};maximumTicks:{minimum:number;maximum:number};bumpTicks:{minimum:number;maximum:number};timeoutSeconds:{minimum:number;maximum:number};maximumAttempts:{minimum:number;maximum:number}}};

// This form sends explicit choices from the authorized catalog. The backend
// resolves references, generates prices and performs every financial calculation.
export function setupPilotClose(options:{api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;
 busy:()=>boolean;pending:()=>boolean;identity:()=>string;instant:(at:string)=>string;zone:()=>string;
 mutate:(path:string,body:Record<string,unknown>)=>Promise<void>}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const input=(id:string)=>get<HTMLInputElement|HTMLSelectElement>('pilot-close-'+id);
 function number(id:string){if(!input(id).value.trim())throw Error('Complete every required numeric setting.');return Number(input(id).value);}
 const due=zonedInput(get<HTMLInputElement>('pilot-close-due'),options.zone,options.api);
 const expiry=zonedInput(get<HTMLInputElement>('pilot-close-expires'),options.zone,options.api);
 let catalog:Catalog|undefined,generation=0;
 function choices(id:string,values:Choice[]){get<HTMLSelectElement>('pilot-close-'+id).replaceChildren(new Option('Choose…',''),...values.map(v=>new Option(v.label,v.value)));}
 function selected(id:string){return [...get<HTMLSelectElement>('pilot-close-'+id).selectedOptions].map(o=>o.value).filter(Boolean);}
 function moves(){
  const fields=get('pilot-close-moves'),previous=new Map([...fields.querySelectorAll<HTMLInputElement>('input')].map(f=>[f.dataset.curve,f.value]));fields.replaceChildren();
  if(!catalog)return;
  const leaves=new Set(selected('curves').flatMap(code=>catalog!.curves.find(c=>c.code===code)?.leafCurves??[]));
  for(const curve of leaves){const label=document.createElement('label');label.textContent=catalog.curves.find(c=>c.code===curve)?.name??curve;
   const field=document.createElement('input');field.type='number';field.required=true;field.step='1';field.dataset.curve=curve;
   field.min=String(catalog.fields.maximumTicks.minimum);field.max=String(catalog.fields.maximumTicks.maximum);field.value=previous.get(curve)??'';label.append(field);fields.append(label);}
  sync();
 }
 async function load(){
  const request=++generation,identity=options.identity();catalog=undefined;get('pilot-close-form').hidden=true;get('pilot-close-anchor-form').hidden=true;
  const result=await options.api<Catalog>('jobs/pilot-close/catalog');
  if(request!==generation||identity!==options.identity())return;
  if(!result||!Array.isArray(result.valuationPolicyChoices)||!policies.every(p=>Array.isArray(result.policies?.[p])))throw Error('Pilot policies are incomplete. Reload before configuring a job.');
  catalog=result;get('pilot-close-warning').textContent=result.warning;
  choices('prior',result.priorCloses.map(c=>({value:c.id,label:`${c.name} · ${options.instant(c.at)}`})));
  choices('anchor',result.anchorFrames.map(f=>({value:f.id,label:`${f.name} · ${options.instant(f.at)}`})));
  choices('source',result.sources.map(s=>({value:s.code,label:s.name})));choices('policy',result.valuationPolicyChoices);
  choices('frequency',result.scheduleChoices);
  for(const p of policies)choices(p,result.policies[p]);
  for(const [id,values] of [['books',result.books.map(b=>({value:b.id,label:b.name}))],['curves',result.curves.map(c=>({value:c.code,label:c.name}))]] as const)
   get<HTMLSelectElement>('pilot-close-'+id).replaceChildren(...values.map(v=>new Option(v.label,v.value)));
  for(const [id,limits] of [['seed',result.fields.seed],['bump-ticks',result.fields.bumpTicks],['timeout',result.fields.timeoutSeconds],['attempts',result.fields.maximumAttempts]] as const){input(id).setAttribute('min',String(limits.minimum));input(id).setAttribute('max',String(limits.maximum));}
  get('pilot-close-form').hidden=!result.canCreate;get('pilot-close-anchor-form').hidden=!result.canCreate;moves();sync();
 }
 get('pilot-close-load').onclick=()=>void options.act(load);
 input('curves').onchange=moves;
 get<HTMLFormElement>('pilot-close-anchor-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
  const identity=options.identity(),request=generation;
  const frame=catalog?.anchorFrames.find(f=>f.id===input('anchor').value);if(!frame)throw Error('Choose an exact existing market close.');
  await options.mutate('market/closes/register',{snapshotId:frame.id,name:input('anchor-name').value});
  if(identity!==options.identity()||request!==generation)return;
  await load();if(identity===options.identity()&&request+1===generation)get('pilot-close-status').textContent='The exact existing inputs are retained. Choose the new anchor close explicitly before creating a job.';
 });};
 get<HTMLFormElement>('pilot-close-form').onsubmit=event=>{event.preventDefault();void options.act(async()=>{
  const source=catalog,identity=options.identity(),request=generation;
  if(!source?.canCreate)throw Error('Load current pilot-job permissions and choices.');
  const policy=source.valuationPolicyChoices.find(p=>p.value===input('policy').value);
  if(!policy||!source.priorCloses.some(c=>c.id===input('prior').value)||!source.sources.some(s=>s.code===input('source').value)||
    policies.some(p=>!source.policies[p].some(c=>c.value===input(p).value)))throw Error('Select every required close and calculation policy.');
  const frequency=source.scheduleChoices.find(c=>c.value===input('frequency').value);if(!frequency)throw Error('Choose an explicit frequency.');
  const definition={priorCloseId:input('prior').value,bookIds:selected('books'),curveCodes:selected('curves'),sourceCode:input('source').value,
   seed:number('seed'),leafMoves:[...get('pilot-close-moves').querySelectorAll<HTMLInputElement>('input')].map(f=>{if(!f.value.trim())throw Error('Specify every maximum price move.');return{curve:f.dataset.curve!,maxTicks:Number(f.value)};}),
   ...Object.fromEntries(policies.map(p=>[p,input(p).value])),valuationPolicy:{...policy.policy,bumpTicks:number('bump-ticks')}};
  const schedule={name:input('name').value,operation:source.operation,intervalSeconds:frequency.intervalSeconds,...source.schedulePolicy,
   timeoutSeconds:number('timeout'),maximumAttempts:number('attempts'),profileId:null,profileVersion:null};
  const dueAt=await due.instant(),expiresAt=await expiry.instant();
  if(request!==generation||identity!==options.identity()||catalog!==source)throw Error('The pilot configuration changed. Reload before creating a job.');
  await options.mutate('jobs/pilot-close/create',{schedule:{...schedule,dueAt,expiresAt},definition});
  if(request!==generation||identity!==options.identity()||catalog!==source)return;
  reset();get('pilot-close-status').textContent='Created the generated pilot-close job. Its saved schedule and run outcomes appear in Jobs.';
 });};
 function sync(){for(const element of get('pilot-close-editor').querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button'))element.disabled=options.busy()||options.pending();}
 function reset(){generation++;catalog=undefined;get<HTMLDetailsElement>('pilot-close-editor').open=false;
  for(const id of ['pilot-close-form','pilot-close-anchor-form']){get<HTMLFormElement>(id).reset();get(id).hidden=true;}
  for(const field of get('pilot-close-editor').querySelectorAll('select'))field.replaceChildren();
  get('pilot-close-moves').replaceChildren();get('pilot-close-warning').textContent='';get('pilot-close-status').textContent='';due.clear();expiry.clear();sync();}
 return{load,sync,reset,refresh(){due.refresh();expiry.refresh();}};
}
