# Synthetic pipeline gas basis and allocation demo

D321 adds an opt-in, disposable example using the ordinary gas booking, quantity,
facility, curve and risk-history operations. All names, prices, quantities and
statements are synthetic. It connects no external source and imports no licensed
market data. It never opens an existing database.

## Start and verify

From this repository, supply the installed stock PostgreSQL tools and supported
.NET SDK explicitly:

```sh
python3.13 tools/run_preview.py --pg-bin /path/to/pg/bin --dotnet /path/to/dotnet \
  --with-gas-basis-demo
```

Open the printed loopback URL and choose the synthetic Trader profile. Stopping
that command removes its owned database. The gas example runs separately from
other optional examples. Its generated manifest and requests stay outside the
repository.

For the executable database/API/browser scenario:

```sh
python3.13 tools/run_preview.py --pg-bin /path/to/pg/bin --dotnet /path/to/dotnet \
  --gas-basis-demo-check --browser-check --browser /path/to/chromium
```

## Pricing and history

Use **As of 2020-12-18**, the synthetic pre-delivery close. Curves & prices shows
six monthly tenors, January through June 2021:

| Curve | Code | January price, USD/MMBtu |
| --- | --- | --- |
| Gas forwards, the selected benchmark | `base_forward` | 3.1 |
| Regional gas spread, separately quoted basis | `gas_basis` | 0.2 |
| Regional gas forwards | `local_gas` | 3.3 |

The declared curve relationship is `local_gas = base_forward + gas_basis`.
The benchmark itself has explicit futures and futures–forward spread inputs.
These are three distinct native risk inputs; no upstream dependency is inferred
from a price level. The delivery point's retained adjustment of -0.1 makes its
physical mark 3.2. That adjustment is separate from the basis quote and service
fees. This example has no usage fee and explicitly disables discounting.

Each basis tenor has 250 synthetic weekday observations from 2020-01-06 through
2020-12-18. The normal curve-history viewer exposes the latest 100 dated closes,
within the existing session catalog bound. The aligned risk-history operation
uses all 250 sessions and 249 joint changes across the three held native inputs.
The weekday calendar makes no exchange-holiday claim. Values are deterministic
example paths, not observed prices. Knowledge times are actual fixture receipts,
not invented historical publication timestamps.

## Recording sequence

1. Inspect the pipeline/zone/delivery-point setup and its exact references.
   Three seeded trades across two books and two counterparties each receive 100 MMBtu on 2021-01-01 and pay 3 USD/MMBtu.
   At the pre-delivery close each has contractual PV of 20 USD.
2. Open a trade's physical Side. Its explicit operational records show nominated
   90, confirmed 85 and scheduled 80 MMBtu. These preserve its contractual 100.
3. Apply **As of 2021-01-15** and open Operations → Facilities & delivery → Quantities & allocations. Choose the seeded
   2021-01-01 delivery-point ledger. Reported flow is 100; the saved plan assigns
   60/25/10, leaving 5 unallocated. Its native-period maximum is 90, so the
   assessment retains the report and shows a 10-unit breach. This profile assesses
   that limit; it does not turn it into a hard curtailment rule.
4. Enter an explicit recording instant on the selected UTC business date and
   acceptance evidence. Review the saved plan and accept it as final inputs.
   Unsaved source/plan edits must first be saved or discarded. The confirmation
   lists the exact saved quantities. Each trade's read-only facility track shows
   its own accepted amount.
5. Before correcting anything, retain the visible knowledge cutoff. Correct
   reported flow to 90. The current plan needs reconciliation; accepted final
   inputs still point to their original allocations. Save a replacement plan
   of 55/25/10 and explicitly accept it to change the final inputs.
6. Apply the earlier knowledge cutoff to inspect the original state. Historical
   views are read-only. Return to working knowledge before another write.
7. Withdraw final acceptance to demonstrate missing final inputs while keeping
   the operational plan. Removing a target or accepting an empty plan likewise
   produces missing final inputs, never zero or an older resurrected allocation.

## Final-input contract

`final_facility_allocated_billing_v1` is an explicit facility-only quantity-input
selection. It uses the ordinary managed gas-day selection and normalization;
manual allocated, nominated, scheduled and contractual quantities are not
fallbacks. It is a billing-input preview, not an invoice or settlement engine.
Existing manual quantity policies and contractual PV retain their contracts.

A whole-plan adoption pins an exact source/plan and creates only changed own-target
revision links. Quantities remain in the original immutable allocation rows.
A later source or plan does not replace accepted inputs until explicit adoption
or withdrawal. Unchanged own facts retain their identities when another target
changes. Pool totals, other counterparties and global adoption heads are not
exposed in an ordinary trade read.

Adoption requires current facility recording authority and gas write authority
for every included or removed target. No role is assigned outside this owned demo.
Receipts retain the original actor and exact scope; an old fact retry returns its
original identity without reinstating it as current. SQL foreign keys, immutable
records and deferred complete-set guards enforce the persistence boundary.
Migration 0197 requires separate schema qualification before hosted activation.
External pipeline submission, invoices, cash posting and operational-quantity PV
remain separate capabilities.

## Local verification

The 5 November 2026 local qualification passed 657 managed core checks and 1,345
frontend tests, plus the owned gas basis scenario and four browser checks. The
existing facility/gas regression passed its database/API checks, 20 facility
browser checks and 17 gas booking/setup browser checks. Runner checks confirm the
new scenario is included in the trading verification partition.

The scenario independently checks benchmark-plus-basis values, 250 native closes,
100 ordinary chart samples and 249 aligned joint changes. It covers unchanged
contractual PV, no-op setup, complete accepted-set integrity against an incomplete
definer writer, concurrent acceptance, historical cutoffs, source corrections,
removed/reinstated targets, withdrawal, exact old-fact replay and permission
revocation. A real observed storage lock crosses the request expiry for an
empty-plan receipt. Browser checks cover saved-quantity confirmation, unsaved
allocation protection, acceptance and withdrawal. These are correctness checks,
not throughput measurements or hosted qualification.
