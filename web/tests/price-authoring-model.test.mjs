import test from 'node:test';
import assert from 'node:assert/strict';
import {publicationIndicesFor,restoreAuthoringRequest,publicationVersion,latestPublications,publicationsOfIndex} from '../dist/price-authoring-model.js';

test('publication choices retain exact version and exclude another commodity or quote',()=>{
 const indices=[{code:'hh',commodity:'gas',quote:'usd_mmbtu'},{code:'power',commodity:'power',quote:'usd_mwh'},{code:'gas_eur',commodity:'gas',quote:'eur_mwh'}];
 assert.deepEqual(publicationIndicesFor({indices},'gas','usd_mmbtu'),[indices[0]]);
 assert.deepEqual(publicationIndicesFor({indices},'oil','usd_bbl'),[]);
});
test('editing a publication appends a version without altering exact index references',()=>{
 const original={key:{catalogId:'catalog',id:'publication',version:3},indices:[{key:{code:'hh',revision:{revisionId:'sealed'}}}]};
 const next=publicationVersion(original);
 assert.equal(next.key.version,4);assert.equal(original.key.version,3);
 next.indices[0].key.code='changed';assert.equal(original.indices[0].key.code,'hh');
});
test('unconfirmed authoring writes restore the original request and never cross editor endpoints',()=>{
 const request={path:'contract-price-terms/save',operationId:'request',body:{snapshotId:'old-close',curveId:'curve',version:2,terms:{index:'hh'}},uncertain:true};
 const restored=restoreAuthoringRequest(request,'contract-price-terms/save');
 assert.deepEqual(restored,request);request.body.terms.index='new';assert.equal(restored.body.terms.index,'hh');
 assert.equal(restoreAuthoringRequest(request,'price-publications/save'),undefined);
 assert.equal(restoreAuthoringRequest({...request,operationId:''},request.path),undefined);
 assert.equal(restoreAuthoringRequest({...request,body:[]},request.path),undefined);
});

const version=(id,number,name,indices,publisher='Synthetic publisher')=>({key:{id,version:number},name,publisher:{name:publisher},indices:indices.map(([code,symbol])=>({key:{code},quote:{code:'synthetic-quote'},symbol,statistic:'final'}))});
test('each publication counts once, by its latest version',()=>{
 const listed=[version('daily',1,'Synthetic daily',[['gas-hub','OLD']]),version('monthly',1,'Synthetic monthly',[]),version('daily',2,'Synthetic daily report',[['gas-hub','NEW']])];
 assert.deepEqual(latestPublications(listed).map(d=>[d.key.id,d.key.version]),[['daily',2],['monthly',1]]);
});
test('an index names the publications that publish it now, with the publisher and its symbol there',()=>{
 const listed=[version('daily',1,'Synthetic daily',[['gas-hub','OLD'],['gas-basis','BASIS']]),version('daily',2,'Synthetic daily report',[['gas-hub','NEW']]),
  version('other',1,'Synthetic second report',[['gas-hub','SECOND']],'Synthetic second publisher'),version('monthly',1,'Synthetic monthly',[['gas-month','M']])];
 assert.deepEqual(publicationsOfIndex(listed,'gas-hub'),[
  {id:'daily',version:2,name:'Synthetic daily report',publisher:'Synthetic publisher',symbol:'NEW',statistic:'final'},
  {id:'other',version:1,name:'Synthetic second report',publisher:'Synthetic second publisher',symbol:'SECOND',statistic:'final'}]);
 assert.deepEqual(publicationsOfIndex(listed,'gas-basis'),[],'an index only an earlier version published is published by none now');
});
