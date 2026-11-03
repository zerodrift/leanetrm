# Volatility surface

Status: implemented locally as [volatility surfaces](../../vol-surfaces.md), decision
D304 in the [decision record](../../decisions.md). The design below is kept as
written except where noted; the shipped behaviour is in those documents.

## Why

Listed American options on futures (D288) are valued one series at a time. For each
series, the volatility is solved so that the pinned tree reproduces that day's
settlement, and nothing relates one series to another. A trader therefore cannot see
the shape of a product's volatility, or which options settle rich or cheap against
that shape. [Remaining work](../../futures-options.md#remaining-work) lists volatility
surfaces, volatility models and volatility graphics as the next release.

This design is the first piece of that work. A trader opens one option product on
one settlement date and sees three things:

- the market implied volatility of every series;
- a smooth fitted surface through those volatilities;
- how far each series sits from the surface: its rich/cheap.

The user asked for exactly this: rich/cheap, a heat map, "and also it just looks
cool". The pictures are meant to be good-looking as well as correct.

## Goals

1. **A backend fit under a named, versioned vol model.**
   - The surface is fitted by a vol model with an identifier and a version, such as
     `smile_quadratic_log_moneyness_v1`.
   - Every result records the model and version that produced it, as the pricers do.
   - One model ships now. Others can be added later as further versions in the same
     slot, without changing callers.
2. **Rich/cheap is market minus fit**, in volatility points, for every series with a
   solved market volatility.
3. **A Vol surfaces page** in the Markets group of the sidebar, with three linked
   views of the same result:
   - **Heat map:** expiry by strike, coloured by rich/cheap.
   - **3D surface:** the fitted surface as a mesh, rotatable by dragging, with each
     market volatility drawn as a point on a stalk from the surface.
   - **Smiles:** one chart per expiry, with the fitted smile as a line, the market
     volatilities as points, and rich/cheap as bars beneath.
4. **Linked highlighting.** Hovering a series in any view highlights the same series
   in the other two and shows its values. This is my call; the user did not ask for
   it.
5. **Explicit historical context.** The page reads under the selected business date
   and resolved knowledge cutoff, like the other market pages. Every value comes from
   one pinned snapshot.
6. **Links from options.** The futures option product view and the option trade view
   link to the surface for their product, at the context's date.
7. **Example data that shows a surface.** The synthetic example catalog gains enough
   strikes and a synthetic smile for the fit and the rich/cheap to mean something
   (see Constraints).

## Non-goals

- **Valuing options from the surface.** Existing valuation, risk and close are
  unchanged. Each series is still calibrated to its own settlement and held under
  `option_implied_volatility_held_v1`. There is no vega, no vol shock, no
  interpolated value for an unsettled series, and no change to Risk. This is my call,
  made because the user was unsure: a wrong picture is visible and harmless, but a
  wrong surface feeding valuation would quietly move option values and Risk. Valuing
  from the surface is the next piece, with its own design and adversarial review.
- **Heavier vol models.** SABR, stochastic or local volatility, vol dynamics (sticky
  strike or sticky delta) and arbitrage-free fitting across expiries wait for a
  consumer that needs them.
- **Fitting on a schedule.** There is no automatic fit after each close yet. A fit
  job is submitted when someone asks for it, from the page or through the same
  operation, and the pilot data step submits jobs for its loaded dates. A scheduled fit waits
  for a close-triggered job design.
- **Settlement ingestion.** Option settlements remain synthetic example rows.
- **Strategies, payoff diagrams and option-chain screens.**
- **Agent writes.** The surface is a read; nothing books or amends from it.

## Anti-goals

These would make the work a failure even if it ran:

- **A default standing in for missing data.** Examples: a guessed smile for an expiry
  with too few strikes, a silent flat volatility, or a surface built from a
  different date's settlements. Missing or insufficient input is a labelled gap or a
  structured error.
- **Maths in TypeScript.** The page draws what the backend returns. It does no
  fitting, interpolation, moneyness or rich/cheap arithmetic, except for mapping
  values to pixels and colours.
- **Non-deterministic results.** The same pinned inputs and model version must give
  identical numbers on every host.
- **A surface that looks plausible but is not.** If the fit is poor, the page says so
  with the fit's residuals. It never smooths the problem away.

## Constraints

From AGENTS.md and the architecture, for this work:

- **Backend calculation.** The managed backend does all fitting and rich/cheap,
  using the project-owned fixed-point arithmetic (`LatticeDecimal`, 30 fractional
  digits, half-even) and the project's own `ln` and `sqrt`. There is no
  numerical-library dependency.
- **Inputs to the fit.**
  - The market volatilities are the existing per-series solved volatilities.
  - Each is solved by `ImpliedVolatility.Solve` under the pinned
    `american_futures_option_crr_v1` policy, from the snapshot's `daily_settlement`
    option and underlying observations, and the currency's payment-discount
    selection.
  - There is one solver, and the surface reuses it.
- **Outcomes the fit does not take in:**
  - A series that falls back to `option_settlement_at_intrinsic_v1` has no market
    volatility. It is shown as "at intrinsic" and left out of the fit.
  - A series whose solve raises a named error, such as `option_settlement_missing`
    or `option_settlement_above_bound`, is shown with that reason and left out.
  - A series at or past expiry is left out of the fit and shown with
    `option_expired`. A product with no live series is refused with
    `option_expired`, and one whose live series all fall outside the discount curve
    with `discount_coverage`.
- **Errors for the whole request:**
  - A request for an unknown or unseen product, or one with no series, raises a
    structured error.
  - A currency without a discount default fails the read with
    `discount_curve_missing`, as today.
- **Model selection is explicit.**
  - The request names the vol model and version.
  - The page sends the single shipped model, shown on screen, and the response
    echoes it.
  - An unknown model or version raises `vol_model_unknown`. Nothing selects a model
    implicitly.
- **Authorization.** The read needs `ReadMarketData` and `ReadTrades`
  (implementation ruling: stored revisions ride the trading visibility protocol,
  which requires `ReadTrades`). It goes through one authorized business operation. A later agent tool
  would call that same operation.
- **Historical context.**
  - The read carries business date and knowledge cutoff, as other market reads do.
  - It never substitutes current data.
  - The response records the snapshot identity it used.
- **Stored surfaces are bitemporal (decided by the user).** See "Stored surfaces"
  below. The tables' named consumer is the Vol surfaces page, and the later
  valuation piece will price from the same records.
- **Example data.**
  - The current catalog has three strikes per expiry, and every settlement comes from
    one flat synthetic volatility (`0.60` for ON, `0.40` for LO). A fit through that
    is exact and every rich/cheap is zero.
  - `tools/exchange_fixture.py` therefore gains:
    - at least seven listed strikes per expiry on the existing grid;
    - settlements from a synthetic smile, meaning volatility as a function of
      log-moneyness and expiry;
    - a small deterministic per-series offset, so some series come out rich and some
      cheap.
  - It stays explicitly synthetic, with no real market levels.
  - Existing checks that pin settlements, series counts or result digests are updated
    deliberately, never loosened.
- **Commodity-neutral.** Nothing in the fit or the page names gas or crude. It works
  for any futures-option product.
- **TDD.** Behaviour checks are written first and are independent of the algorithm.
  For example, a surface generated from known smile parameters is recovered, and
  rich/cheap equals an offset deliberately added to one series.
- **Decision record.** A decision entry (next free number, D304 after the master merges) records the
  vol-model contract, the first model, and view-only scope. docs/futures-options.md
  "Remaining work" is updated to match what ships.

## Decided

- **Rich/cheap.** Market implied volatility minus fitted volatility, in volatility
  points (decided by the user).
- **Views.** All three: the heat map, the 3D surface and the smiles (decided by the
  user).
- **Vol model slot.** A named, versioned vol model contract with one implementation
  now (agreed by the user).
- **Scope.** View-only first (my call, above).
- **Storage.** Fitted surfaces are stored bitemporally (decided by the user).
- **Placement.** A new page in Markets, with links from the option product and trade
  views (my call; the user was unsure and did not object).

## The first vol model (my call)

`smile_quadratic_log_moneyness_v1` fits one smile per expiry, independently.

- **Coordinate.** k = ln(K / F), where F is that expiry's underlying futures
  settlement from the same snapshot. The underlying can differ between expiries.
- **Smile.** σ̂(k) = a + b·k + c·k².
- **Fit.**
  - Ordinary least squares over the expiry's fitted points, solving the 3×3 normal
    equations exactly in `LatticeDecimal`.
  - Points are equally weighted.
  - Each strike contributes one point: the out-of-the-money right's volatility (the
    call above F, the put below; the call at exactly F). The in-the-money American
    series carries early-exercise effects, so leaving it out keeps the smile from
    being pulled around by them.
- **Rich/cheap is reported for every series with a solved volatility, both rights.**
  - It is the series' market volatility minus σ̂ at its strike.
  - An in-the-money series' rich/cheap therefore shows how far it sits from the
    out-of-the-money smile.
- **Minimum data.**
  - An expiry needs at least five distinct fitted strikes, spanning both sides of F.
  - Otherwise the expiry is returned as `vol_fit_insufficient_strikes`, with its
    market points and no fitted smile, and the page shows a labelled gap.
  - Five leaves two degrees of freedom above the three parameters, so the residuals
    mean something.
- **Quality.**
  - The result reports each expiry's parameters, the number of points, the
    root-mean-square residual and the largest absolute residual.
  - A smile that goes to zero or below anywhere inside the fitted strike range raises
    `vol_fit_nonpositive` for that expiry, which then shows as a labelled gap.
- **Across expiries.**
  - No interpolation in time; the 3D mesh is drawn per expiry.
  - Between expiries the page draws straight panels only as a visual join, labelled
    as such in the legend.
  - The response gives the fitted σ̂ on a strike grid per expiry, so the page never
    evaluates the polynomial itself.
  - The grid spans each expiry's fitted strike range at the product's listed strike
    interval. There is no extrapolation beyond the outermost fitted strikes.
- **Rounding.** Volatilities and rich/cheap are returned at 30 digits, and the page
  shows them to two decimals of a volatility point. Display rounding is presentation.

## Stored surfaces

Decided by the user: surfaces are stored, bitemporally. The details are my calls.

- **Two times.**
  - The business time is the settlement business date the surface describes.
  - The knowledge time is when the surface revision was recorded, taken from the
    committed transaction, as for other recorded facts. It is never a time the
    client supplies.
- **Business key.** The exact product version, the business date and the vol model
  identifier and version.
- **What a revision holds.**
  - The resolved snapshot identity and its valuation instant, stored on the
    revision.
  - Every observation revision the calibration consumed, pinned exactly: each
    series' option settlement, its underlying futures settlement, and the
    discount-curve points behind its discount factor to expiry.
  - The solver policy identifier.
  - Everything in the response shape below: per expiry, per series, and the fitted
    grid.
  - Revisions are immutable once written.
- **Writing is a background job (decided by the user).** One authorized
  operation, *fit vol surface*, admits a job; a worker does the fit.
  - **Why:** one product fits in about 12 seconds today (each series runs the full
    pinned tree solve). The user chose a job over a longer synchronous request.
  - **Admission.**
    - It needs `PublishMarketData`, plus `ReadTrades`, because the currency's
      discount default is read through a trading read today. It needs a request
      identity.
    - It is admitted only at a context captured from current knowledge; the
      operation itself refuses any other context with `historical_read_only`.
    - It pins the exact input frame, the product, the business date, the vol model
      and the actor.
    - The same request identity returns the same job (D238).
    - A job already queued or running for the same key and the same pinned frame is
      returned instead of a second one.
  - **Queue (my call).** The job uses the existing retained risk-job queue and its
    replicated risk workers, as a second job kind (`vol_surface_fit_v1`) next to
    portfolio risk. It has the same lease, attempt, cancellation and security
    generation rules. This adds no new queue or service.
  - **Execution.**
    - The worker rechecks the actor's current authority before the fit and again
      at publication.
    - It fits from the pinned frame and records the revision with the rules below,
      in the transaction that completes the job.
    - A fit that fails records no revision and finishes the job as `failed`, with
      its structured code.
  - **Recording rules.**
    - A revision's inputs are its valuation instant and its pinned observations:
      time to expiry runs from the valuation instant, and the solve reads the
      settlements and discount factors.
    - If the latest revision for the key has the same inputs, the job completes with
      that revision and records nothing new. The fit is deterministic, so a
      recomputed result that differs from it fails the job with
      `vol_surface_nondeterministic` and is never stored.
    - If the inputs differ (a settlement or discount-curve point was revised, or
      the fit is at another valuation instant), it records a new revision with its
      own knowledge time. The earlier revision stays readable at earlier cutoffs.
    - The stored digest is the worker's SHA-256 of its canonical serialization,
      compared with the content at recording. The content is stored as `jsonb`, so
      the digest is not a digest of the stored or returned bytes.
    - A request-level error (unknown product, no settlements,
      `discount_curve_missing`, `vol_model_unknown`) records nothing. Per-expiry
      gaps and per-series reasons are part of a successful revision.
- **Reading.**
  - A read at (business date, knowledge cutoff) returns the latest revision for the
    key whose knowledge time is at or before the cutoff, ordered by knowledge time,
    never by row order.
  - It never falls back to another date's surface or another model's.
  - With no such revision, the page shows "not fitted for this date". A user who
    may fit gets a *Fit now* button; others see who can fit it.
  - **Job state.** The read also returns the newest fit job for the key that the
    caller may see: queued, running, failed (with its code) or cancelled. While a
    job is queued or running, the page shows its progress and checks back until it
    finishes. It shows a failed job's reason next to the button.
  - **Out-of-date state.** The read also checks whether any pinned observation,
    settlement or discount-curve point, has a newer revision known at the cutoff
    than the one the surface pinned. If so, the
    surface is still shown, labelled "inputs revised after this fit", with a *Refit*
    button for those allowed to fit. The read never refits by itself.
- **Cost.** Reading is a lookup. The fit runs off the request path; its runtime is
  measured and recorded (series, solves, elapsed) rather than held to a budget.
- **Agents.** A later agent tool calls the same read and fit operations under the
  same permissions. Fitting is a market-data publication, so it is not offered to
  agents in this piece.

## Response shape (outline)

One read returns, for a product, a date and a vol model:

- context: business date, knowledge cutoff, snapshot identity;
- the stored revision: its identity, knowledge time, who recorded it, and whether
  its inputs have since been revised (or "not fitted");
- model: identifier and version, plus the solver policy identifier;
- for each expiry:
  - the expiry instant;
  - the underlying series and its futures settlement F;
  - the fit status (`fitted`, `vol_fit_insufficient_strikes` or
    `vol_fit_nonpositive`);
  - the parameters and quality figures;
  - the fitted grid of (strike, k, σ̂);
- for each series:
  - its code, right and strike;
  - its settlement and solve outcome (`solved`, `at_intrinsic` or an error code);
  - its market volatility, the fitted volatility at its strike, and rich/cheap;
  - whether it was a fitted point.

## The page (my calls)

- **Placement.** A **Vol surfaces** entry in the Markets group, with the same
  permission gate as the other Markets pages.
- **Controls.**
  - A product picker and the shared date and cutoff context.
  - The vol model is shown as a read-only label, since only one exists.
  - A short legend says what rich/cheap means.
- **Heat map.**
  - Rows are expiries and columns are strikes.
  - Cells are coloured on a diverging scale centred on zero, with the same scale in
    light and dark themes.
  - Cells for calls and puts at one strike are split.
  - A cell with no rich/cheap shows its reason on hover: at intrinsic, missing
    settlement, or an expiry without a fit.
- **3D surface.**
  - Drawn on a canvas by project-owned code. No 3D library is added.
  - Drag rotates the view; a reset returns to the default angle.
  - The fitted mesh is coloured by volatility level.
  - Market points sit on stalks, coloured rich or cheap.
  - It has to look good: smooth shading, depth ordering, readable axes.
- **Smiles.** Small multiples, one per expiry, drawn with the existing chart
  dependency (`echarts`), showing:
  - the fitted line;
  - market points, shaped by right;
  - rich/cheap bars;
  - RMS and maximum residual in each header.
- **Linking.** Hovering or focusing a series highlights it in all three views and
  shows a shared readout with series, right, strike, k, market, fitted and
  rich/cheap.
- **States.**
  - Loading, a structured error message, and "no series on this date".
  - Each per-expiry gap names its reason.
- **Look.** It must fit the app's existing style, work on narrow widths, and pass the
  project's browser checks.

## Settled after the builder check (my calls)

- **Which snapshot.**
  - The read resolves its snapshot exactly as option valuation does for the same
    context, and uses that snapshot's `daily_settlement` observations.
  - The expiry test (`option_expired`) uses the same valuation instant as option
    valuation.
  - A context with no settlements for the product returns a structured error, and
    the page shows "no settlements for this date".
  - No other date is substituted.
- **Units on the wire.** Volatilities, fitted values and rich/cheap are decimal
  fractions, as the solver returns them (0.012 is 1.2 volatility points). The page
  shows them as percentage points, which is display formatting like the app's other
  percentages, not calculation.
- **When an out-of-the-money solve fails.** If the out-of-the-money right at a strike
  has no solved volatility, that strike is not a fitted point. The in-the-money right
  is never substituted. If this leaves fewer than five strikes, the expiry is
  `vol_fit_insufficient_strikes`.
- **Heat map columns.**
  - The backend returns the column axis: the union of listed strikes across expiries.
  - Each cell states whether a series exists. "Not listed" is a different cell state
    from a data gap.
- **Example data.**
  - The existing deep in-the-money at-intrinsic series and the missing-settlement and
    missing-underlying cases stay, so the checks have a known cell for each state.
  - Settlements for the existing strikes change under the smile. The checks and
    digests that pin them are re-pinned in the same change, each with its reason.
- **The hosted pilot gets the same example options (decided by the user).**
  - After the release is deployed, the pilot's example data gains the denser strikes
    and smile settlements, so the surface can be seen there.
  - The load is installation work, kept out of the repository like the other pilot
    data steps.
  - It must be idempotent (D238).
    - A new series or settlement is added.
    - A changed settlement for an existing series is recorded as a new revision with
      its own knowledge time, never an overwrite. Earlier cutoffs still show the
      earlier values.
  - It then submits a fit job for each loaded settlement date and product, and
    checks that each one completes.
  - It runs only with the user's approval in chat. It is rehearsed on a disposable
    copy first, and its result is checked on the pilot afterwards.
- **Fit cost.** The first build measured 11.7 seconds for one product with 30
  series in a debug build, about 0.7–1.0 seconds per solve when optimised. That
  missed the original synchronous 5-second budget. The user chose a background job
  (see "Stored surfaces"), so the runtime is now recorded rather than enforced.

## Left to the builder

- API route naming, the operation's exact name, and the request and response field
  names, all following existing market-read conventions.
- Where the vol-model contract lives in `OpenEtrm.MarketData`. It is one interface or
  record pair with one implementation, not a framework.
- The exact synthetic smile parameters and per-series offsets, which must give
  visible rich and cheap points on both products and stay clearly synthetic.
- The colour scales, within the app's theme tokens and accessibility checks.
- The 3D projection maths, shading and interaction details.
- Whether the futures option product view's link opens the page in place or in a new
  dock pane, matching how the app opens related market pages.
- Browser checks for the page, the linking, and every gap and error state.
