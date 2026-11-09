import test from 'node:test';
import assert from 'node:assert/strict';
import {pagingNeeded} from '../dist/jobs-view.js';

test('paging controls appear only when there is another page',()=>{
 assert.equal(pagingNeeded(0,false),false);
 assert.equal(pagingNeeded(1,false),true);
 assert.equal(pagingNeeded(0,true),true);
});
