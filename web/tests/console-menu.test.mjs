import test from 'node:test';
import assert from 'node:assert/strict';
import {consoleMenu,consoleEntryFor} from '../dist/console-menu.js';
import {referenceGroups} from '../dist/data-sections.js';

const allTables=referenceGroups.flatMap(g=>g.sections.map(s=>s.value));
const full=consoleMenu({pageAvailable:()=>true,tables:allTables});

test('six groups in order hold every setup screen',()=>{
 assert.deepEqual(full.map(g=>g.name),['Curves & pricing','Time','Products','Commodities & units','Physical','Entities']);
 assert.equal(full.flatMap(g=>g.entries).length,28);
});
test('setup that depends on time sits together',()=>{
 assert.deepEqual(full[1].entries.map(e=>e.name),['Calendars','Calendar dates','Market clocks','Schedules & date mappings','Power TOUs']);
});
test('curve setup leads with its editors',()=>{
 assert.deepEqual(full[0].entries.map(e=>e.page).slice(0,3),['curve-definitions','custom-curves','configuration']);
});
test('no table is listed twice or lost',()=>{
 const tables=full.flatMap(g=>g.entries).filter(e=>e.table).map(e=>e.table);
 assert.deepEqual([...tables].sort(),[...allTables].sort());
});
test('an unavailable page or table disappears and an emptied group is hidden',()=>{
 const menu=consoleMenu({pageAvailable:page=>page!=='parties',tables:allTables});
 assert.equal(menu.some(g=>g.name==='Entities'),false);
 assert.equal(menu.flatMap(g=>g.entries).length,27);
 const noLocations=consoleMenu({pageAvailable:()=>true,tables:allTables.filter(t=>t!=='locations')});
 assert.equal(noLocations.flatMap(g=>g.entries).some(e=>e.table==='locations'),false);
});
test('an account with no setup access sees an empty menu',()=>{
 assert.deepEqual(consoleMenu({pageAvailable:()=>false,tables:[]}),[]);
});
test('crude setup finds the Delivery setup entry and a table finds its entry',()=>{
 assert.equal(consoleEntryFor(full,'crude-setup').id,'delivery-setup');
 assert.equal(consoleEntryFor(full,'reference','locations').name,'Locations');
 assert.equal(consoleEntryFor(full,'market'),undefined);
});
