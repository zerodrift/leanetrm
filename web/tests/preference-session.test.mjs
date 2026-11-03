import test from 'node:test';
import assert from 'node:assert/strict';
import {newPreferences} from '../dist/preferences.js';
import {preferenceSession} from '../dist/preference-session.js';
const record=(version,preferences)=>({version,preferences});
const owned=(current,ownerId='actor-a')=>({ownerId,current});
const preferences=api=>preferenceSession(api,()=>{});

test('an absent server row creates local presentation choices without uploading old browser data',async()=>{
 const calls=[];const session=preferences(async(path,body)=>{calls.push([path,body]);return owned(null);});
 assert.equal((await session.load('human-a','UTC')).timeZone,'UTC');
 assert.deepEqual(calls,[['settings/preferences',undefined]]);
});
test('server preferences win and the next write carries their exact version',async()=>{
 const saved=newPreferences('America/Chicago'),calls=[];
 const session=preferences(async(path,body)=>{calls.push([path,body]);return body?record(5,body.preferences):owned(record(4,saved));});
 const read=await session.load('human-a','UTC');assert.equal(read.timeZone,'America/Chicago');
 await session.save({...read,timeZone:'UTC'});assert.equal(calls[1][1].expectedVersion,4);assert.equal(calls[1][1].ownerId,'actor-a');
 assert.ok(calls[1][1].operationId);
});
test('queued changes capture their own payload and wait for the previous version',async()=>{
 const sent=[];let finish;
 const session=preferences(async(path,body)=>{if(!body)return owned(null);sent.push(structuredClone(body));if(sent.length===1)return new Promise(resolve=>{finish=resolve;});return record(2,body.preferences);});
 const first=await session.load('human','UTC');const one=session.save({...first,sidebarCollapsed:true});
 await new Promise(resolve=>setImmediate(resolve));
 const two=session.save({...first,timeZone:'America/Chicago'});
 assert.equal(sent.length,1);finish(record(1,sent[0].preferences));await one;await two;
 assert.equal(sent[0].preferences.timeZone,'UTC');assert.equal(sent[1].expectedVersion,1);
});
test('an uncertain save retries the same operation before sending a later change',async()=>{
 const sent=[];let lost=true;
 const session=preferences(async(path,body)=>{if(!body)return owned(null);sent.push(structuredClone(body));if(lost){lost=false;throw Error('reply lost');}return record(body.expectedVersion+1,body.preferences);});
 const initial=await session.load('human','UTC');await assert.rejects(session.save({...initial,sidebarCollapsed:true}),/reply lost/);
 await session.save({...initial,assistantHidden:true});assert.deepEqual(sent[0],sent[1]);assert.notEqual(sent[1].operationId,sent[2].operationId);assert.equal(sent[2].expectedVersion,1);
});
test('late acknowledgements and queued changes cannot cross an owner change',async()=>{
 let finish;const sent=[];
 const session=preferences(async(path,body)=>{if(!body)return owned(null);sent.push(body);return new Promise(resolve=>{finish=resolve;});});
 const a=await session.load('a','UTC');const saving=session.save({...a,sidebarCollapsed:true});await new Promise(resolve=>setImmediate(resolve));
 const queued=session.save({...a,assistantHidden:true});const b=await session.load('b','America/Chicago');
 finish(record(1,sent[0].preferences));await assert.rejects(saving,/account changed/);await assert.rejects(queued,/account changed/);
 assert.equal(sent.length,1);assert.equal(b.timeZone,'America/Chicago');
});
test('a stale version requires a reload and never silently overwrites another tab',async()=>{
 const session=preferences(async(path,body)=>{if(!body)return owned(null);throw Object.assign(Error('Changed elsewhere'),{code:'user_preferences_conflict'});});
 const initial=await session.load('human','UTC');await assert.rejects(session.save({...initial,sidebarCollapsed:true}),/Changed elsewhere/);
 await assert.rejects(session.save({...initial,assistantHidden:true}),/Reload/);
});

test('invalidating before the server profile mutation blocks queued A writes while B access is still loading',async()=>{
 const sent=[];let finish;
 const session=preferences(async(path,body)=>{if(!body)return owned(null,'a');sent.push(body);return new Promise(resolve=>{finish=resolve;});});
 const a=await session.load('a','UTC');const first=session.save({...a,sidebarCollapsed:true});await new Promise(resolve=>setImmediate(resolve));
 const queued=session.save({...a,assistantHidden:true});session.invalidate();
 finish(record(1,sent[0].preferences));await assert.rejects(first,/account changed/);await assert.rejects(queued,/account changed/);
 assert.equal(sent.length,1);await assert.rejects(session.save(a),/Load your profile/);
});

test('a stored zone unavailable in this browser is explained and cannot be silently replaced by an autosave',async()=>{
 const stored={...newPreferences('UTC'),timeZone:'America/Future_Synthetic'},warnings=[],sent=[];
 const session=preferenceSession(async(path,body)=>{if(!body)return owned(record(3,stored));sent.push(body);return record(4,body.preferences);},message=>warnings.push(message));
 const shown=await session.load('human','America/Chicago');assert.equal(shown.timeZone,'America/Chicago');assert.match(warnings[0],/unsupported/);
 await assert.rejects(session.save({...shown,sidebarCollapsed:true}),/Choose and save/);assert.equal(sent.length,0);
 await session.save(shown,true);assert.equal(sent[0].preferences.timeZone,'America/Chicago');assert.equal(sent[0].expectedVersion,3);
});
