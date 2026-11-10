import test from 'node:test';
import assert from 'node:assert/strict';
import {closeConventionProfile} from '../dist/close-convention-profile.js';
const catalog={sessions:[{code:'london',name:'London',state:'published'},{code:'ny',name:'New York',state:'published'},{code:'draft',name:'Draft',state:'draft'}]};
const apiFor=(saved=null,write=async body=>({session:body.session}))=>async(path,body)=>path==='close-sessions/catalog'?catalog:body?write(body):{session:saved};
test('a missing or withdrawn profile default does not choose another convention',async()=>{
 for(const saved of [null,'withdrawn']){
  const profile=closeConventionProfile(apiFor(saved));const read=await profile.load('owner-a');
  assert.equal(read.session,saved);assert.deepEqual(read.conventions.map(c=>c.code),['london','ny']);
 }
});
test('saving an unchanged default writes nothing and choosing one retains its exact code',async()=>{
 const writes=[],profile=closeConventionProfile(apiFor('london',async body=>{writes.push(body);return {session:body.session};}));
 await profile.load('owner-a');await profile.save('london');assert.deepEqual(writes,[]);
 await profile.save('ny');assert.deepEqual(writes,[{session:'ny'}]);
 await assert.rejects(profile.save('draft'),/published/);await assert.rejects(profile.save(null),/Choose/);
});
test('a lost acknowledgement retries its exact convention before a later choice',async()=>{
 let lost=true;const writes=[],profile=closeConventionProfile(apiFor('london',async body=>{writes.push(body);if(lost){lost=false;throw Error('reply lost');}return {session:body.session};}));
 await profile.load('owner-a');await assert.rejects(profile.save('ny'),/reply lost/);
 await profile.save('london');assert.deepEqual(writes,[{session:'ny'},{session:'ny'},{session:'london'}]);
});
test('account changes invalidate delayed reads and queued default saves',async()=>{
 let finish;const writes=[];const profile=closeConventionProfile(apiFor('london',async body=>{writes.push(body);return new Promise(resolve=>{finish=resolve;});}));
 await profile.load('owner-a');const first=profile.save('ny');await new Promise(resolve=>setImmediate(resolve));const queued=profile.save('london');
 profile.invalidate();finish({session:'ny'});await assert.rejects(first,/account changed/);await assert.rejects(queued,/account changed/);assert.equal(writes.length,1);
});
test('an uncertain first default cannot be confirmed as no default by a later blank save',async()=>{
 let lost=true;const profile=closeConventionProfile(apiFor(null,async body=>{if(lost){lost=false;throw Error('reply lost');}return {session:body.session};}));
 await profile.load('owner-a');await assert.rejects(profile.save('ny'),/reply lost/);await assert.rejects(profile.save(null),/Choose/);
});
