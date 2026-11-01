# LeanETRM

**An open source ETRM for commodity trading, built for traders, risk managers and AI agents.**

LeanETRM books trades, builds curves and calculates P&L and risk on a standard,
unmodified PostgreSQL database. Calculations use exact arithmetic, every curve
price traces back to the observations that built it, and corrections never
overwrite history. It is aimed at shops that need real trade capture and risk
without a seven-figure implementation.

> **Status:** a foundation, not a finished product. Try it as a disposable local
> preview on synthetic data. Nothing here is production-qualified.

For a worked PPA example, follow the [PPA tester guide](docs/experimental-ppa-testing.md)
for synthetic booking, graphical break-even pricing and settlement previews.

## What works today

- **Trade capture:** futures, financial gas and power swaps, factor swaps and PPAs,
  with exact party references and retained history. New trade opens each product's
  booking workflow; supported amendments retain immutable revisions
- **Physical gas:** independent fixed delivery, cash and fee sides in the
  [deal ticket](docs/deal-ticket.md), with explicit fuel links, versioned delivery
  setup and trade-owned daily quantity revisions. Contractual pre-delivery PV is
  supported; gas scenario risk and physical settlement remain work
- **Physical crude:** fixed standard-volume booking, contractual pre-delivery PV
  and interactive/queued risk, plus bounded receipts, title transfer, inventory-lot
  allocation, dispatch, invoices, payments and cash-record corrections. See
  [the contract](docs/physical-crude.md) and [operations](docs/physical-crude-operations.md)
- **Curves:** futures, gas and power forwards, basis and interest rates, built from
  explicitly selected prices, with a diagram showing which inputs built each
  contract's price. The custom-curve workbench supports visual/formula recipes and
  explicit weighted contract baskets, reusable published curve outputs and guided
  combinations of computed curves with matching periods and units; all keep their
  upstream graph links; the swap editor selects compatible published
  outputs for monthly or exact daily projections. Effective-version timelines
  resolve under the selected business date and knowledge cutoff
- **P&L and risk:** futures execution P&L and swap/PPA PV using pinned actuals
  and projections. Connected quote and computed-output scenarios produce exact
  delta and gamma; generated-close jobs retain mixed futures-and-swaps reports.
  Interactive and queued portfolio risk also cover supported PPA and crude bindings;
  gas bindings appear as explicit exclusions, and automated gas/crude closes refuse.
  A delta ladder shows each shocked contract's totals by book and measure, see
  [the delta ladder](docs/delta-ladder.md)
- **Agent curve drafts:** authorized agents can read curve ingredients, prepare a
  pinned preview and save a bounded custom-curve draft through the ordinary Save
  operation under the person's authoring permission. Explicit Review opens the
  ordinary editor; publishing remains a person’s action
- **Live corrections:** push updates through the shared header context, with visible refresh/stale status; [policy and limits](docs/live-calculation.md)
- **Risk jobs:** temporary nested stresses, risk around the stressed market,
  and queued mixed-book runs pinned to the header's financial context, with retained
  results and cancellation; see [the risk engine](docs/risk-engine.md)
- **Scheduled swaps:** persistent booking, amendments and inspection of up to 120
  monthly periods per side, with explicit quantities, observations, payment rules
  and discounting; see [quantity contracts](docs/financial-swap-quantities.md)
- **Swap settlement:** opt-in immutable determinations, corrections, gross/net
  allocations, manual cash recording, reversals and a closing-context P&L bridge;
  see [the bounded lifecycle](docs/swap-settlement.md)
- **Blotter:** filters, pivots, saved views, charts, exports, docked trade details,
  pop-out and backend-prepared close-out drafts; see [the workbench](docs/blotter-workbench.md)
- **Dashboard:** account-owned layouts, curve levels/history and positions from
  saved risk run definitions; see [implemented widgets and limits](docs/market-dashboard.md)
- **Interval series:** audited per-interval values on any market clock (price shapes,
  capacity-factor forecasts, metered energy) with CSV import, corrections, a change
  log and reads at a chosen knowledge time. Shaped interval curves consume price
  shapes; PPA quantity/valuation consumes forecast and meter series. PPA settlement
  has a separate read-only monthly gross preview, without cash posting; see
  [the implementation record](docs/implementation.md#audited-interval-series)
- **Interval curves:** hourly and sub-hourly prices marked directly or shaped exactly
  from monthly peak and off-peak marks, delivered hours resolved to collected actuals
  under an explicit due rule, with lineage for every price, see
  [the implementation record](docs/implementation.md#interval-prices-curves-and-block-shaping)
- **History:** browse supported market and trade history at explicit business and
  knowledge times; trading-history cutover boundaries refuse earlier trade contexts
- **Access control:** roles enforced by PostgreSQL row-level security, with people
  and AI agents as separate principals; agent actions require an explicit delegation
- **Private pilot mode:** invited user accounts and scheduled PJM and ERCOT day-ahead
  and real-time price collection; see [private pilot operations](docs/hosted-pilot.md)
- **Page assistant:** bounded delegated reads, recorded conversations, provider
  configuration and usage accounting in the local preview; see
  [the assistant's implemented scope](docs/ai-assistant.md)

## What's not built yet

Full futures settlement PV and variation margin, options, VaR, gas scenario risk
and physical settlement, general physical power booking, inventory costing,
post-delivery crude valuation, PPA accepted-cash accounting/payments, broader agent
credential lifecycle and production qualification remain work. General swap/PPA
portfolio execution directly from compact retained programs is still an integration
task; the booking-storage checkpoint alone does not complete it. See the
[delivery plan](docs/plan.md). If you want to help with any of these, start with
[CONTRIBUTING.md](CONTRIBUTING.md).

## Quickstart

You need the .NET SDK pinned in `global.json`, Node with npm (version in
`.node-version`), Python 3.11+ with assertions enabled and PostgreSQL 16 or later.
CI runs on Ubuntu 24.04.

```sh
git clone https://github.com/zerodrift/leanetrm.git
cd leanetrm
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin --dotnet /path/to/dotnet
```

Open the `http://127.0.0.1:PORT` address it prints. You start as a read-only demo
user; click **Explore sample power prices** for a worked example. To book trades or
correct prices, switch demo users under **Profile → Preview access**.

The preview builds the app, creates its own throwaway PostgreSQL cluster seeded with
synthetic data, and deletes it when you stop it with Ctrl+C. It never connects to an
existing database.

## Docs

- [Public website source and checks](site/README.md)
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
- [Blotter workbench](docs/blotter-workbench.md)
- [Description-driven deal ticket (physical gas)](docs/deal-ticket.md)
- [Scheduled financial swaps](docs/financial-swap-quantities.md)
- [Financial swap settlement and P&L](docs/swap-settlement.md)
- [PPA and heat-rate portfolio consumers](docs/demo-instruments.md)
- [Physical gas booking and quantities](docs/physical-gas-side.md)
- [Physical crude booking](docs/physical-crude.md) and [delivery-to-cash operations](docs/physical-crude-operations.md)
- [Market dashboard and saved risk runs](docs/market-dashboard.md)
- [Booked executable schedule storage and remaining cutover](docs/booked-side-programs.md)
- [Business scenarios, settlement gaps and shared scenarios](docs/business-scenarios.md)
- [Market-data contracts](docs/market-data.md)
- [Reference data and futures](docs/reference-data.md)
- [CME/ICE futures example](docs/exchange-futures.md)
- [Time, delivery calendars and TOU](docs/time-and-schedules.md)
- [Schedules and contract sequences](docs/schedules-and-contract-sequences.md)
- [Power example and its limits](docs/power-preview.md)
- [Risk calculation benchmark](docs/risk-benchmark.md)
- [Connected curve calculation, booked projections and scenarios](docs/connected-calculation.md)
- [Large dependency-graph benchmark](docs/connected-graph-benchmark.md)
- [Nested market scenarios and durable risk jobs](docs/risk-engine.md)
- [Identity, agent login kinds and authorization](docs/security.md)
- [Reference schema and disposable verification](db/README.md)

**Operations**

- [Connected-curve cutover prerequisites](docs/connected-calculation.md#adopting-the-schema-cutover)
- [Jobs, workers and infrastructure](docs/automation.md)
- [Private pilot operations](docs/hosted-pilot.md)
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
