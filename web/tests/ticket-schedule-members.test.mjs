import test from 'node:test';
import assert from 'node:assert/strict';

test('one authored pair names two exact native schedules without inventing a slot',async()=>{
 const {scheduleMembers,requireScheduleMembers}=await import('../dist/ticket-schedule-members.js');
 const state={description:{sides:[{kind:'physical',entryCount:1},{kind:'pair',entryCount:2},{kind:'floating',entryCount:1}]},sides:[{code:'GAS',kind:'physical',values:{}},{code:'BAL',kind:'pair',values:{}}]};
 const entries=[{code:'GAS',owner:'GAS',kind:'physical',name:'Gas'},{code:'BAL.to_us',owner:'BAL',kind:'floating',name:'Owed to us'},{code:'BAL.by_us',owner:'BAL',kind:'floating',name:'Owed by us'}];
 const slots={GAS:0,'BAL.to_us':1,'BAL.by_us':2};
 assert.deepEqual(scheduleMembers(state,entries).map(side=>side.code),Object.keys(slots));
 assert.doesNotThrow(()=>requireScheduleMembers(state,entries,slots));
 for(const changed of [entries.slice(0,2),[entries[0],entries[1],{...entries[2],owner:'missing'}],[entries[0],entries[1],entries[1]]])
  assert.throws(()=>requireScheduleMembers(state,changed,slots),/membership/);
 assert.throws(()=>requireScheduleMembers(state,entries,{...slots,'BAL.by_us':1}),/membership/);
 assert.deepEqual(scheduleMembers({...state,sides:[state.sides[0]]},entries).map(side=>side.code),['GAS'],'stale preview cannot restore a removed card');
});
