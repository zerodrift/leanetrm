import test from 'node:test';
import assert from 'node:assert/strict';
import {plotNumber,pricingSeries} from '../dist/ppa-pricing.js';
const q=(numerator,denominator='1')=>({numerator,denominator});
test('plot coordinates are approximate only; series retain exact server values and classification',()=>{
 const p={slot:{role:1,ordinal:0},month:'2031-11-01',kind:'Delivery',quantityState:'Metered',economicClass:'Realised',coverage:'DeliveredValue',quantity:q('1','3'),amount:q('13','3'),effectivePrice:q('13')};
 const series=pricingSeries([p],'quantity');assert.equal(series[0].points[0].value,p.quantity);assert.match(series[0].name,/Metered.*Realised/);assert.equal(plotNumber(p.quantity),1/3);
 assert.deepEqual(pricingSeries([{...p,quantity:q('0'),effectivePrice:null}],'effectivePrice')[0].points[0].value,null);
 assert.equal(p.quantity.numerator,'1');
});
test('large exact fractions and flat high-magnitude coordinates keep finite plotting bounds',async()=>{
 assert.equal(plotNumber(q('1'+'0'.repeat(400),'2'+'0'.repeat(400))),0.5);
 const {plotBounds}=await import('../dist/ppa-pricing.js');const [lo,hi]=plotBounds([1e23,1e23]);assert.ok(lo<1e23&&hi>1e23);assert.ok(Number.isFinite((1e23-lo)/(hi-lo)));
});
test('finite extreme spans produce finite pixel coordinates',async()=>{
 const {plotCoordinate}=await import('../dist/ppa-pricing.js');assert.equal(plotCoordinate(0,-1e308,1e308,190),95);assert.equal(plotCoordinate(1e308,-1e308,1e308,190),190);
});
