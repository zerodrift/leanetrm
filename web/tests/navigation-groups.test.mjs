import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {navigationGroups,pageGroup,pageAvailable,firstPage} from '../dist/navigation-groups.js';

const allPages=navigationGroups.flatMap(g=>g.pages);
const listed=permissions=>allPages.filter(page=>pageAvailable(page,permissions));

test('physical product workflows remain Trading pages while saved views provide personal sidebar entries',()=>{
 for(const page of ['gas','crude']){assert.equal(pageGroup(page),'Trading');assert.equal(allPages.includes(page),false);assert.equal(pageAvailable(page,['ReadMarketData','ReadTrades']),true);}
});

test('every page belongs to exactly one group and Overview comes first',()=>{
 const pages=navigationGroups.flatMap(g=>g.pages);
 assert.equal(new Set(pages).size,pages.length);assert.equal(navigationGroups[0].label,'Overview');assert.equal(pageGroup('dashboard'),'Overview');
 assert.equal(pageGroup('parties'),'Settings');assert.equal(pageGroup('admin'),'Admin');assert.equal(pageGroup('jobs'),'Operations');
});
test('the groups and their pages keep the sidebar order',()=>{
 assert.deepEqual(navigationGroups.map(g=>[g.id,g.label,[...g.pages]]),[
  ['overview','Overview',['dashboard']],
  ['trading','Trading',['futures']],
  ['markets','Markets',['market','custom-curves','delivery','interval-series','vol-surfaces','interval-curves']],
  ['operations','Operations',['physical-operations','physical','risk','jobs']],
  ['settings','Settings',['schedules','power','reference','parties']],
  ['admin','Admin',['admin','ai']]]);
});
test('PPA bookings is a Trading page the Blotter opens, not a sidebar entry',()=>{
 assert.equal(pageGroup('ppa'),'Trading');assert.ok(!allPages.includes('ppa'));
 assert.equal(pageAvailable('ppa',['ReadMarketData','ReadTrades']),true);assert.equal(pageAvailable('ppa',['ReadMarketData']),false);
});
test('facility quantities require explicit complete-pool access',()=>{
 assert.equal(pageAvailable('facilities',['ReadMarketData','ReadTrades','ReadFacilityQuantities']),true);
 for(const permissions of [[],['ReadMarketData','ReadTrades'],['ReadFacilityQuantities']])assert.equal(pageAvailable('facilities',permissions),false);
});
test('one physical workspace groups delivery setup and facilities without widening pool authority',()=>{
 assert.equal(pageAvailable('physical',['ReadMarketData']),true);
 assert.equal(pageAvailable('physical',['ReadTrades','ReadFacilityQuantities']),false);
 for(const page of ['gas-setup','crude-setup','facilities']){
  assert.equal(pageGroup(page),'Operations');assert.ok(!allPages.includes(page));
 }
 assert.equal(pageAvailable('gas-setup',['ReadMarketData']),true);
 assert.equal(pageAvailable('crude-setup',['ReadMarketData']),true);
 assert.equal(pageAvailable('facilities',['ReadMarketData']),false);
});
test('a page outside every group is refused instead of given a default heading',()=>{
 assert.throws(()=>pageGroup('unknown'),/could not load/);
});
test('trade readers start on the Blotter; market readers on curves; others by permission',()=>{
 assert.equal(firstPage(['ReadMarketData','ReadTrades']),'futures');
 assert.equal(firstPage(['ReadMarketData']),'market');
 assert.equal(firstPage(['ManageAccess','ManageIdentities']),'admin');
 assert.equal(firstPage([]),null);
});
test('each first page needs every permission its navigation entry needs',()=>{
 // The Blotter lists trades valued against market data, so trades alone do not open it.
 assert.equal(firstPage(['ReadTrades']),null);
 assert.equal(firstPage(['ManageAccess']),'admin');
 assert.equal(pageAvailable('admin',['ManageAccess']),true,'access policy administration must not require identity administration');
 assert.equal(firstPage(['ManageIdentities']),null);
 assert.equal(firstPage(['ReadTradeParties','RunJobs']),null);
});
test('AI settings are the first page when the account holds any AI permission and nothing before it',()=>{
 for(const permission of ['ReadModelUsage','ManageModelProviders','ManageAgents'])assert.equal(firstPage([permission]),'ai');
 assert.equal(firstPage(['ManageAgents','ManageAccess','ManageIdentities']),'admin');
});
test('market data outranks administration and the Blotter outranks plain market reading',()=>{
 assert.equal(firstPage(['ManageAccess','ManageIdentities','ReadMarketData']),'market');
 assert.equal(firstPage(['ManageAgents','ReadMarketData','ReadTrades','ManageAccess','ManageIdentities']),'futures');
});

test('the sidebar lists each page for the permissions that open it',()=>{
 const market=['dashboard','market','custom-curves','delivery','interval-series','interval-curves','schedules','power','physical','reference'];
 assert.deepEqual(listed(['ReadMarketData','RunJobs']).sort(),[...market,'jobs'].sort());
 assert.deepEqual(listed(['ReadMarketData','ReadTrades','ReadTradeParties']).sort(),[...market,'jobs','physical-operations','futures','vol-surfaces','parties'].sort());
 assert.deepEqual(listed(['ReadMarketData','ReadTrades']).sort(),[...market,'jobs','physical-operations','futures','vol-surfaces'].sort());
 assert.deepEqual(listed(['ReadMarketData','ReadTradeParties']).sort(),[...market,'jobs','parties'].sort());
 assert.deepEqual(listed(['ManageAccess','ManageIdentities']),['jobs','admin']);
 assert.deepEqual(listed(['ManageAgents']),['jobs','ai']);
 assert.deepEqual(listed([]),['jobs']);
});
test('dashboard is listed for ReadMarketData accounts only',()=>{
 assert.equal(pageAvailable('dashboard',['ReadMarketData']),true);
 for(const other of [[],['ReadTrades'],['ManageAccess','ManageIdentities'],['RunJobs','ReadTradeParties']])assert.equal(pageAvailable('dashboard',other),false);
 assert.equal(firstPage(['ReadMarketData']),'market');
});
test('a page the sidebar does not know is refused instead of listed',()=>{
 assert.throws(()=>pageAvailable('unknown',['ReadMarketData']),/could not load/);
});

const markup=readFileSync(new URL('../src/index.html',import.meta.url),'utf8');
// Pages entered from the switch beside the contract prices of Curves & prices, not from a sidebar button.
const switched=['custom-curves','interval-curves'];
test('the sidebar markup lists each group and its pages in the navigationGroups order, but for the pages a switch enters',()=>{
 const shown=[...markup.matchAll(/<details id="nav-group-([a-z]+)"[^>]*><summary>([^<]+)<\/summary>(.*?)<\/details>/gs)]
  .map(([,id,label,body])=>({id,label,pages:[...body.matchAll(/<button id="nav-([a-z-]+)"/g)].map(m=>m[1]).filter(page=>page!=='configuration')}));
 assert.deepEqual(shown,navigationGroups.map(({id,label,pages})=>({id,label,pages:pages.filter(page=>!switched.includes(page))})));
 assert.doesNotMatch(markup,/id="(market|settings|trading)-menu"/);
});
test('the pages a switch enters have their entries in that switch, named as the navigation names them',()=>{
 const [,switchMarkup]=markup.match(/<nav id="curve-price-navigation"[^>]*>(.*?)<\/nav>/s);
 assert.deepEqual([...switchMarkup.matchAll(/<button id="(?:nav-|curve-price-)([a-z-]+)"/g)].map(m=>m[1]),['contracts','custom-curves','interval-curves']);
 for(const page of switched)assert.ok(allPages.includes(page));
});

test('risk history needs scenario, trade and market authority',()=>{
 assert.equal(pageAvailable('risk',['ReadMarketData','ReadTrades','RunScenario']),true);
 for(const permissions of [[],['RunScenario'],['ReadMarketData','ReadTrades']])assert.equal(pageAvailable('risk',permissions),false);
});

test('Vol surfaces is a Markets page for readers of both market data and trades',()=>{
 assert.equal(pageGroup('vol-surfaces'),'Markets');
 assert.equal(pageAvailable('vol-surfaces',['ReadMarketData','ReadTrades']),true);
 for(const permissions of [[],['ReadMarketData'],['ReadTrades'],['ReadMarketData','PublishMarketData']])assert.equal(pageAvailable('vol-surfaces',permissions),false);
});

// The builder can fill the viewport; a switch below it is effectively absent.
test('the curve workspace switch precedes every curve view',()=>{
 const switchAt=markup.indexOf('<nav id="curve-price-navigation"');
 for(const id of ['market-page','custom-curves-page','interval-curves-page'])
  assert.ok(switchAt<markup.indexOf(`<section id="${id}"`),`workspace switch must precede ${id}`);
});

test('physical work has its own sidebar workspace under Operations',()=>{
 assert.equal(pageGroup('physical-operations'),'Operations');
 assert.equal(allPages.includes('physical-operations'),true);
 assert.equal(pageAvailable('physical-operations',['ReadMarketData','ReadTrades']),true);
 assert.equal(pageAvailable('physical-operations',['ReadMarketData']),false);

});
