// Curve details are drawn when visited, from the selected result, without rebuilding hidden views.
export async function run({command,evaluate,wait,click,set,persona,check,idle,url}){
 await command('Runtime.enable');await command('Page.enable');
 await command('Page.navigate',{url});await wait(idle+"&&document.querySelectorAll('.curve-button').length>0");
 await persona('editor');await click('#nav-market');await click('.tab[data-view=prices]');
 const selected=await evaluate("[...document.querySelector('#snapshot').options].find(o=>o.textContent.includes('Dec 18')).value");
 await set('#snapshot',selected);await wait(idle);
 const choose=async name=>{await evaluate(`([...document.querySelectorAll('.curve-button')].find(b=>b.textContent.startsWith(${JSON.stringify(name)}))).click()`);await wait(idle+"&&document.querySelector('#market-page').dataset.readState==='ready'");};
 const empty="['source-flow','dependencies','mappings','inputs'].every(id=>document.getElementById(id).children.length===0)";
 await choose('Local power forwards');
 await check('choosing prices draws its chart and rows without constructing either hidden detail view',
  "document.querySelector('#curve-chart canvas')&&document.querySelector('#contracts tbody tr')&&"+empty+"&&document.querySelector('#error').hidden");
 await click('.tab[data-view=inputs]');
 await check('revealing price sources draws only its graph and observations',
  "document.querySelector('#source-flow').children.length>0&&document.querySelector('#inputs tbody tr')&&document.querySelector('#dependencies').children.length===0&&document.querySelector('#mappings').children.length===0&&document.querySelector('#error').hidden");
 await evaluate("window.__sourceNodes=[document.querySelector('#source-flow').firstChild,document.querySelector('#inputs').firstChild]");
 await click('.tab[data-view=prices]');await click('.tab[data-view=inputs]');
 await check('returning to unchanged price sources preserves its existing DOM',
  "window.__sourceNodes[0]===document.querySelector('#source-flow').firstChild&&window.__sourceNodes[1]===document.querySelector('#inputs').firstChild");
 await click('.tab[data-view=dependencies]');
 await check('revealing pricing constructs its own graph and steps without replacing source rows',
  "document.querySelector('#dependencies').children.length>0&&document.querySelector('#mappings tbody tr')&&window.__sourceNodes[0]===document.querySelector('#source-flow').firstChild&&window.__sourceNodes[1]===document.querySelector('#inputs').firstChild");
 const next=await evaluate("[...document.querySelector('#pricing-contract').options][1].value");
 await set('#pricing-contract',next);await wait(idle);
 await check('changing the selected contract redraws the visible steps and invalidates hidden sources',
  `document.querySelector('#pricing-contract').value===${JSON.stringify(next)}&&document.querySelector('#mappings tbody tr')&&document.querySelector('#inputs').children.length===0&&document.querySelector('#source-flow').children.length===0`);
 await click('.tab[data-view=inputs]');
 await check('sources revealed after a contract change use that same exact contract',
  `document.querySelector('#source-contract').value===${JSON.stringify(next)}&&document.querySelector('#inputs tbody tr')&&window.__sourceNodes[1]!==document.querySelector('#inputs').firstChild&&document.querySelector('#error').hidden`);
 await click('#source-all');await click('.tab[data-view=dependencies]');
 await check('all-contract selection is retained when the other detail tab is revealed',
  "document.querySelector('#pricing-all').getAttribute('aria-pressed')==='true'&&document.querySelector('#mappings tbody tr')&&document.querySelector('#error').hidden");
 await click('.tab[data-view=prices]');await choose('Gas futures');
 await check('selecting another curve clears the old hidden details',empty+"&&document.querySelector('#error').hidden");
 await click('.tab[data-view=inputs]');
 await check('a direct curve still shows its whole source workbook',
  "document.querySelector('#source-contract').closest('.pricing-toolbar').hidden&&document.querySelectorAll('#inputs tbody tr').length>1&&document.querySelector('#error').hidden");
 await click('.tab[data-view=dependencies]');await click('.tab[data-view=inputs]');
 await click('#inputs tbody tr:last-child button');
 await check('a direct curve can inspect every source after visiting pricing',
  "!document.querySelector('#revision-card').hidden&&document.querySelector('#revisions tbody tr')&&document.querySelector('#error').hidden");
 await set('#correction-value','4.321');
 await click('.tab[data-view=prices]');await click('.tab[data-view=inputs]');
 await click('#profile-open');await click('#preferences-form button[type=submit]');
 await check('tab navigation and a display-preference redraw retain the unfinished correction',
  "!document.querySelector('#revision-card').hidden&&document.querySelector('#correction-value').value==='4.321'");
 await click('.tab[data-view=prices]');await click('#show-inactive-contracts');await click('.tab[data-view=inputs]');
 await check('changing contract display at the same close retains an edit to a selected observation',
  "!document.querySelector('#revision-card').hidden&&document.querySelector('#correction-value').value==='4.321'");
 await click('#reference-area-curves');await click('#nav-table-currencies');
 await click('#profile-open');await click('#preferences-form button[type=submit]');await click('#nav-market');
 await check('returning to Market after changing display preferences on another page draws the selected Sources tab',
  "!document.querySelector('#view-inputs').hidden&&document.querySelector('#inputs tbody tr')&&document.querySelector('#correction-value').value==='4.321'");
 await click('.tab[data-view=prices]');await click('#show-inactive-contracts');await click('.tab[data-view=inputs]');
 await set('#snapshot','');await wait(idle);
 await check('clearing the market date invalidates rendered sources and corrections',
  "!document.querySelector('#inputs tbody tr')&&!document.querySelector('#dependencies .pricing-node')&&!document.querySelector('#revisions').children.length&&document.querySelector('#correction-value').value===''&&document.querySelector('#revision-card').hidden&&document.querySelector('#error').hidden");
 await click('.tab[data-view=prices]');await set('#snapshot',selected);await wait(idle);await choose('Interest rates');
 await check('choosing rates also leaves the hidden detail views unconstructed',empty+"&&document.querySelector('#curve-chart canvas')&&document.querySelector('#error').hidden");
 await click('.tab[data-view=dependencies]');
 await check('rate calculation steps draw on their own tab',"document.querySelector('#dependencies').children.length>0&&document.querySelector('#mappings tbody tr')&&document.querySelector('#inputs').children.length===0&&document.querySelector('#error').hidden");
 await click('.tab[data-view=inputs]');
 await check('rate sources draw when revealed',"document.querySelector('#source-flow').children.length>0&&document.querySelector('#inputs tbody tr')&&document.querySelector('#error').hidden");
 await click('.tab[data-view=prices]');await persona('viewer');
}
