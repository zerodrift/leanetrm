# Contractual factor swaps

`factor_scheduled_financial_swap_v1` supports two floating scheduled sides with
one exact currency and distinct commodities. A user selects the factor value,
numerator quantity unit and denominator quantity unit explicitly. For example,
synthetic power minus gas with a factor of 7 MMBtu/MWh consumes seven gas units
per power unit. This is a contractual cross-commodity ratio, separate from the
physical conversions inside each commodity.

The first side authors supported monthly period totals, calendar-day rates,
explicitly allocated contract totals or integrated MW schedules. The second side
selects its native quote quantity and `factor_of_first_side_v1`; manual amounts,
allocation and a second power schedule are refused. Delivery months must align.
Observation calendars, projected price sources, spreads and payment dates remain
independent. Each agreed period-average price multiplies the resolved period
quantity. This version does not calculate a sum of interval price × interval
energy, support cross-currency payments, or supply an inferred factor.

For every period, the managed backend:

1. Converts first-side quantity to the selected denominator quantity.
2. Multiplies by the positive contractual factor.
3. Treats that result as the selected numerator quantity.
4. Converts to the second side's native quote quantity.

The converter requires matching exact commodity, measurement basis and dimension
within each conversion, plus a coherent declared unit path. It never treats gas
and power as interchangeable because both have an energy dimension. Rational
values are retained without intermediate decimal rounding. A constant native
membership ratio and identical normalized period shares preserve proportional
sizes throughout valuation, source shocks and settlement. An integrated first
side's retained last delivery interval governs completion of both sides,
including daylight-saving boundaries.

## Persistence and authorization

`trading.swap_quantity_factors` stores value, exact quantity foreign keys and both
implementation identifiers. Instruments refer to the immutable factor. Retained
terms reconstruct from normalized relationships. Selected units are part of
economic identity and determination evidence: economically equivalent expressions
can produce equal cash while retaining their distinct original selection.
Ordinary amendments cannot change the instrument implementation version.

The managed operation is the trusted financial compiler. Its HTTP command has
no compiled shares/ratios/intervals fields; unknown submitted fields are refused.
The private database write routine consumes the managed compiler's output and
validates persistence shape, versions and reference keys. It is not a second
pricing implementation for arbitrary SQL clients. Managed reconstruction checks
the stored native ratio against the factor and pinned converter and refuses a
contradictory retained instrument. Runtime database credentials are service
credentials, never end-user access.

A supplemental model consumes its own exact reference revision from its sealed
model row. It does not substitute current or primary references. Reads and
retained risk workers use certified knowledge and current scoped authority;
cache reuse requires the exact catalog/revision pair. Compatible units and their
conversion ratios can be introduced by publishing a new reference version and
configuring a consuming supplemental model, without changing application code.
Old trades continue consuming their original model/reference versions. Existing
primitive single-period entry keeps its original configured reference boundary;
newer supplemental reference versions are offered in scheduled entry.

## Verification

`tools/run_preview.py --factor-swap-check` uses a disposable stock PostgreSQL
fixture. A second reference version and model are published after an older
context was certified. Synthetic factor units introduced only in that revision
produce the same PV and quantities as a native factor. Unequal period quantities
exercise proportional mapping; core checks reject contradictory native ratios
and preserve a pre-change scheduled identity digest.

The fixture checks explicit input refusals, independent payment dates with
versioned discounting, 118 daily native quote shocks in bounded report requests,
retained worker parity, gross final-fixing determinations and synthetic cash,
and a 743-hour March schedule with inherited completion. Browser checks select
the factor units, review backend cashflows and book the exact chosen expression.
Run the existing scheduled settlement/browser suite to qualify legacy behavior.
These are bounded synthetic checks, not a production qualification or an
extension of physical settlement capabilities.
