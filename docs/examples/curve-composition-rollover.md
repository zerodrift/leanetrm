# Separate source curves and rollover: proposed next example

Status: acceptance specification for automatic rollover in the mixed-period
browser example. D331 subsequently implemented an explicit whole-delivery
[splice](../custom-curves.md) and the
[indexed physical gas example](../indexed-physical-gas-example.md) composes separate
daily/monthly sources. Automatic eligibility and month-end window resolution
remain proposed; the explicit-date splice does not select those rules.
All names, dates, prices and milestone times below are synthetic.

## Source ownership and the composed view

The current example owns daily and monthly observations in one direct curve.
The proposed replacement has a daily-only source curve and a monthly-only source
curve, with a composed curve selecting from them. Apply this structure separately
to benchmarks and location basis inputs; location outright curves still declare
their base-plus-spread dependencies.

The composition operation is a splice, not addition or an implicit conversion:

```text
Daily benchmark + Daily location basis -> Daily location outright
Monthly benchmark + Monthly location basis -> Monthly location outright
Daily location outright + Monthly location outright -> select dated constituents
```

The last line denotes source selection, not price arithmetic. Each output retains
the exact source curve, dated contract, observation lineage and definition pins.
The composed curve creates no independently observed copies or new risk factors.
It must not independently mark or override selected prices. A source change flows
through its selected dependencies; an unrelated source remains independent.

Validate compatible commodity, quote units/currency, location and delivery terms.
Each selected output has one source. Reject unexplained overlap, missing coverage,
ambiguous precedence or missing eligible observations. No fallback from daily to
monthly, monthly to daily, an all-in quote to a spread, or one dated contract to
another is implied. Different settlement conventions need explicit support before
combining their outputs; display composition does not unify settlement lifecycles.

## Individual days and BALMO

Preserve individual operating-day identities for delivery, quantities, fixings,
valuation and history. A daily row does not by itself imply an independently
marked/shockable factor: any shared quote or explicit shaping relationship is
part of its construction. A weekend delivery package can cover several operating
dates; its trading/quotation grouping does not erase those delivery dates.

BALMO can name a relative period selector over remaining delivery days. Resolving
that selector retains its exact start/end operating dates, clock/calendar,
inclusion and cutoff rules, selected source contracts and rule version. Tomorrow's
selection does not modify yesterday's dated contracts or receipts.

This is different from a directly quoted BALMO contract over one fixed period.
That quote retains its own dated identity, benchmark and observations. It cannot
supply individual daily prices without an explicit qualified shaping rule.
Likewise, showing a single summary price over daily rows requires a specified
aggregation/quantity-weighting rule. A group heading can say BALMO without
inventing such a scalar price.

The daily window's month-end rule remains unresolved. Two possible policies for
a January 31 context whose next delivery day is February 1 are:

| Selection rule | Daily constituents | Monthly tail |
| --- | --- | --- |
| Month containing the next delivery day | February 1–28 | March onward |
| Remainder of current delivery month | None | February onward |

Neither choice defines all real-market BALMO conventions. Day-ahead, next
operating day and the days following a traded day-ahead package can be different
selectors. Their calendar, package, market-clock and holiday rules must be
explicit; they are not user display-time-zone preferences.

## Three separate transitions

1. **Prompt selection changes.** A relative monthly label resolves to another
   eligible dated contract under a selected rule. Both dated contracts can have
   existed and traded beforehand. Keep their distinct histories and selected
   identity in every captured context.
2. **An eligibility horizon extends.** A newly listed futures series, or a newly
   configured forward bucket, becomes usable under its own retained definition
   and milestones. Knowing its terms before listing does not make it tradable or
   provide a price. Unknown listing/eligibility information is not permission to
   infer it from another contract's expiry.
3. **Delivery/fixing progresses.** A daily or monthly forward retains its delivery
   and settlement terms as marks cease to be eligible and fixings become known.
   A displayed monthly bucket can leave the composed view while its history and
   booked obligations remain. It does not become a set of newly priced daily
   contracts merely because the view selects daily sources instead.

A roll does not close, replace or rebook a Trade. Trade lifecycle, delivery,
variation margin and payment remain independent explicit operations. Ordinary
risk does not shock an eligible frozen final as though it were a live quote.

## Concrete rollover checks to implement

Use separate daily/monthly sources with visibly distinct synthetic prices and
capture instants immediately before, at and after every chosen boundary.

| Boundary | Before | At/after | Required evidence |
| --- | --- | --- | --- |
| February future ends trading January 28 at 19:30 UTC | February and March are already listed; the selected first-live-month rule points to February | The same rule points to the already-existing March contract | No March creation, price copy, history splice or trade mutation |
| February final is published at 19:35 and recorded at 19:36 | The final cannot satisfy an earlier publication/knowledge context | An explicitly eligible final can be pinned for a contractually permitted use | No fallback to March while waiting; distinguish exact-pin replay from qualified historical selection |
| April future first lists January 29 at 10:00 UTC | Its retained listing terms may already be known, but it is not eligible for live selection | It can enter the eligible horizon if its definition and price inputs are available | New eligibility is separate from the prior day's prompt roll; missing marks still fail |
| A daily operating period reaches its selected mark cutoff | That exact dated daily mark is eligible under the synthetic rule | Its later view/fixing behavior follows explicit policy | No reuse of the next day's price or rewriting of old selections |
| January/February daily-window boundary | The selected composition consumes the appropriate daily/monthly source contracts | It recomputes the source selection under the chosen window rule | No missing/duplicate delivery coverage; source contract/quote identities survive the switch |
| A new far-dated forward bucket is configured or becomes eligible | No configured executable bucket/price is assumed | Exact definition and observations are required | No exchange listing rule is imposed on an OTC forward |

The synthetic first-live-month rule explicitly selects the exact `last_trading`
milestone as its expiry-based roll boundary, with separately retained listing
eligibility. Missing that selected milestone fails; final settlement, publication
or delivery events cannot substitute. No automatic exchange calendar
implementation is implied. The actual
financial terms are supplied schedule data. This current-contract reference
advances at recorded expiry; an early-roll reference
would be a separate explicitly selected rule. See
[reusable schedules and contract sequences](../schedules-and-contract-sequences.md)
for materialized/imported dates, optional generators and agent maintenance.

Also prove that source-order changes do not change the splice, that missing daily
marks cannot be hidden by available monthly/BALMO marks, and that a corrected
source observation only changes newly selected receipts. Price histories and
future VaR samples must follow the same dated contracts; an alias roll can produce
a price jump that is not a return on one unchanged asset. Any continuous analytical
history or roll adjustment needs its own explicit convention.

This example qualifies curve inputs/selections first. It does not claim that
futures cash/variation margin, physical delivery, fixing aggregation or instrument
PV is implemented by a browser rollover demonstration.
