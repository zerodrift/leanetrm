import test from 'node:test';
import assert from 'node:assert/strict';
import {curveEdges,changeCurveConnection,curveLayout,previewTable} from '../dist/custom-curve-model.js';

const definition=()=>({code:'synthetic_average',name:'Synthetic average',nodes:[
 {id:'source',kind:'source',parameters:{curve:'synthetic_ng'},inputs:{}},
 {id:'dates',kind:'window',parameters:{schedule:{id:'retained-calendar',version:7},count:5},inputs:{}},
 {id:'sample',kind:'sample',parameters:{},inputs:{curve:'source',dates:'dates'}},
 {id:'average',kind:'average',parameters:{axis:'dates'},inputs:{values:'sample'}}],output:'average'});
test('graph edges preserve every named input connection',()=>assert.deepEqual(curveEdges(definition()),[
 {source:'source',target:'sample',port:'curve'},{source:'dates',target:'sample',port:'dates'},{source:'sample',target:'average',port:'values'}]));
test('editing connections preserves retained calendar references and source terms',()=>{const before=definition(),edited=changeCurveConnection(before,'sample','curve','dates');assert.equal(edited.nodes[2].inputs.curve,'dates');assert.equal(before.nodes[2].inputs.curve,'source');assert.deepEqual(edited.nodes[1].parameters.schedule,{id:'retained-calendar',version:7});});
test('disconnecting removes only the selected port and never mutates the definition',()=>{const before=definition(),edited=changeCurveConnection(before,'sample','curve','');assert.deepEqual(edited.nodes[2].inputs,{dates:'dates'});assert.deepEqual(before.nodes[2].inputs,{curve:'source',dates:'dates'});});
test('layout is presentation state and never changes node configuration',()=>{const before=definition(),copy=JSON.stringify(before),layout=curveLayout(before,{'source':{x:14,y:32}});assert.deepEqual(layout.source,{x:14,y:32});assert.ok(layout.average.x>layout.sample.x);assert.equal(JSON.stringify(before),copy);});
test('table preserves server strings including missing values without calculating',()=>assert.deepEqual(previewTable({columns:['Contract','Price','Unit'],rows:[['NG1','3.012345678901234567','USD/MMBtu'],['NG2',null,'USD/MMBtu']]}),{headers:['Contract','Price','Unit'],rows:[['NG1','3.012345678901234567','USD/MMBtu'],['NG2','Unavailable','USD/MMBtu']]}));
test('only explicit custom-curve rejections release a mutation identity',async()=>{const {knownCurveRejection}=await import('../dist/custom-curve-model.js');for(const code of ['custom_curve_conflict','custom_curve_definition','recipe_definition','recipe_transform','access_denied','historical_read_only','request_format','request_too_large'])assert.equal(knownCurveRejection(code),true,code);for(const code of ['response_unknown','request_cancelled',undefined,'new_server_failure'])assert.equal(knownCurveRejection(code),false);});
test('operator metadata governs conditional controls and removes only incompatible fields',async()=>{const {curveFieldVisible,editCurveParameters}=await import('../dist/custom-curve-model.js');const fields=[{key:'mode'},{key:'count',when:{key:'mode',values:['prior_calendar','prior_business']}},{key:'calendar',when:{key:'mode',values:['prior_business']}}];const old={mode:'prior_business',count:'5',calendar:'retained_calendar',sampling:'context_utc_time'};assert.equal(curveFieldVisible(fields[2],old),true);assert.deepEqual(editCurveParameters(old,'mode','context',fields),{mode:'context',sampling:'context_utc_time'});assert.deepEqual(old,{mode:'prior_business',count:'5',calendar:'retained_calendar',sampling:'context_utc_time'});});
test('weighted mapping edits preserve exact supplied contracts and decimal text without normalizing',async()=>{
 const {changeContractWeights}=await import('../dist/custom-curve-model.js');
 assert.equal(typeof changeContractWeights,'function','The weighted mapping editor must preserve typed transport rows');
 const before=definition();before.nodes.push({id:'basket',kind:'weighted_contracts',parameters:{aggregation:'signed_sum_v1'},inputs:{source_a:'source'},contractWeights:[]});
 const rows=[{outputPosition:2,sourcePort:'source_a',contractId:'exact-contract-a',weight:'-0.700000000000000001'},{outputPosition:2,sourcePort:'source_a',contractId:'exact-contract-b',weight:'1.25'}];
 const edited=changeContractWeights(before,'basket',rows);
 assert.deepEqual(edited.nodes.at(-1).contractWeights,rows);assert.deepEqual(before.nodes.at(-1).contractWeights,[]);
 assert.deepEqual(edited.nodes.at(-1).inputs,{source_a:'source'});assert.deepEqual(edited.nodes.at(-1).parameters,{aggregation:'signed_sum_v1'});
 rows[0].weight='9';assert.equal(edited.nodes.at(-1).contractWeights[0].weight,'-0.700000000000000001');
 assert.deepEqual(changeContractWeights(edited,'basket',[]).nodes.at(-1).contractWeights,[]);
});

test('removing a panel input removes only its matching weight',()=>{
 const before={code:'blend',name:'Synthetic blend',output:'blend',nodes:[{id:'blend',kind:'weighted_panels',parameters:{},inputs:{a:'first',b:'second'},panelWeights:[{sourcePort:'a',weight:'7'},{sourcePort:'b',weight:'3'}]}]};
 const after=changeCurveConnection(before,'blend','a','');
 assert.deepEqual(after.nodes[0].panelWeights,[{sourcePort:'b',weight:'3'}]);
 assert.equal(before.nodes[0].panelWeights.length,2);
});
test('saved curve states are named exactly and an unknown state is refused',async()=>{
 const {recipeStateLabel}=await import('../dist/custom-curve-model.js');
 assert.equal(recipeStateLabel('draft'),'Draft');assert.equal(recipeStateLabel('published'),'Published');
 for(const state of ['','retired',undefined])assert.throws(()=>recipeStateLabel(state),/state/);
});
test('preview tiles show the backend summary without counting points or choosing periods',async()=>{
 const {previewTiles}=await import('../dist/custom-curve-model.js');
 const tiles=previewTiles({points:12,unavailablePoints:0,firstPeriod:{from:'2021-01-01',to:'2021-02-01'},lastPeriod:{from:'2021-12-01',to:'2022-01-01'},unit:'USD/MMBtu'});
 assert.deepEqual(tiles.map(t=>t.label),['Points','First period','Last period','Unit']);
 assert.deepEqual(tiles.map(t=>t.rows[0].figure),['12','Jan 2021','Dec 2021','USD/MMBtu']);
 assert.equal(tiles[0].note,undefined,'no unavailable points needs no note');
 const partial=previewTiles({points:3,unavailablePoints:2,firstPeriod:{from:'2021-01-05',to:'2021-01-06'},lastPeriod:{from:'2021-01-05',to:'2021-01-06'},unit:'USD/MMBtu'});
 assert.equal(partial[0].rows[0].figure,'3');assert.match(partial[0].note,/2 /);
 const empty=previewTiles({points:0,unavailablePoints:0,firstPeriod:null,lastPeriod:null,unit:''});
 assert.deepEqual(empty.map(t=>t.rows[0].figure),['0','None','None','No points']);
 for(const summary of [undefined,{points:'12',unavailablePoints:0,firstPeriod:null,lastPeriod:null,unit:''},{points:1,firstPeriod:null,lastPeriod:null,unit:''},{points:1,unavailablePoints:0,lastPeriod:null,unit:''}])
  assert.throws(()=>previewTiles(summary),/summary/);
});

const operators=[
 {kind:'source',inputPorts:[],outputType:'curve',fields:[]},
 {kind:'window',inputPorts:[],outputType:'dates',fields:[]},
 {kind:'sample',inputPorts:[{name:'curve',type:'curve'},{name:'dates',type:'dates'}],outputType:'samples',fields:[]},
 {kind:'average',inputPorts:[{name:'values',type:'samples'}],outputType:'curve',fields:[]},
 {kind:'weighted_contracts',inputPorts:[],outputType:'selection',fields:[{key:'contractWeights',type:'contract_weights'}],dynamicInputs:{type:'curve',label:'Source',prefix:'source_'}}];
const mapping=()=>{const d=definition();d.nodes.push({id:'other',kind:'source',parameters:{curve:'synthetic_local'},inputs:{}},
 {id:'basket',kind:'weighted_contracts',parameters:{},inputs:{source_a:'source',source_b:'other'},contractWeights:[
  {outputPosition:1,sourcePort:'source_a',contractId:'contract-a',weight:'0.7'},{outputPosition:1,sourcePort:'source_b',contractId:'contract-b',weight:'-0.2'},{outputPosition:2,sourcePort:'source_c',contractId:'contract-c',weight:'1'}]});return d;};
test('removing an arrow removes only that port, keeps mapped contract rows and never mutates',async()=>{
 const {removeCurveConnection}=await import('../dist/custom-curve-model.js');
 const before=mapping(),edited=removeCurveConnection(before,'basket','source_a');
 assert.deepEqual(edited.nodes.at(-1).inputs,{source_b:'other'});
 assert.equal(edited.nodes.at(-1).contractWeights.length,3,'rows naming the port stay for the backend to reject or for reconnection');
 assert.deepEqual(before.nodes.at(-1).inputs,{source_a:'source',source_b:'other'});
 assert.deepEqual(removeCurveConnection(before,'sample','curve').nodes[2].inputs,{dates:'dates'});
});
test('contract objects are the explicit contract connections of a mapping, in order, with their feeding node',async()=>{
 const {contractObjects}=await import('../dist/custom-curve-model.js');
 const d=mapping();
 assert.deepEqual(contractObjects(d,d.nodes.at(-1)),[
  {index:0,source:'source',sourcePort:'source_a',contractId:'contract-a',weight:'0.7',outputPosition:1},
  {index:1,source:'other',sourcePort:'source_b',contractId:'contract-b',weight:'-0.2',outputPosition:1},
  {index:2,source:null,sourcePort:'source_c',contractId:'contract-c',weight:'1',outputPosition:2}]);
 assert.deepEqual(contractObjects(d,d.nodes[0]),[]);
});
test('connectable targets follow catalog port types, without self-loops or duplicate ports',async()=>{
 const {connectableTargets}=await import('../dist/custom-curve-model.js');
 const catalog={operators:[...operators,{kind:'shift',inputPorts:[{name:'curve',type:'curve'}],outputType:'curve',fields:[]}]};
 const d=definition();d.nodes.push({id:'sample_2',kind:'sample',parameters:{},inputs:{}},{id:'shift',kind:'shift',parameters:{},inputs:{}},{id:'basket',kind:'weighted_contracts',parameters:{},inputs:{source_a:'other'}});
 assert.deepEqual(connectableTargets(d,catalog,'source'),[{target:'sample_2',port:'curve',added:false},{target:'shift',port:'curve',added:false},{target:'basket',port:'source_1',added:true}]);
 assert.deepEqual(connectableTargets(d,catalog,'shift'),[{target:'sample',port:'curve',added:false},{target:'sample_2',port:'curve',added:false},{target:'basket',port:'source_1',added:true}],'a node never feeds itself');
 assert.deepEqual(connectableTargets(d,catalog,'sample'),[],'the port it already feeds is not offered again');
 d.nodes.at(-1).inputs.source_b='source';
 assert.ok(!connectableTargets(d,catalog,'source').some(t=>t.target==='basket'),'a mapping it already feeds gets no second port');
 assert.deepEqual(connectableTargets(d,{operators:[]},'source'),[],'without catalog metadata nothing is offered');
});
test('picking a contract into a mapping adds an explicit row with no weight or position chosen',async()=>{
 const {pickContract,contractTargets}=await import('../dist/custom-curve-model.js');
 const d=mapping(),before=JSON.stringify(d);
 assert.deepEqual(contractTargets(d,{operators},'other'),['basket']);assert.deepEqual(contractTargets(d,{operators},'sample'),[],'only a source the mapping accepts');
 const reused=pickContract(d,{operators},'basket','other','contract-z');
 assert.equal(reused.row,3);assert.deepEqual(reused.definition.nodes.at(-1).contractWeights[3],{outputPosition:null,sourcePort:'source_b',contractId:'contract-z',weight:''});
 assert.deepEqual(reused.definition.nodes.at(-1).inputs,{source_a:'source',source_b:'other'});
 d.nodes.push({id:'third',kind:'source',parameters:{},inputs:{}});
 const added=pickContract(d,{operators},'basket','third','contract-t');
 assert.equal(added.definition.nodes.find(n=>n.id==='basket').inputs.source_1,'third');
 assert.equal(added.definition.nodes.find(n=>n.id==='basket').contractWeights.at(-1).sourcePort,'source_1');
 assert.equal(JSON.stringify(d.nodes.slice(0,-1)),JSON.stringify(JSON.parse(before).nodes),'the original definition is unchanged');
 assert.throws(()=>pickContract(d,{operators},'sample','third','contract-t'),/contract/);
});
test('a contract connection without its weight is incomplete',async()=>{
 const {incompleteContractWeights}=await import('../dist/custom-curve-model.js');
 const d=mapping();assert.equal(incompleteContractWeights(d),false);
 d.nodes.at(-1).contractWeights[1].weight='';assert.equal(incompleteContractWeights(d),true);
 assert.equal(incompleteContractWeights(definition()),false);
});
test('auto-layout stacks each column by measured card heights so cards never overlap',async()=>{
 const d=mapping(),heights={source:120,dates:150,other:330,basket:260,sample:140,average:118};
 const layout=curveLayout(d,{},heights),gap=Object.values(layout);
 for(const a of d.nodes)for(const b of d.nodes){if(a.id>=b.id||layout[a.id].x!==layout[b.id].x)continue;const [top,bottom]=layout[a.id].y<layout[b.id].y?[a,b]:[b,a];
  assert.ok(layout[top.id].y+heights[top.id]<layout[bottom.id].y,`${top.id} overlaps ${bottom.id}`);}
 assert.ok(gap.length===d.nodes.length);
 const kept=curveLayout(d,{source:{x:24,y:400}},heights);assert.deepEqual(kept.source,{x:24,y:400});
 assert.ok(kept.other.y>=400+heights.source||kept.other.y+heights.other<400,'a placed card leaves room for a saved one before it');
});
test('connectable targets reuse an empty dynamic port and accept any-typed ports',async()=>{
 const {connectableTargets}=await import('../dist/custom-curve-model.js');
 const catalog={operators:[...operators,{kind:'inspect',inputPorts:[{name:'value',type:'any'}],outputType:'text',fields:[]}]};
 const d=definition();d.nodes.push({id:'basket',kind:'weighted_contracts',parameters:{},inputs:{source_1:'other',source_2:''}},{id:'inspect',kind:'inspect',parameters:{},inputs:{}});
 assert.deepEqual(connectableTargets(d,catalog,'source').filter(t=>t.target!=='sample'),[{target:'basket',port:'source_2',added:false},{target:'inspect',port:'value',added:false}]);
 assert.deepEqual(connectableTargets(d,catalog,'dates').map(t=>t.target+'.'+t.port),['inspect.value'],'any accepts every output type');
});
test('picking a contract fills an existing empty mapping port before adding one',async()=>{
 const {pickContract,contractPort}=await import('../dist/custom-curve-model.js');
 const d=definition();d.nodes.push({id:'basket',kind:'weighted_contracts',parameters:{},inputs:{source_1:'other',source_2:''},contractWeights:[]});
 assert.equal(contractPort(d,{operators},'basket','other'),'source_1','a source already connected keeps its port');
 assert.equal(contractPort(d,{operators},'basket','source'),'source_2');
 const picked=pickContract(d,{operators},'basket','source','contract-s');
 assert.deepEqual(picked.definition.nodes.at(-1).inputs,{source_1:'other',source_2:'source'});
 assert.equal(picked.definition.nodes.at(-1).contractWeights[0].sourcePort,'source_2');
 assert.equal(contractPort(d,{operators},'sample','source'),undefined);
});
test('a fed input can change to any other operation whose output its catalog type accepts',async()=>{
 const {connectableSources}=await import('../dist/custom-curve-model.js');
 const d=mapping();d.nodes.push({id:'third',kind:'source',parameters:{},inputs:{}});
 assert.deepEqual(connectableSources(d,{operators},'basket','source_a'),['average','third'],'not the current source, not one already feeding the mapping, not the mapping itself');
 assert.deepEqual(connectableSources(d,{operators},'sample','dates'),[],'only the date sequence it already has fits');
 assert.deepEqual(connectableSources(d,{operators},'sample','curve'),['average','other','third'],'types only: the backend rejects a cycle');
});
