# Scheduling by Desk Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Scheduling sidebar group lists one desk's entries at a time (Gas, Power, Crude, LNG), each entry a view of a page that exists today, and Lifecycle and Operating facilities are listed under Trading.

**Architecture:** Presentation only, except one optional request field. Facilities stays one page with nine sections; a new data module groups the sections into four sidebar views, in the idiom of `web/src/risk-views.ts`. Physical operations stays one page; it lists one desk's trades by sending the desk to its directory read. A desk switcher in the sidebar decides which entries are shown. No page id, permission, table or migration changes.

**Tech Stack:** TypeScript (`web/src`), Node test runner (`web/tests`), browser scenarios (`tests/browser`), one C# request record and filter (`src/OpenEtrm.Api`), one Python check (`tools/`), Markdown (`docs/`).

**Spec:** [docs/superpowers/specs/2026-11-27-scheduling-workspace-design.md](../specs/2026-11-27-scheduling-workspace-design.md), step 1 only. The desk idea is defined in [the scope design](../specs/2026-11-27-product-scope-and-desks-design.md); the installation setting it describes is not part of this plan.

## Global Constraints

- TypeScript is presentation only. The browser never filters, totals or classifies trades. A desk's trade list comes from the server (Task 2).
- Page ids do not change: `facilities`, `physical-operations`, `rec-inventory`, `lifecycle`, `physical-power`. Sidebar button ids `nav-facilities`, `nav-physical-operations`, `nav-rec-inventory`, `nav-lifecycle` and `nav-physical-power` keep their ids, because about 25 files under `tests/browser` click them.
- No account may reach a page or section it could not reach before, and none may lose one. The sidebar rules added in Task 4 copy the section gates already in `web/src/facilities-workspace.ts` (`transportAllowed`, `physicalAllowed`, the `crude-delivery` rule). The server still authorizes every read and write.
- The assistant's page names and page reports do not change (`web/src/assistant-pages.ts`, `src/OpenEtrm.Api/PageReports.cs`). D367 set this precedent for Jobs.
- Leave guards stay. Every move between entries, sections or desks goes through the existing `leave()` of the page or section being left. Do not add a path that sets a section or desk around a guard, except `prepareView` in Task 3, which runs only while arriving from another page.
- Write the failing test first in every task, run it and see it fail for the stated reason, then implement. One commit per task, with `git commit -s`. Nothing is pushed.
- The code blocks below were written without a compiler. Treat them as the intended shape. If a block does not compile or a quoted line is not where the plan says, find the quoted text, keep the behavior the tests describe, and fix the code.
- Work on the local branch `claude/scheduling-desks`. The two specs and this plan are untracked files there; commit them first, as one commit of their own, unless the owner already has.
- Line numbers are from this plan's base commit (`da0b3c22` on `master`). After a rebase, find the quoted text.
- `D4NN` is the next free decision number in `docs/decisions.md` at build time. On this plan's base the newest is D460.
- Every `Run:` step needs Node from `.node-version` (24.22.0) and `npm ci --prefix web --ignore-scripts` done once. Steps marked **(browser)** also need a Chromium the tests can find (see `web/tests/browser-page.mjs` and `docs/contributing-setup.md`); without one those tests skip, which is not a pass. Steps marked **(database)** need the pinned .NET SDK in `global.json` and a disposable PostgreSQL 18. Do not mark a `Run:` step done without its output. If a tool is missing where you work, stop at the first step that needs it and say so.
- Read `AGENTS.md`, `docs/architecture.md` and `docs/plan.md` before changing anything. Synthetic examples only; no customer, company or host names in any file or commit message.

## Scope decided in this plan

- **One Trade deliveries entry, three desks.** `nav-physical-operations` is listed by the Gas, Crude and LNG desks. The page reads the current desk's trades. On the LNG desk its name is Cargoes.
- **Hidden tabs stay in the document.** All nine Facilities tab buttons are still built. The ones outside the shown view carry `hidden`. Programmatic opens of any section keep working (the storage ticket, the outlook's links, back-navigation, the assistant, existing tests), and the sidebar entry and desk follow the section shown.
- **Not in this plan:** back-navigation restoring Trades and volumes or the position view (the page report names all three `facility_quantities`; changing it reaches the backend page registry); the "Open delivery" link, stage names and outlook identifiers (the spec's step 2); the installation's enabled desks (the scope design); a Power scheduling entry beyond REC inventory.
- **The inner heading "Pipeline outlook"** on the position view is left as it is.

## Review Focus

1. **An account with `ReadMarketData` and `ReadTrades` only.** It sees the Gas desk with Trade deliveries and Storage & allocations, the Power desk, the Crude desk and the LNG desk. It does not see Gas day position or Nominations & transport. (Task 4 permission tests, Task 5 desk tests)
2. **A refused leave.** With unsaved input on a gas delivery day, clicking the Crude desk asks the existing question; choosing Keep leaves the desk, the trade list and the form as they were. (Task 5 browser test, Task 6 scenario)
3. **A section opened from elsewhere.** Opening Crude delivery through `facilities.open('crude-delivery')` while the Gas desk is shown moves the sidebar to the Crude desk and marks Receipts & dispatches. (Task 5)
4. **The directory without a desk.** A request that omits `desk` returns exactly what it returned before, page for page. An unknown desk is refused, never read as "all". (Task 2)
5. **Arrow keys in a tab row.** They move among shown tabs only. (Task 3)

## Files

| File | Change | Task |
| --- | --- | --- |
| `web/src/navigation-groups.ts`, `web/src/index.html`, `web/src/futures-option-view.ts`, `web/src/swap-settlement.ts`, `web/tests/navigation-groups.test.mjs`, `web/tests/lifecycle-model.test.mjs`, `tests/browser/futures-options.mjs`, `tests/browser/structured-power.mjs` | Lifecycle and Operating facilities under Trading | 1 |
| `src/OpenEtrm.Api/TradePositionFacts.cs`, `src/OpenEtrm.Api/TradeReads.cs`, `src/OpenEtrm.Api/Program.cs`, `tools/check_physical_operations.py` | The directory accepts a desk | 2 |
| `web/src/facilities-views.ts` (new), `web/src/facilities-workspace.ts`, `web/src/side-panel.ts`, `web/tests/facilities-views.test.mjs` (new), `web/tests/side-panel-tabs-browser.test.mjs` (new), `web/tests/facilities-workspace-browser.test.mjs`, `tests/browser/gas-entry-outlook.mjs` | Facilities sections grouped into views | 3 |
| `web/src/facilities-views.ts`, `web/src/navigation-groups.ts`, `web/src/index.html`, `web/src/app.ts`, `web/tests/facilities-views-browser.test.mjs` (new), `web/tests/navigation-groups.test.mjs`, `tests/browser/physical-crude.mjs` | Sidebar entries for the views | 4 |
| `web/src/scheduling-desks.ts` (new), `web/src/navigation-groups.ts`, `web/src/physical-operations.ts`, `web/src/index.html`, `web/src/app.css`, `web/src/app.ts`, `web/tests/scheduling-desks.test.mjs` (new), `web/tests/scheduling-desks-browser.test.mjs` (new), `web/tests/physical-operations-browser.test.mjs` | The desk switcher | 5 |
| `tests/browser/gas-operations.mjs` | One end-to-end scenario | 6 |
| `docs/decisions.md`, `docs/plan.md`, `docs/physical-workspace.md`, `docs/delivery-workbench.md`, `docs/gas-facility-desk.md`, `docs/operating-facilities.md`, `docs/structured-power-trading.md`, `docs/frontend-wiring.md`, both specs | Documents and the decision | 7 |

---

### Task 1: Lifecycle and Operating facilities under Trading

The sidebar's groups are data in `web/src/navigation-groups.ts` (line 10) and markup in `web/src/index.html` (lines 12 to 37). `pageGroup(page)` supplies the eyebrow above the page title.

**Files:**
- Modify: `web/tests/navigation-groups.test.mjs`, `web/tests/lifecycle-model.test.mjs`
- Modify: `web/src/navigation-groups.ts:12,15`, `web/src/index.html`
- Modify: `web/src/futures-option-view.ts:238,244`, `web/src/swap-settlement.ts:37`
- Modify: `tests/browser/futures-options.mjs:158,191`, `tests/browser/structured-power.mjs:17`

- [ ] **Step 1: Change the tests to state the new groups**

In `web/tests/navigation-groups.test.mjs`, in the test `the groups and their pages keep the sidebar order`, replace the Trading and Scheduling rows:

```js
  ['trading','Trading',['new-trade','futures','options','lifecycle','physical-power']],
```
```js
  ['scheduling','Scheduling',['physical-operations','facilities','rec-inventory']],
```

Replace the first two lines of the test near line 163:

```js
test('operating facilities are a Trading page with exact physical read permission',()=>{
 assert.equal(pageGroup('physical-power'),'Trading');
```

In `web/tests/lifecycle-model.test.mjs` lines 9 and 10, change the test name to `Lifecycle has one Trading destination for authorized readers and an exact assistant page` and `pageGroup('lifecycle'),'Scheduling'` to `pageGroup('lifecycle'),'Trading'`.

- [ ] **Step 2: Run them and see them fail**

Run: `cd web && npm run build && node --test tests/navigation-groups.test.mjs tests/lifecycle-model.test.mjs`
Expected: FAIL in the three edited tests; the group rows and `pageGroup` still say Scheduling.

- [ ] **Step 3: Move the two pages**

In `web/src/navigation-groups.ts`:

```ts
 {id:'trading',label:'Trading',pages:['new-trade','futures','options','lifecycle','physical-power'],opened:['ppa','gas','crude']},
```
```ts
 {id:'scheduling',label:'Scheduling',pages:['physical-operations','facilities','rec-inventory']},
```

In `web/src/index.html`, cut the whole `<button id="nav-lifecycle" …>` line and the whole `<button id="nav-physical-power" …>` line out of the `nav-group-scheduling` block and paste them, in that order, directly after the `<button id="nav-options" …>` line in the `nav-group-trading` block. Change nothing inside the two buttons.

- [ ] **Step 4: Fix the text that names the old place**

Run: `git grep -n -E "Scheduling → Lifecycle|under Scheduling" -- web/src web/tests tests/browser`

In every hit, `Scheduling → Lifecycle` becomes `Trading → Lifecycle` and `under Scheduling` becomes `under Trading`. On this base the hits in `web/src` are `futures-option-view.ts:238`, `futures-option-view.ts:244` and `swap-settlement.ts:37`.

In `tests/browser/futures-options.mjs` line 158, `includes('Scheduling → Lifecycle')` becomes `includes('Trading → Lifecycle')`; on line 191, `includes('Scheduling')` becomes `includes('Trading')`.

In `tests/browser/structured-power.mjs` line 17, the check's name becomes `operating facilities are listed under Trading and leave the existing facility workspace intact` and `textContent==='SCHEDULING'` becomes `textContent==='TRADING'`.

- [ ] **Step 5: Run the web tests**

Run: `cd web && npm test`
Expected: PASS. If another test asserts where `nav-lifecycle` or `nav-physical-power` sit in the markup, update it to the new place without weakening it.

- [ ] **Step 6: Commit**

```bash
git add web/src/navigation-groups.ts web/src/index.html web/src/futures-option-view.ts web/src/swap-settlement.ts web/tests tests/browser/futures-options.mjs tests/browser/structured-power.mjs
git commit -s -m "List Lifecycle and Operating facilities under Trading"
```

---

### Task 2: The physical directory accepts a desk

`POST /api/physical/operations/directory` (`src/OpenEtrm.Api/Program.cs:2715`) returns booked gas, LNG and crude trades. It binds `TradingPositionsRequest`, which `/trading/positions` also binds, so it gets a request record of its own.

**Files:**
- Modify: `tools/check_physical_operations.py` (function `directory_checks`)
- Modify: `src/OpenEtrm.Api/TradePositionFacts.cs:11,141-158`, `src/OpenEtrm.Api/TradeReads.cs:66`, `src/OpenEtrm.Api/Program.cs:2715-2717`

- [ ] **Step 1: Write the failing check**

In `tools/check_physical_operations.py`, in `directory_checks`, add directly before the final `print`:

```python
    # A desk lists its own instrument type; no desk lists every physical trade, exactly as before.
    assert directory['nextOffset'] is None, 'this fixture must fit one page for the comparison below'
    for desk, kind in (('gas', 'physical_gas'), ('crude', 'physical_crude'), ('lng', 'physical_lng')):
        scoped = client.call('physical/operations/directory', {**request, 'desk': desk})
        assert scoped['positions'] == [p for p in directory['positions'] if p['instrumentType'] == kind], desk
        assert scoped['total'] == len(scoped['positions']) and scoped['nextOffset'] is None, desk
    assert client.call('physical/operations/directory', {**request, 'desk': None})['positions'] == directory['positions']
    refused = client.call('physical/operations/directory', {**request, 'desk': 'power'}, expected=422)
    assert refused['code'] == 'position_desk', refused
```

Before relying on `total` and `nextOffset`, read `TradePositionsPage` in `TradePositionFacts.cs` and one existing use of the response in this file to confirm the JSON key names. Change the final `print` text to end `…without erasing held positions; a desk lists only its own trades`.

- [ ] **Step 2: Run it and see it fail (database)**

`directory_checks` is called from `tools/check_gas_operations.py` line 283. Run that check as `tools/check_all.py` runs it (search `check_all.py` for `check_gas_operations`).
Expected: FAIL at the first scoped call with HTTP 400, because the request record refuses the unknown member `desk`.

- [ ] **Step 3: Add the request record and the filter**

In `src/OpenEtrm.Api/TradePositionFacts.cs`, after `TradingPositionsRequest`:

```csharp
// The physical directory alone accepts a Scheduling desk. No desk lists every physical trade.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PhysicalDirectoryRequest([property:JsonRequired] FuturesReadRequest Context,
    [property:JsonRequired] ImmutableArray<Guid> BookIds,[property:JsonRequired] ImmutableArray<Guid> PortfolioIds,
    [property:JsonRequired] int Offset,[property:JsonRequired] int Limit,string? Desk=null);
```

Change `Physical` and `PhysicalFacts` (lines 141 to 158):

```csharp
    internal async Task<TradePositionsPage> Physical(NpgsqlConnection connection,ImmutableArray<Guid> books,ImmutableArray<Guid> portfolios,string? desk,int offset,int limit,CancellationToken ct)
    {
        RequirePage(offset,limit);
        if(desk is not(null or "gas" or "crude" or "lng"))
            throw new MarketDataException("position_desk","Choose the gas, crude or lng desk, or omit the desk to list every physical trade.");
        return Page(await PhysicalFacts(connection,books,portfolios,desk,ct),offset,limit);
    }
    // Only products with a native consumer on the Physical Operations page belong in this directory.
    private async Task<ImmutableArray<(int Reference,TradePositionFact Position)>> PhysicalFacts(NpgsqlConnection connection,ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,string? desk,CancellationToken ct)
    {
        var facts=ImmutableArray.CreateBuilder<(int Reference,TradePositionFact Position)>();
        if(desk is null or "gas")foreach(var gas in await GasPositions.Read(connection,manifest,bookIds,portfolioIds,ct))
            facts.Add((Integer(gas.Head,"trade_reference"),gas.Fact()));
        if(desk is null or "lng")foreach(var lng in await LngPositions.Read(connection,manifest,bookIds,portfolioIds,ct))
            facts.Add((Integer(lng.Head,"trade_reference"),lng.Fact()));
        if(desk is null or "crude")foreach(var crude in await CrudePositions.Read(connection,manifest,bookIds,portfolioIds,ct))
            facts.Add((Integer(crude.Head,"trade_reference"),crude.Fact()));
        return facts.ToImmutable();
    }
```

Keep the existing comment line above `PhysicalFacts` once, not twice. In `Read` (line 119), the call becomes `PhysicalFacts(connection,bookIds,portfolioIds,null,ct)`.

In `src/OpenEtrm.Api/TradeReads.cs` line 66, add `string? desk` after `portfolios` in `PhysicalPositions` and pass it to `Physical` in the same position.

In `src/OpenEtrm.Api/Program.cs` line 2715, the body type becomes `PhysicalDirectoryRequest` and the call becomes `tradeReads.PhysicalPositions(connection,view,body.BookIds,body.PortfolioIds,body.Desk,body.Offset,body.Limit,ct)`.

Then run `git grep -n "PhysicalPositions\|physical/operations/directory" -- src tests` and confirm no other caller or schema binds the old record to this route.

- [ ] **Step 4: Run the checks (database)**

Run the gas operations check again, then the check that calls `mixed_directory_checks` (`tools/check_structured_power.py`).
Expected: PASS, including the new final line.

- [ ] **Step 5: Commit**

```bash
git add src/OpenEtrm.Api/TradePositionFacts.cs src/OpenEtrm.Api/TradeReads.cs src/OpenEtrm.Api/Program.cs tools/check_physical_operations.py
git commit -s -m "List one desk's trades in the physical operations directory"
```

---

### Task 3: Facilities sections grouped into views

`web/src/facilities-workspace.ts` builds nine tabs in one row (line 40) and a `<select>` for narrow screens (line 41). After this task the workspace knows which view its shown section belongs to and shows that view's tabs only. Nothing in the sidebar changes yet.

**Files:**
- Create: `web/src/facilities-views.ts`, `web/tests/facilities-views.test.mjs`, `web/tests/side-panel-tabs-browser.test.mjs`
- Modify: `web/src/facilities-workspace.ts`, `web/src/side-panel.ts:104-108`
- Modify: `web/tests/facilities-workspace-browser.test.mjs`, `tests/browser/gas-entry-outlook.mjs:33`

- [ ] **Step 1: Write the failing tests**

Create `web/tests/facilities-views.test.mjs`:

```js
import test from 'node:test';
import assert from 'node:assert/strict';
import {facilityViews,facilityViewSections,facilityViewOf,facilityViewNames,facilitySectionNames} from '../dist/facilities-views.js';

test('every Facilities section belongs to exactly one sidebar view',()=>{
 const sections=facilityViews.flatMap(view=>facilityViewSections[view]);
 assert.deepEqual([...sections].sort(),['activity','crude-delivery','definitions','gas-delivery','outlook','quantities','route','trades','transport']);
 assert.equal(new Set(sections).size,sections.length);
 for(const view of facilityViews)for(const section of facilityViewSections[view])assert.equal(facilityViewOf(section),view);
});
test('the views and their tabs are named for the work done there',()=>{
 assert.deepEqual(facilityViews.map(view=>[view,facilityViewNames[view],facilityViewSections[view].map(section=>facilitySectionNames[section])]),[
  ['facilities-position','Gas day position',['Gas day position']],
  ['facilities-transport','Nominations & transport',['Nominations','Route plans','Capacity']],
  ['facilities','Storage & allocations',['Allocations','Physical activity','Trades and volumes','Definitions']],
  ['facilities-crude','Receipts & dispatches',['Receipts & dispatches']]]);
});
```

Create `web/tests/side-panel-tabs-browser.test.mjs`:

```js
import test from 'node:test';
import assert from 'node:assert/strict';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

test('arrow keys move among the tabs that are shown',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="list"></div><div id="panel"></div>',async evaluate=>{
 await evaluate(`(async()=>{window.chosen=[];const {setupTabs}=await import('/side-panel.js');
  setupTabs(document.querySelector('#list'),[['a','A'],['b','B'],['c','C']],{label:'Synthetic tabs',panel:document.querySelector('#panel'),selected:'a',select:id=>chosen.push(id)});
  document.querySelector('#list-c').hidden=true;
  document.querySelector('#list-a').dispatchEvent(new KeyboardEvent('keydown',{key:'End',bubbles:true}));
  document.querySelector('#list-b').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}));})()`);
 assert.deepEqual(await evaluate('chosen'),['b','a']);
}));
```

In `web/tests/facilities-workspace-browser.test.mjs`, line 102, `/Transport route plans/` becomes `/Route plans/`. Then add this test at the end of the file. Its setup is the first test's setup with every permission granted:

```js
test('Facilities shows the tabs of the view its section belongs to',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(`(async()=>{
  window.errors=[];
  const context={contextId:'synthetic-context',effectiveOn:'2032-03-14',knownAt:'2032-03-14T20:00:00Z'};
  const api=async path=>{if(path==='facilities/quantities/directory')return{ownerId:'opaque-owner',context,at:'2032-03-14T23:59:59Z',ledgers:[],choices:[],hasMore:false,choicesTruncated:false,canRecord:true,canConfigure:true};throw Error('Unexpected '+path);};
  window.workspace=(await import('/facilities-workspace.js')).setupFacilities({api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,zone:()=> 'UTC',identity:()=> 'synthetic-operator',context:()=>({effectiveOn:'2032-03-14',knowledgeMode:'current',retained:null,convention:'facility_business_date_end_utc_v1'}),permission:()=>true,changed(){},tradeInspection:()=>null,openTrade:async()=>{}});
  await workspace.load();
  window.tabs=()=>[...document.querySelectorAll('[data-facilities-view]')].filter(b=>!b.hidden).map(b=>b.dataset.facilitiesView);
  window.picks=()=>[...document.querySelector('#facilities-section-picker').options].map(o=>o.value);
 })()`);
 assert.equal(await evaluate('workspace.view()'),'facilities');
 assert.deepEqual(await evaluate('tabs()'),['quantities','activity','trades','definitions']);
 assert.deepEqual(await evaluate('picks()'),['quantities','activity','trades','definitions']);
 assert.equal(await evaluate("document.querySelector('#facilities-workspace-tabs').hidden"),false);
 await evaluate("workspace.prepareView('facilities-transport')");
 assert.equal(await evaluate('workspace.section()'),'gas-delivery');
 assert.equal(await evaluate('workspace.view()'),'facilities-transport');
 assert.deepEqual(await evaluate('tabs()'),['gas-delivery','route','transport']);
 await evaluate("workspace.prepareView('facilities-position')");
 assert.equal(await evaluate('workspace.section()'),'outlook');
 assert.equal(await evaluate("document.querySelector('#facilities-workspace-tabs').hidden"),true,'one section needs no tab row');
 assert.equal(await evaluate("document.querySelector('#facilities-section-picker').hidden"),true);
 assert.equal(await evaluate("workspace.viewAvailable('facilities-crude')"),true);
 assert.deepEqual(await evaluate('errors'),[]);
}));
```

- [ ] **Step 2: Run them and see them fail (browser)**

Run: `cd web && npm run build && node --test tests/facilities-views.test.mjs tests/side-panel-tabs-browser.test.mjs tests/facilities-workspace-browser.test.mjs`
Expected: the build passes. `facilities-views.test.mjs` fails with "Cannot find module … facilities-views.js". The tab test fails with `chosen` equal to `['c','a']`. The new workspace test fails on `workspace.view is not a function`, and line 102 fails on the old tab name.

- [ ] **Step 3: Create the views module**

Create `web/src/facilities-views.ts`:

```ts
// Facilities is one page of nine sections. The sidebar lists it as four views, each holding the sections of one kind of work, in the
// order their tabs show. A view keeps the page's permissions: its sidebar rule in navigation-groups.ts copies its sections' gates.
export type FacilitySection='trades'|'outlook'|'quantities'|'definitions'|'activity'|'transport'|'route'|'gas-delivery'|'crude-delivery';
export const facilityViews=['facilities-position','facilities-transport','facilities','facilities-crude'] as const;
export type FacilityView=typeof facilityViews[number];
export const facilityViewSections:Record<FacilityView,readonly FacilitySection[]>={
 'facilities-position':['outlook'],
 'facilities-transport':['gas-delivery','route','transport'],
 facilities:['quantities','activity','trades','definitions'],
 'facilities-crude':['crude-delivery']};
export const facilityViewNames:Record<FacilityView,string>={'facilities-position':'Gas day position','facilities-transport':'Nominations & transport',facilities:'Storage & allocations','facilities-crude':'Receipts & dispatches'};
export const facilitySectionNames:Record<FacilitySection,string>={outlook:'Gas day position','gas-delivery':'Nominations',route:'Route plans',transport:'Capacity',
 quantities:'Allocations',activity:'Physical activity',trades:'Trades and volumes',definitions:'Definitions','crude-delivery':'Receipts & dispatches'};
export const facilityViewOf=(section:FacilitySection):FacilityView=>facilityViews.find(view=>facilityViewSections[view].includes(section))!;
```

- [ ] **Step 4: Teach the workspace its views**

In `web/src/facilities-workspace.ts`:

Add the import and replace the local `Section` type (line 10):

```ts
import {facilityViews,facilityViewSections,facilityViewOf,facilitySectionNames,type FacilitySection,type FacilityView} from './facilities-views.js';

type Section=FacilitySection;
```

Replace the `sections` constant (line 40) so tabs are built in view order with the new names:

```ts
 const sections:[Section,string][]=facilityViews.flatMap(view=>facilityViewSections[view].map(id=>[id,facilitySectionNames[id]] as [Section,string]));
```

Delete the line `for(const [id,label] of sections)picker.add(new Option(label,id));` (line 42). The picker's options are now drawn by `show()`.

Replace `show()` (line 52):

```ts
 const tab=(id:Section)=>navigation.querySelector<HTMLButtonElement>('#'+navigation.id+'-'+id)!;
 // The tab row and the picker list the sections of the view the shown section belongs to. The other tabs stay in the document,
 // hidden, so an exact section can still be opened from elsewhere; the view then follows it.
 function show(){
  const listed=facilityViewSections[facilityViewOf(section)];
  tabs.mark(section);
  for(const [id]of sections)tab(id).hidden=!listed.includes(id);
  navigation.hidden=picker.hidden=listed.length<2;
  picker.replaceChildren(...listed.map(id=>new Option(facilitySectionNames[id],id)));picker.value=section;
  for(const [id,host]of Object.entries(hosts))host.hidden=id!==(section==='definitions'?'activity':section);if(section==='definitions'||section==='activity')operations.show(section);
 }
 const firstOf=(view:FacilityView)=>facilityViewSections[view].find(available)??null;
```

In `chooseSection`, the first line `tabs.mark(section);picker.value=section;` stays as it is.

In the returned object, `sync()` disables picker options by `available`; it still works on the redrawn options. Add four members to the returned object:

```ts
  view:()=>facilityViewOf(section),
  viewAvailable:(view:FacilityView)=>facilityViewSections[view].some(available),
  // Arriving from another page: shows the view's first permitted section without reading. The page's own load reads it.
  prepareView(view:FacilityView){const next=firstOf(view);if(next&&facilityViewOf(section)!==view){section=next;show();}},
  // On the page: moves to the view through the shown section's leave guard, as a tab does.
  chooseView(view:FacilityView){const next=firstOf(view);if(next&&facilityViewOf(section)!==view)chooseSection(next);},
```

`initial()` is unchanged, so Facilities still opens on Allocations for an account that may read facility quantities.

- [ ] **Step 5: Make arrow keys skip hidden tabs**

In `web/src/side-panel.ts`, inside `setupTabs`, the `list.onkeydown` handler (line 105) becomes:

```ts
  list.onkeydown=event=>{
    const shown=ids.filter(id=>!buttons.get(id)!.hidden);
    const current=shown.find(id=>buttons.get(id)===event.target),next=current===undefined?null:tabAfterKey(shown,current,event.key);
    if(next===null)return;
    event.preventDefault();choose(next,true);
  };
```

- [ ] **Step 6: Fix the one browser scenario that expects the picker on the position view**

In `tests/browser/gas-entry-outlook.mjs` line 33, the position view now has one section and no picker. Replace

```js
document.querySelector('#facilities-section-picker').checkVisibility()&&document.querySelector('#facilities-section-picker').value==='outlook'
```

with

```js
!document.querySelector('#facilities-section-picker').checkVisibility()&&document.querySelector('#outlook-filters').checkVisibility()
```

- [ ] **Step 7: Run the web tests (browser)**

Run: `cd web && npm test`
Expected: PASS, with the browser tests run, not skipped. Every existing test that clicks `[data-facilities-view=…]` still passes, because the buttons still exist. If an existing test expects the narrow-screen picker to list all nine sections, change it to expect the shown view's sections.

- [ ] **Step 8: Commit**

```bash
git add web/src/facilities-views.ts web/src/facilities-workspace.ts web/src/side-panel.ts web/tests/facilities-views.test.mjs web/tests/side-panel-tabs-browser.test.mjs web/tests/facilities-workspace-browser.test.mjs tests/browser/gas-entry-outlook.mjs
git commit -s -m "Group the Facilities sections into four views"
```

---

### Task 4: Sidebar entries for the Facilities views

The model is Risk: `web/src/risk-views.ts` lines 8 to 26, wired in `web/src/app.ts` line 1792. After this task Scheduling lists six entries in one list. Task 5 puts them under desks.

**Files:**
- Modify: `web/src/facilities-views.ts`, `web/src/navigation-groups.ts`, `web/src/index.html`, `web/src/app.ts`
- Create: `web/tests/facilities-views-browser.test.mjs`
- Modify: `web/tests/navigation-groups.test.mjs`, `tests/browser/physical-crude.mjs:52`

- [ ] **Step 1: Write the failing tests**

Create `web/tests/facilities-views-browser.test.mjs`:

```js
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
```

In `web/tests/navigation-groups.test.mjs`:

- In `the groups and their pages keep the sidebar order`, the Scheduling row becomes
  `['scheduling','Scheduling',['facilities-position','physical-operations','facilities-transport','facilities','facilities-crude','rec-inventory']],`
- In `the sidebar lists each page for the permissions that open it` and in `console and sidebar pages expose only their authorized native readers`, add `'facilities-crude'` to every expected list that contains `'facilities'`.
- Add this test after `Transport capacity can be reached without physical-facility authority`:

```js
test('each Facilities view is listed for the permissions its sections need',()=>{
 const trades=['ReadMarketData','ReadTrades'];
 for(const view of ['facilities-position','facilities-transport']){
  assert.equal(pageGroup(view),'Scheduling');
  assert.equal(pageAvailable(view,[...trades,'ReadGasOperations']),true);
  for(const permissions of [trades,['ReadMarketData','ReadGasOperations'],['ReadTrades','ReadGasOperations'],[...trades,'ReadFacilityQuantities']])assert.equal(pageAvailable(view,permissions),false);
 }
 assert.equal(pageAvailable('facilities-crude',trades),true);
 for(const permissions of [[],['ReadMarketData'],['ReadTrades']])assert.equal(pageAvailable('facilities-crude',permissions),false);
});
```

- [ ] **Step 2: Run them and see them fail (browser)**

Run: `cd web && npm run build && node --test tests/navigation-groups.test.mjs tests/facilities-views-browser.test.mjs`
Expected: FAIL. `setupFacilityViews` is not exported, and `pageGroup('facilities-position')` throws "could not load".

- [ ] **Step 3: Add the navigation controller**

Append to `web/src/facilities-views.ts`:

```ts
// Navigation only: the sidebar's Facilities entries. An entry is marked while Facilities shows a section of its view, whichever
// route opened that section. From another page an entry opens Facilities through the existing guards and takes effect once the
// page has moved; a refused leave changes nothing.
export function setupFacilityViews(o:{view:()=>FacilityView;choose:(view:FacilityView)=>void;prepare:(view:FacilityView)=>void;open:()=>void;onPage:()=>boolean;busy:()=>boolean;available:(view:FacilityView)=>boolean}){
 const button=(view:FacilityView)=>document.getElementById('nav-'+view) as HTMLButtonElement;
 let wanted:FacilityView|undefined,shown=false;
 function sync(){for(const view of facilityViews){button(view).disabled=o.busy();button(view).classList.toggle('active',shown&&view===o.view());}}
 for(const view of facilityViews)button(view).onclick=()=>{
  if(o.busy()||!o.available(view))return;
  if(!o.onPage()){wanted=view;o.open();}
  else if(view!==o.view())o.choose(view);
 };
 return {sync,
  // Called as the app moves to a page, before that page reads.
  page(page:string){shown=page==='facilities';if(shown&&wanted)o.prepare(wanted);wanted=undefined;sync();},
  reset(){wanted=undefined;shown=false;sync();},
 };
}
```

- [ ] **Step 4: List the views in the sidebar data**

In `web/src/navigation-groups.ts`, the Scheduling group becomes:

```ts
 {id:'scheduling',label:'Scheduling',pages:['facilities-position','physical-operations','facilities-transport','facilities','facilities-crude','rec-inventory']},
```

In `pageAvailable`, add before `case 'facilities':`:

```ts
  // Views of Facilities (facilities-views.ts). Each rule is the gate of its sections in facilities-workspace.ts.
  case 'facilities-position':case 'facilities-transport':return has('ReadMarketData','ReadTrades','ReadGasOperations');
  case 'facilities-crude':return has('ReadMarketData','ReadTrades');
```

Extend the comment at the top of the file with one sentence: `Facilities' entries are views of Facilities, as Risk's are of Risk.`

- [ ] **Step 5: Add the sidebar buttons**

In `web/src/index.html`, the `<nav aria-label="Scheduling">` block holds these six buttons, in this order. Keep the existing `nav-physical-operations`, `nav-facilities` and `nav-rec-inventory` buttons, with their icons, and change only the label text and the `aria-label` and `title` attributes of the first two:

```html
      <button id="nav-facilities-position" class="nav-item" aria-label="Gas day position" title="Gas day position" hidden><span class="nav-icon" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M3 19h18M6 16V9M11 16V5M16 16v-5M21 16V8"/></svg></span><span class="nav-text">Gas day position</span></button>
      <!-- nav-physical-operations: aria-label, title and text become "Trade deliveries" -->
      <button id="nav-facilities-transport" class="nav-item" aria-label="Nominations &amp; transport" title="Nominations &amp; transport" hidden><span class="nav-icon" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M3 8h12M3 16h12"/><circle cx="18" cy="8" r="3"/><circle cx="18" cy="16" r="3"/></svg></span><span class="nav-text">Nominations &amp; transport</span></button>
      <!-- nav-facilities: aria-label, title and text become "Storage &amp; allocations" -->
      <button id="nav-facilities-crude" class="nav-item" aria-label="Receipts &amp; dispatches" title="Receipts &amp; dispatches" hidden><span class="nav-icon" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M4 7h16M4 12h10M4 17h16"/><path d="M17 10l3 2-3 2"/></svg></span><span class="nav-text">Receipts &amp; dispatches</span></button>
      <!-- nav-rec-inventory: unchanged -->
```

Do not leave the HTML comments in the file.

- [ ] **Step 6: Wire the entries in the app**

All in `web/src/app.ts`.

1. Import: `import {setupFacilityViews,facilityViewNames} from './facilities-views.js';`
2. After the `riskViewsNav` line (1792) add:

```ts
const facilityViewsNav=setupFacilityViews({view:()=>facilities.view(),choose:view=>facilities.chooseView(view),prepare:view=>facilities.prepareView(view),open:()=>navigate('facilities')(),onPage:()=>page==='facilities',busy:()=>busy,
 available:view=>pageAvailable(view,permissions)&&facilities.viewAvailable(view)});
// Facilities is one page: its title is the view its shown section belongs to, and follows a section opened from anywhere.
function syncFacilitiesTitle(){if(page!=='facilities')return;const name=facilityViewNames[facilities.view()];if($('page-title').textContent!==name){text('page-title',name);$('page-title').title=name;}}
```

3. On the next line (1793), remove `'facilities'` from the list of names given `navigate(name)` as their `onclick`. The controller owns those buttons.
4. In `setPage` (line 435), remove `'facilities'` from the list of names whose `active` class is toggled.
5. In `setPage`, directly after `riskViewsNav.page(next);` add `facilityViewsNav.page(next);`
6. In `setPage`'s title expression (line 433), `next==='physical-operations'?'Physical operations'` becomes `next==='physical-operations'?'Trade deliveries'`, and `next==='facilities'?'Facilities'` becomes `next==='facilities'?facilityViewNames[facilities.view()]`.
7. In `syncControls` (line 220), after `facilities.sync();` add `facilityViewsNav.sync();syncFacilitiesTitle();`
8. In the sign-out reset (line 1312), after `facilities.reset();` add `facilityViewsNav.reset();`

`setPage` ends with `syncControls()`, so the title is corrected after `prepare` has moved the section.

- [ ] **Step 7: Fix the one scenario that names the old title**

In `tests/browser/physical-crude.mjs` line 52, `textContent==='Physical operations'` becomes `textContent==='Trade deliveries'`.

Run: `git grep -n -E "'Physical operations'|'Facilities'" -- web/tests tests/browser`
For each hit that asserts the page title or a sidebar label, use the new name. Leave assistant page names alone.

- [ ] **Step 8: Run the web tests (browser)**

Run: `cd web && npm test`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add web/src/facilities-views.ts web/src/navigation-groups.ts web/src/index.html web/src/app.ts web/tests tests/browser/physical-crude.mjs
git commit -s -m "List the Facilities views as Scheduling entries"
```

---

### Task 5: The desk switcher

**Files:**
- Create: `web/src/scheduling-desks.ts`, `web/tests/scheduling-desks.test.mjs`, `web/tests/scheduling-desks-browser.test.mjs`
- Modify: `web/src/navigation-groups.ts`, `web/src/physical-operations.ts:27,103-104`, `web/src/index.html`, `web/src/app.css`, `web/src/app.ts`
- Modify: `web/tests/physical-operations-browser.test.mjs`

- [ ] **Step 1: Write the failing tests**

Create `web/tests/scheduling-desks.test.mjs`:

```js
import test from 'node:test';
import assert from 'node:assert/strict';
import {navigationGroups,schedulingDesks} from '../dist/navigation-groups.js';
import {availableDesks,deskFor} from '../dist/scheduling-desks.js';

const trades=['ReadMarketData','ReadTrades'];
test('Scheduling lists its entries by desk, in the order the work is done',()=>{
 assert.deepEqual(schedulingDesks.map(desk=>[desk.id,desk.label,[...desk.pages]]),[
  ['gas','Gas',['facilities-position','physical-operations','facilities-transport','facilities']],
  ['power','Power',['rec-inventory']],
  ['crude','Crude',['physical-operations','facilities-crude']],
  ['lng','LNG',['physical-operations']]]);
 const scheduling=navigationGroups.find(group=>group.id==='scheduling');
 assert.deepEqual([...new Set(schedulingDesks.flatMap(desk=>desk.pages))].sort(),[...scheduling.pages].sort());
});
test('a desk is offered only when the account can open one of its entries',()=>{
 assert.deepEqual(availableDesks([]).map(desk=>desk.id),[]);
 assert.deepEqual(availableDesks(['ReadMarketData']).map(desk=>desk.id),[]);
 assert.deepEqual(availableDesks(trades).map(desk=>desk.id),['gas','power','crude','lng']);
});
test('an entry is shown under the current desk when it lists it, else under the first desk that does',()=>{
 assert.equal(deskFor('physical-operations','crude',trades),'crude');
 assert.equal(deskFor('physical-operations','power',trades),'gas');
 assert.equal(deskFor('rec-inventory','gas',trades),'power');
 assert.equal(deskFor('facilities-crude',null,trades),'crude');
 assert.equal(deskFor('market','gas',trades),null);
 assert.equal(deskFor('facilities-position','gas',trades),'gas','the desk stays; the entry itself is refused by its own rule');
});
```

Create `web/tests/scheduling-desks-browser.test.mjs`:

```js
import test from 'node:test';
import assert from 'node:assert/strict';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

const entries=['facilities-position','physical-operations','facilities-transport','facilities','facilities-crude','rec-inventory'];
const html='<div id="scheduling-desks" hidden></div>'+entries.map(entry=>`<button id="nav-${entry}">${entry}</button>`).join('');
const setup=`(async()=>{window.opened=[];window.blocked=false;window.rights=['ReadMarketData','ReadTrades','ReadGasOperations'];window.saved={};
 window.desks=(await import('/scheduling-desks.js')).setupSchedulingDesks({permissions:()=>rights,owner:()=>'synthetic-owner',storage:()=>({getItem:key=>saved[key]??null,setItem:(key,value)=>{saved[key]=value;}}),open:entry=>opened.push(entry),busy:()=>blocked});
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
 await evaluate("desks.page('physical-operations')");
 assert.equal(await evaluate('desks.current()'),'crude');
 assert.deepEqual(await evaluate('listed()'),['physical-operations','facilities-crude']);
 assert.equal(await evaluate('saved["openetrm:desk:v1:synthetic-owner"]'),'crude');
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
```

In `web/tests/physical-operations-browser.test.mjs`, add a test modeled on the first one in the file (line 8), passing one more option, `desk:()=>'crude'`, and asserting that the first recorded call's body has `desk:'crude'` beside the existing members. Keep the existing first test unchanged: it proves that without the option no `desk` is sent.

- [ ] **Step 2: Run them and see them fail (browser)**

Run: `cd web && npm run build && node --test tests/scheduling-desks.test.mjs tests/scheduling-desks-browser.test.mjs tests/physical-operations-browser.test.mjs`
Expected: FAIL. `schedulingDesks` and `scheduling-desks.js` do not exist; the new physical operations test finds no `desk` in the request.

- [ ] **Step 3: Declare the desks**

In `web/src/navigation-groups.ts`, above `navigationGroups`:

```ts
// Scheduling's entries by desk, each in the order its work is done. The sidebar shows one desk at a time (scheduling-desks.ts).
// Trade deliveries is one page listed by three desks; it reads the trades of the desk shown.
export type DeskId='gas'|'power'|'crude'|'lng';
export type Desk={id:DeskId;label:string;pages:readonly string[]};
export const schedulingDesks:readonly Desk[]=[
 {id:'gas',label:'Gas',pages:['facilities-position','physical-operations','facilities-transport','facilities']},
 {id:'power',label:'Power',pages:['rec-inventory']},
 {id:'crude',label:'Crude',pages:['physical-operations','facilities-crude']},
 {id:'lng',label:'LNG',pages:['physical-operations']},
];
```

The Scheduling group's `pages` list from Task 4 is unchanged: it is every entry once, in the order the buttons sit in the markup.

- [ ] **Step 4: Create the desk module**

Create `web/src/scheduling-desks.ts`:

```ts
import {schedulingDesks,pageAvailable,type Desk,type DeskId} from './navigation-groups.js';

type DeskStorage=Pick<Storage,'getItem'|'setItem'>;
const key=(owner:string)=>'openetrm:desk:v1:'+owner;
const lists=(desk:DeskId,entry:string)=>schedulingDesks.find(d=>d.id===desk)!.pages.includes(entry);
/** The desks an account can open: each lists at least one entry its permissions allow. */
export const availableDesks=(permissions:string[]):Desk[]=>schedulingDesks.filter(desk=>desk.pages.some(page=>pageAvailable(page,permissions)));
/** The desk an entry is shown under: the current desk when it lists the entry, else the first available desk that does. */
export function deskFor(entry:string,current:DeskId|null,permissions:string[]):DeskId|null{
 const desks=availableDesks(permissions).filter(desk=>desk.pages.includes(entry));
 return desks.find(desk=>desk.id===current)?.id??desks[0]?.id??null;
}

// Navigation only: which desk's entries the Scheduling group lists. Choosing a desk opens its first entry through that entry's own
// guards and takes effect once the page has moved; a refused leave changes nothing. Only the desk's identifier is remembered,
// per tab, workspace and account.
export function setupSchedulingDesks(o:{permissions:()=>string[];owner:()=>string;storage:()=>DeskStorage;open:(entry:string)=>void;busy:()=>boolean}){
 const root=document.getElementById('scheduling-desks')!,entries=[...new Set(schedulingDesks.flatMap(desk=>desk.pages))];
 let desk:DeskId|null=null,wanted:DeskId|undefined;
 function draw(){
  const desks=availableDesks(o.permissions());
  root.hidden=desks.length<2;
  root.replaceChildren(...desks.map(d=>{
   const button=document.createElement('button');button.type='button';button.id='scheduling-desk-'+d.id;button.className='nav-desk';button.textContent=d.label;
   button.setAttribute('aria-pressed',String(d.id===desk));button.disabled=o.busy();
   button.onclick=()=>{if(o.busy()||d.id===desk)return;const first=d.pages.find(page=>pageAvailable(page,o.permissions()));if(first===undefined)return;wanted=d.id;o.open(first);};
   return button;
  }));
  for(const entry of entries)(document.getElementById('nav-'+entry) as HTMLElement).hidden=!pageAvailable(entry,o.permissions())||desk===null||!lists(desk,entry);
 }
 function set(next:DeskId|null){
  if(next===null||next===desk)return;
  desk=next;try{o.storage().setItem(key(o.owner()),next);}catch{/* Navigation also works when browser storage is unavailable. */}
  draw();
 }
 return {
  current:()=>desk,
  label:()=>schedulingDesks.find(d=>d.id===desk)?.label??null,
  // Called when permissions are known: the remembered desk if the account can still open it, else the first it can.
  start(){
   const desks=availableDesks(o.permissions());let saved:string|null=null;try{saved=o.storage().getItem(key(o.owner()));}catch{/* As above. */}
   desk=desks.find(d=>d.id===saved)?.id??desks[0]?.id??null;wanted=undefined;draw();
  },
  // Called as the app moves to a page, with the Scheduling entry it shows. A desk chosen in the switcher, or wanted for an exact
  // target, takes effect here when it lists the entry.
  page(entry:string){const chosen=wanted;wanted=undefined;if(!entries.includes(entry))return;set(chosen!==undefined&&lists(chosen,entry)?chosen:deskFor(entry,desk,o.permissions()));},
  // Called when the entry shown may have changed without a page move, such as a Facilities section opened from elsewhere.
  follow(entry:string){if(entries.includes(entry))set(deskFor(entry,desk,o.permissions()));},
  // The desk an exact target belongs to, used by the next page move.
  want(next:DeskId){wanted=next;},
  sync(){for(const button of root.querySelectorAll('button'))button.disabled=o.busy();},
 };
}
```

- [ ] **Step 5: Send the desk with the directory read**

In `web/src/physical-operations.ts` line 27, add an optional member to the options type: `desk?:()=>string`. In `load` (lines 103 and 104), both `o.api('physical/operations/directory',{…})` request objects gain `...(o.desk?{desk:o.desk()}:{})` as their last member.

- [ ] **Step 6: Add the switcher's markup and style**

In `web/src/index.html`, in the `nav-group-scheduling` block, between `</summary>` and `<nav aria-label="Scheduling">`:

```html
<div id="scheduling-desks" class="nav-desks" role="group" aria-label="Desk" hidden></div>
```

In `web/src/app.css`, after the `.nav-view` rule (line 118):

```css
.nav-desks{display:flex;flex-wrap:wrap;gap:5px;margin:2px 6px 8px}
.nav-desk{background:var(--sidebar-2);border:1px solid var(--border);color:var(--sidebar-text);padding:4px 9px;font-size:12px;font-weight:500;border-radius:999px}
.nav-desk[aria-pressed=true]{background:var(--accent-soft);border-color:var(--accent-border);color:var(--accent-text)}
.sidebar-collapsed .nav-desks{display:none}
```

- [ ] **Step 7: Wire the desks in the app**

All in `web/src/app.ts`.

1. Import: `import {setupSchedulingDesks} from './scheduling-desks.js';`
2. Near `facilityViewsNav` add:

```ts
// The Scheduling entry the app shows, or null on a page outside Scheduling.
const schedulingEntry=()=>page==='facilities'?facilities.view():page==='physical-operations'||page==='rec-inventory'?page:null;
const desks=setupSchedulingDesks({permissions:()=>permissions,owner:()=>administrationScope+':'+exampleId+':'+currentProfile,storage:()=>sessionStorage,open:entry=>$('nav-'+entry).click(),busy:()=>busy});
// Trade deliveries is Cargoes on the LNG desk. The eyebrow names the desk beside the group.
const deliveriesName=()=>desks.current()==='lng'?'Cargoes':'Trade deliveries';
function syncScheduling(){
  const entry=schedulingEntry();if(entry)desks.follow(entry);
  const name=deliveriesName(),nav=$('nav-physical-operations');
  if(nav.title!==name){nav.title=name;nav.setAttribute('aria-label',name);nav.querySelector('.nav-text')!.textContent=name;}
  if(page==='physical-operations'&&$('page-title').textContent!==name){text('page-title',name);$('page-title').title=name;}
  const section=(pageGroup(page)+(entry&&desks.label()?' · '+desks.label():'')).toUpperCase();
  if($('page-section').textContent!==section)text('page-section',section);
}
```

3. In `syncControls` (line 220), after the `syncFacilitiesTitle();` added in Task 4, add `desks.sync();syncScheduling();`
4. In `setPage`, after `facilityViewsNav.page(next);` add `{const entry=schedulingEntry();desks.page(entry??next);}`. `page` already equals `next` there.
5. In `setPage`'s title expression, `next==='physical-operations'?'Trade deliveries'` becomes `next==='physical-operations'?deliveriesName()`.
6. Where navigation follows permissions (lines 1325 to 1328), the loop becomes:

```ts
  for(const group of sidebarGroups){
    for(const page of group.pages)$('nav-'+page).hidden=!pageAvailable(page,permissions);
    groupNode(group.id).hidden=!group.pages.some(page=>pageAvailable(page,permissions));
  }
  desks.start();
```

   `desks.start()` then hides the entries the shown desk does not list. The group's own visibility must use `pageAvailable`, not the buttons' `hidden`, or a group whose shown desk has one entry could be judged empty.
7. In the `physicalOperations` options (line 1419), add `desk:()=>{const desk=desks.current();if(desk!=='gas'&&desk!=='crude'&&desk!=='lng')throw Error('Choose a desk that lists trade deliveries.');return desk;},`
8. In `openNativeDelivery` (line 1425), directly before `await setPage('physical-operations');` add `desks.want(target.instrumentType==='physical_crude'?'crude':'gas');`

`physicalOperations.load()` runs after `setPage` in every route to the page (`showPage`, line 1756), so it reads the desk that `desks.page` has just set.

- [ ] **Step 8: Run the web tests (browser)**

Run: `cd web && npm test`
Expected: PASS.

- [ ] **Step 9: Look at it**

Start the local preview (`tools/run_preview.py`; see `docs/contributing-setup.md`) and check, at 1280 and at 390 pixels wide and with the sidebar collapsed:

- Scheduling shows Gas, Power, Crude and LNG, and under Gas the four entries in order.
- Each desk's Trade deliveries lists only that desk's trades; on LNG the entry and title read Cargoes.
- The eyebrow reads SCHEDULING · GAS and changes with the desk.
- On a gas delivery day, type in a nomination form, click Crude, choose Keep: the desk, list and form are unchanged.
- In Receipts & dispatches, a crude trade's native delivery button opens the Crude desk's Trade deliveries on that trade.

Record what you saw. If anything differs, fix it before committing.

- [ ] **Step 10: Commit**

```bash
git add web/src/scheduling-desks.ts web/src/navigation-groups.ts web/src/physical-operations.ts web/src/index.html web/src/app.css web/src/app.ts web/tests
git commit -s -m "Show Scheduling one desk at a time"
```

---

### Task 6: One end-to-end scenario

`tests/browser/gas-operations.mjs` opens Physical operations as a gas operator in a fixture that holds trades. Add checks there, directly after it first opens the page; read the file first and follow its `check(name, expression)` idiom.

**Files:**
- Modify: `tests/browser/gas-operations.mjs`

- [ ] **Step 1: Add the checks**

Add, in the file's own style, checks that:

1. `#scheduling-desks` is visible, its buttons read `Gas`, `Power`, `Crude`, `LNG` in order, and `#scheduling-desk-gas` has `aria-pressed="true"`.
2. With the Gas desk shown, the visible entries of `#nav-group-scheduling` are `nav-facilities-position`, `nav-physical-operations`, `nav-facilities-transport`, `nav-facilities`, in that order, and `#nav-lifecycle` is inside `#nav-group-trading`.
3. After clicking `#nav-facilities-position`: `#page-title` is `Gas day position`, `#page-section` is `SCHEDULING · GAS`, and `#facilities-workspace-tabs` is not visible.
4. After clicking `#nav-facilities-transport`: the visible tabs are `Nominations`, `Route plans`, `Capacity`.
5. After clicking `#scheduling-desk-crude`: `#page-title` is `Trade deliveries`, `#page-section` is `SCHEDULING · CRUDE`, the visible entries are `nav-physical-operations` and `nav-facilities-crude`, and every row of the Physical trades grid is a crude trade. Capture the directory request as `tests/browser/physical-crude.mjs` line 45 does and assert its body has `desk` equal to `crude`.
6. After clicking `#scheduling-desk-lng`: `#page-title` and the entry's text are `Cargoes`.

- [ ] **Step 2: Run the browser scenarios (browser, database)**

Run the physical and gas browser suites as `tools/check_all.py` runs them (groups `physical` and `gas-browser`), then the suites that hold `structured-power.mjs` and `futures-options.mjs`.
Expected: PASS. A scenario that fails because it clicks a tab through a real pointer that is now hidden should click the view's sidebar entry first; change the click, not the assertion.

- [ ] **Step 3: Commit**

```bash
git add tests/browser
git commit -s -m "Check the Scheduling desks in the running application"
```

---

### Task 7: Documents and the decision

**Files:**
- Modify: `docs/decisions.md`, `docs/plan.md`, `docs/physical-workspace.md`, `docs/delivery-workbench.md`, `docs/gas-facility-desk.md`, `docs/operating-facilities.md`, `docs/structured-power-trading.md`, `docs/frontend-wiring.md`
- Modify: both specs' `Status:` lines

- [ ] **Step 1: Add the decision**

Append `## D4NN — Scheduling lists one desk at a time` to `docs/decisions.md`, in the voice of D367 (line 3853). State, as fact and without history:

- Scheduling shows the Gas, Power, Crude or LNG desk's entries; the list of entries per desk.
- Lifecycle and Operating facilities are Trading pages.
- The entries are views of the existing Facilities and Physical operations pages; page ids, addresses, permissions, assistant page names and every write are unchanged.
- `physical/operations/directory` accepts an optional `desk` of `gas`, `crude` or `lng`; no desk lists every physical trade; another value is refused with `position_desk`.
- This amends the page list of D367.
- What it does not do: the spec's step 2, the installation's enabled desks, a crude position across trades.

- [ ] **Step 2: Update the documents that describe the current interface**

Run: `git grep -n -E "Scheduling →|Scheduling group|Physical operations|Facilities →|Pipeline outlook" -- docs README.md ':!docs/decisions.md' ':!docs/superpowers'`

For each hit that describes where something is today, give the new place and name. Do not edit earlier entries in `docs/decisions.md`, and do not rewrite a sentence that records what an earlier decision did; add the new place beside it if a reader would otherwise be sent to the wrong screen. Add a `D4NN` line at the top of `docs/plan.md` in the style of the lines already there, and record in the plan the items this work leaves out.

- [ ] **Step 3: Mark the spec**

In `docs/superpowers/specs/2026-11-27-scheduling-workspace-design.md`, the status line becomes `Status: step 1 implemented (D4NN). Step 2 not implemented.`

- [ ] **Step 4: Commit**

```bash
git add docs README.md
git commit -s -m "Record the Scheduling desks (D4NN)"
```

---

### Task 8: Full checks and independent review

- [ ] **Step 1: Run everything (browser, database)**

Run: `python3 tools/check_all.py` with the tool paths it asks for.
Expected: PASS. Paste the summary.

- [ ] **Step 2: Independent adversarial review**

`AGENTS.md` requires an independent review for changes to authorization boundaries. Give a reviewer who did not write the code the diff and the "Review Focus" list, with this question: can any account reach a page, section or trade it could not reach before, and can any navigation move a section or desk around an unsaved-edit or pending-command guard? Resolve each finding with a failing test first. Record the review's outcome in `docs/verification.md` in the style of its latest entry.

- [ ] **Step 3: Stop**

Do not push and do not open a pull request. Report the commits, the check output and the review outcome to the owner.
