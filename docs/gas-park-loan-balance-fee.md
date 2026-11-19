# Park and Loan outstanding-balance fees

D410 adds the explicitly selected `physical_gas_park_loan_balance_time_v1`
profile beside the [fixed-fee title-transfer profile](gas-park-loan.md). It
retains the same bounded principal contract: one full accepted advance, one exact
point and quantity basis, and equal partial returns. Customer/provider and
Park/Loan remain explicit independent selections. The fee direction is selected
independently of the principal direction.

## Retained fee law

The rate is a nonnegative amount of the selected currency per native principal
quantity per elapsed UTC hour. The contract explicitly selects accepted
outstanding principal, actual accepted advance as the accrual start, a
contractual fee cutoff, accrual on overdue outstanding principal until that
cutoff, and a separate cash due instant. The cutoff cannot precede principal
return due; cash due cannot precede the cutoff. Passing the cutoff stops fees
without repaying, discharging or replacing principal.

Accepted returns reduce the balance from their effective instant onward. The
calculation integrates exact rational elapsed intervals, retaining subsecond
time without converting hours to rounded decimals. A 23-hour interval is 23
hours; a 25-hour interval is 25 hours. The display zone does not change this law.
The contract also retains an explicit cash increment, tie rule and cumulative
payment-total rounding stage.

The installed valuation plan explicitly returns remaining principal at the fee
cutoff. Before acceptance, its prospective advance is the later of the earliest
advance time and the evaluation instant while the advance window remains open.
After that window closes, an unaccepted agreement is unavailable: the plan
cannot invent a past advance. The trading row retains a null value and the
specific unavailable reason. Whole-book calculations refuse an incomplete total.
Initial booking must precede the advance window.

The earliest possible full-principal charge through the cutoff, including cash
rounding, must fit the existing native cash numerical domain. Terms outside that
domain refuse at admission. A zero rate is an explicit valid selection; it does
not select a different lifecycle or risk capability.

## Determination and independent cash

`gas.park_loan.fee_determine` is a separately configured, reviewed manual
lifecycle action. Each determination pins its exact agreement, source context,
accepted advance, return prefix and previous determination. It computes the
rounded cumulative fee through an explicit observed instant, then contributes
the difference from the previous rounded cumulative total. It never rounds an
incremental interval in isolation. A zero difference records a determination
without creating a cash owner.

Each nonzero contribution has its own native cash owner. Bank payment remains
the separate `gas.cash.record_payment` operation. Paying one contribution does
not rewrite a determination, repay principal or remove a different unpaid
contribution. Returns can finish while fee cash remains unpaid.

A newly accepted backdated return can change the cumulative fee. An explicit
restatement through the already determined instant contributes the signed
difference, including a refund, while retaining the original determination and
paid cash. Correcting underlying evidence instead exposes reconciliation
required; the profile has no implicit financial correction adapter.

For example, with a synthetic principal of 720 and rate 0.004, an accepted
advance at 10:00 followed by return of 270 at 11:00 determines 2.88 through
11:00. Returning the remaining 450 at 12:00 produces a cumulative fee of 4.68;
the next contribution is 1.80. If a newly accepted return instead becomes
effective at 10:30, the first cumulative fee is 2.34 and its restatement
contributes a 0.54 refund. Previously paid 2.88 remains a separate bank fact.

## Valuation and risk

The service retains its prospective rounded fee less the entire cumulative
determined total, including paid contributions. Unpaid native cash is valued
independently; paid cash does not remain in that outstanding-cash value. These
are remaining marked values, not total wealth including bank cash.

Before advance, the service also owns its explicit opposite principal
components. After advance, the existing independent stock, return claim or
liability owns principal exactly once. The explicit whole-book native-gas
pricing policy combines these owners with the remaining service and unpaid
cash. Selecting a trade does not infer ownership of other native positions.

Current-clock price scenarios and retained PRICE workers use ordinary black-box
base/up/down valuation under immutable shared factor scenarios. Quantity scaling
is unqualified because cumulative cash rounding is nonlinear in quantity.
Economic-horizon continuation and generated-close activation remain unsupported
for this profile. Their admission fence includes source-known future agreements,
zero-rate agreements, rounded-zero fees, completed principal and cash-only
states. Empty present components never establish an economic-risk capability.

## Entry, history and authority

The shared ticket first asks for the explicit profile. The managed backend then
returns only that profile's economic fields and exact selector identity. The
client rejects stale or mismatched descriptions and submits only active fields.
Retained inspection and uncertain-response recovery resolve the saved profile
before rendering its terms. Business rules and fee calculations remain managed
and native; the browser only presents their contracts and results.

Every new operation checks exact current actor, reference, policy and source
authority under its original deadline. Accepted retries recover the original
actor-bound outcome. An identical business fact under a fresh request is a
no-op; a different value is never an overwrite. Knowledge and effective time
remain independent. A source-known future checkpoint cannot silently select an
obsolete cumulative head. Historical cuts retain their original accepted facts.

Native determination integrity independently recomputes rational accrual and
rounding, validates the exact event manifest and previous head, and requires the
original typed operation receipt and cash membership. The native calculation
uses a different decomposition from the managed interval calculator. Managed
commit admission retains the original authority deadline after waits; deferred
native constraints supplement that supported writer boundary.

Before confirmation, the review shows the observed accrual-through instant,
previous rounded cumulative fee, new rounded cumulative fee, signed cash
contribution, currency and due instant. Times use the user's display zone.
An incomplete fee effect cannot expose an execution control.

## Qualification boundary

Disposable managed and native checks cover all four principal roles and both
fee directions, exact microsecond intervals, daylight-saving durations, tie
rules, cumulative restatement, paid-cash survival, native cash range, historical
cuts, corrections, duplicate facts, authority and operation seals. Twenty-six
independent whole-book value/scenario checkpoints and nine actual queued PRICE
jobs exercise independent principal and cash owners. Populated upgrade checks
retain the fixed-fee profile and refuse protected predecessor drift.

The final combined local artifact passed 185 actual application browser checks
across all eight principal-role/fee-direction combinations, including exact
monetary review, phone layout, normal whole-book Risk and lost-response recovery.
Seven expired unaccepted rows retained null values and explicit unavailable
reasons while a ninth prospective agreement booked and reopened normally.
The fixed-fee actual browser regression passed 72 checks. The integrated runner
and aggregate CI still gate publication qualification for the exact commit.
[Retained-title custody](gas-park-custody.md), [Transport](gas-transport-financial.md)
and [commercial storage](gas-storage-contract.md) have separate product contracts.
Broader fee laws, correction continuity and economic-horizon risk remain open.
No production schema activation is implied.
