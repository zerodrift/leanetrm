import test from 'node:test';
import assert from 'node:assert/strict';
import {SideCalendarSelection,calendarKey,referenceCalendarChoices,savedCalendarCode} from '../dist/side-calendar.js';

const reference=(revisionId,code='synthetic')=>({revision:{catalogId:'catalog',revisionId},code});
test('one explicit calendar supplies a side without choosing for another side',()=>{
 const first=new SideCalendarSelection(),second=new SideCalendarSelection();
 first.choose('a');assert.equal(first.read(['a','b']),'a');assert.equal(second.value(),undefined);
 assert.throws(()=>second.read(['a','b']),/Choose/);
});
test('matching saved calendars restore, mixed saved calendars require an explicit choice',()=>{
 const selection=new SideCalendarSelection();selection.restore(['a',null,'a']);assert.equal(selection.read(['a','b']),'a');
 selection.restore(['a','b']);assert.equal(selection.value(),undefined);assert.throws(()=>selection.read(['a','b']),/different/);
 selection.choose('b');assert.equal(selection.read(['a','b']),'b');
});
test('same calendar code at different revisions is a conflict, independent of object property order',()=>{
 const a=reference('one'),b=reference('two'),reordered={code:a.code,revision:{revisionId:'one',catalogId:'catalog'}};
 assert.equal(calendarKey(a),calendarKey(reordered));assert.notEqual(calendarKey(a),calendarKey(b));
 const selection=new SideCalendarSelection();selection.restore([calendarKey(a),calendarKey(b)]);
 assert.throws(()=>selection.read([calendarKey(a),calendarKey(b)]),/different/);
});
test('refreshing choices never replaces a missing retained calendar or an explicit clear',()=>{
 const selection=new SideCalendarSelection();selection.restore(['old']);
 assert.throws(()=>selection.read(['new']),/unavailable/);assert.equal(selection.value(),'old');
 selection.choose('');assert.equal(selection.value(),undefined);assert.throws(()=>selection.read(['new']),/Choose/);
});
test('a saved payment code cannot choose arbitrarily between reference versions',()=>{
 const choices=referenceCalendarChoices(['one','two'].map(revision=>({reference:reference(revision),code:'synthetic'})));
 const selection=new SideCalendarSelection();selection.restore([savedCalendarCode('synthetic',choices)]);
 assert.throws(()=>selection.read(choices.map(c=>c.value)),/unavailable/);
});
