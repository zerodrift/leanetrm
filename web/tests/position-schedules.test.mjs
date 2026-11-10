import test from 'node:test';
import assert from 'node:assert/strict';
import {withSideSchedules,genericPositionView} from '../dist/position-schedules.js';
const holding={tradeId:'trade-a',bindingId:'binding-a',revision:'1',instrumentType:'synthetic',tradeReference:'Synthetic trade',instrumentReference:'Synthetic instrument'};
const context={snapshotId:'snapshot-a',contextId:'context-a',knowledgeMode:'retained'};
const view=(h=holding,c=context,zone='UTC')=>withSideSchedules(genericPositionView(h),async()=>{},c,h,()=>{}).view(zone);
test('the common Side inspector has stable identity across unchanged paints and changes for another position, context or display zone',()=>{
 const first=view().scheduleKey;assert.equal(typeof first,'string');assert.equal(view().scheduleKey,first);
 assert.notEqual(view({...holding,revision:'2'}).scheduleKey,first);assert.notEqual(view(holding,{...context,contextId:'context-b'}).scheduleKey,first);assert.notEqual(view(holding,context,'America/Chicago').scheduleKey,first);
});
test('an integrated schedule retains its own renderer identity',()=>{
 const schedules=()=>()=>{},read={...genericPositionView(holding),view:()=>({terms:[],history:[],schedules,scheduleKey:'owned'})};
 const shown=withSideSchedules(read,async()=>{},context,holding,()=>{}).view('UTC');assert.equal(shown.schedules,schedules);assert.equal(shown.scheduleKey,'owned');
});
