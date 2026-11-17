import test from 'node:test';
import assert from 'node:assert/strict';
import {readDeliveryAssessment} from '../dist/delivery-assessment-view.js';

const reference=code=>({revision:{catalogId:'synthetic-catalog',revisionId:'synthetic-revision'},code});
const interval={kind:'interval',from:'2032-03-14T08:00:00Z',through:'2032-03-14T09:00:00Z'};
const transfer={kind:'transfer',from:'2032-03-14T08:30:00Z',through:null};
const specimen=()=>({profile:'synthetic_delivery_v1',quantitySpecification:reference('native_quantity'),activities:[
 {sourceId:'synthetic-flow',stage:'chosen',label:'Chosen receipt',geometry:interval,location:reference('receipt'),quantity:'100/3'},
 {sourceId:'synthetic-receipt',stage:'accepted',label:'Accepted receipt',geometry:transfer,location:reference('terminal'),quantity:'25.5'}],
 measures:[{code:'unassigned',label:'Explicitly unassigned',value:'7.25'}],
 constraints:[{code:'capacity',label:'Receipt capacity',state:'unassessed',sourceId:null,geometry:interval,observed:null,limit:'50',residual:null,basis:'per_elapsed_hour'}],
 capabilities:[{code:'inspect',label:'Inspect retained delivery evidence'}]});

test('one assessment preserves distinct interval and transfer geometry and exact supplied quantities',()=>{
 const value=specimen(),read=readDeliveryAssessment(value);
 assert.deepEqual(read,value);
 assert.equal(read.activities[0].quantity,'100/3');
 assert.equal(read.activities[1].geometry.through,null);
 assert.equal(read.constraints[0].state,'unassessed');
 value.activities[0].quantity='999';
 assert.equal(read.activities[0].quantity,'100/3');
});

test('assessment refuses a transfer disguised as a zero-duration interval or a fabricated end time',()=>{
 for(const geometry of [{kind:'transfer',from:transfer.from,through:transfer.from},{kind:'interval',from:interval.from,through:null},{kind:'unknown',from:interval.from,through:null}]){
  const value=specimen();value.activities[0].geometry=geometry;
  assert.throws(()=>readDeliveryAssessment(value),/assessment/i);
 }
});

test('missing quantities and unsupported constraint states cannot become a displayed successful assessment',()=>{
 for(const mutate of [v=>v.activities[0].quantity=null,v=>v.measures[0].value='',v=>v.constraints[0].state='clear',v=>v.quantitySpecification=null,v=>v.capabilities[0].label='']){
  const value=specimen();mutate(value);assert.throws(()=>readDeliveryAssessment(value),/assessment/i);
 }
});
