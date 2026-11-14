export const fixture=`
const {mountGasOwnedAction}=await import('/gas-owned-lifecycle.js');
window.sent=[];window.gasCurrent=true;window.gasZone='America/Chicago';
const setup={context:{contextId:'source-1',asOf:'2026-11-06T22:00:00Z',businessDate:'2026-11-06',snapshotId:'snapshot-1'},
 accounts:[{id:'account-1',label:'Synthetic owned account'}],policies:[{id:'policy-1',revision:2,accountId:'account-1',action:'gas.owned.receive_title',label:'Observed title · revision 2'}],
 evidence:[{id:'evidence-1',label:'Synthetic observed title · source revision 3'}],lots:[],reservations:[],loans:[],positions:[],fees:[],parties:[],
 choices:{carrying:[{id:'nominal_mark_v1',label:'Nominal marked value'}],titleOrigins:[{id:'opening_owned_stock_v1',label:'Explicit owned opening stock'}]}};
setup.forms={'gas.owned.receive_title':{label:'Accept title to gas',permission:'RecordGasOwnedHoldings',fields:[
 {name:'evidenceId',label:'Observed title evidence',kind:'select',nullable:false,choices:setup.evidence},
 {name:'quantity',label:'Observed quantity',kind:'text',nullable:false},
 {name:'titleAt',label:'Title transferred at',kind:'instant',nullable:false},
 {name:'availableAt',label:'Available at',kind:'instant',nullable:false},
 {name:'originPolicy',label:'Title origin',kind:'select',nullable:false,choices:setup.choices.titleOrigins},
 {name:'carrying',label:'Carrying value',kind:'select',nullable:false,choices:setup.choices.carrying}]}};
setup.configuration={};
const offer={target:{handler:'gas_owned',scopeId:'account-1',bindingId:'account-1'},action:'gas.owned.receive_title',label:'Accept title to gas',
 occurrenceId:'account-1',at:setup.context.asOf,before:null,eligible:true,reason:null,revision:1,stateVersion:1,selection:null,requiredInputs:[]};
const api=async(path,body)=>{if(path!=='time/inspect')throw Error('Unexpected operation');return {candidates:[{instant:'2026-11-06T18:00:00Z',offset:'-06:00'}]};};
const options={api,zone:()=>gasZone,permission:()=>true,current:()=>gasCurrent,changed:()=>{},act:async work=>work(),queue:async(path,body)=>sent.push({path,body}),confirmDiscard:async()=>true};
window.editor=mountGasOwnedAction(document.querySelector('#gas-form'),offer,setup,options);
window.fillGas=()=>{for(const [key,value] of Object.entries({policyId:'policy-1:2',evidenceId:'evidence-1',quantity:'0.333333333333333333',
 titleAt:'2026-11-06T12:00:00',availableAt:'2026-11-06T12:00:00',originPolicy:'opening_owned_stock_v1',carrying:'nominal_mark_v1'})){
 const control=document.querySelector('[name="'+key+'"]');control.value=value;control.dispatchEvent(new Event('input',{bubbles:true}));control.dispatchEvent(new Event('change',{bubbles:true}));}};
`;
export const batchFixture=fixture.replace("const api=async(path,body)=>{", "const api=async(path,body)=>{if(path==='lifecycle/gas/setup'){batchReads.push(body);return await batchRead(body);}")+`
window.batchReads=[];offer.action='gas.futures.reconcile_quantity';offer.target={handler:'gas_futures_reconciliation_batch',scopeId:'account-1',bindingId:'account-1'};
setup.policies[0].action=offer.action;
const target=id=>({handler:'gas_futures_reconciliation',scopeId:'delivery-'+id,bindingId:id});
window.memberFields=id=>[
 {name:'observedPerformance',label:'Observed performance revision',kind:'reference',nullable:false,members:['positionId','performanceId','expectedRevision'],choices:[{id,label:id+' revision 3',value:{positionId:'delivery-'+id,performanceId:id,expectedRevision:3}}]},
 {name:'principalVersion',label:'Principal revision',kind:'reference',nullable:false,members:['expectedPrincipalVersion'],choices:[{id:'4',label:'Principal revision 4',value:{expectedPrincipalVersion:4}}]},
 {name:'quantity',label:'Observed quantity',kind:'text',nullable:false},
 {name:'retracted',label:'Withdraw quantity',kind:'select',nullable:false,choices:[{id:'false',label:'Retain quantity',value:false}]},
 {name:'titleEvidenceId',label:'Title evidence',kind:'select',nullable:true,choices:[]},
 {name:'sourceAllocation',label:'Source allocation',kind:'select',nullable:false,choices:[{id:'preserve',label:'Preserve unallocated quantity',value:'preserve_unallocated_source_quantity_v1'}]},
 {name:'evidence',label:'Evidence reference',kind:'text',nullable:false},
 {name:'supply',label:'Identified supply',kind:'array',nullable:false,fields:[
  {name:'stockUse',label:'Stock use revision',kind:'reference',nullable:false,members:['consumptionId','expectedRevision'],choices:[{id:'use-'+id,label:'Stock use '+id,value:{consumptionId:'use-'+id,expectedRevision:2}}]},
  {name:'lotId',label:'Owned lot',kind:'select',nullable:false,choices:[{id:'lot-'+id,label:'Owned lot '+id}]},
  {name:'evidenceId',label:'Delivery evidence',kind:'select',nullable:false,choices:[{id:'evidence-'+id,label:'Delivery evidence '+id}]},
  {name:'reservationId',label:'Reservation',kind:'select',nullable:true,choices:[]},
  {name:'quantity',label:'Quantity',kind:'text',nullable:false},
  {name:'retracted',label:'Withdraw use',kind:'select',nullable:false,choices:[{id:'false',label:'Retain use',value:false}]}]}];
window.batchRead=async body=>({...setup,target:body.target,forms:{[offer.action]:{label:'Correct quantity',permission:'RecordGasOwnedHoldings',fields:memberFields(body.target.bindingId)}}});
setup.forms={[offer.action]:{label:'Correct linked quantities',permission:'RecordGasOwnedHoldings',fields:[{name:'corrections',label:'Observed corrections',kind:'array',nullable:false,minItems:1,fields:[],variants:['a','b'].map(id=>({id,label:'Observed '+id,target:target(id)}))}]}};
document.querySelector('#gas-form').replaceChildren();window.editor=mountGasOwnedAction(document.querySelector('#gas-form'),offer,setup,options);
window.addMember=id=>{const mode=document.querySelector('[name=corrections]');if(mode.value!=='entries'){mode.value='entries';mode.dispatchEvent(new Event('change',{bubbles:true}));}const add=mode.parentElement.querySelector(':scope > button');add.click();const row=[...document.querySelectorAll('[data-gas-entry=corrections]')].at(-1);const selected=row.querySelector('[name=corrections-member]');selected.value=id;selected.dispatchEvent(new Event('change',{bubbles:true}));};
window.fillMember=id=>{const row=[...document.querySelectorAll('[data-gas-entry=corrections]')].find(r=>r.querySelector('[name=corrections-member]').value===id);const set=(host,name,value)=>{const n=host.querySelector('[name="'+name+'"]');n.value=value;n.dispatchEvent(new Event('change',{bubbles:true}));};
 for(const [name,value]of Object.entries({observedPerformance:id,principalVersion:'4',quantity:'0.125',retracted:'false',titleEvidenceId:'@null',sourceAllocation:'preserve',evidence:'Synthetic correction '+id,supply:'entries'}))set(row,name,value);
 [...row.querySelectorAll('button')].find(b=>b.textContent==='Add entry').click();const stock=row.querySelector('[data-gas-entry=supply]');for(const[name,value]of Object.entries({stockUse:'use-'+id,lotId:'lot-'+id,evidenceId:'evidence-'+id,reservationId:'@null',quantity:'0.125',retracted:'false'}))set(stock,name,value);
};
`;
