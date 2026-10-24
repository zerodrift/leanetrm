# Exchange futures example

This local example contains public specification facts, explicit dated contract
definitions and original synthetic observations. It is selected independently of
the original synthetic workspace, with separate catalog, identity-provider,
workspace, sessions, pending commands and account layouts.

There are 19 real exchange product definitions. Twelve monthly families have 72
source-confirmed maturities from November 2026 through April 2027; 61 are executable.
Eleven ICE year-end maturities retain their real last-trading dates with unknown
cutoff instants until specific-session hours are qualified. No 18:00 fallback is
applied to those dates. Seven daily product definitions retain their sourced terms
but have no executable maturity rows until an actual exchange contract list confirms them. Listing-cycle rules
alone do not establish currently listed maturities. Four snapshots dated
October 13–16 contain generated prices. The explicit application execution bound is October 1; it is not an exchange
first-listing date. Unknown source first-trading facts remain unknown. Prices are
generated trader marks, not official daily settlements. This is a bounded benchmark shortlist,
not a verified ranking by current traded volume or open interest.

| Families | Contract sizing | Public specification |
| --- | --- | --- |
| NYMEX Henry Hub NG | 10,000 MMBtu; physical | [CME](https://www.cmegroup.com/markets/energy/natural-gas/natural-gas.contractSpecs.html) |
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
