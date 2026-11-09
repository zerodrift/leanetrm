# Navigation, Reference Data Console and Curve Workbench Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give setup one Reference Data console, give Risk its own section, and put Curve builder's graph and prices on one screen, without changing any calculation, permission, API or stored data.

**Architecture:** Presentation-only change in `web/src`. Page ids and their guards stay; a pure menu definition drives a console shell in the idiom of `curve-workspace.ts`; Risk's entries are views of the one `risk` page; Curve builder reuses the existing dock with a new default arrangement.

**Tech Stack:** TypeScript (DOM, no framework), `node:test`, the repository's browser checks.

**Spec:** `docs/superpowers/specs/2026-11-09-navigation-and-curve-workbench-design.md`

## Global Constraints

- No backend, API, permission, migration or business-rule change. TypeScript stays presentation only.
- No new dependency.
- Existing page ids stay: `market`, `custom-curves`, `configuration`, `schedules`, `power`, `reference`, `parties`, `gas-setup`, `crude-setup`, `facilities`, `risk`, `jobs`, `admin`, `ai`.
- `#reference…` and `#risk…` addresses keep their formats.
- No backwards-compatibility shims for removed presentation state.
- Examples stay synthetic; no customer, company or competing-product names in source, docs or commit messages.
- Tickets and forms keep one field per row.
- Commits use `git commit -s` and end with the session's attribution trailer. Nothing is pushed or opened as a PR without the owner's explicit approval.
- Decision record: D365, renumbered if another decision lands first.

## Commands

Prefix every command with `export LC_ALL=en_US.UTF-8 PATH="$HOME/.openetrm-tools/node-24.22.0/bin:$PATH"`. Never pkill a preview; stop only your own process.

- **Unit, one file:** `npm run build --prefix web && node --test web/tests/<file>.test.mjs`
- **Unit, all:** `npm test --prefix web`
- **App browser checks:** `python3 tools/run_preview.py --pg-bin /opt/homebrew/opt/postgresql@16/bin --dotnet "$HOME/.openetrm-tools/dotnet-10.0.402/dotnet" --browser-check --browser "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"`
- **Everything:** `python3 tools/check_all.py --pg-bin /opt/homebrew/opt/postgresql@16/bin --dotnet "$HOME/.openetrm-tools/dotnet-10.0.402/dotnet" --browser "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"`

## Review Focus

1. An account lacking `ReadTradeParties` opens the console: Entities is absent, no empty group shows, and nothing else disappears. (Task 1)
2. A person with unsaved edits picks another console entry and answers "stay": the page, the menu's highlighted entry and the edits are unchanged. (Task 2)
3. A `#reference…` link names a table the account cannot see: today's message appears and the console remains usable. (Task 2)
4. A browser holds a Curve builder arrangement saved by the old layout: the new default opens with no error. (Task 5)
5. A Risk run submission is awaiting confirmation while the person switches Risk views: it is retained and still reconcilable. (Task 4)

---

### Task 1: Console menu definition

**Files:**
- Create: `web/src/console-menu.ts`
- Modify: `web/src/data-sections.ts:6-38`
- Test: `web/tests/console-menu.test.mjs`, `web/tests/reference-navigation.test.mjs`

**Interfaces:**
- Produces:
  ```ts
  export type ConsoleEntry={id:string;name:string;page:string;table?:string;alsoPages?:readonly string[]};
  export type ConsoleGroup={id:string;name:string;entries:readonly ConsoleEntry[]};
  export function consoleMenu(o:{pageAvailable:(page:string)=>boolean;tables:readonly string[]}):readonly ConsoleGroup[];
  export function consoleEntryFor(menu:readonly ConsoleGroup[],page:string,table?:string):ConsoleEntry|undefined;
  ```
  An entry's `id` is its table value, or its page id; Delivery setup is `{id:'delivery-setup',name:'Delivery setup',page:'gas-setup',alsoPages:['crude-setup']}`.

- [ ] **Step 1: Write the failing tests** in `console-menu.test.mjs`:

  ```js
  const allTables=referenceGroups.flatMap(g=>g.sections.map(s=>s.value));
  const full=consoleMenu({pageAvailable:()=>true,tables:allTables});
  test('six groups in order, 28 entries',()=>{
   assert.deepEqual(full.map(g=>g.name),['Curves & pricing','Time','Products','Commodities & units','Physical','Entities']);
   assert.equal(full.flatMap(g=>g.entries).length,28);
  });
  test('setup that depends on time sits together',()=>{
   assert.deepEqual(full[1].entries.map(e=>e.name),['Calendars','Calendar dates','Market clocks','Schedules & date mappings','Power TOUs']);
  });
  test('curve setup leads with its editors',()=>{
   assert.deepEqual(full[0].entries.map(e=>e.page).slice(0,3),['curve-definitions','custom-curves','configuration']);
  });
  test('no table is listed twice or lost',()=>{
   const tables=full.flatMap(g=>g.entries).filter(e=>e.table).map(e=>e.table);
   assert.deepEqual([...tables].sort(),[...allTables].sort());
  });
  test('an unavailable page or table disappears and an emptied group is hidden',()=>{
   const menu=consoleMenu({pageAvailable:p=>p!=='parties',tables:allTables});
   assert.equal(menu.some(g=>g.name==='Entities'),false);
   assert.equal(menu.flatMap(g=>g.entries).length,27);
  });
  test('crude setup finds the Delivery setup entry',()=>{
   assert.equal(consoleEntryFor(full,'crude-setup').id,'delivery-setup');
   assert.equal(consoleEntryFor(full,'reference','locations').name,'Locations');
  });
  ```

- [ ] **Step 2: Run it.** Unit, `console-menu`. Expected: FAIL, module not found.
- [ ] **Step 3: Regroup `referenceGroups`** into `Curves & pricing` (`price_index_definitions`, `interest_rate_definitions`, `sources`), `Time` (`calendars`, `calendar_days`, `clock_rules`), `Products` (`futures_products`, `exchanges`, `clearing_houses`, `quote_specifications`, `settlement_targets`), `Commodities & units` (unchanged order), `Physical` (`locations`, `location_types`, `delivery_profiles`), keeping every section's `name` and `consumer`.
- [ ] **Step 4: Implement `console-menu.ts`.** Page entries are the only list here: `before`/`after` the group's tables. Curves & pricing before: Curve definitions, Curve builder, Price setup. Time after: Schedules & date mappings, Power TOUs. Physical before: Delivery setup. Entities: Entities & counterparties. Group `id`s: `curves`, `time`, `products`, `units`, `physical`, `entities`.
- [ ] **Step 5: Run** Unit, all. Expected: PASS, updating only `reference-navigation.test.mjs` expectations that name the old group order.
- [ ] **Step 6: Commit** `Define the Reference Data console menu`.

### Task 2: Sidebar groups and Reference Data console

**Files:**
- Create: `web/src/reference-console.ts`; Test: `web/tests/reference-console-browser.test.mjs`
- Modify: `web/src/navigation-groups.ts`, `web/src/index.html` (sidebar lines 8-36, `#curve-price-navigation`, `#physical-workspace`, reference page), `web/src/app.ts`, `web/src/curve-workspace.ts`, `web/src/reference-navigation.ts`, `web/src/app.css`
- Delete: `web/src/physical-workspace.ts`, `web/tests/physical-workspace-browser.test.mjs`
- Test: `web/tests/navigation-groups.test.mjs`, `web/tests/curve-workspace-browser.test.mjs`
- Docs: `docs/decisions.md` (D365), `docs/physical-workspace.md`, `docs/plan.md`, `docs/markets-desk.md`, and every "Settings ›" / "Settings →" reference in `docs/` and interface text

**Interfaces:**
- Consumes: Task 1's `consoleMenu`, `consoleEntryFor`.
- Produces:
  ```ts
  export function setupReferenceConsole(o:{host:HTMLElement;menu:()=>readonly ConsoleGroup[];open:(entry:ConsoleEntry)=>void;search:(text:string)=>void;busy:()=>boolean}):
   {sync():void;show(page:string,table?:string):void;destination():ConsoleEntry|null;reset():void};
  ```
  `#reference-console` wraps `#reference-console-menu` and `#reference-console-body`, which holds the console pages' existing sections.

- [ ] **Step 1: Write the failing group tests** in `navigation-groups.test.mjs`, replacing the old order test:

  ```js
  assert.deepEqual(navigationGroups.map(g=>[g.id,g.label,[...g.pages]]),[
   ['overview','Overview',['dashboard']],
   ['trading','Trading',['futures']],
   ['markets','Markets',['market','delivery','interval-series','vol-surfaces','interval-curves']],
   ['risk','Risk',['risk','risk-scenarios','risk-var','risk-runs']],
   ['operations','Operations',['physical-operations','facilities']],
   ['reference','Reference Data',['reference']],
   ['infrastructure','Infrastructure',['jobs','admin','ai']]]);
  for(const page of ['custom-curves','configuration','schedules','power','parties','gas-setup','crude-setup','curve-definitions'])assert.equal(pageGroup(page),'Reference Data');
  ```

  Add a reachability table: for `['ReadMarketData']`, `['ReadMarketData','ReadTrades']`, `['ReadMarketData','ReadTradeParties']`, `['ReadMarketData','ReadTrades','RunScenario']` and `['ManageAccess']`, assert the exact available page list, written out literally from today's `pageAvailable` rules with `physical` removed and `curve-definitions` added under `ReadMarketData`.

- [ ] **Step 2: Write the failing console browser tests**, using `withBrowserPage` as `curve-workspace-browser.test.mjs` does:
  - clicking an entry calls `open` once and does not mark it current until `show` is called (Review Focus 2);
  - only the group holding the current entry is open, and another can be opened by hand;
  - typing "tou" leaves only Power TOUs listed, and submitting calls `search('tou')`;
  - `destination()` returns the last shown entry, else the first available;
  - while `busy()` is true, entries are disabled;
  - the toggle folds the menu to a slim strip with `aria-expanded` false, and unfolds it.
- [ ] **Step 3: Run them.** Expected: FAIL.
- [ ] **Step 4: Implement** `reference-console.ts`, the groups, and `pageAvailable('curve-definitions')` under `ReadMarketData`. `pageAvailable` gives `risk-scenarios`, `risk-var`, `risk-runs` the `risk` rule and drops `physical`.
- [ ] **Step 5: Restructure `index.html`.** New sidebar per the spec; `nav-risk-scenarios`, `nav-risk-var`, `nav-risk-runs` are added but hidden until Task 4; `nav-reference` label is "Reference Data". Wrap console pages in `#reference-console`. Remove `physical-type-coverage` and `physical-workspace-nav`; keep the commodity switch above Delivery setup.
- [ ] **Step 6: Wire `app.ts`.** `setPage` shows `#reference-console` for console pages and calls `show`. `nav-reference` navigates to `destination()`. Entries open via `navigate(page)`, and table entries via the existing reference selection. `isPhysicalPage` uses become a local `gas-setup`/`crude-setup` check. Trim `curve-workspace.ts` views to `market` and `interval-curves`. In `setPage`, `configuration` is titled "Price setup", `facilities` "Facilities" and `jobs` "Jobs".
- [ ] **Step 7: Trim `setupReferenceNavigation`** to `{showHits,clear}`, dropping the group and section tabs. Keep `groupReferenceHits`. Hits follow menu order.
- [ ] **Step 8: Add a browser check** in `tests/browser/` for Review Focus 2 and 3: unsaved Schedules edit, choosing another entry and staying; and a `#reference/<unavailable>` address keeping the console usable with today's message.
- [ ] **Step 9: Update the tests and browser checks** naming old labels, without weakening assertions. Run Unit, all, then App browser checks. Expected: PASS.
- [ ] **Step 10: Write D365 and the docs,** including the planned workflow list in `docs/plan.md`.
- [ ] **Step 11: Commit** `Add the Reference Data console and regroup the sidebar (D365)`.

### Task 3: Curve definitions page

**Files:**
- Modify: `web/src/index.html` (`#curve-definition`, `#curve-define`), `web/src/app.ts:103,439-447,1112-1116`, `web/src/assistant-pages.ts`, `web/src/curve-definition-editor.ts:37,96,182-188`
- Test: `web/tests/assistant-pages.test.mjs`, `tests/browser/curve-definitions.mjs`
- Docs: `docs/curve-definitions.md`

**Interfaces:**
- Consumes: Task 2's console and `curve-definitions` page id.
- Produces: page id `curve-definitions`; `openCurveConfiguration(pinned)` navigates there.

- [ ] **Step 1: Write the failing tests.** In `assistant-pages.test.mjs`, a `curve-definitions` page reports `{page:'curves'}` with no curve selected, matching the `market` report while defining. In `tests/browser/curve-definitions.mjs`: the gear opens the definition on the `curve-definitions` page with `Reference Data` as eyebrow; Curves & prices no longer shows `#curve-define`; a historical view offers no gear; leaving with unsaved edits asks.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Move `#curve-definition`** into its own section in `#reference-console-body`. Replace `definingCurve` with the page. `showsDate` and close handling treat it as `market` does. Interface text naming its location is updated.
- [ ] **Step 4: Run** Unit, all and App browser checks. Expected: PASS.
- [ ] **Step 5: Update docs and commit** `Give Curve definitions its own page`.

### Task 4: Risk section and views

**Files:**
- Create: `web/src/risk-views.ts`; Test: `web/tests/risk-views-browser.test.mjs`
- Modify: `web/src/risk-workspace.ts`, `web/src/risk-scope.ts`, `web/src/index.html:405-420`, `web/src/app.ts`, `web/src/app.css`
- Test: `web/tests/navigation-groups.test.mjs`, `tests/browser/` risk suites
- Docs: `docs/risk-workspace.md`, `docs/risk-factor-history.md`

**Interfaces:**
- Produces:
  ```ts
  export const riskViews=['risk','risk-scenarios','risk-var','risk-runs'] as const;
  export type RiskView=typeof riskViews[number];
  export const isRiskView:(page:string)=>page is RiskView;
  export function setupRiskViews(o:{show:(view:RiskView)=>void;open:()=>void;busy:()=>boolean;available:()=>boolean}):{sync():void;select(view:RiskView):void;current():RiskView;reset():void};
  export function riskScopeSummary(scope:{portfolios:readonly string[];books:readonly string[];trades:number|null}):string;
  ```

- [ ] **Step 1: Write the failing tests.**
  - `riskScopeSummary({portfolios:[],books:[],trades:null})` is `'All portfolios · All readable books · All trades in these books'`; `{portfolios:['Example desk'],books:['Book A','Book B'],trades:3}` is `'Example desk · 2 books · 3 trades'`.
  - From `risk`, clicking `nav-risk-var` calls `show('risk-var')` and not `open`; from another page it calls `open` first.
  - After reload, `current()` is `risk`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement** views. `showView` hides or shows `risk-workspace-results`, `risk-workspace-editor`, `risk-history-panel` and the library, always open as plain sections. VaR / ES entry runs the old `risk-history-open` handler on first entry. Scenarios & Greeks keeps `risk-jobs-new`, labeled "New run". Export report and Refresh Risk live on Reports. Remove Worker diagnostics.
- [ ] **Step 4: Implement the scope bar:** a one-line `riskScopeSummary` and a Change control opening the existing selects in a disclosure. Scope rules and control ids are unchanged.
- [ ] **Step 5: Add a browser check** for Review Focus 5: with a retained unconfirmed submission, switching Risk views keeps it and its Reconcile path; and `#risk…` opens Reports.
- [ ] **Step 6: Run** Unit, all and App browser checks. Expected: PASS.
- [ ] **Step 7: Update docs and commit** `Break Risk into its own section with a view per function`.

### Task 5: Curve builder workbench layout

**Files:**
- Modify: `web/src/custom-curves-view.ts:74-83,201-211`, `web/src/dock.ts`, `web/src/app.ts`, `web/src/index.html` (header), `web/src/app.css`
- Test: `web/tests/dock-model.test.mjs`, `tests/browser/custom-curves.mjs`, `tests/browser/curve-objects.mjs`
- Docs: `docs/markets-desk.md`

**Interfaces:**
- Produces:
  ```ts
  export function workbenchLayout():DockLayout; // column split, ratio 0.62: group ['graph','configuration','versions','lineage','history'] active 'graph', above group ['outputs']
  ```
  `setupDock` gains `actions?:HTMLElement`, drawn at the end of the first group's tab strip. `#page-heading-slot` in the app header holds Curve builder's title while that page shows; `#custom-curves-heading` keeps its id.

- [ ] **Step 1: Write the failing tests.** `workbenchLayout()` deep-equals the structure above and is accepted by `parseArrangement`; `restoreArrangement` given an arrangement saved under the old single-group shape opens the default when read from the new key (Review Focus 4). Browser: after opening a template, `#custom-curves-tab-graph` and `#custom-curves-tab-outputs` are both visible; tab labels are `Build, Details, Versions, Lineage, History` and `Preview`; below 960px all six are in one group.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** Dock key becomes `openetrm.custom-curves.layout.v2`. Labels change; ids stay. Preview, Save draft and Publish move to the dock's `actions`; Reset layout to a small overflow menu built with `context-menu.ts`. Move the title, kind and state into `#page-heading-slot`. The console menu starts folded on this page.
- [ ] **Step 4: Run** Unit, all and App browser checks. Expected: PASS.
- [ ] **Step 5: Update docs and commit** `Show Curve builder's graph and prices on one screen`.

### Task 6: Saved-curve picker, Details and Combine curves

**Files:**
- Create: `web/src/curve-picker.ts`; Test: `web/tests/curve-picker-browser.test.mjs`
- Modify: `web/src/custom-curves-view.ts:92-107,118-135,250-252`, `web/src/app.css`
- Test: `tests/browser/custom-curves.mjs`, `tests/browser/curve-composition.mjs`
- Docs: `docs/markets-desk.md`

**Interfaces:**
- Consumes: Task 5's `#page-heading-slot`.
- Produces: `setupCurvePicker(o:{title:HTMLElement;panel:HTMLElement}):{close():void}`, a keyboard-operable popover: Enter or Down opens, Escape closes and returns focus, focus stays within while open.

- [ ] **Step 1: Write the failing tests** for those keys; and a browser check that opening a saved curve from the picker with unsaved edits still asks, and that `Build or discard the ingredient changes` reveals Combine curves in Build.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** The library, New curve, Reload and Ask the assistant move into the panel. Remove the rail and `custom-curves-toggle-rail`. `blend.element` moves to a disclosure beneath Add an operation; `showCombine` reveals `graph`, opens the disclosure and shows the side column. Graph cards' Connect and Contract terms take secondary styling.
- [ ] **Step 4: Run** Unit, all and App browser checks. Expected: PASS.
- [ ] **Step 5: Update docs and commit** `Move saved curves into a picker and tidy Details`.

### Task 7: Out-of-date preview

**Files:**
- Modify: `web/src/custom-curves-view.ts:133,170,215-216,654-656,688`, `web/src/app.css`
- Test: `tests/browser/custom-curves.mjs`
- Docs: `docs/markets-desk.md`

**Interfaces:**
- Produces: internal `markPreviewOutOfDate():void`, called from every edit path that now calls `clearPreview`: `invalidate`, formula compile and the blend change callback.

- [ ] **Step 1: Write the failing browser checks.** After Preview then an edit: the chart stays, `#custom-curves-preview-stale` reads `Out of date: your edits aren't included`, the values table and receipt are hidden, Lineage shows its empty note, and History is cleared. Then each of `snapshotChanged`, `contextChanged`, opening another curve, and a preferences change removes the retained chart and shows the empty state.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** `markPreviewOutOfDate` clears `lastPreview`, lineage, history, table and receipt exactly as `clearPreview` does, and leaves chart and tiles subdued. `refresh` clears a retained display.
- [ ] **Step 4: Run** App browser checks. Expected: PASS.
- [ ] **Step 5: Update docs and commit** `Keep an out-of-date preview visible and clearly labeled`.

### Task 8: Shared header and cleanup

**Files:**
- Modify: `web/src/index.html` (header, status bar, Jobs and Schedules headings), `web/src/app.ts:198,206-207,304,310`, `web/src/app.css`, Jobs render code, `web/src/gas-setup-editor.ts`, `web/src/crude-setup-editor.ts`
- Create: `web/tests/page-header-browser.test.mjs`
- Test: browser checks reading `#status`

**Interfaces:**
- Produces: `pagingNeeded(page:number,hasNext:boolean):boolean`, true when `page>0||hasNext`.

- [ ] **Step 1: Write the failing tests.** `pagingNeeded(0,false)` is false, `(1,false)` and `(0,true)` true. Browser at 1280 wide: `#page-title` is one line for "Schedules & date mappings" and "Jobs", with `title` holding the full text. `#status` exists, is visually hidden and still announces loading.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** Delete `pageStatus()` and its call; keep `#status` for loading and failure announcements; remove the "Please check the message above." text. Remove first-card titles repeating the page title on Schedules, Jobs and Risk. Jobs hides cards with nothing to show and uses `pagingNeeded`. In Facilities and Delivery setup, remove the "Using the header context" panel and help that only restates a control; keep text that states a rule or warns of a consequence.
- [ ] **Step 4: Run** Unit, all and App browser checks. Expected: PASS.
- [ ] **Step 5: Commit** `Use one header row and remove empty and repeated content`.

### Task 9: Final verification and review

**Files:** none planned; fixes as found.

- [ ] **Step 1: Run** Everything. Expected: all groups pass.
- [ ] **Step 2: Walk the app** in a preview at 1280 and 1440 wide and below 960px, as Viewer, Market Data Editor, Trader, Risk Analyst and Access Administrator. Each page the role had before is reachable.
- [ ] **Step 3: Request one independent adversarial review** of the whole branch, focused on page reachability by permission, leave guards, and historical views. Resolve findings and re-run Step 1.
- [ ] **Step 4: Inspect the complete diff, generated files and commit identity** per `AGENTS.md`. Ask the owner before pushing, and decide then whether the four delivery steps go up as separate PRs.
