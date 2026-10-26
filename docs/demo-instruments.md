# Common PPA and heat-rate trade consumers (D229)

PPA trades participate in the common trading workspace, position inspection,
interactive portfolio-risk catalog/report and retained risk jobs. The ordinary
isolated preview seeds eight futures, sixteen financial swaps, eight heat-rate
swaps and five PPA bindings. All examples and observations are synthetic.

## PPA valuation contract

The shared workspace selects `estimate_until_actual_v1`: estimate pending meter
quantities until the meter deadline, then require actuals. The selected policy
appears on rows and in report evidence and is retained with queued requests.
PPA values use `ppa_value_excluding_overdue`, displayed as “Value excluding overdue
cash”. Complete PV, overdue known and overdue estimated amounts remain separate
companion measures. Totals do not combine this measure with ordinary swap PV.

The adapter prepares independent interval sides, uses their native schedules and
exact discount inputs, and binds interval prices to the common connected scenario.
Base, up, down and nested scenarios evaluate the same admitted graph. Frozen
historical samples remain frozen; input dependencies identify affected work.
Native quote units and variable/frozen input use survive the bridge. External
trade quantities scale the unit instrument once.

A selected PPA scope must share an exact compatible definition/reference/market
bundle. Missing or incompatible inputs refuse the whole selected operation;
there is no inferred bundle substitution. Reports retain exact interval read
contexts. Workers can reread only contexts admitted by their own live bound job
attempt, under current service and represented-user permissions. The conservative
row/scenario work bound applies before evaluation.

The five demo bindings cover physical delivery, an indexed virtual structure,
opposite membership directions, shared instrument definitions and asset-generation
quantities. Each binding explicitly covers 73 hours from November 1 to November 4,
2031 in the retained market clock, crossing the autumn clock change. This coverage
is narrower than the full-month instrument terms and is visible independently.

## Heat-rate valuation contract

Heat-rate examples use the existing `factor_scheduled_financial_swap_v1`
contract (D227), with two floating sides and a typed contractual quantity factor.
The first side constructs its schedule from retained quantity parameters; the
factor derives proportional native second-side quantities using qualified unit
conversions. Both sides retain independent price observations and payment rules,
construct their own schedules and compute native PV. Instrument memberships apply
signs and relative quantities once.

Authoring explicitly selects the factor value, numerator and denominator quantity
references and conversion implementation. Exact references, factor and versions
are retained in the contract. Missing paths, inconsistent units or ambiguous
financial behavior refuse. No unit-code switch is added. The existing factor
contract supports settlement; its numerical, alternate-unit and lifecycle checks
remain required. See [factor contracts](factor-swaps.md) for qualified behavior.

## Local checks

The mixed-instrument gate exercises PPA-only directory admission, idempotent
bookings, retained contexts predating later trades, shared blotter/risk agreement,
independent heat-rate shocks and identical interactive/worker exact results.
Worker custody checks use concurrent jobs with disjoint admitted interval
contexts and revoke service authority while the represented user remains allowed.
Browser checks exercise the shared PPA position and retained terms, heat-rate
entry and narrow layout. Numerical and temporal evidence uses disposable stock
PostgreSQL and synthetic inputs; it does not qualify a hosted deployment.

Run the mixed gate with:

```sh
python3 tools/run_preview.py --demo-instruments-check --browser-check \
  --pg-bin /path/to/postgresql/bin --dotnet /path/to/dotnet \
  --browser /path/to/chrome
```

The existing `--ppa-bookings-check` and `--scheduled-swap-check` gates cover
booking/history, exact-bundle refusals and existing swap settlement regressions.
