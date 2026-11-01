# Contractual gas quantities

The quantity foundation defines contractual gas-day geometry and receipt/delivery
quantities. Its first consumer is the managed [physical gas pre-delivery
profile](physical-gas-side.md). That profile now has database booking and a
[deal ticket](deal-ticket.md); [daily operational quantity revisions](physical-gas-daily-quantities.md)
are a separate implemented workflow. Physical completion and cash settlement
remain in the [commodity delivery plan](physical-commodity-test-plan.md).

`GasQuantitySchedule.Generate` consumes immutable `GasQuantityTerms`. The selected
quantity specification and clock reference must belong to the same exact
reference revision. The caller is responsible for resolving their authorized
catalog definitions; a reference key alone does not establish gas eligibility,
measurement conditions or a conversion from standard volume to energy.

`GasDayClock` retains serialized timezone rules and an explicit local start
minute. Schedule generation never reselects the machine's current timezone rules.
Each row retains the operating date and exact half-open UTC bounds. A missing,
ambiguous, nonpositive or unrepresentable boundary refuses explicitly; no offset
or daylight-saving convention is inferred. Clock capture is an explicit draft
helper, not a durable reference-publication operation.

Three quantity bases are supported:

| Basis | Meaning |
| --- | --- |
| PerOperatingDay | The entered receipt quantity for each gas operating day, irrespective of elapsed hours |
| PerElapsedHour | The entered native receipt quantity per elapsed hour, integrated over each exact gas day |
| PeriodTotalEqualOperatingDays | The entered total receipt quantity divided equally among the contractual operating days |

A 40-unit hourly rate produces 920 units on a 23-hour gas day and 1,000 on a
25-hour day. A 1,000-unit daily entitlement stays 1,000 on both. Splitting a
1,000-unit period total across three days retains exactly 1,000/3 per day.
The rate's per-hour interpretation is part of the selected basis, not inferred
from a unit name. The implementation does not yet accept a separate rate-unit
catalog reference or integrate arbitrary intraday rate changes.

The fuel convention and fraction are explicit terms. `None` requires zero fuel.
The other conventions are `net = gross × (1 − fraction)` and
`net = gross / (1 + fraction)`. For 1,000 gross and fraction 0.02 these give
980 and exactly 50,000/51 net respectively. Fuel is gross minus net, preserving
conservation without intermediate rounding. Negative receipts, fuel gains and
fractions outside [0,1) are unsupported. Reverse direction belongs to the
instrument membership, not a negative quantity in this profile.

Terms and captured clocks have explicitly versioned economic identities.
Quantity basis, fuel convention, fuel fraction and local gas-day start affect
identity. Generated rows are derived quantities, not stored meter readings,
allocations, nominations, delivery evidence or payment obligations.

Generation is cancellable and admits at most 1,200 operating days per request.
This is an explicit admission bound, not a performance qualification. Zero
entered quantity is supported; missing input is never treated as zero.

`PhysicalGasQuantityChecks` runs both in the default managed suite and through
`--physical-gas-quantities`. It covers synthetic daylight-saving rules without
depending on an installed IANA release, captured-rule reconstruction, exact
period allocation and fuel conservation, quote-unit mismatch, admitted/refused
term bounds, invalid local/UTC boundaries, cancellation and economic identity.
Independent numerical/temporal review identified the UTC endpoint refusal and
default-suite registration requirements; both have regression coverage.

Local verification on 30 October 2026 passed the full MarketData managed runner
(647 reported core checks) and a final focused gas rerun including the 1,200-day
and identity cases. Independent numerical/temporal review has no remaining
material finding in this increment. These checks qualify the managed quantity
foundation only; later database and browser consumers have their own records
linked above.
