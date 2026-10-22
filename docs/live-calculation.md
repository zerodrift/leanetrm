# Live calculation contexts

Live corrections are part of the existing shared header context and connected
calculation path (D188). The browser subscribes to context changes and calls the
same authorized backend operations used for ordinary pricing, custom previews,
instruments, side schedules and the Blotter. There is no browser pricing engine.

## Selection contract

| Selection | Knowledge and inputs |
| --- | --- |
| Working | Explicit selected observation pins; changing the database does not change those pins |
| Historical | One certified effective date and knowledge cutoff, with exact selected input frames |
| Live corrections | Fixed selected facts, effective/valuation date, observation cutoff, sources, roles and contracts; follow their latest committed corrections through successive certified knowledge contexts |

Choose **Follow live corrections** on a selected market, custom-curve or trading
screen. Live is read-only. **Return to working data** restores explicit working
selections. Entering a screen without a live refresh contract pauses at the
*displayed* exact context in the backend before that screen reads. It does not
silently switch to current knowledge. Resuming requires another explicit Live
selection from working inputs.

The implemented policy is
`selected_facts_latest_committed_corrections_v1`. It does not advance a clock,
select a newer observation time, roll delivery contracts or switch a published
recipe version in exact preview mode. **Preview effective version** explicitly
follows the custom timeline selected at each new certified knowledge context
(D189); ordinary **Preview** returns to an exact version. Native activation and
other rolling policies require separate explicit selection contracts. Frozen
actuals retain the existing frozen-input policy. A live correction is a newly
admitted market frame, not a scenario offset.

## Calculation and reuse

Each update captures one new certified context and resolves selected corrections
under current authority. Normal reads bind that context. Native/basis curves,
published custom ancestors and instrument/side schedules retain their existing
versioned contracts and graph edges. Each risk request pins one generation for
all base/up/down evaluations. Queued jobs retain their admitted context and
inputs; a later live update cannot change a retained run.

The dependency kernel is in `OpenEtrm.Shared.Calculation`. Base-frame advancement
is separate from nested scenario overlays. Its memo pages share completed
immutable calculations with copy-on-write isolation. Independently compiled
native layouts can match semantic execution slots without changing any financial
point, snapshot, lineage or evidence identity. Different alias partitions,
resolved memberships, operator weights, units, domains or time coordinates
require a different qualification. Changed quotes and observed multipliers
invalidate their reverse dependency closure. Even a same-price replacement fact
retains new evidence and invalidates its consumers.

The API/database adapter bounds memoization to sixteen qualified plans and
250,000 aggregate nodes. Larger plans calculate without entering this cache.
Cancelled reads can leave completed numerical work, but never a successful
partial financial result or an authorization proof. Every read still validates
its selected inputs, definitions and current authority. Cache hits do not supply
receipts, privileges or instrument PVs. Every unique instrument is declared and
repriced in each new frame, including its actual fixings, discount inputs and
payment state. Broader PV reuse needs complete noncurve dependency declarations.

Price-screen trace counts describe resolved constituent results, including cache
hits, so an exact pinned price response remains reproducible. Explicit graph/risk
work counters describe physical calculation and may differ with cache warmth.

Preparation and shape qualification still traverse the resolved graph. A changed
integer layout copies completed unaffected memo entries into new slots; an
unchanged layout shares memo pages. This implementation does not claim constant
cost frame admission, incremental database loading or compiled-topology reuse.

## Push, bounded work and visible freshness

Migration 0083 sends identifier-free PostgreSQL notifications on actual financial
publications and authority changes. Existing commit-serialized publication
streams are the durable catch-up record. Each API process listens independently;
reconnection reconciles committed state. Context certification writes do not
notify themselves. Migration 0084 adds custom activation publications to this
same wakeup/head path. An inactive effective interval makes a live effective
preview stale and retains its last successful displayed value. Publishing or
editing a timeline clears an explicit preview until it is calculated again.

The SSE stream subscribes before reconciling. It sends a complete authorized
context/frame summary at admission and after an authorized head changes. Resume
IDs belong to one Live selection; reconnect reads a fresh complete snapshot.
Hidden trading activity is excluded from market-only readers' heads. Current
permission and market/reference scope vectors also detect revocations and
expiring permissions. Unchanged database-wide wakeups send no output; independent
ten-second heartbeats recheck authority. No price polling occurs.

One stream per workspace session, one pending notification and a five-second
write deadline bound a slow reader. A disconnected stream is visibly stale;
**Retry refresh** can reopen a closed stream. A reconnect may arrive
while a previous connection is still being cleaned up; an overlapping admission
is refused and can be retried after cleanup.

The UI keeps one calculation in flight and only the newest pending update.
Its minimum refresh cooldown is 250 milliseconds and grows to twice the last
refresh duration, capped at ten seconds. Hidden tabs defer screen calculations.
It does not receive or render the full dependency graph. Open native history,
custom previews, requested draft PVs, booked instrument/side schedule views and
Blotter results refresh through their ordinary backend operations. Expensive
risk runs remain explicit. Unsupported screens pause Live before reading.

The badge uses both text and color:

- **Live**: the active screen's requested refresh completed.
- **Updating**: previous displayed values may be stale while a generation waits
  or calculates.
- **Stale**: the connection or a requested refresh failed; the last successful
  refresh time remains visible, and a retry must succeed before turning green.

Four recent immutable frame/context pairs form a bounded admission window for
multi-request screen reads. Navigation exposes only the current pair. Explicit
older requests within the window bind their own cutoff, including combined
workspace and history projections. Requests outside it fail and must refresh to
the latest generation. Publication does not reinterpret an older snapshot ID.

## Verification and limits

`tools/run_preview.py --live-calculation-check --browser-check` uses an owned,
stock PostgreSQL database and synthetic inputs. It exercises committed correction
propagation, quiet unrelated/rolled-back wakeups, current/pinned isolation,
combined projections, stream/session admission, native history, custom graph
preservation, failed refresh/retry and server-side pause. Managed checks cover
new snapshot slot matching, affected diamond work, multiplier changes, nested
scenario isolation, more than 64 frame updates, cancellation and structured
depth rejection. Browser queue checks retain at most one pending generation.

These checks establish behavior for the stated synthetic cases. Large financial
workload latency, sustained feed load, graph-compilation reuse, distributed
within-run reduction and real-cluster failure behavior require separate
qualification. The integer [graph benchmark](connected-graph-benchmark.md) is a
capacity measurement, not evidence of financial throughput or browser behavior.
