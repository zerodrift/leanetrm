import {exactRow,publicText,val,type Workspace} from './market-view.js';
import {renderRegularTable} from './regular-table.js';
import type {CurveConfiguration} from './market-navigation.js';

/** Exact retained native rows only. This inspector does not synthesize an editable definition. */
export function nativeCurveConfiguration(workspace:Workspace,code:string){
 const definition=exactRow(workspace.definitions.curves??[],'code',code);
 const members=(workspace.definitions.curve_members??[]).filter(row=>row.curve_code===code).sort((a,b)=>Number(a.ordinal)-Number(b.ordinal));
 const contracts=members.map(member=>({...exactRow(workspace.definitions.contracts??[],'id',val(member,'contract_id')),ordinal:member.ordinal}));
 const inputs=(workspace.definitions.curve_inputs??[]).filter(row=>row.curve_code===code);
 return {definition,contracts,inputs};
}
export function setupNativeCurveInspector(back:()=>void,derive:(code:string)=>void,busy:()=>boolean){
 const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text='')=>{const n=document.createElement(tag);n.textContent=publicText(text);return n;};
 const element=el('div'),leave=el('button','‹ All markets'),title=el('h2'),note=el('p'),terms=el('dl'),sources=el('div'),contracts=el('div'),create=el('button','Create derived curve');
 element.className='card native-curve-inspector';element.id='native-curve-inspector';leave.type='button';leave.className='text-button';leave.onclick=back;
 note.id='native-curve-inspector-reason';note.className='profile-note';terms.className='native-curve-terms';create.type='button';create.id='native-curve-derive';create.className='secondary';
 const help=el('p','A derived curve has its own code and saved graph. This source curve and its recorded prices keep their identity.');help.className='field-help';
 element.append(leave,title,el('p','Retained native configuration · read only'),note,terms,sources,contracts,create,help);
 return {element,show(workspace:Workspace,configuration:CurveConfiguration,name:string,canDerive:boolean){
  const read=nativeCurveConfiguration(workspace,configuration.curve);title.textContent=publicText(name);note.textContent=publicText(configuration.reason??'This retained native configuration is read-only.');
  terms.replaceChildren(...[['Curve code','code'],['Commodity','commodity_code'],['Location','location_code'],['Quote','quote_code'],['Delivery profile','profile_code'],['Settlement target','settlement_target_code'],['Price rule','rule'],['Mark source','direct_source_code'],['Mark role','direct_role']].flatMap(([label,key])=>[el('dt',label),el('dd',val(read.definition,key))]));
  sources.hidden=!read.inputs.length;
  if(read.inputs.length)renderRegularTable(sources,'native-curve-inputs',JSON.stringify(read.inputs),['Input','Source curve','Expiry policy'],read.inputs.map(row=>[val(row,'slot'),val(row,'source_curve_code'),val(row,'expiry_rule')]),[],busy);
  renderRegularTable(contracts,'native-curve-contracts',JSON.stringify(read.contracts),['Position','Kind','Delivery start','Delivery end · excluded','Expiry · UTC','Futures series'],read.contracts.map(row=>['ordinal','kind','delivery_from','delivery_to','expiry_at','futures_series_code'].map(key=>val(row,key))),[0],busy);
  create.hidden=help.hidden=!canDerive;create.disabled=busy();create.onclick=()=>{if(!busy())derive(configuration.curve);};
 }};
}
