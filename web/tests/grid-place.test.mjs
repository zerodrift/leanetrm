import test from 'node:test';
import assert from 'node:assert/strict';
import {retainPlace} from '../dist/grid-place.js';

test('the anchor and selection survive a reorder',()=>{assert.deepEqual(retainPlace({anchor:'b',offset:12,selected:'c'},['a','b','c'],['c','b','a']),{place:{anchor:'b',offset:12,selected:'c'},selectionLost:false});});
test('a removed anchor falls to the next surviving row, then the previous',()=>{
 assert.equal(retainPlace({anchor:'b',offset:5,selected:null},['a','b','c'],['a','c']).place.anchor,'c');
 assert.equal(retainPlace({anchor:'c',offset:5,selected:null},['a','b','c'],['a','b']).place.anchor,'b');
});
test('a removed selection is reported lost',()=>{assert.deepEqual(retainPlace({anchor:'a',offset:0,selected:'b'},['a','b'],['a']),{place:{anchor:'a',offset:0,selected:null},selectionLost:true});});
test('an emptied grid has no anchor',()=>{assert.deepEqual(retainPlace({anchor:'a',offset:9,selected:'a'},['a'],[]).place,{anchor:null,offset:0,selected:null});});
test('no selection is never reported lost, and an anchor that falls keeps its offset',()=>{
 assert.deepEqual(retainPlace({anchor:'b',offset:7,selected:null},['a','b','c','d'],['d','a']),{place:{anchor:'d',offset:7,selected:null},selectionLost:false});
 assert.deepEqual(retainPlace({anchor:null,offset:0,selected:null},[],['a']),{place:{anchor:null,offset:0,selected:null},selectionLost:false});
});
