import test from 'node:test';
import assert from 'node:assert/strict';
import {curveObjects,curveObjectTitle,curveConnectionLabel} from '../dist/curve-object-model.js';

const nodes=()=>[
 {id:'gas',kind:'source',parameters:{curve:'synthetic_gas'},inputs:{}},
 {id:'months',kind:'select',parameters:{mode:'positions',first:'1',count:'3'},inputs:{source:'gas'}},
 {id:'when',kind:'dates',parameters:{mode:'context'},inputs:{}},
 {id:'sample',kind:'sample',parameters:{corrections:'exact'},inputs:{selection:'months',dates:'when'}},
 {id:'result',kind:'output',parameters:{},inputs:{values:'sample'}}];
const definition=()=>({code:'example',name:'Example',output:'result',nodes:nodes()});
test('object canvas retains source, contract set and result identities while placing sampling and dates on the connection',()=>{
 const d=definition(),before=JSON.stringify(d),view=curveObjects(d);
 assert.deepEqual(view.nodes.map(n=>[n.node.id,n.role]),[['gas','curve'],['months','contracts'],['result','result']]);
 assert.deepEqual(view.connections.map(c=>[c.source,c.target,c.operations]),[['gas','months',['months']],['months','result',['when','sample','result']]]);
 assert.equal(JSON.stringify(d),before,'projection must not rewrite the authoring definition or choose dates');
});
test('nested published sources retain exact references and whole-panel connection weights without financial arithmetic',()=>{
 const ref={catalogId:'catalog',curveId:'parent',version:7,outputNode:'out'};
 const d={code:'child',name:'Child',output:'mix',nodes:[{id:'parent',kind:'published_source',publishedSource:ref,parameters:{selection:'all_output_points_v1'},inputs:{}},{id:'mix',kind:'weighted_panels',parameters:{alignment:'exact_delivery_intervals_v1'},inputs:{a:'parent'},panelWeights:[{sourcePort:'a',weight:'-0.700000000000000001'}]}]};
 const view=curveObjects(d);assert.deepEqual(view.nodes[0].node.publishedSource,ref);
 assert.equal(curveConnectionLabel(d,view.connections[0],[]),'Weight −0.700000000000000001');
 assert.deepEqual(view.connections[0].operations,['mix']);
});
test('parallel uses of the same object preserve distinct ports and operation paths',()=>{
 const d=definition();d.nodes.push({id:'other_sample',kind:'sample',parameters:{},inputs:{selection:'months',dates:'when'}},{id:'combine',kind:'weighted_panels',parameters:{},inputs:{left:'sample',right:'other_sample'}});d.output='combine';
 const edges=curveObjects(d).connections.filter(c=>c.target==='combine');
 assert.equal(edges.length,2);assert.notEqual(edges[0].id,edges[1].id);assert.deepEqual(edges.map(c=>c.port),['left','right']);
 assert.deepEqual(edges.map(c=>c.operations),[['when','sample','combine'],['when','other_sample','combine']]);
});
test('incomplete and unknown operations remain inspectable; cycles cannot loop the canvas walk',()=>{
 const d=definition();d.nodes.push({id:'orphan',kind:'sample',parameters:{},inputs:{}},{id:'future',kind:'new_operator',parameters:{},inputs:{}});
 const view=curveObjects(d);assert(view.nodes.some(n=>n.node.id==='orphan'));assert(view.nodes.some(n=>n.node.id==='future'));
 d.nodes.find(n=>n.id==='sample').inputs.selection='sample';assert.doesNotThrow(()=>curveObjects(d));
});
test('object names use offered source names and recipe name without deriving contract terms from labels',()=>{
 const d=definition(),view=curveObjects(d);
 assert.equal(curveObjectTitle(view.nodes[0],d,[{code:'synthetic_gas',name:'Synthetic gas'}],[]),'Synthetic gas');
 assert.equal(curveObjectTitle(view.nodes[1],d,[],[]),'Contract set');
 assert.equal(curveObjectTitle(view.nodes[2],d,[],[]),'Example');
 assert.equal(curveConnectionLabel(d,view.connections[1],[{kind:'sample',label:'Sample prices'},{kind:'output',label:'Curve output'}]),'Sample prices');
});
test('spliced product curves and power lookups are displayed as result curves',()=>{
 for(const kind of ['splice','power_intervals']){
  const d={code:'synthetic',name:'Synthetic',output:'result',nodes:[{id:'operation',kind,parameters:{},inputs:{}},{id:'result',kind:'output',parameters:{},inputs:{values:'operation'}}]};
  const item=curveObjects(d).nodes.find(n=>n.node.id==='operation');assert.equal(item.role,'result');assert.equal(curveObjectTitle(item,d,[],[]),'Result curve');
 }
});
