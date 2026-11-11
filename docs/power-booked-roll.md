# Booked power contractual value change

D380 admits a bounded full-month, already-started fixed physical power contract
through the existing Instrument/Side booking and valuation operations. It does
not implement cash settlement, invoicing, physical inventory or futures margin.

`in_term_fixed_power_contractual_v1` is an explicit new-booking admission: hourly
contractual MW, one complete active calendar month, profile quantity, flat fixed
consideration, no normalization or discounting, monthly unadjusted payment on
the tenth of the following month. Missing admission retains the original
future-only booking contract; unknown or null admissions refuse. Actual capture
and booking knowledge are recorded now, even when business valuation is historical.

## Availability and corrections

`pjm_da_normal_release_replay_v1` models normal DA availability at 13:30 civil
Eastern on the preceding day. This is a historical replay assumption, not an
observed release timestamp or finality status. PJM permits delayed publication.
The calendar and captured clock determine the real UTC instant across DST.
See [PJM Manual 11 revision 109](https://www.pjm.com/-/media/DotCom/documents/manuals/archive/m11/m11v109-energy-and-ancillary-services-market-operations-10-19-2020.pdf).

Only released operating days consume captured LMPs. Their correction revision is
the latest visible at the explicit certified knowledge cutoff. The release gate
does not change a capture's real knowledge time. Unreleased hours retain the
declared futures-plus-basis forecast; expired unfixed anchors refuse. The existing
capture-at-valuation policy remains separately selectable and unchanged.

A location graph whose forward domain begins during the month may explicitly
enroll an actual-only prefix from that month's first day to the forward boundary.
It creates no past forward contracts, marks or dependencies. Missing selected
actuals refuse. The prefix belongs to an exact activation revision, introduced
atomically with a fresh activation. A new activation carries an enrolled prefix
only from its immediate predecessor, validating every new segment; historical
activation receipts and old cutoffs remain unchanged.

## Comparison contract

`POST /api/ppa/pnl`, convention `fixed_power_contractual_change_v1`, compares one
unchanged full-month holding at two ordered recorded closes in the same session
and version, before payment. Each close must be the head known in force at its own explicit cutoff. The requests
name exact frames and monotone certified knowledge cutoffs. An amendment,
assignment/model change, cash boundary or unsupported row state refuses.

The operation reuses the native Side PV rows and exact arithmetic. Its result is
**contractual value change**: projected-to-actual rows contribute fixing,
projected-to-projected rows contribute forward marks, and actual-to-actual rows
contribute capture corrections. Unchanged fixed consideration cancels. Those
components must sum exactly to the native PV change. A future hour can be
price-fixed while still physically undelivered.

The independent synthetic Side oracle buys 1 MW of December 2020 weekday 5x16 at
40 USD/MWh. At 16:00 UTC on December 18 and 21, DA captures of 30 and forecasts
of 52 then 53 give 352 MWh, PVs of -704 and -944, and change -240 = -352 fixing
+ 112 forward marks. A later one-hour Friday correction from 30 to 31 adds 1
when comparing corrected Monday with the frozen Friday cutoff; old-cutoff replay
retains the originals. These are synthetic prices on real calendar conventions.

Synchronous booked graph valuation is supported. Worker enqueue and replay refuse
these assignments until their retained graph evidence has its own qualification,
including zero-hour product windows. Existing native workers remain independent.

## Qualification

The owned HTTP checks retain legacy future-only booking and qualify explicit
in-term booking/retry, null/unknown/partial/discounted admission refusals and the
private SQL boundary. Existing product masks, 23/25-hour DST grids, sparse demand,
captured revisions and the older future-DA eligibility boundary remain regressions.
Native Side checks additionally refuse reversed fixing and changed quantities.

An isolated populated preview qualifies normal booking, two separately recorded
closes, native Side PV and the managed comparison, prefix-only demand, visible
corrections, original-cutoff replay, and ordinary Blotter values in a dedicated
synthetic book. Its preexisting Friday capture revision has 16 peak hours at 20;
retaining that history gives -864/-1104 and the same -240 movement. A later
one-hour correction to 21 gives -239 against the frozen Friday cutoff; restating
both cutoffs gives -863/-1103 and -240. The earlier six location/product forward
values remain unchanged.

A second disposable copy qualifies activation prefix carry/retry, old and new
cutoffs, atomic incompatible-output refusal, replaced-close refusal and retired
identity refusal. This is bounded correctness qualification, not a production finality, accounting
or performance claim.

## Hourly ATC example

D381 extends the same physical Side with a firm hourly MW profile and an
independently selected price shape. The synthetic December example is 2 MW
07:00–15:00 Eastern and 1 MW otherwise, every day. Raw additive offsets are
+4 USD/MWh in those eight hours and -4 otherwise. Each native daily 1x16/1x8
block calibrates its full source-period mean to its futures-plus-basis mark.
The morning/afternoon 1x16 prices become mark+4/mark-4; the eight 1x8 hours
remain at their mark. Monthly sources stay flat under the explicit daily-only
policy. This is a mark-preserving synthetic shaping model, not evidence of
market-replicable hourly prices.

The 744 hourly December intervals carry 992 MWh and fixed consideration of
39,680 USD. With captures of 30 except December 18 peak at 20, and live daily
marks of 52 Friday and 53 Monday, native retained Side PV is -592/-2480.
Contractual change is -1888 = -2208 fixing + 320 remaining marks. Saturday,
Sunday and Monday fix 96 MWh over 72 hours. A one-dollar Friday morning LMP
correction changes value by two dollars; earlier certified cutoffs retain the
original. A flat 1 MW quantity has no premium from the normalized price shape.

Native checks cover shared base/up/down daily anchor shocks (24/8 USD of
shaped quantity exposure), linked cash, legacy identity, partial fixing without
residual recalibration, missing calibration cells, all-actual and monthly-only
scopes, and real DST quantity counts. Owned HTTP checks cover saved immutable
profiles, retained/draft PV parity and first-outcome booking retries. Saved graph
checks also cover exact shape-series foreign keys, SQL wrong-kind/hidden-source
publication refusals, monthly flat prices, mark-preserving corrections and old
cutoff replay. Further monthly shaping statistics, an hourly profile authoring
screen and worker execution remain separate work.

Daily product hour conventions and the monthly off-peak averaging distinction
are described by the public ICE specifications for
[daily peak](https://www.ice.com/api/productguide/spec/6590471/pdf),
[daily off-peak](https://www.ice.com/api/productguide/spec/6590503/pdf) and
[monthly off-peak](https://www.ice.com/api/productguide/spec/6590428/pdf).
Observations in this example are synthetic.

The populated isolated preview also retains earlier synthetic capture revisions:
all December 18 hours at 20 except the morning correction to 21, and December 19
at 40. Preserving that history gives ATC close values -670/-2238 and contractual
change -1568 = -1888 fixing + 320 forward marks. A later single morning correction
to 22 adds 2; the original cutoff still replays -670/-2238, while both restated
closes give -668/-2236 and the same -1568 change. Ordinary Blotter values agree.
A separate disposable copy refuses a changed live shape model in the P&L bridge,
missing required shape cells and a retired identity. The implementation retains the original capture and knowledge facts.
