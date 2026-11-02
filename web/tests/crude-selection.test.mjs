import test from 'node:test';
import assert from 'node:assert/strict';
import {crudeChoices,crudeSelection} from '../dist/crude-selection.js';
const a={code:'a',definitionRevisionId:'revision',commodity:'crude',commodityName:'Crude',grade:'light',gradeName:'Light',facility:'west',facilityName:'West',custodyPoint:'dock',custodyPointName:'Dock',quantityCode:'bbl',quantityName:'Standard barrels',curve:'west_month',curveName:'West monthly',clock:'utc',clockName:'UTC'};
const b={...a,code:'b',grade:'heavy',gradeName:'Heavy',facility:'east',facilityName:'East',custodyPoint:'tank',custodyPointName:'Tank',quantityCode:'m3',quantityName:'Standard cubic metres'};
const rows=[a,b];
test('separate crude reference choices filter only exact authorized tuples',()=>{
 const choices=crudeChoices(rows,{grade:'light'});assert.deepEqual(choices.facility.map(o=>o.value),['west']);assert.deepEqual(choices.quantityCode.map(o=>o.value),['bbl']);
 assert.deepEqual(crudeChoices(rows,{facility:'east'}).grade.map(o=>o.value),['heavy']);
 assert.deepEqual(crudeChoices(rows,{grade:'heavy',facility:'west'}).custodyPoint,[]);
});
test('all independently selected terms identify one exact valuation binding, never a first match',()=>{
 assert.equal(crudeSelection(rows,{grade:'light'}),undefined);
 assert.equal(crudeSelection(rows,a),a);assert.equal(crudeSelection(rows,{...a,custodyPoint:'tank'}),undefined);
 assert.throws(()=>crudeSelection([a,{...a,code:'duplicate'}],a),/ambiguous/);
});
