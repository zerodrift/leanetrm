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
