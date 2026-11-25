# Physical gas booking and pre-delivery valuation

## Imbalance cash-out (D454)

An independent physical Side can carry a `daily_index_cashout_v1` pair of
ordinary named daily index payments. Each gas day has a received `owed_to_us`
entry and a paid `owed_by_us` entry, with separate spreads, the same index and
a payment day 1–28 of the following month. Explicit delivery, observation and
payment calendars are required. The pair uses two payment memberships within
the existing seven-payment/eight-total limits. It cannot have a quantity part,
a share other than one, a partial term, a fuel-linked source or an exercise
right. It can coexist with independent charges and rights on other entries.

The trade states the statement basis (`daily_imbalance_v1` or
`month_to_date_balance_v1`), netting (`net_accounts_v1` or `each_account_v1`)
and final-only or provisional-and-final policy. Service accounts held by the
trade's legal entity supply absolute quantities. No service account is required
at booking. No nominated, scheduled or allocated quantity supplies the balance.
The existing delivery and ordinary payment values are unchanged.

Account coverage, statement heads and accepted transfers use the same business
time and knowledge cutoff as valuation. The latest visible statement is selected
before testing policy; withdrawal or a newer provisional statement under
final-only policy makes the day unassessed. Every account covering the day must
have an eligible statement. Current balance includes accepted movements not
already included in the statement. Net accounts splits their sum into the two
directions; each account splits before addition.

Month-to-date uses the immediately preceding calendar day's balance per account.
Only the first of a month needs no predecessor; a term beginning mid-month has
an unassessed opening month. A missing day affects itself and the next day's
difference. Later consecutive balances can recover. An accepted transfer in the
month refuses that month's value with `gas_imbalance_transfer`.

Before day end, the imbalance is not applicable and has no quantity or amount.
After day end, an eligible statement has a nonnegative absolute quantity on each
entry, including explicit zero. An unpublished index gives projected cash; a
published index earns it. An unassessed row has no quantity, accrued amount or
outstanding amount. Totals remain visible as incomplete subtotals, with counts
at entry, trade, portfolio and risk level. The pair's missing day counts once in
aggregate. Recorded payments survive withdrawal as `UnassessedPaid`; known
corrections can create underpaid or overpaid balances, including a sign change.
A corrected fixing that crosses zero also retains previously recorded cash in
its booked direction. New payments still obey the fixed direction rule.

P&L for a cash-out binding includes all of its native gas cash-payment revisions,
with one stable flow per payment business key. Revised cash replaces prior cash;
it is not added again. Quantity-only value changes remain in the residual.
P&L shows separate opening and closing unassessed-day counts, and an incomplete
zero subtotal remains visible. Joint risk carries coverage on each book subtotal.

`security.record_gas_cash_settlement` accepts one net fact with explicit signed
occurrence allocations for each direction in one month. It records the linked
payments atomically and checks conservation. Identical facts and command retries
retain their original settlement; a different second monthly settlement refuses.
This native operation does not send money. There is no net-settlement entry
screen in this increment. Negative prices value with their sign but cannot be
paid against the entry's opposite direction.

There is no link to an owned operator balance: representing the same gas in both
can double count it. Retired service terms may prevent a late final statement.
Rolled balances, other cash-out pricing profiles, transport imbalances and
accounting remain separate contracts. See [D454](decisions.md#d454--reported-gas-balances-supply-an-explicit-cash-out-pair)
and [local verification](verification.md#imbalance-cash-out-d454).

## Independent gas charges (D450)

A standalone gas composition can include a fixed contractual `charge`
membership alongside physical, consideration and fee Sides. It uses the
existing fixed payment Side and names a physical Side on the same trade to
select the gas-day clock. It owns its terms and quantities, with no source or
stage. The quantity unit and currency match the composition. The combined
limits are one to four physical Sides, seven payment Sides including charges,
and eight Sides in all.

- **Lump sum** (`lump_sum`) stores a stated amount, an explicit UTC payment
  instant and quantity one. Its single gas day is the latest one ended when
  that instant's UTC date begins. A lump sum dated on the booking date can
  already be accrued at booking, before physical delivery starts. An
  independent lump sum can be a premium alongside a right over other Sides.
- **Capacity** (`capacity`) stores a positive fixed rate, a positive daily
  capacity and ordered gas days with an exclusive end. There is one row per
  covered calendar month, including partial months. Each row's quantity is
  the daily capacity times the number of covered days. The stated payment
  day, 1–28 of the following month, pays at UTC midnight without holiday
  adjustment under `next_month_day_utc_v1`.

Both bases use the gas composition's explicit no-discounting selection and
require explicit delivery and payment calendar bindings. The contractual
amount or rate is in trade currency, with the rate per quantity unit per day.
The fixed Side retains the exact versioned quote
and normalizes the raw amount or rate by its multiplier using exact division.
The absolute stated price must be below 10^12. A price literal that cannot
be represented exactly at the supported 18-place decimal scale refuses.
Migration 0400 also checks the first owned UTC instant under the admitted
fixed-offset clock before retaining either charge basis. Exact ratios on the
holding base let the Trade apply quantity once and preserve the same stated charge at different delivery sizes.

Accrual uses each charge's own ended gas days, even outside the physical
term. Equal daily capacity makes accrual proportional to the number of ended
days. Shared UTC payment-date admission and the complete-day overdue rule
apply. A capacity row payable on day 1 can finish later on that UTC date and
is overdue starting the next UTC date. Payments use the retained charge
occurrence and are admitted once any of its days has ended. Both the
valuation instant and the resolved knowledge cutoff govern visibility.
Accrual, part payment, full payment and overpaid balances use the existing
cash lifecycle. The sign of a lump-sum remittance follows the membership
direction multiplied by the stated amount's sign. Zero nominal admits no
remittance. Native payment admission checks earning at the actual paid instant
and refuses with `gas_payment_occurrence` if no day has ended by then. It uses
`gas_payment_amount` for a contradictory sign or zero nominal. The managed
lifecycle checks the earned state at the valuation instant and uses
`payment_state` if visible cash is applied to an occurrence with no ended day.
A right naming a charge refuses with
`gas_composition_input` during booking and `exercise_right` during retained
admission.

Previews, held reads, the Blotter and risk use the retained Instrument and
Side programs. Charges have no market-price dependencies and contribute no
price-factor exposure. Final billing refuses charge slots with
`gas_billing_profile`. Ordinary fixed consideration keeps its existing
billing and physical coverage rules. See the
[ticket contract](deal-ticket.md#gas-charge-side-d450) and
[decision](decisions.md#d450--gas-compositions-admit-fixed-payments-that-own-their-quantity).

## Exercise rights (D448)

A gas composition can include disjoint rights over named existing entries. Every
right saves its ordered entries, period (`delivery_day` or `delivery_month`),
holder (`us` or `counterparty`), settlement (`physical` or `cash_automatic`),
exact group dates and exercise instants, model `spread_call_kirk_lognormal_v1`
and day count `OptionNumericalPolicies.ActualOver365FixedInstant`. Rights belong
to Instrument identity. The existing Side programs and booked schedules still
own the delivery and cash rows they evaluate.

- Physical groups must cover a physical Side. The selected nonnegative
  whole-hour lead saves `lead_before_first_gas_day_v1`. The deadline is at or
  before the first retained gas-day boundary of each selected entry represented
  in the group. Physical exercise requires an eligible recorded election, even
  when the forward sum is positive.
- Cash automatic groups cover payments only and need an actual retained fixing
  instant. The latest fixing instant belongs to each group independently. They
  save no physical lead rule. All required index fixings must be published
  in the selected view for a decision, including when the row takes zero quantity.
  A positive exact holder sum exercises and an exact zero or negative sum
  abandons. Unpublished prices at or after the last fixing instant give an `undecided_forecast` group at zero model time
  while the projection is available, including after the payment due date.
- Admission requires explicitly undiscounted valuation and independent physical
  quantity sources. Every explicit physical quantity link is excluded, including
  a link with zero fuel. Physical day quantities and connected payment quantities
  follow their exact sources. During ordinary valuation an exercised day uses
  the entry's own selected quantity, share and part. Abandoned or undecided days
  contribute zero. A whole-term row accrues only on its exercised days.

An undecided group prices the hypothetical rows for its own dates through the
retained Sides. An undecided physical group uses contract quantities. Fixed
prices and published index fixings are certain. Other prices remain market marks
regardless of the Side's pricing label. A group takes at most one uncertain curve
per holder leg, with distinct curves for two legs. Pricing selects exact-version
volatility per curve and symmetric correlation per qualifying pair, by the
group's delivery month. The convention is `curve_marks_log_return_held_v1`.
A single uncertain leg needs only its volatility. No volatility or correlation
is inferred. Shared price scenarios hold marks, and correlation has no shock.
Interactive and retained joint samples can apply parent-additive volatility
shifts to explicit declared input IDs, alone or with price shifts. The two
shift kinds share the existing limit of 16 per sample. The numerical boundary
and full model table are in [D448](decisions.md#d448--exercise-rights-are-terms-of-an-instrument).

An election is a revisable or withdrawable fact on a physical group of the exact
trade binding. It records `exercised` or `abandoned`, an effective instant and
evidence. The effective instant must be at or before the saved deadline.
Valuation selects the highest revision known at its resolved cutoff
and effective by its valuation instant. A known future-effective change limits
reuse across that boundary. Amendments retain their existing eligibility rules.
A rights-only change creates a separate binding with its own elections. The
prior binding retains its terms, facts and authorized historical values. A leased
risk worker reads rights on a binding held by a selected trade at its retained
cutoff, including when another holding shares that binding.

The curve-mark read and both mark record routes bind the exact selected
definition and curves. Reads select revisions by effective and knowledge
eligibility. Mark recording requires working data with current
`ReadMarketData` and `ImportObservations` authority and exact visible sources.
Mark command replay requires the original effective instant as well as the
original payload. The selected correlation revision belongs to the pricing
binding even when another revision has the same coefficient. Authorized mark
and election commands use expected predecessors and atomic publication. See [recording elections](deal-ticket.md#gas-exercise-rights-d448).

The schedule shows each group's state, exact Forward sum and Counted amount,
plus model inputs for undecided groups. The first two amounts for an exercised
group are gross selected row economics before payment. Abandoned Counted is
zero. Paid, Outstanding and Realized unsettled amounts and settlement-adjusted
PV belong to ordinary Side rows. After payment, group Counted totals need
not equal PV. Ordinary entry contributions plus undecided values reconcile
exactly. Draft and held previews, exact trade reads, the Blotter, interactive
scenarios and retained risk use the same evaluation. The ordinary gas portfolio
policy isolates base holdings with missing marks, an elapsed determination,
conflicting facts or model-input refusals. A failure caused by a requested
scenario refuses the run. Native futures keep their all-valued requirement.

| Refusal | Meaning |
| --- | --- |
| `exercise_right` | Malformed right, overlapping memberships or invalid retained group terms |
| `exercise_model_scope` | Group outside the selected model's geometry, price or discounting scope |
| `exercise_marks` | A required explicit mark is missing or unusable |
| `exercise_determination` | A physical group reached its exercise instant without an eligible decision |
| `exercise_election` | Malformed or inapplicable election |
| `exercise_election_conflict` | The expected election head is stale |
| `exercise_conflict` | A covered physical day has a scheduled or allocated delivery without exercise, or a payment's original covered cash row has no exercised day |
| `holder_choice_inputs` | Invalid model inputs |

This build covers gas exercise over ordinary physical and payment Sides.
D450 permits an independent lump-sum premium while refusing a right that names
a charge. Strip exercise across delivery months, discounting, negative modeled
prices, path-dependent and constrained rights, transport/storage optionality,
and a marks editor are outside the profile. D288 listed futures options and
the D431 heat-rate call keep their own contracts.

## Independent pricing entries (D446–D447)

Every valuation route uses the retained Instrument schedule: draft and held
preview, single-trade read, Blotter, interactive risk and queued risk. Each Side
still owns its saved program and lifecycle rows. Physical selected quantities
are prepared once per entry under the explicit business instant and knowledge
cutoff; policy and source relationships are immutable entry terms in identity.
Every entry also retains its exact physical owner in the schedule execution
identity. Its lifecycle quantities and clock come only from that owner, including
contract-quantity payments beside physical entries with different terms.

Payments and fees state a positive `share` and may choose their own inclusive
start/exclusive end within the source's gas-day boundaries. Whole quantity is
the absent part; `band` takes quantity between a lower daily level and an optional
upper level; `shortfall` takes the deficit below its daily level. Levels are
entered in native units and retained as exact ratios of the holding base.
A part needs an independent physical source and no fuel link anywhere in the
composition. Contract-policy sources still require complete contract-labelled
owner days when a payment has a part. Missing facts refuse; no implicit whole
quantity is substituted. Zero after payment retains the overpaid balance.

New floating payments choose point publications, a named daily index or a named
monthly index. Named terms require exactly one eligible curve with that index as
settlement target and exactly one contract/fixing for each payment gas day. The
fixing's series must name that exact contract. A published monthly price can
earn ended days while later days stay unrealized;
before publication those rows are forecasts and accrue nothing. The retired
strict daily convention refuses new booking and remains readable on saved trades.
Final daily billing accepts only whole, share-one, full-source-term fixed cash.

Schedule rows expose taken quantity, `band_v1`/`shortfall_v1` when present,
source quantity, rank and evidence. Entry amounts and any undecided exercise
contributions partition Instrument value on every route. A held preview supplies
explicit `previewTradeId` and `previewContextId`, requires exact saved terms and
current authority, and is not a booking or amendment request.

Physical gas is entered in the [deal ticket](deal-ticket.md) (D281). Its booking is
the independent-side composition below: one to four physical sides with payment and
fee sides. A purchase or sale at one point is the composition of one physical side.

The single-point template and its operations (`gas/preview`, `gas/instructions`,
`gas/book`, `gas/status`, `gas/directory` and `gas/editor`) are removed; no new
single-point trade can be booked, and migration 0156 revokes the runtime's call of
the retired booking entry, `security.book_physical_gas`. `gas/catalog` remains. Stored
single-point trades stay readable: the Blotter values them and the trade read and the
deal ticket open them read-only, all as the one-physical-side composition they are,
from their retained programs. Their v4 command supplied pipeline, zone, delivery location and product;
the backend resolved the valuation mapping. Delivery dates were independent inputs.
Reopening loads their stored executable programs and resolves source metadata from
authorized typed references. It never rebuilds an instrument or schedule for booked
execution. Both profiles value through the existing exact schedule interpreter and
market-source resolver; no second gas arithmetic engine is used.

The stored single-point profile admitted one forward month or a 1–31-day subset, a selected
fixed-offset gas clock and start minute, daily or equal-day period-total fixed
quantities, receive/deliver direction, fixed consideration, an optional fixed
per-unit fee with its own pay/receive direction, an explicit UTC payment instant and explicit no discounting. It
admits no fuel adjustment. The independent-side profile below admits explicit
fixed-quantity fuel links and, under D350, independent daily indexed consideration.
Operational fuel treatment and broader captured clocks remain separately bounded
capabilities.

D338 adds separate [final daily fixed cash determinations](gas-cash-billing.md);
D339 adds [manual delivery operations](gas-delivery-operations.md), including
nomination requests, external acknowledgments, statements and imbalance
agreements. For these earlier profiles, physical completion/title, inventory,
invoice issuance, payment allocation and settlement-aware portfolio valuation
remain unsupported. D360 below adds started-composition measures and recorded
payments; the separate [D401 owned-holdings contract](gas-owned-holdings.md)
covers native retained interests. The common Blotter includes both gas profiles
through the native retained-schedule pricer, with a distinct contractual PV
measure. One immutable gas instrument is one position binding; physical-side
quantities are never summed into a receipt-plus-delivery total. Missing gas
market context produces an explicit unavailable valuation. Started delivery
refuses for the earlier profiles; D360 compositions use the policy below.
Retained terms and daily operational quantities remain readable.

New trade opens the supported product booking workflows in the same panel, including
listed futures options and physical LNG. Physical gas opens the deal ticket, whose
operations delegate to the composition's preview, booking, amendment, status and
idempotent recovery operations; the other forms use their own. A confirmed booking refreshes
the common Blotter and opens its exact position when uniquely identified. A booked
composition's day quantities are recorded in Scheduling → Physical operations →
Delivery; Side schedules remains read-only. PPA lifecycle operations remain native
and open in the same panel. Unknown booking outcomes block replacement requests and
retain the actor-owned command across ticket remounts, including when supplementary
browser storage fails.

Interactive and queued portfolio risk load supported gas profiles, including
future compositions with fixed or indexed consideration, from their retained
programs and use the common `PV` and `MarketFactors` contracts (D287). Shared
factors appear once across gas and other instruments, while exact membership
directions and quantity ratios determine their repriced contributions. Gas-only
scopes are admitted. Migration 0165 supplies read-only worker access with the
runtime history/current-authority rules and an additional lease fence. Missing
inputs refuse explicitly; started delivery refuses for the earlier profiles,
while D360 compositions follow their explicit started-delivery policy below.
Earlier reports retain their original exclusions. Automated closes containing
gas remain explicitly unsupported. Operational quantity revisions do not change
contractual PV or create extra positions.

## Independent sides and fixed quantity links

The deal ticket's Add side offers physical, fixed payment, index payment and fee
sides. The explicit
fixed-cash profile is `fixed_gas_composition_v1`; compositions with an indexed
payment use `indexed_gas_composition_v1`. Both use the existing v4 context convention
and native composition operations through the ticket. A preview and a booking are
held to the gas setup their
context resolves: a request naming another setup, or none where the context resolves
one, refuses with `ticket_input` on the `gas_setup` field; a retried booking still
receives its first outcome.

Admit one to four physical memberships and up to seven cash/fee memberships,
with at most eight memberships in all, including indexed consideration. Receive/deliver belongs
on each physical membership; pay/receive
belongs on each cash membership. There is no trade-wide direction. Each physical
side selects its own PZL/product and future delivery term of 1–1200 operating
days. Each intersecting month requires exact native contract/assignment coverage;
the forecast curve's grid does not divide the physical Side. Independent sides
can have different dates, quantity bases and clocks.
All memberships require the same exact native quantity specification and currency;
there is no implicit physical-unit or FX conversion.

An independent physical side supplies a positive contractual daily quantity or
equal-day period total. A linked side names another physical side and supplies
an explicit no-fuel, gross-less-fraction or gross-divided-by-one-plus-fraction rule.
The link consumes its source's delivered quantity; its own receipt stage is that
source output, and its own delivery stage applies the selected rule. Fuel is
receipt minus delivery. Linked profiles must have matching operating intervals,
product, clock and quantity basis. Cycles, missing sources, duplicate codes and
physical fan-out refuse. Splits require a separately qualified allocation rule.
These links do not prove route continuity, service rights or custody/ownership
semantics for a transportation contract.

Cash and fees select a physical source and its receipt, delivery or positive fuel
stage, their own fixed per-unit price, payment instant and direction. An eligible
configured usage tariff can supply a delivery-based fee price. A tariff cannot
silently price a receipt/fuel stage or overwrite a manual price. Selecting the
fuel stage bills a separately declared charge on that reduction; it does not
change in-kind fuel into cash-paid fuel. Whole-term fixed cash retains its explicit
payment instant without inferred monthly allocation. D450 adds independent
fixed [lump sums and capacity charges](#independent-gas-charges-d450) with
their own quantities and days.

**Index payment** uses the existing `FloatingSide` under
`indexed_gas_composition_v1`. Named pricing has one exact daily or containing-month
index contract and fixing definition per payment gas day, an explicit forecast
selection and additional spread. Point publications retain their own periods. Payments use an explicit next-month UTC day from 1–28 without
holiday adjustment. Physical and payment Sides keep independent prices and signed
contributions. D360 supplies in-term unrealized and accrued-unsettled measures; D447
uses published-as-available pricing for the new named forms. Final indexed
billing and broader cash settlement remain outside this admission.

The first independent physical amount defines the external holding quantity.
Every side retains a normalized one-unit executable schedule; exact positive
membership ratios preserve the native quantities separately from direction.
The side identifiers, quantity source/stage, fuel rule and reduced integer ratios
are stored in `trading.gas_component_terms`, with exact membership, source-slot,
assignment and tariff foreign keys. Final SQL admission independently resolves
the quantity relationships and validates every unit program before atomic
publication. It also retains the original party, source-visibility, effective
revision, idempotency and cross-family command-collision checks.

Fixed links are materialized at booking. Reopening directly loads the retained
unit programs, multiplies each quantity and PV by its stored membership ratio
and the holding amount once, and reports receipt/delivery/fuel reconciliation.
It does not regenerate a Side or recompute link ratios from current setup. Dated
meter attribution uses each booked physical point and setup version. The saved
programs keep contractual quantities. D445/D447 may apply selected owner facts and parts through the shared lifecycle without changing those
programs; fuel-linked profiles keep contract quantities.

Synthetic period-total oracle, with no discounting: receipt 1,000 at mark 3.20,
delivery 980 at mark 3.10 after 2% gross-based fuel, and a payable fee of 0.10 on
delivery. Contributions are +3,200, −3,038 and −98; PV is 64, and fuel is 20.
Changing only the fee stage to receipt makes PV 62. With the alternate divisor
fuel rule, the same sources produce exact PV `3200/51`; no intermediate rounding
is introduced. The shared managed fuel rule also serves the existing gas
quantity foundation.

The ticket preserves exact reviewed commands across uncertain booking replies
and reload, invalidates review after any term/link change, and provides a durable
composed-trade directory with retained daily schedules, price/payment inspection
and independently signed contributions. Common interactive and queued portfolio
risk include supported gas compositions; generated gas closes still refuse.

## Future composition amendments

The main-window Blotter's **Amend** action opens the ordinary ticket under
`future_gas_composition_replace_v1`. Original and replacement delivery must remain
future, with the latest exact binding/revision, retained definition and explicit
reason. Each original and proposed charge must also have its first gas-day
start strictly after the amendment instant. Any recorded payment on an
original charge blocks replacement, including a payment known now with a
later effective instant. Its payment and allocations retain the original
binding. A changed command appends a revision with new owned physical occurrences;
unchanged typed terms return the existing revision. Retries retain their first
outcome, including after a successor. Legacy single-point trades and trades with
daily quantity, service or facility relationships refuse until operational
continuity is qualified. Default booked inspection and separate trade windows
remain read-only. Shared Side inspection exposes selected pricing/lifecycle
facts without adding a started-delivery cash lifecycle.

## Admitted terms and their consumers

| Term | Executable use |
| --- | --- |
| Pipeline, zone, delivery location and product | Validate the versioned physical path and commodity applicability; resolve one exact valuation mapping; retain the full path in side identity and source metadata. Invalid combinations refuse. |
| Gas clock and delivery period | Generate exact operating-day UTC intervals, retain those grids and enforce the pre-delivery boundary. Captured clock rules participate in economic identity. |
| Quantity basis | Admit normalized daily or equal-operating-day period-total quantities; generate exact delivery quantities. Hourly-rate quantities remain supported by the quantity primitive but are not admitted by this Side profile. |
| Fuel convention and fraction | Apply the selected exact receipt-to-delivery formula once; use its net quantity for physical value and validate cash allocations against that quantity. |
| Valuation assignment identity and effective period | Require exactly one assignment per whole gas day, no duplicate keys, missing coverage, overlap or unused assignments. Retain and resolve the complete assignment definition. |
| Market contract, index, quote and optional fixing | Resolve prices using the existing period-price selector under the certified context, including publication eligibility and missing-fixing refusal. |
| Physical-value price adjustment | Add the compatible typed adjustment before multiplying by quantity. It changes physical valuation, not fixed contractual consideration. |
| Cash price, allocation and payment date | Existing FixedSide/FloatingSide schedules calculate and discount consideration and usage charges independently. |
| Membership direction | Reverse delivery and cash contributions once; external quantity scales the normalized result once. |

Pipeline, zone and point are existing typed location records linked by
`ref_data.pipeline_zones` and `ref_data.pipeline_delivery_points`. Commodity
applicability reuses `ref_data.commodity_locations`. These reference relationships
also appear in the reference browser. The Side holds their exact references,
not duplicated location attributes. Direct-meter attribution and preparation of unsubmitted contractual schedules are supported below. Meter measurements, allocations, a complete gas-product
catalog and transport eligibility remain unsupported. Callers supply qualified reference definitions and one
compatible native quote unit; volume-to-energy conversion remains unsupported.

## Physical location and valuation are separate

Pipelines and zones are network roles of the existing versioned location master.
The membership tables establish which zones belong to a pipeline and which
operating points belong to each zone. They contain no trade dates or prices. The separate gas delivery-convention
reference owns the clock and operating-day start for an admitted path/product;
changing a market curve cannot silently change its physical gas day.
They are reusable for pipeline-delivered commodities; ports, terminals and other
physical arrangements need their own qualified relationships when consumed.

A gas valuation mapping links an exact physical path and product to a market
curve and supported adjustment. The curve's market location can differ from the
physical delivery point. Several physical points can share one curve and have
different explicit adjustments. A missing mapping leaves the physical point
visible in reference selection but blocks pricing. The current profile resolves
one native currency from its explicitly selected mapping; multi-currency mapping
selection remains a separate extension.

The ticket presents physical-side fields, separate consideration and optional
usage-fee sections. Product and pipeline filter the admissible zones and points.
Changing them never changes delivery dates. No month is encoded into a physical
location option. The existing one-forward-month admission bound remains explicit;
an independent date range does not imply unsupported multi-month booking.

Buy/sell appears on the physical-side section, not the trade header. The current
purchase/sale template still constructs one physical side and opposing consideration, with a separately directed optional usage fee;
it does not yet support arbitrary additional physical or financial memberships.
The [composition gate](physical-gas-field-contract.md#reference-identifiers-valuation-and-composition)
requires explicit per-member directions, quantity links and transport/path
semantics before expanding this booking contract. Meter observations, allocations and accepted nomination workflows remain unsupported.

Booked inspection shows the retained physical path and exports daily quantities
from the executed `schedule_quantity` roots. It never regenerates their schedule.
Display-only reference revisions must copy network memberships and include them
in the economic-equivalence proof, so a name edit cannot remove or change a path.

## Quantity and lifecycle boundaries

`PhysicalGasSide` requires a one-unit receipt profile. Its external quantity basis
is interpreted by the explicit daily or period-total convention. This prevents a
booked magnitude from being embedded and multiplied again accidentally.

`PhysicalGasInstrument` admits one dated cash period per consideration/usage
component. Each must cover the complete physical term in the same native unit
and currency, with an allocation exactly equal to the generated net delivery
quantity. This is a fixed contractual quantity relationship validated at
construction, not a dynamic operational quantity port. Future actuals must not
be implemented by silently changing these immutable cash allocations.

The admitted physical measure is future delivery market value, with each day's
value date at the exclusive end of its gas day. It is not a payable cash flow.
Its executable roots distinguish `physical_value` from `contractual_amount` and
its evaluated contribution kind is Delivery. A program compiled with the
legacy lifecycle refuses with `gas_delivery_lifecycle` once any admitted delivery
starts. New gas compositions compile `whole_gas_day_delivery_v1`, under which an
ended gas day leaves the unrealized measure and its fixed cash accrues in the
realized unsettled measure until recorded payments clear it (D360). A gas day not
yet ended values its physical leg at the location curve's own mark even after its
payment index has published; a missing mark refuses, and the fixing never stands
in for it. Neither
lifecycle manufactures actual delivery, retirement, inventory or overdue physical
treatment.

The supplied discount operation is explicit. A value-date change can affect
discounted physical value independently of cash payment dates. Future due cash
continues to obey its existing payment-side policy; this profile does not create
a new physical settlement policy or enroll a trade in swap settlement.

## Execution and identity

`Side.GenerateSchedule()` is parameter-only. Draft schedules compile once on
first executable use and retain immutable typed instructions, sources, exact
grids and named outputs. Serialized executable bytes can be loaded and run
without regenerating Side parameters. Gas booking/publication remains a separate
integration gate, including independent database verification of the new binding
kinds and family.

`SideValuationContext.ForPhysicalGas` requires an explicit certified unshocked
base, a compatible scenario, discounting and qualified assignment definitions.
Source evaluation checks the same model, snapshot inputs and resolved context.
It verifies the complete lookup against its assignment, including fixing time;
another metadata array cannot reinterpret when a price becomes fixed.

Assignments form a separate pricing-binding identity. Instrument identity
retains contractual composition while instrument pricing identity aggregates
every ordered side pricing binding. The identity map refuses reuse of equal
contractual economics with different assignment bindings. No global cache stores
resolved prices, quantity states, authorization or trade lifecycle.

## Numerical and refusal checks

The synthetic one-day purchase, DF=1 and external quantity 1,000, gives:

| Component | Amount |
| --- | ---: |
| Delivery at 3.20 per native unit | +3,200 |
| Fixed consideration at 3.00 | −3,000 |
| Usage charge at 0.10 | −100 |
| Combined pre-delivery PV | +100 |

A +0.10 physical mark shock gives PV +200; a −0.10 shock gives zero. Reversing
direction gives −100 at the base mark. With 2% of gross retained as fuel, 980
units deliver and matched cash gives PV +98. Under the alternate net formula,
the per-unit physical value remains exactly 160/51 at a 3.20 mark. A two-day
period-total profile retains one-half quantity per day, rather than duplicating
the total.

The managed suite also covers independent adjustments, assignment/quantity
mismatches, gaps/duplicates, changed pricing identity, changed model/snapshot,
started delivery, cancellation, final fixing shock neutrality, missing due
fixings and modified fixing-time metadata. The existing fixed/floating lookup
compiler is shared with gas and retains existing family behavior.

## Persistence and current authority

`market_data.gas_valuation_assignments` retains the exact physical path, product,
valuation curve and price adjustment. `ref_data.gas_delivery_conventions` owns the
reference clock and gas-day start independently of that market mapping. Every
field is consumed during metadata resolution and independently revalidated by
final database admission. Typed gas instrument and membership records own the
contract dates, quantity basis, fixed cash prices and payment date. A trade
revision owns its book, quantity and direction. Common booking facts retain the
server-owned human identity and UTC booking date.

Final admission compares every allowed row, edge and output root with the
selected terms. It validates exact rational values by cross-multiplication,
complete native gas-day grids, exact header revisions, the forward month,
quote/index/fixing metadata and canonical assignment. Unknown economic fields
reject. Program bytes must separately match the normalized relational schedule.
Private staging is not a booked position. All sides publish in the trade's
transaction or none do. Retries preserve the original operation and payload;
changed terms or ownership conflict. Recovery receipts are actor-bound. The
browser retains an uncertain request across reloads and reconciles status before
clearing it.

The next gates are shared blotter/portfolio integration, operational quantity
ports and physical completion/settlement policies. The [field contract](physical-gas-field-contract.md)
and [delivery plan](physical-commodity-test-plan.md) retain unsupported terms as
requirements, not dormant Side fields.

## Shared physical pricing configuration

`market_data.gas_valuation_groups` gives a pipeline/product group one curve and
one explicit price adder in that curve's quote. Each point assignment names its
group and a further point adder. The total adder is generated in PostgreSQL;
a complete foreign key pins the group, pipeline, product, curve and group adder.
Duplicated linkage columns cannot diverge. There is no implicit nearest-zone
match or pipeline fallback. Explicit assignment allows several points in one
zone to share a group, points in different zones to share it, and another
pipeline to have a separate group over the same curve.

For the admitted additive profile:

```
physical mark = selected curve value + group price adder + point price adder
physical contribution = signed contractual delivery quantity * physical mark
```

Group and point adjustments change physical valuation only. They do not issue a
payment, change fixed consideration, reduce physical volume or select a tariff.
Percentage valuation adjustments remain unsupported in this profile. Physical
fuel remains a separate quantity rule; the existing managed fuel foundation does
not enable it in booking. Configuration must distinguish market-location basis
from separately owed service costs rather than entering one expense twice.

Scheduling → Facilities & delivery → Delivery setup → Natural gas provides economic authoring for the admitted
profile. The general reference browser retains the imported master relationships;
a published setup's own point and meter relationships are inspected in the gas
setup editor and consumed by its pinned trades. This is separate from the
reference browser's display-name editor. No unrestricted row-edit endpoint is
introduced.

## Meter-attributed contractual schedules

`ref_data.gas_meter_assignments` maps an exact path/product to a pipeline-scoped
external identifier and native quantity specification over half-open operating
dates. The direct-meter profile rejects overlapping assignments to a point,
concurrent reuse of the external identifier within a pipeline, and a quantity
specification belonging to another commodity. Authors serialize under read
committed isolation; other isolation levels refuse before insertion. Grouped
meters and allocation weights are not admitted by this direct-meter profile.

Meter identity is available before flow. Evaluated daily schedule views join it
by operating day under the schedule's exact reference revision. Their quantities
are explicitly labeled `contractual`. Booked views load their quantities from
retained executable roots; meter attribution neither regenerates the schedule
nor replaces its contractual quantities.

Preparing an unsubmitted contractual meter schedule (`gas/instructions`) belonged
to the single-point template and was removed with it. Physical MTM remains
available at an unmetered commercial point. Actual readings and corrected
allocations will be separate observations with purpose-specific eligibility; they
cannot overwrite the immutable Side, meter reference history or stored contractual
schedule.

## Configured usage tariffs

`market_data.gas_usage_tariffs` pins a pipeline, product, exact quote, nonnegative
rate and half-open delivery validity period. The current installed charge basis
is contractual delivery quantity. Selecting a tariff resolves the usage-fee
Side's price; supplying both a tariff and a manual fee refuses. The entire trade
period must be covered without proration or rate fallback.

The v4 command explicitly supplies nullable `usageFee`, `usageTariffCode` and
`usageFeeDirection`. An active fee requires pay or receive; no fee requires a
null direction. Physical direction does not determine that fee direction.
Final database admission independently resolves the tariff, checks the executable
cash program against its rate and stores the exact tariff code and fee sign.
Book/reopen preserves that source and sign. Tariff changes require a new version;
existing bookings retain their pinned version.

This is a bounded per-unit charge consumer, not a complete transport tariff
engine. Reservation/capacity charges, seasonal splits, service priorities,
route-specific rates, tiers and actual-volume billing require additional admitted
profiles. A pipeline-level tariff does not itself prove a transportation right.

## Draft, validate and publish gas delivery setup

The local **Gas delivery setup** view under Scheduling → Facilities & delivery edits a complete snapshot of
point relationships, shared valuation groups, meter assignments and per-unit
tariffs. Select existing master locations, products, clocks, quantities, quotes
and curves. Creating a new location-master identity is a separate reference-data
workflow. Master records and market observations are not cloned or rebound by
publishing a gas setup.

1. A market-definition editor starts from the current published setup or imported
   definitions, changes records and saves an immutable draft.
2. Select a supported delivery period and validate the saved
   version. Preview shows each point's group, curve, index, quote, separate price
   adjustments and number of operating days without a meter assignment.
3. A market-definition approver opens that exact saved version, validates it and
   explicitly publishes. Final SQL admission revalidates the full snapshot.
4. New tickets consume the latest visible published setup for the exact base
   bundle. Each v4 booking explicitly pins nullable `gasSetupId`; null identifies
   the imported setup. Reopening retains the booked setup, physical clock,
   assignments, tariff source and meter lineage.

`market_data.gas_setup_versions` owns the immutable draft payload and published
version, with exact market-definition and reference-master foreign keys. Published
points and meters have typed setup-specific relationships; groups, assignments
and tariffs retain their exact setup foreign keys. Published relationships are
never resolved through a current location override. Point and zone membership
checks reject contradictory parents; group/curve/product compatibility, exact
rates, meter overlaps/identifier collisions and tariff validity are checked
before publication. An intentionally unpriced point or unmetered commercial
point can remain in the setup. Missing pricing blocks MTM; missing compatible
meter coverage blocks instruction preparation, not contractual valuation.

Each saved draft retains its base setup. Publishing a draft based on a superseded
head refuses rather than merging changes implicitly. Save/publish requests have
actor-bound idempotent outcomes; the browser retains the original command through
uncertain responses and reload. Unsaved edits have a leave prompt. Changing a
preview date or a definition invalidates its previous review.

Setup publication uses its own serialized, commit-certified knowledge stream
under the shared context-capture gate. A retained context excludes later setup
publications, including transactions that were uncommitted when it was captured.
Risk workers retain their existing lease and pinned-context gate. Setup authoring
is unavailable in historical mode. Publication order selects the current setup;
future-dated whole-setup activation is not part of this profile. Meter and tariff
validity remains explicitly dated by operating period.

For authored meters, exported attribution identifies `(market catalog, setup ID,
meter code)`; imported meters retain their original reference key. Reusing a
meter code in a successor setup cannot disguise a changed external identifier.
Operational measurements and allocations remain separate capabilities.

## Local verification

The complete managed runner passes 653 reported core checks, including gas
quantity, price-shock, fixing and retained-program cases. The focused stock
PostgreSQL check is `tools/run_preview.py --physical-gas-check` with the installed
`--pg-bin` and `--dotnet` paths. It books the one-physical-side purchase through the
composition operations and verifies the synthetic purchase oracle: physical value
+3200, fixed consideration −3000, usage fee −100, total PV +100. It also verifies
independent physical and fee directions, daily/period-total quantities, the
optional fee, booking/reopen parity, idempotent retry, historical exclusion and
read-only authority. The PZL checks also prove that the same location prices
January and February, changing the configured physical zone changes PV,
mismatched paths and unpriced points refuse, a configured tariff prices PV80 and
refuses outside its period or pipeline, and the delivered point may differ from the
market location. A non-midnight gas day is exercised through preview. The reference
checks verify shared group pricing, independent point/pipeline adders, meter
overlap and identifier collisions, wrong commodity units, inconsistent group
linkage, isolation refusal and display-clone identity preservation; additional
permissive policies cannot bypass current authority. Add `--browser-check --browser`
to exercise the deal ticket's gas entry: one gas entry in New trade, side-level
direction, reference points filtered by zone including unpriced masters, and a fee's
proposed tariff. Add `--gas-setup-check` to qualify economic authoring, configured
point/clock changes, historical setup visibility, tariff pricing and pre-publication
booked price/meter preservation. These are bounded local checks, not production or
complete gas lifecycle qualification.

Stored single-point trades are read, not booked. The mapping of their retained terms
to a one-physical-side draft is checked without a database (`--gas-ticket`), and
`--deal-ticket-check` writes one stored single-point booking through the owner
connection, check-only, from a booked composition's retained programs, then opens
it read-only through `tickets/trade` and compares its draft and schedule with a
ticket preview of those terms.

For a held gas composition whose programs all select the started-delivery
lifecycles, `tickets/trade` also returns `measures` (`unrealized`,
`realizedUnsettled`, their labelled `total`, `label`, `discounting`, and undiscounted
`expectedCash` by due date, which is not part of the total) and the `currency` of
those figures; both are `null` or absent for any other trade.

The multi-side integration runner is `tools/run_preview.py --gas-composition-check`
with the same explicit SDK/PostgreSQL paths. Add `--browser-check --browser` for
the complete ticket and uncertain-response recovery, and `--gas-setup-check` for
publication/rebinding regressions. Independent cases cover the PV64 and alternate
fuel oracles, receipt/delivery fee stages, independent later-month daily quantities,
invalid links, exact replay and direct retained-program reopening. Managed
scenario checks prove a +0.10 common-curve shock changes PV64 to66 because the
net physical quantity is20; contractual fixed fees remain unchanged.


The [daily delivery quantity workflow](physical-gas-daily-quantities.md) now records
separate trade-owned operational revisions and resolves native quantity inputs
from retained gas grids. Contract-quantity memberships keep their existing values.
D445 separately opts a physical membership into `delivery_schedule_v1`: the
shared in-term lifecycle uses selected scheduled/allocated quantities for physical
value, payment and usage-fee accrual, and risk. Retained Side schedules stay
immutable. See the [quantity valuation contract](physical-gas-daily-quantities.md#selected-quantity-valuation-d445).

The integration checkpoint preserves dated-contract observation sources and the
mandatory published-fixing admission rules in the shared schedule runtime. Local
verification passed 651 managed core checks, 925 web checks and 44 physical gas
browser checks. The populated prior-schema upgrade check applies migrations
0142–0146 and proves existing reference, market, book and party rows remain
unchanged. That check does not establish replay of an existing financial trade
or its program history across this upgrade. The shared-workflow increment below
supersedes the earlier gas-bearing workspace refusal.

Reference catalog qualification includes 208 exact-version projection and label
checks. Path tables retain their composite primary-key ordering; gas product
fields resolve commodity labels while futures product fields retain their own
reference contract.

## Independent gas market selection

An installation may explicitly configure `gasMarket` with one sealed definition
revision distinct from the primary market and dated observation/contract selections. Gas setup authoring, physical
booking previews and retained schedule preparation use that revision and its exact
reference version. This does not replace the primary financial or power market,
redirect collection sources, or copy their operational series.

Without this configuration, gas uses the primary market as before. With it, a
missing or ambiguous dated selection refuses; primary price inputs are never a
fallback. The selected certified knowledge cutoff and valuation instant still
bind every read. A financial close does not certify the independent gas inputs.
Explicit gas pins remain exact; a view already carrying the latest-correction
policy resolves corrections only inside its certified cutoff. Daily quantity
records continue to read their original retained contractual programs.

Reopening a booked gas trade uses its visible retained definition header. Trades
booked on the primary market continue to use their original source after gas is
configured separately. Older independent gas revisions must retain their dated source selections in
`retainedGasMarkets`. Booked reads select that source by the authorized instrument
header, including its exact reference revision. Missing or ambiguous retained
sources refuse explicitly; changing configuration does not rebook a trade.


## Shared trading workflow qualification

The common workflow passed 932 web checks, managed API checks and disposable
PostgreSQL integration checks. The mixed fixture covers futures, financial swaps,
heat-rate swaps, PPAs and both gas booking profiles. Ordinary, streamed and exact
individual reads preserve retained position identity and native value measures.
A real leased worker reproduces mixed financial risk and exact gas exclusions;
a retained manifest missing those exclusions fails instead of repairing itself.

Browser qualification passed 18 shared-workflow checks at a 1440 px desktop,
45 gas booking/quantity/setup checks and 39 PPA booking/lifecycle/inspection
checks, including 390 px PPA layout. A lost accepted gas response with browser
storage unavailable survives editor detach/reopen and status reconciliation
without another booking. Closing a ticket clears its browser page-leave warning.
Nominations remain operational revisions of the same
trade, with contractual quantities unchanged. Exact PPA market frame selection
remains explicit when the desk instant cannot uniquely identify one bundle;
cancelled loads cannot reopen an old trade after an editor reset.

Run `tools/run_preview.py` with
`--unified-trading-check --browser-check`, `--gas-composition-check --browser-check`
and `--ppa-bookings-check --browser-check`, supplying the local PostgreSQL,
managed SDK and browser paths required by the preview tool. These checks do not
qualify gas scenario risk, operational PV or physical settlement.
