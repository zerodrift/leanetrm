import test from 'node:test';
import assert from 'node:assert/strict';
import {datasetApi,selectedDataset,datasetScope} from '../dist/dataset-address.js';
import {actorTransport} from '../dist/pending-command.js';

test('all configured datasets keep their own API namespace',()=>{
 assert.equal(datasetApi('baseline'),'/api/');
 assert.equal(datasetApi('exchange'),'/examples/exchange/api/');
 assert.equal(datasetApi('synthetic'),'/examples/synthetic/api/');
 assert.equal(selectedDataset('?example=synthetic'),'synthetic');
 assert.throws(()=>selectedDataset('?example=synthetic&example=baseline'));
 assert.throws(()=>selectedDataset('?example=unknown'));
 assert.throws(()=>datasetApi('unknown'));
});

test('switching and reloading datasets preserves each uncertain command without consuming another catalog request',()=>{
 const base='installation:tenant:workspace',catalog='10000000-0000-0000-0000-000000000001',other='10000000-0000-0000-0000-000000000002';
 const saved=new Map(),storage={getItem:k=>saved.get(k)??null,setItem:(k,v)=>saved.set(k,v),removeItem:k=>saved.delete(k)};
 let selected='baseline';const identity=()=>datasetScope(base,selected,catalog)+':user';
 const first=actorTransport(identity,()=>storage);
 first.write({path:'parties/save',operationId:'primary-uncertain',body:{code:'synthetic_party'},uncertain:true});
 selected='synthetic';assert.equal(first.read(),null);
 first.write({path:'parties/save',operationId:'demo-uncertain',body:{code:'synthetic_party'},uncertain:true});
 const reloaded=actorTransport(identity,()=>storage);assert.equal(reloaded.read().operationId,'demo-uncertain');
 selected='baseline';assert.equal(reloaded.read().operationId,'primary-uncertain');
 reloaded.write(undefined);selected='synthetic';assert.equal(reloaded.read().operationId,'demo-uncertain');
 assert.equal(datasetScope(base,'baseline',catalog),base,'the existing primary transport identity is retained');
 assert.notEqual(datasetScope(base,'synthetic',catalog),datasetScope(base,'synthetic',other));
 assert.throws(()=>datasetScope(base,'synthetic',''));
});
