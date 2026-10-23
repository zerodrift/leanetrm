# Experimental PPA testing checkpoint

This feature branch is for synthetic testing and review. It is not a production
release or an existing-database upgrade qualification. Do not point the preview
at another application or database. The launcher creates and removes its own
stock PostgreSQL cluster; stopping it discards all exploratory bookings.

## Prerequisites and startup

Use Python **3.11 or newer**, Node **24.22.0** (`.node-version`), .NET SDK
**10.0.402** (`global.json`), stock PostgreSQL **16+** binaries, and an installed
Chromium-compatible browser for browser verification. Python assertions must stay
enabled. Run from the repository root. Put the required Node and Python versions
on `PATH`; an operating system's older `python3` is insufficient.

```sh
python3 --version
node --version
python3 tools/run_preview.py --example standard --with-ppa \
  --pg-bin /absolute/path/to/postgresql/bin \
  --dotnet /absolute/path/to/dotnet
```

The launcher installs locked frontend dependencies, builds the API and frontend,
loads explicitly synthetic references and November 2031 shape inputs, and prints
`PREVIEW_URL`. Open that URL. Local synthetic personas need no external account;
select **Trader**. The option creates no PPA booking. Ctrl+C stops the owned
processes and deletes the disposable database. Never reuse its connection files.

## PPA entry and graphical model pricing

Open **PPAs**, select an available ordinary market snapshot and load its current
knowledge cutoff. Choose **Price an unbooked draft** for an unbooked scenario, or **New PPA booking**
for a booking. A booking also requires an authorized book and synthetic legal
entity/counterparty; select these explicitly from the catalog.

A reproducible fixed physical example uses these explicit choices:

| Field | Synthetic value |
| --- | --- |
| Composition | Fixed physical |
| Delivery month | 2031-11-01 through 2031-12-01 |
| Native clock / cadence | `synthetic_eastern` / 60 minutes |
| Selected coverage, local time | 2031-11-02 00:00 through 2031-11-03 00:00 |
| Contract quantity / unit | 80 / `test_capacity_quantity` (MW) |
| Currency / discounting | `usd` / false |
| Quantity | Profile, all hours, `test_capacity_quantity` capacity and `test_power_quantity` energy |
| Calendar / pending actuals | `synthetic_eastern_holidays` / `require_actuals_v1` |
| Delivery coefficient / location | +1 / `test_hub_a` |
| Strike coefficient / price | −1 / flat 40, multiplier 1, `test_power_quantity` |
| Both payment rules | Monthly, day 10 of following month, short month reject, unadjusted |

Review the terms and preview November 2031. The repeated DST hour is retained:
25 hours × 80 MW = 2,000 MWh. Select the strike payment and `CompletePv`, choose
two quote decimal places and `half_even_v1`, then calculate the model break-even
price. The synthetic result is **41.25 USD/MWh**, sensitivity **−2,000**, input
value **2,500 USD**, and zero rounded-quote residual. Physical value **82,500 USD**
is separate from gross contractual payment **−80,000 USD** at the entered strike.

Inspect monthly volumes, market/strike profiles, gross payments and the
value-versus-strike graph. Exact receipts and drill-down values come from the
server; plotted numbers are presentation conversions. Changing terms, cutoff or
snapshot invalidates the old result. A draft calculation never books or amends a
trade. To book, use the explicit reviewed booking flow. Amend/terminate from the
blotter, preserving prior bindings and their original payment dates.

For the supported virtual fixed-for-floating example, choose its composition,
replace delivery with an indexed receipt on `test_power_hourly_shaped`, coefficient
+1, multiplier 1, index multiplier 1 and adder 1.25; retain the −1 flat strike
payment and the same quantity profile. Its model break-even is also 41.25.
These are synthetic model values, with no commercial risk premium.

## Settlement preview examples

The bounded API supports completed monthly gross fixed or indexed payments for
supported physical PPAs. It requires explicit lineage opt-in, complete retained
coverage and final meter publication heads. There is no separate approval UI.
The proposal permission has no default grant: merely selecting Trader does not
confer it. The executable examples grant the scoped permission only inside their
owned test database, publish synthetic evidence and exercise the authorized API:

```sh
python3 tools/run_preview.py --pg-bin /absolute/path/to/postgresql/bin \
  --dotnet /absolute/path/to/dotnet --ppa-check
python3 tools/run_preview.py --pg-bin /absolute/path/to/postgresql/bin \
  --dotnet /absolute/path/to/dotnet --settlement-quarter-check
```

The first runs the 721-hour November 2020 example; the second runs 2,884 native
quarter-hours in a separate fresh database. They include fixed cash, unequal
interval quantities/prices, explicit payment-index selection, multiplier/adder,
negative prices, zero/missing, amendments, termination and correction-cutoff
replay. See [the operation and evidence contract](settlement-preview.md),
[HTTP examples](../tools/check_indexed_settlement_preview.py) and the
[independent specification](../specs/settlement/reference_model.py).
Collected price availability does **not** certify contractual finality. Only the
meter publication is explicitly final. No preview recognizes cash or changes PV.

## Qualification and limits

Run the full gate from a clean checkout with fresh owned resources:

```sh
python3 tools/check_all.py --pg-bin /absolute/path/to/postgresql/bin \
  --dotnet /absolute/path/to/dotnet \
  --browser /absolute/path/to/chromium
```

Success requires every stage to finish, the final `All synthetic verification
suites passed.` marker, and positive `DURABLE_COMPLETION_EVIDENCE` for the real
long-running risk calculation. An interrupted required stage fails qualification.
A draft PR or green build alone is not complete qualification; consult its actual
CI results. Local diagnostic measurements do not promise all admitted workloads
complete within the interactive 20-second target.

- Pricing solves one explicitly selected flat strike using exact linear valuation;
  unsupported terms, missing coverage and zero sensitivity refuse. It requires an
  explicit measure, cutoff, snapshot, quantity, coverage and discount choice.
- Settlement previews are one completed month, at most 32 bindings/four assets,
  3,100 native contributions/12,400 meter cells, 15 seconds and 16 MiB. Missing or
  incompatible evidence refuses the whole result. PPA valuation previews retain
  their separate 20-second/8 MiB limits.
- A catalog retains at most **4,096 contexts**. Per-cell finality publication
  consumes this budget; four-asset quarter-hour monthly publication cannot fit
  through that sequence. Fresh separate fixtures are required for the examples.
- Settlement approvals, accepted-cash accounting, obligations, payments,
  reversals and automated correction adjustments are deferred. No settlement
  worker, gas settlement expansion or generated-close PPA cutover is included.
- Existing-close PPA risk preserves admission pins across retries and uses fenced
  renewable ownership with an explicit overall deadline. The dedicated connected-risk worker reads shared expiry metadata under its
  existing current-service and scoped temporal predicates, omits unused PPA
  asset/source metadata, and uses a trusted boolean complete-scope guard. The connected portfolio
  consumer refuses books containing unsupported PPA positions as a whole.
- Migrations **0090–0100** append current master (through 0089). Fresh installation
  is tested. Upgrades from existing populated databases, including unpublished
  earlier migration numbering, are **unqualified**; do not renumber a live history.

[Phase C](ppa-phase-c.md), [pricing](ppa-break-even.md) and
[meter finality](meter-finality.md) document individual historical qualifications.
The draft PR records the current integrated commit's gate and CI results.

The durable recovery workload checks the risk loop's curve counters separately:
for 360 direct factors and 721 scenarios it expects 720 rebuilt nodes and
258,480 explicit-parent reuses. The preceding PPA preparation warms the base;
its 360 cached base values cause neither a new evaluation nor an explicit-parent
reuse in that loop. These fields do not measure complete preparation work or
all cache hits. Complete worker elapsed time includes preparation.
