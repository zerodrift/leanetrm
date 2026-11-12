# Physical power curves and hourly settlement

D376–D381 implement a bounded forward-to-actual power valuation path. Prices
are synthetic in the executable examples; delivery dates, time zones, TOU masks
and averaging conventions remain explicit. The [booked close comparison](power-booked-roll.md)
records the full-month peak and shaped ATC examples.

## Supported behavior

| Area | Current support | Boundary |
| --- | --- | --- |
| Product forwards | Independent peak/off-peak futures-plus-basis curves using declared native dependencies | An outright mark creates no inferred futures exposure |
| Mixed tenor | A product's daily section splices into monthly contracts at a whole-month boundary | Explicit cutover; no inferred partial-month mark |
| Location lookup | One published power output composes the product curves and supplies the physical location assignment | Exact curve/output and activation versions; ambiguous assignments refuse |
| Trade products | All hours, peak and off-peak select the Side's delivery hours on the exact published TOU/calendar | Daily construction products and the trade's delivery product remain distinct |
| Firm hourly quantities | A profile retains 24 local-hour MW factors; linked cash consumes the same exact MWh | Nonnegative, nonzero aggregate, hourly all-hours profile; no automatic normalization |
| Hourly price shape | Explicit additive offsets calibrated over each full native daily 1x16/1x8 block | Monthly sources remain flat; no monthly shaping statistic is inferred |
| Captured prices | Curve-owned PJM hourly DA/RT source with exact publication/index/location/units/clock | Captured prices are not an assertion of official finality |
| DA replay | Explicit normal prior-day 13:30 Eastern release rule | Modeled availability; real capture and correction knowledge times remain unchanged |
| Actual-only history | An activation may enroll a month-start prefix before its forward domain | Selected prefix hours require captured actuals and have no forward dependency |
| Booking and valuation | Future contracts and explicit bounded in-term fixed physical admissions use native Instrument/Side PV | In-term profile is one complete active month with the declared payment rules |
| Close comparison | Fixed contractual value change decomposes into fixing, forward marks and capture corrections | Unchanged holding/model, two exact ordered recorded closes and certified cutoffs, before payment |
| Risk | Existing immutable base/up/down scenarios revalue native Sides and their declared futures/basis dependencies | Graph worker enqueue/replay remains explicitly fenced |

## Physical mapping and graph composition

```text
Physical Side location + delivery product
    -> physical mapper and selected location activation
    -> exact published location curve/output
    -> product/native-contract ownership for each requested hour
    -> forward price or eligible captured LMP
    -> native Side PV using contractual hourly MWh
```

The physical Side retains location, delivery product and quantity terms. DA/RT
comes from the one resolved curve/output binding; it is not a separate Side
market selector. Simultaneous DA/RT assignments for one location remain outside
this admission. The location curve owns
its physical settlement publication, index, captured-source binding and
eligibility policy. There is no separate Side settlement override. An alternate
requires a distinct supported physical/pricing combination and its own
assignment; an invented ISO node or duplicate ambiguous assignment is not a
pricing selector. Existing gas/crude mapping and independent financial Side
contracts retain their behavior.

Product-specific curves remain useful on their own. Each futures-plus-basis
curve uses the ordinary arithmetic graph and exact contract mappings. Published
product splices may feed a `saved_product_forwards_v1` output. The lookup's graph
edges retain their ordinary dependency meaning; no conditional edge type or
trade-owned dictionary selects prices.

The bounded hourly output admits flat native monthly peak/off-peak branches,
daily native 16/8 branches, and explicit whole-month cutovers. Every demanded
hour has exactly one owner. A daily 1x16 source includes its own daily hours on
weekends and holidays; a weekday peak trade still consumes only its independently
selected weekday peak hours. No weekend price branch is added on top of an
already complete off-peak complement.

## Dates, grids and price statistics

Delivery periods, source expiry, publication availability, valuation time and
knowledge time are separate inputs. Native clocks generate half-open UTC
intervals with local operating labels and repeated-hour occurrences. Missing
spring hours are absent; repeated autumn hours have distinct UTC identities.
Exact holiday and TOU versions determine membership rather than labels such as
`pk`, `offpk` or `ATC`.

A workhorse example begins daily delivery on December 19, 2020 and switches to
monthly contracts on March 1, 2021. This uses explicit daily delivery periods for the
December remainder, January and February; it does not reinterpret a monthly
contract as a remaining-month quote. The cutover is selected explicitly. The
historical demo extrapolates current daily product patterns; it does not claim
that these exact contracts were listed historically.

Daily and monthly marks can have different statistics. The current daily
additive shape preserves each native block's duration-weighted mean. ICE monthly
off-peak uses an average of daily averages, which is generally different from a
pooled-hour mean. Monthly shaping therefore remains outside this admission.
The public specifications for [daily peak](https://www.ice.com/api/productguide/spec/6590471/pdf),
[daily off-peak](https://www.ice.com/api/productguide/spec/6590503/pdf) and
[monthly off-peak](https://www.ice.com/api/productguide/spec/6590428/pdf) describe
those product conventions. No external price dataset is distributed.

## Quantity shape and price shape

`local_hour_contractual_mw_factors_v1` retains 24 factors on an exact immutable
physical Side. The factors scale its one-MW contract basis; they are firm
quantities rather than capacity-factor forecasts. The existing schedule
compiler emits native quantity literals and the linked consideration Side uses
the same MWh. Both occurrences of a repeated local hour receive that hour's
factor. Legacy profiles without factors preserve their prior economic identity.

`daily_native_block_additive_v1` instead retains one exact additive interval
series on the curve output. For a block mark F and raw offsets s, the existing
calibration computes `price[h] = F + s[h] - duration_mean(s)`. This mean is
independent of the consuming trade's quantities. The saved output validates the
complete exact daily branch set, quote, references, clock, cadence and series
kind; incompatible or hidden sources refuse publication and read admission.

For 2 MW from 07:00–15:00 Eastern and 1 MW otherwise, raw offsets of +4 in
those eight hours and -4 otherwise give mark+4/mark-4 within daily 1x16 and a
flat daily 1x8 price. December contains 744 intervals and 992 MWh. A flat 1 MW
consumer has no price-shape premium. A nonuniform quantity can have a premium;
mark preservation does not establish unique or market-replicable hourly prices.

## Forward-to-actual resolution

`power_captured_hourly_v1` requires matching publication/index, PJM location,
DA/RT statistic, exact reference bundle, hourly clock and quote unit. The curve
replaces an eligible interval's complete forward price with its captured LMP;
it does not add the LMP to futures or basis. Missing selected ended hours refuse.
Unselected product hours require neither captures nor live marks.

Under `flat_native_mark_replace_actual_v1`, unfixed live hours keep their existing
forecast. A partially fixed shaped block keeps its original full-block
calibration and still requires every calibration cell. A fully fixed block,
actual-only prefix or monthly-only request needs no daily shape cells. An
expired anchor with unfixed hours refuses; there is no frozen-price carry or
whole-period residual recalibration.

The selected captured-at-valuation policy and
`pjm_da_normal_release_replay_v1` are distinct. Replay models normal DA release
at 13:30 civil Eastern on the preceding day, including DST. It selects only
released operating days and the capture revision visible at the explicit
certified cutoff. It does not backdate capture knowledge, assert an observed
release or make revisions final. Later corrections remain new visible facts.

An actual-only prefix belongs to an exact activation revision. A fresh
activation may carry the unchanged enrollment only from its immediate
predecessor after validating the new output. Retries return the first outcome;
incompatible outputs or changed prefix facts refuse atomically.

## Qualification and remaining work

Native checks cover product partitions, sparse demand, genuine 23/25-hour days,
quantity/linked-cash conservation, independent numerical oracles, daily shape
calibration, partial and complete fixing, and shared base/up/down Side PV.
Owned HTTP checks cover publication/source compatibility, saved graph references,
booking/retry, immutable hourly profiles, corrections and historical replay.
The [close comparison](power-booked-roll.md) records additional populated preview
qualification; that is not a production finality or accounting claim.

Still separate: monthly price-shaping statistics, nested overlapping daily/monthly
quote constraints, residual recalibration, subhourly graph products, an hourly
quantity-profile authoring screen, observed ISO release/finality integration,
graph worker custody, cash/invoices, physical accounting and futures margin.
The proposed affine owner/gain/offset optimization has not been qualified or
benchmarked. This implementation claims no new latency or throughput result.
