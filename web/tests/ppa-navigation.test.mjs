import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';

const source=readFileSync(new URL('../src/app.ts',import.meta.url),'utf8');
const handler=source.match(/\$\('nav-ppa'\)\.onclick=\(\)=>void act\((async\(\)=>\{[^\n]+\})\);/)[1];
const navigation=(setPage,load)=>new Function('setPage','ppa','return '+handler)(setPage,{load});

test('PPA reads wait for live pause and page readiness',async()=>{
 let resume;const events=[];
 const pause=new Promise(resolve=>{resume=resolve;});
 const pending=navigation(async page=>{assert.equal(page,'ppa');events.push('pause');await pause;events.push('ready');},async()=>events.push('read'))();
 await Promise.resolve();assert.deepEqual(events,['pause']);
 resume();await pending;assert.deepEqual(events,['pause','ready','read']);
});

test('failed live pause refuses PPA loading through the normal action error path',async()=>{
 const failure=Error('pause refused');let reads=0;
 await assert.rejects(navigation(()=>Promise.reject(failure),async()=>{reads++;})(),failure);
 assert.equal(reads,0);
});
