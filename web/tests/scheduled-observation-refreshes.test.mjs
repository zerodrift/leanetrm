import test from 'node:test';
import assert from 'node:assert/strict';
import {observationRefreshQueue} from '../dist/scheduled-swap-editor.js';

test('period refreshes run one at a time and a waiting refresh covers later edits of its period',async()=>{
 const runs=[];let release;const blocked=new Promise(accept=>release=accept);
 const period=name=>{const refresh=async()=>{runs.push(name);if(runs.length===1)await blocked;};return refresh;};
 const first=period('first'),second=period('second');
 let finished;const done=new Promise(accept=>finished=accept);
 const refresh=observationRefreshQueue(async work=>{await work();if(runs.length===3)finished();});
 refresh(first);await Promise.resolve();await Promise.resolve();
 assert.deepEqual(runs,['first']);
 for(let edit=0;edit<6;edit++){refresh(first);refresh(second);}
 assert.deepEqual(runs,['first'],'a refresh waits for the one in progress');
 release();await done;await new Promise(accept=>setTimeout(accept,10));
 assert.deepEqual(runs,['first','first','second'],'edits made while a period waits share its next refresh');
});

test('an edit made while its period refreshes queues another refresh of that period',async()=>{
 const runs=[];let edited=false,refresh;
 const period=async()=>{runs.push('period');if(!edited){edited=true;refresh(period);}};
 refresh=observationRefreshQueue(work=>work());
 refresh(period);await new Promise(accept=>setTimeout(accept,10));
 assert.deepEqual(runs,['period','period']);
});
