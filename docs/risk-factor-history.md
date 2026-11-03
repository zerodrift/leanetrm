# Aligned dated-factor history

D296 adds the managed `FactorHistory` operation, its authorized
`POST /api/portfolio/risk/history` adapter, and the graphical history panel in Risk.
This is the common calibration input for statistical engines. History preparation
does not itself calculate covariance, VaR or expected shortfall; the parametric
engine does that after alignment.

## Guided VaR setup (D301)

1. In **Risk**, choose the portfolio, book(s), and optionally individual trades.
2. Open **Parametric VaR**. Starting settings are **99% confidence**, a
   **six-month history window**, **one business-day horizon**, **zero drift**, and
   at least **60 complete common daily changes**. A sole calendar is selected
   automatically; choose a calendar when more than one is offered.
3. **Official daily close** is the initially permitted price type. Add and order
   alternatives only when they are acceptable for this calculation. The pinned
   feed/source is unchanged. Select a shorter window or custom dates if the
   calendar does not cover the requested window; dates are never shortened silently.
4. Click **Run VaR** or **Queue VaR job**. End-of-day history is prepared
   automatically, then the engine calculates covariance, native finite-difference
   sensitivities, VaR and expected shortfall. **Preview price coverage** is optional.
5. Review the aligned-factor grid, coverage, exclusions and the report's
   correlation/covariance matrix. Open a grid cell for its selected type, actual
   observation timestamp, source and available alternatives. Green identifies an
   official close, amber an allowed alternative, and grey unavailable data.

**Advanced settings** exposes the minimum common changes, drift, trading-tick step
and delta tolerances. The starting finite-difference settings are one native
trading tick, zero absolute tolerance and `0.0001` relative tolerance. These are
visible editable inputs, sent explicitly and retained with the calculation;
missing API fields still fail. An unavailable result never relaxes the settings.

Prepared history is reused only for the same account, full retained context,
scope, pricing policy, calendar, window and history/price-type settings. Changing
confidence alone can reuse it. Scope, context or history-input changes invalidate
it. An unconfirmed queued command must be reconciled before another is submitted.

## End-of-day sampling and price types

The guided profile uses `daily_latest_visible_session_end_v1` with
`ordered_price_types_fixed_series_source_unit_contract_v1`. It captures each
business date's **two local-midnight boundaries**, UTC instants and offsets under
`captured_calendar_day_windows_v1`. The request uses the fixed `end_of_day`
sentinel, without time entry. A date's window is half-open `[start, next midnight)`;
it includes fractional late marks and preserves 23/25-hour DST days. Only dates
whose exclusive end is at or before both the forecast market and valuation
cutoffs are complete. Unfinished dates are omitted visibly. The forecast knowledge
instant and certified publication prefix still bound every observation; choosing
end of day never advances the forecast or admits information learned later.

The first allowed type is `official_close_v1`: domain `DailySettlement` with
`Final` publication state. Additional supported choices distinguish final trader
marks, final index fixings, final parameters, final contract settlements,
preliminary trader marks and preliminary daily settlements. This is the recorded
publication classification, not an inferred claim about a free-text feed name.

Selection tries the explicitly ordered list per factor and date. Within a role,
the latest timestamp and unique visible correction head are resolved **before**
state eligibility. A latest preliminary head cannot revive an earlier final.
Missing or ineligible official data can use the next type only when the user has
allowed it; correction forks or changed units/contracts refuse that endpoint
rather than hiding behind a fallback. No price is carried from a previous date.
The current-PV factor's original source, role, state and base anchor stay intact;
only calibration can consume the explicitly permitted alternate roles/states.

Every selected type, available type and official-close status is retained in the
manifest's `dayPrices`. Endpoint observation keys resolve to exact timestamps in
its retained evidence. A change between unlike selected types is flagged in the
grid: it includes that price-type difference, with no inferred basis adjustment.
Migration 0180 extends the trusted clock-capture/retention guards; no raw worker
permission is broadened. Legacy exact-close requests, receipts and manifest hashes
keep their existing contract.

## Universe and source mapping

The operation starts with the complete live dependency closure of the selected
portfolio, books and optional individual trades. It uses the same retained
materializers and common PV/factor contracts as scenario risk. It never discovers
exposure from nonzero Greeks or an instrument-type filter, and never loads every
available quote as though it were held.

Shared inputs become one column keyed by their exact input series/use. Conflicting
source, role, publication state, quote, units, dated contract or base observation
refuse before deduplication. Connected curve references are unioned deterministically.
Explicit frozen facts are excluded from calibration. Every live column remains
present even when its observed change is exactly zero.

The common profile is `dated_factor_common_session_changes_v1`. The original
exact-close alignment remains available through its explicit request contract. Its
explicit
policies are fixed current dated-input mapping, unique visible correction head,
exact session close, native absolute change, common complete adjacent sessions,
and exact source/role/state/unit/contract regime. Arithmetic uses
`exact_18_fractional_abs_lt_1e24_v1`. No contract roll, proxy, regime substitution,
percentage/log transform, carry or interpolation is inferred.

## Forecast coordinates and correction visibility

The forecast retains the financial context ID, valuation and market cutoffs,
certified knowledge instant, **market observation** publication prefix, exact
definition/snapshot identities and connected graph manifests. The schedule
publication prefix is a separate coordinate and cannot substitute for market
knowledge. Both the certified instant and market prefix bound every observation;
future-observed marks also remain outside the forecast market cutoff.

Visible correction heads are resolved before publication-state eligibility.
A later preliminary correction cannot revive its superseded final ancestor.
Competing visible heads make an endpoint unavailable; event order does not choose
an arbitrary winner. A future-known correction cannot hide the earlier visible
head. Exact current anchors retain their visible ancestry even when they fall
outside the requested lookback. Missing, cross-fact or temporally inconsistent
visible ancestry refuses the manifest.

## Sessions and retained clock evidence

For legacy exact-close alignment, the caller selects the exact calendar version,
inclusive first date, exclusive window end, local close time with seconds, and
explicit reject policies for
ambiguous and nonexistent times. The server reads the authorized reference clock
and resolves every original business date once. The
`captured_calendar_session_closes_v1` contract retains serialized rules, their
SHA-256 fingerprint, and the resolved UTC instant and offset for every session.

Replay validates that fingerprint, complete date coverage, local/offset arithmetic,
ordering and forecast cutoffs, then uses captured instants without consulting the
current time-zone database. This avoids relying on the platform-dependent full
`TimeZoneInfo` serialization round trip ([runtime issue](https://github.com/dotnet/runtime/issues/63889)).
It checks internal consistency, rather than certifying arbitrary caller-authored
offsets against opaque rules. The HTTP adapter accepts references and choices;
it never accepts client-resolved sessions, observations or clock evidence.

Pairing precedes mark availability filtering. Missing Tuesday therefore rejects
Monday–Tuesday and Tuesday–Wednesday; it never creates a Monday–Wednesday change.
A holiday or DST weekend preserves the actual UTC separation between adjacent
calendar sessions. The supported horizon is one adjacent business-session change,
without annualization or square-root scaling.

Only complete common vectors enter `samples`. Each retains both endpoint marks,
their certified knowledge and complete correction lineage, and exact additive
changes in native units. Zero and negative prices are valid. Rejected intervals
retain partial endpoints and per-factor reasons for missing, stale, off-close,
source/role, publication-state, conflicting-head or mapping-regime failures.
The explicitly selected minimum counts complete **changes**, not endpoint marks.
Insufficient history produces unavailable coverage while preserving the evidence.
Excluded portfolio positions also prevent a complete-scope calibration claim.

## Admission, display and export

Admission bounds are 1,024 live factors, 2,049 sessions, 250,000 factor/session
cells, 500,000 observation versions, 3,660 calendar days and a 32 MiB response.
The adapter validates the matrix before loading source history. SQL reads only
the exact series, bounded close window and required anchor ancestry, with certified
knowledge predicates and a global observation count limit. It projects consumed
typed fields rather than unused source-event text. These are refusal limits,
not qualified production throughput claims.

The operation uses the existing Risk retained-context and trade-scope admission.
Current trade, scenario, market and exact-reference authority is checked again
after evidence assembly and immediately before publication. Captured identities
grant no permission. The panel uses Risk's portfolio/book/trade selection,
materializes the visible server-provided starting profile, and rejects responses
inconsistent
with its submitted scope, snapshot, window or policies.

The panel displays coverage, exclusions and rejected intervals, plus one selected
factor's graphical changes and exact endpoint table. Changing the displayed column
does not change the calibration universe. Context, scope, account or input changes
clear the result. Export rereads the captured canonical request under current
authority and requires the same manifest identity before downloading JSON.
Managed `FactorHistory.Replay` reconstructs the exact manifest from retained
evidence and checks its identity; it is not an authorization service.

The owned standard preview and dedicated history suite seed generated closes for
the existing dated factors from 6 January through 18 December 2020: 250 weekday
closes and 249 adjacent changes. `weekdays_utc` means weekdays without exchange
holiday claims, at the explicitly selected UTC close. Existing curve and contract
names remain their normal domain labels; generated-data provenance belongs to
the development environment. Repeated seed calls preserve observation business
keys. These paths are examples, not redistributed historical exchange prices.

## Verification and remaining work

The focused managed checks cover shared factors, exact zero/negative changes,
culture-independent identities, future knowledge, ineligible successors, missing
sessions, holidays, off-close marks, forks, regimes, cutoff/policy/minimum refusal,
ancestry, retained replay, DST and admission bounds. Owned HTTP checks cover
selected and mixed scopes, the distinct market/schedule prefixes, canonical
reread, corrected history and out-of-window anchor ancestry, rejected forged
policies/schedules, bounded typed evidence and current authority. Browser checks
cover explicit setup, exact graphics/tables, reauthorized export, unavailable
coverage, mobile layout and invalidation on input or parent-scope changes.

No table or worker job is added for this bounded read/export consumer.
[Parametric risk](parametric-risk.md) subsequently adds covariance, delta-normal
VaR/ES and a durable statistical-report archive with trusted clock capture and
separate source pins. Full-repricing historical simulation, rolling backtests,
Monte Carlo/PCA/hybrid and EVT remain subsequent increments.
