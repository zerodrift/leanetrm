# Amending a running trade: design

Status: the first two slices below are implemented (D439, D440). The remaining
slices are designed here and not built. This document records the contract, the
alternatives considered and the facts that decide the order of work.

## Intent

A booked trade must stay editable after it has started. The motivating case is
extending the maturity of a running bilateral trade by a few months. A change
that alters commercial terms is an amendment that needs its own commercial
confirmation. A change to internal booking fields is not.

## Goals

- Change the remaining term of a running trade, including moving its end later,
  while every period already fixed, delivered or paid keeps its exact terms.
- Keep one trade: the same trade identifier and reference, with one new
  immutable revision per amendment.
- Mark exactly the revisions that change commercial terms as awaiting
  confirmation.

## Non-goals

- A generated confirmation document, for new bookings or amendments. Planned,
  not built (see Remaining work).
- Editing a period that has started. Realized fixings, deliveries and cash stay
  facts; correcting them is a separate retained operation.
- Novation, partial termination and backdated insertion into revision history.

## Anti-goals

- No overwrite of a booked revision and no change of trade identity.
- No inferred continuity: where the system cannot prove that a started period
  is unchanged, the amendment refuses with a structured error.

## What exists today

| Trade type | Amendment before this work |
| --- | --- |
| Futures | Booking fields, contract, lots and price while the contract is live and not margined |
| Financial and heat-rate swaps | All terms, only before the first fixing and before any payment is due |
| PPA | Forward replacement from a boundary at or after the valuation instant; the replacement had to end on the retained end. Termination at a boundary |
| Physical gas composition | Whole replacement while every delivery day is in the future and no operational quantity exists |
| Futures options, crude, LNG, heat-rate call options, gas storage, operating rights, REC forwards, gas park loan and custody, gas transport | None |

Every amendable type appends an immutable revision with its own event
identifier, actor, reason and publication; the trade identifier and the trade
reference never change. A bilateral revision is admitted under an executed
agreement and a credit decision, and a commercial confirmation (sent, matched
or disputed) is recorded by hand against one exact trade revision. Before this
work every new revision awaited a confirmation, including a memo change, and
re-resolved the agreement and credit decision.

## Approaches

1. **Amend from a date forward (chosen).** The amendment names a boundary.
   Everything before it is retained exactly; everything from it on is replaced
   and may end later. One trade, one new revision. This covers any mid-life
   change, and PPAs already work this way.
2. **Book the added months as a linked trade.** Cheap, since booking exists for
   every type, but it covers extension only, leaves two rows for one deal and
   does not fit contracts whose state carries across the join, such as storage
   inventory.
3. **Close the remainder and rebook.** Breaks trade identity, reports as a
   close plus a new trade and needs a termination operation that only PPAs have.

Approach 1 was chosen because it preserves trade identity and the links that
hang from it: confirmations, settled periods, deliveries and P&L continuity.

## The shared rule

An amendment of a running trade supplies an effective instant equal to the
working As of and replaces terms only for periods that start after it. A period
that has started is retained with the same identity and quantity. The
replacement may add periods after the retained end. Ending earlier than the
retained end is a termination, not an amendment. Each type implements this
rule against its own retained facts; where a type cannot yet prove continuity
it refuses.

## Slice 1: commercial and internal changes (D440, implemented)

`desk.commercial_change(previous row, new row)` decides, for every bilateral
revision table, whether a revision changes commercial terms. It compares the
two revision rows after removing the internal booking fields (revision links,
event identity, book, memo, reason, request body, operation, actor, publication
and effective instant). Any other difference is commercial. A table whose
revision row carries no instrument binding, as PPA rows do, cannot prove its
terms unchanged, so its revisions are always commercial. An unknown column is
commercial. A scheduled swap states its quantity basis, unit and delivery hours
in its retained request; those stated terms are compared too, because the same
resolved instrument can be stated in more than one way. Choosing another
agreement for the operation is commercial. The rule therefore fails toward
asking for a confirmation.

A revision without a commercial change is admitted under the agreement and
credit decision of the revision before it, with no new credit check, and
records `carried_from`: the earlier revision whose commercial terms it carries.
Confirmations are read and recorded through that revision. A commercial
revision is admitted as before and awaits its own confirmation.

Consequences: a book move or memo correction no longer shows as awaiting
confirmation and is no longer refused by a credit hold. A physical gas
amendment always creates a new instrument, so every gas amendment is
commercial; this over-asks for a book-only gas change and never under-asks.

## Slice 2: PPA extension (D439, implemented)

`security.save_ppa_booking` accepts an amended segment whose coverage ends
later than the retained end, from a boundary inside the retained coverage or
appended exactly at its end. A segment ending before the retained end is
refused; shortening is a termination. Earlier bindings keep their binding
identity, coverage, quantity and terms.

The monthly gross settlement planner accepts a head whose coverage runs
contiguously from the original start to at or beyond the original end. Added
cells are keyed to the original payment lineage, as replacement cells already
are; the amended binding still needs its explicit lineage mapping before any
month previews.

Unchanged constraints that bound an extension: amended terms keep the trade's
original definition and reference revisions, so added months are valued only
where the original curves, sources and calendars cover them; coverage must lie
inside the amended instrument's delivery term; capacity delivery uses whole
operating months; a terminated head cannot be amended.

## Slice 3: running financial swaps (designed, not built)

A running scheduled swap is refused at five independent places, and four
further layers depend on revision identity:

1. The head gate in `security.save_scheduled_swap` refuses when any period has
   a due fixing or a passed payment day.
2. The period preparers refuse any submitted period whose payment day has
   passed, so a retained period cannot be re-admitted as new.
3. The managed preview repeats both tests and prices with a pure schedule that
   refuses overdue payments; a running trade needs lifecycle valuation against
   its settlement occurrences.
4. The editor reports a trade with settlement occurrences as not amendable.
5. A submission must use the market definition revision effective at the
   valuation date, while a retained period's identity includes the definition
   revision it was booked under.

Dependent layers: lifecycle action profiles are bound to the origin revision
and stop until configured again; the general trading P&L report keys positions
by binding, so a changed instrument reports as closed plus new; the change
review compares periods by index; the period editor has no locked rows.

Constraints found: a composed single-period swap cannot gain periods and cannot
become a scheduled swap under the same trade; a contract-total quantity basis
re-allocates every month when the term changes, which changes determined
periods; a power side captures its clock over the whole term.

Decisions this slice needs before it is built:

- Whether a retained period keeps its booked definition revision inside a side
  that otherwise uses the current one.
- Whether lifecycle profiles carry forward for unchanged periods.
- Whether the general P&L report should treat an amended binding as continuing.
- Whether the booking rule tightens to the contractual P&L rule (a changed
  period must start after the effective instant), which today is stricter than
  booking.

## Slice 4: running physical gas (designed, not built)

`future_gas_composition_replace_v1` requires every original and replacement
delivery day to be in the future and refuses a trade with nominations,
scheduled quantities, service accounts or facility links. A running amendment
must carry the started delivery obligations, and the facts recorded against
them, onto the new revision without re-creating them.

## Slice 5: gas storage (blocked)

A storage contract stops being valued when its first gas day begins, because
measured flows, inventory actuals and settlement cash are not built. A running
storage contract cannot be extended meaningfully until that exists.

## Remaining work

- Generated confirmation documents for new bookings and amendments.
- Slices 3 to 5 above.
- A termination is a commercial change but creates no admission, so no
  confirmation can be recorded against it.
- Exchange-traded futures stay outside confirmations. Their edits are booking
  corrections; the history still labels them as amendments.
- The amendment preview does not yet say whether the change will need a new
  confirmation; the desk shows it after the revision is saved.
- The trade types with no amendment at all.
