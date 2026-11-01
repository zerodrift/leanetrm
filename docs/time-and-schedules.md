# Time, delivery calendars and TOU

Status: baseline approved for implementation and publication on 2026-10-16.
These are baseline contracts, not a claim that every combination is implemented.
Bounded clock/TOU publication and MW integration are implemented in the
[power preview](power-preview.md); retained schedule execution and its remaining
integration limits are recorded in [whole-term side schedules](side-schedules.md).
Historical selection is specified in [context.md](context.md).
Reusable schedule rows, optional deterministic generators and expiry-based
contract selectors are specified in
[schedules-and-contract-sequences.md](schedules-and-contract-sequences.md).

## 1. Instants, dates and clock rules

Persist actual instants on the UTC timeline: knowledge/effective endpoints,
observations, delivery interval endpoints, payments, lifecycle actions and audit
timestamps. APIs emit UTC timestamps with an explicit `Z`; accepted offset-bearing
inputs normalize to the same instant. Database session or server time zones must
not change identity, comparisons or calculations.

A business date or recurring wall-clock time is not an instant. Operating dates,
holiday dates and contractual date-only fields remain dates. A TOU rule such as
weekdays 07:00 to 23:00 remains a local recurring rule with an explicit clock.
Do not convert those source fields to arbitrary UTC midnights or fixed UTC hours.

Keep three independent conventions:

| Convention | Purpose |
| --- | --- |
| User display zone | Render actual instants for the current user |
| Contract/market clock | Determine delivery days, TOU membership and contractual boundaries |
| Source clock and labeling | Interpret imported operating dates, hour labels and observations |

One market can use different conventions for different products, rules and feeds.
Do not infer a clock from the operator, location, browser or database connection.

## 2. Versioned clock definitions and TOU rules

A clock definition supports a regional IANA time zone with its retained rule
version, or an explicit fixed UTC offset. For example, a DST-following regional
clock and a fixed UTC-05:00 clock are distinct definitions even if their offsets
match in winter. Abbreviations such as EST or CST alone are insufficient input.
Resolve any source abbreviation through an explicit versioned adapter mapping.

Each TOU definition retains the applicable:

- Clock definition/version, local time windows and inclusive/exclusive boundaries.
- Weekday/season predicates and holiday calendar/version.
- Operating-day boundary and the rule assigning intervals to an operating date.
- Delivery interval resolution and hour-beginning/hour-ending label convention.
- Missing/repeated-time policy, including any first/second occurrence distinctions.

Some definitions combine predicates under different clocks, such as a fixed-clock
operating date with a regional-clock peak window. Each predicate must identify its
clock and date basis explicitly; a single global timezone field is insufficient
for such a definition. Reject unsupported combinations rather than guessing.

Side.GenerateSchedule consumes these pinned rules and produces concrete half-open
UTC intervals `[start, end)`. Retain rule provenance, operating dates and any
contractual labels alongside those interval identities. Generated schedule
artifacts can be reused; their source definition remains the economic authority.
UTC-authored TOU rules are supported when the contract actually specifies UTC.

## 3. DST, quantities and interval identity

A DST-following clock can produce a 23-hour or 25-hour operating day. A full
midnight-to-midnight day under an unchanged fixed offset has 24 elapsed hours.
The applicable market/contract rule determines membership of skipped and repeated
wall-clock hours. Do not silently manufacture a missing delivery hour or collapse
two repeated hours into one. Different offsets/UTC intervals distinguish repeated
occurrences even if their local labels match.

For example, continuous 1 MW delivery over a full day containing a one-hour DST
transition produces 23 MWh or 25 MWh as appropriate. Integrate rates using actual
interval duration. A total-period MWh quantity follows its explicit distribution
rule and must not be multiplied by the hour count again. Quantity-basis changes
must not enter schedule generation as implicit normalization.

When a user enters a single local timestamp, reject nonexistent times and require
an offset or explicit occurrence selection for ambiguous times. Recurring TOU
rules instead follow their declared market policy; this is not an instruction to
reject every schedule that crosses a DST transition.

Imported data retains the original source labels, clock mapping and duplicate
hour indicator where supplied. Resolve them to unambiguous UTC intervals before
joining prices, schedules or quantities. Bare operating-date/hour labels cannot
identify a repeated hour without additional source semantics. Do not guess its
occurrence or join unrelated products solely because timestamps match.

If a TOU boundary cuts through a delivery or pricing interval, split the interval,
apply an explicit supported contractual allocation rule, or reject the case.
Do not classify the whole interval from its start time or hour-ending label alone.
When resolutions differ, any price aggregation uses the declared duration/quantity
weighting and preserves energy and cashflow totals; an unweighted average is not
an implicit conversion rule.

## 4. Presentation, history and reuse

Ordinary timestamps display in the user's selected time zone with a visible zone
and offset where needed. Market schedules also retain a clearly labeled market
clock view with operating date and contractual interval labels. A user-zone
conversion must not relabel the contractual operating date or hour-ending code.
Changing the display zone never changes delivery, energy, TOU membership or PV.

Clock rules, calendar versions, TOU definitions and source mappings are retained
reference data selected under the same historical context as other inputs. Pin
the actual timezone-rule dataset version in schedules and calculation manifests;
a later timezone database update must not silently regenerate a saved schedule.
Any intentional rule correction or schedule regeneration creates a new retained
version with explicit provenance.

Schedule/calculation reuse includes all economically relevant clock, calendar,
TOU, duration and shape inputs. User display preferences do not enter economic
identity. Shared interval/calendar work may be computed once for a resolved
definition and context, without removing per-trade provenance or authorization.

## 5. Delivery and qualification

The first linear slice may use explicit dated UTC buckets. It must preserve the
types and provenance needed for clock-aware schedules, reject unsupported TOU
rules and never pretend a fixed 24-hour template covers every power product.

Before enabling automatic TOU schedules or rate quantities, qualify synthetic
fixed-offset and DST-following examples across both transition directions:

1. Preserve both repeated intervals, missing-hour policy and actual durations.
2. Verify whole-day and TOU-specific MW-to-MWh totals and total-quantity shapes.
3. Exercise overnight windows, operating dates, weekends, holidays and mixed-clock
   predicates; price joins retain the correct interval and product identity. Test
   boundaries inside intervals and aggregation across different resolutions.
4. Change user display zones, including across a date boundary, with unchanged
   schedule identities, energy, PV and risk results.
5. Replay with retained rule versions after a reference/timezone update.
6. Round-trip supported UTC bounds and offsets through database, API and runtime;
   reject ambiguous imports and unsupported clock rules without silent defaults.
