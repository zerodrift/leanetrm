import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';

const source=readFileSync(new URL('../src/app.ts',import.meta.url),'utf8');
const ppaSource=readFileSync(new URL('../src/ppa-editor.ts',import.meta.url),'utf8');

test('cancelled PPA loading cannot restore a requested trade after its editor was reset',async()=>{
 const body=ppaSource.match(/async inspect\(at:string,tradeId:string\)\{([^}]+)\}/)[1];
 const calls=[];
 const inspect=new Function('load','catalog','inspectTrade','at','tradeId',`let inspectOnSelect;return (async()=>{${body};return inspectOnSelect;})()`);
 let finish;const loading=new Promise(resolve=>{finish=resolve;});
 const cancelled=inspect(()=>loading,{},id=>calls.push(id),'2031-01-01T00:00:00Z','original-trade');
 finish(false);
 assert.equal(await cancelled,undefined);
 assert.deepEqual(calls,[]);
 assert.equal(await inspect(async()=>true,{},async id=>calls.push(id),'2031-01-01T00:00:00Z','selected-trade'),'selected-trade');
 assert.deepEqual(calls,['selected-trade']);
});
const branch=source.match(/case 'ppa':(await setPage\('futures'\);[^\n]+return;)/)[1];
const handler='async()=>{'+branch+'}';
const navigation=(setPage,load,open=async()=>{})=>new Function('setPage','futures','return '+handler)(setPage,{load,openProduct:open});

test('PPA reads wait for live pause and page readiness',async()=>{
 let resume;const events=[];
 const pause=new Promise(resolve=>{resume=resolve;});
 const pending=navigation(async page=>{assert.equal(page,'futures');events.push('pause');await pause;events.push('ready');},async()=>events.push('read'),async product=>{assert.equal(product,'ppa');events.push('ticket');})();
 await Promise.resolve();assert.deepEqual(events,['pause']);
 resume();await pending;assert.deepEqual(events,['pause','ready','read','ticket']);
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
