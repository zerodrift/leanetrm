import {test} from 'node:test';
import assert from 'node:assert/strict';
import {PendingAdministration,accessChange,knownAdministrationRejection} from '../dist/admin-view.js';
test('uncertain administrative retries retain exact identity and payload',()=>{
  const pending=new PendingAdministration();const body={login:'synthetic',roles:['viewer']};
  const first=pending.begin('admin/create',body,()=> 'first');
  assert.deepEqual(pending.begin('admin/create',{...body},()=> 'second'),first);
  assert.throws(()=>pending.begin('admin/create',{...body,roles:['access_administrator']}),/outstanding request/);
  assert.throws(()=>pending.begin('admin/update',body),/outstanding request/);
  pending.clear();assert.equal(pending.begin('admin/create',body,()=> 'next').operationId,'next');
});
test('only explicit rejected outcomes may discard an administrative retry identity',()=>{
  for(const code of [undefined,'request_failed','response_unknown','administration_outcome_unknown','gateway_failure'])assert.equal(knownAdministrationRejection(code),false);
  for(const code of ['admin_conflict','access_denied','request_token','last_administrator','request_format','request_too_large','agent_managed'])assert.equal(knownAdministrationRejection(code),true,code);
  const body={roles:['viewer']},pending=new PendingAdministration();pending.begin('admin/create',body,()=> 'original');body.roles.push('access_administrator');
  assert.deepEqual(pending.command.body.roles,['viewer']);
});
test('audit explains access suspension, restoration and role replacement',()=>{
  const before={name:'Synthetic',enabled:true,roles:['viewer']};
  assert.equal(accessChange(null,before),'Account created');
  assert.equal(accessChange(before,{...before,enabled:false,roles:[]}),'Access suspended · Roles changed');
  assert.equal(accessChange({...before,enabled:false},before),'Access restored');
});
