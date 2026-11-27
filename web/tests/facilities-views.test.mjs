import test from 'node:test';
import assert from 'node:assert/strict';
import {facilityViews,facilityViewSections,facilityViewOf,facilityViewNames,facilitySectionNames} from '../dist/facilities-views.js';

test('every Facilities section belongs to exactly one sidebar view',()=>{
 const sections=facilityViews.flatMap(view=>facilityViewSections[view]);
 assert.deepEqual([...sections].sort(),['activity','crude-delivery','definitions','gas-delivery','outlook','quantities','route','trades','transport']);
 assert.equal(new Set(sections).size,sections.length);
 for(const view of facilityViews)for(const section of facilityViewSections[view])assert.equal(facilityViewOf(section),view);
});
test('the views and their tabs are named for the work done there',()=>{
 assert.deepEqual(facilityViews.map(view=>[view,facilityViewNames[view],facilityViewSections[view].map(section=>facilitySectionNames[section])]),[
  ['facilities-position','Gas day position',['Gas day position']],
  ['facilities-transport','Nominations & transport',['Nominations','Route plans','Capacity']],
  ['facilities','Storage & allocations',['Allocations','Physical activity','Trades and volumes','Definitions']],
  ['facilities-crude','Receipts & dispatches',['Receipts & dispatches']]]);
});
