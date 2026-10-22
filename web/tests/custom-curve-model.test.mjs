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
