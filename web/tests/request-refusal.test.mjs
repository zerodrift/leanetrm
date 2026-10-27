import test from 'node:test';
import assert from 'node:assert/strict';
import {requestRefusal} from '../dist/request-refusal.js';

const generic='The app could not complete that request. Please refresh and try again.';

test('a refusal the page has no wording for shows the server\'s own explanation',()=>{
 assert.equal(requestRefusal({code:'portfolio_context',message:'Select explicit books and a snapshot of the exact model and certified effective date.'}),
  'Select explicit books and a snapshot of the exact model and certified effective date.');
});

test('a refusal without an explanation keeps the general advice',()=>{
 assert.equal(requestRefusal({code:'request_failed'}),generic);
 assert.equal(requestRefusal({code:'preview_error',message:'  '}),generic);
 assert.equal(requestRefusal({}),generic);
 // The server could not read what the page sent, so the page and the server disagree.
 assert.equal(requestRefusal({code:'request_format',message:'The request is incomplete or malformed.'}),generic);
});

test('an incomplete scope names each affected trade once, grouped by why it was refused',()=>{
 const text=requestRefusal({code:'pilot_close_inputs',
  message:'The complete selected scope contains unsupported or incompatible inputs; no partial report was admitted.',
  affectedBindings:[
   {tradeId:'080855b1-c98a-4dc8-a73b-dbaf7aa35de0',bindingId:'8b4f0069-d709-4265-9bd2-f24d7dfdb661',reason:'exact_bundle_whole_operation_v1'},
   {tradeId:'080855b1-c98a-4dc8-a73b-dbaf7aa35de0',bindingId:'11111111-d709-4265-9bd2-f24d7dfdb661',reason:'exact_bundle_whole_operation_v1'},
   {tradeId:'4045f650-79fe-4970-b8f8-1c705d02d1c0',bindingId:'c4b72f1f-6564-41dd-8d2f-c58b7b11f59b',reason:'exact_bundle_whole_operation_v1'},
   {tradeId:'6abf2a03-43a5-416c-aa17-098fa45830a2',bindingId:'cbba4072-76e2-42ab-813a-239645d5f996',reason:'missing_curve_view'}]});
 assert.equal(text,[
  'The complete selected scope contains unsupported or incompatible inputs; no partial report was admitted.',
  '2 trades were booked under other reference or definition versions than the selected market data: 080855b1-c98a-4dc8-a73b-dbaf7aa35de0, 4045f650-79fe-4970-b8f8-1c705d02d1c0.',
  '1 trade needs a curve the selected market data omits: 6abf2a03-43a5-416c-aa17-098fa45830a2.',
  'Choose books that leave out these trades to see the rest.'].join('\n'));
});

test('a refusal reason the page has no wording for is named by its code',()=>{
 assert.equal(requestRefusal({code:'pilot_close_inputs',message:'No partial report was admitted.',
  affectedBindings:[{tradeId:'6abf2a03-43a5-416c-aa17-098fa45830a2',bindingId:'cbba4072-76e2-42ab-813a-239645d5f996',reason:'ppa_terms'}]}),
  'No partial report was admitted.\n1 trade was refused (ppa_terms): 6abf2a03-43a5-416c-aa17-098fa45830a2.\nChoose books that leave out these trades to see the rest.');
});
