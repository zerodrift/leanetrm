# Synthetic option-chain impact inventory

Before this change, the shared exchange fixture supplied five expiries per ON/LO, seven call/put
strikes per expiry and one extra ON intrinsic call: 141 series and 564 settlement observations over
four snapshots.

## Consumers

- `tests/OpenEtrm.Api.Checks/AgentToolChecks.cs` — managed or HTTP/native check.
- `tests/OpenEtrm.Api.Checks/PhysicalOptionValueChecks.cs` — managed or HTTP/native check.
- `tests/OpenEtrm.Api.Checks/PnlPersistenceChecks.cs` — managed or HTTP/native check.
- `tests/OpenEtrm.Api.Checks/VolSurfaceFitChecks.cs` — managed or HTTP/native check.
- `tests/browser/futures-options.mjs` — actual-browser regression.
- `tests/browser/options.mjs` — actual-browser regression.
- `tests/browser/vol-surfaces.mjs` — actual-browser regression.
- `tools/check_agent_tool_http.py` — managed or HTTP/native check.
- `tools/check_cash_option_value.py` — managed or HTTP/native check.
- `tools/check_futures_option_lifecycle.py` — managed or HTTP/native check.
- `tools/check_futures_option_portfolio.py` — managed or HTTP/native check.
- `tools/check_futures_option_storage.py` — managed or HTTP/native check.
- `tools/check_gas_park_custody_upgrade.py` — managed or HTTP/native check.
- `tools/check_hybrid_monte_carlo_risk.py` — managed or HTTP/native check.
- `tools/check_lifecycle_actions.py` — managed or HTTP/native check.
- `tools/check_lifecycle_option_expiries.py` — managed or HTTP/native check.
- `tools/check_lifecycle_option_scheduling.py` — managed or HTTP/native check.
- `tools/check_lifecycle_risk_scope.py` — managed or HTTP/native check.
- `tools/check_lifecycle_valuation_browser.py` — managed or HTTP/native check.
- `tools/check_physical_option_native_projection.py` — managed or HTTP/native check.
- `tools/check_recorded_cash.py` — managed or HTTP/native check.
- `tools/check_recorded_cash_upgrade.py` — managed or HTTP/native check.
- `tools/check_risk_fit_authority.py` — managed or HTTP/native check.
- `tools/check_selected_physical_authority.py` — managed or HTTP/native check.
- `tools/check_vol_surface_admission_expiry.py` — managed or HTTP/native check.
- `tools/check_vol_surface_api.py` — managed or HTTP/native check.
- `tools/check_vol_surface_fit.py` — managed or HTTP/native check.
- `tools/check_vol_surface_storage.py` — managed or HTTP/native check.
- `tools/exchange_fixture.py` — fixture owner.
- `tools/run_preview.py` — isolated demo and integration runner (exchange and cash P&L, optional
  exchange installation).
- `tools/seed_exchange_positions.py` — synthetic futures-only demo seeding; option additions do not
  alter its holdings.

## Impact

- The prior storage checks pinned 141 contracts, 70/71 series and 564 observations; the widened
  expectations are 261 contracts, 130/131 series and 1,044 observations.
- Agent catalogue checks read expiry-specific chains; original series remain, new rows increase
  catalogue size.
- Fitting/analytics process more unique series and tree prices. Runtime and retained response size
  increase; measure rather than infer.
- Options, strategy, lifecycle and risk checks can select a different first listed series. The
  independent LO Greek check is pinned to its original January 2027 put at 58.00, preserving its
  numerical tolerance. The existing 35-pair near-money parity qualification also keeps its original
  domain and tolerance; added discounted American wings are not assumed to have the same vega. Price
  reproduction and sign checks cover all added ON series; managed calibration comparisons cover both
  products. The parity check does not qualify new-wing Greek accuracy.
- Exact existing marks/strikes, intrinsic and missing-settlement specimens, dates, reference
  identifiers and existing observations must remain unchanged.
- Browser chain/heatmap dimensions and mesh topology expand. Add axes/points/gap checks; preserve
  linked highlights and existing navigation checks.
- Storage-only surfaces in `check_vol_surface_storage.py` use deliberately synthetic JSON and do not
  constitute executable axis inputs. Migration 0360 versions refit comparison without modifying
  retained content; old axis reads explicitly refuse missing inputs.
- Current demo documentation now states thirteen strikes. Historical measured runtimes retain their
  workload labels.

The added wings use a separately named flatter synthetic volatility profile, authorized for these
additional series only; pricing and clearing rounding remain the existing generator's.

`tools/check_all.py`, `tests/test_check_all.py` and `tests/browser/run.mjs`
orchestrate these consumers. The widened fixture is also exercised through those
existing complete-gate entry points.
