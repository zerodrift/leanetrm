# Physical LNG cargo

Status: implemented with local managed, native database/HTTP, browser and worker
qualification. Production schema release, licensed market inputs and physical
mapping activation remain separate installation steps.

## First executable contract

The bounded contract is one future LNG cargo, a fixed forecast discharge-energy
quantity and an independent fixed-price consideration side. Loading and discharge
have separate exact terminal/custody references and explicit UTC windows. Loading
must finish no later than discharge starts. This valuation profile ends when
loading starts; the clock cannot manufacture loaded cargo, title or actuals.

The contract explicitly selects gross discharge energy, zero quantity tolerance,
fixed consideration and undiscounted contractual value. These capabilities must
be selected on entry rather than supplied as financial defaults. No mass/volume
conversion, loss, boil-off, fuel, tolerance or actualization rule is inferred from
the commodity, terminals or Incoterms.

Quantity entry uses an exact energy specification directly, or an exact discharge
mass specification with a selected versioned positive gross-energy-per-mass
factor. The original quantity, both specifications, factor and implementation are
retained. Decimal boundaries are exact; unsupported precision refuses rather
than rounding. Loaded mass is not a substitute for discharge mass. Geometric
unit conversion alone cannot establish LNG energy content. The
[GIIGNL custody transfer resources](https://www.giignl.org/resources) describe the
distinct custody measurements; this implementation does not reproduce a custody
measurement procedure or assert measured actuals.

## Reference and pricing contract

Product, loading terminal, loading custody point, discharge terminal, discharge
custody point, quantity specification, unit and clock remain separate choices
from exact reference tables. A singleton requires explicit selection. Valid
unpriced masters remain visible.

The backend resolves the discharge product/terminal/custody/energy/clock tuple
to one exact monthly outright valuation assignment covering the entire discharge
window. A trader cannot choose a curve to bypass missing or ambiguous mapping.
The source index, quote multiplier, monthly contract, fixing and selected market
observations remain exact inputs. Mapping coverage does not imply price coverage.
There is no gas-hub substitution, inferred oil linkage or inferred upstream risk.

The selected initial production benchmark is Platts JKM for explicitly qualified
northeast Asia delivery mappings. This selection does not activate every LNG
terminal or supply observations, monthly fixing terms or redistribution rights.
An installation supplies its exact qualified source and physical mapping. Test
fixtures use independently authored prices and locations.

The physical side supplies noncash delivery market value once. The independent
consideration side owns its explicit price, direction and payment instant at or
after discharge ends. Trade-owned positive total energy scales each normalized
side once; voyage hours and days do not scale cargo energy. Each side retains its
complete executable schedule. Reopening executes that retained content without
regenerating it or following a newer mapping.

## Incoterms

Booking saves exact reference relationships for edition, rule and named place.
The edition and rule are separate selectors; the selected rule must belong to the
selected edition. The named contractual place is separate from both custody
points. The first consumer is the retained LNG cargo trade, not a standalone
catalog viewer.

These saved terms record contractual selection. They do not select a pricing
curve or conversion, create title events, or synthesize freight, insurance, tax
or other charges. ICC distinguishes the named place from title transfer in its
[Incoterms guidance](https://library.iccwbo.org/clp/clp-incoterms-qa-2020.htm?AGENT=ICC_UK).
Rule identifiers are reference metadata; the full ICC rules are not reproduced.

## Local qualification

- A synthetic forecast of 980 tonnes with an explicit 50 energy units per tonne
  conversion gives 49,000 energy units. At a delivered mark of 11 and independent
  consideration of 10, delivery value is 539,000, cash is -490,000 and contractual
  PV is 49,000 in the selected currency, with explicit discount factor one.
- A +1/-1 shock to the declared delivery mark changes PV by +49,000/-49,000.
  Fixed cash and the selected mass-energy factor do not change.
- Exact programs survive serialization and reproduce the same value. Wrong
  mapping, physical bindings, quantity basis, lifecycle or source pins refuse.
- Booking and recovery are idempotent and actor-bound; changed payload reuse
  refuses. Final database admission independently validates terms and schedules.
- Populated schema 0175 upgrades through 0179 without changing prior reference,
  market or trading columns, sealed programme bytes or the authorized old reader.
  An unexpected predecessor function causes atomic refusal.
- Common positions, Blotter and interactive/queued risk execute retained
  programmes under exact source frames. Normalized schedule readers can be
  disabled without changing booked editor or Blotter value. A later ambiguous
  mapping blocks fresh entry while saved source pins and command replay remain
  unchanged. Revoked reference access fails a queued job without publishing a
  result that omits the cargo. Generated closes remain unsupported for LNG.
- Native admission independently refuses normally sealed programs with dated
  total rows or reordered instructions; booking without programme publication
  rolls back at commit. Natural booking-authority expiry during an observed
  publication wait refuses even a completed-command retry.
- Browser checks cover separate explicit singleton choices, unpriced masters,
  derived pricing, stale-response rejection, Incoterms, user-zone instants and
  coded server failure after acceptance. Reloaded reconciliation needs no fresh
  reference catalog; its status operation needs current trade-read authority.

The current consumer accepts direct gross-energy input or its exact selected
discharge-mass conversion. Other units refuse rather than silently converting.
Bookings retain one original revision; amendment, cancellation, operational
actuals and settlement are not implemented by this profile. Retained fact reads
remain available after loading starts, while this profile's PV refuses.

## Subsequent slices

Physical load/discharge actuals, parcel allocation, loss/boil-off attribution,
terminal inventory, regasification, demurrage, corrections, invoicing and payments
require their own executable contracts. No operational completion is implied by
pre-delivery valuation. Physical cargo logistics and tanker-freight pricing remain
distinct workflows. Freight charges and broader fee catalogs are added with their
first supported contractual consumer; no unused fee table is introduced here.
