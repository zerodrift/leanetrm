import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

// A real scrolling box in a disposable browser; no application runtime is attached.
test('a conversation follows new content at its end and stays where the person scrolled up to read',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(
 '<div id="host" style="height:200px;overflow-y:auto"></div>',async evaluate=>{
  const seen=await evaluate(`(async()=>{
   const {follow}=await import('/conversation-follow.js'),box=document.querySelector('#host');
   const add=lines=>()=>{for(let i=0;i<lines;i++)box.append(Object.assign(document.createElement('p'),{textContent:'line'}));};
   const end=()=>box.scrollHeight-box.scrollTop-box.clientHeight<=1;
   follow(box,add(40));const followed=end();
   follow(box,add(10));const kept=end();
   box.scrollTop=0;follow(box,add(10));const read=box.scrollTop;
   follow(box,add(1),true);const asked=end();
   return {followed,kept,read,asked};
  })()`);
  assert.deepEqual(seen,{followed:true,kept:true,read:0,asked:true});
 }));
