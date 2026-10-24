import assert from 'node:assert/strict';
import test from 'node:test';
import {runInNewContext} from 'node:vm';
import {idle} from '../../tests/browser/owned-browser.mjs';
import {run as futures} from '../../tests/browser/futures.mjs';
import {run as risk} from '../../tests/browser/risk.mjs';
import {run as tradeEditor} from '../../tests/browser/trade-editor.mjs';
import {run as swaps} from '../../tests/browser/swaps.mjs';
import {run as tableInteractions} from '../../tests/browser/table-interactions.mjs';
import {run as profileDialog} from '../../tests/browser/profile-dialog.mjs';

async function startupCondition(run) {
  const stop = new Error('Captured the first readiness condition');
  let expression;
  await assert.rejects(run({
    command: async () => ({}),
    wait: async condition => { expression = condition; throw stop; },
    idle,
    url: 'http://127.0.0.1:1',
  }), error => error === stop);
  return expression;
}

const states = [
  {name: 'before the body exists', busy: null, body: false, profile: false, status: null, ready: false},
  {name: 'while the header precedes the status element', busy: null, body: true, profile: true, status: null, ready: false},
  {name: 'while application initialization is busy', busy: 'true', body: true, profile: true, status: 'Loading market data…', ready: false},
  {name: 'after application initialization completes', busy: 'false', body: true, profile: true, status: 'Ready', ready: true},
];

for (const [name, run] of Object.entries({futures, risk, tradeEditor, swaps, tableInteractions, profileDialog})) {
  for (const state of states) {
    test(`${name} waits safely ${state.name}`, async () => {
      const expression = await startupCondition(run);
      const nodes = new Map([
        ['#profile-open', state.profile ? {} : null],
        ['#status', state.status === null ? null : {textContent: state.status}],
      ]);
      const document = {
        body: state.body ? {getAttribute: () => state.busy} : null,
        querySelector: selector => nodes.get(selector) ?? null,
      };
      assert.equal(Boolean(runInNewContext(expression, {document})), state.ready);
    });
  }
}
