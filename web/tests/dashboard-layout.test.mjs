import test from 'node:test';
import assert from 'node:assert/strict';
import {addWidget,removeWidget,moveOnBoard,moveToSection,arrange,morningSections,homeSection,endOfSection} from '../dist/dashboard-layout.js';

const widget=(id,kind,config,section,order,board={x:0,y:0,w:4,h:3})=>({id,kind,config,board,morning:{section,order}});
const levels=widget('00000000-0000-4000-8000-000000000001','latest_levels',{curves:['hh','ttf']},'prices_positions',0);
const chart=widget('00000000-0000-4000-8000-000000000002','price_chart',{curve:'hh',contractId:'00000000-0000-4000-8000-0000000000c1'},'prices_positions',1,{x:4,y:0,w:8,h:4});
const ladder=widget('00000000-0000-4000-8000-000000000003','positions',{definitionId:'00000000-0000-4000-8000-0000000000d1'},'prices_positions',2,{x:0,y:3,w:4,h:4});
const layout=(...widgets)=>({version:1,arrangement:'board',widgets});

test('arrange keeps every widget and its config',()=>{
  const start=layout(levels,chart,ladder);
  const morning=arrange(start,'morning');
  assert.equal(morning.arrangement,'morning');
  assert.deepEqual(arrange(morning,'board'),start);
  assert.deepEqual(morning.widgets,start.widgets);
});

test('morning sections follow the fixed order and show empty sections',()=>{
  const sections=morningSections(layout(levels,chart));
  assert.deepEqual(sections.map(s=>[s.section,s.label]),[['weather_events','Weather & events'],['inventories','Inventories'],['prices_positions','Prices & positions'],['missed','Might have missed']]);
  assert.deepEqual(sections.map(s=>s.widgets.length),[0,0,2,0]);
});

test('widgets in a section are listed by their order',()=>{
  const sections=morningSections(layout(chart,ladder,levels));
  assert.deepEqual(sections[2].widgets.map(w=>w.id),[levels.id,chart.id,ladder.id]);
});

test('a widget moved to another section stays there after arranging',()=>{
  const moved=moveToSection(layout(levels,chart),chart.id,'missed',0);
  const again=arrange(arrange(moved,'morning'),'board');
  assert.deepEqual(morningSections(again).map(s=>s.widgets.map(w=>w.id)),[[],[],[levels.id],[chart.id]]);
  assert.deepEqual(again.widgets.find(w=>w.id===levels.id),levels);
});

test('every home section is Prices & positions for the phase 1 kinds',()=>{
  for(const kind of ['latest_levels','price_chart','positions'])assert.equal(homeSection(kind),'prices_positions');
});

test('adding, moving and removing change only the named widget',()=>{
  const added=addWidget(layout(levels),chart);
  assert.deepEqual(added.widgets,[levels,chart]);
  const box={x:2,y:5,w:6,h:2};
  const moved=moveOnBoard(added,chart.id,box);
  assert.deepEqual(moved.widgets,[levels,{...chart,board:box}]);
  assert.deepEqual(removeWidget(moved,chart.id).widgets,[levels]);
});

test('the original layout is not changed by an edit',()=>{
  const start=layout(levels);
  const snapshot=structuredClone(start);
  addWidget(start,chart);moveOnBoard(start,levels.id,{x:1,y:1,w:1,h:1});moveToSection(start,levels.id,'missed',3);removeWidget(start,levels.id);arrange(start,'morning');
  assert.deepEqual(start,snapshot);
});

test('a widget the layout does not hold, or one added twice, is refused',()=>{
  assert.throws(()=>removeWidget(layout(levels),chart.id),/not on the dashboard/);
  assert.throws(()=>moveOnBoard(layout(levels),chart.id,{x:0,y:0,w:1,h:1}),/not on the dashboard/);
  assert.throws(()=>moveToSection(layout(levels),chart.id,'missed',0),/not on the dashboard/);
  assert.throws(()=>addWidget(layout(levels),levels),/already on the dashboard/);
});

test('a section ends after its last order, and an empty section starts at zero',()=>{
  assert.equal(endOfSection(layout(levels,chart,ladder),'prices_positions'),3);
  assert.equal(endOfSection(layout(levels),'missed'),0);
});
