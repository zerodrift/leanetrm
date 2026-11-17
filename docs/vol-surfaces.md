# Volatility surfaces

Status: local implementation, verified as recorded in
[verification](verification.md#volatility-surfaces-d304). This is not a production
activation record. The decision is [D304](decisions.md); D356 keys each surface to
its close and adds the surface library.

A volatility surface shows, for one listed futures option product at one close, the
implied volatility of every series, a fitted smile per expiry, and how far
each series sits from that smile (its rich/cheap). It is a view: option valuation,
risk and close do not read it. Each option series is still calibrated to its own
settlement and held under `option_implied_volatility_held_v1`, as described in
[futures options](futures-options.md#valuation).

## The page

**Vol surfaces** is a page in the Markets group of the sidebar. It is listed for a
person holding `ReadMarketData` and `ReadTrades`. It reads at the close (or working
snapshot) and knowledge context selected in the shared header, and has:

- a header naming that close by its session and business day, for example
  *Houston EOD · Fri, Oct 16, 2026*, or a working snapshot by its instant, with the
  vol model shown read-only;
- a shelf of product cards, one per `futures_option_products` row of the reference
  data in force at the context, each saying **Calibrated here** (with its fitted
  expiries and series), **Latest ·** the newest close it is calibrated at, or **Not
  calibrated yet**. Products calibrated here come first. A search box narrows the
  cards through the catalog's own search, and **More products** reads the next page.
  Nothing is chosen for the person;
- a **Term structure** card: the fitted volatility at the money by expiry, drawn
  from each fit's stored level and broken where an expiry is not fitted, over a
  card per expiry with its level, slope, fit points and residual, or why it is
  not fitted. A point or a card brings that expiry's smile into view;
- where a fit is offered at a working snapshot, a **One day counted on** choice
  of clock with nothing chosen for the person; calibrating without one asks for it;
- in the selected series' readout, **Open chain**, which opens the series' row
  on [Options](futures-options.md#reads-and-entry-points);
- for the chosen product, its calibration state with *Recalibrate* where allowed,
  and **Calibrated at**: a chip per close the product has a surface at, newest first;
- a legend for rich/cheap.

A surface belongs to the close it was fitted at, so the page never shows another
close's surface in place of the selected one. When the selected close has none, a
panel says so and offers *Calibrate at* that close to a person who may fit, and
*Open* the newest close this product is calibrated at. Opening a close, from that
panel or from a chip, asks first (D361): switching chooses it in the header's As of
control, so every page reads it, and a recorded close can instead open in a separate
window on the same product, leaving this one where it is. A close of another session is not switched to
for the person (D334): the page names the session to choose in the header. A
replaced close is history, and a historical view offers only its own business day;
the page says so rather than moving.

A fitted surface is drawn three ways, linked so that hovering or focusing a series
in one view highlights it in the others and in a shared readout (series, right,
strike, log-moneyness, market, fitted and rich/cheap):

- **Heat map.** Rows are expiries, columns are the backend's strike axis, and each
  strike's cell is split into call and put. Cells are coloured on a diverging
  rich/cheap scale centred on zero. A cell with no rich/cheap states its reason; a
  strike with no listed series at an expiry is drawn as not listed, which is a
  different state from a data gap. Arrow keys move between listed cells.
- **3D surface.** Project-owned canvas code with no 3D library. Each fitted expiry's
  grid is a row of the mesh, coloured by volatility level; the straight panels
  between expiries are a visual join, not a fit, and the legend says so. Market
  volatilities sit on stalks from the smile, coloured rich or cheap. Drag or the
  arrow keys rotate the view, and a reset returns it to the default angle.
  A rich/cheap of exactly zero is shown as "On the smile", in a neutral colour, in
  every view.
- **Smiles.** One chart per expiry: the fitted grid as a line, the market
  volatilities as points shaped by right, and rich/cheap as bars. Each header states
  the RMS and largest absolute residual and the number of fit points.

The page maps values to pixels, colours and text only. It does not fit,
interpolate, compute moneyness or compute rich/cheap. Wire values are decimal
fractions as the solver returns them (0.012 is 1.2 volatility points) and are
serialized as exact decimal text; the page shows them in volatility points to two
decimals, rounding half to even.

An option trade's Details in the Blotter and the option ticket, once a series is
chosen, link to **Volatility surface**. The link opens the page on that option's
product through the same page change as the sidebar: it reads at the applied
context, and unsaved ticket entries are asked about first. The Blotter's own window
and a trade's own window offer no link, since the page lives in the main window.

## The vol model

The vol model is an explicit input of every fit and read. The response echoes it,
every stored revision records it, and an unknown model is refused with
`vol_model_unknown`; nothing selects a model implicitly. The contract
(`IVolModel` in `OpenEtrm.MarketData`) has an identifier, a fit of one expiry's
points and an evaluation of fitted parameters. One model ships:
`smile_quadratic_log_moneyness_v1`.

- **Coordinate.** k = ln(K / F), with F the expiry's underlying futures settlement
  from the same snapshot. Different expiries may have different underlyings.
- **Smile.** σ̂(k) = a + b·k + c·k², fitted per expiry by ordinary least squares
  with equal weights. The 3×3 normal equations are solved by Gaussian elimination
  with partial pivoting in `LatticeDecimal` (30 fractional digits, half-even, with
  the project's own `ln` and `sqrt`). No numerical library is used.
- **Fit points.** Each strike contributes at most one point: the out-of-the-money
  right's solved volatility (the put below F, the call at or above F). An
  in-the-money series is never substituted for an out-of-the-money one that did not
  solve.
- **Minimum data.** An expiry needs at least five distinct fitted strikes, with at
  least one below F and one at or above it. Otherwise, or when its underlying has no
  settlement, the expiry is `vol_fit_insufficient_strikes` and has no fitted smile.
- **Positivity.** A smile that is zero or below at any fitted strike, or at its
  vertex when the vertex lies inside the fitted range, makes the expiry
  `vol_fit_nonpositive`.
- **Quality.** A fitted expiry reports its parameters, point count, RMS residual and
  largest absolute residual.
- **Grid.** The response gives σ̂ at each strike from the lowest to the highest
  fitted strike in steps of the product's `strike_interval`, ending on the highest
  fitted strike. Nothing is extrapolated beyond the outermost fitted strikes, and
  there is no interpolation in time.

A gap expiry keeps its series and their market volatilities; the page shows it as a
labelled gap.

An expiry whose every series is at or past its expiry is `option_expired`, whatever
its underlying settlement. Its series stay listed, but its strikes are not on the
strike axis, and the page leaves it out of the heat map, the 3D surface and the
smiles, naming it once as expired. Months that have lapsed therefore never widen
the axis or add rows.

## Server-defined surface coordinates (D421)

The 3D mesh consumes a separate projection of the retained fit. No coordinate is
computed from current prices, current time or a series' market volatility.
`POST /api/vol-surfaces/axes` takes the same context, product, model and optional
exact definition selection as the surface read. It returns the available axis
policies and their named presets, with `initialAxis` explicitly offered as
`log_moneyness_v1`. The page displays that selection and its preset points before
sending them explicitly in a surface read. The metadata operation calculates no
surface nodes. Each fitted read returns the same policies in `surface.projection`.

| Axis id | Coordinate | Named preset |
| --- | --- | --- |
| `strike_v1` | Absolute underlying strike, in the underlying futures product's exact quote unit | Listed strikes: the retained union of live listed strikes |
| `log_moneyness_v1` | k = ln(K/F) | −0.10 to +0.10 by 0.01, including both ends |
| `quick_delta_v1` | Call quick delta N(−k / (σ̂(k)√T)) | 10P, 25P, ATM, 25C, 10C: 0.90, 0.75, 0.50, 0.25, 0.10, in strike order |

Put quick delta is call quick delta minus one. ATM is at the money forward:
call quick delta 0.50 and k = 0. This is a display coordinate, distinct from the
American option model's finite-difference delta stored in series analytics.
Every label and unit, including custom point labels, is supplied by the server.

Every live expiry retains `yearFraction`: the exact
`AmericanFuturesOptionModel.YearFraction` under the fit's option policy at its
valuation instant. It also retains the existing `daysToExpiry`. The depth axis
uses that year fraction, in years; height is fitted annualized volatility. The
server supplies all three axes' labels, units and tick values/labels.

`points` is a required array of exact decimal strings, between 1 and 501 entries.
A missing or empty list, nondecimal or nonstring value, duplicate numeric value
(including `0.5` and `0.50`), nonpositive strike, or delta outside (0,1) raises
`vol_axis_points`. An unknown or missing axis raises `vol_axis_unknown` when
points are otherwise valid. Points are ordered by strike: ascending for strike
and k, descending for call quick delta. Their canonical value is their point id.

For each explicit point, each fitted expiry supplies `nodes`: `pointId`,
`axisValue`, `label`, `logMoneyness`, implied `strike`, `fitted`, `covered` and a
structured `reason` (`code`, `message`) when uncovered. A node outside the fitted
range has no fitted volatility; its k and implied strike remain available where
the numerical range permits them. No expiry without a fit gets a mesh row. The
renderer joins consecutive point ids only when all four corners are covered and
the expiry rows are neighbours. It never joins across an uncovered point or a
gap expiry. Each listed series also carries `axisValue`, evaluated with the
fitted smile at its own k, so a market-volatility stalk stands at the surface's
coordinate. A listed series outside the fitted range has no stalk coordinate.

For a target call quick delta, put z = N⁻¹(Δq). With σ̂(k) = A + Bk + Ck²,
solve:

```text
(z√T C) k² + (1 + z√T B) k + z√T A = 0
```

The selected root is continuous with −z√T A/(1 + z√T B) as C tends to zero.
A rationalized evaluation avoids cancellation. A vanished linear denominator
has no finite branch and does not select the other root. The result must
reproduce its target delta within the stated tolerance and must lie inside the
retained fit grid's inclusive bounds. Extrapolation is prohibited.

`vol_surface_axes_decimal_v1` uses the project's 30-place, half-even
`LatticeDecimal` arithmetic and a delta residual tolerance of 10⁻²⁰.
`normal_decimal_taylor_bisection_v1` evaluates the normal integral by a
project-owned Taylor series with 90 fractional digits and rounds once to 30;
its inverse bisects the wide integral to a 30-place root. Normal tails beyond
12 standard deviations round to zero/one (omitted probability below 2×10⁻³³).
The retained plot inputs and returned projection identify both implementations.
Independent reference, symmetry, inverse-grid, tail and branch checks accompany
them; no external numerical dependency is added.

| Code | Meaning |
| --- | --- |
| `vol_axis_inputs_missing` | A stored fit lacks its own year fraction, forward, fitted range, unit or supported policy/implementation pin; current data is never substituted |
| `vol_axis_outside_fit` | This point lies outside an expiry's inclusive fitted range |
| `vol_axis_no_real_root` | The quadratic has no real root on the branch continuous from zero curvature, or that branch has no finite linear limit |
| `vol_axis_root_range` | That root exceeds the supported decimal range |
| `vol_axis_nonpositive_volatility` | The fitted volatility is not positive at the selected root |
| `vol_axis_invalid_root` | The selected root fails the quick-delta residual tolerance |

Coverage codes are per-node gaps, visible on hover and keyboard focus, rather
than a request failure. Missing retained inputs are a structured request error.
Older revisions remain immutable. Metadata discovery exposes an explicit refit
recovery state; migration `0358_vol_surface_axis_inputs.sql` compares refits
within the same retained plot-input policy so a new-policy fit records a new
revision without weakening same-policy determinism or request-id replay.

The sample retains every original strike and all 564 original settlements,
pinned by an independent digest regression. Six additional wing strikes per
expiry use `synthetic_flat_wings_v1`: the existing Black-76 premium generator
and clearing rounding with flatter, explicitly synthetic wing volatilities.
The original high-curvature synthetic smile has no 10-delta roots at several
expiries, so extending it unchanged cannot establish coverage. Added wings
change the fitted smiles and rich/cheap, but no old settlement. The inventory
of affected consumers is recorded in [fixture impact](vol-surface-fixture-impact.md).

## Market volatilities and rich/cheap

Each listed series is calibrated by the same calibration option valuation uses:
`american_futures_option_crr_v1`, from the frame's `daily_settlement` option and
underlying observations and the currency's payment-discount selection. A series'
outcome is `solved`, `at_intrinsic` (the intrinsic fallback, so no volatility) or the
calibration's error code, such as `option_settlement_missing` or `option_expired`.
Every listed series of the product is shown, a series at or past its expiry with
`option_expired`. Only solved series have a market volatility.

**Rich/cheap is market minus fit**, for every solved series of a fitted expiry, both
rights: its market volatility minus σ̂ at its strike. An in-the-money series'
rich/cheap therefore shows how far it sits from the out-of-the-money smile. A
positive value is rich and a negative one cheap.

The exchange example's settlements come from a synthetic smile in log-moneyness and
time to expiry, with fixed synthetic offsets on eight out-of-the-money series so that
both products show rich and cheap points. Each product lists five expiries of thirteen
strikes, each as a call and a put: 130 series for LO, and 131 for ON, which also keeps
its existing deep in-the-money call that settles at intrinsic. None of these values
is a market level.

## Option analytics

D408. The fit also values every calibrated series and stores, per unit of quote,
its `value` and four sensitivities: `delta` per 1.00 of the underlying futures
price, `gamma` per 1.00 squared, `vega` per volatility point and `theta` for one
calendar day, later minus earlier. Each is `{value, missing}`: an exact decimal,
or the code of why there is none. `priceBump` is the nominal futures bump used.

- **Route.** Each expiry's solved series are one-contract holdings in one
  prepared portfolio, valued by ordinary instrument PV under the shared layers
  through the common finite-difference sampler. Price, volatility and clock are
  sampled separately, so a refused sample leaves only its own measures missing.
- **Selection.** A fit states `{policy, bump, volatilityBump, clock}`: policy
  `option_sensitivities_layers_v1`; `bump` as a trading P&L report states its
  factor bump (`ticks`, `priceSteps`, `absoluteTolerance`,
  `deltaRelativeTolerance`, `gammaRelativeTolerance`); `volatilityBump` `0.01`;
  and `clock`, null at a recorded close and one of the offered clock rules at a
  working snapshot. The read returns `sensitivities.offered`, `clocks` and
  `clockRequired` for the page to send back; nothing is filled in.
- **What the surface states.** `sensitivities` on the surface echoes the
  selection with `clockSource` (`close_convention` or `request`), `clockZone`
  and `dayTo`, the instant theta's day runs to. Each series also has
  `inTheMoney`, and each expiry `atTheMoneyStrike` (the listed strike nearest the
  underlying settlement, the higher of two equally near) and `daysToExpiry`
  (the model's own year fraction as days).
- **Missing codes.** `option_at_intrinsic`, `sensitivity_unstable`,
  `option_sensitivity_clock`, `valuation_time_coverage`, or the code a refused
  sample raised. A series that did not calibrate has no analytics. A pinned
  clock the worker cannot count a day on selects no day: the surface is still
  fitted and its thetas are missing.
- **Inputs.** The discount-curve points the one-day roll read are recorded with
  the fit's other observations, so a revision of one flags the stored surface.
- **Work.** A solved series costs about 17 tree prices beyond its calibration:
  a base value, six samples on each of the price and volatility axes (half,
  whole and double bumps), a base for each of the three sampled requests and
  the day's sample. The expiries are valued in parallel.
- **Identity.** A refit is compared with the latest revision of the same
  location and the same stated selection (migration
  `0341_vol_surface_sensitivities.sql`).

These are the option model's own figures for the [Options page](futures-options.md#reads-and-entry-points).
They enter no Risk, VaR or P&L report.

## Stored revisions

A surface is stored as an immutable, bitemporal revision (migration
`0186_vol_surfaces.sql`):

- **Business key.** The exact product version (reference catalog, reference revision
  and code, by foreign key), the settlement business date and the vol model.
- **Location (D356, migration `0212_vol_surface_closes.sql`).** The recorded close the
  surface was fitted at (`close_id`, by foreign key to `market_data.market_closes`,
  which names its session and business day), or none for a working snapshot, and the
  valuation instant. A frame id is session-scoped (each sign-in sees a close under
  its own alias), so it is kept only as provenance and never locates a surface. A
  recorded close must be a close at the revision's valuation instant that pins every
  input of the revision. Working snapshots of one instant can pin different
  observations, so a read or fit-job status also takes the frame's pins: a revision
  is the frame's only when every input is one of them.
- **Business time.** The settlement business date (`effective_on`). For visibility
  it is the start of that UTC date, the date convention of trading contexts.
- **Knowledge time.** The trading publication protocol: each new revision takes the
  next `trading.publications` order, and its knowledge time is that publication's
  commit, certified into `trading.knowledge_receipts` by a later context capture.
  Reads use `security.trading_visible(catalog_id, publication_order, effective_at)`.
  No separate clock or client-supplied time is used, and a revision just recorded
  states no `knownFrom` until a capture certifies it.
- **Contents.** The resolved frame identity, the frame's valuation instant
  (`valuation_at`), the exact observation revisions the calibration consumed
  (`trading.vol_surface_inputs`), the solver policy, the surface as JSON and a
  digest. The `market_data.guard_knowledge_receipt()` trigger refuses updates and
  deletes.
  - The pinned observations are every observation the calibration read: each
    series' option settlement, its underlying futures settlement, and the
    discount-curve points behind each series' discount factor to expiry.
  - The valuation instant is an input because time to expiry runs from it.
  - The digest (`content_sha256`) is the worker's SHA-256 of its canonical
    serialization of the surface. The content is stored as `jsonb`, so the digest
    is not a digest of the stored or returned bytes. Recording compares it, with
    the content, to decide whether a refit reproduced the stored surface.

Recording follows D238:

- A request identity returns its first outcome; the same identity with different
  contents is refused with `vol_surface_input`.
- The inputs of a revision are its valuation instant and its pinned observations.
  If the key's latest revision at the same close (or working snapshot) and valuation
  instant has the same inputs, sensitivity selection, retained plot-input policy
  and content, it is returned and nothing new is stored. The same inputs under
  those same policies with different content are refused with
  `vol_surface_nondeterministic`. An explicit new plot-input policy records a new
  revision while preserving the old one.
- Different inputs, for example after a settlement or a discount-curve point is
  revised, or a fit at another valuation instant, record a new revision with its
  own knowledge time. Earlier revisions stay readable at earlier
  cutoffs.

A read returns the latest revision of the key at the context's close (or working
snapshot) and valuation instant, whose inputs the frame pins, visible at the bound
context, ordered by publication
order, never by row order. It never falls back to another date, another session's
close, the close a newer one replaced, another instant of the day or another model. It also states whether any pinned observation, settlement or
discount-curve point, has a newer revision known at that context (`inputsRevised`);
the read never refits.

## Fit jobs

A fit is a background job of kind `vol_surface_fit_v1` on the retained risk-job
queue (`risk.jobs`), next to portfolio risk, under the same lease, attempt,
cancellation and security-generation rules (migration `0187_vol_surface_jobs.sql`).
There is no separate queue, service or cache.

- **`POST /api/vol-surfaces/fit`** takes `{requestId, snapshotId, product, model,
  sensitivities}` and returns `{jobId, state}`. `sensitivities` states the
  [option analytics](#option-analytics) selection in full and is refused with
  `option_sensitivity_policy` when it is absent or unsupported. It is admitted only over working data, at the
  context captured for the working frame; the submit operation itself refuses a
  context not captured from current knowledge with `historical_read_only`. It
  holds a share lock on the catalog's knowledge clock, so a later certification
  waits for the submit to commit rather than superseding its context first.
  Admission pins the frame, the exact product version, the business date, the
  model, the solver policy, the engine version and the actor.
- The same request identity returns its first job. A queued or running job of the
  same person for the same key and pinned frame is returned instead of a second one.
  Native admission rechecks all three required authorities before returning,
  including after an advisory-lock wait. An expired token refuses with
  `risk_job_denied` and records no reuse alias (migration `0188`).
- A risk worker claims the job, rechecks the actor's authority, reads the pinned
  inputs, fits, and records the revision in the transaction that completes the job.
  That completion is fenced by the attempt's lease and rechecks the actor's
  authority and the security generation, so a lost lease or lost authority records
  nothing. A worker build claims only jobs admitted under its own engine version.
- A fit that fails records nothing and finishes the job `failed` with its code.
  Deterministic refusals at completion (`vol_surface_nondeterministic`,
  `vol_surface_input` and others) fail the job rather than being retried.
- The completed job records its measures: series, solved series and elapsed
  milliseconds. No runtime budget is enforced.
- The Risk page's job list reads portfolio risk jobs by their convention, so fit jobs
  do not appear there.

There is no scheduled fit: a job is submitted when someone asks, from the page or
through the same operation.

## Reading

**`POST /api/vol-surfaces/read`** takes `{context, product, model, axis, points}`, where `context`
is the trading read context the option operations use (`snapshotId`, convention
`futures_option_premium_v1`, `knowledgeMode` and, for a historical view,
`contextId`). It returns `snapshotId`, `model`, `solverPolicy`, `state` (`fitted` or
`notFitted`), the `revision` (`id`, `knownFrom`, `actor`, `inputsRevised`), the
caller's newest fit `job` for the key (`id`, `state`, `errorCode`), `canFit` and the
`surface`. The business date is always the context's own; the database refuses any
other date with `vol_surface_context`. The close and instant are those of the
context's frame. A replaced close stays readable as history and reads its own
surface; the header never offers it, and a fit there is new work, refused with
`close_replaced` (D334).

**`POST /api/vol-surfaces/library`** takes `{context, model, offset}` and, optionally,
`product`. It lists, newest business date first, the latest revision of each product
at each close (or working snapshot) and instant on or before the context's business
date and known at its cutoff: `revision`, `product`, `productName`, `closeId`,
`businessDate`, `valuationAt`, `knownFrom`, `close` (`session`, `businessDate`,
`inForce`, or null for a working snapshot), `here`, and `expiries`, `fittedExpiries`
and `series` counts. `here` marks the surface a read at the context's own close,
instant and pins returns; those rows come first, so a page shows them whatever the
library's size. A surface whose close the reader cannot see is left out. Pages hold
48 surfaces; `hasMore` says whether more follow. It also returns `canFit`. The page
reads it whole for the cards, and per product for the chips.

## Permissions

| Operation | Needs |
| --- | --- |
| Read a stored surface, and the page | `ReadMarketData` and `ReadTrades` |
| Submit a fit job | `PublishMarketData`, `ReadTrades` and `ReadMarketData`, in the current workspace |
| Run and record a fit (worker, as the job's actor) | the same three, rechecked before the fit and at completion |

Reading needs `ReadTrades` because revisions ride the trading visibility protocol.
Fitting needs `ReadTrades` because the currency's discount default is read through a
trading read. A historical context is refused with `historical_read_only`. Agent
principals are refused at admission, and agents have no fit operation; the page is
one the assistant does not read.

## States and errors

The page states:

- **No option products** at the context, **no product matches** a search, or
  **choose an option product**.
- **Reading the surface…**
- **Not calibrated at** the close, with *Calibrate at* that close for a person who
  may fit in the current workspace, and *Open* the newest close the product is
  calibrated at. Others see that calibrating needs Publish market data and Read
  trades; a historical view says to calibrate in the current workspace.
- **Calibrating at** the close, queued or running, checking back every 1.5 seconds
  while the page is shown. A hidden page sends no reads and reads at once when shown
  again. When the job finishes, the product's chips and the cards are read again.
- **Calibrated at** the close, with its knowledge time, or **Inputs revised after
  this fit** with *Recalibrate*. A failed or cancelled last job is shown with its
  code.
- **Each gap expiry** names its reason, and each cell without rich/cheap names its
  series' outcome.
- **A structured error**, for example a product that the context's reference data
  does not list, which is named rather than replaced by another product or by none.

A calibration request whose outcome is unknown keeps its request identity, and
*Retry the calibration request* sends the same request; no fresh calibration is
offered meanwhile.

Request-level codes:

| Code | Meaning |
| --- | --- |
| `vol_model_unknown` | The model is not a supported vol model |
| `vol_surface_product` | The product is not an option product of this market, or not readable |
| `vol_surface_context` | The context states no business date or was not captured, the read's date is not the context's, or it names no valuation instant |
| `vol_surface_no_settlements` | The frame has no settlement of any live listed series of the product |
| `option_expired` | Every listed series of the product is at or past its expiry |
| `discount_coverage` | No live listed series' expiry is covered by the discount curve |
| `vol_surface_input` | A malformed request, request identity reuse with other contents, an inconsistent fit input, or a surface that states no sensitivity selection |
| `option_sensitivity_policy` | The fit states no supported sensitivity selection, names a clock at a recorded close, or names none or an unoffered one at a working snapshot |
| `vol_surface_nondeterministic` | A refit at the same valuation instant and pinned observations produced different content |
| `historical_read_only` | A fit was requested from a historical context, or at a context not captured from current knowledge |
| `discount_curve_missing` | The currency has no discount default |

Per-expiry codes are `option_expired`, `vol_fit_insufficient_strikes` and `vol_fit_nonpositive`.

## Measured runtime

Fitting is dominated by calibration: each series runs the full pinned tree solve.
The first build measured 11.7 s for one product with 30 series in a Debug build,
which missed a 5 s synchronous budget, so fits became jobs. With the example's 70
and 71 series per product, a Debug `--vol-surface-fit` check took 24.7 to 31.2 s per
product across runs. A fit job took 18.3 to 22.6 s end to end in the preview, and
the fit inside the risk worker took 15.9 to 20.9 s across runs. These are local
observations, not a performance qualification; see
[verification](verification.md#volatility-surfaces-d304).

## Remaining work

- **Valuing options from the surface.** No interpolated value for an unsettled
  series, and no change to option valuation, Risk or close.
- **Volatility in Risk.** The stored vega is the option model's own figure for
  the Options page. Volatility shocks, vega and VaR on volatility in Risk reports
  remain.
- **Scheduled fitting** after each close: a fit triggered by a close being recorded,
  for an explicitly configured, versioned list of products and model per session,
  run as a named service principal. A session with nothing configured fits nothing.
- **Reusing a surface at another close.** Not supported: carrying a smile to another
  day or session needs an explicit sticky-strike or sticky-moneyness policy and
  time decay, and rich/cheap against another day's smile measures the market's move,
  not mispricing.
- **Heavier vol models**, such as SABR, local or stochastic volatility, vol dynamics
  and arbitrage-free fitting across expiries.
- **Agent fit access.** Agents cannot submit fits, and the assistant does not read
  the page.
- **Settlement ingestion.** Option settlements remain synthetic example rows.
