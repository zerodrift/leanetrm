import test from 'node:test';
import assert from 'node:assert/strict';
import {workbenchLayout,workbenchArrangementKey} from '../dist/curve-workbench.js';
import {parseArrangement,restoreArrangement,defaultLayout} from '../dist/dock-model.js';

const tabs=['outputs','graph','configuration','versions','lineage','history'];
const rules={maxDepth:2,fixed:tabs,ratioRange:[0.2,0.8]};

test('the workbench opens with the views above the preview',()=>{
 assert.deepEqual(workbenchLayout(),{kind:'split',direction:'column',ratio:0.62,children:[
  {kind:'group',tabs:['graph','configuration','versions','lineage','history'],active:'graph'},
  {kind:'group',tabs:['outputs'],active:'outputs'}]});
});
test('the workbench arrangement is one a saved layout could hold',()=>{
 assert.deepEqual(parseArrangement({layout:workbenchLayout(),collapsed:[]},tabs,rules),{layout:workbenchLayout(),collapsed:[]});
});
test('an arrangement stored under the old key is never read',()=>{
 assert.notEqual(workbenchArrangementKey,'openetrm.custom-curves.layout.v1');
});
test('an arrangement that cannot be read opens the workbench',()=>{
 const fallback={layout:workbenchLayout(),collapsed:[]};
 assert.deepEqual(restoreArrangement({layout:{kind:'unknown'}},tabs,rules,fallback),fallback);
 assert.deepEqual(restoreArrangement(undefined,tabs,rules,fallback),fallback);
 assert.notDeepEqual(defaultLayout(tabs,'outputs'),workbenchLayout());
});
