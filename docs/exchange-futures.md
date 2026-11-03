# Exchange futures example

This local example contains public specification facts, explicit dated contract
definitions and original synthetic observations. It is selected independently of
the original synthetic workspace, with separate catalog, identity-provider,
workspace, sessions, pending commands and account layouts.

There are 20 real exchange futures product definitions. Thirteen monthly families have 78
source-confirmed maturities from November 2026 through April 2027; 67 are executable.
Eleven ICE year-end maturities retain their real last-trading dates with unknown cutoff
instants until specific-session hours are qualified. No fallback time is applied to those
dates. Seven daily product definitions retain their sourced terms
but have no executable maturity rows until an actual exchange contract list confirms them. Listing-cycle rules
alone do not establish currently listed maturities. Four snapshots dated
October 13–16 contain generated prices. The explicit application execution bound is October 1; it is not an exchange
first-listing date. Unknown source first-trading facts remain unknown. Prices are
generated: ICE prices are trader marks, and NYMEX NG, CL and option prices are synthetic
daily settlements. None of them is an official settlement. This is a bounded benchmark shortlist,
not a verified ranking by current traded volume or open interest.

| Families | Contract sizing | Public specification |
| --- | --- | --- |
| NYMEX Henry Hub NG | 10,000 MMBtu; physical | [CME](https://www.cmegroup.com/markets/energy/natural-gas/natural-gas.contractSpecs.html) |
| NYMEX Light Sweet Crude CL | 1,000 bbl; physical | [CME](https://www.cmegroup.com/markets/energy/crude-oil/light-sweet-crude.contractSpecs.html) |
| ICE PJM RT PMI, DA PJC | 1 MW; dated peak MWh totals | [PMI](https://www.ice.com/products/6590369), [PJC](https://www.ice.com/products/6590366) |
| ICE ERCOT North RT ERN | 1 MW; dated peak MWh totals | [ERN](https://www.ice.com/products/6590337) |
| ICE CAISO SP-15 SPM, NP-15 NPM | 1 MW; dated peak MWh totals | [SPM](https://www.ice.com/products/6590382), [NPM](https://www.ice.com/products/6590362) |
| ICE Chicago CFB, HSC FQH, Waha FQW fixed price | 2,500 MMBtu fixed total | [CFB](https://www.ice.com/products/68358298), [FQH](https://www.ice.com/products/69723155), [FQW](https://www.ice.com/products/69723181) |
| ICE Chicago DGD, HSC HXS, Waha WAH basis | 2,500 MMBtu fixed total | [DGD](https://www.ice.com/products/6590132), [HXS](https://www.ice.com/products/6590137), [WAH](https://www.ice.com/products/6590171) |
| ICE Chicago CSS, HSC UCS, Waha WAS daily swing | 2,500 MMBtu fixed total per dated contract | [CSS](https://www.ice.com/products/6590224), [UCS](https://www.ice.com/products/6590255), [WAS](https://www.ice.com/products/6590256) |
| ICE PJM PDA, ERCOT END daily peak | 800 MWh fixed total | [PDA](https://www.ice.com/products/6590471), [END](https://www.ice.com/products/6590453) |
| ICE CAISO NP-15 DPN, SP-15 SDP daily peak | 400 MWh fixed total | [DPN](https://www.ice.com/products/6590450), [SDP](https://www.ice.com/products/6590477) |

Contract-size and tick fields are separate. The example selects screen execution
ticks explicitly. Its generated marks use a separately declared increment based on the product quotation precision;
block execution grids and final-settlement processing are not enabled. Monthly
maturity dates and power totals are checked against exchange expiry records.
Ordinary-session last-trading cutoffs use the [ICE trading-hours specification](https://www.ice.com/publicdocs/futures_us/ICE_Futures_US_Regular_Trading_Hours.pdf);
this example does not implement a general holiday/early-close resolver. Its trading
calendar records only the four known business dates used by the sample frames;
uncovered dates remain unavailable. Delivery peak calendars are separate.
The source rulebook is [ICE U.S. Chapter 18](https://www.ice.com/publicdocs/rulebooks/futures_us/18._US_Gas_Power_and_Environmental_Contracts.pdf).
Published monthly lot totals are retained with exact series, quantity, factor,
rule version and source references; they are not recalculated using an ambient
timezone. Nominal MW remains distinct from resolved MWh. Calendar-day lot
integration has independent numerical controls in the tests;
invented products are excluded from this exchange example.

## Configured close of trading

Last-trading and option-expiry instants are derived from a sourced date and a configured
local time of day, never written as literal instants. Each row of
`ref_data.trading_close_times` holds:

- the product;
- the milestone, `last_trading` or `option_expiry`;
- the local time;
- the reference clock;
- the qualification source.

Where the instants are derived and checked:

- **SQL.** `ref_data.close_instant` derives each stored milestone and option-series
  instant. A product with no setting raises `close_time_setting_missing`. Writes check
  that a stored instant equals the configured close.
- **Managed backend.** The backend derives the instants again when it materializes
  futures and option series. A stored instant that differs raises `close_time_mismatch`.
- **Insertion order.** A close configured after its product's milestones must reproduce
  every stored instant.
- **Milestone clock.** A last-trading milestone carries the configured close's clock, and
  its product cites the qualification source as date evidence.

| Products | Close | Clock | Qualification |
| --- | --- | --- | --- |
| NYMEX NG, CL; options ON, LO | 14:30 | New York (`ept`) | `operator:nymex-close-1430-new-york-v1`, an operator qualification of the close of trading; not an exchange-published instant |
| ICE monthly families | 18:00 | New York (`ept`) | `operator:source-date-plus-ice-1800-et-v1` |

## NYMEX CL and listed options

The example also holds NYMEX CL and the American options on NG (ON) and CL (LO). Their
facts were retrieved on 2026-11-01 from the CME specification and calendar pages and from
rulebook chapters 300, 310 and 370 and the strike-price listing and exercise-procedures
table, which is dated 2026-08-24. Each source is recorded in `ref_data.sources`, with
facts only and no document text or prices.

- **CL:** six maturities, November 2026 through April 2027, with their published last
  trading dates and the configured close.
- **Option products (`ref_data.futures_option_products`):**
  - one option exercises into one contract of the underlying future, so the multiplier
    is 10,000 MMBtu for ON and 1,000 bbl for LO;
  - the premium increment is $0.001 for ON and $0.01 for LO, used as both trading tick
    and settlement clearing increment;
  - the lot quantity is one option contract;
  - premium is paid up front;
  - contrary instructions are not accepted;
  - near-the-money strikes are listed every $0.05 for ON and every $0.50 for LO.
- **Option rules (`ref_data.futures_option_rules`):** versioned and sourced.
  - American exercise.
  - Expiry: ON expires one business day before its underlying, LO three business days
    before.
  - Automatic exercise, `nymex_itm_settlement_v1`: an option in the money by any amount
    against the underlying's daily settlement on its last trading day is exercised. At
    exactly the money, calls are exercised and puts abandoned. The row stores these as
    parameters of the `settlement_moneyness_v1` evaluation: in-the-money threshold 0,
    at-the-money call `exercise`, at-the-money put `abandon`.
  - Exercise-notice cutoffs: 4:15 p.m. and 4:30 p.m. The rule states no time zone, so no
    instruction-cutoff instant is derived.
- **Option series (`ref_data.futures_option_series`):** explicit rows for the December
  2026–April 2027 maturities. Each maturity has seven strikes on the listed grid, at the
  maturity's centre and 2, 4 and 6 strike intervals either side, as calls and puts. One deep in-the-money ON call (December 2026, strike 2.00) is added for the
  intrinsic-value fallback. Each series:
  - names its exact underlying futures series;
  - takes its published expiry date;
  - expires at the configured close.

  November 2026 options have expired and are no longer published, so they are omitted.

Every series is an executable `future_option` market contract on its product's direct
`daily_settlement` curve. The market curve directory lists price curves only; an option's
settlement curve is named by the `curve` field of its `options/catalog` entry. NG and CL futures are priced at `daily_settlement` as well,
because an option and its underlying are calibrated under the same observation role.

### Option lifecycle

Each lifecycle event is one authorized operation. It needs the booking authority, current
knowledge and a request identity (D238):

- a retry returns its first outcome, but only while the caller can still see the trade;
- recording an existing fact again under a new request returns the existing record: the
  same instruction, or a holding's expiry on the same settlement;
- a contrary instruction raises `option_instruction_conflict`, and an expiry on a
  different settlement raises `option_expiry_conflict`.

- **`security.record_futures_option_event`** (`options/exercise`, `options/assignment`,
  `options/instruction`):
  - exercise closes lots of a long holding, assignment lots of a short holding, at the
    frame's time;
  - an event dated after the moment it is recorded raises `option_event_future` and
    writes nothing (D290);
  - the date must be a business day of the underlying's trading calendar, from the
    trade's execution through the option's expiry date;
  - instructions are refused with `option_contrary_instructions_not_accepted` unless the
    product row accepts them. Where accepted, a new instruction needs open lots
    (`option_holding_closed`) and a business time at or after the trade's execution
    (`option_instruction_window`); both its business time and the time it is recorded
    must be at or before the series' configured cutoff, so an older frame cannot file a
    late instruction. Instructions follow the cutoff, not the exercise calendar.
- **`security.process_futures_option_expiry`** (`options/expiry`) closes a holding's
  open lots at the option's expiry instant:
  - it can be recorded only once that instant has passed: a recording time before it
    raises `option_expiry_not_reached` and writes nothing, whatever the frame holds;
  - a filed instruction decides; otherwise the product's automatic exercise rule
    evaluates the underlying's final daily settlement on the expiry date, taken from
    the frame's selected observations;
  - the event records the frame (snapshot) identity and the one settlement it selected,
    and the linked future's request names both;
  - with no such settlement it raises `option_expiry_settlement_missing`, writes
    nothing and leaves the holding open.
- **Linked futures.** Each exercised or assigned lot books one future of the underlying
  at the strike. It goes in the option's book with its legal entity and counterparty,
  booked by the caller, in the same transaction and through the futures booking path.
  - A call exercise or put assignment books a long future; a put exercise or call
    assignment books a short future.
  - The future's operation identity is derived from the event's request identity.
  - The future is valued on the underlying's single direct daily-settlement curve.
- **Reads.** Open lots are never negative. `options/holding` reads a holding's open lots
  and events at the read's knowledge, so a read before an event shows the holding as it
  was.

The exercise-notice times of day are not enforced, because the rule states no time zone.
No agent tool records lifecycle events.

### Synthetic option settlements

Each frame's option settlement is generated as follows:

1. Take a European Black-76 value at a zero rate, from the underlying's synthetic
   settlement in the same frame.
2. Measure years to expiry T as act/365 seconds to the configured expiry.
3. Use a synthetic smile volatility, base + skew·k + curve·k² + term·T with
   k = ln(strike / futures): (0.55, −0.20, 2.00, 0.10) for ON and
   (0.36, −0.30, 4.00, 0.08) for LO. A fixed table in the fixture adds +0.03 or −0.03
   to four out-of-the-money series of each product, so a fitted surface shows two rich
   and two cheap points per product.
4. Round half-even to the clearing increment.

The values are computed with the standard-library `decimal` oracle in
`tests/fixtures/futures_option_oracle.py`. Every settlement lies above intrinsic value
plus one increment and at or below its bound: the futures price for a call, the strike
for a put. The exception is the deep in-the-money fallback series, which settles exactly
at intrinsic value. The storage check verifies these bounds in every frame.

The exact `ref_data.futures_series(catalog_id, revision_id, code)` primary key is
the instrument identity. Product delivery profile is authoritative; curve recipes
cannot supply a different economic profile. SQL booking and managed loading both
enforce it. A dated basis future additionally retains the regional index and the
exact matching Henry Hub futures series for its final-settlement formula.
These references do not become live pricing dependencies: a directly observed
basis quote is its own factor, including when its value is zero or negative.
An outright constructed from Henry Hub plus basis would require an explicit
composition and mappings; this example does not infer one.

Executable scope is booking, amendments, exact execution P&L and deterministic
shared base/up/down open sensitivity. Daily/final settlement, variation-margin
transitions, final index fixings and physical delivery operations remain
unimplemented. Listed options on NG and CL have their own capability record in
[futures options](futures-options.md). The power worksheet consumes an explicitly linked index using a
synthetic flat price, not official hourly market observations.

Amount precision belongs to shared current deal presentation, with independent
trade/fixed/floating role settings. Futures execution prices follow trading ticks;
settlement marks follow clearing increments. Other decimal-place settings default
to the documented deal policy and can be edited in the trade inspector. Display
changes do not append financial revisions or change exact exported values.
