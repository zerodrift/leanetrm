# Operating facilities and supplied-plan valuation

Status: bounded local implementation (D412). Managed checks, 50 disposable
database cases and owned HTTP qualification pass on the current base. Browser component tests qualify form transport, recovery and desktop/phone
layout. Thirteen owned live-server browser checks also pass native save/reopen,
pricing, lost-response recovery, leave guards and account isolation. The
[saved trading, hedging and inventory continuation](structured-power-trading.md)
defines its additional native and GUI contracts separately.

Trading → Operating facilities attaches supported operating parameters to
an existing reference location. A facility specification, a contractual operating
right, the supplied plan and hedge trades remain separate. Thermal generation,
battery storage and solar output rights share common Side valuation while their
physical calculations retain separate implementations.

## Saved identity and history

Migration [0342](../db/migrations/0342_power_operating_facilities.sql) stores
immutable operating versions. Location name, type and coordinates come from
`ref_data.locations`; the operating extension creates no second facility master.
Each version pins the exact definition/reference bundles, location, clock and
consumed quantity specifications through foreign keys. It retains its own
identity, version, UTC effective bounds, implementation and closed scalar
parameter record. Solar has an energy specification and no unused MW reference.
Thermal and battery versions require explicit compatible MW/MWh specifications;
thermal also requires a distinct selected fuel-energy commodity.

Saves require an expected head and request identity. Exact command retries return
their first outcome. A different request for an already recorded business fact
returns that original version without another version or publication, including
when a later correction exists. Changed facts append a version; concurrent
conflicting changes cannot both advance the same head. Knowledge comes from the
existing commit-certified publication prefix.

Exact reads require a certified context and matching explicit business date. The
selected frame's valuation instant remains separate: a regional close's business
date can differ from its UTC date. A version
already known at that cutoff can take effect in a future delivery period; pricing
checks that every supplied delivery interval fits its saved effective window.
There is no implicit current-version substitution. Directory selection is
explicit: `all_known_versions_v1` includes known future windows;
`active_asof_latest_v1` selects the latest applicable revision at the as-of
instant. Reads apply current authority. Saves and command-status recovery use
current control state; historical contexts cannot author versions or recover a
later command outcome as historical data.

`ReadPhysical` and `ReadMarketData` govern scoped reads. Setup authoring and API
command recovery additionally require `ManagePhysicalSetup`; previews require
`RunScenario`. The managed
[store](../src/OpenEtrm.Api/PhysicalOperatingFacilityStore.cs) and ordinary API
operations share the same authorization and storage boundary.

Migration 0342 created the two Physical capabilities and granted them to no role.
Migration [0389](../db/migrations/0389_operating_role_grants.sql) installs them
(D444). Market data editor holds `ManagePhysicalSetup`. It and every human role
that reads trades hold `ReadPhysical`: Trader, Risk analyst, Facility operations,
Facility operating limits, Gas delivery operations, Gas service setup, Contracts
administration, Counterparty credit, Trade confirmations and Trade supervisor.
Viewer, Market data approver, Access administrator, Assistant prompt editor and
the agent-only roles hold neither. The migration assigns no role to any person.

| A person holding | Can |
| --- | --- |
| Market data editor | Open Operating facilities, read facility versions, save a version and recover that save |
| Trader | Read facility versions; review, book, reopen and value operating rights; read them in the Blotter |
| Risk analyst | Read facility versions and operating rights; run the supplied-plan preview and the hedge report |
| Another role that reads trades | Read facility versions and operating rights, including on the confirmations desk and in a plant's facility report |

An operating right is a trade. A trade reader without `ReadPhysical` is refused
the whole position, valuation or risk read of a scope that holds one
(`operating_position_unavailable`), does not see the trade on the confirmations
desk and reads a plant's facility report without its installed capacity and
operating trades. The assistant's agent role holds no Physical capability: a read
it makes under that role, such as its trade-reading tool, is refused over such a
scope, while a workspace page operation runs under the asking person's authority
and reads what that person reads.

## Saved fields and their consumers

Only fields with an implemented feasibility or pricing consumer are saved. Exact
decimal strings retain the reference capture bound of 18 fractional digits and
magnitude below `10^24`; excess precision is rejected without rounding.

| Facility fields | Current calculation consumer |
| --- | --- |
| Location, clock, native interval, exact quantities and effective bounds | Reference admission, native UTC grid construction, unit checks and delivery-window coverage. Thermal/solar use 15 or 60 minutes; battery also supports 5 and 30 minutes. |
| Thermal minimum/maximum output | Online minimum output and each interval's output/available-MW bounds. Availability itself is a supplied operating input. |
| Thermal ramp-up/down MW per hour | Output changes against elapsed interval hours under the interval-average target model. This does not certify a continuous ramp trajectory. |
| Thermal startup/shutdown output limits | Maximum output at a boundary start and before a boundary stop. |
| Thermal minimum up/down minutes | Commitment transitions, including elapsed opening-state time and the terminal minimum-time check. |
| Thermal startup-instruction lead minutes | Each start's explicit instruction must arrive after required downtime and early enough for the stated lead. Partial startup states are unsupported. |
| Thermal maximum starts | Opening starts already used plus starts in the supplied plan cannot exceed the selected budget. |
| Thermal consumption heat rate, no-load fuel/hour and startup fuel | Exact modeled consumption: incremental fuel from output MWh, no-load fuel over online elapsed hours, and fuel per start. Contract guarantees remain separate inputs. |
| Battery charging/discharging MW ratings | Each direction's power bound scaled by the supplied available-power fraction and actual interval duration. One direction may have a declared zero rating. |
| Battery minimum/maximum stored MWh | Opening state, every state transition and terminal-state feasibility. |
| Battery separate charge/discharge efficiencies and grid-side AC boundary | `closing MWh = opening MWh + charge efficiency × grid-charge MWh − grid-discharge MWh / discharge efficiency`. |
| Battery explicit zero auxiliary/self-discharge, fixed capacity, prohibited simultaneous operation and grid charging | Admission and state equations use these supported policies; richer policies refuse. |
| Battery throughput policy and optional cumulative budget | Explicitly uncapped, or a cap on cumulative grid-charge plus grid-discharge MWh, including throughput already used in the opening state. |
| Solar generated/delivered measurement stage | The supplied production MWh must use the selected stage; entitlement and delivery/payment calculations retain it. There is no irradiance or nameplate-production model. |

The thermal consumer is [ThermalToll](../src/OpenEtrm.MarketData/Power/Structured/ThermalToll.cs);
the battery consumer is [BatteryOperatingPlans](../src/OpenEtrm.MarketData/Power/Structured/BatteryOperatingPlans.cs).
Both evaluate supplied plans deterministically with exact quantities and native
UTC intervals, including repeated local hours.

## Operating and contractual inputs

Opening state, dispatch and availability are explicit preview assumptions, not
static facility setup or persisted operating instructions. Thermal inputs include
online/offline state, elapsed time, previous output, starts used and startup
instructions. The supported terminal policy requires minimum-time obligations to
be satisfied within the horizon. Battery inputs include opening stored energy,
cumulative throughput and explicit terminal energy bounds. Carrying opening
inventory supplies future usable energy; the remaining-value calculation does
not subtract a newly marked opening cost basis.

Thermal guaranteed heat rate/no-load/startup fuel belongs to the right's terms.
The result keeps guaranteed fuel and modeled consumption distinct, and valuation
explicitly selects which fuel quantity it prices. Neither becomes a meter actual.
Solar entitlement is an exact fraction; as-produced and fixed-profile delivery
are separate choices. Supplied production uses the explicit hypothetical-forecast
assumption and creates no eligible actual energy or certificates. Fixed-profile
plans expose delivery, replacement and surplus quantities; their full valuation
requires additional qualified balancing-price sides and is unavailable here.
The pure [solar consumer](../src/OpenEtrm.MarketData/Power/Structured/SolarOutputRights.cs)
also defines evidence-aware quantity checks, but the current preview supplies no
certified meter/deemed-source adapter.

Prices, payment rule, economic perspective, complete-right normalization and fees
belong to valuation inputs. The current measure is explicit right-holder
`remaining_operating_value_v1`, with explicitly undiscounted amounts. Fixed native
prices and the API's explicitly selected flat price contract are supported; a
selected contract must cover every operating date. Battery charging and
discharging have separate prices. Optional variable fees use output MWh for
thermal, charge-plus-discharge MWh for battery, and payment MWh for solar. An
optional access fee uses an exact complete-right quote and the explicit
once-per-preview-horizon basis. Startup-fee quotes and discounted valuation are
unsupported.

The [managed preview](../src/OpenEtrm.Api/StructuredPowerCalculation.cs) limits a
request to 1,024 intervals. Each payment Side owns its whole-term schedule;
ordinary `FixedSide`/`FloatingSide` and `OperatingRightInstrument` consume typed
outputs from the same feasible plan. Gross schedule rows retain quantity, price,
payment instant, amount and PV. Each Side exposes its coefficient and signed
contribution; instrument value sums those contributions. A preview creates no
trade. The separate reviewed booking operation retains the supplied plan and
membership-owned programs under the saved complete-right contract; cash
settlement remains outside that profile.

## Disabled future fields and REC boundary

The UI displays planned fields as disabled **Coming soon** controls. They add no
inert persisted parameters: battery degradation/cycle models, nonzero auxiliary
or self-discharge losses and renewable charging attribution; thermal continuous
startup/ramp trajectories, alternative fuel selection and start-count fee quotes;
solar balancing valuation, admitted metered/deemed evidence, meter-evidence
deadlines and automatic certificate issuance. Shared rights, hybrid
solar-plus-storage/interconnection constraints, ancillary services, intrinsic
optimization and stochastic option value also remain future work.

[RecInventory](../src/OpenEtrm.MarketData/Power/Structured/RecInventory.cs) is a pure
immutable ledger for supplied evidence-backed certificate ranges: receipt,
allocation, release, transfer, retirement and corrections under explicit
effective/knowledge coordinates. It checks ownership, eligibility and overlap,
with request replay and business-fact no-ops. The continuation adds its
[durable manual evidence consumer and standalone forwards](structured-power-trading.md),
with exact ownership/delivery references and current authority. External registry
submission and automatic issuance remain future work. Forecast solar entitlement
never creates issued inventory; battery discharge never issues another generation
certificate.

## Verification and remaining qualification

The current checks cover managed API reference and valuation behavior, 20 thermal
cases, 16 battery cases and 16 common operating-Side cases, plus the solar-output
and REC-inventory behavior groups. The dedicated
[storage check](../tools/check_operating_facilities.py) passes 50 cases in a new
disposable stock PostgreSQL cluster. It covers installed role authority, exact references, canonical
business-fact no-ops, expected-head races, command replay, rollback, old knowledge cutoffs, known future
versions, historical read-only behavior, revoked authority and expiry after lock
waits. Independent review exercises the numerical and temporal/authorization
boundaries.

Owned HTTP checks pass save/reopen/replay, historical authoring refusal,
unauthorized reads and deterministic replay under an exact retained context.
They verify synthetic battery value 610, thermal guaranteed/consumption values
760/840 and solar value 500 in the selected quote currency, plus 7 MWh of
fixed-profile replacement exposure. Gross supplied-plan rows carry no fabricated
meter evidence. Run from the repository with Python 3.10+, stock PostgreSQL 16+
and the SDK pinned by `global.json`, supplying their installed binary paths:

```sh
python3 tools/run_preview.py \
  --pg-bin /path/to/postgresql/bin \
  --dotnet /path/to/dotnet \
  --structured-power-check
```

Production performance, operational dispatch, broader optional rights and cash
settlement remain separate gates. Saved booking and price hedging use the bounded
continuation contract linked above.
