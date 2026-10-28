import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';

const source=readFileSync(new URL('../src/app.ts',import.meta.url),'utf8');
const branch=source.match(/case 'ppa':(await setPage\('ppa'\);[^\n]+return;)/)[1];
const handler='async()=>{'+branch+'}';
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


test('the sidebar has no PPA entry, while page reporting and unsaved checks include the PPA page',()=>{
 const [,entries]=source.match(/for\(const name of \[([^\]]*)\]\)\s*\$\('nav-'\+name\)\.onclick=navigate\(name\)/);
 assert.doesNotMatch(entries,/'ppa'/);
 const report=source.match(/function pageState\(\):PageState\{([\s\S]*?)\n\}/)[1];
 const shown={trade:null,book:'d1000000-0000-4000-8000-000000000001'};
 assert.deepEqual(new Function('page','ppa',report)('ppa',{shown:()=>shown}),{page:'ppa',...shown});
 const unsaved=source.match(/function unsaved\(\)\{([\s\S]*?)\n\}/)[1];
 assert.equal(new Function('page','ppa',unsaved)('ppa',{unsaved:()=>true}),true);
});
