import test from 'node:test';
import assert from 'node:assert/strict';
import {whenVisible,visibleReads} from '../dist/visible-read.js';

test('offscreen and detached content reads nothing; visible content reads once; disposal cancels a pending read',()=>{
 const observers=[];
 globalThis.IntersectionObserver=class{
  constructor(callback){this.callback=callback;observers.push(this);}
  observe(target){this.target=target;}
  unobserve(){}
  disconnect(){this.disconnected=true;}
  send(isIntersecting){this.callback([{target:this.target,isIntersecting}]);}
 };
 try{
  let reads=0;const target={isConnected:true};const dispose=whenVisible(target,()=>reads++),first=observers[0];
  assert.equal(reads,0);first.send(false);assert.equal(reads,0);
  target.isConnected=false;first.send(true);assert.equal(reads,0);
  target.isConnected=true;first.send(true);first.send(true);assert.equal(reads,1);assert.equal(first.disconnected,true);
  dispose();const cancel=whenVisible(target,()=>reads++),second=observers[1];cancel();second.send(true);assert.equal(reads,1);
 }finally{delete globalThis.IntersectionObserver;}
});
test('batched entries cannot repeat a one-shot read or dispatch after reset, cancellation or context change',()=>{
 const observers=[];
 globalThis.IntersectionObserver=class{constructor(callback){this.callback=callback;observers.push(this);}observe(){}unobserve(){}disconnect(){}
  send(targets){this.callback(targets.map(target=>({target,isIntersecting:true})));}};
 try{
  const a={isConnected:true},b={isConnected:true};let reads=0;
  whenVisible(a,()=>reads++);observers[0].send([a,a]);assert.equal(reads,1);
  let current=true;const seen=[];
  const reader=visibleReads(()=>current,target=>{seen.push(target);current=false;});reader.reset([a,b]);observers[1].send([a,b]);assert.deepEqual(seen,[a]);
  current=true;const reset=[];const other=visibleReads(()=>current,target=>reset.push(target));other.reset([a]);other.reset([b]);
  observers[2].send([a,b,b]);assert.deepEqual(reset,[b]);other.cancel();observers[2].send([b]);assert.deepEqual(reset,[b]);
 }finally{delete globalThis.IntersectionObserver;}
});
