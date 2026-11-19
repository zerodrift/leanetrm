# LeanETRM

**An open source ETRM for commodity trading, built for traders, risk managers and AI agents.**

LeanETRM books trades, builds curves and calculates P&L and risk on a standard,
unmodified PostgreSQL database. Exact quantity and cash arithmetic, explicit
numerical policies and retained source lineage make calculations auditable;
corrections never overwrite history. It is aimed at shops that need trade capture
and risk with explicit inputs and a retained calculation history.

> **Status:** a foundation, not a finished product. Try it as a disposable local
> preview on synthetic data. Nothing here is production-qualified.

For a worked PPA example, follow the [PPA tester guide](docs/experimental-ppa-testing.md)
for synthetic booking, graphical break-even pricing and settlement previews.

The mandatory [instrument contract](docs/instrument-contracts.md) requires every
financial product to derive from `Instrument`. Futures and exchange-traded options
are the only Side-free products; all others must compose concrete Sides that own
their schedules and PV. Independent signed memberships and trade quantities remain
separate. The current composition refactor and its qualification gates are
recorded in [the plan](docs/plan.md).

## What works today

- **Trade capture:** futures, listed American options on futures, financial gas and
  power swaps, factor swaps and PPAs with exact party references and retained
  history. **New trade** groups instruments by product family. From a supported
  position in the Blotter, review an amendment, close-out or quantity-adjustment
  draft, or run risk for that trade. See the [Blotter workbench](docs/blotter-workbench.md).
- **Physical gas:** fixed-quantity delivery with independent fixed or daily indexed
  consideration and fixed fee sides in the [deal ticket](docs/deal-ticket.md), explicit
  fuel links, multi-month booking and versioned delivery setup. Future compositions
  support amendments before operational/facility relationships exist. Contractual
  pre-delivery net MTM and interactive/queued scenario risk retain independent
  physical and payment forecasts. Started booked compositions retain remaining
  value, accrued unsettled cash and recorded payments under the explicit
  undiscounted policy.
  Manual nominations, acknowledgments, schedule adoption and imbalance reconciliation
  reuse retained daily quantities. Independent owned stock, operator claims/debts
  and commodity loans retain their holdings and surviving cash; commercial invoice
  issuance and broader physical settlement remain work. See
  [delivery operations](docs/gas-delivery-operations.md),
  [billing](docs/gas-cash-billing.md) and [owned holdings](docs/gas-owned-holdings.md)
- **Gas services.** Park and Loan supports title transfer with a fixed fee or a fee
  on the outstanding balance. Retained-title parking tracks custody and returns.
  A Transport agreement can value a whole package of rights from a published
  mark and track its fixed fee separately. See [Park and Loan](docs/gas-park-loan.md),
  [balance fees](docs/gas-park-loan-balance-fee.md), [custody](docs/gas-park-custody.md)
  and [Transport rights](docs/gas-transport-financial.md).
- **Gas storage.** Book capacity with inventory limits, daily injection and
  withdrawal ratchets, fuel and charges. Value a recorded plan before the term
  starts, or compare it with a plan proposed from daily forward prices. Recording
  the proposal makes it available for valuation. See the [gas storage contract](docs/gas-storage-contract.md).
- **Heat-rate call options.** Book a daily strip of calls on power against fuel
  at a contractual heat rate. Booked valuation uses both forwards and recorded
  volatility and correlation marks. Exercise determination and settlement remain
  open. See the [heat-rate call contract](docs/heat-rate-call-option.md).
- **Physical crude:** fixed standard-volume booking, contractual pre-delivery PV
  and interactive/queued risk, plus bounded receipts, title transfer, inventory-lot
  allocation, dispatch, invoices, payments and cash-record corrections. See
  [the contract](docs/physical-crude.md) and [operations](docs/physical-crude-operations.md)
- **Physical LNG:** future cargo booking with separate loading/discharge terms,
  explicit discharge-energy quantity or retained mass-to-energy conversion,
  Incoterms and fixed consideration. Retained trading views and interactive/queued
  risk support contractual PV before loading starts; cargo actuals and settlement
  remain work. See [the bounded cargo contract](docs/physical-lng.md)
- **Physical operations:** gas and crude workflows have a dedicated Operations
  workspace. Gas, crude and LNG share a read-only delivery grid with native
  intervals, quantities and source status; LNG operational actions remain TBD.
  The delivery workbench links chosen gas routes to booked delivery quantities and
  can prepare reviewed batches of manual nominations. See [physical operations](docs/physical-workspace.md),
  the [delivery grid](docs/physical-delivery-view.md) and the [workbench](docs/delivery-workbench.md).
- **Curves.** Use **Curves & prices** for contract prices and hourly or sub-hourly
  prices. Open **Curve builder** from Reference Data to author a curve. Futures,
  gas and power forwards, basis and interest rates are built from explicitly selected prices, with a diagram showing which inputs built each
  contract's price. The custom-curve workbench supports visual/formula recipes and
  explicit weighted contract baskets, reusable published curve outputs and guided
  combinations of computed curves with matching periods and units; all keep their
  upstream graph links; the swap editor selects compatible published
  outputs for monthly or exact daily projections. Effective-version timelines
  resolve under the selected business date and knowledge cutoff. Explicit delivery
  splices and whole-interval lookups retain selected source evidence; see
  [curve evaluation](docs/curve-evaluation.md). Selecting a curve loads its
  declared dependencies. The price result and first-use catalog are read separately.
  See [curve read coverage](docs/async-reads.md).
- **P&L and risk:** futures execution P&L and swap/PPA PV using pinned actuals
  and projections. [Trading P&L](docs/trading-pnl.md) compares two retained close
  contexts with attribution and an explicit Run then Save workflow; unsupported
  scope refuses rather than silently excluding holdings. Connected quote and
  computed-output scenarios calculate
  delta and gamma from shared scenario PV evaluations; generated-close jobs retain
  mixed futures-and-swaps reports. Interactive and queued portfolio risk also cover
  supported PPA, gas, crude and LNG bindings; generated gas, crude and LNG closes refuse.
  A delta ladder shows each shocked contract's totals by book and measure, see
  [the delta ladder](docs/delta-ladder.md). Native one-session delta-normal VaR/ES
  uses retained complete held-factor history, with signed contributions and
  correlation diagnostics; see [parametric risk](docs/parametric-risk.md)
- **Agent curve drafts:** authorized agents can read curve ingredients, prepare a
  pinned preview and propose a bounded custom-curve draft. Saving uses the ordinary
  authorized operation after explicit human approval; publishing remains a person’s action
- **Asynchronous reads:** selected curve, trading, Risk and option-strategy reads complete
  independently through correlated SSE responses, with per-view progress and
  cancellation. Commands and durable jobs retain their existing contracts;
  see [coverage and limits](docs/async-reads.md)
- **Futures margin:** the explicitly selected native-margin profile retains
  determinations, corrections, variation-margin cash, partial payments and remaining
  value; see [the bounded lifecycle](docs/lifecycle-delivery.md)
- **Live corrections:** push updates through the shared header context, with visible refresh/stale status; [policy and limits](docs/live-calculation.md)
- **Facility quantities:** shared reported quantities, explicit trade/Side allocation plans,
  versioned operating maxima and historical views for retained gas days; see
  [the implemented scope](docs/facility-quantity-ledger.md)
- **Risk jobs.** Queue mixed-book runs at the selected financial context, apply
  nested stresses, inspect retained results or cancel an active run. Jobs & Infrastructure can
  run one or two workers in a registered pool or drain their active jobs before
  stopping. See the [risk engine](docs/risk-engine.md) and [worker pool](docs/managed-risk-pool.md).
- **VaR / expected shortfall:** guided portfolio/book/trade setup,
  visible editable defaults, automatic end-of-day history alignment, official-close
  priority with explicit approved price-type alternatives, aligned-factor coverage
  grids and correlation/covariance graphics; see [how to run VaR](docs/risk-factor-history.md#guided-var-setup-d301)
  and [the parametric model](docs/parametric-risk.md). Full-PV
  [Monte Carlo](docs/monte-carlo-risk.md) adds explicit seeded correlated Gaussian
  shocks, full PV and convergence/sampling diagnostics. Optional
  [PCA / hybrid qualification](docs/pca-hybrid-risk.md) adds loadings, residual
  diagnostics and every-path candidate comparisons with user-selected fallback
  and cost. [Hybrid Monte Carlo valuation](docs/pca-hybrid-risk.md#hybrid-monte-carlo-valuation-d311)
  separately predicts the distribution with sampled full-PV validation and visible
  approximate/observed/fallback graphics. Fallback starts off and requires explicit
  opt-in. Full-PV
  [historical simulation](docs/historical-risk.md) adds empirical loss distributions,
  signed book/trade allocations, sample PV drilldown and a matching normal comparison
- **Scheduled swaps:** persistent booking, amendments and inspection of up to 120
  monthly periods per side, with explicit quantities, observations, payment rules
  and discounting; see [quantity contracts](docs/financial-swap-quantities.md)
- **Swap settlement:** opt-in immutable determinations, corrections, gross/net
  allocations, manual cash recording, reversals and a closing-context P&L bridge;
  see [the bounded lifecycle](docs/swap-settlement.md)
- **Blotter:** filters, pivots, saved views, charts, exports, docked trade details,
  pop-out, maximize/restore for every pane, shared portfolio/book selectors, personal
  sidebar pins and backend-prepared close-out drafts. Shared lazy, paged Side
  inspection exposes pricing, source/calculation detail and native lifecycle state
  for swaps, PPAs, gas, crude and LNG; unsupported values retain explicit reasons.
  See [the workbench](docs/blotter-workbench.md) and
  [Side inspection limits](docs/side-schedules.md#implementer-contract-d353)
- **Dashboard:** account-owned layouts, curve levels/history and positions from
  saved risk run definitions; see [implemented widgets and limits](docs/market-dashboard.md)
- **Interval series:** audited per-interval values on any market clock (price shapes,
  capacity-factor forecasts, metered energy) with CSV import, corrections, a change
  log and reads at a chosen knowledge time. Shaped interval curves consume price
  shapes; PPA quantity/valuation consumes forecast and meter series. PPA settlement
  has a separate read-only monthly gross preview, without cash posting; see
  [the implementation record](docs/implementation.md#audited-interval-series)
- **Physical power:** published daily/monthly product curves, curve-owned hourly
  LMP resolution, explicit DA availability replay, booked fixed physical Sides
  and shaped ATC quantities/prices. Close comparisons reconcile fixing, marks
  and captured corrections. See [support and limits](docs/power-curve-composition.md).
  Structured power also supports booked thermal tolls, battery and solar rights
  valued on a supplied plan, plus REC forwards and inventory. See
  [operating rights and certificates](docs/structured-power-trading.md).
- **Interval curves:** hourly and sub-hourly prices marked directly or shaped exactly
  from monthly peak and off-peak marks, delivered hours resolved to collected actuals
  under an explicit due rule, with lineage for every price, see
  [the implementation record](docs/implementation.md#interval-prices-curves-and-block-shaping)
- **Listed option analytics.** Explore fitted expiry smiles, term structure,
  rich/cheap heat maps and a linked 3D view using server-defined coordinates. The Options
  strategy view plots combinations of legs on one expiry and one underlying future.
  Valuation and risk keep each series’ own settlement calibration. See
  [volatility surfaces](docs/vol-surfaces.md) and [listed options](docs/futures-options.md).
- **History:** browse supported market and trade history at explicit business and
  knowledge times; trading-history cutover boundaries refuse earlier trade contexts.
  Close-based views use the latest close in force for the selected session and
  business day, including in historical views. Each signed-in tab keeps its
  **As of** selection. **Back** restores the previous cutoff and chosen close.
  Saved calculations retain their original inputs. See [historical context](docs/context.md)
  and [regional closes](docs/regional-closes.md).
- **Access control:** roles enforced by PostgreSQL row-level security, with people
  and AI agents as separate principals; agent actions require an explicit delegation
- **Private pilot mode:** invited user accounts and scheduled PJM and ERCOT day-ahead
  and real-time price collection; see [private pilot operations](docs/hosted-pilot.md)
- **Page assistant:** bounded delegated reads, human-approved business proposals,
  page guidance, selection questions and presentation walkthroughs, plus recorded
  conversations, provider configuration and usage accounting. You can enable
  search of your own chat history for an individual question. Walkthroughs use
  supported native controls and stop at unsaved edits; see
  [workspace operations](docs/assistant-workspace.md) and
  [walkthrough limits](docs/assistant-walkthroughs.md)
- **Operating settings and profiles:** versioned assistant/access policies under
  separate administration permissions, account-owned display preferences, explicit
  process/connection budgets, and server-supplied risk/request limits. Gas, crude
  and LNG execute retained instrument schedules under their exact commodity contracts;
  see [implemented consumers and remaining work](docs/operational-limits.md)

## What's not built yet

Open product work includes heat-rate call exercise and settlement, gas storage
facility allocation and measured flows, and dispatch and metered performance for
structured power rights. Broader futures settlement, inventory costing,
post-delivery crude valuation and PPA cash acceptance and payment also remain open.
Other gaps include EVT and operating backtests, broader agent credential
management, production qualification and general swap/PPA portfolio execution
from compact retained programs. See the
[delivery plan](docs/plan.md). If you want to help with any of these, start with
[CONTRIBUTING.md](CONTRIBUTING.md).

## Commercial support

LeanETRM is free under Apache 2.0 and will stay that way. If you want help
putting it to work, three things are available from the author.

**Pilot.** We run LeanETRM beside your current system, on your own trades, for
a fixed scope and a fixed fee. Typical scope is one desk and one commodity
through a month-end. You get a written comparison of valuations, positions and
P&L against your system of record, and a list of what would have to be built
before production.

**Implementation.** Product coverage for your book, integrations with your
market data, scheduling and accounting systems, data migration, and the controls
and sign-on your security team requires.

**Support.** Ongoing fixes, upgrades and new products under an annual agreement.

LeanETRM is not production-qualified yet. A pilot is how you find out what that
would take for your book.

Contact: Joe Isaac, [joe@trustygenius.com](mailto:joe@trustygenius.com) or
[LinkedIn](https://www.linkedin.com/in/jmisaac).

## Quickstart

You need the .NET SDK pinned in `global.json`, Node with npm (version in
`.node-version`), Python 3.12+ with assertions enabled and PostgreSQL 18.
CI runs on Ubuntu 24.04.

```sh
git clone https://github.com/zerodrift/leanetrm.git
cd leanetrm
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin --dotnet /path/to/dotnet
```

Open the `http://127.0.0.1:PORT` address it prints. You start as a read-only demo
user. Use **Curves & prices** and the header's close-session/date controls to
inspect the synthetic market. To book trades or correct prices, switch demo users
under **Profile → Preview access**; **New trade** opens the product booking workflows.

The preview builds the app, creates its own throwaway PostgreSQL cluster seeded with
synthetic data, and deletes it when you stop it with Ctrl+C. It never connects to an
existing database.

## Docs

- [Default public page and checks](site/README.md)
- [Frontend widget-to-backend operation map and verification](docs/frontend-wiring.md)

**Design**

- [Architecture and domain contracts](docs/architecture.md)
- [Delivery plan and acceptance criteria](docs/plan.md)
- [Decisions, proposals and unresolved choices](docs/decisions.md)
- [Events and lifecycle](docs/lifecycle.md)
- [Historical context and temporal data](docs/context.md)
- [Market model proposal and key linkages](docs/market-model.md)

**Implementation**

- [Current implementation and verification](docs/implementation.md)
- [Asynchronous workbench reads: coverage, protocol and limits](docs/async-reads.md)
- [Blotter workbench](docs/blotter-workbench.md)
- [Description-driven deal ticket (physical gas)](docs/deal-ticket.md)
- [Scheduled financial swaps](docs/financial-swap-quantities.md)
- [Financial swap settlement and P&L](docs/swap-settlement.md)
- [PPA and heat-rate portfolio consumers](docs/demo-instruments.md)
- [Heat-rate call options](docs/heat-rate-call-option.md) and [commercial gas storage](docs/gas-storage-contract.md)
- [Structured power operating rights, hedges and REC inventory](docs/structured-power-trading.md)
- [Physical gas booking and quantities](docs/physical-gas-side.md) and [Transport rights](docs/gas-transport-financial.md)
- [Physical crude booking](docs/physical-crude.md) and [delivery-to-cash operations](docs/physical-crude-operations.md)
- [Physical LNG cargo booking and valuation](docs/physical-lng.md)
- [Curve-definition preview and remaining publication work](docs/curve-definitions.md)
- [Market dashboard and saved risk runs](docs/market-dashboard.md)
- [Booked executable schedule storage and remaining cutover](docs/booked-side-programs.md)
- [Business scenarios, settlement gaps and shared scenarios](docs/business-scenarios.md)
- [Market-data contracts](docs/market-data.md)
- [Reference data and futures](docs/reference-data.md)
- [CME/ICE futures example](docs/exchange-futures.md)
- [Listed American options on futures](docs/futures-options.md)
- [Volatility surfaces](docs/vol-surfaces.md)
- [Time, delivery calendars and TOU](docs/time-and-schedules.md)
- [Schedules and contract sequences](docs/schedules-and-contract-sequences.md)
- [Physical power curves, hourly settlement and current limits](docs/power-curve-composition.md)
- [Booked peak and shaped ATC close comparisons](docs/power-booked-roll.md)
- [Power example and its limits](docs/power-preview.md)
- [Risk calculation benchmark](docs/risk-benchmark.md)
- [Connected curve calculation, booked projections and scenarios](docs/connected-calculation.md)
- [Large dependency-graph benchmark](docs/connected-graph-benchmark.md)
- [Nested market scenarios and durable risk jobs](docs/risk-engine.md)
- [Native one-session parametric VaR/ES](docs/parametric-risk.md)
- [Full-PV historical simulation VaR/ES](docs/historical-risk.md)
- [Full-PV Monte Carlo VaR/ES](docs/monte-carlo-risk.md)
- [PCA diagnostics and hybrid qualification](docs/pca-hybrid-risk.md)
- [Identity, agent login kinds and authorization](docs/security.md)
- [Reference schema and disposable verification](db/README.md)

**Operations**

- [Physical operations workspace](docs/physical-workspace.md) and [common delivery view](docs/physical-delivery-view.md)
- [Manual gas delivery operations](docs/gas-delivery-operations.md)
- [Final daily gas cash determinations and corrections](docs/gas-cash-billing.md)
- [Shared facility quantities, physical Side allocations and operating limits](docs/facility-quantity-ledger.md)
- [Regional close sessions and historical-view policy](docs/regional-closes.md)
- [Workspace assistant operations and human approval](docs/assistant-workspace.md)
- [Assistant digests and presentation walkthroughs](docs/assistant-walkthroughs.md)
- [Connected-curve cutover prerequisites](docs/connected-calculation.md#adopting-the-schema-cutover)
- [Jobs, workers and infrastructure](docs/automation.md) and [managed risk worker pools](docs/managed-risk-pool.md)
- [Private pilot operations](docs/hosted-pilot.md)
- [Operating policies, user preferences and process configuration](docs/operational-limits.md)
- [Local verification and CI](docs/verification.md)

The wider documents specify the intended system; they are not claims of implemented
functionality or production qualification. The intended hosted domain is
leanetrm.com. No hosted service or deployment is implied by this repository's
current status.

## Development

Read the [project instructions](AGENTS.md), [architecture](docs/architecture.md) and
[delivery plan](docs/plan.md) before changing the application.

- `python3 tools/check_standalone.py` checks dependency boundaries.
- Add `--check` to the preview command to run the HTTP integration checks, then clean up.
- `npm test --prefix web` runs presentation checks, including isolated browser-control
  checks when `CHROME_BIN` names an installed browser. The complete verification
  command passes its explicit `--browser` selection to these checks.
- [Local verification and CI](docs/verification.md) has the complete check command
  and hosted-runner boundaries.

## Contributing

Small fixes are welcome. Sign off contribution commits with `git commit -s`
under the [Developer Certificate of Origin](https://developercertificate.org/).
See [the contribution guide](CONTRIBUTING.md); designated core contributors also
sign the [Core Contributor Agreement](CLA.md).

## License

Copyright (c) 2026 Trusty Genius LLC.

Licensed under the [Apache License, Version 2.0](LICENSE).

The local frontend uses pinned public presentation dependencies for its trading
grid and analytical charts. The build serves their assets locally and includes
required license notices under `web/dist/vendor`. Business calculations remain
in the managed backend; see [implemented capabilities](docs/implementation.md).
