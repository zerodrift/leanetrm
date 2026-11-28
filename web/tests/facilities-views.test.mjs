import test from 'node:test';
import assert from 'node:assert/strict';
import {facilityViews,facilityViewSections,facilityViewOf,facilityViewNames,facilitySectionNames,facilitySectionAvailable,facilityViewAvailable,isFacilityView} from '../dist/facilities-views.js';

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
test('a view is available when one of its sections is, by the permissions that section needs',()=>{
 const has=held=>(...rights)=>rights.every(right=>held.includes(right)),trades=['ReadMarketData','ReadTrades'];
 assert.deepEqual(facilityViews.filter(view=>facilityViewAvailable(view,has(trades))),['facilities','facilities-crude']);
 assert.deepEqual(facilityViews.filter(view=>facilityViewAvailable(view,has([...trades,'ReadGasOperations']))),[...facilityViews]);
 assert.deepEqual(facilityViews.filter(view=>facilityViewAvailable(view,has(['ReadTrades','ReadGasOperations','ReadFacilityQuantities']))),[]);
 assert.equal(facilitySectionAvailable('quantities',has(trades)),false);
 assert.equal(facilitySectionAvailable('quantities',has([...trades,'ReadFacilityQuantities'])),true);
 assert.equal(facilitySectionAvailable('outlook',has([...trades,'ReadFacilityQuantities'])),false);
 assert.equal(isFacilityView('facilities-crude'),true);assert.equal(isFacilityView('physical-operations'),false);
});
