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
    exactly the money, calls are exercised and puts abandoned.
  - Exercise-notice cutoffs: 4:15 p.m. and 4:30 p.m. The rule states no time zone, so no
    instruction-cutoff instant is derived.
- **Option series (`ref_data.futures_option_series`):** explicit rows for the December
  2026–April 2027 maturities. Each maturity has three strikes on the listed grid, as calls
  and puts. One deep in-the-money ON call (December 2026, strike 2.00) is added for the
  intrinsic-value fallback. Each series:
  - names its exact underlying futures series;
  - takes its published expiry date;
  - expires at the configured close.

  November 2026 options have expired and are no longer published, so they are omitted.

Every series is an executable `future_option` market contract on its product's direct
`daily_settlement` curve. NG and CL futures are priced at `daily_settlement` as well,
because an option and its underlying are calibrated under the same observation role.

### Synthetic option settlements

Each frame's option settlement is generated as follows:

1. Take a European Black-76 value at a zero rate, from the underlying's synthetic
   settlement in the same frame.
2. Use a synthetic volatility: 0.60 for ON, 0.40 for LO.
3. Measure years to expiry as act/365 seconds to the configured expiry.
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
unimplemented. The power worksheet consumes an explicitly linked index using a
synthetic flat price, not official hourly market observations.

Amount precision belongs to shared current deal presentation, with independent
trade/fixed/floating role settings. Futures execution prices follow trading ticks;
settlement marks follow clearing increments. Other decimal-place settings default
to the documented deal policy and can be edited in the trade inspector. Display
changes do not append financial revisions or change exact exported values.
