import test from 'node:test';
import assert from 'node:assert/strict';
import {comparableDates,otherSessionRefusal,sessionOffer} from '../dist/close-sessions.js';

const close=(id,session,businessDate)=>({id,at:`${businessDate}T16:00:00Z`,businessDate,name:id,originCloseId:id,close:{session,sessionVersion:1,businessDate,replacedBy:null}});
const working=(id,at)=>({id,at,businessDate:at.slice(0,10),name:'Edited inputs',originCloseId:null});

test('a chosen session offers its own closes and every working snapshot, separately (D334)',()=>{
 const frames=[close('a','london','2020-12-17'),close('b','new_york','2020-12-17'),working('w','2020-12-17T16:00:00Z')];
 assert.deepEqual(sessionOffer(frames,'london',null),{closes:[frames[0]],working:[frames[2]]});
});
test('no chosen session offers no close: nothing picks one',()=>{
 const frames=[close('a','london','2020-12-17'),working('w','2020-12-17T16:00:00Z')];
 assert.deepEqual(sessionOffer(frames,null,null),{closes:[],working:[frames[1]]});
});
test('a historical view offers the closes of its own business day, not of the UTC date of their instant',()=>{
 const late={...close('late','late','2020-12-17'),at:'2020-12-18T01:30:00Z'};
 const other=close('other','late','2020-12-18');
 assert.deepEqual(sessionOffer([late,other],'late','2020-12-17').closes,[late]);
});
test('opening a close of another session names that session and chooses nothing (D334)',()=>{
 const frames=[close('a','london','2020-12-17'),close('b','new_york','2020-12-17'),working('w','2020-12-17T16:00:00Z')],names=new Map([['london','London EOD'],['new_york','New York EOD']]);
 assert.equal(otherSessionRefusal(frames,'b','london',names),'That answer used the New York EOD close session. Choose that session to open its As of date.');
 assert.equal(otherSessionRefusal(frames,'b',null,names),'That answer used the New York EOD close session. Choose that session to open its As of date.');
 assert.equal(otherSessionRefusal(frames,'a','london',names),null);
 assert.equal(otherSessionRefusal(frames,'w','london',names),null);
 assert.equal(otherSessionRefusal(frames,'missing','london',names),null);
});

test('a derived pricing snapshot keeps its server business date across UTC midnight',()=>{
 const source={id:'source',name:'Source inputs',at:'2020-12-19T00:00:00Z',businessDate:'2020-12-18',close:null};
 assert.deepEqual(sessionOffer([source],null,'2020-12-18').working,[source]);
 assert.deepEqual(sessionOffer([source],null,'2020-12-19').working,[]);
});

test('P&L compares the chosen convention\'s closes by business day and working snapshots by instant, and no other convention\'s close',()=>{
 const frames=[close('a','london','2020-12-16'),close('b','new_york','2020-12-16'),working('w','2020-12-17T09:30:00Z'),close('c','london','2020-12-17')];
 const day=date=>'day '+date,instant=at=>'at '+at;
 assert.deepEqual(comparableDates(frames,'london',day,instant),[
  {id:'a',at:'2020-12-16T16:00:00Z',label:'day 2020-12-16',session:'london'},{id:'c',at:'2020-12-17T16:00:00Z',label:'day 2020-12-17',session:'london'},
  {id:'w',at:'2020-12-17T09:30:00Z',label:'Working snapshot · at 2020-12-17T09:30:00Z',session:null}]);
 // With no convention chosen no close is compared: nothing picks a convention.
 assert.deepEqual(comparableDates(frames,null,day,instant).map(f=>f.id),['w']);
});
