import test from 'node:test';
import assert from 'node:assert/strict';
import {powerChoices} from '../dist/power-options.js';
const presets=[{product:'north',block:'peak',minutes:60},{product:'north',block:'offpeak',minutes:30},{product:'south',block:'all',minutes:15}];
test('power hours and intervals follow only the selected product and hours',()=>{
 assert.deepEqual(powerChoices(presets),{products:['north','south'],blocks:[],intervals:[]});
 assert.deepEqual(powerChoices(presets,'north'),{products:['north','south'],blocks:['peak','offpeak'],intervals:[]});
 assert.deepEqual(powerChoices(presets,'north','offpeak'),{products:['north','south'],blocks:['peak','offpeak'],intervals:[30]});
 assert.deepEqual(powerChoices(presets,'north','all').intervals,[]);
 assert.deepEqual(powerChoices([], 'north'),{products:[],blocks:[],intervals:[]});
});
