import test from 'node:test';
import assert from 'node:assert/strict';
import {rememberPage,restoredPage} from '../dist/page-memory.js';
const storage=()=>{const values=new Map();return {getItem:key=>values.get(key)??null,setItem:(key,value)=>values.set(key,value)};};
test('reload restores a page only for its workspace and account under current permissions',()=>{
 const store=storage(),permissions=['ReadMarketData','ReadTrades'];
 rememberPage(()=>store,'workspace:trader','reference');
 assert.equal(restoredPage(()=>store,'workspace:trader',permissions),'reference');
 assert.equal(restoredPage(()=>store,'workspace:other',permissions),null);
 assert.equal(restoredPage(()=>store,'other:trader',permissions),null);
 assert.equal(restoredPage(()=>store,'workspace:trader',[]),null);
 rememberPage(()=>store,'workspace:trader','retired-page');
 assert.equal(restoredPage(()=>store,'workspace:trader',permissions),null);
});
test('blocked browser storage cannot prevent navigation',()=>{
 const store={getItem(){throw Error('blocked');},setItem(){throw Error('blocked');}};
 assert.doesNotThrow(()=>rememberPage(()=>store,'workspace','market'));
 assert.equal(restoredPage(()=>store,'workspace',['ReadMarketData']),null);
 assert.doesNotThrow(()=>rememberPage(()=>{throw Error('blocked getter');},'workspace','market'));
 assert.equal(restoredPage(()=>{throw Error('blocked getter');},'workspace',[]),null);
});
