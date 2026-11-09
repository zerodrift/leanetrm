import test from 'node:test';
import assert from 'node:assert/strict';
import {authoringMarket,authoringMarketField} from '../dist/authoring-market.js';
test('source bundle choice retains the exact server selection without choosing by label, position or curve code',()=>{
 const markets=[{revisionId:'original',name:'Original source market'},{revisionId:'supplemental',name:'Explicit supplemental source'}];
 assert.equal(authoringMarket({markets,marketRevisionId:'supplemental'}).revisionId,'supplemental');
 assert.deepEqual(authoringMarketField('supplemental'),{marketRevisionId:'supplemental'});
 assert.throws(()=>authoringMarket({markets,marketRevisionId:'original'},'supplemental'),/inconsistent/);
 assert.throws(()=>authoringMarket({},'supplemental'),/inconsistent/);
 assert.deepEqual(authoringMarket({}),{revisionId:undefined,markets:[]});assert.deepEqual(authoringMarketField(undefined),{});
 for(const catalog of [{markets},{marketRevisionId:'original'},{markets,marketRevisionId:'hidden'},{markets:[markets[0],markets[0]],marketRevisionId:'original'}])assert.throws(()=>authoringMarket(catalog),/inconsistent/);
});
