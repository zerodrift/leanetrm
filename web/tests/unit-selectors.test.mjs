import test from 'node:test';
import assert from 'node:assert/strict';
import {setupUnitConverter} from '../dist/units-view.js';
test('unit entry narrows by commodity and clears incompatible downstream selections',()=>{
 const nodes=new Map();
 globalThis.Option=class{constructor(text,value){this.text=text;this.value=value;}};
 globalThis.document={getElementById(id){if(!nodes.has(id))nodes.set(id,{value:'',textContent:'',hidden:false,replaceChildren(...options){this.options=options;this.value=options[0]?.value??'';}});return nodes.get(id);}};
 const view=setupUnitConverter({api:async()=>{},act:async fn=>fn(),busy:()=>false});
 view.populate({references:{commodities:[{code:'gas',name:'Gas'},{code:'power',name:'Power'},{code:'unused',name:'Unused'}],units:[{code:'thermal',symbol:'MMBtu'},{code:'electric',symbol:'MWh'}],quantity_specifications:[{code:'gas-unit',commodity_code:'gas',unit_code:'thermal',basis_code:'energy',dimension_code:'energy'},{code:'power-unit',commodity_code:'power',unit_code:'electric',basis_code:'energy',dimension_code:'energy'}]}},true);
 const field=id=>document.getElementById(id);
 assert.deepEqual(field('unit-commodity').options.map(o=>o.value),['','gas','power']);
 assert.deepEqual(field('unit-from').options.map(o=>o.value),['']);
 field('unit-commodity').value='gas';field('unit-commodity').onchange();
 assert.deepEqual(field('unit-from').options.map(o=>o.value),['','gas-unit']);
 field('unit-from').value='gas-unit';field('unit-from').onchange();field('unit-to').value='gas-unit';
 field('unit-commodity').value='power';field('unit-commodity').onchange();
 assert.equal(field('unit-from').value,'');assert.equal(field('unit-to').value,'');
 assert.deepEqual(field('unit-from').options.map(o=>o.value),['','power-unit']);
});
