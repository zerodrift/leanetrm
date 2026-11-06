import {gasDeliveryFixture} from './physical-delivery-fixture.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';

test('the trade grid exposes booked facts without substituting a price or an aggregate gas volume',async()=>{
 const {physicalTradeGrid}=await import('../dist/physical-operations-grid.js');
 const trade={tradeId:'trade',bindingId:'instrument',revision:1,tradeReference:'TR-000042',bookId:'book',book:'Synthetic book',instrumentType:'physical_gas',marketLabel:'Synthetic basis',deliveryFrom:'2021-01-01',deliveryTo:'2021-02-01',bookedQuantity:null,bookedUnit:null};
 const grid=physicalTradeGrid([trade]);
 assert.deepEqual(grid.keys,['trade','commodity','book','from','to','market','quantity','unit','workflow']);
 assert.equal(grid.rows[0][0],'TR-000042');assert.equal(grid.rows[0][1],'Natural gas');assert.equal(grid.rows[0][6],'');assert.equal(grid.displayRows[0][6],'By side');assert.equal(grid.rows[0][8],'Manual operations');
 assert.equal(grid.headers.some(h=>/price$|present value|pnl|pv/i.test(h)),false);
});

test('the delivery grid retains exact fractions, independent tracks, zero and withdrawn sources for every side/day',async()=>{
 const {physicalDeliveryGrid}=await import('../dist/physical-delivery.js');
 const row={obligationId:'one',sideCode:'receipt',day:'2021-01-01',start:'2021-01-01T06:00:00Z',end:'2021-01-02T06:00:00Z',pipeline:'synthetic_pipeline',zone:'synthetic_zone',deliveryPoint:'synthetic_point',quantitySpecification:{code:'synthetic_quantity'},contractual:{numerator:'1000',denominator:'31'},selected:{numerator:'0',denominator:'1'},kind:'allocated',revisionId:'final',heads:[{id:'nom',kind:'nominated',status:'provisional',quantity:'100.000000000000000001',evidence:'Synthetic request',revision:1},{id:'sched',kind:'scheduled',status:'withdrawn',quantity:null,evidence:'Synthetic withdrawal',revision:2},{id:'final',kind:'allocated',status:'final',quantity:'0',evidence:'Synthetic final allocation',revision:3}]};
 const other={...row,obligationId:'two',sideCode:'delivery',day:'2021-01-02',start:'2021-01-02T06:00:00Z',end:'2021-01-03T06:00:00Z',kind:'contractual',revisionId:null,heads:[]};
 const grid=physicalDeliveryGrid(gasDeliveryFixture({expectedRevision:1,rows:[row,other]}),'UTC');const value=(index,key)=>grid.rows[index][grid.keys.indexOf(key)];
 assert.equal(grid.rows.length,2);assert.notEqual(grid.rowKeys[0],grid.rowKeys[1]);assert.equal(value(0,'contract'),'1000/31');assert.equal(value(0,'nominated'),'100.000000000000000001');assert.equal(value(0,'scheduled'),'');assert.equal(grid.displayRows[0][grid.keys.indexOf('scheduled')],'Withdrawn');assert.equal(value(0,'allocated'),'0');assert.equal(value(0,'status'),'Final');assert.equal(value(1,'status'),'Contractual');assert.equal(value(1,'allocated'),'');
});
