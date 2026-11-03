import test from 'node:test';
import assert from 'node:assert/strict';
import {crudeChoices,crudeSelection,crudeReferenceFields,standardTemperature} from '../dist/crude-selection.js';
const refs={catalogId:'catalog',revisionId:'delivery',commodities:[{code:'crude',name:'Crude'}],
 grades:[{code:'light',name:'Light',commodity_code:'crude'},{code:'unmapped',name:'Unmapped grade',commodity_code:'crude'}],
 locations:[{code:'terminal',name:'Terminal'},{code:'new_dock',name:'New dock'},{code:'hub',name:'Unqualified hub'}],
 deliveryPoints:[{commodity_code:'crude',grade_code:'light',facility_code:'terminal',custody_point_code:'new_dock'}],
 quantities:[{code:'barrels',commodity_code:'crude',dimension_code:'volume',unit_code:'bbl'}],units:[{code:'bbl',symbol:'bbl'}],clocks:[{code:'utc'},{code:'cpt'}]};
const profileClocks=[{code:'utc',kind:'fixed_offset',fixed_offset_seconds:0}];
test('physical parameters never include a trader-selected valuation curve',()=>{
 assert.deepEqual([...crudeReferenceFields],['commodity','grade','facility','custodyPoint','quantityCode','clock']);
});
test('separate controls offer reference masters without a precombined pricing tuple',()=>{
 const choices=crudeChoices(refs,profileClocks);
 assert.deepEqual(choices.grade.map(o=>o.value),['light','unmapped']);
 assert.deepEqual(choices.facility.map(o=>o.value),['terminal']);
 assert.deepEqual(choices.custodyPoint.map(o=>o.value),['new_dock']);
 assert.equal(choices.quantityCode[0].label,'bbl');
});
test('one available reference never fills a missing physical choice',()=>{
 assert.equal(crudeSelection({commodity:'crude',grade:'light'}),undefined);
 assert.equal(crudeChoices(refs,profileClocks).clock.length,1);
 assert.equal(crudeSelection({commodity:'crude',grade:'light',facility:'terminal',custodyPoint:'new_dock',quantityCode:'barrels'}),undefined);
});
test('physical selection sends no user-provided curve or binding override',()=>{
 const selected={commodity:'crude',grade:'light',facility:'terminal',custodyPoint:'new_dock',quantityCode:'barrels',clock:'utc'};
 assert.deepEqual(crudeSelection({...selected,curve:'override',crudeSetupId:'another'}),selected);
});
test('the clock list offers only the clocks the crude profile admits, each named by its time',()=>{
 assert.deepEqual(crudeChoices(refs,profileClocks).clock,[{value:'utc',label:'UTC'}]);
});
test('a standard temperature reads in degrees, and a scale the crude contract does not name is refused',()=>{
 assert.equal(standardTemperature('60','fahrenheit'),'60 °F');
 assert.throws(()=>standardTemperature('15','rankine'),/temperature scale/);
});
