import {scenarioShift,type ScenarioTarget,type ScenarioShift} from './risk-scenarios.js';
import {node} from './risk-report-elements.js';
import type {JointRequest,JointCapabilities,RiskTolerance} from './joint-risk.js';

type SelectedShift={targetId:string;label:string;unit:string;shift:ScenarioShift};
export type JointChoices={fields:Record<string,string>;staged:SelectedShift[];scenarios:{id:string;label:string;shifts:SelectedShift[]}[];axes:{id:string;target:SelectedShift}[];mixed:{id:string;firstAxis:string;secondAxis:string}[]};
const empty=():JointChoices=>({fields:{},staged:[],scenarios:[],axes:[],mixed:[]});
/** Builds explicit transport from user choices. Target resolution and every numerical decision remain in the backend. */
export function setupJointEditor(host:HTMLFieldSetElement,changed:(event:Event)=>void,act:(work:()=>Promise<void>)=>Promise<void>){
 let state=empty(),targets:ScenarioTarget[]=[],caps:JointCapabilities|undefined,mode:'scenarios'|'derivatives'|undefined;
 const value=(key:string)=>state.fields[key]??'';
 const set=(key:string,next:string,event:Event)=>{state.fields[key]=next;changed(event);};
 const button=(text:string,id:string,action:()=>void)=>{const b=node('button',text);b.id=host.id+'-'+id;b.type='button';b.className='secondary';b.onclick=event=>void act(async()=>{action();changed(event);draw();});return b;};
 function input(parent:HTMLElement,key:string,label:string,required=false,type='text'){
  const field=node('label',label),control=node('input');control.id=host.id+'-'+key;control.type=type;control.value=value(key);control.required=required;control.maxLength=key==='label'?160:128;
  if(type==='text')control.autocomplete='off';control.oninput=event=>set(key,control.value,event);field.append(control);parent.append(field);return control;
 }
 function select(parent:HTMLElement,key:string,label:string,choices:{id:string;label:string}[],refresh=false){
  const field=node('label',label),control=node('select');control.id=host.id+'-'+key;control.append(new Option('Choose…',''),...choices.map(c=>new Option(c.label,c.id)));control.value=value(key);
  control.onchange=event=>{set(key,control.value,event);if(refresh)draw();};field.append(control);parent.append(field);return control;
 }
 function chosenShift(sizeKey:string,convention:string):SelectedShift{
  const target=targets.find(t=>t.id===value('factor')),size=value(sizeKey).trim();
  if(!target||!size)throw Error('Choose a current target and enter its explicit signed shift or positive bump.');
  return {targetId:target.id,label:target.label,unit:target.unit,shift:scenarioShift(target,size,convention,{from:value('from'),to:value('to')})};
 }
 function tolerance(kind:string):RiskTolerance{
  const absolute=value(kind+'-absolute').trim(),relative=value(kind+'-relative').trim();
  if(!absolute||!relative)throw Error('Enter both absolute and relative tolerances for every requested derivative type. Zero is an explicit choice.');
  return {absolute,relative};
 }
 function draw(){
  host.replaceChildren();if(!mode)return;
  const heading=node('legend',mode==='scenarios'?'Named joint scenarios':'Selected Greeks and cross-gamma');host.append(heading);
  if(!caps){host.append(node('p','Joint risk is unavailable in this catalog. Refresh the selected financial context.'));return;}
  const named=mode==='scenarios',parameters=node('div');parameters.className='joint-risk-fields';host.append(parameters);
  if(named){input(parameters,'name','Scenario set name / identifier',true);input(parameters,'version','Scenario set version',true);}
  else{
   const policies=node('div');policies.className='joint-risk-fields';host.append(node('p','Each axis produces delta and diagonal gamma at half, nominal and double its entered bump. Select cross-gamma pairs explicitly below.'),policies);
   for(const kind of ['delta','gamma','mixed']){const label=kind==='mixed'?'Cross-gamma':kind==='gamma'?'Diagonal gamma':'Delta';input(policies,kind+'-absolute',label+' · absolute tolerance',kind!=='mixed'||state.mixed.length>0);input(policies,kind+'-relative',label+' · relative tolerance',kind!=='mixed'||state.mixed.length>0);}
   host.append(node('p','Absolute tolerances apply in each reported native sensitivity unit. Relative tolerances are dimensionless. Enter every policy explicitly.'));
  }
  const targetFields=node('div');targetFields.className='joint-risk-fields';host.append(targetFields);
  select(targetFields,'factor','Scenario target',targets.map(t=>({id:t.id,label:`${t.label} · ${t.unit}`})),true);
  if(targets.find(t=>t.id===value('factor'))?.kind==='delivery_bucket'){input(targetFields,'from','Delivery start',false,'date');input(targetFields,'to','Delivery end · exclusive',false,'date');}
  if(named){
   input(targetFields,'shift','Signed absolute shift');host.append(button('Add shift to this scenario','add-shift',()=>{
    if(state.staged.length>=caps!.maxShifts)throw Error(`A scenario accepts at most ${caps!.maxShifts} shifts.`);
    state.staged.push(chosenShift('shift',caps!.shiftConvention));state.fields.shift='';
   }));
   const staged=node('ol');state.staged.forEach((s,index)=>{const row=node('li',`${s.label} · ${s.shift.size} ${s.unit} `);row.append(button('Remove','remove-shift-'+index,()=>{state.staged.splice(index,1);}));staged.append(row);});host.append(staged);
   input(host,'label','Scenario label');host.append(button('Add named scenario to set','add-scenario',()=>{
    if(!value('label').trim()||!state.staged.length)throw Error('Enter a scenario label and add at least one shift.');
    if(state.scenarios.length>=caps!.maxScenarios)throw Error(`A set accepts at most ${caps!.maxScenarios} named scenarios.`);
    state.scenarios.push({id:crypto.randomUUID(),label:value('label').trim(),shifts:structuredClone(state.staged)});state.staged=[];state.fields.label='';
   }),button('Clear staged scenario','clear-staged',()=>{state.staged=[];state.fields.label='';state.fields.shift='';}));
   const list=node('ol');list.className='joint-risk-list';state.scenarios.forEach((s,index)=>{const row=node('li');row.append(node('strong',s.label),node('p',s.shifts.map(x=>`${x.label}: ${x.shift.size} ${x.unit}`).join('; ')),button('Remove scenario','remove-scenario-'+index,()=>{state.scenarios.splice(index,1);}));list.append(row);});host.append(list,node('p',`${state.scenarios.length} scenarios in this set. Each branches independently from the selected parent; no probability is assigned.`));
  }else{
   input(targetFields,'bump','Positive absolute bump');host.append(button('Add sensitivity axis','add-axis',()=>{
    if(state.axes.length>=caps!.maxAxes)throw Error(`Select at most ${caps!.maxAxes} sensitivity axes.`);
    state.axes.push({id:crypto.randomUUID(),target:chosenShift('bump',caps!.bumpConvention)});state.fields.bump='';
   }));
   const axes=node('ol');axes.className='joint-risk-list';state.axes.forEach((axis,index)=>{const row=node('li',`${axis.target.label} · ±${axis.target.shift.size} ${axis.target.unit} `);row.append(button('Remove axis and its pairs','remove-axis-'+index,()=>{state.axes.splice(index,1);state.mixed=state.mixed.filter(p=>p.firstAxis!==axis.id&&p.secondAxis!==axis.id);}));axes.append(row);});host.append(axes);
   const pairs=node('div');pairs.className='joint-risk-fields';const choices=state.axes.map(a=>({id:a.id,label:a.target.label}));select(pairs,'first','First axis',choices);select(pairs,'second','Second axis',choices);host.append(pairs,button('Add cross-gamma pair','add-pair',()=>{
    const first=value('first'),second=value('second');
    if(!state.axes.some(a=>a.id===first)||!state.axes.some(a=>a.id===second)||first===second)throw Error('Choose two distinct current sensitivity axes.');
    if(state.mixed.length>=caps!.maxMixed)throw Error(`Select at most ${caps!.maxMixed} mixed pairs.`);
    if(state.mixed.some(p=>p.firstAxis===first&&p.secondAxis===second||p.firstAxis===second&&p.secondAxis===first))throw Error('This unordered pair is already selected.');
    state.mixed.push({id:crypto.randomUUID(),firstAxis:first,secondAxis:second});
   }));
   const list=node('ol');state.mixed.forEach((pair,index)=>{const row=node('li',`${choices.find(a=>a.id===pair.firstAxis)?.label} × ${choices.find(a=>a.id===pair.secondAxis)?.label} `);row.append(button('Remove pair','remove-pair-'+index,()=>{state.mixed.splice(index,1);}));list.append(row);});host.append(list);
  }
 }
 function request():JointRequest{
  if(!caps||!mode)throw Error('Choose an available joint report and refresh its catalog.');
  const offered=new Set(targets.map(t=>t.id)),selected=mode==='scenarios'?state.scenarios.flatMap(s=>s.shifts):state.axes.map(a=>a.target);
  if(selected.some(s=>!offered.has(s.targetId)))throw Error('A selected joint target is no longer available. Remove or reselect it before running.');
  if(mode==='scenarios'){
   if(!value('name').trim()||!value('version').trim()||!state.scenarios.length)throw Error('Name and version the set, then add at least one named scenario.');
   if(state.staged.length||value('label').trim()||value('shift').trim())throw Error('Add the staged scenario to the set, or clear it before running.');
   return {convention:caps.convention,scenarios:{id:value('name').trim(),version:value('version').trim(),convention:caps.scenarioConvention,
    scenarios:state.scenarios.map(s=>({id:s.id,label:s.label,shifts:s.shifts.map(x=>structuredClone(x.shift))}))},derivatives:null};
  }
  if(!state.axes.length)throw Error('Add at least one sensitivity axis.');
  if(value('bump').trim())throw Error('Add or clear the staged axis before running.');
  return {convention:caps.convention,scenarios:null,derivatives:{convention:caps.derivativeConvention,
   axes:state.axes.map(a=>({id:a.id,bump:structuredClone(a.target.shift),deltaTolerance:tolerance('delta'),gammaTolerance:tolerance('gamma')})),
   mixed:state.mixed.map(p=>({...p,tolerance:tolerance('mixed')}))}};
 }
 return {show(next:typeof mode){mode=next;host.hidden=host.disabled=!mode;draw();},reset(){state=empty();targets=[];caps=undefined;draw();},
  load(next:JointCapabilities|undefined,offered:ScenarioTarget[]){caps=next?.convention==='portfolio_joint_risk_v1'?next:undefined;targets=offered;draw();},
  choices:()=>structuredClone(state),put(kept:JointChoices){state=structuredClone(kept);draw();},request};
}
