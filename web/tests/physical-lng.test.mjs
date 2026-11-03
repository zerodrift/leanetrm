import test from 'node:test';
import assert from 'node:assert/strict';
import {lngChoices,lngSelection,lngPhysicalFields} from '../dist/lng-selection.js';
import {lngRequest,restoreLngCommand} from '../dist/physical-lng-model.js';
import {PendingCommand} from '../dist/pending-command.js';
const refs={referenceCatalogId:'catalog',referenceRevisionId:'revision',commodities:[{code:'lng',name:'Synthetic LNG'}],products:[{code:'priced',commodity_code:'lng'},{code:'unpriced',commodity_code:'lng'}],custodyPoints:[{stage:'load',facility_code:'load',custody_point_code:'load_custody'},{stage:'discharge',facility_code:'discharge',custody_point_code:'discharge_custody'}],locations:['load','load_custody','discharge','discharge_custody','place'].map(code=>({code})),quantities:[{code:'energy',commodity_code:'lng',unit_code:'mmbtu',dimension_code:'energy'},{code:'mass',commodity_code:'lng',unit_code:'tonne',dimension_code:'mass'}],units:[{code:'mmbtu',symbol:'MMBtu'},{code:'tonne',symbol:'t'}],clocks:[{code:'utc'}],conversions:[{code:'factor'}],incotermsEditions:[{code:'2020'}],incotermsRules:[{code:'cif_2020',edition_code:'2020',rule_code:'CIF',name:'Cost, Insurance and Freight'}]};
const physical={referenceCatalogId:'catalog',referenceRevisionId:'revision',commodity:'lng',product:'priced',loadFacility:'load',loadCustody:'load_custody',dischargeFacility:'discharge',dischargeCustody:'discharge_custody',energyQuantityCode:'energy',clock:'utc'};
const profile={code:'fixed_gross_discharge_energy_v1',quantityBasis:'gross_discharge_energy_total',tolerance:'0',discounting:false};
const fields={loadFrom:'2027-01-03T00:00:00Z',loadTo:'2027-01-04T00:00:00Z',dischargeFrom:'2027-01-10T00:00:00Z',dischargeTo:'2027-01-11T00:00:00Z',paymentAt:'2027-02-01T00:00:00Z',quantity:'980.125',quantityCode:'mass',quantityUnit:'tonne',conversionMode:'factor',conversionCode:'factor',direction:'receive',considerationDirection:'pay',fixedPrice:'10.25'};
const incoterms={edition:'2020',rule:'cif_2020',namedPlace:'place'};
test('all LNG physical and Incoterms choices come from separate reference masters, including unpriced products',()=>{
 const c=lngChoices(refs);assert.deepEqual(c.product.map(r=>r.value),['priced','unpriced']);assert.deepEqual(c.loadFacility.map(r=>r.value),['load']);assert.deepEqual(c.loadCustody.map(r=>r.value),['load_custody']);
 assert.deepEqual(c.dischargeFacility.map(r=>r.value),['discharge']);assert.deepEqual(c.dischargeCustody.map(r=>r.value),['discharge_custody']);assert.deepEqual(c.energyQuantityCode.map(r=>r.value),['energy']);
 assert.deepEqual(c.quantityCode.map(r=>r.value),['energy','mass']);assert.deepEqual(c.quantityUnit.map(r=>r.value),['mmbtu','tonne']);assert.deepEqual(c.edition.map(r=>r.value),['2020']);assert.deepEqual(c.rule.map(r=>r.value),['cif_2020']);assert.equal(c.namedPlace.length,5);assert.equal(c.rule[0].label,'CIF · 2020');
 assert.equal(lngSelection({}),undefined);assert.equal(lngSelection({commodity:'lng'}),undefined);assert.equal(lngSelection(physical).clock,'utc');assert.equal(lngPhysicalFields.length,8);assert.equal('curve' in c,false);
});
test('LNG request binds exact physical terms, original decimals, independent Incoterms and selected conversion without a source hint',()=>{
 const r=lngRequest({snapshotId:'frame'}, {bookId:'book'},physical,incoterms,profile,fields);
 assert.deepEqual(r.physical,physical);assert.deepEqual(r.incoterms,incoterms);assert.equal(r.quantity,'980.125');assert.equal(r.conversionCode,'factor');assert.equal(r.quantityUnit,'tonne');assert.equal(r.discounting,false);assert.equal(r.considerationDirection,'pay');
 for(const key of ['curve','assignmentCode','definitionRevisionId','energy','presentValue','schedule'])assert.equal(key in r,false);
 const direct=lngRequest({}, {},physical,incoterms,profile,{...fields,conversionMode:'direct',conversionCode:'stale'});assert.equal(direct.conversionCode,null);
});
test('missing explicit choices and unresolved wall times refuse rather than changing economics',()=>{
 assert.throws(()=>lngRequest({}, {},physical,incoterms,undefined,fields),/profile/);
 assert.throws(()=>lngRequest({}, {},physical,incoterms,profile,{...fields,conversionMode:''}),/conversion/);
 assert.throws(()=>lngRequest({}, {},physical,incoterms,profile,{...fields,conversionCode:''}),/conversion/);
 assert.throws(()=>lngRequest({}, {},physical,{...incoterms,rule:''},profile,fields),/Incoterms/);
 assert.throws(()=>lngRequest({}, {},physical,incoterms,profile,{...fields,loadFrom:'2027-01-03T00:00'}),/Resolve/);
});
test('unknown booking outcome retains the original actor request and operation through a denied retry',()=>{
 const p=new PendingCommand(c=>c==='access_denied');p.begin('lng/book',{quantity:'980.125'},()=> 'original');p.sending();const restored=restoreLngCommand(JSON.parse(JSON.stringify(p.command)));assert.deepEqual(restored,{...p.command,uncertain:true});p.command=restored;const old=p.sending();p.failed('access_denied',old);assert.equal(p.command.operationId,'original');assert.equal(p.command.body.quantity,'980.125');assert.equal(restoreLngCommand({...restored,path:'crude/book'}),undefined);
});
