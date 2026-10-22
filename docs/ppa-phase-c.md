# PPA Phase C qualification

Historical status: bounded local Phase C qualification complete, 2026-10-22. The complete
45-stage gate passes, and independent whole-branch review has no open material
finding. Phase C implements D192–D199 within the limits below. Settlement was outside Phase C; subsequent bounded previews are documented in
[settlement preview](settlement-preview.md). Generated-close PPA cutover remains deferred; production deployment is not
qualified by these synthetic checks.

## Implemented capability

The authorized `ppa/catalog` operation exposes `ppa_capabilities_v1`, exact
reference/definition metadata, five composition templates, explicit policies,
work/response limits and unsupported-feature names. Semantic validation remains
authoritative: a catalog reference is not evidence that every possible combination
is supported. PPA instrument validation identifies the version and term; shared quantity,
price, compiler and transport errors retain their existing codes/messages without
a universal slot/version envelope. Unknown request members refuse as `request_format`.

| Capability | Actual consumer and boundary |
| --- | --- |
| Fixed or indexed physical | One energy delivery plus its linked strike/indexed payment; optional linked fixed charge and certificate delivery/payment. Physical market value is separate from contractual cash. |
| Virtual fixed-for-floating | One indexed and one strike payment with explicit quantity bindings, with optional certificate delivery/payment. No energy delivery side. |
| Basis cash | Two indexed payments on distinct curves and the same explicit quantity definition. |
| Capacity agreement | Accredited capacity delivery and linked fixed/indexed payment; complete operating months only. Qualified period fixings are supported. |
| Prices and quantities | Native 15/60-minute energy, flat/stepped/exact anniversary prices, explicit index multiplier/adder; asset forecast/meter sources or supported flat/TOU contract profiles. |
| Booking lifecycle | Exact immutable terms, idempotent book/amend/terminate operations, optimistic concurrency and retained historical bindings. R3 refuses changed same-trade bundles and incompatible complete-book requests atomically. |
| Interactive valuation | Authorized base preview, period summaries and bounded read-only row inspection. Arithmetic, schedules and normalization remain on the server. |
| Guided UI | Canonical templates, physical/virtual booking, amendment/termination, blotter and inspection through the same API. API slot ordinals are 0–7; guided amendment refuses noncanonical layouts it cannot construct. |
| Durable risk | Read-only `close_book_risk_v1` uses an existing recorded close, immutable admission inputs, explicit work limits and persisted typed receipts. Current authority and live fencing are checked across retries and publication. |

Asset normalization sums the declared assets' compatible whole-asset quantities
at the requested stage, then divides by the sum of those same assets' capacity
bases in force. Each contribution and effective version is retained. Contract MW
is applied once by the trade binding. Missing contribution and explicit zero
remain distinct. Contract profiles are already per contract MW.

Coverage is native and half-open. Earlier binding economics retain their original
payment dates and quantities after amendment/termination; certificate holdings
retain generation vintage. `ValueExcludingOverdue` contains future dated PV and
retained holdings. Known and estimated overdue cash remain separate, undiscounted
companion balances; `CompletePv` is unavailable when any overdue row exists,
including offsetting balances. Payment lifecycle contains only projected and
delivered-unsettled states. No accepted obligation or paid state is implied.

Collected actual prices preserve D171/C9 compatibility restrictions. They do not
establish contractual settlement finality. Meter deadlines are evaluated for each
interval: at equality a missing eligible reading refuses under either pending
policy. Later-known corrections do not become known at an earlier cutoff.

## Storage, authority and recovery

Migrations 0087–0092 add interval source relationships, PPA reference bindings,
durable terms/bookings, bounded authoring reads, existing-close risk and renewable
ownership respectively. Prior migrations and historical job/run/report identities
remain unchanged. Fresh isolated probes verify forced RLS on all 27 new tables,
no runtime UPDATE/DELETE grants, and no PUBLIC execute on the 38 new function
names. Internal publication/terminal helpers remain unavailable to runtime users;
the scoped metadata helper cannot be assumed by those users.

Existing-close attempts keep the admitted close, knowledge context, publication
head, policies, factors and overall deadline. Renewable ownership cannot revive
an expired or replaced fence. CPU calculation runs without a database transaction;
short publication locks protect atomic complete-report/terminal-success commit.
Lost acknowledgement reconciles that exact committed outcome. Preparation retains
the authorization epoch share lock, so authority changes can wait during this
phase; it does not hold ownership locks that prevent renewal or cancellation.
Legacy generated-close workers retain their original operation gates and leases.

## Measurements and limits

These are local synthetic measurements on a 14-logical-processor, 48-GiB macOS
Arm64 host with pinned .NET 10.0.402, Node 24.22.0 and stock PostgreSQL 16. The
[implementation record](implementation.md#managed-covered-side-valuation) retains the
workload definitions and measurement protocols. Earlier measurements are not
new performance runs from final qualification.

| Evidence | Result and practical boundary |
| --- | --- |
| Complete HTTP base previews, after navigation fix | One five-side instrument/binding: 20-year hourly 9.435 s, 10-year quarter-hour 19.186 s. Allocations through result construction 6.605/12.915 GB; sampled process RSS 3.095/4.430 GB. Both samples met 20 seconds; no percentile or concurrent-load guarantee. |
| Request-owned series index optimization | Original controlled engine medians fell to 5.126/9.072 s; full validation/selection work is not repeated per month or side. No secondary optimization. |
| Slice6 managed risk benchmarks | Five workloads, three samples each. The three large risk cases took medians 886.614, 471.861 and 1,723.645 s; these remain engine measurements, not durable completion qualification for those books. |
| Accepted renewable-completion evidence | Actual ten-year hourly, one five-side instrument/binding/book: 326.922 s; replacement with lost commit acknowledgement 329.441 s. Exactly one complete report and success audit, base 40,408,960, 360 factors/721 scenarios/316,057,560 evaluated rows. |
| Final qualification recovery rerun | The same actual ten-year hourly workload completed in 332.465 s after worker replacement and lost commit acknowledgement, with exactly one complete report/success audit. All 721 scenarios and 316,057,560 rows matched independent values/work; 62 lease advances had median 5.003870 s (range 5.002391–5.008733). |
| Renewal and lock observations | Selected 15-second lease, five-second renewal; 62 observed advances had median 5.004067 s. Original preparation envelope 22.155–22.615 s; independent renewal/cancellation during bound preparation completed in about 15 ms. Physical CPU cessation remains cooperative. |

The bounded API accepts at most 8 members, ordinals 0–7, 100 assets, 256 bindings,
360 hourly or 120 quarter-hour months, two million evaluated rows per scenario,
four million preparation contributions, 35,136 detail rows over at most twelve
months, 32-KiB request bodies and 8-MiB responses. Durable risk admits at most two
billion row-scenario evaluations and 4,096 factors, with an explicit overall job
deadline of 1–3,600 seconds. Persisted report-size bounds are documented in
[automation](automation.md). These are refusal limits, not completion or
memory guarantees; preparation remains eager. The HTTP 20-second cancellation
deadline is not hard CPU preemption. An overall durable job deadline remains
explicit and is never extended by renewal.

No settlement acceptance, payment recording, net grouping or contractual cash
rounding is implemented. Other unsupported cases include nonlinear clauses,
custom contractual interval profiles, continuous escalation, resampling,
percentage-of-output normalization, cross-revision valuation, partial-month
capacity pricing, delivery services, FX, runtime templates and historical-session
PPA valuation. Retained revision inspection is available. No PPA-specific agent
tool or reference-definition authoring API is provided. Production deployment,
concurrent throughput and generated-close cutover remain unqualified.

## Final qualification scope

The complete 45-stage gate covers managed/frontend checks, isolated stock schema
and runtime authorization, independent PPA HTTP oracles, R3, quantity/scenario
reuse, browser/mobile/navigation checks, existing-close risk, renewable recovery
and legacy generated-close behavior. Independent whole-branch review additionally
examines migrations, current authority, temporal reads and worker recovery.

Final review reproduced two admission/documentation gaps: the catalog lacked an
unsupported-feature list, and managed validation admitted slot ordinal 8 although
storage allowed only 0–7. Failing checks preceded the additive metadata and managed
bound fixes. The highest valid ordinal remains supported.

The subsequent complete-gate attempt exposed a worker recovery defect: a database
cancellation callback threw after cancellation was requested, bypassing exact
committed-outcome reconciliation. The fix contains and records cancellation callback failures only after the token
is cancelled, joins renewal, then performs the original exact, currently
authorized completion lookup. Incomplete, denied and caller-cancelled outcomes
cannot become success. Deterministic checks and independent source review pass;
the actual replacement/lost-ack recovery rerun and complete 45-stage gate also
pass. No migration,
economic policy, authorization rule or numerical algorithm changed.

The final complete command passed in 1,532.375 seconds (25 minutes 32 seconds).
It includes 498 managed core checks, 215 frontend tests, 43 independent PPA HTTP
economic cases, 18 R3 checks, 30 PPA browser checks, the cancellation-callback
regression and actual renewable recovery. The independent Fraction/ZoneInfo oracle
also matches all 122 managed outputs. All executable, test, migration and
configuration hashes remained unchanged during the final gate. The 23 owned
preview databases were removed; independent probes also removed their resources.

Independent adversarial review covers the entire Phase C delta, including all six
migrations, actual runtime RLS/grants, temporal selection, current authority,
exact-term persistence, complete-book revision refusal, scenario reuse, worker
recovery and legacy identities. Reproduced findings are resolved. Remaining
performance, preparation-lock, deployment and settlement limits above remain
explicit; passing this gate does not remove them.

The next settlement task is [proposed separately](ppa-settlement-next.md). It is
not part of Phase C completion or an authorization to implement settlement.
