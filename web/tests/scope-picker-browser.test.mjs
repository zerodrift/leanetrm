import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage} from './browser-page.mjs';

const page='<link rel="stylesheet" href="/app.css"><div id="host"><label for="portfolios">Portfolios</label><select id="portfolios" multiple></select><label for="books">Books</label><select id="books" multiple></select></div>';
async function fixture(work){
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{
   const {setupBookScopePicker}=await import('/scope-picker.js');
   window.busy=false;
   window.scope=setupBookScopePicker({books:document.getElementById('books'),portfolios:document.getElementById('portfolios'),busy:()=>busy});
   window.directory=[{id:'north',name:'North book',portfolio_id:'energy',portfolio:'Energy'},{id:'south',name:'South book',portfolio_id:'energy',portfolio:'Energy'},{id:'rates',name:'Rates book',portfolio_id:'rates-portfolio',portfolio:'Rates'}];
   scope.set(directory,{bookIds:[],portfolioIds:[]});
   window.selected=id=>Array.from(document.getElementById(id).selectedOptions,o=>o.value);
  })()`);
  await work(evaluate);
 });
}
test('searchable portfolio choices narrow the books while preserving an explicit intersection',{skip:!browserAvailable},async()=>{
 await fixture(async evaluate=>{
  await evaluate("document.querySelector('#portfolios-picker summary').click();document.querySelector('#portfolios-picker input[type=checkbox][value=energy]').click()");
  assert.deepEqual(await evaluate("selected('portfolios')"),['energy']);
  await evaluate("document.querySelector('#books-picker summary').click()");
  assert.deepEqual(await evaluate("Array.from(document.querySelectorAll('#books-picker .scope-option input'),n=>n.value)"),['north','south']);
  await evaluate("const search=document.querySelector('#books-picker input[type=search]');search.value='south';search.dispatchEvent(new Event('input',{bubbles:true}))");
  assert.deepEqual(await evaluate("Array.from(document.querySelectorAll('#books-picker .scope-option input')).filter(n=>n.checkVisibility()).map(n=>n.value)"),['south']);
  await evaluate("document.querySelector('#books-picker input[type=checkbox][value=south]').click();scope.set(directory,{bookIds:selected('books'),portfolioIds:['rates-portfolio']})");
  assert.deepEqual(await evaluate("selected('books')"),['south'],'changing portfolio choices must not silently broaden a selected book scope');
  assert.equal(await evaluate("document.querySelector('#books-picker').textContent.includes('Outside selected portfolios')"),true);
  await evaluate("document.querySelector('#books-picker [aria-label=\"Remove Energy · South book\"]').click()");
  assert.deepEqual(await evaluate("selected('books')"),[]);
 });
});
test('scope controls clear names on replacement and follow busy state',{skip:!browserAvailable},async()=>{
 await fixture(async evaluate=>{
  await evaluate("scope.set(directory,{bookIds:['north'],portfolioIds:['energy']});busy=true;scope.sync()");
  assert.equal(await evaluate("document.querySelector('#books-picker summary').getAttribute('aria-disabled')"),'true');
  await evaluate("busy=false;scope.set([],{bookIds:[],portfolioIds:[]});scope.sync()");
  assert.equal(await evaluate("document.getElementById('host').textContent.includes('North book')||document.getElementById('host').textContent.includes('Energy')"),false);
 });
});
test('refreshing the same directory keeps controls bound to the current selection',{skip:!browserAvailable},async()=>{
 await fixture(async evaluate=>{
  await evaluate("scope.set(directory,{bookIds:[],portfolioIds:[]});document.querySelector('#books-picker summary').click();document.querySelector('#books-picker input[value=north]').focus();document.querySelector('#books-picker input[value=north]').click()");
  assert.deepEqual(await evaluate("selected('books')"),['north']);
  assert.equal(await evaluate("document.activeElement.value"),'north','checkbox selection retains keyboard focus');
  await evaluate("scope.set(directory,{bookIds:['north'],portfolioIds:[]});document.querySelector('#books-picker input[value=north]').click()");
  assert.deepEqual(await evaluate("selected('books')"),[]);
  await evaluate("scope.set(directory,{bookIds:['north'],portfolioIds:[]});scope.set(directory,{bookIds:['north'],portfolioIds:[]});document.querySelector('#books-picker [aria-label=\"Remove Energy · North book\"]').click()");
  assert.deepEqual(await evaluate("selected('books')"),[]);
  await evaluate("document.querySelector('#books-picker').open=true;document.querySelector('#books-picker input[type=search]').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}))");
  assert.equal(await evaluate("document.querySelector('#books-picker').open"),false);
  assert.equal(await evaluate("document.activeElement===document.querySelector('#books-picker summary')"),true);
 });
});
test('equal book names in different portfolios have distinct choices, chips and summary',{skip:!browserAvailable},async()=>{
 await fixture(async evaluate=>{
  await evaluate("scope.set([{id:'north-hedge',name:'Hedge book',portfolio_id:'north',portfolio:'North'},{id:'south-hedge',name:'Hedge book',portfolio_id:'south',portfolio:'South'}],{bookIds:['north-hedge','south-hedge'],portfolioIds:[]})");
  assert.deepEqual(await evaluate("Array.from(document.querySelectorAll('#books-picker .scope-option'),n=>n.textContent)"),['North · Hedge book','South · Hedge book']);
  assert.deepEqual(await evaluate("Array.from(document.querySelectorAll('#books-picker .scope-chip>span'),n=>n.textContent)"),['North · Hedge book','South · Hedge book']);
  assert.equal(await evaluate("document.querySelector('#books-picker .scope-value').textContent"),'North · Hedge book, South · Hedge book');
 });
});
