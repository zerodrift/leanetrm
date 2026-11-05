import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

const html=readFileSync(new URL('../src/index.html',import.meta.url),'utf8');
const begin=html.indexOf('<section id="physical-workspace"'),end=html.indexOf('<section id="crude-setup-page"',begin);
assert.ok(begin>=0&&end>begin,'The physical workspace header must precede its existing editors.');
const page=`<link rel="stylesheet" href="/app.css"><div id="host"><button id="nav-physical">Facilities &amp; delivery</button>${html.slice(begin,end)}</section></div>`;

test('one physical workspace preserves guarded navigation and each section’s access',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async(evaluate,command)=>{
  await evaluate(`(async()=>{window.opened=[];window.allowed=['gas-setup','crude-setup'];window.blocked=false;
   window.physical=(await import('/physical-workspace.js')).setupPhysicalWorkspace({open:page=>opened.push(page),available:page=>allowed.includes(page),busy:()=>blocked});
   physical.show('market');})()`);
  assert.deepEqual(await evaluate('opened'),[],'constructing or hiding navigation performs no data read');
  await evaluate("document.querySelector('#nav-physical').click()");
  assert.deepEqual(await evaluate('opened'),['gas-setup']);
  await evaluate("physical.show('gas-setup')");
  assert.equal(await evaluate("document.querySelector('#physical-workspace').hidden"),false);
  assert.equal(await evaluate("document.querySelector('#nav-physical').getAttribute('aria-current')"),'page');
  assert.equal(await evaluate("document.querySelector('#nav-facilities').hidden"),true,'delivery readers do not gain pool access');
  await evaluate("document.querySelector('#nav-crude-setup').click()");
  assert.equal(await evaluate("document.querySelector('#nav-gas-setup').getAttribute('aria-current')"),'page','a cancelled leave keeps the displayed commodity selected');
  await evaluate("physical.show('crude-setup');physical.show('market');document.querySelector('#nav-physical').click()");
  assert.deepEqual(await evaluate('opened'),['gas-setup','crude-setup','crude-setup'],'returning to the common entry remembers the last physical section');
  await evaluate("allowed.push('facilities');physical.show('crude-setup');physical.sync();document.querySelector('#nav-facilities').click();physical.show('facilities')");
  assert.equal(await evaluate("document.querySelector('#physical-commodity').hidden"),true);
  await evaluate("document.querySelector('#physical-delivery-setup').click()");
  assert.equal(await evaluate('opened.at(-1)'),'crude-setup','delivery setup remembers its commodity when the facility ledger is open');
  await evaluate("blocked=true;physical.sync();document.querySelector('#nav-gas-setup').onclick()");
  assert.equal(await evaluate('opened.at(-1)'),'crude-setup','a busy owner cannot start a competing navigation');
  await evaluate("blocked=false;allowed=[];physical.sync()");
  assert.equal(await evaluate("document.querySelector('#physical-workspace').hidden"),true,'revoked access withholds the entire workspace');
  await evaluate("allowed=['gas-setup','crude-setup'];physical.reset();document.querySelector('#nav-physical').click()");
  assert.equal(await evaluate('opened.at(-1)'),'gas-setup','an account reset does not carry the previous account’s section');
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  await evaluate("physical.show('gas-setup')");
  assert.equal(await evaluate('document.documentElement.scrollWidth<=innerWidth'),true);
 });
});


test('facility coverage exposes disabled planned workflows without creating navigation',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async(evaluate,command)=>{
  await evaluate(`(async()=>{window.opened=[];window.allowed=['gas-setup','crude-setup','facilities'];window.blocked=false;
   window.physical=(await import('/physical-workspace.js')).setupPhysicalWorkspace({open:page=>opened.push(page),available:page=>allowed.includes(page),busy:()=>blocked});physical.show('gas-setup');})()`);
  assert.equal(await evaluate("document.querySelector('#physical-type-coverage').hasAttribute('data-own-controls')"),true,'the coverage panel owns disabled states across the app busy lifecycle');
  const planned=['Gas storage','Power plants','Rail / trains','Barges','Tankers','Trucks','LNG terminals','Processing plants','Terminals / ports','Bulk storage / tank farms'];
  assert.deepEqual(await evaluate("[...document.querySelectorAll('#physical-type-coverage button[data-planned]')].map(b=>b.querySelector('strong').textContent)"),planned);
  assert.equal(await evaluate("[...document.querySelectorAll('#physical-type-coverage button[data-planned]')].every(b=>b.disabled&&b.textContent.includes('Planned · TBD'))"),true);
  await evaluate("document.querySelectorAll('#physical-type-coverage button[data-planned]').forEach(b=>b.click())");
  assert.deepEqual(await evaluate('opened'),[],'planned workflows cannot read or open business operations');
  assert.match(await evaluate("document.querySelector('#physical-type-definitions').textContent"),/Reference data.*Locations/);
  await evaluate("document.querySelector('#physical-pipelines').click()");
  assert.equal(await evaluate('opened.at(-1)'),'gas-setup');
  await evaluate("document.querySelector('#physical-crude-sites').click()");
  assert.equal(await evaluate('opened.at(-1)'),'crude-setup');
  await evaluate("blocked=true;physical.sync()");
  assert.equal(await evaluate("document.querySelector('#physical-pipelines').disabled"),true);
  await evaluate("blocked=false;allowed=['gas-setup'];physical.sync()");
  assert.equal(await evaluate("document.querySelector('#physical-crude-sites').disabled"),true,"a setup card cannot widen the owner's authority");
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  assert.equal(await evaluate('document.documentElement.scrollWidth<=innerWidth'),true);
 });
});
