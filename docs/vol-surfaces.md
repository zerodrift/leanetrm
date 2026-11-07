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
- for the chosen product, its calibration state with *Recalibrate* where allowed,
  and **Calibrated at**: a chip per close the product has a surface at, newest first;
- a legend for rich/cheap.

A surface belongs to the close it was fitted at, so the page never shows another
close's surface in place of the selected one. When the selected close has none, a
panel says so and offers *Calibrate at* that close to a person who may fit, and
*Open* the newest close this product is calibrated at. Opening a close, from that
panel or from a chip, is the header's own move: an offered close is chosen in the As
of control, so every page reads it. A close of another session is not switched to
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
both products show rich and cheap points. Each product lists five expiries of seven
strikes, each as a call and a put: 70 series for LO, and 71 for ON, which also keeps
its existing deep in-the-money call that settles at intrinsic. None of these values
is a market level.

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
  instant has the same inputs and the same content, it is returned and nothing new is
  stored. The same inputs with different content are
  refused with `vol_surface_nondeterministic`.
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

- **`POST /api/vol-surfaces/fit`** takes `{requestId, snapshotId, product, model}`
  and returns `{jobId, state}`. It is admitted only over working data, at the
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

**`POST /api/vol-surfaces/read`** takes `{context, product, model}`, where `context`
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
| `vol_surface_input` | A malformed request, request identity reuse with other contents, or an inconsistent fit input |
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
- **Vega and volatility shocks.**
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
