import test from 'node:test';
import assert from 'node:assert/strict';
import {draftShapes,curveSelectionKey,readCurveShapes,readInputCurveShapes} from '../dist/curve-shapes.js';

const shape=(curve,definitionRevisionId='r1')=>({definitionRevisionId,curve,unit:'USD/MMBtu (IT)',unavailableReason:null,points:[{contractId:'k1',deliveryFrom:'2021-01-01',deliveryTo:'2021-02-01',price:'3.49',unavailableReason:null}]});
const failed='Curve shape unavailable · the curve read failed';
// The draft's source of shapes, with the curves the desk read and the revision it read them for, and a counter of the reads made for the draft.
function source({held,deskRevision='r1',answer=(curve,revision)=>shape(curve,revision)}={}){
 const reads=[],selections=[];
 const exact=held?new Map([...held].map(([curve,value])=>[curveSelectionKey({curve,definitionRevisionId:deskRevision}),typeof value==='object'?{...value,definitionRevisionId:deskRevision}:value])):undefined;
 const shapes=draftShapes({desk:async()=>exact,read:async market=>{reads.push(market.curve);selections.push(market);return answer(market.curve,market.definitionRevisionId);}});
 return {shapes,reads,selections};
}

test('a market the desk holds the curve of is drawn from the desk\'s own read, and no curve is read for the draft',async()=>{
 const held=new Map([['gas_forward',shape('gas_forward')]]),{shapes,reads}=source({held});
 assert.deepEqual(await shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'}),held.get('gas_forward'));
 assert.deepEqual(reads,[]);
});

test('the desk\'s curves stand for a draft only in the revision they were read for: for any other revision of the close the curve is read for the draft',async()=>{
 const held=new Map([['gas_forward',shape('stale')]]),{shapes,reads}=source({held,deskRevision:'r0',revision:'r1'});
 assert.equal((await shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'})).curve,'gas_forward','not the curve the desk held for the other revision');
 assert.deepEqual(reads,['gas_forward']);
});

test('a market the desk does not hold is read once, and the next draft of it reads nothing',async()=>{
 const {shapes,reads}=source({held:new Map()});
 const first=await shapes.shape({curve:'power_forward',definitionRevisionId:'r1'});
 assert.equal(first.curve,'power_forward');
 assert.equal(await shapes.shape({curve:'power_forward',definitionRevisionId:'r1'}),first);
 assert.deepEqual(reads,['power_forward']);
});

test('drafts that ask for the same market at once share one read',async()=>{
 const {shapes,reads}=source({held:new Map()});
 const [a,b]=await Promise.all([shapes.shape({curve:'power_forward',definitionRevisionId:'r1'}),shapes.shape({curve:'power_forward',definitionRevisionId:'r1'})]);
 assert.equal(a,b);assert.deepEqual(reads,['power_forward']);
});

test('the desk\'s own read still under way is waited for, so that a curve it is about to hold is not read again',async()=>{
 let arrive;const reads=[];
 const shapes=draftShapes({desk:()=>new Promise(resolve=>{arrive=resolve;}),read:async market=>{reads.push(market.curve);return shape(market.curve,market.definitionRevisionId);}});
 const asked=shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'});
 await new Promise(resolve=>setImmediate(resolve));
 assert.deepEqual(reads,[],'nothing is read while the desk may still hold it');
 const held=shape('gas_forward');arrive(new Map([[curveSelectionKey({curve:'gas_forward',definitionRevisionId:'r1'}),held]]));
 assert.equal(await asked,held);assert.deepEqual(reads,[]);
});

test('no desk read at all, as in a pivot, leaves every market to be read',async()=>{
 const {shapes,reads}=source({held:undefined});
 await shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'});
 assert.deepEqual(reads,['gas_forward']);
});

test('a desk read that failed is not held against the draft: its curve is read for the draft, and the draft is answered',async()=>{
 const reads=[];
 const shapes=draftShapes({desk:async()=>{throw Error('the desk\'s read failed');},read:async market=>{reads.push(market.curve);return shape(market.curve,market.definitionRevisionId);}});
 assert.equal((await shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'})).curve,'gas_forward');
 assert.deepEqual(reads,['gas_forward']);
});

test('a curve the desk holds only as the reason it has none is read for the draft, since that reason may have passed',async()=>{
 const held=new Map([['gas_forward','Curve shape unavailable · the curve read failed']]),{shapes,reads}=source({held});
 assert.equal((await shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'})).curve,'gas_forward');
 assert.deepEqual(reads,['gas_forward']);
});

test('a read that failed is not kept: the next draft asks again',async()=>{
 const answers=[failed,shape('gas_forward')],{shapes,reads}=source({held:new Map(),answer:()=>answers.shift()});
 assert.equal(await shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'}),failed);
 assert.equal((await shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'})).curve,'gas_forward');
 assert.deepEqual(reads,['gas_forward','gas_forward']);
});

test('a read that throws is answered with the reason, never thrown at the draft, and is not kept either',async()=>{
 let attempts=0;
 const {shapes,reads}=source({held:new Map(),answer:curve=>{if(attempts++===0)throw Error('down');return shape(curve);}});
 assert.equal(await shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'}),failed);
 assert.equal((await shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'})).curve,'gas_forward');
 assert.deepEqual(reads,['gas_forward','gas_forward']);
});

test('a curve the close itself cannot shape is a shape that says so, and is kept',async()=>{
 const none={curve:'power_euro',unit:null,points:null,unavailableReason:'missing_curve'},{shapes,reads}=source({held:new Map(),answer:()=>none});
 assert.equal(await shapes.shape({curve:'power_euro',definitionRevisionId:'r1'}),none);assert.equal(await shapes.shape({curve:'power_euro',definitionRevisionId:'r1'}),none);
 assert.deepEqual(reads,['power_euro']);
});

test('different exact definitions of one curve are independently read and cached',async()=>{
 const {shapes,selections}=source({held:new Map()});
 const first=await shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'});
 const second=await shapes.shape({curve:'gas_forward',definitionRevisionId:'r2'});
 assert.equal(first.definitionRevisionId,'r1');assert.equal(second.definitionRevisionId,'r2');
 assert.notEqual(first,second);
 assert.equal(await shapes.shape({curve:'gas_forward',definitionRevisionId:'r2'}),second);
 assert.deepEqual(selections,[{curve:'gas_forward',definitionRevisionId:'r1'},{curve:'gas_forward',definitionRevisionId:'r2'}]);
});

test('missing exact definition refuses the chart without substituting the page revision',async()=>{
 const {shapes,reads}=source();
 assert.equal(await shapes.shape({curve:'gas_forward',definitionRevisionId:''}),'Curve shape unavailable · the exact market definition is missing');
 assert.deepEqual(reads,[]);
});

test('a reset forgets what was read, for the desk has read its curves again',async()=>{
 const {shapes,reads}=source({held:new Map()});
 await shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'});shapes.reset();await shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'});
 assert.deepEqual(reads,['gas_forward','gas_forward']);
});

test('a read that began before a reset does not answer for what comes after it',async()=>{
 let release;const reads=[];
 const shapes=draftShapes({desk:async()=>new Map(),read:async market=>{reads.push(market.curve);if(reads.length===1)await new Promise(resolve=>{release=resolve;});return shape(market.curve,market.definitionRevisionId);}});
 const early=shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'});
 await new Promise(resolve=>setImmediate(resolve));
 shapes.reset();
 const late=shapes.shape({curve:'gas_forward',definitionRevisionId:'r1'});
 await new Promise(resolve=>setImmediate(resolve));
 release();await Promise.all([early,late]);
 assert.deepEqual(reads,['gas_forward','gas_forward'],'the read made after the reset is its own');
});

// The one backend read that shapes curves, which the desk makes for the positions it holds and a draft makes for a market it does not.
test('the shapes of some curves are read at a close, in the context the page reads in, and each is kept by its curve',async()=>{
 const calls=[],api=async(path,body)=>{calls.push([path,body]);return {curves:[shape('gas_forward'),shape('stray')]};};
 const found=await readCurveShapes(api,{snapshotId:'s1'},['gas_forward','power_forward']);
 assert.deepEqual(calls,[['market/curve-shapes',{snapshotId:'s1',curves:['gas_forward','power_forward']}]]);
 assert.deepEqual([...found.keys()],['gas_forward'],'a curve that was not asked for is dropped, and one that was not returned has no entry');
 assert.equal(found.get('gas_forward').curve,'gas_forward');
});

test('shapes read in another market context than the page\'s are not given: each curve says so',async()=>{
 const other=async()=>({curves:[shape('gas_forward')],context:{contextId:'c9'}});
 const found=await readCurveShapes(other,{snapshotId:'s1',context:{contextId:'c1'}},['gas_forward']);
 assert.equal(found.get('gas_forward'),'Curve shape unavailable · the curves were read in another market context');
 const same=await readCurveShapes(async()=>({curves:[shape('gas_forward')],context:{contextId:'c1'}}),{snapshotId:'s1',context:{contextId:'c1'}},['gas_forward']);
 assert.equal(same.get('gas_forward').curve,'gas_forward');
 const working=await readCurveShapes(async()=>({curves:[shape('gas_forward')],context:{contextId:'c1'}}),{snapshotId:'s1'},['gas_forward']);
 assert.equal(working.get('gas_forward'),'Curve shape unavailable · the curves were read in another market context','working data states no context, and a read that states one is not it');
});

test('a read that fails leaves every curve with the reason, a denial said as the account\'s and not as a failure, and never fails',async()=>{
 const failing=async()=>{throw Error('down');},denied=async()=>{throw Object.assign(Error('no'),{code:'access_denied'});};
 assert.equal((await readCurveShapes(failing,{snapshotId:'s1'},['a','b'])).get('b'),'Curve shape unavailable · the curve read failed');
 assert.equal((await readCurveShapes(denied,{snapshotId:'s1'},['a'])).get('a'),'Curve shape unavailable · this account cannot read market data');
});

test('input inspection requests and matches exact definitions, including the same code in two versions',async()=>{
 const markets=[{curve:'gas_forward',definitionRevisionId:'r1'},{curve:'gas_forward',definitionRevisionId:'r2'}],calls=[];
 const api=async(path,body)=>{calls.push([path,body]);return {curves:[shape('gas_forward','r2'),shape('gas_forward','r1')]};};
 const found=await readInputCurveShapes(api,{snapshotId:'s1'},markets);
 assert.deepEqual(calls,[['market/input-curve-shapes',{snapshotId:'s1',curves:markets}]]);
 for(const market of markets)assert.equal(found.get(curveSelectionKey(market)).definitionRevisionId,market.definitionRevisionId);
 const wrong=await readInputCurveShapes(async()=>({curves:[shape('gas_forward','other')]}),{snapshotId:'s1'},[markets[0]]);
 assert.equal(wrong.get(curveSelectionKey(markets[0])),'Curve shape unavailable · the read did not return this exact curve definition');
});

test('draft delivery annotations do not enter the exact input inspection contract',async()=>{
 const draft={curve:'gas_forward',definitionRevisionId:'r1',deliveryFrom:'2020-12-01',deliveryTo:'2021-01-01'},calls=[];
 const found=await readInputCurveShapes(async(path,body)=>{calls.push([path,body]);return {curves:[shape('gas_forward','r1')]};},{snapshotId:'s1'},[draft]);
 assert.deepEqual(calls,[['market/input-curve-shapes',{snapshotId:'s1',curves:[{curve:'gas_forward',definitionRevisionId:'r1'}]}]]);
 assert.equal(found.get(curveSelectionKey(draft)).curve,'gas_forward');
});
