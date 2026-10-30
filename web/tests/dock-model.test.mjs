import test from 'node:test';
import assert from 'node:assert/strict';
import {collapse,defaultLayout,moveTab,resize,restoreArrangement,select,splitsOf,toggleCollapsed} from '../dist/dock-model.js';

const tabs=['outputs','graph','configuration','versions','lineage'];
const rules={maxDepth:2,fixed:[],ratioRange:[0.2,0.8]};
const layoutOf=(saved,list=tabs)=>restoreArrangement({layout:saved,collapsed:[]},list,rules,{layout:defaultLayout(list,'outputs'),collapsed:[]}).layout;
const group=(list,active=list[0])=>({kind:'group',tabs:list,active});
const split=(direction,a,b,ratio=0.5)=>({kind:'split',direction,ratio,children:[a,b]});
const allTabs=l=>l.kind==='group'?l.tabs:l.children.flatMap(allTabs);
const groupsIn=l=>l.kind==='group'?[l]:l.children.flatMap(groupsIn);
const depth=l=>l.kind==='group'?0:1+Math.max(...l.children.map(depth));

test('the default layout is one group of every tab with Outputs selected',()=>{
 assert.deepEqual(defaultLayout(tabs,'outputs'),group(tabs,'outputs'));
});

test('dropping on a right edge makes a row split of the target and the moved tab, half and half',()=>{
 const l=moveTab(defaultLayout(tabs,'outputs'),'graph',{group:0,edge:'right'},rules);
 assert.deepEqual(l,split('row',group(['outputs','configuration','versions','lineage'],'outputs'),group(['graph'])));
});

test('left and top edges put the moved tab first; top and bottom make columns',()=>{
 const base=defaultLayout(['a','b','outputs'],'outputs');
 assert.deepEqual(moveTab(base,'a',{group:0,edge:'left'},rules),split('row',group(['a']),group(['b','outputs'],'outputs')));
 assert.deepEqual(moveTab(base,'a',{group:0,edge:'top'},rules),split('column',group(['a']),group(['b','outputs'],'outputs')));
 assert.deepEqual(moveTab(base,'a',{group:0,edge:'bottom'},rules),split('column',group(['b','outputs'],'outputs'),group(['a'])));
});

test('dropping on a centre moves the tab into that group and selects it',()=>{
 const two=moveTab(defaultLayout(['outputs','graph','lineage'],'outputs'),'graph',{group:0,edge:'right'},rules);
 const l=moveTab(two,'lineage',{group:1,edge:'centre'},rules);
 assert.deepEqual(l,split('row',group(['outputs'],'outputs'),group(['graph','lineage'],'lineage')));
});

test('a group that loses its last tab closes and its sibling takes its place',()=>{
 const two=moveTab(defaultLayout(['outputs','graph'],'outputs'),'graph',{group:0,edge:'right'},rules);
 assert.deepEqual(moveTab(two,'graph',{group:0,edge:'centre'},rules),group(['outputs','graph'],'graph'));
});

test('moving the selected tab away selects a neighbour in the group it left',()=>{
 const l=moveTab(defaultLayout(tabs,'outputs'),'outputs',{group:0,edge:'right'},rules);
 assert.equal(l.children[0].active,'graph');
});

test('moves that change nothing return the layout as it was',()=>{
 const one=defaultLayout(tabs,'outputs');
 assert.deepEqual(moveTab(one,'graph',{group:0,edge:'centre'},rules),one);
 const solo=moveTab(defaultLayout(['outputs','graph'],'outputs'),'graph',{group:0,edge:'right'},rules);
 assert.deepEqual(moveTab(solo,'graph',{group:1,edge:'left'},rules),solo);
 assert.deepEqual(moveTab(one,'graph',{group:7,edge:'right'},rules),one);
 assert.deepEqual(moveTab(one,'nothing',{group:0,edge:'right'},rules),one);
});

test('groups are numbered depth first',()=>{
 let l=moveTab(defaultLayout(tabs,'outputs'),'graph',{group:0,edge:'right'},rules); // [outputs..] | [graph]
 l=moveTab(l,'versions',{group:1,edge:'bottom'},rules); // graph above versions, group 1 and 2
 assert.deepEqual(l.children[1],split('column',group(['graph']),group(['versions'])));
 l=moveTab(l,'lineage',{group:0,edge:'centre'},rules);
 assert.deepEqual(l.children[0].tabs,['outputs','configuration','lineage']);
});

test('splits nest at most two levels; a deeper drop lands in the target centre',()=>{
 let l=moveTab(defaultLayout(tabs,'outputs'),'graph',{group:0,edge:'right'},rules);
 l=moveTab(l,'versions',{group:1,edge:'bottom'},rules);
 assert.equal(depth(l),2);
 const deeper=moveTab(l,'lineage',{group:2,edge:'right'},rules);
 assert.equal(depth(deeper),2);
 assert.deepEqual(deeper.children[1].children[1],group(['versions','lineage'],'lineage'));
});

test('restore returns a valid saved layout with every tab once',()=>{
 const saved=split('column',group(['outputs','graph'],'graph'),group(['configuration','versions','lineage'],'lineage'),0.3);
 assert.deepEqual(layoutOf(JSON.parse(JSON.stringify(saved))),saved);
});

test('restores default for unknown or missing tabs',()=>{
 const def=defaultLayout(tabs,'outputs');
 assert.deepEqual(layoutOf(split('row',group(['outputs','graph','configuration','versions']),group(['lineage','retired'])),tabs),def,'unknown tab');
 assert.deepEqual(layoutOf(group(['outputs','graph']),tabs),def,'missing tabs');
 assert.deepEqual(layoutOf(split('row',group(tabs.slice(0,3)),group(tabs.slice(2))),tabs),def,'a tab twice');
});

test('restore falls back to the default for anything that is not a layout',()=>{
 const def=defaultLayout(tabs,'outputs');
 for(const bad of [null,undefined,'x',7,[],{},{kind:'group'},group([],'outputs'),group(tabs,'missing'),
  {kind:'split',direction:'diagonal',ratio:0.5,children:[group(['outputs']),group(tabs.slice(1))]},
  {kind:'split',direction:'row',ratio:0.5,children:[group(['outputs'])]},
  {kind:'split',direction:'row',ratio:'half',children:[group(['outputs']),group(tabs.slice(1))]},
  {kind:'split',direction:'row',ratio:NaN,children:[group(['outputs']),group(tabs.slice(1))]},
  {kind:'group',tabs:[1,2,3,4,5],active:1}])assert.deepEqual(layoutOf(bad,tabs),def,JSON.stringify(bad));
});

test('restore falls back to the default beyond two levels of split',()=>{
 const deep=split('row',group(['outputs']),split('column',group(['graph']),split('row',group(['configuration']),group(['versions','lineage']))));
 assert.deepEqual(layoutOf(deep,tabs),defaultLayout(tabs,'outputs'));
});

test('restore clamps the ratio to between 0.2 and 0.8',()=>{
 const make=r=>split('row',group(['outputs']),group(tabs.slice(1)),r);
 assert.equal(layoutOf(make(0.01),tabs).ratio,0.2);
 assert.equal(layoutOf(make(0.99),tabs).ratio,0.8);
 assert.equal(layoutOf(make(0.6),tabs).ratio,0.6);
});

test('collapse keeps every tab once in order and the active tab',()=>{
 let l=moveTab(defaultLayout(tabs,'outputs'),'graph',{group:0,edge:'right'},rules);
 l=moveTab(l,'versions',{group:1,edge:'bottom'},rules);
 const one=collapse(l);
 assert.equal(one.kind,'group');
 assert.deepEqual([...one.tabs].sort(),[...tabs].sort());
 assert.deepEqual(one.tabs,allTabs(l));
 assert.equal(one.active,'outputs');
 assert.deepEqual(collapse(defaultLayout(tabs,'outputs')),defaultLayout(tabs,'outputs'));
});

test('select shows a tab in the group that holds it and leaves the other groups as they were',()=>{
 const two=moveTab(defaultLayout(tabs,'outputs'),'graph',{group:0,edge:'right'},rules);
 const l=select(two,'versions');
 assert.deepEqual(l,split('row',group(['outputs','configuration','versions','lineage'],'versions'),group(['graph'])));
 assert.deepEqual(two.children[0].active,'outputs','the layout it was given is unchanged');
 assert.equal(select(two,'retired'),two);
});

test('resize sets one split ratio, numbered depth first, within 0.2 and 0.8',()=>{
 let l=moveTab(defaultLayout(tabs,'outputs'),'graph',{group:0,edge:'right'},rules);
 l=moveTab(l,'versions',{group:1,edge:'bottom'},rules);
 const inner=resize(l,1,0.7,rules);
 assert.equal(inner.ratio,0.5);
 assert.equal(inner.children[1].ratio,0.7);
 assert.equal(resize(l,0,0.05,rules).ratio,0.2);
 assert.equal(resize(l,0,0.95,rules).ratio,0.8);
 assert.equal(resize(l,0,NaN,rules),l);
 assert.equal(resize(l,5,0.4,rules),l);
 assert.equal(l.ratio,0.5,'the layout it was given is unchanged');
});

test('splitsOf numbers splits depth first, each before its children',()=>{
 const inner=split('column',group(['graph']),group(['versions'])),outer=split('row',group(['outputs','configuration','lineage']),inner);
 assert.deepEqual(splitsOf(outer),[outer,inner]);
 const left=split('column',group(['outputs']),group(['graph'])),right=split('column',group(['configuration']),group(['versions','lineage']));
 const both=split('row',left,right);
 assert.deepEqual(splitsOf(both),[both,left,right]);
 assert.deepEqual(splitsOf(group(tabs)),[]);
});

test('a fixed tab cannot be collapsed and other tabs toggle',()=>{
 const rules={maxDepth:3,fixed:['grid'],ratioRange:[0.05,0.95]},a={layout:defaultLayout(['summary','grid','details','charts'],'grid'),collapsed:[]};
 assert.deepEqual(toggleCollapsed(a,'charts',rules).collapsed,['charts']);
 assert.deepEqual(toggleCollapsed(toggleCollapsed(a,'charts',rules),'charts',rules).collapsed,[]);
 assert.throws(()=>toggleCollapsed(a,'grid',rules),/cannot be collapsed/);
 assert.throws(()=>toggleCollapsed(a,'nothing',rules),/Unknown tab/);
});

test('depth follows the rules: three levels for the Blotter, two for Curve builder',()=>{
 const names=['a','b','c','d','e'],deepRules={...rules,maxDepth:3};
 let l=defaultLayout(names,'a');
 l=moveTab(l,'b',{group:0,edge:'right'},deepRules); // a.. | b
 l=moveTab(l,'c',{group:1,edge:'bottom'},deepRules); // b over c
 assert.equal(depth(l),2);
 const three=moveTab(l,'d',{group:2,edge:'right'},deepRules); // a depth-3 split under c
 assert.equal(depth(three),3);
 assert.deepEqual(three.children[1].children[1],split('row',group(['c']),group(['d'])));
 // At depth 3 a group takes a tab only at its centre.
 const centre=moveTab(three,'e',{group:3,edge:'right'},deepRules);
 assert.equal(depth(centre),3);
 assert.deepEqual(groupsIn(centre).find(g=>g.tabs.includes('e')).tabs,['d','e']);
 // With maxDepth 2 the same drop at depth 2 lands in the centre.
 const two=moveTab(l,'d',{group:2,edge:'right'},rules);
 assert.equal(depth(two),2);
 assert.deepEqual(two.children[1].children[1],group(['c','d'],'d'));
 // A saved depth-3 layout restores under maxDepth 3 and not under 2.
 const fallback={layout:defaultLayout(names,'a'),collapsed:[]},saved={layout:three,collapsed:[]};
 assert.deepEqual(restoreArrangement(saved,names,deepRules,fallback),saved);
 assert.deepEqual(restoreArrangement(saved,names,rules,fallback),fallback);
});

test('restoreArrangement refuses unknown, missing or duplicate tabs and a collapsed fixed tab, returning the fallback',()=>{
 const rules={maxDepth:3,fixed:['grid'],ratioRange:[0.05,0.95]},names=['grid','charts','details'];
 const fallback={layout:defaultLayout(names,'grid'),collapsed:[]};
 const ok={layout:split('row',group(['grid']),group(['charts','details'])),collapsed:['details']};
 assert.deepEqual(restoreArrangement(JSON.parse(JSON.stringify(ok)),names,rules,fallback),ok);
 for(const bad of [
  {layout:group(['grid','charts','retired']),collapsed:[]},
  {layout:group(['grid','charts']),collapsed:[]},
  {layout:split('row',group(['grid','charts']),group(['charts','details'])),collapsed:[]},
  {layout:ok.layout,collapsed:['grid']},
  {layout:ok.layout,collapsed:['charts','charts']},
  {layout:ok.layout,collapsed:['retired']},
  {layout:ok.layout},ok.layout,null,'x'])assert.deepEqual(restoreArrangement(bad,names,rules,fallback),fallback,JSON.stringify(bad));
});

test('restoreArrangement clamps the ratio to the rules\' range',()=>{
 const wide={maxDepth:3,fixed:[],ratioRange:[0.05,0.95]},names=['a','b'],fallback={layout:defaultLayout(names,'a'),collapsed:[]};
 const make=r=>restoreArrangement({layout:split('row',group(['a']),group(['b']),r),collapsed:[]},names,wide,fallback).layout.ratio;
 assert.equal(make(0.01),0.05);assert.equal(make(0.99),0.95);
 assert.equal(resize(split('row',group(['a']),group(['b'])),0,0.01,wide).ratio,0.05);
});
