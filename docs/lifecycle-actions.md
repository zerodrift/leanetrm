# Lifecycle actions

Lifecycle discovery, review and execution use one managed handler registry. The
registry dispatches a typed native target; product operations retain ownership of
financial terms, calculations, records and corrections. A target may be a trade
binding or a native holding. Shared commands are authorization and first-outcome
receipts, not another financial event journal.

## Selected source and authority

Every reviewed action pins its certified context, business date and calculation
instant. A host snapshot is also pinned when a product consumes its market
inputs. Business dates come from that context rather than the display time zone
or the UTC date of the instant. Discovery resolves this source through the same
trading workspace selection used elsewhere in the application.

Read authority is captured before source lookup and registered with the owned
transaction. Write authority, including `RunJobs` for automatic execution, is
captured before waits and mutable head lookup. Final admission retains the
original deadlines, current actor/session authority and exact retained
references. Restoring a read savepoint does not discard the source or admission
guard. A reviewed hash identifies financial inputs; approval remains the host's
separate exact-request authorization.

Every write carries a request ID and returns its first outcome on exact replay.
Receipt lookup precedes mutable host-frame and fresh-source lookup, so evicting a
working frame does not prevent recovery of an accepted outcome. Replay still
checks current authority and exact retained references.
A new request acknowledging an identical accepted financial snapshot returns
that accepted version and the current financial version. It neither creates
cash nor advances the financial sequence. Changed source lineage is a new fact
even when its rounded total is unchanged. Past accepted facts can be
acknowledged after a later correction without rolling the current state back.

## Explicit configuration

Versioned action policies select manual, review or automatic operation and a
half-open effective window. Only actions with a qualified automatic consumer
can select automatic mode. Product profiles bind exact action-policy versions
and product-native terms; there are no inferred financial defaults.

Listed futures options select the generated future's mark curve and exact
margin policy separately from the official expiry source. Exercise or assignment
and margin enrollment share the causal native event and publication transaction.
Actual future state still requires a fresh certified post-commit context.

Swap profiles identify one exact trade revision, binding, membership and payment
period. Several periods of one swap may be configured concurrently. Each has a
stable profile ID and the native side-period occurrence ID. Native wrappers
require the selected profile, action window and source admission before calling
the existing settlement arithmetic and cash producer. Legacy public write
adapters refuse with `lifecycle_configuration_required`; their native producers
are not executable by the application runtime directly.

Observed swap cash uses a trimmed stable reference within the trade, cash
instant and explicit reversal relationship. Re-recording the same complete fact
at a later recording instant acknowledges the original payment. A conflicting
amount, currency or allocation under that key requires a qualified correction
or reversal, and does not create another payment silently.

Futures setup lists exact held and unadopted futures, source choices, dated
margin occurrences, opening treatment and rounding policy. Saving and adoption
reuse the existing native margin commands and their original commit admission.

## Historical projection inputs

The knowledge-only swap reader returns all source-visible accepted
determinations and observed payments, including future effective instants. It
proves exact ownership, terms, contribution chains, allocations and publication
receipts. Ordinary opening state continues to recognize facts only when their
effective time is reached. Hypothetical projection consumes the separate typed
history; it does not manufacture native accepted facts.

The knowledge-only futures reader separately retains the complete accepted
margin heads, every original and correction cash membership, and native payment
revision history. Future effective facts remain inactive at the opening. The
read verifies the held execution and exact source inputs, and preserves the
earlier opening-read deadline through source lookup and final commit admission.
Paid-cash allocations require an existing obligation; payment before its due
time is supported, while an advance before ownership requires a separate asset
contract.

## Qualification

The disposable PostgreSQL checks exercise actual managed booking and source
selection, policy/profile writes, the shared owned action coordinator, native
financial producers, exact replay and same-fact acknowledgement. They cover
option exercise with atomic generated-future adoption, historical option source
isolation, original read-grant expiry under a successor grant, swap source
corrections, unchanged-price changed lineage, observed-payment conflicts,
knowledge-only future cash and determinations, and independently configured
multi-period swaps. The checks also verify that legacy native write entry points
are unavailable to the runtime.

Browser, whole-portfolio economic risk, automatic host dispatch, gas continuation
and physical-futures delivery have separate integration checks. This document
does not claim those consumers are qualified by the action checks alone.
