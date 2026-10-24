import test from 'node:test';
import assert from 'node:assert/strict';
import {trackEdits} from '../dist/edit-tracking.js';

test('a form is edited by the person\'s own input and by a picker that sets a value for them, never by a script\'s event, and its owner clears the mark',()=>{
 const form=new EventTarget(),edits=trackEdits(form);
 assert.equal(edits.edited(),false);
 form.dispatchEvent(new Event('input'));form.dispatchEvent(new Event('change'));
 assert.equal(edits.edited(),false,'an event a script dispatches is not an edit, and a picker\'s are a script\'s');
 edits.mark();
 assert.equal(edits.edited(),true,'a picker marks the form itself, when the person chooses what it sets');
 edits.clear();
 assert.equal(edits.edited(),false);
});

test('the owner hears when a form starts and stops holding edits, once for each, and not for what changes nothing',()=>{
 const form=new EventTarget(),heard=[],edits=trackEdits(form,[],()=>heard.push(edits.edited()));
 edits.clear();
 assert.deepEqual(heard,[],'a form that holds nothing is cleared without a word');
 edits.mark();edits.mark();
 assert.deepEqual(heard,[true],'it starts holding edits once, however many follow');
 edits.clear();edits.clear();
 assert.deepEqual(heard,[true,false],'and stops holding them once');
});
