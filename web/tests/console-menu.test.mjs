import test from 'node:test';
import assert from 'node:assert/strict';
import {consoleMenu,consoleEntryFor,consoleAreas} from '../dist/console-menu.js';
import {referenceGroups} from '../dist/data-sections.js';

const allTables=referenceGroups.flatMap(g=>g.sections.map(s=>s.value));
const full=consoleMenu({pageAvailable:()=>true,tables:allTables});
const names=id=>full.find(g=>g.id===id).entries.map(e=>e.name);

test('seven areas in order hold every setup screen',()=>{
 assert.deepEqual(full.map(g=>g.name),['Curves','Pricing','Exchanges & conventions','Calendars','Commodities & units','Locations & delivery','Legal entities']);
 assert.deepEqual(consoleAreas.map(a=>a.id),['curves','pricing','products','time','units','physical','entities']);
 assert.equal(full.flatMap(g=>g.entries).length,29);
});
test('contract pricing and publications each stand on their own under Pricing',()=>{
 assert.deepEqual(names('pricing'),['Contract pricing','Publications','Data sources']);
});
test('curve setup leads with its editors',()=>{
 assert.deepEqual(names('curves'),['Curve definitions','Curve builder','Interest rates']);
});
test('setup that depends on time sits together, date rules beside the date schedules they refer to',()=>{
 assert.deepEqual(names('time'),['Calendars','Calendar dates','Market clocks','Date schedules','Date rules','Power TOUs']);
});
test('where things are delivered sits with how',()=>{
 assert.deepEqual(names('physical'),['Locations','Location types','Delivery patterns','Delivery setup']);
});
test('no table is listed twice, and only one read inside another entry is left out',()=>{
 const tables=full.flatMap(g=>g.entries).filter(e=>e.table).map(e=>e.table);
 assert.deepEqual([...tables].sort(),allTables.filter(t=>t!=='price_index_definitions').sort());
 assert.equal(full.flatMap(g=>g.entries).some(e=>e.name==='Price indexes'),false);
});
test('price indexes are read with the publications that publish them',()=>{
 assert.equal(consoleEntryFor(full,'reference','price_index_definitions').id,'publications');
 assert.equal(consoleEntryFor(consoleMenu({pageAvailable:page=>page!=='configuration',tables:allTables}),'reference','price_index_definitions'),undefined);
});
test('an entry that is one view of a page is found by that view',()=>{
 assert.equal(consoleEntryFor(full,'configuration',undefined,'contracts').id,'configuration');
 assert.equal(consoleEntryFor(full,'configuration',undefined,'publications').id,'publications');
 assert.equal(consoleEntryFor(full,'schedules',undefined,'contracts').id,'date-rules');
 assert.equal(consoleEntryFor(full,'schedules',undefined,'exchange').id,'schedules');
 assert.equal(consoleEntryFor(full,'configuration'),undefined,'a page of several views names the one shown');
});
test('an unavailable page or table disappears and an emptied area is hidden',()=>{
 const menu=consoleMenu({pageAvailable:page=>page!=='parties',tables:allTables});
 assert.equal(menu.some(g=>g.name==='Legal entities'),false);
 assert.equal(menu.flatMap(g=>g.entries).length,28);
 const noLocations=consoleMenu({pageAvailable:()=>true,tables:allTables.filter(t=>t!=='locations')});
 assert.equal(noLocations.flatMap(g=>g.entries).some(e=>e.table==='locations'),false);
 const noPriceSetup=consoleMenu({pageAvailable:page=>page!=='configuration',tables:allTables});
 assert.deepEqual(noPriceSetup.find(g=>g.id==='pricing').entries.map(e=>e.name),['Data sources']);
});
test('an account with no setup access sees an empty menu',()=>{
 assert.deepEqual(consoleMenu({pageAvailable:()=>false,tables:[]}),[]);
});
test('crude setup finds the Delivery setup entry and a table finds its entry',()=>{
 assert.equal(consoleEntryFor(full,'crude-setup').id,'delivery-setup');
 assert.equal(consoleEntryFor(full,'reference','locations').name,'Locations');
 assert.equal(consoleEntryFor(full,'market'),undefined);
});
