import test from 'node:test';
import assert from 'node:assert/strict';
import {validateMarketNavigation,navigationGroups,navigationCategories,navigationCurveKind} from '../dist/market-navigation.js';
import {frameCatalogNote} from '../dist/frame-catalog.js';

const navigation={kind:'market_navigation',kindsVersion:'curve_kinds_v1',profile:'viewer',context:null,
 definitionRevision:{catalogId:'catalog',revisionId:'definitions'},referenceRevision:{catalogId:'reference',revisionId:'revision'},
 futuresConvention:'explicit_convention',example:null,initialFrameIds:['frame'],frames:[{id:'frame',name:'Original',at:'2020-12-18T16:00:00Z'}],
 curveNames:[{code:'future',name:'Stored futures'},{code:'forward',name:'Stored forwards'},{code:'rates',name:'Stored rates'}],
 curves:[{code:'future',name:'Stored futures',marketType:'futures',category:{code:'gas',name:'Synthetic gas'},kind:'market_settlement'},
 {code:'forward',name:'Stored forwards',marketType:'forwards',category:{code:'power',name:'Synthetic power'},kind:'basis_spread'},
 {code:'rates',name:'Stored rates',marketType:'rates',category:null,kind:'interest_rate'}]};
test('navigation groups explicit server classifications without executable workspace data',()=>{
 const before=JSON.stringify(navigation);validateMarketNavigation(navigation);
 assert.deepEqual(navigationGroups(navigation,'commodity',null,'all').map(g=>g.name),['Gas','Interest rates','Power']);
 assert.deepEqual(navigationGroups(navigation,'marketType','gas','futures').flatMap(g=>g.curves.map(c=>c.code)),['future']);
 assert.deepEqual(navigationGroups(navigation,'commodity','gas','forwards'),[]);
 assert.equal(JSON.stringify(navigation),before);
});
test('curves within a group follow display-name order, not code or received order',()=>{
 const curve=(code,name)=>({code,name,marketType:'forwards',category:{code:'gas',name:'Synthetic gas'}});
 // Code order, both received orders and code-unit order all differ from the collated name order.
 const curves=[curve('b_spread','Regional spread'),curve('c_all_in','Hub · all-in prices'),curve('a_hub','Hub forwards')];
 for(const received of [curves,[...curves].reverse()])
  assert.deepEqual(navigationGroups({...navigation,curves:received},'commodity',null,'all').map(g=>[g.name,g.curves.map(c=>c.name)]),
   [['Gas',['Hub · all-in prices','Hub forwards','Regional spread']]]);
});
test('market filter choices follow display-name order, not code or received order',()=>{
 const curve=(code,category)=>({code,name:code,marketType:'forwards',category});
 const power={code:'a_power',name:'Synthetic power'},ethane={code:'z_ethane',name:'Synthetic éthane'},gas={code:'m_gas',name:'Synthetic gas'};
 // Éthane collates with E but follows every ASCII letter in code-unit order.
 const curves=[curve('one',gas),curve('two',power),curve('three',ethane),curve('four',power)];
 for(const received of [curves,[...curves].reverse()])
  assert.deepEqual(navigationCategories({...navigation,curves:received}),
   [{code:'z_ethane',name:'Éthane'},{code:'m_gas',name:'Gas'},{code:'a_power',name:'Power'}]);
});
test('interest-rate curves stay apart from a commodity coded or named like them',()=>{
 const value={...navigation,curves:[...navigation.curves,{code:'rate_future',name:'Stored rate futures',marketType:'futures',category:{code:'rates',name:'Interest rates'},kind:'trader_mark'}]};
 validateMarketNavigation(value);
 const groups=navigationGroups(value,'commodity',null,'all');
 // Equal names keep the directory's order.
 assert.deepEqual(groups.map(g=>[g.name,g.curves.map(c=>c.code)]),[['Gas',['future']],['Interest rates',['rates']],['Interest rates',['rate_future']],['Power',['forward']]]);
 assert.equal(new Set(groups.map(g=>g.key)).size,groups.length);
 assert.deepEqual(navigationGroups(value,'marketType',null,'all').map(g=>[g.name,g.curves.map(c=>c.code)]),[['Forward curves',['forward']],['Futures',['future','rate_future']],['Interest rates',['rates']]]);
 assert.deepEqual(navigationCategories(value).map(c=>c.code),['gas','rates','power']);
 assert.deepEqual(navigationGroups(value,'commodity','rates','all').flatMap(g=>g.curves.map(c=>c.code)),['rate_future']);
});
test('all markets is no commodity code, so a commodity coded all can be chosen alone',()=>{
 const value={...navigation,curves:[...navigation.curves,{code:'all_future',name:'Stored all futures',marketType:'futures',category:{code:'all',name:'Synthetic all'},kind:'scaled'}]};
 validateMarketNavigation(value);
 assert.deepEqual(navigationGroups(value,'commodity','all','all').flatMap(g=>g.curves.map(c=>c.code)),['all_future']);
 assert.deepEqual(navigationGroups(value,'commodity',null,'all').flatMap(g=>g.curves.map(c=>c.code)).sort(),['all_future','forward','future','rates']);
});
test('curve details name the commodity only for commodity curves',()=>{
 const rateFuture={code:'rate_future',name:'Stored rate futures',marketType:'futures',category:{code:'rates',name:'Interest rates'}};
 assert.deepEqual([...navigation.curves,rateFuture].map(navigationCurveKind),['Gas · Futures','Power · Forward curves','Interest rates','Interest rates · Futures']);
});
test('navigation rejects missing or conflicting metadata rather than inventing a workspace',()=>{
 for(const change of [n=>delete n.kind,n=>delete n.curveNames,n=>delete n.definitionRevision,
  n=>n.curves[0].marketType='unknown',n=>delete n.curves[0].category.name,n=>n.curves[0].category=null,
  n=>n.curves[2].category={code:'rates',name:'Interest rates'},n=>delete n.curves[2].category,
  n=>n.curves.push({code:'other_gas',name:'Stored other gas',marketType:'forwards',category:{code:'gas',name:'Other gas'}}),
  n=>n.curves.push(n.curves[0]),n=>n.frames.push(n.frames[0]),n=>n.initialFrameIds.push('missing'),
  n=>n.context={effectiveOn:'2020-12-17'},n=>n.frames[0].observationIds=[],
  n=>delete n.curves[0].kind,n=>n.curves[0].kind='shaped',n=>n.curves[0].kind='toString',n=>delete n.kindsVersion,n=>n.kindsVersion='curve_kinds_v2']){
  const value=structuredClone(navigation);change(value);assert.throws(()=>validateMarketNavigation(value),/navigation/);
 }
 validateMarketNavigation({...navigation,curves:[...navigation.curves,{code:'second_gas',name:'Stored second gas',marketType:'forwards',category:{code:'gas',name:'Synthetic gas'},kind:'custom'}]});
});
test('stored closes retain their origin and explicit bounded-catalog policy',()=>{
 const value=structuredClone(navigation);value.frames[0].originCloseId='stored-close';
 assert.throws(()=>validateMarketNavigation(value),/navigation/);
 value.frameCatalog={durableSelection:'exact_recorded_close_pins_v1',selectionPolicy:'preserved_session_plus_latest_visible_closes_v1',maximumFrames:128,durableLimit:108,hasMoreDurableCloses:true};
 assert.equal(validateMarketNavigation(value).frames[0].originCloseId,'stored-close');
 assert.match(frameCatalogNote(value.frameCatalog),/recent/);assert.match(frameCatalogNote(value.frameCatalog),/original inputs/);
 const unsupported=structuredClone(value);unsupported.frameCatalog.durableSelection='infer_latest';assert.throws(()=>validateMarketNavigation(unsupported),/navigation/);
});
