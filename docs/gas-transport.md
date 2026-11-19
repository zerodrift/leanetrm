# Gas transport

**New trade → Structured → Transport** books a whole package of rights with an
independent fixed fee. Its financial profile values the rights from an explicitly
adopted mark and supports retained price-risk runs. The capacity workflow below
assesses entitlement use. Chosen routes check conservation, and the delivery
workbench can link route quantities to booked obligations. See [financial
Transport](gas-transport-financial.md), [chosen routes](gas-transport-routes.md)
and the [delivery workbench](delivery-workbench.md).

The [operating contract](gas-transport-capacity-operations.md) and
[wire boundary](gas-transport-capacity-api.md) define manual entitlement setup,
independent chosen claims, exact source admission and explicit empty release.

## Capacity component

`gas_transport_gross_uniform_capacity_v1` assesses one complete admitted
entitlement scope. It retains an exact version, native quantity basis, service
interval, eligible receipt/delivery paths and complete dated limit schedules.
Agreement, receipt point, delivery point and route capacity remain separate
constraints. Alternate routes consume the same agreement entitlement.

The selected law uses gross receipt quantities for agreement, receipt and route
capacity, and gross delivery quantities for delivery-point capacity. Each stage
has its own explicit interval and uniform rate. Rates use actual elapsed hours;
a short or long operating day does not become 24 hours. All interval boundaries
are exact microsecond instants and half-open. Dated limits apply throughout the
interval; matching a period's integrated capacity does not prove feasibility.
Missing, overlapping or incomplete limit coverage refuses assessment rather
than implying unlimited or zero capacity. Zero capacity can be stated explicitly.

Assessment retains exact rational rates and each contributing claim identity.
It returns breaches without clipping quantities. A capacity-consuming
replacement explicitly requires a feasible assessment. A separately selected
empty release may preserve other plans' breaches while removing its own claims.
It uses no financial mark, default calendar, hidden current clock, physical
netting or inferred point conversion.

Exact rates retain the shared 4,096-bit bound on reduced numerators and
denominators. Distinct stage durations can exceed that numerical envelope even
when their quantities fit the capacity limits and their claim count is allowed.
Such an assessment refuses with `numeric_range`; it does not round rates, return
a partial assessment or infer feasibility.

Receipt/delivery conservation, fuel, losses, transit timing and balancing have
their own required route contracts. This component does not infer those terms
from a quantity difference or decide whether an observed flow is physically
possible. Physical pipeline asset limits remain independent of commercial
entitlement; this is not a hydraulic or linepack model. Native adapters must
establish complete scope authority and independently enforce admitted witnesses
before a public preview, reservation or write can consume this component.

## Remaining product delivery

Physical performance still needs separate scheduled, measured and allocated
quantities. Interruption, pooling, release, resale, recall and operator balances
each require their own rights and conservation rules.
Physical activity, title, custody, service entitlement and fee cash remain
independent. A schedule or measurement cannot silently create ownership or cash.

Chosen-plan service value and the value of unused contractual flexibility are
separate capabilities. Unsupported optionality cannot become a successful zero
in complete portfolio value or risk. No pricing, quantity-scaling or economic
continuation capability is claimed by the capacity assessment.
