import test from 'node:test';
import assert from 'node:assert/strict';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

const entries=['facilities-position','physical-operations','facilities-transport','facilities','facilities-crude','rec-inventory'];
const html='<div id="scheduling-desks" hidden></div>'+entries.map(entry=>`<button id="nav-${entry}">${entry}</button>`).join('');
const setup=`(async()=>{window.opened=[];window.calls=[];window.refused=[];window.explained=[];window.blocked=false;window.rights=['ReadMarketData','ReadTrades','ReadGasOperations'];window.saved={};
 window.desks=(await import('/scheduling-desks.js')).setupSchedulingDesks({permissions:()=>rights,owner:()=>'synthetic-owner',storage:()=>({getItem:key=>saved[key]??null,setItem:(key,value)=>{saved[key]=value;}}),open:(entry,arrive)=>{opened.push(entry);return new Promise(resolve=>{calls.push({arrive,settle:resolve});});},may:(desk,entry)=>!refused.includes(desk)&&!refused.includes(entry),refuse:desk=>explained.push(desk),busy:()=>blocked});
 window.listed=()=>[...document.querySelectorAll('[id^=nav-]')].filter(b=>!b.hidden).map(b=>b.id.slice(4));
 window.pressed=()=>[...document.querySelectorAll('#scheduling-desks button')].filter(b=>b.getAttribute('aria-pressed')==='true').map(b=>b.textContent);})()`;
const browser={skip:!browserAvailable,timeout:browserTestTimeout};

test('the first available desk is shown with its entries only',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);await evaluate('desks.start()');
 assert.equal(await evaluate('desks.current()'),'gas');
 assert.deepEqual(await evaluate('pressed()'),['Gas']);
 assert.deepEqual(await evaluate('listed()'),['facilities-position','physical-operations','facilities-transport','facilities']);
 assert.deepEqual(await evaluate("[...document.querySelectorAll('#scheduling-desks button')].map(b=>b.textContent)"),['Gas','Power','Crude','LNG']);
 assert.equal(await evaluate("document.querySelector('#scheduling-desks').hidden"),false);
}));
test('choosing a desk opens its first entry and changes desk only when the page has moved',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);await evaluate("desks.start();desks.page('physical-operations');document.querySelector('#scheduling-desk-crude').click()");
 assert.deepEqual(await evaluate('opened'),['physical-operations']);
 assert.equal(await evaluate('desks.current()'),'gas','a refused leave changes nothing');
 await evaluate("calls[0].arrive();desks.page('physical-operations')");
 assert.equal(await evaluate('desks.current()'),'crude');
 assert.deepEqual(await evaluate('pressed()'),['Crude']);
 assert.deepEqual(await evaluate('listed()'),['physical-operations','facilities-crude']);
 assert.equal(await evaluate('saved["openetrm:desk:v1:synthetic-owner"]'),'crude');
}));
test('a refused leave leaves no desk behind for a later navigation',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);await evaluate("desks.start();desks.page('physical-operations');document.querySelector('#scheduling-desk-crude').click()");
 await evaluate("calls[0].settle();new Promise(resolve=>setTimeout(resolve,0))");
 await evaluate("desks.page('physical-operations')");
 assert.equal(await evaluate('desks.current()'),'gas');
 assert.deepEqual(await evaluate('pressed()'),['Gas']);
}));
test('a second choice made while the first still asks does not take the first one\'s desk',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);await evaluate("desks.start();desks.page('physical-operations');document.querySelector('#scheduling-desk-crude').click();document.querySelector('#scheduling-desk-lng').click()");
 assert.deepEqual(await evaluate('opened'),['physical-operations','physical-operations']);
 await evaluate("calls[1].settle();new Promise(resolve=>setTimeout(resolve,0))");
 await evaluate("calls[0].arrive();desks.page('physical-operations')");
 assert.equal(await evaluate('desks.current()'),'crude','the navigation that moved the page carries its own desk');
}));
test('a desk the app holds back is not opened',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);await evaluate("desks.start();refused=['crude'];document.querySelector('#scheduling-desk-crude').click();document.querySelector('#scheduling-desk-power').click()");
 assert.deepEqual(await evaluate('opened'),['rec-inventory']);
 assert.equal(await evaluate('desks.current()'),'gas');
 assert.deepEqual(await evaluate('explained'),['crude'],'the app is told so it can say why');
}));
test('a desk opens on its first entry the app does not hold back',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);await evaluate("desks.start();refused=['physical-operations'];document.querySelector('#scheduling-desk-crude').click()");
 assert.deepEqual(await evaluate('opened'),['facilities-crude'],'Receipts & dispatches stays reachable');
 assert.deepEqual(await evaluate('explained'),[]);
 await evaluate("calls[0].arrive();desks.page('facilities-crude')");
 assert.equal(await evaluate('desks.current()'),'crude');
 await evaluate("document.querySelector('#scheduling-desk-lng').click()");
 assert.deepEqual(await evaluate('opened'),['facilities-crude'],'a desk with no other entry is not opened');
 assert.deepEqual(await evaluate('explained'),['lng']);
}));
test('an entry on another desk is reached through that desk\'s control',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);await evaluate('desks.start()');
 assert.equal(await evaluate("desks.chip('rec-inventory').id"),'scheduling-desk-power');
 assert.equal(await evaluate("desks.chip('facilities-crude').id"),'scheduling-desk-crude');
 assert.equal(await evaluate("desks.chip('physical-operations')"),null,'the shown desk lists it');
 assert.equal(await evaluate("desks.chip('market')"),null);
}));
test('the desk follows an entry opened by another route, and a page outside Scheduling keeps it',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);await evaluate("desks.start();desks.follow('rec-inventory')");
 assert.equal(await evaluate('desks.current()'),'power');
 await evaluate("desks.page('market')");
 assert.equal(await evaluate('desks.current()'),'power');
 await evaluate("desks.want('crude');desks.page('physical-operations')");
 assert.equal(await evaluate('desks.current()'),'crude');
 await evaluate("desks.page('physical-operations')");
 assert.equal(await evaluate('desks.current()'),'crude','a wish is used once');
}));
test('one available desk needs no switcher, and a remembered desk is restored',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);await evaluate("saved['openetrm:desk:v1:synthetic-owner']='lng';desks.start()");
 assert.equal(await evaluate('desks.current()'),'lng');
 assert.deepEqual(await evaluate('listed()'),['physical-operations']);
 await evaluate("rights=[];desks.start()");
 assert.equal(await evaluate('desks.current()'),null);
 assert.equal(await evaluate("document.querySelector('#scheduling-desks').hidden"),true);
 assert.deepEqual(await evaluate('listed()'),[]);
}));
test('a busy app does not change desk',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);await evaluate("desks.start();blocked=true;desks.sync();document.querySelector('#scheduling-desk-power').click()");
 assert.deepEqual(await evaluate('opened'),[]);
 assert.equal(await evaluate("document.querySelector('#scheduling-desk-power').disabled"),true);
}));
