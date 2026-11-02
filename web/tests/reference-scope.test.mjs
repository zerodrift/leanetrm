import test from 'node:test';
import assert from 'node:assert/strict';
import {sameReferenceSource} from '../dist/reference-scope.js';
const source={referenceRevision:{catalogId:'reference',revisionId:'browser-revision'},definitionRevisionId:'browser-definitions'};
const owner={profile:'reader',referenceSources:{interest_rate_definitions:source}};
const read={...source,profile:'reader',context:null};
test('reference reads use the category’s explicitly declared catalog, independently of pricing definitions',()=>{
 assert.equal(sameReferenceSource(owner,'interest_rate_definitions',read,null),true);
 for(const changed of [{referenceRevision:{catalogId:'other',revisionId:'browser-revision'}},{referenceRevision:{catalogId:'reference',revisionId:'pricing-revision'}},{definitionRevisionId:'pricing-definitions'},{profile:'other'},{context:{contextId:'other'}}])
  assert.equal(sameReferenceSource(owner,'interest_rate_definitions',{...read,...changed},null),false);
 assert.equal(sameReferenceSource(owner,'currencies',read,null),false);
 assert.equal(sameReferenceSource({...owner,referenceSources:undefined},'interest_rate_definitions',read,null),false);
});
