# LeanETRM

**An open source ETRM for commodity trading, built for traders, risk managers and AI agents.**

LeanETRM books trades, builds curves and calculates P&L and risk on a standard,
unmodified PostgreSQL database. Calculations use exact arithmetic, every curve
price traces back to the observations that built it, and corrections never
overwrite history. It is aimed at shops that need real trade capture and risk
without a seven-figure implementation.

> **Status:** a foundation, not a finished product. Try it as a disposable local
> preview on synthetic data. Nothing here is production-qualified.

## What works today

- **Trade capture:** futures, plus financial gas and power swaps built from reusable
  fixed and floating sides, with legal entities, counterparties and an immutable
  amendment history
- **Curves:** futures, gas and power forwards, basis and interest rates, built from
  explicitly selected prices, with a diagram showing which inputs built each
  contract's price
- **P&L and risk:** futures execution P&L and projected swap PV, plus shared price
  shocks that revalue open futures positions with exact delta and gamma
- **Blotter:** filters, pivots, saved views, charts and exports
- **Interval series:** audited per-interval values on any market clock (price shapes,
  capacity-factor forecasts, metered energy) with CSV import, corrections, a change
  log and reads at a chosen knowledge time; nothing prices or settles from them yet,
  see [the implementation record](docs/implementation.md#audited-interval-series)
- **History:** view the market as you knew it on any past date and knowledge time
- **Access control:** roles enforced by PostgreSQL row-level security, with people
  and AI agents as separate principals; agent actions require an explicit delegation
- **Private pilot mode:** invited user accounts and scheduled PJM and ERCOT day-ahead
  and real-time price collection; see [private pilot operations](docs/hosted-pilot.md)

## What's not built yet

Daily settlement, actual swap fixings, recurring payment strips, swap risk, futures
PV and variation margin, options, physical forwards, VaR, production sign-in, agent
credentials and the in-app AI assistant ([design](docs/ai-assistant.md)). See the
[delivery plan](docs/plan.md). If you want to help with any of these, start with
[CONTRIBUTING.md](CONTRIBUTING.md).

## Quickstart

You need the .NET SDK pinned in `global.json`, Node with npm (version in
`.node-version`), Python 3 and PostgreSQL 16 or later. CI runs on Ubuntu 24.04.

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

**Design**

- [Architecture and domain contracts](docs/architecture.md)
- [Delivery plan and acceptance criteria](docs/plan.md)
- [Decisions, proposals and unresolved choices](docs/decisions.md)
- [Events and lifecycle](docs/lifecycle.md)
- [Historical context and temporal data](docs/context.md)
- [Market model proposal and key linkages](docs/market-model.md)

**Implementation**

- [Current implementation and verification](docs/implementation.md)
- [Business scenarios, settlement gaps and shared scenarios](docs/business-scenarios.md)
- [Market-data contracts](docs/market-data.md)
- [Reference data and futures](docs/reference-data.md)
- [CME/ICE futures example](docs/exchange-futures.md)
- [Time, delivery calendars and TOU](docs/time-and-schedules.md)
- [Schedules and contract sequences](docs/schedules-and-contract-sequences.md)
- [Power example and its limits](docs/power-preview.md)
- [Risk calculation benchmark](docs/risk-benchmark.md)
- [Identity, agent login kinds and authorization](docs/security.md)
- [Reference schema and disposable verification](db/README.md)

**Operations**

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
- `npm test --prefix web` runs the browser-independent presentation checks.
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
