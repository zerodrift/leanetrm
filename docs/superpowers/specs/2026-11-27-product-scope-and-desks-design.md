# Product scope: a gas and power core with switchable desks

Status: draft proposal. Direction not yet approved by the owner. Not implemented.

## Intent

The repository describes itself as an ETRM for commodity trading. What it holds
is uneven. Physical gas and power are deep: booking, schedules, delivery
operations, storage, transport, billing and risk. Physical crude is a bounded
terminal workflow. Physical LNG is one cargo's booked terms. RECs are an
inventory page and a forward. Every installation shows all of it, so a gas
scheduler meets crude and certificate screens, and the product promises breadth
it does not have.

The product becomes a physical gas and power system. RECs belong with power.
Crude and LNG stay in the same build as desks an installation switches on. A desk that is off shows no
entry points and admits no new bookings. Depth is added to gas and power first.

This is a statement of scope and one installation setting. It moves no code
between projects, changes no schema that exists and alters no calculation.

## What a desk is

A desk is a named product family: the instruments it books, the pages where its
work is done and its setup screens. Desks are declared once, in the managed
backend, in a small project-owned list.

| Desk | Maturity | Instruments (New trade names) | Pages |
| --- | --- | --- | --- |
| Gas | Core | Physical gas, Park and loan, Two-location gas, Transport, Gas storage | The gas Scheduling entries; Delivery setup for gas |
| Power | Core | PPA, Operating right, Heat-rate call option, REC forward | Operating facilities; Power TOUs; REC inventory |
| Crude | Preview | Physical crude | The crude Scheduling entries; Delivery setup for crude |
| LNG | Preview | Physical LNG | The LNG Scheduling entry |

Everything else is the core and belongs to no desk: futures, futures options and
swaps; the Blotter, Markets, Risk, Lifecycle, Reference Data and
Administration. A financial instrument on a crude or LNG curve is a core
instrument. It follows the market data an installation holds, not a desk, so
crude and LNG futures and swaps stay bookable with those desks off.

Maturity is documentation of how much of a desk exists. Preview means bounded
and not the current focus. It carries no behavior.

## Goals

1. Documentation states the product as physical gas and power, with the other
   desks named as preview.
2. An installation names the desks it runs. No desk is on unless named.
3. A desk that is off has no sidebar entry, no New trade tile and no setup
   screen, and the server refuses its bookings for every caller.
4. Switching a desk off never hides a position, a cash record or a risk result.
5. Every desk keeps its full checks in CI whatever an installation enables.

## Non-goals

- A plug-in or extension framework, separate assemblies or packages per desk,
  or a separate repository for any desk.
- Removing or rewriting crude or LNG code, tables or migrations.
- Licensing, metering or per-desk pricing.
- Per-account desk access. Roles and permissions are unchanged.
- New crude, LNG or shipping capability. Marine cargoes, voyages, time charter,
  laytime and demurrage each need their own contract and wait for gas and power
  scheduling to be complete.

## Anti-goals

- A desk is switched off and its booked trades drop out of the Blotter, a
  directory, P&L or risk.
- A desk is on in one entry point and off in another: hidden in the sidebar but
  bookable through the assistant or a direct request.
- A missing or misspelled setting is read as "all desks" or "no desks".
- Desk membership is decided in TypeScript.
- Checks for a preview desk stop running because the default preview omits it.

## Constraints

- No silent defaults. The setting is required and explicit. An absent value or
  an unknown desk code refuses startup with a structured error.
- Interface and agent entry points use the same authorized business operations,
  so the refusal lives in the operation, not in a page.
- A configuration value needs a named consumer. This one has three: the page
  and New trade lists, booking admission, and the startup check below.
- Historical views read what was booked. The setting is not a calculation
  input and has no effect on any retained result.

## Approaches

**A. Documentation only.** State the scope and stop there. Honest, but every
installation still shows every desk.

**B. An explicit installation setting (recommended).** One required list of
desk codes in the installation's configuration, enforced at the booking
operations and reflected in navigation. Small, explicit and reversible.

**C. Desk permissions.** New read and book permissions per desk, granted by
role. This answers "who may use crude", not "does this installation run crude",
and it changes authorization for every existing role.

**D. Extract desks into modules.** Separate projects or packages loaded when
enabled. Crude and LNG are already in the shared schema under immutable
migrations and in the same two backend assemblies as gas and power. Extraction
is a large refactor of working code with nothing a user can see, and a second
extension layer over Instrument and Side that no consumer needs.

## The setting

- **Where.** The hosted installation configuration gains a required list of
  enabled desk codes. The local preview takes the same list from its launcher.
  `tools/run_preview.py` and every check that starts the application name all
  four desks, so checks cover everything.
- **Validation.** At startup the list is checked against the declared desks.
  An unknown code, a duplicate or an absent list refuses startup. An empty list
  is valid and means the core alone.
- **Reported.** The response that carries an account's permissions also carries
  the enabled desks. The browser uses it to list pages; it decides nothing else.
- **Changing it.** Edit the configuration and restart. There is no interface to
  toggle a desk and no stored history of the setting.

## What a disabled desk changes

| Entry point | Behavior when the desk is off |
| --- | --- |
| Sidebar | The desk's pages and Scheduling entries are not listed |
| New trade | The desk's instruments are left out; a group left empty is not shown (the chooser already works this way for permissions) |
| Reference Data | The desk's setup screens are left out |
| Booking and desk operations | Refused with a structured error that names the desk, for the interface, imports, jobs and the assistant alike |
| Assistant | The desk's pages are not reportable and its tools are not offered, because the operations refuse |

The setting is never consulted by the Blotter, position directories, valuation,
risk, P&L, Lifecycle discovery or any historical read. Those show every booked
trade whatever is enabled.

### A desk with trades cannot be switched off

At startup the server counts booked trades per disabled desk. Any trade on a
disabled desk refuses startup, naming the desk and the count. This keeps one
rule: if the trades exist, the screens that operate them exist. An installation
that has never used a desk can leave it off; one that has must keep it on.

## Documentation and plan

- `README.md`: the opening line and "What works today" describe a physical gas
  and power system, then list crude and LNG as preview desks with what each
  holds.
- `docs/architecture.md`: the desk list, the setting and the refusal rule.
- `docs/plan.md`: gas and power scheduling come first; the deferred crude, LNG
  and shipping work is recorded so that narrowing the focus loses nothing.
- `docs/decisions.md`: the next decision id (D461 unless another lands first).
- `docs/hosted-pilot.md`: the new required configuration value.

## Delivery order

1. Documentation: scope statement, desk list, plan. No code.
2. The setting: declaration, startup validation, the with-trades refusal and
   the booking refusal. Backend only; every existing check passes with all
   desks named.
3. Navigation: sidebar, New trade and Reference Data honor the enabled desks.
   The Scheduling desk switcher in the
   [Scheduling design](2026-11-27-scheduling-workspace-design.md) reads the
   same list.

## Verification

- Failing behavior checks first, per `AGENTS.md`.
- Startup: absent list, unknown code and duplicate each refuse; an empty list
  starts.
- With crude off on a database without crude trades: booking a crude trade is
  refused through the HTTP operation and through the assistant's tool; gas and
  power bookings succeed; the sidebar, New trade and Reference Data omit crude.
- With crude off on a database holding a crude trade: startup refuses and names
  the desk.
- With every desk on: the full existing suite is unchanged. A permissions table
  test shows each installed role reaches the same pages as before.
- A check that every instrument type the booking operations accept belongs to
  the core or to exactly one desk, so a new instrument cannot be added
  unclassified.
- Independent review of the admission refusal and the startup rule, as a
  release and authorization boundary.

## Decided by the owner

- RECs are part of the Power desk, not a desk of their own.
- Financial instruments on crude and LNG, futures included, are core and stay
  available whatever desks are enabled.

## Decisions for the owner

1. **The with-trades rule.** The alternative is to allow a desk to be switched
   off while it holds trades: positions stay visible and valued, but nothing
   can operate them until it is switched back on.
2. **Recommended installation.** The documentation would recommend Gas and
   Power for a new installation. The hosted demonstration data includes other
   desks' trades and would keep them on.

## Choices made on the owner's behalf

The word "desk"; four desks with these names; the Core and Preview labels;
configuration over a stored, editable setting; refusing startup over warning.

## Left to the builder

The configuration key and launcher option names, the error codes, where the
desk declaration sits in the backend, and how the profile response names the
list.
