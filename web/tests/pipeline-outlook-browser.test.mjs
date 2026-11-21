import assert from 'node:assert/strict';
import test from 'node:test';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const setup=`const {setupPipelineOutlook}=await import('/pipeline-outlook.js');
const root=document.querySelector('main'),at='2021-11-07T06:30:07Z',end='2021-11-07T06:30:14Z',knownAt='2021-11-07T07:30:07.123456Z';
const base={pipeline:'Synthetic line',product:'Gas',unit:'MMBtu',direction:'receive',referenceCatalogId:'catalog',zone:'North'};
const stage={reported:0,provisional:0,withdrawn:0,quantity:null,reportedQuantity:null};
const row=rev=>({path:{...base,referenceRevisionId:rev,point:'Point '+rev},period:'2021-11-07',occurrences:1,contracted:'25',nominated:stage,submitted:stage,confirmed:stage,scheduled:stage,allocated:stage});
const source=r=>({path:r.path,day:r.period,start:at,end,reference:'Trade '+r.path.referenceRevisionId,side:'Receipt',tradeId:'booked-'+r.path.referenceRevisionId,revision:4,contracted:'25',nominated:null,confirmed:null,scheduled:null,allocated:null,submitted:{quantity:'23',status:'final'},requests:[{externalReference:'Request A',submittedAt:knownAt}]});
let actor='first',busy=false,reason=null,work,opened=[],errors=[];
const act=async fn=>{if(busy)return;busy=true;try{await fn();}catch(e){errors.push(e.message);}finally{busy=false;}};
const widget=setupPipelineOutlook(root,{context:()=>({effectiveOn:'2021-11-01'}),identity:()=>actor,zone:()=>'America/Chicago',busy:()=>busy,
 act:fn=>work=act(fn),tradeInspection:()=>reason,openTrade:(id,revision)=>act(async()=>opened.push([id,revision])),
 api:async(_,p)=>{const rows=(p.from==='2021-11-01'?['A','B']:['B']).map(row);return {at,context:{knownAt},rows,pipelines:rows,sources:rows.map(source),balances:[],entitlements:[{id:'agreement',label:'Agreement'}],
 capacity:p.entitlementId?{ready:true,unit:'MMBtu',tables:[{columns:['Start','End'],rows:[[at,end]],instantColumns:[0,1]}]}:null};}},()=>{});
await widget.load();
const click=id=>document.querySelector(id).click(),panel=()=>root.querySelector('.outlook-report').innerText,filter=()=>root.querySelector('#outlook-filters select');`;
const browser={skip:!browserAvailable,timeout:browserTestTimeout};

test('booked Side links open the native revision and present a missing-context refusal',browser,()=>withBrowserPage('<main></main>',async evaluate=>{
 const result=await evaluate(`(async()=>{${setup}
 click('#outlook-views-sources');click('.outlook-report tbody .outlook-drill');await work;
 reason='Choose a close to inspect the trade.';click('.outlook-report tbody .outlook-drill');await work;return {opened,errors};})()`);
 assert.deepEqual(result,{opened:[['booked-A',4]],errors:['Choose a close to inspect the trade.']});
}));

test('the outlook displays native seconds, fractional knowledge and submission times, and both fall-back offsets',browser,()=>withBrowserPage('<main></main>',async evaluate=>{
 const result=await evaluate(`(async()=>{${setup}
 click('#outlook-views-sources');const sources=panel(),status=root.querySelector('.outlook-asof').innerText;
 click('#outlook-views-capacity');const entitlement=root.querySelector('[aria-label="Transport entitlement"]');
 entitlement.value='agreement';entitlement.dispatchEvent(new Event('change'));await work;return {sources,status,capacity:panel()};})()`);
 for(const text of [result.sources,result.capacity]){assert.match(text,/01:30:07 UTC-05:00/);assert.match(text,/01:30:14 UTC-05:00/);}
 assert.match(result.sources,/Request A · .*01:30:07\.123456 UTC-06:00/);
 assert.match(result.status,/01:30:07 UTC-05:00.*Known at .*01:30:07\.123456 UTC-06:00/);
}));

test('an explicit pipeline revision persists through an empty window within its account',browser,()=>withBrowserPage('<main></main>',async evaluate=>{
 const result=await evaluate(`(async()=>{${setup}
 const selected=filter().options[1].value;filter().value=selected;filter().dispatchEvent(new Event('change'));
 root.querySelector('#outlook-from').value='2021-11-02';click('#outlook-read');await work;
 const empty={value:filter().value,label:filter().selectedOptions[0].textContent,body:panel()};
 actor='second';await widget.load();const other={value:filter().value,labels:[...filter().options].map(o=>o.textContent)};
 return {selected,empty,other};})()`);
 assert.equal(result.empty.value,result.selected);assert.match(result.empty.label,/Synthetic line · A/);
 assert.match(result.empty.body,/No booked gas Sides in this window/);
 assert.equal(result.other.value,'');assert.ok(!result.other.labels.includes(result.empty.label));
}));
