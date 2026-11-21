# Regional currency examples

`regional_currency_examples_v1` is an explicitly selected, bounded synthetic
preset. It extends the standard December 2020 example without replacing its USD
sources, exact references, currency discount default or booked trades.

```sh
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin \
  --dotnet /path/to/dotnet --with-regional-seed
```

The preset records **NYC Close**, at 16:00 `America/New_York`, and **UK Close**, at
16:30 `Europe/London`. Each uses its own published clock and a Monday–Friday
synthetic calendar without holiday exclusions. Samples cover 15 July and
15–18 December 2020. UK cutoffs are respectively 15:30 and 16:30 UTC; NYC cutoffs
are 20:00 and 21:00 UTC. July has newly authored synthetic observations before
both cutoffs; original December observations are never retimed.

The currency directory contains USD, GBP, EUR and CAD. GBP, EUR and CAD each have
their own outright forward, index, observations, exact currency discount default
and interest rate curve. Their three dated zero-rate pillars are 19 December 2020,
3 February 2021 and 3 February 2022. All three curves explicitly use decimal annual
marks, simple compounding, Actual/365 Fixed, log-linear discount interpolation and
no extrapolation. The synthetic marks are independent examples, not benchmark
rates or FX conversions.

Three booked swaps consume these inputs through the ordinary booking operation,
including retained independent Side schedules. Each receives ten units of its
own January 2021 floating price and pays its own fixed price, with discounting
enabled. The declared payment rule is three calendar days after the final delivery
day, unadjusted: 3 February 2021. Quotes reuse the standard example's explicit gas
quantity/unit and location. These are generic synthetic contracts; this preset
does not implement a UK, continental European or Canadian gas market product.
CAD supplies a working pricing example; AECO remains a future product consumer.
The trades are economically booked at the July UK sample, so they can be inspected
at every seeded regional close.

`tools/regional_seed.py` loads new source revisions and prepares exact source
matrices and stable command IDs. `tools/OpenEtrm.Seed` publishes the catalogue and
close sessions, grants capture authority, records qualified closes and books the
swaps through existing authorized operations. HTTP and seeding share
`SwapBookingOperations`, so retained Side publication is part of both paths.
Hosted loading requires explicitly supplied source/installation, portfolio,
party versions and the owner's current authenticated session. It verifies market
permissions and ownership of the exact reference catalogue before and after each
privileged source phase, including after read/constraint completion. Helpers are
session-local and disappear when the seed connection closes.

Repeated or interrupted runs verify existing exact facts and resume without
duplicating source rows, definitions, observations, closes or trades. Conflicting
terms under the same seed version refuse; changing financial terms requires a new
explicit revision. The source publication gate also serializes concurrent resumes.
Existing financial contexts are never imported or saved. Closes retain their own
input evidence, and each operation/job attempt constructs its runtime context.
No schema migration or new persistence table is required.

The old synthetic UTC bootstrap convention is retired from discovery while its
retained closes stay readable. Fresh preview users move from that known bootstrap
selection to NYC Close. A subsequent seed run preserves UK or another personal
selection. Additive hosted runs leave personal selections unchanged.

## Qualification

```sh
python3 tools/run_preview.py --pg-bin /path/to/postgresql/bin \
  --dotnet /path/to/dotnet --regional-seed-check --check
```

The reference CI group includes this gate. It exercises an interruption after a
committed mapping and two concurrent resumes, native winter/summer close
qualification, independent discount-factor and booked-PV calculations, unchanged
USD holdings, retained-worker parity and a complete repeated seed. Disposable
hosted admission checks cover read-only actors, shared read-only reference scope,
expired sessions, sign-in/role deadlines during a phase and revoked replay.
Successful source phases revoke their temporary-schema runtime grants before
commit; admission checks verify that no such grants remain.
The focused reference demo separately qualifies the shared HTTP booking path.
A separate `--regional-seed-startup-check` in the trading CI group runs the full interactive seed,
including the compiled date schedules and mappings, checks initial discovery and
then removes its owned database. Schedule authoring captures a new context after
selecting its author; no read reference crosses a profile change.
Full CI, release packaging and installation activation remain separate gates.

Local qualification passed on .NET SDK 10.0.402 and unmodified PostgreSQL 18.7:
ten regional closes, three currency curves with nine rate pillars, three booked
discounted swaps, exact preservation of 24 USD holdings, independent numerical
oracles, retained-worker parity and unchanged business facts on replay. The
interactive startup, existing reference-demo and scheduled booking/amendment
gates also passed, alongside 178 Python checks. These are bounded functional
results, not capacity measurements.
