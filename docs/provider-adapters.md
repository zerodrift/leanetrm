# Model provider adapters

The page assistant and Settings › AI › Test route by the admitted provider and
its exact installed usage mapping. Model names never select a provider. The
existing key custody, account terms, administrator attestation, model capability
versions, effective prices and enablement gates remain required.

Capability versions explicitly choose `fixed` (a sourced non-repointing claim) or
`alias` (administrator approval to follow the provider's name), under D213 and
migration 0105. Identifier source/date fields describe that policy. New settings
forms require a choice. Existing versions and legacy command retries retain the
fixed-only contract. An alias still accepts only its requested name and exact
listed served identifiers; it never accepts arbitrary snapshots or a new provider.
The existing call record pins requested model, capability/price versions and
reported served identifier. A returned alias cannot prove an underlying snapshot;
administrators must review capabilities and prices when the provider changes it.

| Provider | API | Usage mapping | Reasoning settings the mapping sends |
| --- | --- | --- | --- |
| Anthropic | Messages | `messages_v1` | low, medium, high, xhigh, max |
| OpenAI | Responses | `openai_responses_v1` | low, medium, high, xhigh, max |
| xAI | Responses | `xai_responses_v1` | low, medium, high, xhigh |
| Meta | Responses | `meta_responses_v1` | minimal, low, medium, high, xhigh, max |
| Google | streamGenerateContent | `generate_content_v1` | minimal, low, medium, high |

A model's sourced capability version must choose its supported subset. These
mappings target adjustable reasoning models accepting the parameters below;
older models that reject them need a separately qualified mapping. A protocol
name alone does not prove a particular model is supported.

Responses requests use fixed global HTTPS endpoints, explicit standard service
(`default`), `store: false`, `background: false`, `truncation: disabled`, the
selected output limit and reasoning effort. Only client function tools are
offered, with automatic choice and parallel calls. Encrypted reasoning items
are requested for stateless replay; reasoning summaries are never requested or
published. OpenAI fixes implicit cache retention to `30m`; xAI and Meta use
automatic caching without separately billed writes. The caching switch's explicit
breakpoints remain specific to Messages. No adapter changes provider or retries
a possibly billed failed call. Sampling parameters are omitted: this mapping
provides no configurable sampling contract, and Test must reject incompatible
models rather than choosing another behavior.

Google uses one escaped model path segment, the `x-goog-api-key` header, one text
candidate, an explicit thinking level without thought summaries, `serviceTier:
standard` and `store: false`. It creates no explicit cache resource. Function
responses resolve in order against the immediately preceding model reply; opaque
thought signatures stay with their original parts. Unsupported part types and
mixed protobuf-union payloads fail before exposing tool requests.

All adapters retain the existing bounded streaming transport: strict UTF-8 and
JSON without duplicate properties, no proxy or redirect, byte and idle limits,
cancellation and exactly one terminal result. A served identifier is checked
before its text is emitted. Replies belong to one vendor and requested model;
continuation data stays in worker memory and prints no content. HTTP error text
is never returned or logged. A bounded HTTP 400 body can identify the documented
`context_length_exceeded` code.

Usage is taken from the final Responses object or Google's last chunk. OpenAI
subtracts cache reads and writes from input; xAI and Meta subtract cache reads.
Their output already includes reasoning. Google subtracts cached content from
prompt tokens and adds candidate and thought tokens for output. Reasoning remains
an informational subset. Google text-modality breakdowns must agree with their
reported totals. Missing priced counts, inconsistent totals, malformed or falling
counters, unrecognized nonzero usage fields and unsupported service tiers prevent
pricing. A failed or interrupted stream without final counts has unknown usage;
a protocol failure after complete final counts retains those counts. A connection
that sent nothing has sourced zero usage. No HTTP error status is assumed unbilled.

Migration 0104 installs source/date confirmations and each adapter's applied
mapping and reasoning settings. It includes no real token prices, credentials,
account attestations, capability versions or enabled models. Installation requires
a qualified schema/application release; an application-image update alone cannot
install this migration. Existing immutable versions retain their mapping identity.

The managed checks use synthetic recordings, including tool continuations, disjoint
usage, malformed parts, billing modifiers, limits and mismatched served models.
Owned HTTP checks exercise routing, exact synthetic costs, idempotent Test replay
and durable mismatch closure on disposable stock PostgreSQL. They call no real
provider. Account/model availability and real-provider task qualification remain
open under O09.

D213 local qualification uses `--provider-adapters-check` and `--assistant-check`
with disposable stock PostgreSQL and a loopback fake provider. It covers a
pre-0105 capability/audit upgrade, unchanged legacy replay outcomes, helper drift
rejection, explicit alias approval and request-identity conflicts, invalid policy
values, normal provider/model enablement gates, exact allowed snapshot reporting,
unchanged synthetic token costs and durable rejection of unlisted served models.
The managed provider conformance checks and all 324 web checks pass. These checks
send no real provider request and do not install migration 0105 on a hosted system.

Protocol sources checked on 2026-10-23:

- OpenAI: [streaming events](https://developers.openai.com/api/reference/resources/responses/streaming-events),
  [function calling](https://developers.openai.com/api/docs/guides/function-calling),
  [prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching).
- xAI: [stateless tool loops](https://docs.x.ai/developers/model-capabilities/text/best-practices),
  [usage](https://docs.x.ai/developers/advanced-api-usage/prompt-caching/usage-and-pricing).
- Meta: [Responses](https://dev.meta.ai/docs/protocols/responses),
  [usage](https://dev.meta.ai/docs/token-counting), [reasoning](https://dev.meta.ai/docs/reasoning).
- Google: [REST contract](https://ai.google.dev/api/generate-content),
  [thought signatures](https://ai.google.dev/gemini-api/docs/generate-content/thought-signatures).
