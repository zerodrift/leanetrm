# Amending a running trade: design

Status: the first three slices below are implemented (D439, D440, D441), the
third within the limits it states. The remaining slices are designed here and not
built. This document records the contract, the
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

## Slice 3: running financial swaps (D441, implemented within limits)

Settlement facts already belonged to the trade by membership and delivery month,
and the revision guard already admitted a new parent side whose settled periods
keep their owned period identity. The slice extends that proof from settled
periods to every started one and removes the refusals in front of it.

Decisions:

- A period that has settled or started is locked. It keeps its identity,
  quantity, terms and the definition and reference revisions it was booked
  under.
- An amendment changes only periods that start after the effective instant;
  booking adopts the contractual P&L rule. A request that changes a locked
  period refuses with a structured error naming the period and the reason. The
  editor shows locked periods as read-only and says why: settled, delivered, in
  progress or fixing.
- An amended swap stays one trade in its P&L bridge, which reports the value
  change caused by amendments as Impact of amendments.
- Price, spread, quantity and payment rule of a period that has not started
  may change; that is an ordinary commercial amendment that awaits its own
  confirmation. A side keeps one index, currency and price settlement across
  its periods, so a started period fixes those for the side, and the swap
  keeps its direction and discounting.
- A period an amendment adds starts after the effective instant, whether or
  not the swap has started.
- Run settings follow a payment occurrence to the new revision while its
  period, direction and quantity are unchanged; a new or changed occurrence
  needs its own configuration.

Which periods have started is judged at the working As of. An amendment dated
before the trade's latest settlement activity is refused unless the account
holds the Amend behind settlement permission, which the Trade supervisor role
carries (D442).

Built: the owned write takes started months from the booked rows and proves
them unchanged; the preview values the proposed terms under the trade's
settlement lifecycle; the ticket locks started months, keeps entered months when
the term is extended and reviews the lifecycle value; run settings carry.

Limits, each a structured refusal:

- The booked market definition revision must still be the one in force. One
  instrument cannot yet be valued across two market definitions, so started
  months on an earlier revision cannot be joined by months on a later one.
- Power-hour quantities, factor contracts, quantity-weighted observations and
  official daily exchange settlement are outside the rule.
- A side that allocates one contract total keeps its total and its months once
  a month has started, because a new term would re-spread the started months.
- A composed single-period swap cannot gain periods and cannot become a
  scheduled swap under the same trade.

Still to do for this slice: amending across market definition revisions; net
settlement settings and scheduled lifecycle jobs, which name the revision they
were saved under and are saved again; the general trading P&L report, which does
not cover swaps, and so has no Impact of amendments column; a termination of a
scheduled swap.

## Changing a market, index or curve definition (proposed, not built)

Two rules hold today. A published definition never changes in place: a change
is a new revision. A new revision never rebinds a booked trade: each side and
period keeps the exact revisions it was booked under, values and fixes against
them, and refuses when its revision has no market frame at the working As of.

Not built: publishing a revision does not show the trades bound to the one
before it, and no operation moves a trade to a later revision.

Proposed:

- Publishing a revision lists the trades still bound to the revision before it
  and changes none of them.
- Moving a trade to a later revision is an amendment of that trade from a date
  forward, under the shared rule above. It is commercial whenever the revision
  changes a fixing source, a calendar or contract terms.
- A bulk move lists the affected trades, asks for confirmation and creates one
  amendment per trade. A trade that cannot be moved refuses on its own and is
  reported; the others proceed.

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
- Listing the trades bound to a superseded definition revision, and moving them
  by amendment.
- A termination is a commercial change but creates no admission, so no
  confirmation can be recorded against it.
- Exchange-traded futures stay outside confirmations. Their edits are booking
  corrections; the history still labels them as amendments.
- The amendment preview does not yet say whether the change will need a new
  confirmation; the desk shows it after the revision is saved.
- The trade types with no amendment at all.
