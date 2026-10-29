import test from 'node:test';
import assert from 'node:assert/strict';
import {collapse,defaultLayout,moveTab,restore} from '../dist/curve-dock-model.js';

const tabs=['outputs','graph','configuration','versions','lineage'];
const group=(list,active=list[0])=>({kind:'group',tabs:list,active});
const split=(direction,a,b,ratio=0.5)=>({kind:'split',direction,ratio,children:[a,b]});
const allTabs=l=>l.kind==='group'?l.tabs:l.children.flatMap(allTabs);
const depth=l=>l.kind==='group'?0:1+Math.max(...l.children.map(depth));

test('the default layout is one group of every tab with Outputs selected',()=>{
 assert.deepEqual(defaultLayout(tabs),group(tabs,'outputs'));
});

test('dropping on a right edge makes a row split of the target and the moved tab, half and half',()=>{
 const l=moveTab(defaultLayout(tabs),'graph',{group:0,edge:'right'});
 assert.deepEqual(l,split('row',group(['outputs','configuration','versions','lineage'],'outputs'),group(['graph'])));
});

test('left and top edges put the moved tab first; top and bottom make columns',()=>{
 const base=defaultLayout(['a','b','outputs']);
 assert.deepEqual(moveTab(base,'a',{group:0,edge:'left'}),split('row',group(['a']),group(['b','outputs'],'outputs')));
 assert.deepEqual(moveTab(base,'a',{group:0,edge:'top'}),split('column',group(['a']),group(['b','outputs'],'outputs')));
 assert.deepEqual(moveTab(base,'a',{group:0,edge:'bottom'}),split('column',group(['b','outputs'],'outputs'),group(['a'])));
});

test('dropping on a centre moves the tab into that group and selects it',()=>{
 const two=moveTab(defaultLayout(['outputs','graph','lineage']),'graph',{group:0,edge:'right'});
 const l=moveTab(two,'lineage',{group:1,edge:'centre'});
 assert.deepEqual(l,split('row',group(['outputs'],'outputs'),group(['graph','lineage'],'lineage')));
});

test('a group that loses its last tab closes and its sibling takes its place',()=>{
 const two=moveTab(defaultLayout(['outputs','graph']),'graph',{group:0,edge:'right'});
 assert.deepEqual(moveTab(two,'graph',{group:0,edge:'centre'}),group(['outputs','graph'],'graph'));
});

test('moving the selected tab away selects a neighbour in the group it left',()=>{
 const l=moveTab(defaultLayout(tabs),'outputs',{group:0,edge:'right'});
 assert.equal(l.children[0].active,'graph');
});

test('moves that change nothing return the layout as it was',()=>{
 const one=defaultLayout(tabs);
 assert.deepEqual(moveTab(one,'graph',{group:0,edge:'centre'}),one);
 const solo=moveTab(defaultLayout(['outputs','graph']),'graph',{group:0,edge:'right'});
 assert.deepEqual(moveTab(solo,'graph',{group:1,edge:'left'}),solo);
 assert.deepEqual(moveTab(one,'graph',{group:7,edge:'right'}),one);
 assert.deepEqual(moveTab(one,'nothing',{group:0,edge:'right'}),one);
});

test('groups are numbered depth first',()=>{
 let l=moveTab(defaultLayout(tabs),'graph',{group:0,edge:'right'}); // [outputs..] | [graph]
 l=moveTab(l,'versions',{group:1,edge:'bottom'}); // graph above versions, group 1 and 2
 assert.deepEqual(l.children[1],split('column',group(['graph']),group(['versions'])));
 l=moveTab(l,'lineage',{group:0,edge:'centre'});
 assert.deepEqual(l.children[0].tabs,['outputs','configuration','lineage']);
});

test('splits nest at most two levels; a deeper drop lands in the target centre',()=>{
 let l=moveTab(defaultLayout(tabs),'graph',{group:0,edge:'right'});
 l=moveTab(l,'versions',{group:1,edge:'bottom'});
 assert.equal(depth(l),2);
 const deeper=moveTab(l,'lineage',{group:2,edge:'right'});
 assert.equal(depth(deeper),2);
 assert.deepEqual(deeper.children[1].children[1],group(['versions','lineage'],'lineage'));
});

test('restore returns a valid saved layout with every tab once',()=>{
 const saved=split('column',group(['outputs','graph'],'graph'),group(['configuration','versions','lineage'],'lineage'),0.3);
 assert.deepEqual(restore(JSON.parse(JSON.stringify(saved)),tabs),saved);
});

test('restores default for unknown or missing tabs',()=>{
 const def=defaultLayout(tabs);
 assert.deepEqual(restore(split('row',group(['outputs','graph','configuration','versions']),group(['lineage','retired'])),tabs),def,'unknown tab');
 assert.deepEqual(restore(group(['outputs','graph']),tabs),def,'missing tabs');
 assert.deepEqual(restore(split('row',group(tabs.slice(0,3)),group(tabs.slice(2))),tabs),def,'a tab twice');
});

test('restore falls back to the default for anything that is not a layout',()=>{
 const def=defaultLayout(tabs);
 for(const bad of [null,undefined,'x',7,[],{},{kind:'group'},group([],'outputs'),group(tabs,'missing'),
  {kind:'split',direction:'diagonal',ratio:0.5,children:[group(['outputs']),group(tabs.slice(1))]},
  {kind:'split',direction:'row',ratio:0.5,children:[group(['outputs'])]},
  {kind:'split',direction:'row',ratio:'half',children:[group(['outputs']),group(tabs.slice(1))]},
  {kind:'split',direction:'row',ratio:NaN,children:[group(['outputs']),group(tabs.slice(1))]},
  {kind:'group',tabs:[1,2,3,4,5],active:1}])assert.deepEqual(restore(bad,tabs),def,JSON.stringify(bad));
});

test('restore falls back to the default beyond two levels of split',()=>{
 const deep=split('row',group(['outputs']),split('column',group(['graph']),split('row',group(['configuration']),group(['versions','lineage']))));
 assert.deepEqual(restore(deep,tabs),defaultLayout(tabs));
});

test('restore clamps the ratio to between 0.2 and 0.8',()=>{
 const make=r=>split('row',group(['outputs']),group(tabs.slice(1)),r);
 assert.equal(restore(make(0.01),tabs).ratio,0.2);
 assert.equal(restore(make(0.99),tabs).ratio,0.8);
 assert.equal(restore(make(0.6),tabs).ratio,0.6);
});

test('collapse keeps every tab once in order and the active tab',()=>{
 let l=moveTab(defaultLayout(tabs),'graph',{group:0,edge:'right'});
 l=moveTab(l,'versions',{group:1,edge:'bottom'});
 const one=collapse(l);
 assert.equal(one.kind,'group');
 assert.deepEqual([...one.tabs].sort(),[...tabs].sort());
 assert.deepEqual(one.tabs,allTabs(l));
 assert.equal(one.active,'outputs');
 assert.deepEqual(collapse(defaultLayout(tabs)),defaultLayout(tabs));
});
