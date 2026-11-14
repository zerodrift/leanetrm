import {test} from 'node:test';
import assert from 'node:assert/strict';
import {priceSourceInputIds} from '../dist/market-view.js';

const source={definitionRevisionId:'secondary',referenceCatalogId:'references',referenceRevisionId:'reference-version'};
function fixture(){
  const observations=[{id:'secondary-mark'},{id:'earlier-revision'}];
  return {
    result:{snapshotId:'close',curve:'regional_delivery',observationScope:'selected_frame',observations,
      inputSelection:{snapshotId:'close',source,observationIds:['secondary-mark']},points:[{lineage:['secondary-mark']}]},
    workspace:{definitions:{revisions:[{id:'secondary',reference_catalog_id:'references',reference_revision_id:'reference-version'}]},
      observations,frames:[{id:'close',observationIds:['primary-mark']}]}
  };
}

test('a separately sourced curve displays its selected inputs at the same recorded close',()=>{
  const {result,workspace}=fixture();
  assert.deepEqual([...priceSourceInputIds(result,workspace)],['secondary-mark']);
});

test('historical revisions returned for inspection cannot satisfy an unpinned price lineage',()=>{
  const {result,workspace}=fixture();result.points[0].lineage=['earlier-revision'];
  assert.throws(()=>priceSourceInputIds(result,workspace));
});

test('the selected input packet must identify the priced snapshot and exact definition and reference versions',()=>{
  for(const change of [selection=>selection.snapshotId='another-close',selection=>selection.source={...source,definitionRevisionId:'primary'},
    selection=>selection.source={...source,referenceCatalogId:'other-references'},selection=>selection.source={...source,referenceRevisionId:'other-version'}]){
    const {result,workspace}=fixture();workspace.frames[0].observationIds=['secondary-mark'];change(result.inputSelection);
    assert.throws(()=>priceSourceInputIds(result,workspace));
  }
});

test('a selected input requires its source row and unique explicit identity',()=>{
  for(const change of [result=>delete result.inputSelection,result=>result.inputSelection.observationIds.push('secondary-mark'),
    result=>result.inputSelection.observationIds.push(null),result=>result.observations=[],result=>result.observationScope='all_visible']){
    const {result,workspace}=fixture();workspace.frames[0].observationIds=['secondary-mark'];change(result);
    assert.throws(()=>priceSourceInputIds(result,workspace));
  }
});
