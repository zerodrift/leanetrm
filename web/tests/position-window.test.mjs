import test from 'node:test';
import assert from 'node:assert/strict';
import {positionWindowRequest,positionWindowUrl,readPositionWindowUrl,openPositionWindow,positionWindowNames} from '../dist/position-window-selection.js';
import {readSelectedPosition} from '../dist/position-facts.js';

const selection={snapshotId:'10000000-0000-0000-0000-000000000001',context:{contextId:'20000000-0000-0000-0000-000000000002',effectiveOn:'2031-03-12',knownAt:'2031-03-12T18:45:00.1234560Z'},
 asOf:'2031-03-12T16:00:00+00:00',tradeId:'30000000-0000-0000-0000-000000000003',bindingId:'40000000-0000-0000-0000-000000000004',revision:2,instrumentType:'ppa',zone:'America/Chicago',example:'baseline'};

test('a separate trade window always reads the exact retained selection, without recapturing current knowledge',()=>{
 assert.deepEqual(positionWindowRequest(selection),{context:{snapshotId:selection.snapshotId,convention:'trading_workspace_v1',knowledgeMode:'retained',contextId:selection.context.contextId},
  tradeId:selection.tradeId,bindingId:selection.bindingId,revision:2,instrumentType:'ppa'});
});
test('opening requests a separate popup without an opener, and a null browser result never becomes a false blocked warning',()=>{
 const calls=[];assert.doesNotThrow(()=>openPositionWindow('https://example.invalid/',selection,(...args)=>{calls.push(args);return null;}));
 assert.equal(calls.length,1);assert.equal(calls[0][1],'_blank');
 for(const feature of ['popup=yes','noopener','noreferrer'])assert.ok(calls[0][2].split(',').includes(feature));
 assert.deepEqual(readPositionWindowUrl(calls[0][0]),selection);
});

test('separate-window references round-trip in the fragment without credentials, opener state or a current-context fallback',()=>{
 const url=positionWindowUrl('https://example.invalid/app?example=exchange',selection),parsed=new URL(url);
 assert.equal(parsed.pathname,'/position.html');assert.equal(parsed.search,'');assert.equal(parsed.username,'');assert.equal(parsed.password,'');
 assert.deepEqual(readPositionWindowUrl(url),selection);assert.equal(new URLSearchParams(parsed.hash.slice(1)).has('csrf'),false);
});
test('malformed, duplicate or missing window context refuses explicitly',()=>{
 const base=positionWindowUrl('https://example.invalid/',selection);
 for(const change of [u=>u.hash+='&context='+selection.context.contextId,u=>u.hash=u.hash.replace('type=ppa','type=unknown'),
  u=>u.hash=u.hash.replace('revision=2','revision=0'),u=>u.hash=u.hash.replace('snapshot='+selection.snapshotId,'snapshot='),
  u=>u.hash+='&knowledgeMode=current',u=>u.search='?context=current',u=>u.hash=u.hash.replace('zone=America%2FChicago','zone=not-a-zone'),
  u=>u.hash=u.hash.replace('effective=2031-03-12','effective=2031-03-13')]){
  const url=new URL(base);change(url);assert.throws(()=>readPositionWindowUrl(url.href),/exact position and retained market context/);
 }
});
test('the value in a separate window must match its exact binding, revision, type and all retained context coordinates',()=>{
 const value={context:selection.context,asOf:selection.asOf,holding:{tradeId:selection.tradeId,bindingId:selection.bindingId,revision:'2',instrumentType:'ppa'}};
 assert.equal(readSelectedPosition(value,selection.context,selection.asOf,selection),value);
 for(const changed of [{...value,context:{...value.context,knownAt:'2031-03-12T18:46:00Z'}},{...value,asOf:'2031-03-12T17:00:00Z'},
  ...[{bindingId:selection.tradeId},{revision:'3'},{instrumentType:'future'}].map(change=>({...value,holding:{...value.holding,...change}}))])
  assert.throws(()=>readSelectedPosition(changed,selection.context,selection.asOf,selection),/does not match/);
});
test('market labels must be returned explicitly by the authorized retained read, with no code-name fallback',()=>{
 const names=[{code:'synthetic_curve',name:'Synthetic energy curve'}];assert.equal(positionWindowNames(names),names);
 for(const value of [undefined,null,{},[{code:'synthetic_curve'}],[{code:'synthetic_curve',name:''}],[{code:4,name:'Synthetic'}]])
  assert.throws(()=>positionWindowNames(value),/authorized market names/);
});
