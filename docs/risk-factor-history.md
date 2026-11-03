# Aligned dated-factor history

D296 adds the managed `FactorHistory` operation, its authorized
`POST /api/portfolio/risk/history` adapter, and the graphical history panel in Risk.
This is the common calibration input for subsequent statistical engines. It does
not calculate VaR or expected shortfall.

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

The first profile is `dated_factor_common_session_changes_v1`. Its explicit
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

The caller selects the exact calendar version, inclusive first date, exclusive
window end, local close time with seconds, and explicit reject policies for
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
grant no permission. The panel uses Risk's portfolio/book/trade selection, leaves
every financial input unselected initially, and rejects responses inconsistent
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
