import test from 'node:test';
import assert from 'node:assert/strict';
import {commodityName,quoteUnit} from '../dist/market-view.js';

const workspace={definitions:{revisions:[{reference_catalog_id:'catalog',reference_revision_id:'original'}]},references:{commodities:[{code:'power',name:'Original power'}],currencies:[{code:'usd',display_code:'USD'}],units:[{code:'mwh',symbol:'old symbol'}],quantity_specifications:[{code:'power_energy',unit_code:'mwh'}],quote_specifications:[{code:'power_price',quantity_code:'power_energy',currency_code:'usd',multiplier:'1'}]},referenceDisplay:{sourceRevision:{catalogId:'catalog',revisionId:'original'},displayRevision:{catalogId:'catalog',revisionId:'labels'},version:1,references:{commodities:[{code:'power',name:'Updated power'}],units:[{code:'mwh',symbol:'MWh'}]}}};

test('current curve labels use explicitly applied reference metadata without changing financial rows',()=>{
 const before=JSON.stringify(workspace);
 assert.equal(commodityName('power',workspace),'Updated power');
 assert.equal(quoteUnit('power_price',workspace),'USD / MWh');
 assert.equal(JSON.stringify(workspace),before);
 assert.equal(workspace.references.units[0].symbol,'old symbol');
});

test('an absent historical overlay retains the exact original labels',()=>{
 const historical={...workspace,referenceDisplay:null};
 assert.equal(commodityName('power',historical),'Original power');
 assert.equal(quoteUnit('power_price',historical),'USD / old symbol');
});

test('presentation rejects a display revision attached to another financial revision',()=>{
 const wrong={...workspace,referenceDisplay:{...workspace.referenceDisplay,sourceRevision:{catalogId:'catalog',revisionId:'other'}}};
 assert.throws(()=>commodityName('power',wrong),/inconsistent/i);
});
