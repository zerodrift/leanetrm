import test from 'node:test';
import assert from 'node:assert/strict';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

const views=['facilities-position','facilities-transport','facilities','facilities-crude'];
const html=views.map(view=>`<button id="nav-${view}">${view}</button>`).join('');
const setup=`(async()=>{window.chosen=[];window.prepared=[];window.opened=0;window.onPage=true;window.blocked=false;window.shown='facilities';window.allowed=()=>true;
 window.nav=(await import('/facilities-views.js')).setupFacilityViews({view:()=>shown,choose:view=>chosen.push(view),prepare:view=>{prepared.push(view);shown=view;},open:()=>opened++,onPage:()=>onPage,busy:()=>blocked,available:view=>allowed(view)});})()`;
const browser={skip:!browserAvailable,timeout:browserTestTimeout};
const active=view=>`document.querySelector('#nav-${view}').classList.contains('active')`;

test('on Facilities an entry moves to its view through the workspace and marks the view shown',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);
 await evaluate("nav.page('facilities')");
 assert.equal(await evaluate(active('facilities')),true);
 await evaluate("document.querySelector('#nav-facilities-transport').click()");
 assert.deepEqual(await evaluate('chosen'),['facilities-transport']);
 assert.equal(await evaluate('opened'),0);
 assert.equal(await evaluate(active('facilities-transport')),false,'the mark waits for the workspace to move');
 await evaluate("shown='facilities-transport';nav.sync()");
 assert.equal(await evaluate(active('facilities-transport')),true);
 assert.equal(await evaluate(active('facilities')),false);
 await evaluate("document.querySelector('#nav-facilities-transport').click()");
 assert.deepEqual(await evaluate('chosen'),['facilities-transport'],'the shown view is not chosen again');
}));
test('from another page an entry opens Facilities and takes effect only when the page has moved',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);
 await evaluate("onPage=false;nav.page('market');document.querySelector('#nav-facilities-position').click()");
 assert.equal(await evaluate('opened'),1);
 assert.deepEqual(await evaluate('prepared'),[],'a refused leave changes nothing');
 await evaluate("onPage=true;nav.page('facilities')");
 assert.deepEqual(await evaluate('prepared'),['facilities-position']);
 assert.equal(await evaluate(active('facilities-position')),true);
 await evaluate("nav.page('market')");
 assert.equal(await evaluate(active('facilities-position')),false);
}));
test('a busy app or a view the account may not open does nothing',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);
 await evaluate("nav.page('facilities');allowed=view=>view!=='facilities-crude';document.querySelector('#nav-facilities-crude').click();blocked=true;nav.sync();document.querySelector('#nav-facilities-position').click()");
 assert.deepEqual(await evaluate('chosen'),[]);
 assert.equal(await evaluate("document.querySelector('#nav-facilities-position').disabled"),true);
}));
