import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

const browser={skip:!browserAvailable,timeout:browserTestTimeout};
const html='<section id="roadmap" hidden></section>';
const setup=`(async()=>{window.roadmap=(await import('/scheduling-roadmap.js')).setupSchedulingRoadmap(document.querySelector('#roadmap'));
 window.titles=()=>[...document.querySelectorAll('#roadmap h3')].map(e=>e.textContent);})()`;

test('Crude names its missing workflows as TBD without offering operational actions',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);await evaluate("roadmap.show('crude')");
 assert.equal(await evaluate("document.querySelector('#roadmap').hidden"),false);
 assert.match(await evaluate("document.querySelector('#roadmap summary').textContent"),/Crude.*Planned workflows/);
 assert.equal(await evaluate("document.querySelector('#roadmap details').open"),false);
 await evaluate("document.querySelector('#roadmap summary').click()");
 assert.deepEqual(await evaluate('titles()'),['Cargo parcels & liftings','Pipeline movements','Tank inventory & blending','Voyages & freight','Time charters','Laytime & demurrage','Rail & truck movements']);
 assert.deepEqual(await evaluate("[...document.querySelectorAll('.workflow-status')].map(e=>e.textContent)"),Array(7).fill('TBD'));
 assert.match(await evaluate("document.querySelector('#roadmap').textContent"),/not implemented/);
 assert.match(await evaluate("document.querySelector('#roadmap').textContent"),/synthetic example/);
 assert.match(await evaluate("document.querySelector('#roadmap').textContent"),/off-hire/);
 assert.equal(await evaluate("document.querySelectorAll('#roadmap button,#roadmap input,#roadmap select,#roadmap form').length"),0);
}));

test('the roadmap follows the shown desk and disappears outside Scheduling',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);await evaluate("roadmap.show('gas')");
 assert.deepEqual(await evaluate('titles()'),['Pipeline connections','Storage statements']);
 await evaluate("roadmap.show('power')");
 assert.deepEqual(await evaluate('titles()'),['Schedules & dispatch','Transmission scheduling']);
 await evaluate("roadmap.show('lng')");
 assert.deepEqual(await evaluate('titles()'),['Cargo actuals & reconciliation','Terminal operations','Voyages & freight','Time charters','Laytime & demurrage']);
 await evaluate('roadmap.show(null)');
 assert.equal(await evaluate("document.querySelector('#roadmap').hidden"),true);
 assert.equal(await evaluate("document.querySelector('#roadmap').textContent"),'');
}));

test('ordinary page synchronization preserves an expanded roadmap and keyboard focus',browser,()=>withBrowserPage(html,async evaluate=>{
 await evaluate(setup);
 await evaluate("roadmap.show('crude');document.querySelector('#roadmap summary').click();document.querySelector('#roadmap summary').focus();roadmap.show('crude')");
 assert.equal(await evaluate("document.querySelector('#roadmap details').open"),true);
 assert.equal(await evaluate("document.activeElement===document.querySelector('#roadmap summary')"),true);
}));

const index=await readFile(new URL('../src/index.html',import.meta.url),'utf8');
const roadmapMarkup=index.split('\n').find(line=>line.includes('id="scheduling-roadmap"'));
assert.ok(roadmapMarkup,'use the actual application roadmap markup');
test('an expanded roadmap never becomes the assistant target for an operating page',browser,()=>withBrowserPage('<main>'+roadmapMarkup+'<section id="physical-operations-page">Trade deliveries</section></main>',async evaluate=>{
 const target=await evaluate(`(async()=>{const {setupSchedulingRoadmap}=await import('/scheduling-roadmap.js');
  const {pagePresentationTarget}=await import('/assistant-walkthroughs.js');setupSchedulingRoadmap(document.querySelector('#scheduling-roadmap')).show('crude');
  document.querySelector('#scheduling-roadmap details').open=true;return pagePresentationTarget(document.querySelector('main')).id;})()`);
 assert.equal(target,'physical-operations-page');
}));
