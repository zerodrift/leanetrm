import test from 'node:test';
import assert from 'node:assert/strict';
import {financialReferenceFields} from '../dist/financial-provenance.js';
test('export identities preserve catalog, revision and denomination separately from display labels and trade quantity',()=>{
 const currency={revision:{catalogId:'c',revisionId:'r'},code:'gbp'},quantity={revision:{catalogId:'c',revisionId:'r'},code:'contract'},energy={revision:{catalogId:'c',revisionId:'r'},code:'gj'};
 const row={currencyReference:currency,quantitySpecification:quantity,priceUnitReference:{currency,quantity:energy,multiplier:'0.01'}};
 const fields=financialReferenceFields(row);
 assert.equal(fields.currencyCode,'gbp');assert.equal(fields.currencyRevisionId,'r');assert.equal(fields.quantityCode,'contract');assert.equal(fields.priceQuantityCode,'gj');assert.equal(fields.priceMultiplier,'0.01');
 assert.deepEqual(financialReferenceFields({...row,currencyReference:{...currency,revision:{catalogId:'other',revisionId:'r'}}}).currencyCatalogId,'other');
 assert.equal(financialReferenceFields({currencyReference:currency,quantitySpecification:quantity}).priceQuantityCode,'');
});
