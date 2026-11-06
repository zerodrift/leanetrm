# Common physical delivery view (D343)

`PhysicalDeliveryView` is the managed read contract consumed by the operations
grid. Gas, crude and LNG supply it through their existing authorized reads. It
projects retained schedule geometry and source records; it adds no persistence
table, calendar, financial policy or generic write endpoint.

Each view identifies its trade, exact binding and revision, explicit quantity
selection policy, period label, supported track/attribute columns and declared
capabilities. Each row carries its physical membership, interval, direction,
exact location and quantity-specification references, unit label, contractual
quantity, selected input and independent source heads. Source addresses retain
the Side, schedule publication, program digest, output root, instruction, grid
and cell; gas also retains its booked obligation identity. Missing records,
withdrawals and explicit zero remain distinct.

The common TypeScript presenter renders supplied columns and exact quantity
text, including fractions. It does not derive intervals, convert units, choose
tracks or infer completion and billing status. Grid row selection retains the
native recording scope and existing edit/context/recovery guards. Capability
declarations describe supported workflows; they grant no command authority.
Unimplemented capabilities display TBD and create no operation buttons.

## Implemented adapters

| Consumer | Geometry and quantities | Operational behavior |
| --- | --- | --- |
| Gas | Retained gas-day quantity outputs, absolute booked membership scale, native operational projection and nominated/confirmed/scheduled/allocated heads | Existing manual workflow and its native permissions; live connectivity is TBD |
| Crude | One retained whole-window standard-volume total; exact custody reference and independent physical direction | Existing receipt, inventory, dispatch and cash workflow remains separate below the grid |
| LNG | One retained cargo window from loading start through discharge end, with the booked discharge-energy total and discharge custody reference | Delivery inspection; cargo actuals and reconciliation remain TBD |

`PhysicalDeliverySchedule.ReadLiteralQuantities` shares the extraction of exact
retained quantity-output cells among these consumers. It applies the explicit
absolute membership scale once and refuses unsupported dynamic quantity outputs.
The gas adapter separately retains its gas-day shape and count constraints.
Whole-window quantities are not spread over invented daily rows. Cash and fee
memberships are excluded by each adapter's explicit physical membership contract.

## Adding an instrument

An implementer supplies an authorized native adapter that returns this view for
its explicitly physical memberships. Reuse the literal-output projection when
its semantics match; otherwise resolve the supported quantity sources in the
managed backend and retain their exact interval/source identities. Keep location,
unit, direction, scale and quantity policy explicit. Do not identify physical
delivery by the presence of a schedule or by a commodity label alone.

The generic grid accepts native intervals and supplied track names without a
commodity-specific table implementation. Registration/discovery, retained source
admission and any executable operational commands still need their own native
integration and qualification. The synthetic 15-minute grid check demonstrates
presentation geometry; it does not implement power operations. New commodity
workflows, dynamic quantity adapters and live connections remain separate work.
