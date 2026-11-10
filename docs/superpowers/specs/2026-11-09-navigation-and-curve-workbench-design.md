# Navigation, Reference Data console and curve workbench

Status: proposed design, approved direction, not implemented.

## Intent

Setup screens are scattered. Curve definitions, Curve builder and Price setup are
buried inside Curves & prices. Power TOUs sits alone in Settings while the calendars
and market clocks it depends on are two tab levels down in Reference data. Gas and
crude delivery setup are inside an Operations page. Pages carry stacked headers,
repeated titles, placeholder tiles and empty cards.

The result should read as a trading system with a clear place for each kind of work:
markets and risk are used daily, setup lives together, and infrastructure is out of
the way.

This changes where screens live and how they look. It changes no calculation,
permission, API, stored data or financial behavior.

## Goals

1. Every setup screen is reachable from one Reference Data console.
2. Risk is a top-level section with one entry per function.
3. Curve builder shows the graph and its prices on one screen.
4. Every page has one single-row header and one title.
5. Placeholder and empty content stops taking up space.

## Non-goals

- New backend operations, permissions, tables or migrations.
- Changes to pricing, risk, schedules, units or any other business rule.
- A redesign of the Blotter or Dashboard beyond the shared header.
- Automatic preview. Curve builder's Preview still runs only when asked.

## Anti-goals

These would make the work a failure even if it looked better:

- A page reachable today becomes unreachable, or reachable by an account that could
  not reach it before.
- A leave guard, unsaved-edit question or retained-request recovery is lost in a move.
- A stale preview can be mistaken for, or used as, a current result.
- A historical view changes what it reads.

## Sidebar

Groups, in order. `navigationGroups` in `web/src/navigation-groups.ts` and the
`nav-group-*` markup in `web/src/index.html` change together.

| Group | Entries (page id) |
| --- | --- |
| Overview | Dashboard (`dashboard`) |
| Trading | Blotter (`futures`) and saved views |
| Markets | Curves & prices (`market`), Delivery prices (`delivery`), Interval data (`interval-series`), Vol surfaces (`vol-surfaces`) |
| Risk | Reports (`risk`), Scenarios & Greeks (`risk-scenarios`), VaR / ES (`risk-var`), Run history (`risk-runs`) |
| Operations | Physical operations (`physical-operations`), Facilities (`facilities`) |
| Reference Data | Reference Data (`reference`) |
| Infrastructure | Jobs (`jobs`), Users & access (`admin`), AI (`ai`) |

Settings and Admin no longer exist as groups. The group label is the eyebrow above a
page title, so moved pages show their new group. "Settings ›" wording in interface
text and docs becomes the new location.

`opened` pages: `ppa`, `gas`, `crude` stay in Trading; `interval-curves` stays in
Markets; every console page belongs to Reference Data.

Existing page ids are kept so `webPages` in `assistant-pages.ts`, the assistant's
navigation targets, page memory and permissions keep working. New ids are
`risk-scenarios`, `risk-var`, `risk-runs` (views of `risk`, below) and
`curve-definitions` (a page).

`nav-physical` and `physical-workspace.ts` go away: with Delivery setup moved, the
only destination left is Facilities, listed directly and available under its existing
`facilities` permission rule. `page-memory.ts` already treats an unknown remembered
page as nothing remembered.

## Reference Data console

A new presentation module, `web/src/reference-console.ts`, in the idiom of
`curve-workspace.ts`: navigation owns no reads, and the existing page controller
applies leave, context and authority guards before showing a page.

### Menu

One ordered definition drives the menu. Each entry names a page, or the `reference`
page with a table.

| Group | Entries |
| --- | --- |
| Curves & pricing | Curve definitions (`curve-definitions`), Curve builder (`custom-curves`), Price setup (`configuration`), Price indexes, Interest rates, Data sources |
| Time | Calendars, Calendar dates, Market clocks, Schedules & date mappings (`schedules`), Power TOUs (`power`) |
| Products | Futures, Exchanges, Clearing houses, Price conventions, Settlement rules |
| Commodities & units | Commodities, Units, Unit conversions, Currencies, Measurement bases, Quantity definitions, Numeric input types |
| Physical | Delivery setup (`gas-setup` / `crude-setup`), Locations, Location types, Delivery patterns |
| Entities | Entities & counterparties (`parties`) |

Twenty-eight entries: seven pages and the 21 tables of `referenceGroups`. Table
entries keep their existing names and consumer text; `referenceGroups` is regrouped
to match or replaced by this definition, with no second list of the same tables.

- Only the group holding the current entry is open. Other groups can be opened by hand.
- An entry shows only if `pageAvailable` or `dataSections` allows it; a group with no
  entries is hidden. The sidebar entry keeps the `reference` page's rule, which every
  console entry also requires.
- Opening Reference Data returns to the last entry used this visit, else the first
  available.
- `#reference…` addresses keep their format and open the console at that table or item.
- Moving between entries goes through the same `navigate` path as the sidebar, so
  unsaved edits still ask before leaving.
- Each page keeps its own header context. Curve builder and Price setup keep the
  market close; Reference data and Schedules keep the business date (`usesBusinessDate`);
  `curve-definitions` keeps the market context it has today on `market`. The header
  controls may therefore change between console entries, and no entry reads under a
  context it did not read under before.

### Search

The search box at the top of the menu does two things. Typing filters the menu's
entry names in the browser. Submitting runs the existing cross-category reference
search and shows its hits in the main area, as today. The backend search covers
reference tables only, so the placeholder reads "Search reference data".

### Width

The menu is about 176px wide and independently scrollable. It folds to a slim strip
by its toggle, and on Curve builder it starts folded. Folding is this viewer's
presentation state.

### What moves

- **Curve definitions** becomes its own page, `curve-definitions`. Today it is a state
  of `market` (`definingCurve`, `#curve-definition`, `#curve-define`). The gear beside
  a curve (D361) and `openCurveConfiguration` navigate to this page and open the
  pinned definition. The leave and historical rules it has today are kept. The
  assistant's page report for it stays what it is today: the `curves` page with no
  curve selected, so the assistant's operations and targets do not change.
- **Curve builder** and **Price setup** leave the `curve-price-navigation` strip,
  which keeps Contract prices and Hourly / sub-hourly prices. `curve-workspace.ts`
  shrinks to those two.
- **Price setup** is the one name. The page title "Contract definitions" goes away.
  The assistant's existing `contract_definitions` identifier stays.
- **Delivery setup** keeps its Natural gas / Crude oil switch above the page.
  `physical-type-coverage` is removed, including its working Pipelines and Crude
  delivery sites tiles, which the switch duplicates.
- **Reference data's** group tabs and category tabs are replaced by the menu.
  `setupReferenceNavigation` keeps the search and item addressing it still needs.

## Risk

`risk` remains one section, one workspace and one value of the app's `page`. The four
sidebar entries are views of it: `navigationGroups` lists their ids and `pageAvailable`
gives them `risk`'s rule, while the controller maps each to `page==='risk'` plus a view
held by the Risk workspace. The existing `page==='risk'` guards, assistant reports and
page memory stay as they are, and a remembered Risk page reopens on Reports. Moving
between views neither reloads nor discards anything and asks no leave question.

| Entry | Shows today's |
| --- | --- |
| Reports | `risk-workspace-results`, Export report, Refresh Risk |
| Scenarios & Greeks | `risk-workspace-editor` (run setup and job status) |
| VaR / ES | `risk-history-panel` (setup and price coverage) |
| Run history | Reports and runs library |

- Panels now always open in their view rather than as collapsing `details`.
- The portfolio, book and trade scope becomes a compact bar shared by all four views,
  showing the current choice and opening the existing controls on demand. The scope
  rules in `docs/risk-workspace.md` are unchanged: an empty parent selection means all
  readable books, and an empty individual selection still disables Run.
- `#risk…` report addresses open Reports.
- Worker diagnostics leaves Risk. Its Manage worker nodes button only navigated to
  Jobs, where the Infrastructure card already lives.

## Curve builder workbench

Layout, top to bottom, built on the existing dock (`dock.ts`, `dock-model.ts`):

1. **App header**, the only header. The title slot shows the open curve's name with a
   picker, its kind and state chip. With no curve open it shows "Curve builder".
2. **Tab row**: Build, Details, Versions, Lineage, History, with Preview, Save draft,
   Publish at its right end. Reset layout moves to a small overflow menu.
3. **Work area**: default arrangement is a split, the tabbed group above Preview. The
   current Graph content keeps its board, modes and inspector.

Changes in detail:

- `Graph` is labeled **Build** and `Configuration` is labeled **Details**. `Outputs` is
  labeled **Preview** and is its own group below by default. The tab ids may stay;
  the arrangement key is bumped so older saved arrangements open the new default,
  with no migration of them.
- The dock's dragging and the below-960px single group behave as today.
- **Saved curves** moves from the left rail into the picker popover, along with New
  curve, Reload and Ask the assistant. The popover is fully keyboard operable, like
  `context-menu.ts`. The rail and its collapse toggle are removed. With no curve open,
  the Start a curve card stays.
- **Details** holds code, name, kind and template, and the ingredients list.
- **Combine curves** moves to Build's side column as a disclosure beneath Add an
  operation. "Build or discard the ingredient changes" still opens it.
- Graph cards lose their large filled buttons in favor of secondary styling. Behavior
  and the card and arrow menus are unchanged.

### Out-of-date preview

Today an edit clears the preview. Instead, Preview keeps the last chart and tiles
visually subdued under the label "Out of date: your edits aren't included" with its
Preview button. Implementation rule: the retained display is pixels only. `lastPreview`,
Lineage and History clear exactly as they do today, so nothing can read or act on the
stale result. The retained display clears entirely on `snapshotChanged`,
`contextChanged`, `liveRefresh`, `refresh`, opening another curve and leaving the page,
since those values would then answer a different question or lose their formatting.

## Shared header and cleanup

- **Header**: one row. The title does not wrap; it truncates with its full text
  available. A page does not repeat its title in a first card.
- **Status line**: `pageStatus()`'s descriptions go away. `#status` remains as a
  visually hidden live region for loading and failure announcements, and "Please check
  the message above." is no longer visible beside the error it describes.
- **Facilities**: help text trimmed to what a person needs to act.
- **Jobs**: sections with nothing to show are hidden, and paging controls appear only
  when there is more than one page.
- Blotter, Dashboard and Power TOUs change only through the shared header.

## Delivery order

Four independently reviewable changes, in order. Each leaves the application working.

1. Sidebar groups, Reference Data console, Curve definitions page.
2. Risk section and views.
3. Curve builder workbench.
4. Shared header and cleanup.

## Verification

- Failing behavior checks first, per `AGENTS.md`. `web/tests/navigation-groups.test.mjs`
  defines the new groups before they exist.
- A permissions table test: for each demo role, the set of reachable pages before and
  after is identical, apart from the renamed and removed entries above.
- Browser checks for leaving a console entry with unsaved edits, deep links
  (`#reference…`, `#risk…`, the D361 gear), and the out-of-date preview.
- `tests/browser/*` and `tools/check_*.py` that name old labels or tabs are updated to
  the new ones without weakening what they assert.
- Screens checked at 1280 and 1440 wide, and on the below-960px layout.
- `tools/check_all.py` passes. One combined independent review at the end, covering
  navigation, authorization of reachable pages and historical views.

## Documentation

Add decision D365 to `docs/decisions.md`, renumbering if another decision lands first.
Update `docs/markets-desk.md`, `docs/risk-workspace.md`, `docs/curve-definitions.md`,
`docs/physical-workspace.md` and the "Settings ›" references elsewhere. Record the
planned facility and transport workflows from the removed tiles in `docs/plan.md`, so
removing them from the interface loses nothing.

## Choices made on the owner's behalf

The owner chose the sidebar shape, one console, Risk breakouts and the workbench. These
were proposed and not objected to, and can be revisited: the Risk entry names; the six
console groups; "Price setup" as the single name; Details and Build as tab names;
Combine curves' new place; removing the "Planned · TBD" tiles; Facilities as the
Operations entry's name; Worker diagnostics leaving Risk.

Builder's calls: the console's folded state on Curve builder, actions sitting on the
tab row, the menu search behavior, and the out-of-date preview's handling of a context
change.

## Left to the builder

Exact spacing and truncation, the popover's construction, how `referenceGroups` is
merged with the menu definition, and which interface strings need rewording once their
location changes.
