import test from 'node:test';
import assert from 'node:assert/strict';
import {publicationIndicesFor,restoreAuthoringRequest,publicationVersion} from '../dist/price-authoring-model.js';

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
