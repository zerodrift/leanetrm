import {test} from 'node:test';
import assert from 'node:assert/strict';
import {pageReport,reportedPages} from '../dist/assistant-pages.js';
import {assistantPageCases} from '../../tests/browser/assistant-page-states.mjs';

test('owned assistant browser representatives cover each labeled page with its explicit report identity',()=>{
  const cases=assistantPageCases(),expected=cases.map(([page])=>page).sort();
  assert.equal(new Set(expected).size,expected.length,'each admitted page needs one representative');
  for(const [page,state] of cases)assert.equal(pageReport(state).page,page,JSON.stringify(state));
  assert.deepEqual(expected,reportedPages.toSorted());
});
