import test from 'node:test';
import assert from 'node:assert/strict';
import {freshnessFooter} from '../dist/freshness.js';

test('freshness footer states source, observation time and state; a missing time reads "No observation yet"',()=>{
  const footer=freshnessFooter({state:'latest',observedAt:'2026-10-30T15:00:00Z',source:'Synthetic'},'UTC');
  assert.match(footer,/Synthetic/);assert.match(footer,/Oct 30, 2026/);assert.match(footer,/3:00/);assert.match(footer,/Latest/);
  const none=freshnessFooter({state:'not_yet_available',observedAt:null,source:'Synthetic'},'UTC');
  assert.match(none,/No observation yet/);assert.match(none,/Not yet available/);assert.doesNotMatch(none,/20\d\d/);
});

test('the observation time is shown in the given zone',()=>{
  const f={state:'historical',observedAt:'2026-10-30T15:00:00Z',source:'Synthetic'};
  assert.match(freshnessFooter(f,'UTC'),/3:00\s?PM/);
  assert.match(freshnessFooter(f,'America/Chicago'),/10:00\s?AM/);
});

test('each state has its own wording',()=>{
  const words=['latest','newer_available','historical','not_yet_available'].map(state=>freshnessFooter({state,observedAt:null,source:'S'},'UTC'));
  assert.equal(new Set(words).size,4);
  assert.match(words[1],/Newer available/);assert.match(words[2],/Historical/);
});

test('failed shows the backend reason',()=>{
  const footer=freshnessFooter({state:'failed',observedAt:null,source:'Feed',reason:'The provider refused the request.'},'UTC');
  assert.match(footer,/Failed/);assert.match(footer,/The provider refused the request\./);
});

test('a failure without a reason is refused rather than shown bare',()=>{
  assert.throws(()=>freshnessFooter({state:'failed',observedAt:null,source:'Feed'},'UTC'),/reason/);
});

test('a chosen run and a run valued at an earlier close are worded as such, never as Latest',()=>{
  const chosen=freshnessFooter({state:'chosen',observedAt:null,source:'Risk run'},'UTC'),earlier=freshnessFooter({state:'earlier_close',observedAt:null,source:'Risk run'},'UTC');
  assert.match(chosen,/Chosen run$/);assert.match(earlier,/Valued at an earlier close$/);
  for(const words of [chosen,earlier])assert.doesNotMatch(words,/Latest/);
});

test('a chosen run valued at an earlier close says both',()=>{
  assert.match(freshnessFooter({state:'chosen_earlier_close',observedAt:null,source:'Risk run'},'UTC'),/Chosen run · valued at an earlier close$/);
});
