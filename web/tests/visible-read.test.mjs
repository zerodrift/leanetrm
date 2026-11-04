import test from 'node:test';
import assert from 'node:assert/strict';
import {whenVisible} from '../dist/visible-read.js';

test('offscreen and detached content reads nothing; visible content reads once; disposal cancels a pending read',()=>{
 const observers=[];
 globalThis.IntersectionObserver=class{
  constructor(callback){this.callback=callback;observers.push(this);}
  observe(target){this.target=target;}
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
