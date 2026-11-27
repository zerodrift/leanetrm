import test from 'node:test';
import assert from 'node:assert/strict';
import {browserPageActions} from './owned-browser.mjs';
import {revealSwingReview} from './gas-swing.mjs';

function fixture({readyAt=0,visibleAt=0,replaceDuringPaint=false,scrollError}={}){
 let elapsed=0,frames=0,replaced=false;
 const selector='[aria-label="Synthetic retained days"]',scrolls=[];
 const node=name=>({
  checkVisibility:()=>elapsed>=visibleAt,
  scrollIntoView(options){
   if(scrollError)throw scrollError;
   scrolls.push({name,at:elapsed,options});
  },
 });
 const original=node('original'),replacement=node('replacement');
 const document={querySelector(value){
  assert.equal(value,selector);
  if(elapsed<readyAt||replaced&&elapsed<500)return null;
  return replaced?replacement:original;
 }};
 const requestAnimationFrame=callback=>queueMicrotask(()=>{
  elapsed+=16;frames++;
  if(replaceDuringPaint&&frames===1)replaced=true;
  callback();
 });
 const evaluate=async expression=>Function('document','requestAnimationFrame',`return ${expression}`)(document,requestAnimationFrame);
 const actions={evaluate,...browserPageActions(evaluate,{now:()=>elapsed,pause:async ms=>{elapsed+=ms;}})};
 return {reveal:()=>revealSwingReview(actions,selector),scrolls,elapsed:()=>elapsed,frames:()=>frames};
}

test('Swing review waits for the ticket to remount before scrolling',async()=>{
 const f=fixture({readyAt:12500});
 await f.reveal();
 assert.deepEqual(f.scrolls,[{name:'original',at:12500,options:{block:'center'}}]);
 assert.equal(f.frames(),2);
});

test('Swing review waits for visible content before scrolling',async()=>{
 const f=fixture({visibleAt:12500});
 await f.reveal();
 assert.equal(f.scrolls.length,1);
 assert.equal(f.scrolls[0].at,12500);
});

test('Swing review follows a replacement made between animation frames',async()=>{
 const f=fixture({replaceDuringPaint:true});
 await f.reveal();
 assert.deepEqual(f.scrolls.map(value=>value.name),['original','replacement']);
 assert.ok(f.scrolls[1].at>=500);
 assert.equal(f.frames(),4);
});

for(const [state,options] of [['missing',{readyAt:Infinity}],['hidden',{visibleAt:Infinity}]]){
 test(`Swing review times out if its target stays ${state}`,async()=>{
  const f=fixture(options);
  await assert.rejects(f.reveal(),/Timed out after 30000ms/);
  assert.equal(f.elapsed(),30000);
  assert.deepEqual(f.scrolls,[]);
 });
}

test('Swing review scrolls ready content once and waits for painting',async()=>{
 const f=fixture();
 await f.reveal();
 assert.equal(f.scrolls.length,1);
 assert.equal(f.frames(),2);
 assert.equal(f.elapsed(),32);
});

test('Swing review reports unexpected scrolling errors without retrying',async()=>{
 const error=Error('Synthetic scrolling failure'),f=fixture({scrollError:error});
 await assert.rejects(f.reveal(),value=>value===error);
 assert.equal(f.elapsed(),0);
});
