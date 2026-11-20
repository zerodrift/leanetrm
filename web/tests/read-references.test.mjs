import test from 'node:test';
import assert from 'node:assert/strict';
import {attachReadReferences,carryReadReferences,cloneRead,readReferenceHeaders,retainedKnowledge,renewReadReferences} from '../dist/read-references.js';
import {actorTransport} from '../dist/pending-command.js';
const first='00000000-0000-0000-0000-000000000001',second='00000000-0000-0000-0000-000000000002';
const header='X-OpenETRM-Read-References';
test('an owned receipt renews stale nested references without changing the original command',()=>{
 const request=attachReadReferences({context:{contextId:first},run:{id:'original-run',part:3,parts:5},requestId:'original-request'},{[first]:'old-workspace-proof'});
 carryReadReferences(request,request.context);
 const before=JSON.stringify(request),source=attachReadReferences({contextId:first},{[first]:'current-owned-proof'});
 renewReadReferences(request,source);
 assert.deepEqual(JSON.parse(readReferenceHeaders(request)[header]),{[first]:'current-owned-proof'});
 assert.equal(JSON.stringify(request),before);
});
test('independent read results retain their own references after a newer read completes',()=>{
 const a=attachReadReferences({context:{contextId:first},value:'3.25'},{[first]:'proof-a'});
 const b=attachReadReferences({context:{contextId:second},value:'3.50'},{[second]:'proof-b'});
 const request={context:{snapshotId:'snapshot',...retainedKnowledge(a.context)},other:retainedKnowledge(b.context)};
 assert.deepEqual(JSON.parse(readReferenceHeaders(request)[header]),{[first]:'proof-a',[second]:'proof-b'});
 assert.deepEqual(JSON.parse(JSON.stringify(request)),{context:{snapshotId:'snapshot',knowledgeMode:'retained',contextId:first},other:{knowledgeMode:'retained',contextId:second}});
 assert.deepEqual(readReferenceHeaders(cloneRead(request)),readReferenceHeaders(request));
 assert.deepEqual(JSON.parse(readReferenceHeaders(retainedKnowledge(cloneRead(a).context))[header]),{[first]:'proof-a'});
});
test('transport renewal cannot change financial request identity',()=>{
 const request={context:{contextId:first},operationId:'same-command',value:'-2.125'};
 const a=attachReadReferences(cloneRead(request),{[first]:'first-proof'}),b=attachReadReferences(cloneRead(request),{[first]:'renewed-proof'});
 assert.equal(JSON.stringify(a),JSON.stringify(b));
 assert.notDeepEqual(readReferenceHeaders(a),readReferenceHeaders(b));
 assert.deepEqual(JSON.parse(readReferenceHeaders(carryReadReferences({contextId:first,asOf:'exact'},a))[header]),{[first]:'first-proof'});
 assert.deepEqual(JSON.parse(readReferenceHeaders({a,b})[header]),{[first]:'renewed-proof'});
});
test('unknown or malformed transport metadata is refused',()=>{
 for(const value of [{bad:'proof'},{[first]:5},Array(9).fill('proof'),{[first]:'x'.repeat(8193)}])assert.throws(()=>attachReadReferences({},value),/invalid/);
 assert.deepEqual(readReferenceHeaders({contextId:first}),{});
});
test('an unconfirmed command keeps its separate proof after a document reload',()=>{
 const values=new Map(),storage={getItem:key=>values.get(key)??null,setItem:(key,value)=>values.set(key,value),removeItem:key=>values.delete(key)};
 const command=attachReadReferences({operationId:'same-command',body:{context:{contextId:first}}},{[first]:'original-proof'});
 actorTransport(()=> 'actor',()=>storage).write(command);
 const recovered=actorTransport(()=> 'actor',()=>storage).read();
 assert.equal(JSON.stringify(recovered),JSON.stringify(command));
 assert.deepEqual(readReferenceHeaders(recovered),readReferenceHeaders(command));
 actorTransport(()=> 'actor',()=>storage).write(undefined);assert.equal(values.size,0);
});
