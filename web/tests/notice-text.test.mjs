import test from 'node:test';
import assert from 'node:assert/strict';
import {withNotice} from '../dist/notice-text.js';

test('a notice added to what the page already says is kept beside it, and one already said is not said twice',()=>{
 assert.equal(withNotice('','Risk results cleared.'),'Risk results cleared.');
 assert.equal(withNotice('The panel was closed.','Risk results cleared.'),'The panel was closed. Risk results cleared.');
 assert.equal(withNotice('The panel was closed. Risk results cleared.','Risk results cleared.'),'The panel was closed. Risk results cleared.');
});
