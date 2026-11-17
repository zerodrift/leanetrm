# New Trade Page and Grouped Instrument Chooser Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make New trade a sidebar entry above Blotter with instruments in named groups and tickets at full page width, without changing any booking operation, permission, API or stored data.

**Architecture:** Presentation-only change in `web/src`. New trade is a second presentation of the Blotter's page, in the idiom of Risk's sidebar entries (`risk-views.ts`): the app's `page` stays `'futures'`, the Blotter view (`futures-view.ts`) holds `desk` or `entry`, and in `entry` its one Details pane fills the area through the dock's existing maximize while the Blotter's own parts are hidden by style. One pure definition in `new-trade.ts` lists the groups and says what each window offers.

**Tech Stack:** TypeScript (DOM, no framework), `node:test`, headless-Chrome `*-browser.test.mjs` fixtures, the repository's live browser checks (`tests/browser`).

**Spec:** `docs/superpowers/specs/2026-11-16-new-trade-page-design.md`

## Global Constraints

- No backend, API, permission, migration or business-rule change. TypeScript stays presentation only; no amount, date, schedule or eligibility is computed in the browser.
- No new dependency. No backwards-compatibility shims: `newTradeProducts`, `comingSoon`, `soonInstruments` and `loadEntry` are removed, not aliased.
- One Details pane, one mode, one instance of each editor. No second controller and no second copy of a form.
- Kept identifiers: page reports `blotter_new_trade`, `blotter_new_future`, `blotter_new_swap` and every other `blotter_new_*`; element ids `new-trade-<product>`, `new-trade-product`, `new-trade-note`, `new-trade-availability`, `futures-new`, `position-details` and its parts; storage keys of the retained request.
- Group names and order, verbatim: **Financial**, **Power**, **Gas & LNG**, **Oil & liquids**, **Structured**. Sidebar label: **New trade**.
- No instrument is selected for the person. A group with nothing to show is not drawn.
- Inside a side and inside a form without sides: one field per row. Across the page: the sides, and the band of trade-level fields. Tab order equals visual order; nothing is reordered by style alone.
- A field the backend does not use is drawn Coming soon and is never sent.
- Examples stay synthetic; no customer, company or competing-product names in source, docs, fixtures or commit messages.
- Commits use `git commit -s` and end with the session's attribution trailer. Nothing is pushed or opened as a PR without the owner's explicit approval.
- Decision record: D421, renumbered if another decision lands first (D419 and D420 are on open pull requests).

## Commands

Prefix every command with `export LC_ALL=en_US.UTF-8 PATH="$HOME/.openetrm-tools/node-24.22.0/bin:$PATH"`. Never pkill a preview; stop only your own process.

- **Unit, one file:** `npm run build --prefix web && node --test web/tests/<file>.test.mjs`
- **Unit, all:** `npm test --prefix web`
- **Live browser checks:** these need the API and PostgreSQL. They run against the synthetic preview on the development host and in CI, not on a laptop. Where a step says "live", run it there and record the result; if the host is not reachable, say so in the task's notes and run it in Task 8.

## Review Focus

1. A person with entries in a ticket presses Blotter in the sidebar and answers Keep editing: New trade stays, with the entries, the title and the sidebar mark unchanged. (Task 4)
2. Choosing Future reads the whole Blotter: while it reads, the page says the contracts are loading instead of inviting another choice, and a failed read brings the chooser back with the failure said. (Task 4)
3. A booking's outcome is not known: the retained card shows above the ticket, the chooser is withheld with the reason, and Check status that finds it committed lands on the Blotter with the position open. (Tasks 4, 8)
4. The As of date or the account changes while New trade is shown: the page shows the chooser or the reason booking is unavailable, never an empty pane or the Blotter's "Select a position" line. (Tasks 3, 4)
5. A saved Blotter view is applied, or the window becomes narrow, while New trade is shown: the pane still fills the area. (Task 4)

---

### Task 1: Instrument groups

**Files:**
- Modify: `web/src/new-trade.ts:8-9,44-66` (replace `newTradeProducts`, `comingSoon`, `soonInstruments`)
- Modify: `web/src/blotter-channel.ts:9,52`, `web/src/futures-view.ts:341,660` (read names from the definition)
- Test: `web/tests/new-trade.test.mjs`

**Interfaces:**
- Produces:
  ```ts
  export type InstrumentEntry=
    |{kind:'product';product:NewProduct;name:string}
    |{kind:'link';id:'operating-right';name:string;about:string;page:'physical-power'}
    |{kind:'soon';id:'storage';name:string;about:string};
  export type InstrumentGroupId='financial'|'power'|'gas-lng'|'oil-liquids'|'structured';
  export type InstrumentGroup={id:InstrumentGroupId;name:string;entries:readonly InstrumentEntry[]};
  export const instrumentGroups:readonly InstrumentGroup[];
  /** Every product once, in the chooser's order. */
  export const newProducts:readonly NewProduct[];
  export function productName(product:NewProduct):string;
  /** The product's one-line description as a sentence. */
  export function productAbout(product:NewProduct):string;
  /** The groups a window draws: entries it cannot open or show are left out, and a group left empty is not returned. */
  export function chooserGroups(o:{opens:(product:NewProduct)=>boolean;links:(page:string)=>boolean;soon:boolean}):readonly InstrumentGroup[];
  ```
- Exact content of `instrumentGroups`, in order:

  | id | name | entries |
  | --- | --- | --- |
  | `financial` | Financial | `futures` Future, `options` Futures option, `swaps` Swap |
  | `power` | Power | `ppa` PPA, `rec` REC forward |
  | `gas-lng` | Gas & LNG | `gas` Physical gas, `park-loan` Park and loan, `lng` Physical LNG |
  | `oil-liquids` | Oil & liquids | `crude` Physical crude |
  | `structured` | Structured | link `operating-right` Operating right (about: `Thermal toll, battery or solar output right · entered on Operating facilities`, page `physical-power`); `transport` Transport; soon `storage` Storage (about: the hover text `comingSoon` holds today) |

- [ ] **Step 1: Write the failing tests** in `web/tests/new-trade.test.mjs`, replacing the test that reads `newTradeProducts`:

  ```js
  const main={opens:()=>true,links:()=>true,soon:true};
  const shape=groups=>groups.map(g=>[g.id,g.name,g.entries.map(e=>e.kind==='product'?e.product:e.id)]);

  test('the instruments are grouped in the chooser\'s order',()=>{
   assert.deepEqual(shape(chooserGroups(main)),[
    ['financial','Financial',['futures','options','swaps']],
    ['power','Power',['ppa','rec']],
    ['gas-lng','Gas & LNG',['gas','park-loan','lng']],
    ['oil-liquids','Oil & liquids',['crude']],
    ['structured','Structured',['operating-right','transport','storage']]]);
  });
  test('every product is listed once and has a name',()=>{
   assert.equal(new Set(newProducts).size,newProducts.length);assert.equal(newProducts.length,10);
   assert.equal(productName('park-loan'),'Park and loan');assert.equal(productName('gas'),'Physical gas');
   assert.equal(productAbout('futures'),'An exchange contract with a defined expiry.');
  });
  test('a window that opens only futures and swaps draws one group and no link or coming-soon entry',()=>{
   assert.deepEqual(shape(chooserGroups({opens:p=>p==='futures'||p==='swaps',links:()=>false,soon:false})),[['financial','Financial',['futures','swaps']]]);
  });
  test('an account that cannot open Operating facilities is not offered the operating right',()=>{
   const structured=chooserGroups({...main,links:()=>false}).find(g=>g.id==='structured');
   assert.deepEqual(structured.entries.map(e=>e.kind),['product','soon']);
  });
  test('the operating right names the page it is entered on and nothing else',()=>{
   const link=instrumentGroups.at(-1).entries[0];
   assert.deepEqual(link,{kind:'link',id:'operating-right',name:'Operating right',about:'Thermal toll, battery or solar output right · entered on Operating facilities',page:'physical-power'});
  });
  ```

- [ ] **Step 2: Run** `npm run build --prefix web && node --test web/tests/new-trade.test.mjs`. Expected: build fails or the tests fail on the missing exports.
- [ ] **Step 3: Implement** the exports above in `web/src/new-trade.ts`. `links` is asked with the entry's `page`. Remove `newTradeProducts`, `comingSoon` and `soonInstruments`; `blotter-channel.ts` validates with `newProducts`; the two notices in `futures-view.ts` read `productName` (the retained-request label keeps its `.replace(/^Physical/,'physical')`).
- [ ] **Step 4: Run** `npm test --prefix web`. Expected: PASS except `side-card-layout-browser.test.mjs`, whose fixture imports `newTradeProducts` (fixed in Task 2). If the build refuses because `setupNewTrade` still calls removed names, do Task 2's Step 3 before running and commit the two tasks together.
- [ ] **Step 5: Commit** `git commit -s -m "Group New trade's instruments in one definition"`

### Task 2: The chooser

**Files:**
- Modify: `web/src/new-trade.ts:68-101` (`setupNewTrade`)
- Modify: `web/tests/side-card-layout.fixture.mjs:9-14`
- Test: `web/tests/new-trade-chooser-browser.test.mjs` (create)

**Interfaces:**
- Consumes: Task 1's `InstrumentGroup`, `productAbout`.
- Produces:
  ```ts
  export function setupNewTrade(shell:SidePanel,o:{
    choose:(product:NewProduct)=>void;
    link:(page:string)=>void;
    groups:()=>readonly InstrumentGroup[];   // read on every show and sync
    offered:()=>boolean;                      // false: the chooser is not drawn
    busy:()=>boolean;
  }):{show(mode:NewMode,state:'choose'|'loading'):void;loading(product:NewProduct):void;sync():void};
  ```
- Markup contract (later tasks and the live checks rely on it):
  - `#new-trade-product`: `role="group"`, `aria-label="Instrument"`, `data-state="choose"` while `mode.product===null`, else `"chosen"`; `hidden` while `!o.offered()`.
  - Each group: `<section class="instrument-group" data-group="<id>">` with `<h3 class="instrument-group-name">` and `<div class="instrument-entries">`.
  - Product: `<button type="button" id="new-trade-<product>" class="instrument" aria-pressed>` holding `<span class="instrument-name">` and `<span class="instrument-about">`.
  - Link: `<button type="button" id="new-trade-operating-right" class="instrument instrument-link">` with the name, `<span class="instrument-mark" aria-hidden="true">↗</span>` and the about line; pressing it calls `o.link(entry.page)` and nothing else.
  - Coming soon: a disabled button, class `instrument ticket-soon`, `aria-disabled="true"`, `title` its about text, holding `<span class="ticket-soon-tag">Coming soon</span>`.
  - `#side-panel-title` and `#new-trade-note` as today. `loading(product)` sets the status line to that product's `loading` text without changing the mode; the next `show` replaces it.

- [ ] **Step 1: Write the failing browser test** `web/tests/new-trade-chooser-browser.test.mjs`, in the pattern of `web/tests/dock-maximize-browser.test.mjs` (`withBrowserPage`, skip when `!browserAvailable`). The page builds a stub shell `{element:name=>parts[name]}` over `head`, `body`, `view` divs, imports `/new-trade.js`, and calls `setupNewTrade` with `groups:()=>chooserGroups({opens:()=>true,links:()=>true,soon:true})`, recorders for `choose` and `link`, and `window.state={busy:false,offered:true}`. Assert:

  ```js
  // after show({kind:'new',product:null},'choose')
  assert.deepEqual(await evaluate(`[...document.querySelectorAll('.instrument-group-name')].map(n=>n.textContent)`),['Financial','Power','Gas & LNG','Oil & liquids','Structured']);
  assert.equal(await evaluate(`document.querySelector('#new-trade-product').dataset.state`),'choose');
  assert.equal(await evaluate(`document.querySelectorAll('#new-trade-product [aria-pressed=true]').length`),0);
  assert.equal(await evaluate(`document.querySelector('#new-trade-futures .instrument-about').textContent`),'An exchange contract with a defined expiry.');
  // pressing a product and the link
  assert.deepEqual(await evaluate(`(document.querySelector('#new-trade-swaps').click(),document.querySelector('#new-trade-operating-right').click(),[chosen,linked])`),[['swaps'],['physical-power']]);
  // Storage cannot be pressed
  assert.equal(await evaluate(`(b=>b.disabled&&b.title.startsWith('Will book a storage contract'))(document.querySelector('.ticket-soon'))`),true);
  // after show({kind:'new',product:'swaps'},'loading')
  assert.deepEqual(await evaluate(`[document.querySelector('#new-trade-product').dataset.state,[...document.querySelectorAll('[aria-pressed=true]')].map(b=>b.id)]`),['chosen',['new-trade-swaps']]);
  // loading(product) names what is being read
  assert.equal(await evaluate(`(entry.show({kind:'new',product:null},'choose'),entry.loading('futures'),document.querySelector('[role=status]').textContent)`),'Loading the futures contracts…');
  assert.equal(await evaluate(`(entry.show({kind:'new',product:null},'choose'),document.querySelector('[role=status]').textContent)`),'Choose an instrument type to enter a new trade.');
  // busy disables, not offered hides
  assert.equal(await evaluate(`(state.busy=true,entry.sync(),document.querySelector('#new-trade-ppa').disabled)`),true);
  assert.equal(await evaluate(`(state.offered=false,entry.sync(),document.querySelector('#new-trade-product').hidden)`),true);
  ```

- [ ] **Step 2: Run** `npm run build --prefix web && node --test web/tests/new-trade-chooser-browser.test.mjs`. Expected: FAIL (no `.instrument-group-name`).
- [ ] **Step 3: Implement** `setupNewTrade` to the markup contract. The groups are drawn again when `o.groups()` returns a different set of entry ids than the one drawn. Update `side-card-layout.fixture.mjs` to pass the new options and compare the pressed products with `newProducts`.
- [ ] **Step 4: Run** `npm test --prefix web`. Expected: PASS once Task 4's Step 3 passes the new options from `futures-view.ts`; until then pass them there with `groups:()=>chooserGroups({opens:()=>true,links:()=>false,soon:true})`, `offered:()=>true`, `link:()=>undefined` so the build is green, and note that Task 4 replaces these three lines.
- [ ] **Step 5: Commit** `git commit -s -m "Draw New trade's instruments in their groups"`

### Task 3: The Details pane rests on a mode

**Files:**
- Modify: `web/src/details-pane.ts:11,57-65`
- Modify: `web/src/futures-view.ts:326` (pass `rest`)
- Test: `web/tests/details-pane.test.mjs`

**Interfaces:**
- Produces: `setupDetailsPane(host,o)` takes one more option, `rest:()=>PanelMode`: the mode the pane shows when it holds nothing. `reset()` and the collapse (⌄, Escape, `collapse()`) put the pane at `o.rest()` instead of `null`; with `rest:()=>null` nothing changes from today. `o.collapsed()` is still called after a collapse.

- [ ] **Step 1: Write the failing tests** in `web/tests/details-pane.test.mjs`; give the file's `fixture` a `rest` it passes through (default `()=>null`):

  ```js
  const chooser={kind:'new',product:null};
  test('a pane that rests on the chooser returns to it when reset, and is never empty',async()=>{
   const f=fixture({rest:()=>chooser});
   await f.show({kind:'new',product:'swaps'});f.shell.reset();
   assert.deepEqual(f.shell.current(),chooser);
   assert.equal(f.host.querySelector('#position-details').hidden,false);
  });
  test('collapsing a ticket with edits asks first: kept edits stay, discarded ones return the pane to its rest',async()=>{
   const f=fixture({rest:()=>chooser});
   await f.show({kind:'new',product:'swaps'});f.edit(true);
   const kept=f.collapse();await settle();f.answer('Keep editing');await kept;
   assert.deepEqual(f.shell.current(),{kind:'new',product:'swaps'});
   const gone=f.collapse();await settle();f.answer('Discard changes');await gone;
   assert.deepEqual(f.shell.current(),chooser);
  });
  test('a pane whose rest becomes nothing closes on the next reset',async()=>{
   let rest=chooser;const f=fixture({rest:()=>rest});
   f.shell.reset();assert.deepEqual(f.shell.current(),chooser);
   rest=null;f.shell.reset();
   assert.equal(f.shell.current(),null);assert.equal(f.host.querySelector('#position-details').hidden,true);
  });
  ```
  The file's `fixture`, `answer`, `edit` and `settle` are used as they are.

- [ ] **Step 2: Run** `npm run build --prefix web && node --test web/tests/details-pane.test.mjs`. Expected: the three new tests FAIL.
- [ ] **Step 3: Implement** `rest` in `details-pane.ts`; `futures-view.ts` passes `rest:()=>null` for now.
- [ ] **Step 4: Run** `npm test --prefix web`. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -s -m "Let the Details pane rest on a mode of its host's choosing"`

### Task 4: The Blotter view's New trade presentation

**Files:**
- Modify: `web/src/futures-view.ts` — `setupFutures` options, `placeDock` (:230), `detailsPane` setup (:326), `syncProductPending` (:338), `setupNewTrade` call (:344), `modeChanged` (:348), `leavePage` (:429), `openNewTrade` (:607), `openFuture` (:617), `chooseProduct` (:643), `send` (:1474), the `futures-reconcile` handler (:1535), `presentationLocate` (:1727), the returned object (`loadEntry` :1710, `bookedExternal` :1814)
- Modify: `web/src/app.ts` — `setupFutures` call (:1456), the two `loadEntry` callers (:1406, :1667)
- Modify: `web/src/index.html:11-15` (sidebar entry), `web/src/navigation-groups.ts:11,34`
- Test: `web/tests/navigation-groups.test.mjs`, `tests/browser/new-trade-page.mjs` (create; live)

**Interfaces:**
- Consumes: Task 1 `chooserGroups`, Task 2 `setupNewTrade` options and `loading`, Task 3 `rest`.
- Produces, on the main variant of `options.window` (the pop-out's variant is unchanged):
  ```ts
  entry:{
    /** The title New trade shows, "New trade" or the instrument's (`modeLabel`); null when the Blotter itself is shown. */
    shown:(title:string|null)=>void;
    /** Whether this account's sidebar lists a page a chooser link goes to. */
    links:(page:string)=>boolean;
    /** Goes to that page through the ordinary page change and its leave question. */
    link:(page:string)=>void;
  }
  ```
- Produces, on the object `setupFutures` returns (replacing `loadEntry`):
  ```ts
  /** Whether New trade is shown in place of the Blotter. */
  entry():boolean;
  /** Shows New trade without reading: the page then says why booking is unavailable until a catalog is read. */
  showEntry():void;
  /** Shows New trade and makes sure a booking catalog is held: the one the Blotter read, or the directory alone. */
  enterEntry(signal?:AbortSignal):Promise<void>;
  ```
- Behaviour the view must have (main window; the pop-out behaves as today except for its chooser's groups):
  1. **State.** `presentation:'desk'|'entry'`, `desk` at start. One function changes it. In `entry`: `#futures-page` carries `data-entry`; the pane rests on `{kind:'new',product:null}`; the dock's Details pane is maximized (`dock.maximize('details')`), which is not part of the saved view. In `desk`: no attribute, rest `null`, `dock.restore()`. On every change the pane is settled to the new rest when it holds a mode the presentation does not show (a non-`new` mode in `entry`, a `new` mode in `desk`), and `entry.shown` hears the title.
  2. **Entering.** `enterEntry` refuses while the Blotter is popped out (`Bring the Blotter back before opening a ticket.`), shows `entry`, and when `catalog` is undefined reads `trading/directory` with the checks `loadEntry` had, without `clear()`, taking the result only if no catalog arrived meanwhile and the account and selection are those it was read for. It never reads a valuation.
  3. **Leaving.** `leavePage()` returns to `desk` first. `modeChanged` returns to `desk` when the pane comes to show a position, the risk report or a shocked contract while in `entry`. `send`, the Check status handler and `bookedExternal` return to `desk` before the read that follows a booking, so the person sees the Blotter read and the new position open, as today.
  4. **Toolbar New trade** (`#futures-new`) in the main window asks about the pane's edits through `asked` and then runs `enterEntry`; in the pop-out it opens the pane as today.
  5. **Reconcile** on a product ticket's retained card, pressed in `desk`, asks the same way, shows `entry` and opens that ticket with `chooseProduct(product,true,true)`.
  6. **Future.** `openFuture` calls `entry.loading('futures')` before its read, and `chooseProduct`'s failure path draws the chooser again (`entry.show(newTrade,'choose')`) when the pane still shows it, so a status left by `loading` never outlives the read.
  7. **Staying filled.** `placeDock` maximizes Details again when the presentation is `entry` and the dock is not maximized on it (a saved view applied through `dock.set` clears it).
  8. **Chooser.** `setupNewTrade` gets `groups` from `chooserGroups`: the main window `{opens:()=>true,links:options.window.entry.links,soon:true}`, the pop-out `{opens:p=>p==='futures'||p==='swaps',links:()=>false,soon:false}`; `offered:()=>bookingControl().enabled`; `link` from `options.window.entry.link`.
  9. **Close-out drafts are untouched.** `draftCloseOut` shows a `new` mode in the Details pane from a Blotter row; it stays there, in `desk`. The presentation changes only at the points above.
  10. **Walkthrough pointer.** `presentationLocate` for `open` / `new_future` returns `#futures-new` in `desk` and `#new-trade-futures` in `entry`.
- App wiring in this task: pass `entry` (`shown` sets `#page-title` and its `title` to the given text or `Blotter`; `links:page=>pageAvailable(page,permissions)`; `link:page=>navigate(page)()`); the Options page's book-a-series and `showTarget`'s three `blotter_new_*` reports call `setPage('futures')` then `enterEntry` in place of `loadEntry`; add `<button id="nav-new-trade" class="nav-item" aria-label="New trade" title="New trade" hidden>` above `#nav-futures` with a plus-in-a-document icon in the sidebar's 24-unit stroke style and `<span class="nav-text">New trade</span>`; Trading's pages become `['new-trade','futures','options']`; `pageAvailable('new-trade')` joins the `futures` case; `#nav-new-trade`'s click does nothing while `futures.entry()`, and otherwise runs through `leaving`: apply a changed context, `setPage('futures')`, then front the pop-out and stop if `futures.poppedOut()`, else `enterEntry()` when an As of date is chosen and `showEntry()` when none is.

- [ ] **Step 1: Write the failing unit tests** in `web/tests/navigation-groups.test.mjs`: change the Trading row of the order test to `['trading','Trading',['new-trade','futures','options']]`, and add

  ```js
  test('New trade is listed for the accounts the Blotter is listed for',()=>{
   assert.equal(pageGroup('new-trade'),'Trading');
   assert.equal(pageAvailable('new-trade',['ReadMarketData','ReadTrades']),true);
   for(const permissions of [[],['ReadMarketData'],['ReadTrades']])assert.equal(pageAvailable('new-trade',permissions),false);
   assert.equal(firstPage(['ReadMarketData','ReadTrades']),'futures');
  });
  ```

- [ ] **Step 2: Write the failing live check** `tests/browser/new-trade-page.mjs`, registered in `tests/browser/run.mjs` in the suite `futures.mjs` runs in, using `idle` from `owned-browser.mjs`. It asserts, in order:
  1. `#nav-new-trade` is shown above `#nav-futures`; pressing it gives `#page-title` "New trade", `#futures-page[data-entry]`, `#new-trade-product[data-state=choose]` with no pressed button, and `#desk-positions`, the Blotter toolbar and `#details-collapse` not visible (`checkVisibility()` false).
  2. Pressing `#new-trade-swaps` gives title "New swap" and `#swap-editing` inside `#position-details-form`.
  3. With a value typed into the swap form, pressing `#nav-futures` shows the pane's prompt; **Keep editing** leaves title "New swap" and the value; pressing again and **Discard changes** gives title "Blotter", no `data-entry`, `#desk-positions` visible and `#position-details` hidden.
  4. From the Blotter, `#futures-new` gives title "New trade" with the chooser.
  5. Pressing `#new-trade-futures` shows "Loading the futures contracts…" in the pane's status before the form, then title "New future" with `#futures-new-trade` in the form part.
  6. Booking that future ends on title "Blotter" with `#position-details` showing a position.
  7. Pressing `#new-trade-operating-right` from the chooser gives title "Operating facilities".
  8. On New trade, choosing another As of date leaves `#futures-page[data-entry]` with either `#new-trade-product` or `#new-trade-availability` rendered, and `#details-empty` not rendered.
  9. On New trade, with the window narrowed below the width at which the Blotter's panes stack, the chooser is still rendered and `#desk-positions` is not.

- [ ] **Step 3: Run** `npm run build --prefix web && node --test web/tests/navigation-groups.test.mjs` — expected FAIL on the Trading row. Run the live check on the development host — expected FAIL at `#nav-new-trade`.
- [ ] **Step 4: Implement** the behaviour and wiring above.
- [ ] **Step 5: Run** `npm test --prefix web` — expected PASS. Run `new-trade-page.mjs` live — expected PASS (styles for hiding the Blotter's parts land in Task 6; if assertion 1's visibility checks fail only for want of them, do Task 6's Step 3 first and commit the two together).
- [ ] **Step 6: Commit** `git commit -s -m "Show New trade as a page of its own over the Blotter's entry pane"`

### Task 5: Every other route lands on New trade

**Files:**
- Modify: `web/src/app.ts` — `showPage` cases `crude`, `gas`, `ppa` (:1544-1546), the sidebar marks (:1579-1580), the walkthrough's `go` pointer (:1786), `entry.shown`
- Modify: `web/src/assistant-pages.ts:106-107` (`pageNames`)
- Test: `web/tests/assistant-pages.test.mjs`; live: `tests/browser/new-trade-page.mjs`

**Interfaces:**
- Consumes: Task 4's `entry()`, `enterEntry`, `entry.shown`.
- Produces: no new names.

- [ ] **Step 1: Write the failing unit test** in `web/tests/assistant-pages.test.mjs`, using the file's existing way of reading a report's display name:

  ```js
  test('a trade being entered is named New trade, whichever instrument it is',()=>{
   const nameOf=page=>lookingAt({page,selection:{}},{mode:'current',frameId:null,frameLabel:null},{curve:code=>code,table:code=>code}).page;
   const entered=reportedPages.filter(page=>page.startsWith('blotter_new_'));
   assert.equal(entered.length,11);
   for(const page of entered)assert.equal(nameOf(page),'New trade',page);
  });
  ```

- [ ] **Step 2: Extend the live check** with: `showPage`-style entry from PPA bookings' route (the sidebar has none; use the walkthrough or assistant target the existing checks use for `blotter_new_swap`) lands on title "New swap" with `data-entry`; while New trade is shown `#nav-new-trade` has class `active` and `#nav-futures` has not, and the reverse on the Blotter.
- [ ] **Step 3: Run** both. Expected: FAIL (names read "Blotter"; `#nav-new-trade` never active).
- [ ] **Step 4: Implement.** `showPage('crude'|'gas'|'ppa')` becomes `setPage('futures')`, `futures.enterEntry()`, `futures.openProduct(...)`. `entry.shown` also marks the sidebar: `#nav-new-trade` active when a title is given, `#nav-futures` active only when none is and no saved view is shown (the saved-view marks at :1579 take `!futures.entry()` too). The `go` pointer for a `blotter_new_*` target is `#futures-new` when the Blotter itself is shown and `#nav-new-trade` otherwise; other `blotter*` targets keep `#nav-futures`. Every `blotter_new_*` key of `pageNames` reads `New trade`.
- [ ] **Step 5: Run** `npm test --prefix web` and the live check. Expected: PASS.
- [ ] **Step 6: Commit** `git commit -s -m "Route every ticket opening to the New trade page"`

### Task 6: The page's styles: the chooser and room for the ticket

**Files:**
- Modify: `web/src/app.css` — the chooser (`.product-switch` users at :1036-1041, `.ticket-soon` at :751), the Details form's one-column rules (:1806-1811), the entry presentation (new block beside the dock's maximize rules at :1801-1803)
- Modify: `web/src/futures-view.ts` (the form part's class follows the presentation)
- Test: `web/tests/new-trade-page-browser.test.mjs` (create), `web/tests/side-card-layout.fixture.mjs` and its `side-card-layout-browser.test.mjs`

**Interfaces:**
- Consumes: Task 2's markup contract, Task 4's `data-entry`.
- Produces: the pane's form part (`#position-details-form`) has class `details-form` in `desk` and `entry-form` in `entry`, never both. The one-column rules stay on `.details-form` unchanged, so the Details pane lays tickets out exactly as today.

Rules the styles must meet (the selectors are the builder's):
- In `#futures-page[data-entry]`, not rendered: the Blotter's toolbar and its menus, the lens bar, every dock group head, divider and grip, the pane's own bar (◂ ▸ title ⌄), the stale notes of the other panes. Rendered: `#futures-retained`, `#new-trade-availability`, the pane.
- `#new-trade-product[data-state=choose]`: groups as labelled blocks in a grid that fills the width, each instrument a tile with its name and description, at least 44 px tall. `[data-state=chosen]`: one compact strip, group name then small buttons, descriptions not rendered, the pressed one in the accent. Both wrap in a narrow container; neither scrolls sideways.
- Coming soon and the link keep the dashed and ↗ treatments; existing tokens only (`--surface`, `--border`, `--accent`, `--side-*`), no new colour.
- `.entry-form`: a ticket's sides sit next to each other from 600 px of container width (the existing `side-comparison` and `ticket-deck` rules do this once the one-column rules no longer apply), more sides than fit scroll sideways inside their deck, and `.trade-editor-actions` stays in view while the ticket scrolls.

- [ ] **Step 1: Write the failing browser tests.**

  `web/tests/new-trade-page-browser.test.mjs`: build `<section id="futures-page" data-entry>` holding a dock (`setupDock` with the four Blotter tabs, as `dock-maximize-browser.test.mjs` does), `setupDetailsPane` in its Details body with `rest:()=>({kind:'new',product:null})`, and `setupNewTrade` over its shell; maximize Details. For each host width 1280, 1440, 1920 and 400 px:

  ```js
  // nothing chosen
  assert.equal(await evaluate(`document.querySelectorAll('.instrument-group').length`),5);
  assert.equal(await evaluate(`[...document.querySelectorAll('.dock-bar, .dock-tab, .details-bar')].every(n=>!n.checkVisibility())`),true);
  assert.equal(await evaluate(`document.documentElement.scrollWidth<=document.documentElement.clientWidth`),true);
  assert.equal(await evaluate(`[...document.querySelectorAll('.instrument:not(.ticket-soon)')].every(b=>b.getBoundingClientRect().height>=44)`),true);
  // at 1280 and wider the five groups share rows; at 400 they stack
  assert.equal(await evaluate(`new Set([...document.querySelectorAll('.instrument-group')].map(g=>Math.round(g.getBoundingClientRect().top))).size`)<5,width>=1280);
  // an instrument chosen: one strip, far shorter than the tiles were
  assert.equal(await evaluate(`(entry.show({kind:'new',product:'swaps'},'loading'),document.querySelector('#new-trade-product').getBoundingClientRect().height)`)<(width>=1280?80:260),true);
  assert.equal(await evaluate(`document.querySelector('.instrument-about').checkVisibility()`),false);
  ```

  In `side-card-layout.fixture.mjs`, add checks that the same native sided ticket, hosted in a `.entry-form` host of 1280, 1440 and 1920 px, has its side cards on one row (equal tops, increasing lefts, aligned common rows) and no card narrower than 260 px, that in a 560 px `.entry-form` host the cards stack, and that in a `.details-form` host of 1280 px the layout is the one the fixture already asserts for the Details pane.

- [ ] **Step 2: Run** `npm run build --prefix web && node --test web/tests/new-trade-page-browser.test.mjs web/tests/side-card-layout-browser.test.mjs`. Expected: FAIL.
- [ ] **Step 3: Implement** the styles and the form part's class.
- [ ] **Step 4: Run** `npm test --prefix web`. Expected: PASS, `trading-layout-browser.test.mjs` and `deal-ticket-browser.test.mjs` included.
- [ ] **Step 5: Commit** `git commit -s -m "Give New trade's chooser and tickets the page's width"`

### Task 7: Each ticket at page width

**Files:**
- Modify as each ticket needs: `web/src/app.css`; `web/src/futures-booking.ts`, `web/src/swap-editor.ts`, `web/src/scheduled-swap-editor.ts`, `web/src/futures-option-view.ts`, `web/src/ppa-editor.ts`, `web/src/rec-forward-view.ts`, `web/src/gas-trade-view.ts`, `web/src/park-loan-trade-view.ts`, `web/src/lng-trade-view.ts`, `web/src/crude-trade-view.ts`, `web/src/transport-trade-view.ts`
- Test: `web/tests/side-card-layout.fixture.mjs`, `web/tests/new-trade-page-browser.test.mjs`, and the browser test each editor already has

**Interfaces:**
- Consumes: Task 6's `.entry-form`.
- Produces: no new names. Each editor keeps the order of its own parts; where a part must move for the page to read well it moves in the markup, for both hosts, and its checks move with it.

What every ticket shows under `.entry-form`, in the order its editor already has:
- a title row: the instrument's name and its one-line description (the pane's head already draws both);
- the trade-level fields (trade date, book, counterparty and the product's others) several across in one band, each at least 180 px wide, wrapping; one field per row is kept below 600 px;
- the sides next to each other, one field per row inside each;
- the schedule and the preview at the full width of the ticket;
- Future, Futures option and REC forward, which have no sides: the form at one field per row no wider than 560 px, with the contract's or certificate's read-only facts, mark and price preview beside it where the editor has them as a separate part, and below it under 900 px of container width.

- [ ] **Step 1: Write the failing layout checks.** For each of the ten products that the fixtures can build without a backend (`side-layout-fixtures.mjs` lists them), add to the browser fixture, in a 1440 px `.entry-form` host: the trade band has at least two fields on its first row; no field inside a side shares a row with another; the sideless forms' field column is at most 560 px wide and their facts part starts to its right; nothing overflows the host sideways. For a product no fixture builds, note it for Step 4.
- [ ] **Step 2: Run** the two browser tests. Expected: FAIL on the trade band and the sideless forms.
- [ ] **Step 3: Implement**, product by product, the smallest style and markup change that meets the rules. Do not add a wrapper that changes tab order.
- [ ] **Step 4: Look at each ticket on the running preview** (development host), at 1440 px and at 1024 px, and in the Blotter's Details pane for an amendment or close-out that shares the editor: Future, Futures option, Swap (ordinary, scheduled, heat rate), PPA, REC forward, Physical gas, Park and loan, Physical LNG, Physical crude, Transport. Fix what reads badly within the rules; record what was looked at in the task's notes.
- [ ] **Step 5: Run** `npm test --prefix web`. Expected: PASS.
- [ ] **Step 6: Commit** `git commit -s -m "Lay each new-trade ticket out for the page's width"`

### Task 8: Live checks open New trade as a page

**Files:**
- Modify: `tests/browser/new-trade.mjs`
- Modify: the live checks that assume a new trade sits beside the positions. Find them with `grep -rlE "details-collapse|closeNewTrade|startNewTrade|openPpaBookings|product-soon|#futures-new" tests/browser` and, for each match, decide from the lines around it whether it concerns a new trade (change) or a position, amendment, close-out or risk report (leave).
- Test: the suites those files belong to, live.

**Interfaces:**
- Produces, in `tests/browser/new-trade.mjs` (same exports, same signatures):
  - `startNewTrade` presses `#futures-new` when it is rendered and `#nav-new-trade` otherwise, and waits for `#futures-page[data-entry]`, `#position-details[data-mode=new]` and `#new-trade-product`.
  - `chooseProduct` is unchanged but for `transport` and `park-loan` and `rec` joining `forms` with their form roots and heads (`New Transport agreement`, `New Park and loan`, `New REC forward`).
  - `openPpaBookings` waits for title `New PPA` in place of `Blotter`.
  - `closeNewTrade` leaves the page: presses `#nav-futures`, answers the pane's prompt **Discard changes** when it is shown, and waits for title `Blotter` and `#position-details` hidden.

- [ ] **Step 1: Change the helper** and each affected check. A check that read the Blotter's rows while a new ticket was open now reads them after the ticket is booked or left.
- [ ] **Step 2: Run the affected suites live**, `new-trade-page.mjs` among them. Expected: PASS. A failure that shows the page behaving against the spec is fixed in the page, with its own failing check first, not in the check.
- [ ] **Step 3: Add to `new-trade-page.mjs`** the retained-request path, using the fault the existing retained-request checks use to lose a booking's reply: on New trade, the card `#futures-pending` shows above the ticket, `#new-trade-product` is hidden and `#new-trade-availability` reads `A trade request is retained. Reconcile or retry it before creating another.`; Check status ends on title `Blotter` with the position open. And the pop-out: its New trade opens in its Details pane and `#new-trade-product` holds exactly `#new-trade-futures` and `#new-trade-swaps`.
- [ ] **Step 4: Run live.** Expected: PASS.
- [ ] **Step 5: Commit** `git commit -s -m "Open New trade as a page in the live browser checks"`

### Task 9: Documents and the decision record

**Files:**
- Modify: `docs/decisions.md` (D421), `docs/blotter-workbench.md`, `docs/deal-ticket.md`, `docs/frontend-wiring.md`, `docs/superpowers/specs/2026-11-09-navigation-and-curve-workbench-design.md` (its navigation table), `docs/plan.md`, `docs/superpowers/specs/2026-11-16-new-trade-page-design.md` (status line)

- [ ] **Step 1: Write D421** in the format of D365: the requirement (entry outgrew the Details pane), the decision (New trade is a sidebar entry and a presentation of the Blotter's page; instruments in five groups from one definition; tickets at page width; after booking the person lands on the Blotter), what it supersedes (the lines of `blotter-workbench.md` and `deal-ticket.md` that place new-trade forms in the Details pane; D221's placement of New trade, not its rules), and what is left (staying on the page after booking, once receipts carry the trade reference; a common part order for every ticket, with the description-driven ticket).
- [ ] **Step 2: Update the other documents** to say what is now true: where New trade is, what the Details pane still holds, the sidebar's Trading entries, and in `docs/plan.md` the completed item and the two left. The spec's status line becomes "implemented by D421".
- [ ] **Step 3: Check** `grep -rn "New trade" docs/*.md | grep -i "details pane\|side panel"` returns no line that still places new-trade entry there.
- [ ] **Step 4: Commit** `git commit -s -m "Record the New trade page (D421)"`

### Task 10: Whole-branch verification and review

- [ ] **Step 1: Run** `npm test --prefix web`. Expected: PASS, with the count of tests run.
- [ ] **Step 2: Run the live suites** that include a changed check, on the development host. Expected: PASS.
- [ ] **Step 3: Read the whole diff** against `origin/master` for leftovers: a name that no longer has a user, a style no element matches, a comment describing the old placement, anything not synthetic.
- [ ] **Step 4: One independent adversarial review** of the branch, given the spec, this plan's Review Focus and the diff, and asked in particular about: the unsaved-edit question on every way out of a ticket; the retained request from New trade, the Blotter and the pop-out; the Blotter's read and risk run across a visit to New trade; a read that completes after the presentation changed. Resolve every finding, each with a failing check first, and run Steps 1 and 2 again.
- [ ] **Step 5: Report** to the owner what was run and what it showed. Nothing is pushed without their approval.
