import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
import {transform} from 'esbuild';
import {createAsyncReadClient} from '../dist/async-read-client.js';

// Execute the actual account-refresh entry points while access is pending. The
// isolated view stubs have no account or database; SSE uses the real dispatcher.
const source=await readFile(new URL('../src/app.ts',import.meta.url),'utf8');
const functions=['cancelCurveRead','openProfile','loadIdentity'].map(name=>{
 const match=source.match(new RegExp(`(?:async )?function ${name}\\([^\\n]*?\\{[^]*?\\n\\}`));
 if(name==='cancelCurveRead')return source.match(/function cancelCurveRead\(\)\{[^\n]+\}/)[0];
 assert.ok(match,`Missing account entry point ${name}`);return match[0];
});
const script=(await transform(functions.join('\n'),{loader:'ts',target:'es2022'})).code;
const switchScript=(await transform([
 source.match(/^function renderProfileIdentity[^\n]+/m)?.[0],
 source.match(/^async function profileChoices[^\n]+/m)?.[0],
 source.match(/^\$\('open'\)\.onclick=[^\n]+/m)?.[0],
].map(value=>{assert.ok(value,'Missing account switch entry point');return value;}).join('\n'),{loader:'ts',target:'es2022'})).code;
const flush=()=>new Promise(resolve=>setImmediate(resolve));
class Source extends EventTarget{
 closed=false;
 close(){this.closed=true;}
 emit(type,value){this.dispatchEvent(new MessageEvent(type,{data:JSON.stringify(value)}));}
}

test('permission refresh retires pending reads before temporarily clearing account context',async()=>{
 const nodes=new Map(),node=id=>{if(!nodes.has(id))nodes.set(id,{dataset:{},replaceChildren(){},closest(){return this;}});return nodes.get(id);};
 const noop=()=>{},curveController=new AbortController(),pageController=new AbortController();
 let locationResets=0,parkLoanResets=0,transportResets=0,pointResets=0,markResets=0;const productResets=[];
 const context={currentProfile:'synthetic-reader',viewHistory:[{}],inspectedContexts:new Map([['old',{}]]),navigation:{context:{contextId:'synthetic-retained-context',knownThrough:4}},
  permissions:['ReadMarketData'],marketGeneration:0,curveRead:curveController,pageRead:{controller:pageController},
  $:node,select:node,choose:noop,clearPrices:noop,clearReference:noop,setCurveNames:noop,renderBlotterViews:noop,scheduleWorkspace:undefined,
  gasLocations:{reset(){locationResets++;}},parkLoan:{reset(){parkLoanResets++;}},transportTicket:{reset(){transportResets++;}},pointTraderMark:{reset(){pointResets++;}},curveMarks:{reset(){markResets++;}}};
 for(const name of ['recInventory','structuredPower','closeConventionDefaults','lifecycle','physicalOperations','deliverySetup','referenceConsole','curveWorkspace','customCurves',
  'volSurfaces','optionChains','riskJobs','riskViewsNav','facilities','facilityViewsNav','jobs','dashboard','deliveryPrices','intervalSeries','intervalCurves','ppa','gas','crude',
  'lng','gasSetup','crudeSetup','referenceEditor','parties','unitConverter','futures','power','touProducts','schedules','dateEditor','priceSetup',
  'curveDefinitions','administration','aiSettings','agentGrants','referenceNavigation','marketCalendar','accessPolicy','assistantPolicy'])
  context[name]={reset:noop,invalidate:noop,clear:noop};
 for(const name of ['recInventory','structuredPower','optionChains'])context[name]={reset:()=>productResets.push(name)};
 let stream,submitted,accessReads=0,denyAccess;
 const heldAccess=new Promise((resolve,reject)=>{denyAccess=reject;});
 context.api=async path=>{assert.equal(path,'access');return ++accessReads===1?{permissions:['ReadMarketData','ReadTrading']}:heldAccess;};
 const protocol='async_reads_v1',channelId='10000000-0000-4000-8000-000000000001';
 context.readClient=createAsyncReadClient({getIdentity:()=>({profile:context.currentProfile,context:context.navigation?.context??null}),
  post:async(path,body)=>{
   if(path.endsWith('/channels'))return {protocol,channelId,profile:context.currentProfile,context:context.navigation.context,admissionWindowMs:230000};
   if(path.endsWith('/cancel'))return {...body,state:'cancellation_requested'};
   submitted=body;return {channelId,requestId:body.requestId,state:'accepted'};
  },createEventSource:()=>stream=new Source()});
 vm.createContext(context);vm.runInContext(script,context);
 const pending=context.readClient.read('curve.shapes.v1',{snapshotId:'20000000-0000-4000-8000-000000000001'}).then(()=> 'installed',error=>error.code);
 let refreshing;
 try{
  await flush();stream.emit('ready',{protocol,channelId});await flush();assert.ok(submitted);
  refreshing=context.openProfile().catch(error=>error.message);await flush();
  assert.equal(accessReads,2,'a changed permission list must enter identity refresh and await fresh access');
  assert.equal(locationResets,1);assert.equal(node('gas-locations-page').hidden,true);
  assert.equal(parkLoanResets,1,'the Park and loan ticket must release the previous account draft');
  assert.equal(transportResets,1,'the Transport ticket must release the previous account draft');
  assert.deepEqual(productResets.sort(),['optionChains','recInventory','structuredPower'],'REC, power and options must each release the prior account state before access returns');
  assert.equal(node('rec-inventory-page').hidden,true,'the previous account REC view must be hidden while access is pending');
  assert.equal(node('physical-power-page').hidden,true,'the previous account power view must be hidden while access is pending');
  assert.equal(node('options-page').hidden,true,'the previous account option view must be hidden while access is pending');
  assert.equal(markResets,1,'the curve marks view must release the previous account state');
  assert.equal(node('curve-marks-page').hidden,true,'the previous account curve marks view must be hidden while access is pending');
  assert.equal(pointResets,1,'the market publisher must release the previous account view and retain only its actor-bound recovery');
  assert.equal(context.navigation,undefined,'the new account metadata is still loading');
  stream.emit('response',{protocol,channelId,requestId:submitted.requestId,operation:submitted.operation,state:'completed',snapshotId:submitted.body.snapshotId,result:{curves:[]}});
  assert.equal(await pending,'async_read_reset','an intentional refresh must not appear to be an unexpected authority change');
  assert.equal(stream.closed,true);assert.equal(curveController.signal.aborted,true);assert.equal(pageController.signal.aborted,true);
  assert.equal(context.pageRead,undefined,'the superseded page cannot publish a late error');
 }finally{
  context.readClient.close();denyAccess(Error('Synthetic access read ended'));await refreshing;
 }
});

for(const admitted of [true,false])test(admitted?'an accepted actor is named even when its selected market context cannot load':'a refused actor switch keeps the original identity',async()=>{
 const nodes=new Map(),node=id=>{if(!nodes.has(id))nodes.set(id,{value:'',textContent:'',hidden:false,open:true,selectedOptions:[],setAttribute(name,value){this[name]=value;},close(){this.open=false;}});return nodes.get(id);};
 node('profile').options=[{value:'viewer',textContent:'Viewer'},{value:'trader',textContent:'Trader'}];node('profile').value='trader';node('profile-label').textContent='Viewer';node('profile-title').textContent='Viewer';
 let failure,identityLoads=0,backs=0;
 const context={currentProfile:'viewer',permissions:['ReadMarketData'],bootstrapPath:'bootstrap',hostedPilot:false,
  $:node,select:node,text(id,value){node(id).textContent=value;},friendly:value=>value,
  choose(id,options,value){node(id).options=options.map(option=>({value:option.value,textContent:option.name}));const selected=options.find(option=>option.value===value);node(id).value=value;node(id).selectedOptions=selected?[{textContent:selected.name}]:[];},
  deadlines:{read(){}},setHostMode(){},mayLeaveForDialog:async()=>true,
  futures:{ended(){}},savedPreferences:{invalidate(){}},blotterBack:async()=>{backs++;},
  profileAction:async(_control,work)=>{try{await work();}catch(error){failure=error.message;}},
  api:async(path,body)=>{if(path==='profile'){assert.equal(body.profile,'trader');if(!admitted)throw Error('Synthetic actor admission denied');return {};}
   assert.equal(path,'bootstrap');return {profiles:[{id:'viewer',name:'Viewer'},{id:'trader',name:'Trader'}],customProfiles:[],requestDeadlines:{},mode:'preview'};},
  loadIdentity:async()=>{identityLoads++;context.permissions=['ReadMarketData','BookTrade'];throw Error('Choose a market date above.');}};
 vm.createContext(context);vm.runInContext(switchScript,context);node('open').onclick();
 for(let i=0;i<8&&failure===undefined;i++)await flush();
 assert.equal(context.currentProfile,admitted?'trader':'viewer');
 assert.equal(node('profile-label').textContent,admitted?'Trader':'Viewer','the header must name the actor whose admission succeeded');
 assert.equal(node('profile-title').textContent,admitted?'Trader':'Viewer');
 assert.equal(identityLoads,admitted?1:0);assert.equal(backs,admitted?0:1);
 assert.equal(failure,admitted?'Choose a market date above.':'Synthetic actor admission denied','the original source or admission refusal remains visible');
 assert.equal(node('profile-dialog').open,true,'a context refusal must not look like a completed dialog action');
 if(admitted)assert.match(node('profile-access').textContent,/Book Trade/);
});
