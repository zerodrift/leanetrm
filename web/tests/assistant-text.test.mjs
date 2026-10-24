import {test} from 'node:test';
import assert from 'node:assert/strict';
import {answerBlocks,inlines} from '../dist/assistant-text.js';

const text=value=>({kind:'text',text:value});

test('paragraphs split on blank lines and keep their single line breaks',()=>{
  assert.deepEqual(answerBlocks('First line\nsecond line\n\n\nNext paragraph'),[
    {kind:'paragraph',lines:[[text('First line')],[text('second line')]]},
    {kind:'paragraph',lines:[[text('Next paragraph')]]}]);
  assert.deepEqual(answerBlocks('Carriage\r\nreturns\rand\nfeeds'),[{kind:'paragraph',lines:[[text('Carriage')],[text('returns')],[text('and')],[text('feeds')]]}]);
  assert.deepEqual(answerBlocks(''),[]);
  assert.deepEqual(answerBlocks('  \n\n'),[]);
});

test('bulleted and numbered lists become lists, each run of one kind its own',()=>{
  assert.deepEqual(answerBlocks('Totals:\n- Book A\n* Book B\n+ Book C\n1. First\n2) Second\n\nAfter'),[
    {kind:'paragraph',lines:[[text('Totals:')]]},
    {kind:'list',ordered:false,items:[[text('Book A')],[text('Book B')],[text('Book C')]]},
    {kind:'list',ordered:true,items:[[text('First')],[text('Second')]]},
    {kind:'paragraph',lines:[[text('After')]]}]);
  // A line that is not an item ends the list rather than joining an item.
  assert.deepEqual(answerBlocks('- One\nplain'),[{kind:'list',ordered:false,items:[[text('One')]]},{kind:'paragraph',lines:[[text('plain')]]}]);
  // A marker needs a space after it, so negative numbers and emphasis stay text.
  assert.deepEqual(answerBlocks('-5 lots\n*not a list*'),[{kind:'paragraph',lines:[[text('-5 lots')],[{kind:'emphasis',text:'not a list'}]]}]);
});

test('fenced code keeps its text exactly, even unclosed while the answer streams',()=>{
  assert.deepEqual(answerBlocks('Run:\n```sql\nSELECT *\n  FROM t; -- **not strong**\n```\nDone'),[
    {kind:'paragraph',lines:[[text('Run:')]]},{kind:'code',text:'SELECT *\n  FROM t; -- **not strong**'},{kind:'paragraph',lines:[[text('Done')]]}]);
  assert.deepEqual(answerBlocks('```\n- not a list\n\nstill code'),[{kind:'code',text:'- not a list\n\nstill code'}]);
});

test('emphasis, strong and code spans are recognised only when closed; anything else is literal text',()=>{
  assert.deepEqual(inlines('A **strong** and *emphasised* `code` mix'),[text('A '),{kind:'strong',text:'strong'},text(' and '),
    {kind:'emphasis',text:'emphasised'},text(' '),{kind:'code',text:'code'},text(' mix')]);
  assert.deepEqual(inlines('`**literal** inside code` then **x**'),[{kind:'code',text:'**literal** inside code'},text(' then '),{kind:'strong',text:'x'}]);
  assert.deepEqual(inlines('2 * 3 * 4 and ** loose ** and `open'),[text('2 * 3 * 4 and ** loose ** and `open')]);
  assert.deepEqual(inlines('a*b*c'),[text('a'),{kind:'emphasis',text:'b'},text('c')]);
});

test('links, images and markup stay literal text; nothing becomes a link or an element',()=>{
  for(const literal of ['[the blotter](https://example.invalid/x)','![chart](https://example.invalid/c.png)','<script>alert(1)</script>',
    'See https://example.invalid/path and <b>bold</b> | a | table |'])
    assert.deepEqual(answerBlocks(literal),[{kind:'paragraph',lines:[[text(literal)]]}]);
});

test('a line longer than 2,000 characters stays text, so a long answer draws in time',()=>{
  const long='*a* '.repeat(500)+'**b**';
  assert.equal(long.length,2005);
  assert.deepEqual(inlines(long),[text(long)]);
  assert.equal(inlines('*a* '.repeat(499)).filter(s=>s.kind==='emphasis').length,499);
});
