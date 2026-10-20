import test from 'node:test';
import assert from 'node:assert/strict';
import {validateMarketNavigation,navigationGroups} from '../dist/market-navigation.js';
import {frameCatalogNote} from '../dist/frame-catalog.js';

const navigation={kind:'market_navigation',profile:'viewer',context:null,
 definitionRevision:{catalogId:'catalog',revisionId:'definitions'},referenceRevision:{catalogId:'reference',revisionId:'revision'},
 futuresConvention:'explicit_convention',example:null,initialFrameIds:['frame'],frames:[{id:'frame',name:'Original',at:'2020-12-18T16:00:00Z'}],
 curveNames:[{code:'future',name:'Stored futures'},{code:'forward',name:'Stored forwards'},{code:'rates',name:'Stored rates'}],
 curves:[{code:'future',name:'Stored futures',marketType:'futures',category:{code:'gas',name:'Synthetic gas'}},
 {code:'forward',name:'Stored forwards',marketType:'forwards',category:{code:'power',name:'Synthetic power'}},
 {code:'rates',name:'Stored rates',marketType:'rates',category:{code:'rates',name:'Interest rates'}}]};
test('navigation groups explicit server classifications without executable workspace data',()=>{
 const before=JSON.stringify(navigation);validateMarketNavigation(navigation);
 assert.deepEqual(navigationGroups(navigation,'commodity','all','all').map(g=>g.name),['Gas','Interest rates','Power']);
 assert.deepEqual(navigationGroups(navigation,'marketType','gas','futures').flatMap(g=>g.curves.map(c=>c.code)),['future']);
 assert.deepEqual(navigationGroups(navigation,'commodity','gas','forwards'),[]);
 assert.equal(JSON.stringify(navigation),before);
});
test('navigation rejects missing or conflicting metadata rather than inventing a workspace',()=>{
 for(const change of [n=>delete n.kind,n=>delete n.curveNames,n=>delete n.definitionRevision,
  n=>n.curves[0].marketType='unknown',n=>delete n.curves[0].category.name,
  n=>n.curves.push(n.curves[0]),n=>n.frames.push(n.frames[0]),n=>n.initialFrameIds.push('missing'),
  n=>n.context={effectiveOn:'2020-12-17'},n=>n.frames[0].observationIds=[]]){
  const value=structuredClone(navigation);change(value);assert.throws(()=>validateMarketNavigation(value),/navigation/);
 }
});
test('stored closes retain their origin and explicit bounded-catalog policy',()=>{
 const value=structuredClone(navigation);value.frames[0].originCloseId='stored-close';
 assert.throws(()=>validateMarketNavigation(value),/navigation/);
 value.frameCatalog={durableSelection:'exact_recorded_close_pins_v1',selectionPolicy:'preserved_session_plus_latest_visible_closes_v1',maximumFrames:128,durableLimit:108,hasMoreDurableCloses:true};
 assert.equal(validateMarketNavigation(value).frames[0].originCloseId,'stored-close');
 assert.match(frameCatalogNote(value.frameCatalog),/recent/);assert.match(frameCatalogNote(value.frameCatalog),/original inputs/);
 const unsupported=structuredClone(value);unsupported.frameCatalog.durableSelection='infer_latest';assert.throws(()=>validateMarketNavigation(unsupported),/navigation/);
});
