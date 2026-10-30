# Decisions and unresolved choices

Status: design record, not implementation evidence.
The architecture is in [architecture.md](architecture.md); delivery scope is in
[plan.md](plan.md).

## Agreed requirements

D215 authorizes the financial swap settlement MVP through S1–S6, with futures
margining and physical settlement subsequent. The [swap settlement contract](swap-settlement.md)
records explicit opt-in, gross occurrence rounding from pinned exact-currency
minor units, corrections against prior rounded totals with explicitly supplied
due instants, nominal due balances without interest/credit adjustments, and
realization per completed final contractual period independently of payment.
Completion is pinned under `financial_swap_period_completion_v1`: date-only
exclusive ends use midnight UTC; clocked MW periods use retained final interval
ends. Matching gross periods require both completion and finality. These policies
do not supersede bounded physical contracts D202–D204. Implemented capability and
remaining bounds are recorded with the executable consumers in the linked contract.

| ID | Decision | Consequence |
| --- | --- | --- |
| D01 | Public project under Apache-2.0, with DCO sign-offs for contribution commits and an additional agreement for designated core contributors | Preserve license/attribution; review all published material. Casual contributors need no separate CLA. Core contributors retain copyright and grant additional rights under [agreement v1.2](../CLA.md); see the [contribution guide](../CONTRIBUTING.md) and [integration setup](contributing-setup.md). Live signing checks and merge enforcement require separate verification |
| D02 | Fresh implementation/history, with synthetic examples | Private sources, datasets and operational history are not imported |
| D03 | Vanilla PostgreSQL | No custom server fork or mandatory private runtime dependency |
| D04 | Preserve the independent side architecture | Trades reference immutable instruments; composed instruments have reusable sides through explicit memberships; simple contracts may be leaf instruments (D110) |
| D05 | AI is a first-class application user | Core workflows have discoverable typed operations, not only browser controls |
| D06 | LLM-agnostic boundaries | Provider-specific types stay outside domain/calculation contracts |
| D07 | Deterministic business execution | Models propose; application policy and versioned calculations execute |
| D08 | Robust administration belongs in the foundation | Principals, delegation, policy, current permissions and audit are core requirements |
| D09 | Shock shared primitive factors | All affected computations consume the same immutable scenario |
| D10 | Black-box pricers | Risk does not require knowledge of a model's internal algorithm |
| D11 | Reuse unit results and keep quantity outside the instrument | Every instrument is unit-level; trades hold instrument-to-quantity bindings. D146 clarifies that this is mandatory, with no optional scalability flag; market nonlinearity is independent |
| D12 | Declare curve dependencies | Derived gas/power/location values rebuild from approved recipes |
| D13 | Typed values and controlled precision | Money, price, quantity, units, rounding and numerical policy are distinct contracts |
| D14 | Futures and fixed/floating linear products are the initial product scope | Futures settlement/variation-margin semantics are required; options/optimization remain later breadth |
| D15 | Sides own schedule generation and PV behavior | Each side computes its contribution from its schedule; instruments combine schedules and valuations |
| D16 | An option side can reference sibling sides | Explicit context-bound dependencies preserve a flat instrument-side topology |
| D17 | Physical and financial sides can form a composite | Delivery terms/activity and financial settlement remain separately modeled |
| D18 | Lifecycle is a core design concern | Amendments, due events and expiry must have explicit retained transitions |
| D19 | Portfolio contains books; book contains trades | Business ownership and aggregation follow this hierarchy |
| D20 | Trade owns instrument-to-quantity bindings and lifecycle directly | Multiple instruments can belong to one trade without another quantity-bearing domain class |
| D21 | Economic equivalence governs definition reuse | Equal canonical instrument economics can share a definition without merging trade quantities or lifecycle state |
| D22 | Bitemporal historical environment is a baseline requirement | Select business/effective time and what was known; include trades, relationships and market data |
| D23 | Context governs the whole business environment | Resolve once and propagate the same immutable selection through UI, API, agents, calculations, joins and exports |
| D24 | Temporal names are effective_from/effective_to and known_from/known_to | Point queries use effective_at and known_at; keep contract effective/delivery attributes distinct |
| D25 | Temporal intervals are half-open, with UTC knowledge timestamps and a finite open-end sentinel | Document [from,to) in column comments, use consistent suffixes and reserve the selected upper-bound convention |
| D26 | Persist instants in UTC and display them in the user's selected time zone | Date-only terms remain dates; display preferences cannot change economic meaning |
| D27 | Core reference tables and referential integrity belong in the starting application | Currencies, units, commodities, locations, venues, calendars, rates and futures use database-enforced exact-version relationships |
| D28 | Public futures catalogs contain generic placeholders and synthetic examples | Real definitions require sourced verification, ongoing maintenance and installation-specific data rights; no placeholder booking/valuation |
| D29 | leanetrm.com is the intended eventual hosted domain | Hosting and data-use/distribution permissions are separate later operational work |
| D30 | Use C#/.NET, TypeScript and stock PostgreSQL | Pin runtime/dependencies during implementation; numerical contracts remain independent of application frameworks |
| D31 | Never assume intent or silently use defaults | Financial modes/policies and applied context choices are explicit; missing, ambiguous or conflicting behavior raises a structured error |
| D32 | All-in marks and spread-dependent prices have distinct declared semantics | No inferred base dependency or implied spread; incompatible mark types cannot silently change a recipe |
| D33 | Determine risk through deterministic shocked PV results | Rebuild declared dependencies and evaluate base/up/down PV; no allocation from spread size, price proportions or inferred relationships |
| D34 | Dated market contracts own identifiable histories; curves are cross-sections | Stable factor histories support explicit aligned samples; rolling references retain dated constituents and their versioned selection rule |
| D35 | Curves own common specifications; dated contracts own their specific terms | Commodity, yield and composite/algorithmic curves use typed compatible constituents; detailed storage records need not be public business objects |
| D36 | Compose curves through explicit versioned source bindings at every depth | Basis/base orientation, operations, parameters and delivery mappings are declared; forwards may consume futures; reject cycles |
| D37 | Separate curve construction from settlement targets | Consuming a futures input does not transfer its cash lifecycle to a forward; each index/formula, fixing and payment convention remains explicit |
| D38 | Source expiry requires an explicit transition rule | Freeze eligible final facts, use a specified transition or end eligibility; no implicit roll, current-price substitution or historical rewrite |
| D39 | Establish units/money and current authorization with the market foundation | Typed quantities/prices/currency amounts and explicit cash rounding; permissions must precede exposed operations |
| D40 | Agents have their own principal identities and agent login kind | Verify distinct credential/login binding; delegated actions retain agent actor and delegator identities |
| D41 | Use generic typed series/observations instead of a heat-rate-specific table | Prices and numeric ratios have retained dated observations; constants and dependencies are explicit |
| D42 | Supply common role templates with exact scoped assignments | Viewer, Trader, Risk Analyst, Market Data Editor/Approver and Access Administrator, and the agent reader held only by agent principals (P26); identities and roles are separate |
| D43 | Keep the next slice bounded | Generic series/curves, exact retained input pins and current database isolation precede APIs; full historical selectors and broader algorithms follow |
| D44 | Organize the user interface around market data and familiar business labels | Browse futures and forward curves by commodity or market type; keep internal identifiers and implementation terminology out of visible screens |
| D45 | Keep time zone and evolving personal customizations in a user profile | Remember presentation preferences separately from financial inputs and authority; account-wide persistence follows verified sign-in |
| D46 | Put a compact date/time context at the top of the application | Display minutes without changing exact retained instants or version identity; distinguish same-time versions and repeated local times without showing GUIDs |
| D47 | Separate business references, market setup and internal storage concepts | Present business definitions separately from versioned pricing/delivery configuration; hide join/version machinery without weakening integrity or permissions |
| D48 | Use typed locations with explicit geography | Reuse the location/type relationship for pipelines, nodes, hubs, regions and delivery points; support supplied GPS points without inventing coordinates, route/area geometry or topology |
| D49 | Require a current consumer or concrete executable baseline case for each table/value/dependency | Retire speculative catalogs safely; keep future breadth in the plan and record actual reference/setup uses |
| D50 | Include a basic interest-rate curve in the synthetic setup | Dated zero-rate samples retain histories and produce discount factors under explicitly selected conventions |
| D51 | Keep units and the used rate conventions project-owned | Avoid a large financial-library dependency for the bounded baseline; qualify new conventions with independent exact-result tests |
| D52 | Explain composite/basis curve prices with a per-contract diagram and calculation table | Use boxes for one contract and connected contract/price tables for all; group identical mappings visually while retaining explicit row exceptions and exact evaluated values under the selected market date. Supplied curves without evaluated dependencies open a plain table of all contracts and source observations, without a single-price box or redundant selection controls |
| D53 | Keep collapsible navigation and ordinary-table filtering as presentation state | Remember layout per user; scope transient table filters to the displayed authorized context, keep exact values and leave connected worksheet mappings complete |
| D54 | Allow explicitly selected daily and monthly contracts in one curve | Retain dated constituent histories and versioned resolution/membership rules; daily-to-monthly base-price projection and source-expiry transitions are explicit, with no inferred daily marks, averaging or automatic roll |
| D55 | Bind BALMO basis to its explicitly quoted benchmark and exact remaining period | A matching BALMO benchmark is distinct from a full-month future's frozen final; a BALMO quote cannot supply daily marks without a qualified shaping rule, and changing the period start creates a different dated contract |
| D56 | Advance the current contract at its selected expiry milestone through its retained sequence | Pin the exact event role/instant; the synthetic futures example uses last trading. The next sequence entry becomes current; farther-end listing is independent. Preserve historical pins and reject missing milestones, eligibility or prices without substitution |
| D57 | Support reusable retained schedules supplied as rows or produced by explicit deterministic definitions | People, code and agents consume the same pinned dates; generators and parameterized schedule references preserve provenance and validation without requiring a general scripting language |
| D58 | Separate named schedule definitions may share identical immutable typed date payloads | Retain each definition's meaning, provenance, coverage and occurrence links; canonical sharing does not merge obligations, historical versions or calculation identities |
| D59 | Assign explicit symbolic shortcuts to reusable named schedules | A shortcut binds a pinned schedule, relative position and date role; sides/instruments and later dashboards/risk use a common resolver. Combine/compare/diff remain coding benefits, not a mandatory calendar UI |
| D60 | Build date schedules before binding dated-contract fields | Standard and typed custom fields retain explicit schedule references and resolved values; preserve occurrence mappings, reject cycles and keep existing series/milestone identities authoritative |
| D61 | Support business-day arithmetic on date-valued references | Expressions such as `oe.ng1 + 1bd` use an explicitly bound versioned calendar/date rule, with no inferred clock/calendar or hidden adjustment; positive/negative movement now counts strictly beyond the base; zero offsets remain unsupported instead of choosing a policy |
| D62 | Design the date foundation around typed immutable data and native operations | A small common sequence contract exposes selection, generation, movement, adjustment and comparison over distinct typed views. Calendars and policies are explicit inputs; each supported operation requires a concrete consumer and independent checks |
| D63 | Use standard business names while distinguishing schedule anchors from business-day adjustments | Proposed `RollConvention` describes unadjusted recurring anchors; `BusinessDayConvention` adjusts a closed date. A calendar can bind an explicit adjustment policy; business-day movement and contract expiry advancement retain separate semantics |
| D64 | Exercise generated dates in private curve contract drafts before publication | A bounded monthly anchor editor supplies indexed references to start/end and last trading. Keep expressions, resolved values, calendar snapshots and exact source pins; existing curve templates are not required; qualification, dependent mappings and durable bitemporal activation remain separate work |
| D65 | Use `oe.name` as canonical exposed date references | Namespace aliases are an optional future authoring convenience; definitions store canonical names. Period fields use start/end, and a single result uses date |
| D66 | Save independent schedules before using them in curve/contract date fields | Draft labels/rules are editable; publication freezes an exact version. Editing published terms creates the next draft. Stable identity, effective intervals, scoped committed publication views and supported engine pins preserve earlier resolution |
| D67 | Establish certified effective/knowledge contexts alongside current RLS | The schedule slice uses retained engine commit timestamps, a closed certification watermark, scoped prefix snapshots and immutable context receipts. Context IDs convey no permissions; historical reads obey current scope and revocation |
| D68 | Fail explicitly when timestamp history cannot be certified | Qualify fresh primary installations with tracking enabled; verify full transaction epoch and timeline before engine lookup, retain certificates, reject finer-than-storage precision and refuse missing history or clock regressions. Other domains and operational migrations remain separately qualified work |
| D69 | Apply one certified market environment across existing business screens | Sealed definition/observation events have a separate commit-certified prefix at the same exact cutoff as schedules. Temporal RLS filters market/reference closure; historical contexts are read-only, explicit correction selection never substitutes sources and main valuation dates must match the context |
| D70 | Scope administration to workspace memberships and current authority | Membership suspension does not globally disable a principal. Private narrow operations, exclusive first admission, exact request identities, expected versions and atomic append-only audit protect account/role changes. Administration does not imply business-data access; preview registration is not production authentication |
| D71 | Share one explicit verification command between local checks and hosted CI | Pin SDK, Node, dependency locks and action commits; run synthetic suites with stock database binaries, owned disposable resources and read-only repository access. Record changing runner/browser/database versions and qualify actual hosted execution separately from workflow configuration |
| D72 | Retain typed prices and multipliers throughout curve arithmetic | Working price units bind exact currency, quantity specification and quote multiplier. Add only identical units; multiply only explicit dimensionless or quantity-ratio values. Keep quote-role checks separate, validate completed output units/domain and retain exact intermediate numeric bounds without imposing final quote limits on intermediates |
| D73 | Start futures with explicit fixed-total cash booking and typed execution P&L | Reuse exact dated curve contract side/instrument identities across independent signed-lot trades/books. Keep settlement Unprocessed; no claim of futures PV, margin or lifecycle processing |
| D74 | Certify immutable futures booking history under the common historical context | Exact effective instants and a separate trading prefix share the certified market/schedule cutoff. Current ReadTrades authority and actor-owned idempotency/status remain enforced; missing legacy certificates require a verified migration |
| D75 | Make Trading → Blotter the primary trading workspace | Start with individual trade bindings; support chosen columns, portfolio/book slicing, explicit managed pivot dimensions/measures and named saved layouts. Preserve trade obligations and units/currencies when aggregating |
| D76 | Keep effective/knowledge controls in the common header beside the market date | One shared historical selection applies across screens. Show the active preview user and current permissions; layout/profile labels do not confer authority |
| D77 | Share deterministic report and calculation operations between UI and agents | Snapshot export precedes a qualified day-over-day P&L operation. Resolve comparison contexts, intervening trades/lifecycle cash and numerical policy explicitly; pricing and shared-factor risk remain managed operations |
| D78 | Populate the standard local blotter with explicit weekday executions | Eight synthetic January futures trades use existing authorized commands and stable request identities. The four weekday market frames show cumulative holdings; no inferred roll, weekend sample or real-market calendar claim |
| D79 | Standardize ordinary table workspaces and persist named blotter layouts per account | Open directly to trade rows with toolbar controls for columns, filters and managed pivots. Store bounded versioned presentation layouts under current human identity/workspace RLS; reject concurrent stale saves/deletes. Layouts contain no financial context, authority or results; production sign-in remains separate |
| D80 | Use pinned public presentation components for shared grids and charts | Tabulator serves the trading grid; Apache ECharts serves curves, histories and position profiles. Self-host assets and preserve required licenses/notices. Exact decimal values and all financial aggregation remain managed inputs/results |
| D81 | Save row/column pivot and grid/chart layouts as versioned account presentation state | Version 2 adds split dimensions, column order/widths/sorting and chart choices; retain version 1 compatibility. Exact typed partitions survive matrix reshaping, missing cells remain blank and draft revisions survive financial refresh/date changes |
| D82 | Start shared risk with immutable absolute live-quote overlays and within-run affected reuse | Pin exact dated inputs; preserve observed facts, rebuild declared affected nodes once per scenario and invoke eligible black-box unit valuations. Reject unsupported shock/scaling conventions. Measure actual curve/pricer work against independent full repricing |
| D83 | Qualify open futures sensitivity separately from settlement PV | The first blotter consumer retains Unprocessed settlement and execution bases; report base/up/down execution P&L, shock changes and exact position-scale delta/gamma with explicit units. RunScenario is separate from Trader authority; full futures PV and durable runs require further contracts |
| D84 | Keep side families small and behavior-based | FixedSide, FloatingSide, PhysicalSide and OptionSide cover the general families; D110 supersedes the artificial FuturesSide requirement. Instrument classes specify supported side types and relationships. Commodity, receipt/payment direction and configurable financial terms do not create additional side subclasses. Preserve signed membership coefficients and trade-owned signed quantities without a second direction sign. This records design, not a completed generic hierarchy refactor |
| D85 | Capture user-selected side direction and sum normalized signed contributions | Pay/receive, deliver/accept and other supported direction pairs map to one validated signed membership coefficient. Each side contribution has a common instrument currency and per-unit quantity basis; instrument PV sums them and the trade applies its signed quantity once. Reject conflicting signs or missing unit/FX transformations |
| D86 | Normalize futures unit value per lot with explicit settlement inputs | Apply quote/quantity/time transformations once, then scale by trade lots. Holding-specific settlement and valuation-basis state remain explicit; the existing execution-P&L slice does not implement full futures PV. D110 supersedes D87: the contract is a leaf instrument and pricing selection is external |
| D87 | Superseded by D110: every Instrument has sides | FuturesInstrument inherits from Instrument and contains one FuturesSide inheriting from Side. The side pins the exact dated market contract and lot/quote terms; common instrument valuation sums its normalized signed contribution like any other instrument. No side-free futures path or separate aggregator is needed |
| D88 | Enforce nonempty immutable memberships on composed instruments (scope revised by D110) and preserve exact final contribution arithmetic | The composed-instrument constructor rejects missing sides. Contributions retain side identity, currency, quote scale and per-unit quantity basis; fused exact summation applies membership coefficients and trade quantity once without rounding a per-lot intermediate. Futures definitions, including first trading, must match the model's retained contract/series/quote terms; supported analytics remain execution P&L and open sensitivity |
| D89 | Amend the latest eligible trade revision; preserve earlier states | The first editor supports an explicit reason and effective instant at the selected working As of context, with user confirmation. It appends a versioned booking-state event and rejects stale heads, decreasing effective instants, historical writes and unsupported settled/expired transitions. It never rewrites superseded revisions or shared instrument definitions. Backdated insertion into an existing revision chain needs separate correction rules |
| D90 | Distinguish working knowledge, selected price date and actual today | Display working/historical mode and actual today in the user's time zone. Available historical price dates are not presented as today's market data. Explain disabled booking and provide an explicit return to working inputs |
| D91 | Give trades and reusable instruments stable readable references | Allocate separate TR and IN numbers once within the selected trading catalog, atomically with entity creation. Exact UUIDs remain relationship keys. Revisions and book moves retain the trade reference; reused instruments retain one instrument reference. Preserve saved layouts and expose optional identifiers/revision columns, editor labels and export/risk input references |
| D92 | Keep fee aggregation and reconciliation separate from instrument pricing | Fees may originate from individual executions or explicitly grouped positions. Define calculation scope, grouping/netting, period, tiers and allocation independently from reusable instrument terms. Retain expected charges, statement evidence, adjustments and payments separately; missing fee coverage is unknown. Fee implementation and net reporting wait for supported rules and reconciliation contracts; no empty controls or inferred rates |
| D93 | Select financial swap index sampling and weighting explicitly | FixedSide and FloatingSide remain independent reusable definitions. The initial managed model supports one monthly index projection, equal daily/calendar observations, equal business-day observations, calendar-day weighting using the preceding or following eligible observation, and explicit positive quantity weighting. Pin the exact index, calendar where consumed, dated projections, quantity basis, delivery/fixing/payment terms and implementation. Require complete coverage; monthly marks cannot substitute for daily inputs. The projection-only model stops at the first fixing until actual-fixing lifecycle support exists |
| D94 | Discount financial swaps using the currency-configured default unless discounting=false | Resolve the exact mapping and curve in the selected effective/knowledge context; missing defaults are errors. Preserve exact pillar factors and use selected log-linear discount-factor interpolation strictly between bracketing pillars. The bounded managed numerical policy uses exact geometric roots and final nearest/ties-to-even at 18 digits rounding. No extrapolation, curve fallback or inferred payment-date adjustment. Explicitly undiscounted valuation uses factor 1 without consuming rates |
| D95 | Each side owns schedule generation and PV; instruments aggregate side PVs | FixedSide and FloatingSide value their own payment rows under explicit context and side-specific factors. Side PV is per normalized unit and excludes membership signs and trade quantity. Instrument.PV delegates to Side.PV, validates compatible complete inputs and sums membership-signed exact side PVs. Trade scaling occurs once afterward. Independent payment dates/DFs are supported; no pre-PV cross-side cash netting. Rational side values preserve exact cancellation/scaling. Inspected rows are the actual valued rows. Option sibling base versus final contribution and exercise-control accounting remain governed by D16/O13; no option model is implied |
| D96 | Instrument editors present vertical side columns with aligned fields | Keep trade booking inputs separate from side terms. Delivery start/end and currency may initialize unset counterpart fields during entry, with no copy button. Values stay visible/editable; later edits do not overwrite the other side. Ongoing synchronization is not inferred. Multi-side entry requires backed booking operations and independent side values remain inspectable |
| D97 | Persist bounded swaps as independently reusable typed sides and exact reset references | The local booking model retains one whole delivery month, separate fixed/floating payment instants, opposite membership signs, positive total quantity and explicit discounting. Monthly-index and complete daily-calendar arithmetic projection rules have storage and booking consumers. Daily observations aggregate into the floating side's one monthly payment; exact allocated reset cash contributions reconcile to that payment. Other core averaging rules need persisted calendar/weight authoring before exposure. Reuse complete side/instrument terms, retain immutable amendment events and preserve the common TR/IN namespaces |
| D98 | Keep product measures explicit in the shared trading blotter | Futures execution P&L and projected swap PV are separate nullable measures. Backend pivots partition exact instrument type, currency and quantity specification; unavailable measures stay blank. Swap rows open their backed editor and actual valued schedules. The existing scenario workflow remains futures-only; no swap sensitivity or live feed is implied |
| D99 | Extend synthetic swap markets through a separate immutable bundle | Preserve the original futures market revision and its observation pins. Explicitly allow-list a supplemental daily bundle and resolve its frame/pins within the same retained knowledge context. Currency-default mappings use exact curve/currency keys and the shared certified trading publication prefix. Later backdated mappings cannot alter an earlier certified context |
| D100 | Generate contractual payment dates from independent side rules | New bookings select monthly payment frequency and either N days after the last included delivery day or a specified day of the following month. Select calendar/business-day lag, short-month reject/clamp where consumed, adjustment and exact calendar explicitly. Apply lag before adjustment; business lag counts strictly after the anchor. Store typed rule identity and retain unadjusted/final dates on actual valued rows. Different rules remain different side definitions even when dates coincide. Payment dates have no user-selected time or display-zone conversion. Legacy manual-date revisions retain their exact original instants and replay identities |
| D101 | Separate reset frequency, averaging method and payment frequency in entry | Reset frequency selects the price observations; averaging method combines them; payment frequency controls payment periods. Current booking consumes monthly index/no averaging or daily calendar/arithmetic mean and one monthly payment per side. Do not infer compounding, calculation stubs or unsupported frequency combinations. New controls require supported backend operations and actual input coverage |
| D102 | Select commodity from exact reference data before index and unit choices | Commodity filters available indices, projection curves and compatible quantity specifications. The selected exact quote supplies the working unit/basis; commodity does not imply a unique unit or an automatic conversion. Backend writes independently validate commodity/curve/quote compatibility. Changing commodity clears dependent entry selections and invalidates preview |
| D103 | Reuse the published schedule resolver for explicit date entry | Suggestions and resolution use the editor's same complete effective/knowledge certificate. Show the resolved date, version and occurrence, then apply a literal date to the selected side field. This authoring helper creates no live schedule link. Discard stale results on edits, context/authority changes and close |
| D104 | Read configurable business choices from reference tables; represent common conventions with enums | Use exact selected currency, commodity, index, calendar, quote and curve rows rather than UI catalogs. Supply implemented payment algorithms through the backend capability catalog. The standard synthetic-price preview uses USD from the currency reference table; changing an existing fictional-currency dataset requires an explicit rebuild or new reference revision |
| D105 | Trade owns events and booking facts | Events, TraderId, TradeDate, counterparty, memo and legal entity belong to Trade, outside reusable instrument/side definitions and their identity. Trader is the original authenticated human booker. D129 assigns its immutable UTC date and persistence stamp on the server, separately from historical economic coordinates. Bounded futures/swap trade revisions now retain these fields; general lifecycle execution remains work |
| D106 | Instruments define unit economics; Trade holds a separate unit-aware Quantity per instrument binding | Reuse the existing Quantity value type: ExactDecimal amount plus exact commodity/basis/unit specification. External means outside the instrument definition. Instrument unit normalization and side membership coefficients are reusable terms; booked size is not. Existing Money and UnitPrice contracts retain currency amounts and currency-per-quantity values respectively |
| D107 | Mark economic identity fields and version their canonical encoding | Executable instruments/sides declare EconomicDefinition versions and IdentityField properties. Ordered side economics and membership coefficients determine instrument identity; booked quantities/parties/metadata do not. Explicit value encodings, exact reference versions and normalized decimals avoid convenience-property or numeric-format drift. Canonical payload equality protects digest lookup. Operation-local reuse preserves exact retained UUIDs; database unique economic terms and serialized publication retain atomic cross-booking reuse independently of supplied hashes |
| D108 | Bind exact LegalEntity and Counterparty versions to each retained trade revision | The booking legal entity and counterparty are separate scoped immutable domain models with code/name and exact catalog/ID/revision keys. New booking/amendment paths require both parties and include them in idempotency. D129 assigns read-only TradeDate server-side. Validate effective/knowledge visibility and current authority. Preserve legacy NULL facts without inferring parties from book, trader or instrument; explicit amendments supply missing party facts. V2 booking events retain the expanded payload; original V1 events and certificates remain readable |
| D109 | Superseded in part by D110–D112: normalize unit quantities and retain product conversion and discount choices in instrument identity | Instrument/side unit amounts are one and excluded from keys; exact quantity bases remain marked. FuturesInstrument owns typed LotSize, resolved from the exact immutable series and applied once alongside external booked lots without intermediate rounding. FinancialSwapInstrument marks Discounting; a different choice creates a different instrument while sharing sides. Specialized and common pricing reject policy conflicts. Existing indexed typed-term database lookups remain; persisted canonical-digest lookup is unfinished |
| D110 | Let a simple futures contract be the instrument | Supersedes D87 and the futures-wrapper portion of D109. FuturesContract is a leaf Instrument keyed by the exact futures-series primary key, independent of curve recipe/version and trade quantity. LotSize and settlement method resolve from immutable reference terms. SidedInstrument retains real composition. Explicit pricing bindings hold curve/member and quote policy; typed migration aliases preserve historical UUIDs/events. |
| D111 | Separate shared factors, contractual equivalence and bound valuation work | Floating sides identify the selected curve and contractual observation/averaging/payment configuration; resolved projection aliases have a separate binding identity. Reuse includes complete bindings/context. Different payment dates can distinguish contracts without duplicating commodity factors. Exact market series remain selected factor identities; VaR/covariance and cross-revision forward normalization remain work |
| D112 | Align economic lookup with typed primary/candidate keys | Futures SeriesKey expands to ref_data.futures_series(catalog_id,revision_id,code); primary-key lookup precedes alias retention and verifies full keys on UUID collisions. Fixed-side exact quote reference participates in managed identity as in SQL uniqueness. D112/0020 excludes generated monthly contract aliases from floating economic uniqueness and verifies complete retained projection bindings before reuse. Payment/side/instrument candidate-key lookups remain atomic under publication fencing; hashes cannot bypass exact relationships or authority |
| D113 | Observable derivatives retain their own identity and underlying reference | Options may have market quotes and also reference a future/forward or explicit index/basket as underlying. Contract terms include strike/right/expiry/exercise/settlement/premium style. Measure/numeraire and market-input mode belong to valuation context. An outright option mark implies no inferred underlying exposure. Executable options and their lifecycle remain unimplemented |
| D114 | Separate amount presentation from exact values and make unit conversion explicit | AmountFormat supplies configurable display precision and exact-reference labels; decimal futures price display follows tick scale. Currency amounts, prices, quantities and factors retain their exact raw values for arithmetic, sorting and export. Qualified rational unit paths preserve commodity/basis/version; only an explicit final conversion rounding choice may change the returned quantity. UnitPrice conversion is inverse to quantity conversion, and final cash applies all multipliers before precision enforcement. Supplemental unit catalogs retain their own exact published reference binding and historical visibility |
| D115 | Keep financial amount types at operation boundaries and retain precision on deals/sides | Existing Money/Quantity/UnitPrice types remain the typed foundation; a decimal string alone is not an amount contract. The versioned display default applies until an explicit deal/side role policy is saved. Personal display preferences do not own precision; display formatting remains separate from contractual rounding and economic identity. Profile no longer owns precision. Shared current deal presentation persists separate trade/fixed/floating role policies, independently of financial history and contractual rounding; unsaved power examples retain local controls. Final money display defaults to three places; only money-decimal controls are exposed under collapsed Advanced settings below ordinary trade entry. Other amounts use compact managed formatting, while futures retain tick places |
| D116 | Support explicit total-energy and flat-power quantity bases | Total MWh and flat MW are separate entry choices. A side's delivery rule supplies actual eligible durations, clock/calendar/TOU context and versioned integration. Allocating total energy requires an explicitly selected profile and preserves the total without intermediate rounding drift. Trade holdings remain external. Both modes now have a bounded unsaved preview with rational typed interval energy and exact index-quantity matching; booked side persistence remains work |
| D117 | Trader identifies the original authenticated human booker | Exact scoped workspace membership backs the captured trader identity and name. It is assigned automatically at booking and remains unchanged across amendments, account renames and disabling. The amendment actor remains independently audited. Callers cannot assign another trader; service/agent booking needs a separately supported attribution contract. D129 records UTC booking facts |
| D118 | Maintain party names through immutable scoped revisions | Legal-entity/counterparty editors use the same authorized operations as API clients. Separate party-read/write permissions do not grant trade access. Codes are stable, edits require an expected head and nondecreasing effective time, and exact operation/actor retries retain the original result. Historical views are read-only and exact trade references keep their names. Broader party attributes and deactivation remain work |
| D119 | Normalize futures lots from explicit dated contract terms | Physical/cash settlement and fixed/per-day/per-hour lot bases remain explicit. The execution-P&L v2 contract consumes an immutable typed lot resolution for variable lots. An exchange-published total is authoritative, with an exact series/quantity/source FK and a positive factor; calendar-day integration has a distinct named rule. Never infer nominal month hours, TOU or upstream exposure. Delivery and variation-margin processing remain separate capabilities |
| D120 | Keep exchange examples in an independent authorized namespace | A second local demo uses public product facts, exact sourced contract-size inputs, synthetic admission starts and synthetic observations. It has separate catalogs, workspace/identities, sessions, request-token scope and retained commands; identical business handlers serve both examples. Existing baseline definitions/trades are not repinned or rewritten. Product selection follows the exchange benchmark shortlist, not an asserted live liquidity ranking |
| D121 | Keep quoted basis futures as independent observables with exact settlement references | Regional basis futures retain an exact regional index and matching dated Henry Hub futures series under index-minus-future settlement terms. Direct quotes carry only their selected quote factor; settlement references do not infer live benchmark exposure. Negative and zero basis marks retain their declared quote type. The exact series primary key remains instrument identity; SQL and managed binding enforce authoritative product profiles and quote type. Settlement execution remains unimplemented |
| D122 | Preserve exact unit cash through risk position scaling and enforce current reference authority on raw operations | Unit pricers and execution bases retain ExactMoney until the position result becomes Money. Raw futures/swap writes recheck current reference scope after replay resolution and before reference/calendar reads; discount-default reads also require the selected version to be visible. Accepted control replay remains available under current trade authority |
| D123 | Tailor screens to current permissions, not persona names | Navigation, editing controls and banners follow the permissions the API reports; the backend still authorizes every operation. Readers see published schedules read-only |
| D124 | Serve display names and choice lists from stored data and the validating backend | Curves, rate curves and roles carry stored names; calendars and clocks are versioned reference rows; rule choices (schedule anchors/adjustments/selection, shortcut date roles, identity kinds, power blocks/intervals/labels, rate convention labels) are served by the code that validates them. The browser keeps no lookup tables of business names or codes |
| D125 | Calendar weekend masks use ISO weekday bits | `weekend_mask` bit 0 is Monday through bit 6 Sunday; `calendar_days` rows close (false) or open (true) a date. Recorded as a column comment |
| D126 | Schedules bind an exact reference calendar version | New and edited versions use engine `date_schedule_v2` with an exact-version calendar foreign key; retained `date_schedule_v1` inline calendars stay readable and are never reinterpreted |
| D127 | IANA clock rules must match the installed tzdb release | A rule's declared `tzdb_version` must equal the release read from the installed zoneinfo data; a mismatch or unknown release blocks the calculation |
| D128 | TOU products are authored definitions, not reference data | Edited in Power TOUs as drafts, published by approvers, immutable once published and bound to an exact sealed clock rule. Calculations use each product's latest published version, current inputs only. The preview starts with common ISO peak conventions to verify before settlement |
| D129 | Assign immutable booking facts on the server and compose product editors beneath Trade Terms | `server_booking_utc_v1` retains the original authenticated human workspace member, captured label, persistence timestamp and UTC date. Exact replay precedes stamping. SQL/API reject forged facts; amendments retain the original facts and separate amendment actor. Economic effective time stays distinct. Retained commit-derived timestamps and migration-captured labels have explicit provenance. Widgets share trade terms and product/side presentation; instrument-bound quantities replace the apparent trade total. The current executable operation still supports one instrument binding per trade |
| D130 | Price and shock temporary instrument drafts without booking | Product-only draft operations require an explicit market context, unit-aware quantity and valuation policy. Price returns a retained context; deal risk requires its own capability and deterministic base/up/down repricing of declared live commodity quote factors by one trading tick. Exact rational cash and derivatives preserve daily averaging fractions under `exact_rational_symmetric_deal_repricing_v1`; display rounding is separate. Interest rates are held fixed, factors are bounded to 128, and neither operation writes trade state. Futures report execution P&L/open exposure until settlement PV is implemented |
| D131 | Recheck exact reference scope at definition publication | New TOU and reference-calendar schedule publications require current read access to their retained clock/calendar catalogs inside the raw security-definer boundary. API/RLS draft checks do not replace this authorization. Accepted publication replay returns only the original control ordinal without creating another fact |
| D132 | Private hosted pilot uses invitation-only local accounts | Immutable principal subjects, supported salted password hashes, one-use expiring invitation digests, secure fixed-life cookies and server-side session revocation replace preview personas. Runtime and authentication database logins remain distinct, cannot own application objects and cannot assume each other or the security owner. Browser/session restarts retain installation identity and encrypted data-protection keys. The initial operator invitation is prepared privately; no default account or password is published. The API binds only an explicit private gateway address; an independent pre-DNAT ingress guard restricts its caller, and the database and collector publish no ports |
| D133 | Delivery-price collection is separate from forward marks | The bounded managed worker supports PJM hourly DA/RT LMP and ERCOT hourly DAM / fifteen-minute RT settlement-point prices. Exact source definitions retain unit, location and clock foreign keys; complete native intervals are required before a whole job batch can publish. Immutable operator-published operating-day boundaries come from the managed schedule compiler. Daily UTC scheduling wakes the same worker; per-job schedule switches, explicit windows, request rates, limits, service authority, execution fences and durable outcomes govern collection. No inferred forward curve or upstream exposure is created. Refined by D236 |
| D134 | Collection corrections join the certified knowledge clock | Whole delivery-price batches share the catalog publication gate. Every date-context capture first certifies committed collection batches against retained origin, database identity, timeline, commit tracking and the existing certified watermark. Exact cutoff reads retain context evidence and preserve A-to-B-to-A occurrences. The separate delivery-history operation rejects a bound historical workspace context rather than substituting current reference admissions. A future integrated fixing consumer must pin its selected data explicitly. Refined by D236 |
| D135 | Hosted application updates use attested GitHub Release bundles and a local pull operator | Only the current protected branch head with successful exact-source CI is admitted. Repository, signer workflow, source ref, commit and run attempt are verified without a server GitHub credential. Installed operator code and Compose remain pinned locally; matching schema, migration, private binding and ingress baselines permit image updates only. Preflight is isolated from public promotion; temporary infrastructure failures retry, deterministic failures quarantine, and interrupted promotion requires explicit reconciliation. Retain active/previous owned images; never infer a safe rollback of financial state |
| D136 | Scheduled jobs can launch agents, and authorized agents can create recurring schedules, temporary jobs and permitted child work | People, UI/API/MCP and agents share validated operations. Distinct management/launch/business capabilities, explicit bounded delegation and current authorization govern every run; service privileges never flow into launched agents. Durable occurrence/attempt identity, retained contexts, aggregate limits, expiry/cancellation and audit lineage follow the [execution contract](architecture.md#111-schedules-temporary-jobs-and-agent-execution). The bounded protocol is described in D140; full agent execution remains work |
| D137 | Use Docker Swarm for deployment and image-based agent/container execution | A trusted controller launches approved, digest-pinned execution profiles for admitted jobs. Agents request work through authorized operations, without Docker management or database access. Swarm resource state is separate from durable business state, fencing, current authority and aggregate budgets; see the [Swarm contract](architecture.md#112-docker-swarm-and-image-based-execution). This adds scheduling/controller processes to P01's initial application/database shape. Implementation and deployment migration/qualification remain work |
| D138 | Implement scheduling as a separately deployable .NET Worker Service in this repository | Reuse managed business/timing/authorization operations over stock PostgreSQL; no Rust scheduler or scheduler database extension is required. The scheduler can run on a separate eligible Swarm node, independently of API restarts. Durable occurrence admission, leases/fencing and recovery support overlapping replicas; node placement or a singleton setting is not a lock. Keep Docker management access in the separate execution controller. This resolves the scheduler implementation choice for D136–D137; the bounded implementation is described in D140 |
| D139 | Provide a shared Jobs & Infrastructure view with ownership-based job controls and separately authorized node maintenance | All authenticated members can open the view, see approved shared status and control their own jobs; administrators with installation-wide job-management authority can control all jobs. Detailed data access stays separately authorized. Node inspection, pause/drain/activate, governed placement and service restarts, plus typed host reboot/Engine restart follow the [maintenance contract](architecture.md#113-infrastructure-administration-and-node-maintenance). Job ownership/management does not grant shared-node control. Durable maintenance, capacity/quorum/stateful gates and uncertain-command reconciliation remain required; the bounded implementation is described in D140 |
| D140 | Bound the first executable automation protocol and retain uncertain compute | Implement explicit UTC once/fixed intervals, owner-wide quotas, current identity/delegation, forced RLS and a separately deployed .NET worker. Approved container profiles pin a single-platform manifest, platform, private network and resource bounds under `bounded_exit_v1`; process completion asserts no financial result. Never replay an admitted uncertain create or release compute reservations from missing history. Availability commands use exact versions and shared launch/maintenance admission; unknown updates require a higher-version observation-preserving fence. Full agent/tool runtime, external stop attestation, host management and real Swarm/deployment qualification remain work; see [automation](automation.md) |
| D141 | Release migration order preserves the installed reference-intake schema before durable execution | Retain immutable migrations 0001–0033 and the exact 0034 reference-import DDL. The durable-job and Swarm schemas follow as 0035 and 0036. Release checks pin retained bytes and unique sequence numbers; restricted import and scheduler executors cannot inherit each other’s authority. Reference intake is a trusted separately managed operation; no source datasets or capture runner are included in this release. Installations using earlier durable-job filenames require explicit reconciliation; automatic schema adoption remains disabled |
| D142 | Page navigation targets less than one second and loads only consumed observations | Measure initial usable application data and every business page separately, including hydrated grids/charts under explicit workload and network conditions. Navigation requests the catalog without observation history; price evaluation explicitly requests selected-frame source versions under unchanged current authorization and historical RLS. Trading and power reads load their explicit frame facts; schedules and unit catalogs need no observations. Existing full workspace inspection remains available. Format displayed result points only. Initial bootstrap may explicitly project fresh navigation permissions for its verified account, consumed once after an exact identity check. Later access refreshes and every business operation reauthorize independently; no shared permission or calculation cache is introduced |
| D143 | Historical visibility uses exact publication lookups and Blotter loads one authorized workspace | Distinct bundle and observation branches use existing publication indexes. Reference visibility joins its exact definition publication directly, retaining both current catalog authorities, certified receipt identity, knowledge prefix/cutoff and half-open effective interval. Volatile PL/pgSQL definer functions retain their ownership and privileges; function-local generic planning reuses plans while current authority is checked on every invocation. Where an exact restrictive visibility policy already invokes the same fresh reference-access guard, its permissive policy omits the duplicate check. Catalog-only and separate quantity restrictions remain; migration 0041 rejects unexpected policy/helper definitions. A combined Blotter operation returns catalog and report under one bound request/context. Navigation explicitly requests a booking directory without enumerating or pricing unused entry contracts. Selecting futures entry reloads both the priced catalog and report together before opening the form; historical requests retain their selected read-only context. The omitted mode preserves the legacy priced-catalog response, and unknown modes are rejected. Existing report operations still reauthorize exports, charts and explicit refreshes. No permission cache or database extension is introduced |
| D144 | Business reference reads follow declared definitions; inspection loads the selected table | Bound business reads retain only the exact reference relationships consumed by their definition bundle, including declared futures basis benchmark relationships. This read closure does not infer pricing dependencies or exposure. Complete reference inspection uses a separate authorized operation and explicit bounded pages; search applies to the selected table. Every request preserves its exact revision and historical context and checks current authority. Legacy full workspace inspection remains available; partial reference responses identify their scope |
| D145 | Portfolio risk calls the book's common valuation contract | Clarifies D10: risk creates immutable factor scenarios and calls `Book.PV(context)` for values by trade/instrument binding, book and currency. Books delegate through trades to `Instrument.PV(context)`; pricing methodology, quantity application, execution bases and lifecycle valuation remain behind that boundary. A common complete dependency declaration lets the valuation planner skip unaffected work and share unit calculations across holdings in the same resolved context (D146). Zero sensitivity does not remove dependencies; missing declarations fail explicitly. The managed portfolio projection now delegates through shared unit values and book-owned holding arithmetic; futures remain explicitly execution P&L until full settlement PV is supported |
| D146 | All instruments have unit-level valuation and all holding quantities remain external | Clarifies D11 and supersedes optional quantity-scaling capability language. A trade stores instrument references with quantities; there is no alternate trade layout. Resolve each shared instrument once per complete context and value it once per scenario across all references. Book/trade valuation applies each external quantity and retains separate binding results. Holding quantity never enters unit pricing or its reuse key. A nonlinear market payoff still obeys this unit contract; incompatible quantity-dependent behavior needs a compatible explicit model rather than an opt-out |
| D147 | Aggregate exact book/unit quantities before summary scenario arithmetic | Book-only risk compiles external quantities and execution costs once per book/unit, retains `ExactQuantity`/`ExactMoney`, and applies shared unit values to those groups. Detailed reports retain separate holdings. Numerical policy `exact_18_fractional_book_repricing_v2` projects only the selected output boundary, allowing exact offsets before bounded output. Futures convention `unprocessed_futures_execution_pnl_risk_v2` validates holdings without precomputing bounded P&L and rejects conflicting pricing bindings for a shared instrument/context. Streaming factor pairs share one base without retaining the full scenario matrix |
| D148 | Instruments report their own market factors through a common method | `Instrument.MarketFactors(UnitValuationContext)` returns an immutable typed `QuoteFactor` list with exact series/use, contract, quote/unit, selected observation and source metadata. Implementers own the declaration; curve consumers may use the context's shared dependency resolver. Books ask each unique instrument once, validate complete pinned factors, then union/deduplicate by factor identity. The risk engine does not inspect product fields or infer exposure. Discovery retains hedged and locally zero-sensitivity dependencies, omits unrelated inputs and gives independent all-in marks no inferred upstream factors. Selected books/portfolios scope the API's suggested list; bump size/convention remain explicit. Current factor discovery covers live commodity quote inputs; rate/volatility/parameter shock families require explicit future contracts |
| D149 | Ship the page assistant in three phases, starting with a local-preview core | Phase 1 ([ai-assistant.md](ai-assistant.md)) runs only under `--local-preview` and is inert under `--hosted-pilot`; phase 2 adds agent notes and phase 3 the hosted baseline. This brings a bounded Milestone 5 slice forward without meeting its acceptance criteria. Phase 3 waits on four undecided gates: retention, approval to enable a provider, per-catalog data scope and API egress control; D192 decides them and runs phase 1 hosted |
| D150 | Allow model-provider vendor names as an exception to the AGENTS.md company-name rule | Anthropic, OpenAI, Google, xAI and Meta and their public API identifiers may appear in source, documentation, fixtures and commit messages. No real token prices ship. Meta is shown but cannot be enabled until its usage fields are confirmed from published documentation |
| D151 | A docked assistant answers page questions and navigates | Agents use the fixed read tools, Navigate and Show card; more tools come later. No tool reads delivery-price data. Cards are application-drawn tables, bar charts, line charts and value tiles; no embedded page views and no agent-written code. Server-sent events, not SignalR, carry the stream |
| D152 | Administer models and agents in Settings › AI; only agent managers create agents | Usage, Models & prices, Providers and Agents tabs. The Agents form follows the mock, with saved drafts and Publish replacing "Test in pop-up"; an unsaved definition never runs. Only `ManageAgents` holders create agents; users pick among agents allowed for their role, and trader, risk analyst and market data editor get `UseAgents`. Model administrators price models through an operator-assigned role holding `ReadMarketData` |
| D153 | Bound phase 1 cost per turn and report the cost of every call | Per-turn limits plus usage and cost by model, user and agent. Answers are completed turns; other turns' cost is a separate line. Usage days use a viewer-picked zone, prefilled from Profile, over half-open ranges (D25). Day-over-day and period P&L comparison is the next slice and monthly spending caps a later one; until caps exist this deviates from the architecture §9 provider budgets and Milestone 5 resource budgets |
| D154 | Re-grant on a provider or model change; replay on the user's own access | A version that changes provider or model needs a new grant, and every grant has a maximum duration. Stored conversation content replays when the asking user can currently read its sources; replay needs no grant |
| D155 | Keep conversation memory and one agent per conversation | Follow-ups send earlier turns as labeled history (ai-assistant.md §7.6). A conversation keeps its agent; the last explicit agent choice is a browser-local preference (D45). A background indicator and an unread History marker show turns that finished while the panel was closed |
| D156 | Agent notes add a common notebook alongside agent notebooks | Phase 2 designs agent notebooks plus one workspace notebook that every agent can read and write. P35 records the proposed note authority; whether agent notebooks are also per user is settled with that design |
| D157 | Keep exchange first-trading facts separate from explicit sample execution eligibility | A date-only source milestone keeps a null event instant. Candidate terms require an exact-version admission, generated-mark provenance, a qualification source and the supported execution convention. Named eligibility rules retain either an exact source instant or the explicitly approved next native-day boundary with its clock offset and tzdb release. SQL independently checks the zone/date offset; managed operations require the installed clock release. Conflicting or absent qualifications fail. Eligibility is a binding/context input, not contract identity or an invented exchange event. Source-series foreign keys record lineage, not proof that all qualified terms equal the source. Official settlement, variation margin and delivery remain unsupported |
| D158 | Supersede the installation-specific restrictions in D157 with versioned futures execution policies and separate installation seeding | Source product/series facts, execution qualification and price provenance are distinct. `futures_execution_policies` binds exact series/implementation to an explicit eligibility bound, permitted price source/role and retained qualification evidence. Date-only qualification retains an offset/tzdb proof checked against the pinned source clock and a bound after the whole source date. Source first-trading instants may remain NULL; an application bound never overwrites an exchange fact or enters instrument identity. Candidate terms can execute under the selected supported policy; placeholders cannot. Synthetic legacy fixtures retain their explicit exact-instant convention. The exchange example contains sourced product facts and generated trader marks, with invented controls confined to tests. Hosted data/scripts remain private installation inputs; no host names, source-locator prefixes or pilot-only policy names enter domain eligibility. Migration 0040 preserves the public 0038/0039 history and converts unambiguous retained admissions without changing source facts or contract keys; schema upgrades still require a qualified release |
| D159 | Resolve job-entry wall clocks in the user's display zone and transport UTC instants | Shared managed time inspection handles DST gaps and repeated clocks. Jobs and infrastructure timestamps reformat after a profile-zone change. Fixed intervals retain elapsed UTC recurrence; display preference does not change that business rule. Date-only booking/delivery facts remain literal dates, and explicitly authored contract clocks retain their own convention. Time inspection is an authenticated formatting utility and does not require market-data authority |
| D160 | Give navigation and trading reads explicit scopes | A distinct typed market-navigation read returns only consumed curve classifications/names, frame summaries and exact revision/context identities under current authority. It never masquerades as a full workspace. Full hydration is explicit, rejects changed identity/selection and precedes pricing/reference presentation. First pricing can include the complete catalog projection from the same bound model read, with catalog observations empty and selected result sources separate; default evaluation and legacy full endpoints remain unchanged. An explicit Blotter directory can skip unused valuation reads only when both raw product revision tables are empty under the bound authorized trading context. Exact sealed bundle/reference visibility, selected observation completeness, view/books and final authority still apply. Any visible revision uses valuation. Priced trading workspace reads explicitly consume first/last-trading milestones and exact primary snapshot observations, retaining the complete catalog/report, required-fact failures and selected historical cutoff. Generic/full metadata reads and supplemental swap correction resolution keep their broader scope. Only `POST /api/trading/workspace` JSON negotiates gzip (fastest), including HTTPS, after existing request admission; clients that do not accept gzip retain identity transport. Bootstrap and authentication remain outside compression. These projections defer validation of unused economic material rather than inventing financial defaults. Measure first-use hydration and cold API startup separately from warm navigation |
| D161 | Author custom curves as exact, versioned typed graphs with shared backend evaluation | Visual editing, formulas and qualified SQL transformations consume the same graph contract. Sources/calendars/schedules retain exact-version foreign keys. Prompt ranks before price retrieval and rolls at the recorded last-trading cutoff. The selected `ng_balmo` averages complete daily forwards inclusive of context date and month-end at one observation instant; separately quoted BALMO remains a different observable. Historical windows distinguish rolling ranks from a context-locked dated contract and freeze past samples. Means retain exact rational quote units and live primitive lineage. SQL initially means one registered immutable sum/count transformation over supplied scaled-integer panels, with no market-table reads or arbitrary SQL execution. Current implementation and limits are recorded in [custom curves](custom-curves.md); bookable custom indices and historical VaR/covariance remain later work |
| D162 | Select exact market snapshots through a scoped availability calendar | As of remains the market valuation context, separate from effective/knowledge-time controls. Managed operations qualify complete inputs for the chosen curve or full custom graph. Unavailable dates are disabled; no alternative close or partial data is chosen. Displayed days and close times follow the user's time zone while exact snapshot identity, UTC instant and financial business date remain unchanged. Multiple closes or correction versions on a displayed day require explicit selection. Availability is authorized under the selected current or certified historical context and rechecked on use |
| D163 | Publish the homepage source separately from the authenticated workspace | Buildless HTML/CSS/SVG in `site/` describe the open-source project and a bounded invitation-only pilot. The generic gateway serves exact `/`, `/_landing/site.css` and `/_landing/favicon.svg`; unknown `/_landing/` paths return 404. Public files accept GET/HEAD only. Exact `/app` forwards to the application's native `/` with the original query, `/app/` redirects canonically, and only the exact login endpoint rewrites an upstream `Location: /` success to `/app`. Other authentication, assets and API routes remain upstream-protected. API routes keep the API's body limits, 8 MiB for the interval-series revise route (D173) and 32 KiB otherwise, and the gateway answers a larger API body with 413 and the API's `request_too_large` code and message; a change to an API body limit changes the template with it. The revise route therefore takes anonymous bodies of up to 8 MiB at the gateway, buffered before the application authenticates them. Public headers are confined to the public files. Installation paths, upstream addresses and certificates remain explicit private inputs. The image-only release envelope and automatic updater do not install static content or gateway changes; those need separate operator qualification. Owned stock nginx plus a synthetic upstream qualify routing, not real account authentication or a production deployment |
| D164 | Edit display metadata through immutable reference versions and explicitly apply it to current presentation | The first editor covers names, descriptions, unit symbols, contract labels and optional location points. It clones a retained reference revision, limits writable fields and requires a separate reviewed apply operation. Activation proves identical reference keys and non-display values. Edit receipts retain typed foreign keys to original and edited item rows; activation pins the same exact source in its foreign key to the edit. Request identity, actor and workspace scope remain explicit. Pricing references, native observations, market bundles, typed units and booked trades retain their original identities. Current presentation receives a separate display revision; historical contexts show the original metadata and cannot inspect later edit requests. Existing write/seal reference permissions authorize the same operations from UI and API. Financial-term editing and curve/price migration require a separate policy and remain outside this workflow |
| D165 | Resolve swap fixings from exact index observation schedules | Remove the editable first-fixing boundary from new swap entry. A sealed market bundle binds each supported index observation period to its exact quote, source, actual series, dated publication instant and implementation. Pricing projects unresolved observations and consumes exact final `index_fixing` snapshot pins at or after each required publication. Missing due fixings are errors; no forward fallback, skipped dates or changed averaging weights. Actuals are frozen facts with no commodity quote shock. Qualified schedules are installation data, never inferred from curve names, delivery starts, expiry or profile time zones. Existing projection-only sides and their historical identities retain their declared boundary; exact legacy retries remain available but new manual-boundary writes are closed. Payment/settlement state, production index qualification and broader observation-profile authoring remain separate work |
| D166 | Reject ambiguous swap discount defaults on write and on lookup | Refines D94: missing and ambiguous currency defaults are both errors. `currency_discount_default_key` allows one mapping per exact catalog, definition revision, reference catalog/revision, currency, effective instant and publication; a later publication may still replace a mapping at the same instant, consistent with D99. The lookup also raises `swap_discount_ambiguous` when more than one mapping shares the latest instant and publication, rather than choosing by query plan. Migration 0052 stops on retained tied mappings without merging or deleting them |
| D167 | Share saved contract-date rules as exact published versions | The contract-date editor saves durable workspace drafts and separately publishes immutable versions. Each date field is a literal or an exact published schedule version with its occurrence and business-day offset; the UTC last-trading cutoff is explicit and independent of delivery end. Ordered nonoverlapping delivery periods and increasing last-trading instants are required, without assuming last trading precedes delivery. Saving retains an exact certified validation context. Resolving a selected published definition requires an explicit context and current access to its exact schedules and calendars; later symbolic schedule publications cannot rebind it. Publication participates in the existing certified knowledge capture. Request replay retains a command hash and control identity, without a stored full-content response. Session candidates are not imported automatically. The current consumer is the shared date library and resolver; this does not activate exchange contracts, publish price curves or change booked side dates |
| D168 | Manage delivery-price collection schedules alongside automation jobs without conflating their executors | Jobs lists the existing scoped collection jobs separately from automation jobs. A current workspace `ManageJobs` grant permits amending the name, schedule switch and inclusive delivery-day offsets under the registered collector's exact catalog/workspace scope. The registered service, source identities, operator-enabled gate and qualified daily UTC cadence remain fixed. Editing shares the existing collector execution fence and rechecks current caller/service authority after lock waits and before replay. Immutable exact-generation command receipts contain control terms, not cached source or service metadata. The UI retains uncertain requests across reload and denial, validates the returned receipt and offers explicit reconciliation. Collection remains a separate delivery-price operation; it does not publish forward marks or run portfolio valuation |
| D169 | Generate explicitly selected pilot closes and persist whole-book mixed-product risk through fenced jobs | The generated-pilot operation requires an exact retained anchor, selected books and root curves, synthetic source, seed, per-native-leaf clearing-tick bounds, explicit UTC calendar/schedule and versioned valuation policy. It never presents generated marks as official settlement. A registered service uses a dedicated runtime login and short-lived claim-bound context; current service and owner authority fence both publication and calculation. Marks and reports commit separately, and retries reuse each exact occurrence. Futures execution P&L/open risk and swap present value/risk remain separate currency groups. Exact anchored final settlements and published index fixings remain frozen and cannot be omitted or regenerated. The shared black-box adapter uses external quantities, exact model/dependency/discount pins and `exact_rational_portfolio_repricing_v1`; commodity quote shocks hold rate inputs fixed. The legacy interactive futures-only portfolio route and its exact18 policy remain unchanged. This is not variation-margin accounting, historical VaR or a new latency qualification |
| D170 | Price multi-year monthly swaps by composing existing monthly sides with explicit quantity allocation | Managed monthly legs retain one to 1,200 complete contiguous months, exact price/fixing/payment contracts and per-payment discount inputs. Quantity means per month, total allocated equally by month, or total allocated by actual calendar days; the selected basis enters economic identity. Allocations remain rational through side PV, then membership signs and external trade quantities apply once. Shared pricing-binding declarations protect definition reuse. Lookup indexes and one-base quote scenarios retain full immutable context and validation; numerical rounding utilities are shared without moving financial rules out of their modules. The booking/editor and persisted risk API limitations remain explicit in [pricing lookups](pricing-lookups.md) |
| D171 | Connect an explicitly mapped power forward to collected native-interval delivery prices | One exact model/curve/source association feeds the existing certified price-history reader at an explicit cutoff and the shared interval settlement kernel. Current selected TOU/clock/shape terms and exact source/unit relationships are retained; a complete operating day is required. MW applies its explicit shape and actual duration once; already-shaped MWh cannot be shaped or integrated again. The unsaved authorized HTTP operation preserves UTC/DST identities and batch lineage, and does not assert publisher finality, booked cash or payment. See [power preview](power-preview.md#collected-interval-delivery-value). Existing synthetic flat-forward calculations retain their meaning |
| D172 | Price every market bundle from its snapshot's exact pins | Refines D99, D160, D162 and D165. A snapshot names exact observation revisions in every market bundle a valuation reads, including separately sealed swap bundles. A separate bundle uses the revisions its snapshot explicitly selected, otherwise the bundle frame declared for the snapshot's instant. Working frames and their re-reads under an earlier captured context never follow `corrects_id` chains. A correction or another revision reaches valuation only through an operation that creates a new snapshot: an authorized correction, explicit selection of another revision of the same source fact, or a certified context. Only frames a certified context resolves under its explicitly selected `latest_correction` policy follow each unfrozen pin to its latest revision visible at the cutoff; recorded closes stay exact. Separate-bundle corrections and selections are API operations; no screen offers them yet. Closes record main-market pins only, so registration from a snapshot with separate-bundle selections is refused (`close_swap_market_pins`). Previously the separate daily bundle followed every pinned observation to its latest correction on every read, so one snapshot could price differently over time; a recorded close's separate-bundle prices now come from the declared bundle frame for its instant, which the close itself does not record. Main-bundle closes, bookings and retries already stored exact pins and keep their meaning. Which publisher corrections count for settlement remains a revision policy of the settlement definition. The publication-driven settlement design ([settlement plan](settlement-plan.md), P39) keeps this rule: an accepted trade fixing retains its selected pins, and a later vendor correction is a correction candidate, never a silent replacement |
| D173 | Store audited interval series as delta revisions read as of an explicit knowledge time | Implements phase A of P40 ([ppa-design](ppa-design.md) §5; [implementation](implementation.md#audited-interval-series)). A series pins a kind, one exact specification in a sealed reference revision, a clock and a 5, 15, 30 or 60 minute resolution; a capacity-factor forecast, like a multiplicative shape, takes only a dimensionless value specification. Each revision stores only the intervals it sets or withdraws (`null` withdraws), with its actor, a 1 to 500 character reason, its request identity and a commit-certified knowledge time; a read derives each interval from the latest revision known at an explicit, inclusive cutoff, and stored intervals change only at their exact start. A revision holds at most 200,000 intervals: its stored-overlap check and writes hold the shared knowledge gate lock, which blocks every temporal read, while checks of the request alone run before it, and a 701,280-interval append exceeded the 5 s command timeout, so larger imports are several revisions. A read window is bounded by duration, and storage refuses a From or To strictly inside a stored interval of any revision, withdrawn or not (`interval_series_grid`), so no interval is dropped at either end; reads never load the clock, and a whole local day across a half-hour clock change is a window. Only the revise route accepts bodies up to 8 MiB (all others 32 KiB), and a body over its route's limit is `413` `request_too_large`. Reads and writes need read access to the pinned sealed reference revision. Every request member is required and unknown members are refused with `400` `request_format`, so nothing is defaulted. Operations refuse a historical session, storage hides series from one, and reads take an explicit `knownAt`, so current data is never substituted for history. CSV imports never withdraw an interval. A revision expands onto the pinned clock, and an IANA-zone clock requires the installed tzdb release to equal the pinned rule's (D127), so after a tzdata update a series on one cannot be revised until it can be re-pinned (O27); its reads stay available. Owner bindings (curve, asset) wait for the phase B and C consumers. No consumer, historical session mode or fix for the 4,096-contexts-per-catalog limit (O07) exists yet; the measured 20-year workloads and these limits are in the implementation record |
| D174 | Price interval curves from exact marks, block shapes and collected actuals at explicit knowledge times | Implements phase B of P40 ([ppa-design](ppa-design.md) §6; [implementation](implementation.md#interval-prices-curves-and-block-shaping)); refines D173, whose shape series a shaped curve now binds, and D171, whose collected-price source and reader resolve delivered intervals. An interval contract is a forward with an exact UTC interval and its clock labels; it expires at its interval end, so an ended interval's mark is never built. A block curve crosses date-only constituents with all hours or one published TOU version's peak or off-peak window on its delivery profile's clock, grid and holidays; a shaped curve calibrates its blocks' marks onto one shape series, additively or multiplicatively, so each block's duration-weighted mean equals its mark exactly, and blocks must cover a window exactly once. Each price keeps its interval, exact fraction and lineage; six-place display is presentation. A window is compiled once and evaluated per scenario, recalibrating only blocks whose mark, mark lineage or shape values changed. Collected actuals are due by an explicit rule (start or end plus offset); an interval ended at the valuation instant takes an actual known by the cutoff and a live one only an actual known at that instant, so nothing is priced in hindsight; a due actual that is missing is refused, an ended interval before its due time is unresolved with no price, and only a live interval takes its mark; aggregates are explicit duration-weighted means per whole local hour or operating day that keep their native intervals, and cash remains Σ(MWh × price). Marks come from the frame's pins, and a frame selects only intervals live at its valuation instant, so a plain read refuses delivered ones; a window must lie on the curve's intervals throughout. Shapes and actuals are read at an explicit request knowledge time, because a frame's cutoff is its valuation instant, and responses name the knowledge contexts read. Every request member is required, as in D173. Reads need ReadMarketData and refuse a historical session; a curve whose definition cannot resolve is listed as unavailable with its reason. Shaped curves do not resolve against actuals yet, and side consumers, booking and settlement remain phases C and D |
| D175 | Interest-rate curves have no commodity, and the browser presents server classifications | Replaces the synthetic `rates` commodity category, which shared the commodity code space: a commodity coded `rates` would have shared the interest-rate filter value, group and display label. The market-navigation read gives each commodity curve its exact commodity code and label and each interest-rate curve `category: null`; its `rates` market type identifies it. The Market filter lists commodities only; interest-rate curves remain under All markets, the Rates type filter and their own group. Sidebar groups are keyed by commodity or market type and never merge by name. The curve detail, futures tick and rate handling use the directory's classification; the browser's own classification from workspace rows is removed. No backwards compatibility: saved collapsed groups reset, and D176 resets saved Market choices |
| D176 | All markets is the absence of a commodity filter, not a code | The Market filter used `all`, which is also a valid commodity code, so a commodity coded `all` could not be chosen on its own. All markets is now no commodity: `null` in the filter and saved preferences, and the empty option value on the page, which no validated commodity code can equal. The browser preference keeps the Market choice in a new `market` field. No backwards compatibility: Market choices saved in the former `commodity` field are ignored, so those profiles show All markets |
| D177 | Page-assistant tools read the page's exact frame and return every read refusal as a step | Refines P28–P30 and P32 ([ai-assistant.md](ai-assistant.md) §7.1–§7.3, §7.5, §14). A turn's record keeps an As of frame's exact pins (name, observations, frozen observations, curve-contract and swap market selections, latest-correction resolution), from which tool reads rebuild the frame. A read's own refusal of its input returns to the model as a refused step with its code and the page's fixed text, and the turn continues. Custom curves, Interval series and Jobs & Infrastructure are not supported pages, and Read market data reads the pinned frame only. Steps record their operation, input, code, read instant and the accesses their reads ran under; results are stored with cards. Worker writes evaluate the source-access check as a context issued to the turn's agent would, proven equal by a check, while reads still run in an agent context. Navigate follows the read tools, refusing an unissued handle or a page the delegator cannot open as `tool_arguments_invalid`. A call whose tool requests would take the turn past its steps has none run (`limit_tool_steps`) |
| D178 | Nested risk scenarios inherit the shared resolved financial context | The UI header, interactive risk and jobs use the same effective/knowledge environment and pinned market frame. Immutable child layers apply signed absolute parent-relative quote shifts under an explicit rebuild convention. Parent/sibling contexts remain unchanged; risk bumps are centered on the selected stressed parent. Date rolls, new factor families and future regimes need supported typed contracts |
| D179 | Persist complete risk runs with narrow fenced workers | Admission retains actor/delegation, authorization generation, exact context/market/book/observation references, scenario request and managed engine fingerprint. Short transaction-local loading precedes pure calculation; current authority and exclusive lease ownership fence result publication. Recovery retains the manifest, cancellation requires truthful acknowledgment, and workers claim only compatible engine versions |
| D180 | Scale independent risk runs before distributing one run | A PostgreSQL queue admits multiple worker replicas while each worker preserves shared curve/unit pricing across its selected books. An optional Docker Swarm definition places those processes; no hosted deployment is implied. Single-run scenario sharding, distributed reduction and recurring date rules require separate workload and failure evidence |
| D181 | Enforce the existing instrument valuation contract in interactive portfolio risk | Implements D95/D145/D148 through one authorized portfolio materializer and pure scenario calculation, shared by interactive jobs and generated-close reports. `Instrument.Declare` is required; default `Value` calls `PV` and validates the returned instrument and scenario. Generic PV models can return exact cash directly, while side models retain their own schedule/PV and required discount inputs. Futures use their explicit execution-P&L implementation; no full futures PV or synthetic lifecycle schedule is introduced. Holding and book results retain exact rational amounts under the existing rational policy, with separately labeled twelve-place ties-to-even display values. Current futures/swaps can span exact primary/supplemental market partitions; all holding rows survive unrelated shocks with unchanged cash and zero sensitivity. Migration 0074 retains supplemental input relationships through exact foreign keys. The new portfolio routes and convention coexist with legacy futures artifacts. Universal `Instrument.GenerateSchedule`, additional product implementations, factor families and cluster capacity qualification remain work |
| D182 | Prepare connected historical curve graphs and qualify their managed execution before hosted integration | Retain dependency references through primitive quotes, means, weighted baskets, rolling selections, schedule mappings and common instrument PV. Resolve logical bindings under explicit effective/knowledge coordinates into immutable exact versions. Use compact managed adjacency plans, complete dependency declarations, scenario-owned caches and nested additive overlays. Separate primitive quote shifts from calculated-output offsets; support explicit parallel, point/bucket and dated-contract targets. Keep all date, pricing and risk behavior in the backend. The executable prototype and integer graph benchmark do not establish booked custom-index integration, SQL activation, distributed reduction or million-point financial throughput. Volatility and correlation families require separately specified conventions |
| D183 | Connect published managed custom outputs to existing monthly indexed swaps and shared portfolio risk | An optional explicit projection pins the saved curve version, output node/position and selection/implementation policy. Preserve contractual index/fixing/payment rules; due actuals cannot fall back to custom forecasts. Distinct custom bindings have distinct v5 economic identity and deterministic runtime request aliases; v3/v4 encodings remain unchanged. Exact rational projected values pass through shared side/instrument PV and external quantities. Migrations 0076–0077 retain published bindings, graph manifests and per-sample exact observations separately from primary snapshot selections. Rebuild under the same certified financial context with current actor/delegation and worker service authority, then compare the manifest. Managed saved operators within the same source catalog/revision are the baseline; SQL transformations stay panel-only, HTTP shocks remain primitive quote bumps, and no trade-editor picker is added. Database logical activation, daily custom mappings, cross-revision histories, distributed reduction and large financial-workload qualification remain work |
| D184 | Page-assistant reads bind the ask's capture and return the page's values with its display | Refines D177, P29 and P30 ([ai-assistant.md](ai-assistant.md) §7.2, §7.3, §8, §14), decided on the owner's delegation. Working-data reads bind the context the ask captured, as of the turn's frame, and never capture or take lock 730849102; current-only definitions bind no context and are labeled with their read instant. Contract definitions are read with the schedule library under the one bind a request makes, so the page and `market_data.contract_definitions_v1` share that read, keeping §7.3's mapping. Page reports name one id per navigable view from a closed set; ask refuses any other, before admission and writing nothing (`page_report_invalid`), with the parser Read current page dispatches with. A refusal carries the fixed text the server keeps for its code, or one fixed generic sentence, and each operation declares the refusals that mean the page's selection is unavailable (`page_selection_unavailable`). Results show amounts as the page does, under `tool_amount_display_v1`, from the inputs the page starts its display with: a trade as its editor shows it, from its contract's settlement-price increment and the deal's saved formats, read under the agent's context in the read's transaction, and what the page shows by the viewer's own display precision, a preference outside the turn's pins, from the default; one corpus, with those starting inputs, asserts it in the managed and the page's checks. Navigate is not offered until it is built, and a request for a tool not offered is unreadable, keeping its final usage. Reads after a call whose usage is unrecognized or past the input limit run, and the next open ends the turn; the worker checks only the step limit before reading, the database remaining its backstop. A denied read is a refused step, and its publication recheck decides whether the turn continues. Each read is bounded by the runtime set's `ToolRead` (5 s over all its commands) and runs on agent pools of its own (`openetrm-agent-reads`); a read that returns once its turn is to stop is no step, and one its bound stopped fails the turn `limit_elapsed` at the deadline, else `tool_failed`. D245 replaces `ToolRead` with each agent version's time per read and `limit_tool_read` |
| D185 | A turn's stream serves the latest turn, reads every event but pushed text under the replay recheck, and states each ending as the server reads it | Refines P33 ([ai-assistant.md](ai-assistant.md) §7.5, §8, §13, §14). A stream serves its conversation's latest turn only, with a replay window of 256 of that turn's events. Answer text reaches a stream in memory once its publication recheck has committed, which implies the replay recheck at that instant; every other event, a gap and the ending are read in the owner's own request context with the replay recheck, a tool step through a wake and that read. Every event's data says whether the replay recheck hides the turn's answer (`hidden`), and the terminal event whether the answer is `incomplete`; the panel derives neither. Text committed before an access loss may still reach a stream admitted before it, once, within its buffer or a read under way; nothing appended after the loss reaches one. `stream_expired` follows the turn's elapsed limit plus the admission deadline. Admission accepts a GET without Origin, refuses a foreign one and requires `Sec-Fetch-Site: same-origin` and `Sec-Fetch-Mode: cors`. Any of the owner's sessions may stream a turn, replacing its older stream, with no per-session cap; a non-terminal turn's stream may start the worker as an ask does, and a lost generation or shutdown ends it `agent_runtime_unavailable`. A read refused for lost `UseAgents` ends it `access_denied`; any other failed read cuts it without a final event, as a full buffer does. A missing or expired session is `session_ended` on every assistant route. The runtime set's version changes with its stream values. The conversation read shares the stream's event helper and answers byte for byte as before. Stop and mark read stay idempotent without an operation identity |
| D186 | Use one connected curve evaluator, typed weighted recipes and portfolio scenario coordinates end to end | Supersedes the active SQL panel path and the legacy futures-risk HTTP coexistence recorded in D161/D181/D183. Ordinary, basis and custom curves compile connected topology and evaluate through the same graph; source compilation does not price nodes. `custom_curve_recipe_v2` adds multiple named source ports with exact member weights and explicit `signed_sum_v1` or `normalized_weighted_mean_v1`. Migration 0080 persists those relationships and refuses existing saved recipes pending explicit reset/rebooking. Migration 0081 requires an explicit archive/reset of all retained old risk jobs, retains each prepared graph manifest and exact scenario axes/resolved points, and anchors runtime recipe coordinates to that manifest. The portfolio operation is the sole portfolio-risk HTTP/job contract, exposing primitive/output coordinates and contract/point/parallel/whole-bucket targets with nested layers. The visual/formula workbench and authorized monthly projection picker transport backend contracts; selection, formulas, dates and arithmetic remain managed. Typed source operations retain 18-place decimal representability; connected combinations/PV retain exact rationals. Logical database activation, daily custom mappings, cross-revision history, new factor families, distributed within-run reduction and financial throughput qualification remain work |
| D187 | Reuse published custom outputs as exact connected panels | Extends D186 with `published_source`, a typed catalog/curve/version/output reference and explicit `all_output_points_v1` selection. Migration 0082 persists normalized exact output-node FKs and checks the complete ancestry under current authority and the certified context. Parent panels preserve graph links, grouping metadata, native sample pins and manifests through preview, monthly booking, nested risk and worker reconstruction. Exact-version DAGs permit older versions of the same logical curve; cycles and paths beyond 32 published-source edges or 256 exact versions reject. Shared ancestors prepare once per operation. Source execution eligibility is explicitly retained separately from risk repricing. Resampling published panels at new dates, arbitrary panel alignment/weights, wider market revisions and performance qualification remain work |
| D188 | Integrate live correction contexts with the common calculation path | Live is an explicit, read-only selected-fact correction policy over fixed effective/valuation/observation coordinates, sources, roles and contracts. Commit-serialized publication heads plus current permission/scope vectors drive identifier-free PostgreSQL wakeups and authorized SSE context summaries. Active screens use existing backend reads; risk and retained jobs pin one admitted generation. Shared graph memo pages advance separately from scenarios; semantic slot matching preserves full financial identities, and unique instrument PVs rerun per frame. Bounded notifications, latest-only browser refresh, visible stale status and explicit server-side pause keep update handling integrated. Migration 0083 adds wakeup triggers/head access; no new financial data table. Broader live quote/time/rolling policies and financial throughput qualification remain work. See [live calculation](live-calculation.md) |
| D189 | Persist complete custom-curve effective timelines and map daily projections by delivery identity | Migration 0084 publishes immutable, optimistic and idempotent timelines of exact published version/output FKs. Certified knowledge selects the latest timeline before resolving its disjoint half-open effective intervals; gaps and empty plans are inactive, without fallback to older plans. Effective preview uses the ordinary graph path and explicit Live intent; booked instruments and retained jobs remain exact. Migration 0085 supports daily calendar arithmetic projections through `exact_delivery_interval_v1` with a null position; monthly scalar policy and identity remain unchanged. Unresolved resets demand exact one-day outputs, shared ancestor demands coalesce before historical sample loading, and only consumed eligible forecasts compile. Actual precedence and full schedule weights remain intact. The UI transports authorized backend contracts. Native activation, cross-revision resampling and financial throughput qualification remain work |
| D190 | Navigate opens a page the assistant reads or a target a read in the turn issued, keeping the turn's pinned context | Refines D177, D184 and P30 ([ai-assistant.md](ai-assistant.md) §7.1, §7.3, §8, §14), decided by the owner on 2026-10-22. A target is a page report plus the pinned context: one of the seven pages the assistant reads; a selection in that page's report form, the input of its read tool for a view it shows, or none for the page as it opens; the turn's knowledge mode, the context its reads bind and its As of frame; whether it is the page, a record or a filter; and a label the server composes. Every read issues its own selection as a filter target, a curve read its curve and a trade read the trade at the exact revision it read as record targets, and the curve directory and Blotter rows issue each curve or trade they list as a record target; there is no separate context kind. Results carry each target as a handle numbered across the turn (`n1`, `n2`, …), within the 500-record and 64 KiB bounds, so read results change and the runtime set becomes `turns_v4`. The worker holds a turn's issued targets in memory, a handle naming its target only once its step is published; only a target Navigate issues is stored, in its event, and no table is added. Navigate (`navigation.target_v1`) takes `page` and `target`, both required and exactly one non-null. A target's page is checked in a request context issued to the agent by the access functions and permissions that page's reads run under: a page that passes records those accesses, which join the source set, and one that fails is `tool_arguments_invalid` and adds nothing to it, departing from the read rule that a bound read's accesses always join, since joining would fail the turn `access_changed`; an unissued handle is `tool_arguments_invalid` too, keeping the input, and the turn continues; a check stopped or failing once bound ends the turn as a read does, recording the instant and accesses it ran under. A handle names its target once the step that issued it is published, so a later request of the same call can name one, and a page target opens its page as it opens at the pinned context, whatever the page's reads hold there. A Navigate step that issued a target is followed, in its transaction and under its publication recheck, by a `navigation` event carrying the step's number and the target, which the replay recheck hides as it hides text (migration 0086, whose database check also requires Navigate's operation and keeps record targets to Curves & prices and the Blotter). Pages without a read tool are not targets. The panel that follows targets, navigation history and Back stay in step 6 |
| D191 | The assistant panel reports exactly what a page shows, moves the page only for a live target it can follow, and keeps a tab's unfinished turn streaming | Refines D45, D185 and D190 ([ai-assistant.md](ai-assistant.md) §7.1, §8, §10.1, §14), decided by the owner on 2026-10-22. Looking at, and the ask that sends it, report the curve directory while Curves & prices shows no curve, the curve on its Prices and Dependencies tabs and its observations on Inputs; power hours on Power TOUs once an As of frame is chosen and TOU products before; Blotter trade rows without their grouping, totals with it, and the Blotter's risk results as the unsupported `blotter_risk`. An open trade's dialog is modal, so while Looking at names the trade at its revision nothing can be asked until it closes. The panel does not send once the page has changed since Looking at showed it. Only a navigation event that arrives live, on the stream the tab's own ask opened, moves the page, and only while the panel is open on an idle page with no dialog open, when the target keeps the knowledge and As of frame the page shows and nothing on the page is unsaved; several move in order, each with its notice and Back. Any other target, replayed after a reload, read in History, sent to a stream resumed or opened again, or held, shows its notice with Go, which first selects the target's context: working data at its frame, or its retained view selected again by id through `market/context/retained`, exactly as captured and never a new capture. A working-data target changes context when a historical view, an unapplied historical selection or another frame is selected; a retained one when its view is not the selected one or its frame differs. Unsaved means, on the page being left, edits the person made in a trade booking or amendment form or in the party, reference display, contract-date or Schedules forms, or a request whose outcome is unconfirmed; on such a page Go and Back first say so and act when asked again. Back, kept in the tab's memory for each move, including one that failed once it had changed the page, restores the page, what it showed in report form and its context; a reload forgets it, and a move from an unapplied historical selection, which Go discards, has none. A tab keeps its stream while its conversation's turn is unfinished, with the panel closed too, and a gap in the stream's sequences reads the turn again; a turn ending while the panel is closed lights the launcher and stays unread in History until the open panel shows the conversation and marks it read. Nothing polls: other tabs learn of it from History. An ask whose outcome is unconfirmed stays, through reloads and other conversations, until Retry sends it again or Discard forgets it. Consent shows the §7.1 sentence, its date the grant's end in the person's zone, the version's grant days from the moment Allow grants it; Allow grants that version and sends the question again under a new request identity. The usage line gives tokens by dimension, `unknown` where any call's count is unknown, and each currency's exact recorded cost named by its reference code, `unpriced` where a call has no price, without rounding. Answers are drawn from text nodes: paragraphs, bulleted and numbered lists, emphasis, strong, and inline and fenced code, a line over 2,000 characters staying text; links, images, tables and markup stay literal. Step lines name the tool and, for a refusal or failure, why in plain words, a withheld earlier turn has a line of its own, and no identifier is shown. The panel's width and the last explicit agent choice are browser-local preferences; a reopened conversation chooses the version its last question used while that is listed, otherwise its agent's only listed version. `assistant/agents` names each vendor. No migration |
| D192 | Run the page assistant under `--hosted-pilot`, bound to the account's sign-in session | Supersedes P37 and decides the four hosted gates D149 left open ([ai-assistant.md](ai-assistant.md) §11, §13, §14), decided by the owner on 2026-10-22. Hosted mode maps the assistant, stream and Settings › AI routes and `market/context/retained`, and runs the worker, provider client and agent pools on the installation's runtime and authenticator logins; the signed-in account takes a preview profile's place. An application session belongs to the sign-in session it was opened under, names itself to the database by that session's digest and ends no later than it, and one opened under an earlier sign-in is not used again. Migration 0087 makes the database refuse a hosted ask (`turn_input`) whose binding names another sign-in session or ends after the one its request context was issued for, and cancels a sign-in session's turns `session_ended` in the transaction that revokes it, which signing out, a password reset and disabling the account or its membership do; signing out ends its streams at once and a stream reading after a revocation elsewhere is refused `session_ended`. Providers are enabled in Settings › AI alone, by the access administrator after recording terms and attesting, with no installation-level vendor list. `host.json` requires `credentialKeyFile` and `credentialRetryKeyFile`, two private files that `tools/provision_hosted.py` writes, or its `--credential-keys` mode for an existing installation, so no person handles them; custody checks are the preview's. Earlier images refuse the two fields, so an operator adds them after the previous API's last start. Conversations are kept (O11). An agent reads its workspace's one market and one reference catalog through the tools its version switches on (O22); O28 records per-catalog choice. Model requests reach only a provider's fixed endpoint, a turn's once it is enabled and Test's once it is attested, or the loopback `OPENETRM_PROVIDER_CHECK_ENDPOINT` that owned checks set and a deployment refuses in a serving API; the host firewall allows the API container outbound TCP 443 and DNS only, and every deployment verifies that fence and the bridge it names. One serving process holds the worker generation; a deploy candidate refuses agent work while it does, and multi-process leases stay later. The API's pools hold at most 46 connections and the database allows 80. The gateway is unchanged. The owner qualifies a vendor live before anyone else gets the launcher, and O09 closes only for that vendor. D153's spending rules are unchanged |
| D193 | Combine computed panels with explicit weights and exact delivery alignment; agent preparation hands off an unpriced draft through shared operations | Refines D161, D177, D186–187 and D190–192. `weighted_panels` retains typed decimal weights on exact named edges; signed sums and normalized nonnegative means require identical complete unique delivery sets and price units. Different declared historical windows preserve their own exact sample evidence; no resampling, conversion or position matching is inferred. Guided composition supports up to 30 ingredients within 32 authoring nodes; advanced graphs use the same backend contract. Preparation bounds cumulative sample references at 50,000 before expansion and visits shared formula objects once. Migrations 0088–0089 retain normalized weights and permit read-only `read_custom_curves` / `prepare_custom_curve`; runtime set `turns_v5` adds their versioned contracts and a server-issued proposal kind pointing to a successful step in its own turn. Replay access records every transitive reference catalog, including calendars of exact older schedule versions. Current owner Review revalidates the original pin and source authority, opens an unpriced draft under the current header and protects unsaved edits. It never auto-navigates, changes the header or grants agent writes/risk execution (D260 later permits one agent write, a custom-curve draft). Ordinary Preview/Save/Publish use existing operations and human permissions. Existing declarations without panel weights retain their canonical bytes. Exact schedule references use their own certified publication/effective coverage after symbolic successors, with current calendar authority; the active schedule library remains separate. Historical resampling, unit conversion, volatility/correlation and production performance qualification remain separate work |

| D194 | Opt-in final-settlement anchors and explicit live balance blocks share interval-price resolution | Accepted for phase C slice 1. `freeze_final_settlement_v1` admits only exact final-contract-settlement observations from the declared source, observed at or after expiry and eligible under the selected market and certified knowledge cutoffs; live cells additionally require the final to be learned by valuation. Missing or ineligible finals fail; no last-mark fallback is inferred. Shaped frozen-anchor cells are modeled, never hourly actuals or qualified period fixings, and explicitly report missing live market-risk coverage. Selected live balance constituents calibrate over their complete declared product periods; collected actuals replace cells unchanged. Overlapping eligible live anchors in the same product are refused. Collected-source compatibility is extracted without weakening the existing currency, outright quote, multiplier, quantity, commodity, location, exact-reference and provider-clock restrictions. Existing live-only curves and swap fixing semantics remain unchanged. Implementation qualification is recorded separately; no PPA booking or settlement capability is implied. |
| D195 | Exact PPA reference bindings, anniversary factors, bounded meter estimates and aggregate capacity normalization | Accepted for phase C slice 2. Assets, effective versions, location assignments and quantity sources retain sealed definition and exact reference pins. Anniversary-step escalation/degradation uses explicit non-February-29 bases and exact rational powers; unsupported bases fail. Meter deadlines are explicit versioned UTC interval-end offsets of 0–129600 elapsed minutes; at or after the deadline, missing meters fail under both pending-actual policies. Earlier estimates require the selected forecast and remain estimated; certified later corrections can update delivered quantities, never live forecasts with meters. Multi-asset energy sums one whole-asset contribution per declared asset and divides once by the sum of those same assets' effective MW bases; zero contributions retain their denominator, missing contributions fail. Accreditation is a numerator over the same capacity basis. Contract profiles remain per contract MW. Forecast adjustments and stage conversions apply once. Remaining P40 wording retains explicit physical value dates, location adjustments, collected actual eligibility, current-session boundaries and later settlement/execution gates. R1–R3 remain open; this decision grants no partial-period entitlement or cross-revision binding policy. |
| D196 | Delivery-coverage valuation, whole-month capacity pricing and explicit outstanding-value measures | Accepted for phase C slice 3. Bindings select half-open native delivery coverage while retaining the instrument term, later payments and certificate holdings. Unit reuse requires exact instrument, coverage and request context; full side values are not reused solely by side ID. Capacity uses complete operating months, exact daily accredited-MW/basis sums converted under mw_day_per_operating_day_v1, with one assignment per asset/location component for the complete pricing month; partial-month entitlement and intra-month price assignment splits are refused. D94 discounting is explicit in identity. ValueExcludingOverdue sums not-yet-due PV and current vintage holding value; overdue known and estimated cash remain separately visible without PV and make CompletePv unavailable. Delivered value stays separate; estimates retain their state, and scenario-frozen excluded amounts can change in a new certified valuation context. All 12 accepted cases govern verification. D197 subsequently settles R3; no later booking or settlement implementation is inferred. |
| D197 | Exact binding revisions and bounded single-bundle PPA valuation | Accepted R3 policy, including ten acceptance cases; durable booking and bounded base-valuation admission are implemented in slice 4. Bindings retain exact instrument, definition and associated reference pins, original delivery economics, payments and holdings. All retained bindings of one trade must use its original bundle; an incompatible amendment fails atomically. Different trades in one book may retain different historical bundles, but the bounded consumer admits one explicitly selected bundle for the complete requested position set. Any incompatible binding refuses the entire valuation/risk/close operation without omission, re-pinning, inferred equivalence or successful partial totals. Definition incompatibility has primary code ppa_definition_revision; otherwise incompatible references use ppa_reference_revision, with both reasons retained when applicable. Authorized diagnostics identify the selected scope and affected pins in deterministic trade/binding order; current authorization precedes disclosure. Explicit supported subsets retain their exact scope and cannot masquerade as whole-book results. Ended delivery does not discard overdue amounts or retained holdings. Cross-revision frames, equivalence, factor alignment and aggregation remain unsupported. R1/C7 economics remain unchanged. D199 adds the guided UI; D200 adds read-only existing-close risk and persisted valuation receipts. Settlement remains outside this implementation. |
| D198 | Bound the durable PPA API without changing economic contracts | Migration 0092 stores typed exact terms and immutable revision coverage. The current consumer admits 8 memberships, 100 assets and 256 bindings; terms span at most 360 hourly or 120 quarter-hour months. Separate ceilings are 2,000,000 evaluated schedule rows across unique instrument/coverage units, 4,000,000 quantity-preparation contributions, 35,136 returned native rows over at most 12 explicit detail months, 8 MiB serialized UTF-8 response and a 20-second request deadline. Preparation counts selected native intervals per declared asset, or capacity operating-day contributions, independently of output rows. Repeated bindings may reuse computation but count separately against returned detail. The 32 KiB request-body limit remains. Inputs and current authority are checked before result disclosure; no cross-request economic cache is introduced. Eligible sealed bundles establish knowledge of exact referenced TOU versions; standalone unproven versions refuse with ppa_tou_knowledge. Unsupported whole-book consumers refuse retained PPA positions instead of omitting them. |

| D199 | Guided PPA authoring and bounded authorized inspection | Slice 5 presents the installed capability catalog over the existing booking, amendment, termination and base-valuation operations. The browser preserves exact strings and server classifications; it performs no financial arithmetic. A request explicitly selects its market bundle, certified knowledge cutoff and pending-actuals policy. Directory reads admit the complete selected book scope before returning pages of at most 64 trades. Native inspection selects one trade, binding, side and original month, returns at most 128 rows per page, and retains D198's full-operation work, deadline and response limits. Migration 0093 supplies current-known authoring choices eligible at the selected frame effective instant and the trusted historical binding projection, without changing row security or writes. Historical binding inspection is available to authorized readers, independently of private actor-owned operation recovery. The guided form requires review of current terms before submitting and preserves an uncertain operation's exact identity and payload across reloads within its actor/workspace browser session. Changed terms, context or detail selection invalidate displayed evidence. No economic policy, risk consumer or settlement operation is added. |

| D200 | Read-only existing-close book risk with durable admission and worker gates | Migration 0094 adds close_book_risk_v1 with an existing close, complete selected books, certified admission context, trading publication head, valuation-instant PPA cutoff, explicit economic policies and resolved native bumps pinned across retries. Current authority and live worker fences remain mandatory at reads and publication. Every retained binding must fit the selected exact bundle and supported valuation implementation; refusal never returns partial totals. Typed report receipts retain PPA inputs and separate known and estimated overdue balances; held certificates retain vintage-mark sensitivity. Bound admission reads immutable headers through a dedicated NOLOGIN/NOBYPASSRLS metadata-helper owner inheriting runtime policies; its metadata-only current-read exception preserves scoped/reference/additional restrictive policies and ordinary historical raw-table denial. Runtime roles cannot assume that helper role. Admission bounds PPA work at 2,000,000,000 row-scenario evaluations over unique covered units and their affected factor scenarios, and 4,096 factors; existing schedule/preparation bounds remain. Legacy claim signatures never admit the new operation and refuse generated_pilot_close_v1 occurrences containing PPA positions. Existing job/run identities remain unchanged. Existing-close execution writes its report and valuation receipts only, never market facts or knowledge certification. Generated-close cutover and settlement remain separately scoped future work. |
| D201 | Renewable existing-close ownership and atomic completion | Migration 0095 renews only close_book_risk_v1 claims using a separate scheduler connection at one third of the explicitly configured 5–120 second lease. Database time after lock waits, current service/owner/delegator authority, exact worker/login/fence, unexpired ownership, job expiry and the original overall deadline govern every renewal. Expired or replaced ownership cannot revive. Admission pins and attempts are unchanged by renewal. Preparation uses a live nonlocking claim guard; immutable CPU valuation holds no database transaction. Fresh bound publication atomically writes the validated complete report and terminal success, with the publication queue acquired before ownership rows. Unknown commit acknowledgements are reconciled against the exact committed occurrence under current authority. Success-first cancellation returns the completed outcome without changing state, including a pre-completion observed version; command-key conflicts and authorization remain enforced. Generated-close and other legacy workers retain their existing contracts. |
| D202 | Accept the bounded monthly gross fixed-strike settlement specification and implement only shared meter-finality evidence | [SP1–SP9](ppa-settlement-spec.md) are accepted only for explicitly opted-in bounded fixed-strike contracts: stable lineage/occurrence ownership, separately verified meter heads, whole-occurrence exact aggregation and two-decimal ties-to-even rounding, immutable acceptance, cumulative rounded correction deltas at original due terms, distinct controlling-principal exact-action approval with explicit expiry up to 24 hours, financial idempotency and disjoint valuation ownership. This policy acceptance does not authorize cash implementation or opt in existing bookings. [Migration 0096](meter-finality.md) implements only immutable exact-reading final/suspended/withdrawn heads, certified temporal reads, scoped verification authority without default grants and idempotent publication. Global S1, gas, cash determinations, obligations, payments and workers remain open. |
| D203 | Bounded power payment-lineage planning and monthly gross preview | The narrower power-only sequence is accepted. [Migration 0097 and the preview](settlement-preview.md) retain immutable explicit original/successor payment mappings, select one certified trade head, build its complete native contribution manifest and consume only explicitly final meter heads. Exact original MW, strikes and due terms aggregate once per canonical occurrence under accepted SP1–SP9. Authorized bounded HTTP operations return component evidence and nonbinding proposal pins; missing, withdrawn, conflicting or incompatible inputs refuse the whole result. This adds no approvals, accepted cash, claims, obligations, payments, workers or gas. Global S1/S2 remain open. |
| D204 | PPA MVP scope reset and exact draft break-even pricing | The MVP includes settlement calculations, model break-even pricing and the existing-editor graphical workbench. Settlement approvals, accepted-cash accounting, payments, reversals and automated correction adjustments are deferred; no separate approval workflow or persisted proposed/executed state is a pricing prerequisite. Contract amendments remain distinct from versioned market/meter corrections. The [bounded draft solver](ppa-break-even.md) holds every other input fixed, solves one explicit flat strike using two exact engine evaluations, and returns its scope, sensitivity, rounded quote residual and complete temporal receipt. Graphical values and aggregation remain server-owned. |
| D206 | Numeric blotter columns and managed row/column pivots share typed report measures | Quantity, prices, execution P&L and PV render numeric values with separate unit/currency columns. Grid comparison filters and sorting preserve exact decimal/rational strings. The backend supplies additive quantity/cash totals and explicit price minimum, maximum and unweighted arithmetic mean; means require `arithmetic_price_mean_to_even_18_v1`. Exact quantity/currency references and price multipliers partition cells. Saved layout version 4 retains rows, columns, measures, linked bar/line/heatmap choices and the desk's row grouping. It is the only version: a layout saved in an earlier version is not migrated, is listed as unavailable and must be saved again. Charts use the same retained financial context and backend grouped values; approximate coordinates position pixels while tooltips/export retain exact values. Charts represent the selected report scope before local grid filters. Subtotals, grand totals across axes and cell drill-through remain work |
| D207 | Swaps compose fixed/floating or floating/floating sides with explicit spreads and reviewed par entry | Extends D14 and refines D04, D97, D165, D172 and D183. Floating/floating uses two independently selected sides with opposite explicit coefficients and an explicit fixed spread on each floating side, including zero. Each side retains its exact index, fixing schedule, projection source, quote unit, delivery and payment rule. Spread is applied once to each resolved reset before schedule aggregation; floating definition v6 retains the new term while absent spreads preserve v3/v4/v5 canonical encodings. Blank fixed price requires an explicit `par_fixed_price_to_even_v1` precision/tie policy; Price solves from connected side PV and fixed-side discounting, displays the concrete rate for review, and Risk/booking use that fixed rate. Explicit zero is a fixed quote. Migration 0101 retains exact side membership, spreads, per-slot fixing evidence and generated/existing-close discount relationships, preserving renewable publication fencing and PPA receipts. Retries preserve their original outcome and recheck current source authority. Published custom projections remain explicit on either side; no index, spread, source or financial fallback is inferred |
| D219 | The Blotter is the default trading page and opens on the newest close under the rules recorded here | Decided by the owner ([blotter-desk.md](blotter-desk.md), Opening on the newest close); builds on D162 and takes D217's latest instant. Accounts that can read trades open on the Blotter, others on Curves & prices. Pages that read As of start from the authorized availability calendar (`market/dates`), read for the whole workspace in the person's display zone; its newest date (`maxDate`) holds its latest close. The uniquely latest close is selected and the As-of control shows **Latest close**. Several closes at that instant, such as a close and its corrected version, open the calendar on their date and select nothing (D162). Choosing another close replaces the badge with **Selected close**; any close at the latest instant reads **Latest close**, whoever chose it, and an earlier close of the same date does not. A reload, a switch of demo user and a return to working data start from the newest close again, and the page that is showing reads it; a session does not remember a chosen close, so the badge always says which applies. Knowledge mode is unchanged: working data at current knowledge, and a historical or live view selects no close by itself. When the calendar's latest close becomes later than it was when the close shown was selected, the control shows **Newer close available**, whoever selected that close: a button that selects the newer close, or asks when several closes share its instant. A close selected with no later close since stays **Selected close**. Availability is read again when a page opens, after a correction is saved or the display time zone changes, and when the window regains focus, never on a timer, and the close never changes without a click. The As of control and its badge float beside the shared Context panel, whose summary names the knowledge in use and whose panel holds live corrections (D217). No schema, API or permission change |
| D220 | Trading reads report positions that cannot be valued at the selected close, with structured reasons; totals exclude them and say so | Decided by the owner ([blotter-desk.md](blotter-desk.md), Positions the close cannot value). Trading workspace, Blotter and pivot reads, with the futures holdings and futures Blotter reads they share, return every position in scope. A position whose valuation at the selected close fails with exactly `source_expired` or `missing_observation` stays in the read with that code as `unavailableReason`; its mark, execution P&L and PV are null, never zero, and its booked facts are unchanged. The backend owns this list; every other failure, including authorization, context, scope and catalog consistency, still fails the read. PV, execution P&L and curve-price statistics add valued positions only and are null where none is valued; trade counts, lots, quantity and execution- and fixed-price statistics count every position, because they are booked facts. Each pivot group, futures book total and report states `unvaluedPositions`, the distinct trades it leaves out. The Blotter shows **Not valued** with the reason in plain words in a position's value cells. Every valuation total excludes such positions and says so: summary tiles, group subtotals, pivots, charts and exports. A portfolio risk run, its catalog and its queued jobs leave out each position whose valuation at the run's base (unshocked) close fails with one of those codes, the same positions and reasons the Blotter reports, and list them as `excludedPositions`, each with the book it is held in; no holding row or total includes them. A queued run leaves out exactly what its submission left out. Any other failure, including one under a shock or stress, still fails the run, and generated pilot close reports still fail whole. The desk's batched curve read follows the same rule per contract: a contract the pinned inputs cannot price with exactly one of those codes has no price and states the code as its `unavailableReason`, while the curve's other contracts keep their prices. A curve the pinned data cannot read at all states, as the curve's `unavailableReason` with no unit or points, the code its own price read raises (`missing_curve` for a rate curve, which has no contract prices), and does not fail the other curves of the request; authorization, pin, context, request-format and catalog-consistency failures still fail the whole request. The pivot conventions keep their meaning for fully valued scopes. Response fields and a retained job-frame entry only; no schema or permission change |
| D221 | Trade entry and amendment move from modal dialogs into the Blotter's side panel. Refines D191: the page report names the panel's position and mode, and the risk lens state | Decided by the owner ([blotter-desk.md](blotter-desk.md), Side panel). Amend puts the position's form in the panel's Terms tab in place of its read view, with the amendment reason and effective time as before, for the position's own binding or instrument. An earlier revision of the trade can be of another binding, which an amendment of the instrument gives a trade: it is read as it is, names its instrument, and cannot be amended from the position. The amend dialogs are gone, and so is D191's rule that nothing can be asked of the assistant while a trade is open. New trades are entered in the same panel: New trade opens it on a Future | Swap switch with neither chosen, and reads nothing until the person chooses a product, whose form then takes the panel's form part with its Price before booking, the draft valuation with the curve of each market the draft is for and its delivery period marked, drawn from the desk's own read of its curves when the desk holds the curve and from one read of it otherwise. New trade follows the backend: it is offered only where the backend's `canBook` allows booking, a historical view, a live view (which says it is live) and an account without Book Trade permission are told why in words under the toolbar, and no field opens there. A new trade starts empty. A product whose read fails leaves the panel on the Future | Swap choice with the failure said. A booked trade, or one Check status finds committed, appears selected in the grid with the panel on its position, and when a row search or column filter hides its row the table says so and offers to clear the filter; a booking whose outcome is not known is kept in the retained card and its form is not offered again, and a booking or an amendment the backend refused keeps its form and the edits in it, with the reason, to be corrected. What a person types or chooses in the form is an edit, a date chosen in a calendar or applied from the date lookup included, and every way out of the form inside the page asks first, in the panel and before the page is busy: switching to another position or mode, closing the panel or the form, replacing the form's revision, a new read of the Blotter (Save preferences included), a move to another page (the sidebar, Open curve, New trade for a PPA), another As of date, another context, user or live selection, signing out, another market example, and the header's time controls, opened or already open and changed. A close that is chosen is applied only once the person agrees: the As of select, which every read and command takes its close from, stays on the close the Blotter shows while the question is open, and so do the calendar and the offer of a newer close. Keeping the form puts back what the person had changed, the time controls' inputs exactly and the example select, and over a route of the profile dialog says what was not done. A reload, closing the tab and going back are asked about by the browser while the panel holds the form of an amendment or of a new trade with edits, and the assistant's own moves ask through its Go anyway, after which the forms are given up with the page. A chart's image export that fails reports the error and leaves the Blotter as it is, unless the failure is a denial of access, which clears it as it does for every read. When an amendment is sent or reconciled the panel follows the trade by its id to the binding the amendment produced, which its receipt names, on its Terms, and says so when that is another binding, or that the trade is not in the read at it. A live correction (D188) reads an open panel again unless it holds edits, which it leaves alone, and clears a risk run with the note "Risk results cleared · live corrections changed the inputs"; the note stays if the read then fails. The risk report is the panel's Risk report mode, whose setup's choices and sizes are edits like a form's: a correction, and a settlement record, read it again at the new inputs, so that a run is always made at the inputs on screen, carrying a setup's choices over and naming what the new catalog no longer offers; no live view can book, so no new-trade form is held while corrections arrive. Amend is offered to an account that may amend, and the panel says why when it is not; for a revision that cannot be amended it says why once the editor has been read, and before that Amend opens the form read-only. Every retained trade request, a booking or an amendment of a future or a swap, is shown in a card that names the request, and the trade of an amendment, with its Check status and Retry: in the panel, on whatever tab or form it shows, and on the page while the panel is closed. A swap's read-only Terms show its valuation and schedules. Looking at and the ask's page report name the trade the panel shows, at the revision the Blotter holds or, while its form is open, the one the form shows, or the new trade it holds, by its product, or by none while no product is chosen (`blotter_new_trade`, `blotter_new_future`, `blotter_new_swap`: three more ids of the assistant's closed set of page reports, none of which the assistant reads); a target for a trade is refused in a pivot, the view the Blotter has applied, in words that name the Individual trades view, and a target for another revision than the Blotter holds says which way it differs. History lists every revision with its instrument and facts, each price in the unit of its own contract (the position's contract in the position's unit, whether or not the editor's catalog holds it) and each quantity in the unit of its own index, said to be unavailable when the catalog does not hold it. The page report states the risk lens as D222 records. Presentation and page-report change only; the assistant's closed set of page reports gains three ids for a new trade, and there is no schema or permission change |
| D222 | Risk reports come back inline: run bar, Values/Risk lens, risk tile and panel detail. Shock sizes are explicit per price unit | Decided by the owner ([blotter-desk.md](blotter-desk.md), Risk report inline). A run's results are shown in the desk and no longer in the side panel, whose Risk report mode keeps the setup, Run now, Queue as job, the retained job history and the work each part's calculation did. A run bar above the tiles states what ran: the report and its size for each price unit, which held prices or curves it does not shock, how many factors it shocked, the close, the knowledge it was read at and when it ran, or how its queued jobs stand; Edit shocks opens the Risk report mode on the run's choices, Export downloads every part's report read again under current authority, and Clear takes the run off the desk. The trade rows get a Values \| Risk lens, which turns to Risk when a run's results first arrive: Risk shows each position's shock, size and unit, its up and down changes with small diverging bars on one scale per currency, and its delta in its unit. A factor moves a position when its up or down change is not zero; a position several factors move shows **n shocks**, and its panel's Risk tab, which says its results come from the risk run on the desk and points to the Risk report, lists each factor with its base, up and down values, changes, delta and gamma, each with its exact amount. Nothing is added across factors, parts or positions: the risk tile shows the largest move in each currency, picked by exact comparison of magnitudes, which position and factor it is, the number of factors, and each position's own largest move as a small bar; the long and short chart becomes **Change if each contract moves up**, a bar for each position one factor moves at its up change, naming the positions several factors move and those none moves. Missing results are never facts: while a part of the run has no results, as it failed or was refused, its submission has no known outcome or the job history no longer lists it, a position no shock with results moves is **Not known**, with the part and why, in the lens, the chart and the tile, never not moved, the chart and the tile say "No known move" when no shock with results moves a position, the tile calls its largest move the largest known one, the run bar and the tile state how many shocks are known, of how many the run ran when that is stated, and a part the job history no longer lists is said to be missing from it. The tile and a position's Risk tab state a run whose results are refused, with why, and one no part of which has results, and the tile says when a run moves no position. The run bar, the tile, the chart and the lens's book headers say what the run leaves out (D220), by the run's own list and the book it names for each position; a row the run leaves out says so with its own reason. Pivot views have no lens: the run bar points to Export, and to the trade rows and a position's Risk tab, which show a run made on trade rows. Results are drawn against the positions only for the read they were run at: the same market context, the same close, by its time and by the frame each part's report names, as every close of one date in a historical view shares the view's context, and the same books and portfolios, which every queued job states; anything else, or a run that does not state its books, is made elsewhere, stated by the run bar with its captured coordinates and exported. A queued run is shown once none of its parts is under way, and its positions, valued and left out, must be those of the read. Every new read of the Blotter clears a run, each read at current knowledge being made at a context of its own: a change of close, context or book scope with the note "Risk results cleared · the close or books changed", any other new read (Refresh, another Display or pivot, the Blotter opened again) with "Risk results cleared · the Blotter was read again", and a change of account with none, whether or not the run has results; a live correction and a settlement record keep their own notes (D221). The parts of a queued run made in parts state the run with each part: the job's retained frame holds the run's identity, the part and the part count, and every queued job's frame the books and portfolios its run was asked for, as chosen; the job store admits each part once, every part of a run at one retained context and one close, for one scope, with one part count, serializing a run's parts sent at once, and every job read states its job's run, or none, its scope and how many shocks it asked for; the job history lists a run as one entry, also after a reload. The page report names the lens: trade rows under the Risk lens are `blotter_risk_lens`, one more id of the assistant's closed set of page reports, which the assistant does not read; the panel's risk report, a new trade and a position it shows are reported first. Shock sizes are entered per price unit and none is built in (blotter-desk.md, Risk report inline). A run for one position alone is not built: the portfolio risk request, its catalog and retained jobs scope a run by books only. Migration 0113 adds an index, a trigger and the run, scope and shock count of each job read, with no table change; the closed set gains one page id; no permission change |
| D223 | Desk money totals add present value and execution P&L across quantity units within one exact currency and instrument type; measures, instrument types and currencies are never added together; quantity measures stay partitioned by quantity specification (D98) | Decided by the owner ([blotter-desk.md](blotter-desk.md), Desk layout, Summary tiles: one row per currency inside a tile, never added across currencies); refines D98 and D206 for money measures. A pivot that also partitions by quantity specification never states one Swap PV or one futures execution P&L for a currency held in several quantity units, and the browser may not add amounts. A second pivot convention, `trading_blotter_money_totals_v1`, partitions by the view's dimensions, the exact instrument type and the exact currency reference only, not by quantity specification or price multiplier, and still by definition revision when a dimension names a market or contract; under either convention such a group states its `definitionRevisionId`, so two groups of one market are never indistinguishable. It accepts only `presentValue`, `executionPnl` and `tradeCount`; quantity, lots and every price statistic are refused with `blotter_view`. A group states `instrumentType`, `currency`, `currencyReference`, its dimension fields, its measures and `unvaluedPositions`, and no unit, quantity specification or price unit. PV and execution P&L add the valued positions of the group as exact money of its one currency (D220) and are null, never zero, where none is valued or the measure does not apply to the instrument type; `tradeCount` counts every distinct trade of the group. `trading_blotter_totals_v1` is unchanged and keeps D98's partitions. `trading/blotter`, the workspace view and each of its summaries accept either convention, with the summary limits unchanged. Response contract only; no schema or permission change |
| D231 | Prices over time use KLineChart; cross-sections by delivery period stay on ECharts | Decided by the owner (2026-10-26), who asked for it; the design is [markets-desk.md](markets-desk.md). `klinecharts` 10.0.3 (Apache-2.0, no runtime dependencies) draws a contract's price history on Curves & prices and interval prices on Delivery prices through one module, `time-chart.ts`. Forward curves and custom-curve previews are cross-sections by delivery period, not time, and keep the shared ECharts component, which preserves gaps. A point without a price is a gap: no line, bridge or zero is drawn across it, and an isolated priced point is a dot. Hover shows the exact backend decimal string. Colors, fonts and grid come from the CSS tokens. Drawing tools (horizontal line, trend line, ray, text note) are unsaved presentation state and the toolbar says so |
| D232 | Chart overlays are computed by the server from a versioned catalog under an exact rational policy | Decided in the markets desk design ([markets-desk.md](markets-desk.md)), under the owner's delegation (2026-10-26). `GET analytics/overlays` lists the catalog: `simple_moving_average_v1`, `exponential_moving_average_v1` (the recurrence uses the previously reported rounded value and is seeded by the rounded simple mean of the first window; it restarts its seed after a gap) and `rolling_range_v1` (highest and lowest). The window is required, 2 to 250; a request carries at most 4 overlays; a missing, out-of-range or unknown parameter is an `overlay_parameter` refusal, never a default. Arithmetic is exact signed rational, rounded half to even at 18 decimal places under `exact_rational_half_even_18_v1`, which each response names with the definition version. A window holding a missing point has no value and the reason `window_incomplete`; nothing is bridged. The series holds at most 35,136 points (one year of half-hour intervals) in plain ASCII decimals at ordered instants, else an `overlay_input` refusal. The history read states the series charted as `chart`, one point per instant under D237; samples at one instant that differ make it a conflict, returned with status 200 and no `overlays`, and the browser draws only `chart`. Bollinger bands and any overlay needing a square root wait for an explicit irrational-number policy |
| D233 | Curve summaries come from the backend; the front contract is the first priced contract in delivery order | Decided in the markets desk design ([markets-desk.md](markets-desk.md)), under the owner's delegation (2026-10-26). `market/curve-shapes` and `evaluate` return the contract count, the front contract and price (rates: maturities, first maturity, first zero rate, day count) so the browser neither counts points nor reads the first one. The front is the first contract in delivery order that the selected close prices. A curve with none has `front` null and an availability reason: `no_remaining_contracts` when the close leaves no contract to price, `no_priced_contract` when contracts remain but none has a price. The Curves & prices board reads the summaries in batches of 64 at the selected close and states why a card cannot be priced; the close is never substituted (D162) |
| D234 | Delivery prices opens on a backend-chosen selection and its statistics are backend fields | Decided in the markets desk design ([markets-desk.md](markets-desk.md)), under the owner's delegation (2026-10-26). The catalog returns `opening:{sourceId,fromDate,throughDate,policy:'newest_available_delivery_day_v1'}`: the source with the newest available delivery day, ties broken by source name then identity. The page opens on it with a **Newest delivery day** badge, which becomes **Selected range** once a filter, date or cutoff changes; a catalog without an opening says there are no delivery prices yet. The browser chooses no source or day. The period read adds `summary` (interval count and missing intervals, low and high with instants, time-weighted average over published intervals, latest knowledge instant used) under the D232 rational policy, and `chart`, one point per expected interval of its operating days with the reason `not_published_at_cutoff` for an unpublished interval. The catalog adds a `collection` summary. Overlays on the period read run over those points; a period holding rows outside its expected intervals is refused as `power_period_interval` rather than charted |
| D235 | Custom curves asks before discarding unsaved edits | Decided in the markets desk design ([markets-desk.md](markets-desk.md)), under the owner's delegation (2026-10-26). The question uses the Blotter's leave path and the `beforeunload` guard, and asks only on moves that discard the edits: another page, a changed historical context or return to working data, an account switch or sign-out, another example, and in the page another library entry, New curve, a template or Reload library. A change of close keeps the graph (D162) and asks only when an effective-version timeline is unpublished. Saving preferences and following live never ask. A request with an unconfirmed outcome is retained across a move and is not an unsaved edit. Preview and Save wait, naming what is missing, while a contract connection lacks its contract, output position or weight |
| D236 | Delivery-price collection publishes only changed intervals | Refines D133 and D134 under D238 (migration 0118). A successful attempt must still cover every interval of its operating days, so completeness is checked per attempt and the published union at any cutoff is complete. Its batch holds only the intervals whose end, numeric price or provider revision differ from the latest recorded batch for that interval; an attempt with no such interval succeeds and publishes no batch. A changed provider revision with an equal price is a new revision. Numeric equality decides sameness, and the first recorded text is kept. Each attempt's changes are one batch with one receipt, so no reader sees part of an attempt |
| D237 | History reads resolve versions under an explicit revision policy | Decided by the owner (2026-10-26). Refines D172 for history reads only; valuation, snapshots and recorded closes keep exact pins. A history request must name `exact_snapshot_pins_v1` (each frame's exact pins, as before) or `as_known_at_cutoff_v1`; a missing or unknown policy is a `history_revision_policy` refusal, never a default. The cutoff policy follows each pin's correction chain, with the certified-context resolver, to the latest revision recorded at or before the read's bitemporal knowledge cutoff: the selected context's own instant, which the response echoes as `knowledgeCutoff` (`knowledgeBasis` `selected_context`), or current knowledge for working data (`knowledgeCutoff` null, `knowledgeBasis` `current_knowledge`). A revision recorded after the cutoff is invisible, and a request body never supplies a different cutoff. Samples at one instant that are then identical collapse to one (D238); different ones are a `history_conflict` refusal naming the instant. Each point carries its observation identities and `knownAt`, the latest certified publication instant among them (null while uncertified). Superseded versions stay reachable in Price sources |
| D238 | Every write is idempotent; a changed value is a new revision with its own knowledge time | Decided by the owner (2026-10-26). Recording an existing fact (same business key, value and effective time) returns the existing record instead of adding a row, for imports, scrapes, collection jobs, closes and retried commands; retried commands carry a request id and receive their first outcome. A different value for the same business key is a new revision, never an overwrite, and reads resolve revisions by knowledge time under an explicit policy, never by row or insertion order. Recorded in AGENTS.md so every database operation follows it |
| D245 | Each agent version sets its time per read | Decided by the owner (2026-10-27). Replaces `ToolRead` (D184) with `readSeconds`, 1 to 30 seconds and not above the time per turn (migration 0120); a read past it ends the turn `limit_tool_read`, enforced by the API and by the database |
| D246 | The Blotter lists a PPA booked under other reference or definition versions than the selected close as not valued; whole-book consumers still refuse | Decided by the owner (2026-10-27). Refines D220 for the Blotter only. Trading workspace, Blotter and pivot reads list each PPA trade with a binding booked under other definition or reference versions than the selected market data, with `unavailableReason` `other_definition_versions`, null value fields and its booked facts stated under its own versions; valuation totals leave it out and count it in `unvaluedPositions`. Close risk, portfolio risk and generated pilot closes still refuse the whole scope with `pilot_close_inputs`. Response values only; no schema or permission change |
| D247 | Answers draw pipe tables as tables of text cells | Decided by the owner (2026-10-27). Refines D191 (§10.1). A header row, a separator row and rows of the header's width become a table built from text nodes, with emphasis and code in cells; any other pipe text stays literal. HTML, links and images stay literal text and URLs are not linked. Front end only |
| D249 | Each agent version sets its history window | Decided by the owner (2026-10-27). A turn sends at most its version's latest `historyTurns` earlier turns, 0 to 1,000, enforced by the API and the database, and records those it sends; withholding applies within the window. Versions saved earlier take 1,000 (migration 0121), which a conversation already longer refuses. The input-token limit still applies |
| D250 | The page assistant reads PPA bookings through Read trades; PPA bookings is not a Navigate page | Decided under the owner's delegation (2026-10-27). Refines D184, D190 and D191. An inspected PPA is reported as Read trades' trade view of it (`trades.trade_v1`, instrument type `ppa`); a listed book as the new `ppa_book` view of exactly one book (`trades.ppa_book_v1`), the page's own directory read of the whole book at the turn's pin, rechecking the book's scope, refused `result_too_large` above 64 trades, listing the page's columns. Both capture no context, run under the Blotter's accesses and permissions and bind the pinned context, cutoff and frame, not the page's own market inputs and cutoff, which Looking at says. PPA bookings and the targets its reads issue are not opened by Navigate (`tool_arguments_invalid`), since the database's navigation targets do not list it. No migration, runtime-set or permission change |
| D251 | Saved Blotter views can be listed in the sidebar; PPA bookings has no sidebar entry | Decided by the owner (2026-10-28). Saved views are layout version 5, which adds `sidebar` (true or false), set by Show in the sidebar when a view is saved; earlier layouts are not migrated. The sidebar lists a person's flagged views under the Blotter and opens one as choosing it in the Blotter does. PPA bookings stays a Trading page opened from New trade; a person who wants a PPA list saves a Blotter view of PPAs and lists it. No migration or permission change |
| D252 | The Blotter lists a PPA the selected market data cannot price as not valued; one such PPA no longer refuses the whole read | Decided under the owner's delegation (2026-10-28). Refines D246. When the PPAs of a Blotter read cannot be priced together because the market data has no price for part of some delivery (`interval_block_coverage`, `interval_curve_coverage`, `interval_actual_missing`, or a frame without a needed projection), the trades concerned are listed whole as not valued with `unpriced_delivery` and the rest are priced together as before. The owner amendment of 2026-10-29 also lists missing or unsupported retained schedules as `needs_rebooking`, keeping affected complete totals unavailable. Corrupt content and authority failures still refuse the read. An interval no period block covers is now `interval_block_coverage`, apart from overlapping blocks (`interval_block_partition`). Whole-book consumers keep refusing. No migration or permission change |
| D253 | One side model: a Side is its parameters for the whole term, generates a schedule of rows, and reads row values from the calculation graph | Decided by the owner (2026-10-28); local implementation trial in progress, qualification pending. No side is made of other sides; `FixedSide` and `FloatingSide` price payments for swaps and PPAs alike, with PPA-only price sides removed; physical delivery sides stay their own family. `Side.GenerateSchedule()` builds one `ScheduleRow` per period or interval for the whole term, holding graph references, not values; a shock to the graph changes exactly the rows that read it. `Side.PV` sums its evaluated rows and `Instrument.PV` sums coefficient × side PV. Amended the same day: every `SidedInstrument` follows the model (futures stay leaf instruments, D110), and a position's detail opens under its Blotter row, showing each side's evaluated schedule. Amended by D258: rows are its logical rows, and PV and the Blotter sum named roots and contributions only. Refines D04 and D84 |
| D256 | A turn's pinned As of frame may hold up to 256 KiB | Decided by the owner (2026-10-27). Migration 0122 raises `assistant.pin_turn`'s bound on a frame's pin JSON from 64 KiB to 256 KiB (262,144 bytes); nothing else changes. It applies over 0072's definition or one already identical to its own, as the hosted pilot's has been since its hotfix. |
| D257 | A period-contract price node returns the selected settlement index's published fixing once due, and a side may override its curve's settlement index | Decided by the owner (2026-10-28); local implementation trial in progress, qualification pending. For date-only period-contract observations (swaps and PPA period curves; interval curves are unchanged), each `FloatingSide` index price term (D253) states its settlement selection: the curve's declared single-index target, or a different named override index, never defaulted. The price node returns the bound projection before the selected index's fixing instant and that index's published final fixing, selected under the market cutoff, at or after it; between expiry and the fixing instant it is refused as today. Node dependencies follow the resolved context, so a due node depends only on its fixing. An override projects from the curve's forward marks as the recorded no-basis choice `forward_curve_no_basis_v1`, labelled in receipts and risk. The swap spread becomes the price term's adder. Refines D165 and D253; consistent with D32 and D37 |
| D258 | Persist each booked side's complete schedule and load it for valuation | Decided by the owner (2026-10-28); local implementation trial in progress, qualification pending. Refines D253 and D195. Booking retains every logical row of a side's pricing (observations, applications, averages, quantity steps, amounts, discount and valuation steps) with explicit roles and named output roots, ordered named edges and typed exact-version bindings. The tables are immutable, published and readable only under `temporal_read`, and valuation finds a schedule through the trade revision visible at its cutoff. Rows define how values are obtained; evaluated values are valuation results. Large schedules stage unpublished and publish in one short fenced, idempotent transaction (D238). Obligation rows keep occurrence keys and digests across amendments of later periods. Normalization becomes a shared specification on the side, and cross-side inputs are typed ports. Valuation refuses rather than regenerates; drafts are transient. Amended for the local trial: stock PostgreSQL retains native calculations as compact vector operations with exact grids and cell identities, rather than separate arithmetic records per interval; scalar dated operations share the contract. Physical layout follows measurement |
| D259 | The backend names each curve's kind (`curve_kinds_v1`); Curve builder and Curves & prices show it as a labelled icon | Decided 2026-10-29; its icons and colours approved by the owner, and `interest_rate` added under the owner's delegation. Kinds let a person tell market, relationship and synthetic curves apart at a glance; a colour is never the only signal, since each kind also has an icon shape and a text label, and dark-scheme colours follow when the app defines a dark theme. A custom curve's kind follows its output step: `weighted_blend` from `weighted_panels`/`weighted_contracts`; `rolling_average` for `mean` over `dates` on a `prior_calendar`/`prior_business` sequence; `balance_of_month` for `mean` over `contracts` on `balance_month` dates; `prompt_contract` for a single relative `positions` selection; anything else, a pass-through included, is `custom`, never a guess. A market curve's kind follows its rule: `market_settlement` or `trader_mark` by direct role (the schema requires one of the two for a direct curve), `basis_spread` for `Add`, `scaled` for `ScaleAdd`; a stored interest-rate curve is `interest_rate`. The kind is derived at read time from the exact definition, never stored, and returned with saved curves, sources, published sources and the navigation directory with `kindsVersion`. The browser draws only what it is sent and refuses an unknown code or version. No migration or permission change |
| D260 | The page assistant may save a custom-curve draft for the person; this supersedes D193's "never grants agent writes" for that one tool | Decided 2026-10-29; [details](#d260--the-page-assistant-saves-custom-curve-drafts). Migration 0128 lays the database groundwork: `save_custom_curve_draft` needs `ReadMarketData` and `WriteMarketDefinitions`, the agent writes under its own `DraftCustomCurves` with its delegator's current `WriteMarketDefinitions` and only the curve version it claimed for a step of its turn, each draft generation records who saved it (a person, or the assistant with its turn and step), `security.custom_curve_agent_writable` limits the assistant to a new curve or a draft whose latest generation it saved in the same conversation, and a `draft` navigation target opens a successful save step of its own turn. The tool ships in runtime set `turns_v9` and records its successful step in the draft's own transaction; Curve builder shows **Saved by assistant** from the saver. The assistant never publishes, changes activations or overwrites a person's draft |
| D261 | A reference display edit is saved and applied in one reviewed step that needs both `WriteReferenceData` and `SealReferenceData` | Decided 2026-10-30. `POST /reference/commit` runs `security.save_reference_display_edit` then `security.apply_reference_display` in one transaction under one operation id; the route holds `WriteReferenceData` and the apply function enforces `SealReferenceData`, so a person holding only one of them cannot edit and any refusal (denied, invalid, stale `expectedVersion`, conflicting retry) leaves no unapplied draft. An exact retry replays both first outcomes (D238); the same id with different values is `reference_conflict`. Confirming the listed changed fields before committing replaces the separate apply review; `reference/save` and `reference/apply` are removed. No seeded role holds both permissions; an administrator assigns both roles to people who edit labels. History lists one item's applied display versions (a futures product's include its contracts' label edits), newest first, with the time, the workspace member who authored the edit and its changed fields, read by `security.reference_display_history` (migration 0130), the only SQL change: the display overlay scoped to the original exact reference version is retained, and financial reference rows are still never rewritten. Display editing already covers every category with a name or label column; `calendar_days.description` stays read-only because those rows have no code key. `GET /reference/futures-product` returns the same `referenceDisplay` as the category read |

## Proposed implementation choices

These are working proposals, not additional user requirements. Resolve them
through the evidence and acceptance criteria in the plan.

| ID | Proposal | Rationale / validation |
| --- | --- | --- |
| P01 | Modular monolith; browser/application/database tiers | Business modules stay shared. D137 adds supervised scheduler/controller processes and Swarm workload execution; process separation enforces the privileged execution boundary |
| P03 | Relational recipe/edge tables and compiled dependency DAG | Shared computation without a graph database |
| P04 | Finite differences and full repricing as the generic risk path | Supports black-box pricers; analytic paths are optional validated accelerators |
| P05 | Within-run reuse first | Demonstrate identity and dependency completeness before cross-run caches |
| P06 | Committed revision ordering behind the historical date selector | Resolve user dates to certified knowledge prefixes; avoid unsupported physical commit-time claims |
| P07 | Trusted typed operators before general scripting | Bounded extension surface with explicit dependencies and versions |
| P08 | One authorized operation catalog for UI, API and MCP | Prevent business-rule and permission drift across clients |
| P09 | Common interfaces and abstract base classes for instrument/side behavior | Require PV/schedule behavior; share ordinary aggregation and delegate models where appropriate |
| P10 | Typed lifecycle commands and immutable business events over versioned trade state | Domain classes decide behavior; the application authorizes and commits transitions atomically |
| P11 | Versioned local TOU rules with explicit market clocks, compiled to UTC intervals | Distinguish fixed offsets, regional DST rules, source conventions and user display zones; retain schedule provenance |
| P12 | Immutable coherent reference-revision bundles, frozen after draft insertion | Initial storage implementation has exact-revision FKs and tested freeze/write locks; freeze is not activation or historical publication |
| P13 | Internally distinguish typed observations, reusable value definitions and curve membership | Storage proposal supports the agreed Curve/MarketContract/MarketObservation domain; exact decomposition remains under review |
| P14 | Select snapshot inputs by explicit requirement and role/use class | First proposed subset permits one coherent variable selection per factor, plus separately pinned frozen facts; reject incompatible views |
| P15 | First implemented exact arithmetic mode uses 18 fractional digits and magnitude below 10^24 | Checked integer arithmetic rejects precision loss/overflow; broader numerical policies remain O01 |
| P16 | First managed security evaluator uses exact scoped role grants and intersected agent delegation | Deny by default; managed current-state inputs and SQL transaction fencing are implemented; verified authentication/API/admin integration remains |
| P17 | Bind trusted database identity through private transaction capabilities | Isolated authenticator/runtime roles; exact session login/PID/transaction binding, no caller-set identity variable |
| P18 | Serialize current security changes against admitted database requests | Coarse epoch fence; actual-current expiry checks after admission waits; qualify throughput before broader use |
| P19 | Implement the used day-count and compounding rules in a small project-owned module | Explicit Actual/365 Fixed, simple compounding and UTC valuation-date basis; exact rational intermediates and named ties-to-even output policy; no external financial-library dependency |
| P20 | Store currently supported rate convention identifiers on the interest specification | Separate day-count/compounding catalogs have no independent consumer; no default or broad catalog import |
| P21 | Compose the mixed-period example from separate daily-only and monthly-only source curves | Use an explicit selection/splice operator retaining original dated identities and lineage; keep named BALMO windows distinct from directly quoted period contracts; see examples/curve-composition-rollover.md |
| P22 | Implement a schedule-backed relative-contract resolver before a broad date language | Reuse dated futures-series/milestone references; agents may maintain validated rows, with a bounded optional generator materializing the same input contract; see schedules-and-contract-sequences.md |
| P23 | Use a small typed, compiled temporal kernel for date sets, ordered occurrences and interval selections | Compose calendars and roll/TOU rules explicitly; measure compact arrays/bitsets and shared immutable payloads before choosing storage/cache breadth or claiming speed |
| P24 | Seal page-assistant credentials under preview key files and call only fixed vendor endpoints | [ai-assistant.md](ai-assistant.md) §5.2: AES-GCM under fresh nonces and two generated key files, a write-only API, redaction before SQL, and a key kept out of browser storage, URLs and pending-command records; Replace key clears the attestation and disables the provider (§9). Clients use no proxy or redirect, never keep error bodies (§6.1), and only a loopback override reaches the test fake |
| P25 | Pin effective-dated token prices when each model call opens | ai-assistant.md §5.3: exact 18-digit `token_cost_v1` costs with `token_cost_display_v1` and `token_cost_report_v1`; totals per exact currency reference (D72). A withdrawal appends like a correction and leaves calls starting in its interval unpriced. Unpriced, unknown and unrecognized usage store no cost and never show zero |
| P26 | Add four assistant permissions and an agent reader role held only by agent principals | ai-assistant.md §5.4: `ManageModelProviders`, `ManageAgents`, `ReadModelUsage` and `UseAgents`. Approved 2026-10-21: the role amends D42 to seven role templates. Users & access refuses agent principals and the role; hosted provisioning grants an explicit list of human roles |
| P27 | Use three protocol adapters with explicit parameters and sourced usage mappings | ai-assistant.md §6: Messages, Responses and `generateContent` for five vendors. Every parameter whose vendor default could change behavior or billing is sent unless a supported model refuses it, and a withheld parameter's reported value, where the response reports one, must be the standard one; identifiers must not be repointed, and enabling needs the exact usage mapping version its adapter applies, confirmed by a reviewed migration with its date and sources, published terms that an administrator records as sourced and dated versions, and an attestation bound to the current credential and terms versions; Replace key and new terms each disable the provider until it is attested and enabled again. A model's capability version records its facts, each sourced and dated, and the usage mapping version it was validated under, which states the reasoning efforts its adapter sends; Enable needs a model whose current version was validated under the applied mapping, on an enabled, qualified provider, and an edit leaves the model's state alone. Conformance runs against a fake provider and is not qualification (O09) |
| P28 | Derive grants from the agent version and recheck authority at publication and replay | ai-assistant.md §7.1: one current grant per user and agent, coverage by provider, model identity and permissions, pre-checked exclusive admission, an agent request context per tool read, checks before every model call, and content events appended under the epoch share lock |
| P29 | Pin each turn's context at ask and reserve retained contexts for pages | ai-assistant.md §7.2: one working-data capture after admission, current-only definitions labeled with their read instant and a stored page report. Each working-data ask permanently uses a retained context (O07) |
| P30 | Read through a fixed tool set over operations extracted from the page handlers | ai-assistant.md §7.3 and §12 step 2: pages and tools share one implementation and authorization path. Results are bounded and never truncated or paged; refusals return as step results; the server issues every navigation target |
| P31 | Run turns in one interim in-process worker per database | ai-assistant.md §7.4: advisory-lock generation, fencing tokens, serialized admission, a stuck-turn backstop and an interrupted state. Milestone 4 durable runs and phase 3 leases replace it |
| P32 | Declare per-turn limits and a versioned runtime set | ai-assistant.md §7.5: input is checked against reported usage, never estimated; only transport and overload retry, before publication and with known input; nothing is truncated. Administrator pause, single-turn cancellation and run inspection (architecture §9 Operations; ai-assistant.md §2) are deferred |
| P33 | Stream turn events with final events and cookie plus Sec-Fetch admission | ai-assistant.md §8: one stream per unfinished turn, answer text pushed in memory once its publication recheck commits and every other event read with the replay recheck (D184), a single-line JSON wire format and `session_ended` on assistant routes |
| P34 | Keep answer text plain and cards free of agent-supplied numbers and text | ai-assistant.md §9–§10: answers render no tables, links or images; card specifications bind this turn's reads, with targets issued and stored at creation; provider tests run under a deadline and a per-test token |
| P35 | Govern agent notes like earlier turns | ai-assistant.md §11: a note keeps its source set and pinned context; reading it adds its sources to the reading turn; it reaches a model only after the replay and source-access checks pass and the turn's grant lists every permission its sources used, and a person only through the replay recheck; text a user typed is shared only after its author confirms it |
| P36 | Hold page-assistant state in exact-version, immutable or append-only tables | ai-assistant.md §5.1: prices and the display name belong to the model identity, so a rename never leaves a model unpriced; agent versions pin an exact model version and turns an exact agent version; grants reuse the existing delegation tables with a grant audit |
| P37 | Superseded by D192: keep phase 1 inert under `--hosted-pilot` while its migrations ship | ai-assistant.md §11: no assistant routes, worker, provider client or agent pool when hosted, checked by `tools/check_hosted_http.py`; hosted provisioning grants an explicit list of human roles. The first hosted release after phase 1 waits for operator baseline qualification (D135) |
| P38 | Deliver the assistant in verified steps | ai-assistant.md §12–§13: each step leaves the application working with no provider configured; every structured error code and every "never" rule gets a check; independent adversarial review precedes each step's publication |
| P39 | Add publication-driven OTC fixings and trade-owned cash settlement through explicit versioned contracts | [Settlement design](settlement-plan.md): preserve clock-triggered v1 replay; qualify power/gas fixing publication and retain accepted fixing/obligation/payment events under current book/source authority. The collected-interval consumer D171 is a narrower implementation; publisher eligibility, cash and approval conventions remain explicit gates. The [bounded monthly gross specification](ppa-settlement-spec.md) supplies proposed SP1–SP9 wording and independent executable cases; D202 accepts those conventions only for the explicitly opted-in bounded fixed-strike contract; no shared settlement phase is declared complete |
| P40 | Deliver linear PPAs through audited interval series, shaped interval curves and reusable physical/financial sides | [PPA design](ppa-design.md): physical sides value commodity received or delivered at the location's effective-dated valuation curve; financial sides price the contractual payment; rows stay gross. `PpaInstrument` has open side memberships and a 1 MW contract-capacity unit; a virtual PPA has no physical side. Shaping calibrates stored additive or multiplicative shapes to a non-overlapping block partition. Pending actuals require an explicit fail or estimate policy; a virtual PPA may declare one net payment group of its own sides. Option-like terms are rejected. Phase C conventions and bounded consumers were subsequently accepted and implemented under D194–D201; the current catalog admits five explicit compositions, not arbitrary memberships. Net grouping and settlement remain proposals following P39 |
| P41 | Refines D215 and D220: when settlement starts, a swap's Blotter row changes only `presentValue` (the remaining value), `mark` (none, and so no mark minus price), `settlement` and `lifecycle`. Its fixed price (the common one for a scheduled swap), delivery (floating dates for a single-period swap, the envelope for a scheduled one), market key, label and curves, side-2 quote multiplier, quantity, commodity and direction stay as the Projection row stated them | D220 keeps a position's booked facts whatever the close values; a lifecycle changes what values a position, not what was booked, and nulls would drop fixed-price statistics and move a single-period swap's delivery and market grouping when settlement starts. `--scheduled-swap-check` compares each swap's row before and after its first determination. Awaiting the owner's confirmation |
| P42 | Move operational limits out of code into explicit, versioned configuration | [operational-limits.md](operational-limits.md): each limit keeps a fixed ceiling in code and the database, and its operating value moves to the agent version, installation settings (revisioned, under `ManageAgents` or `ManageAccess`) or hosted configuration, by owner. No value is filled in by code; a missing or out-of-range value is refused. Every operation records the revision or configuration digest it applied. Contract constants, calculation limits and pre-authorization guards stay fixed. Proposed 2026-10-27 after D251; awaiting the owner's review |
| P43 | Execute D253's booked schedules directly with dynamic inputs under one resolved context | [side-plans.md](side-plans.md): answers D253's row-node question for D258's retained schedules. Rows are retained relationally but are not individual calculation-graph nodes. The owner's 2026-10-29 amendment supersedes compilation per calculation: booking retains the complete executable schedule, and valuation directly reads its price, quantity and discount inputs under one context with explicit per-role cutoffs. Independent authorized source reads run concurrently in bounded batches; source selection and evaluated row states vary with context while the stored instructions do not. Scenarios recompute affected terms and report no per-node amount. No cross-calculation value cache. Accepted for the local implementation trial on 2026-10-28, amended 2026-10-29; direct-execution implementation and qualification remain pending |

## Clarifications that must survive implementation

- Applying one shock is separate from sharing one valuation. Different contracts
  consume the same shocked market even when they need different computations.
- Do not shock individual trades. Shock factors, rebuild dependencies and run
  the affected pricers.
- A factor can be a dependency even when the measured delta is zero.
- All instruments are unit-level, independently of market linearity. Future
  options at different strikes are different definitions; repeated references
  to one instrument reuse its unit value and apply their external quantities.
- A power/gas relationship must be an explicit recipe with units and delivery
  mappings. Correlation alone is not an instruction to propagate a pricing shock.
- A graph database is unnecessary; dependency ordering and reverse indexes are
  still required.
- Computation reuse cannot widen data access or replace current authorization.
- Dates, source availability, effective time and application revisions are
  different coordinates.
- Side schedule generation and valuation are behaviors, not only fields on a
  generic record. Inheritance enforces contracts; composition combines economics.
- Sibling visibility uses declared stable references and the instrument's pinned
  context. It is not unrestricted access to other records or recursive total PV.
- Combining physical and financial schedules preserves event identities, typed
  delivery metadata and explicit quantity links; dates alone are not join keys.
- Portfolio/book/trade are business objects. Instrument quantities and per-trade
  state belong directly to Trade; persistence rows are not additional business
  objects by default.
- Historical context selects effective business state and knowledge independently.
  Market observation/delivery dates and installation knowledge are different
  coordinates. Current authorization still governs historical access.
- Temporal joins include book/trade membership and market/reference bindings.
  Retained immutable definitions alone do not make those relationships historical.

## Choices to resolve before the relevant implementation

| ID | Choice | Required evidence / decision point |
| --- | --- | --- |
| O01 | Working decimal representation, precision and overflow limits | Multiplication/division, units, cancellation and deterministic parallel reduction; representative benchmarks; decide in core milestone |
| O02 | Initial quantity bases and normalized schedule rules | Common quantity/output basis, versioned per-side transformations, total versus rate quantities, one sign convention and payment timing; decide before bookings |
| O03 | Primitive factor schema, quote units and alias rolls | Dated gas and power examples with pillar mappings and no double shocks; decide in scenario milestone |
| O04 | First identity integration and bootstrap flow | Human/agent lifecycle, delegation expiry/revocation/redelegation, MFA/federation integration and credential custody; decide before an exposed application. The page assistant creates agent principals only through `ManageAgents` (D152); users delegate to them through standing, revocable grants with a maximum duration (D154, P28); redelegation, MFA and federation stay open |
| O05 | Company/workspace/book/portfolio scope relationships | Capability matrix, delegation/elevation and current security classification after historical book transfers; decide before persistent business operations |
| O06 | Approval thresholds and separation-of-duties defaults | Action classes, exact payloads, authority revalidation and atomic consumption; decide before enabling policy-controlled writes |
| O07 | Date-to-knowledge resolution implementation | Mandatory baseline date selector; qualify publication/commit semantics, valid interval storage under timestamp ties/regressions, consistent cross-scope cuts, dependency closure, lag, recovery and retention before exposure; also reclaim the retained contexts that assistant asks consume (P29) |
| O08 | Historical engine retention and upgrade compatibility | Replay fixtures across a schema/model upgrade; decide before claiming exact historical replay |
| O09 | Initial model providers and supported capabilities | Common adapter conformance plus task outcomes; product operations remain provider independent. P27 proposes three protocol adapters for five vendors; conformance against a fake provider does not close this item, which stays open until real-provider task outcomes are recorded |
| O10 | Performance deployment envelope | The page target is less than one second (D142). Qualify supported data volume, network conditions, cold/warm behavior, concurrent clients and calculation workloads before broader performance acceptance. Page-assistant phase 1 performance acceptance waits on it (ai-assistant.md §13) |
| O11 | Audit, market-data and conversation retention | Access, redaction, deletion/export obligations and retained replay needs; decide before real data. The hosted page assistant keeps conversations until an installation needs a retention policy (D192) |
| O12 | Advanced P&L explain ordering and cross terms | Reconcile controlled multi-factor movements with full revaluation; later risk milestone |
| O13 | Option-side contribution and exercise-control accounting | Distinguish extra payoff from control of sibling obligations; choose/test adjustment or joint allocation, option coefficient and controlled quantity fraction without double counting; before option support |
| O14 | Initial physical-side valuation conventions | Define delivery value, payments, quantity sources and commodity-specific location/clock schemas; before physical-product support |
| O15 | Supported lifecycle transitions and state dimensions | Define booking corrections, economic termination, due events, realized fixings and outstanding obligations; before enabling the associated command |
| O16 | Lifecycle occurrence identity and correction rules | Separate occurrence/action/attempt/effect IDs; define corrections, replay compatibility, current dispatch authority and duplicate/stale-job tests; before automatic lifecycle processing |
| O17 | Historical-context UI defaults | Visible date/time boundaries and time zone, simple versus advanced coordinate controls, coverage and lag labels; before historical UI acceptance |
| O18 | Exact supported temporal upper bound | Confirm the proposed reserved 9999-12-31T00:00:00Z sentinel across database, runtime and API; reject real values at/beyond it |
| O19 | First supported TOU and market-clock conventions | Choose product/feed-specific clocks, operating-day boundaries, holiday/DST policies, partial-interval allocation, weighted resolution mappings, rule retention and mixed-clock scope; qualify before automatic TOU or rate schedules |
| O20 | First supported futures valuation and settlement convention | Reconcile execution basis, settled/unsettled variation, remaining value/risk and closeout; resolve before futures PV or settlement; bounded fixed-total execution-P&L booking is separately defined by D73 |
| O21 | Multiple measurement bases for one commodity | Initial schema has one canonical dimension; add explicit volume/energy bases and state-dependent conversion inputs before supporting mixed-basis products |
| O22 | Reference verification, activation and entitlements | Complete source evidence, official series date checks, calendar coverage, scoped content approval and current use/display/distribution grants before real data activation |
| O23 | Reference capture numeric bound compatibility | Initial SQL rejects scale >18, magnitude >=1e24 and nonfinite values without rounding; qualify runtime/API compatibility and needed precision before real imports; this is separate from O01 working arithmetic |
| O24 | Market identity, value types and reference extensions | Review market-model.md; specify commodity measurement bases, quote/cash precision, per-day lots, locations/index components and typed market/static operand FKs before migration |
| O25 | Supported input/history/settlement policy inventory | Qualify role-specific selection, correction identity, variable/frozen inputs, sampling, publication and finality rules; reject unsupported combinations before activation |
| O26 | Remaining-day window and schedule qualification | Expiry-based current-contract advancement is agreed in D56. Choose current delivery month versus month containing next delivery day for daily/BALMO views; qualify clock/package boundaries, sequence coverage, listing eligibility and bounded generator parameters |
| O27 | Re-pinning an interval series' clock to a successor reference revision across tzdb releases | A series on an IANA-zone clock can be revised only while the installed tzdb release equals its pinned one (D127, D172); after a tzdata update its reads stay available and its revisions are refused. Required evidence: an identical clock contract, retained revision history and an audited re-pin; decide before phase B consumers depend on long-lived series |
| O28 | Per-catalog agent data scope | Tools read the workspace’s primary catalogs and explicitly declared dependency catalogs under current source authority (D192–D193), including authorized foreign-reference calendars. There is no per-agent catalog selector. Decide whether a future installation requires an additional per-version catalog restriction before enabling that policy |

Open choices are not reasons to stop independent implementation work. Choose
reversible details using documented evidence. Ask for product-policy decisions
when the alternatives change the user's intended behavior.

## Recording changes

When a decision changes, record the reason, affected contracts and compatibility
impact here. Update the architecture and plan to describe the resulting design.
Preserve the meaning of already-retained calculation artifacts; a new model or
numerical policy does not silently reinterpret an old result.

- **D42 amended by P26 (approved 2026-10-21):** an agent principal needs a role
  holding exactly the reads its tools make, and no person may hold it. Migration 0066,
  the page assistant's agent administration, adds the agent reader role as a seventh
  role template, alongside P26's `ManageAgents` and `UseAgents`. The six human
  templates and every existing assignment are unchanged: a trigger on role grants keeps
  the role to agent principals and agent principals to it, Users & access grants no one
  the role, and hosted provisioning grants its owner the six human roles by name.

- **D183 extends D161/D182 (2026-10-21):** saved managed graph outputs now have a
  typed persisted consumer in monthly indexed swap projections and the shared
  portfolio/worker path. Explicit published version binding preserves existing
  graph history; it does not infer activation by name. New schema baselines need
  operator review, and no hosted migration or worker deployment is implied.

- **D186 supersedes the active execution paths in D161/D181/D183 (2026-10-21):**
  the SQL panel executor and legacy futures-risk HTTP fallback are removed in favor
  of the connected graph and sole portfolio operation. Earlier decision and
  verification entries remain historical records. This is an explicit schema and
  API cutover: 0080 refuses saved recipes until an operator has reset/rebooked them,
  and 0081 refuses any retained risk jobs, including completed ones, pending an
  explicit operator archive/reset. Neither migration deletes data or silently
  transforms immutable financial history. The UI now exposes
  exact weighted mappings, compatible published monthly projections and typed
  quote/output scenario targets. Integer-kernel benchmark results are unchanged;
  they do not qualify this end-to-end financial path.

- **D192 supersedes P37 (2026-10-22):** the page assistant runs under `--hosted-pilot`.
  Migration 0087 binds hosted turns to their sign-in session and cancels them when it
  is revoked. `host.json` gains two required fields, so an installation's operator adds
  its credential key files and those fields, raises the database's connection limit to
  80 and installs the egress firewall rule in the schema window that applies 0087
  ([hosted-pilot.md](hosted-pilot.md)). The preview's provider check endpoint variable is
  renamed `OPENETRM_PROVIDER_CHECK_ENDPOINT`.

- **D221 refines D191 (2026-10-24):** a trade is open in the Blotter's side panel, not in a
  modal dialog, so the assistant is asked while it is open. Looking at names the trade at its
  revision, the ask's page report is unchanged in form for a trade, a new trade being entered is
  reported by its product and read by no one, and an amendment or a new trade under way holds a
  live move away from the page as any other unsaved edit does. D191's sentence that nothing can
  be asked until the trade's dialog closes no longer holds; its other rules stand.

- **D219 takes D217's latest instant and floating context (2026-10-25):** both decisions open
  browsing on the latest close, so one mechanism now chooses it: the workspace's availability
  calendar, whose uniquely latest close is selected with the **Latest close** badge, and whose
  several closes at that instant open the calendar and select nothing (D162). A date's earlier
  close is no longer the latest close, a later close of the same date is a newer one, and the
  badge's basis is the latest instant rather than the newest date. Returning to working data
  opens it on that close, as D217 does, and the page reads it; applying a historical view still
  selects no close by itself (D219), as no badge states a default there. The As of control and
  its badge, the knowledge in use and live corrections float with D217's Context panel instead of
  sitting in the header (refining D76 and the header placement D162 describes); panels that slide
  over the page keep the floating band clear, and the Assistant launcher steps aside from a docked
  side panel. Presentation only; no schema, API or permission change.

## D205 — Native indexed physical payment preview

The bounded monthly gross indexed-payment contract explicitly binds the payment
side's index. Resolve native prices at the certified cutoff, apply its multiplier
and adder, multiply by each interval's contractual quantity, aggregate exactly and
round once per canonical occurrence. Reuse complete lineage, original segment
terms/due dates, final meter evidence and exact normalization. Collected prices
remain available source observations, not contractually final publications.
Missing or incompatible evidence refuses the whole preview. This adds no cash
acceptance, obligations, payment workflow or automated correction adjustment.

## D208 — Preserve original Side price applications alongside shared observation terms

`FloatingObservationPlan` serves both Side generation and booking resolution for
the six existing averaging modes. A payment retains each target interval,
observation ordinal, mapping rule and exact weight before shared observations are
resolved. The same payment evaluator values those applications and derives its
observation subtotals; the inspector does not calculate a second schedule.

Migration 0102 retains exact calendar and positive quantity authoring with
versioned reference relationships, complete identity comparisons and shared
per-Side admission. Existing economic identity versions and the two earlier command
signatures retain their meaning. General pricing windows, zero-weight means,
recurring booking and paired observations require separate qualified versions.

## D209 — Dependent selectors use applicable data and explicit date availability

Futures entry narrows exact listed contracts by commodity, exchange, product and
delivery date. Swap entry narrows indices by commodity/search and links quantity
choices to the selected quote specification. A unique applicable reference may
populate its control; ambiguous financial choices remain explicit. Parent changes
clear incompatible selections and prior results. Existing execution eligibility,
custom projections and all six observation modes retain their backend contracts.

Delivery-price calendars consume certified operating-day availability at one
resolved knowledge cutoff (migration 0103), with current source/reference authority
rechecked after certification. End dates stop at coverage gaps and the existing
read-window bound. Configured sources without published prices contribute no
choices. Swap delivery calendars use the existing managed observation/fixing rules
within the supported single-month booking scope. Unavailable days are disabled;
retained contractual dates remain visible separately from selectable alternatives.

Instant labels, including retained risk-job coordinates, use the profile time
zone. Contractual delivery and operating dates retain their declared market/date
semantics. The existing managed market-close calendar remains authoritative for
As of selection; lightweight navigation and paginated reference data are not
filtered by incompletely loaded client observations. Quantity conversion and power
TOU controls cascade only through their supplied applicable catalogs.

## D210 — Explicit swap entry templates and side-scoped choices

Entry follows Trade, Instrument, then Sides. Choosing a fixed/floating or
floating/floating instrument card applies the managed
`monthly_18_calendar_days_v1` starting template visibly: pay first / receive
second, zero floating spreads, monthly payments 18 calendar days after the last
delivery day, and no adjustment. This initializes only empty controls; switching
structure preserves user edits and never applies defaults to a retained trade.
The starting template does not select commodity, index, dates, quantity or
discounting. After a commodity choice, D209 may populate a unique applicable
index; ambiguous choices remain explicit.
Booking receives concrete side terms through the existing operations; missing
terms never acquire a backend fallback from the entry template.

Each floating side selects a commodity before its index, projection and compatible
unit choices. The fixed side has no commodity selector. Changing the reference
floating commodity clears the fixed price and incompatible quantity selection;
changing either commodity clears a nonzero spread and observation configuration.
The current swap model still requires compatible units and a shared commodity;
independent filters do not implement cross-commodity valuation.

New Trade also offers PPA and routes to the existing authorized PPA workflow,
where the user explicitly selects market inputs before entering a booking.

## D211 — Additional model-provider mappings

The page assistant adds OpenAI, xAI and Meta Responses mappings and Google's
streamGenerateContent mapping through the existing provider-neutral boundary.
[Provider adapters](provider-adapters.md) specifies fixed destinations, explicit
parameters, immutable per-vendor usage normalization and source confirmations.
Migration 0104 adds no prices, credentials, account attestation, model capability
versions or enablement. Meta's documented Responses usage now permits its own
confirmed mapping, replacing the earlier unconfirmed status under D150. Synthetic
conformance does not close real-provider qualification O09 or authorize a hosted
schema/application deployment.

## D212 — Shared interval data, masks and exact column arithmetic

Keep the audited interval-value store independent of TOUs, schedules and trades.
Reuse the shared UTC grid and bitmask foundation for TOU selection, alignment and
overlap. Request-owned compact C# columns hold factors, offsets, forecasts and
meter quantities with exact wide-value fallback and separate retained lineage.
Explicit kernels provide masked sums, products, dot products and aligned column
cross-products; they do not infer units, fill gaps, normalize weights or advance
knowledge. Current consumers are block shaping and PPA quantity preparation.
Hourly pricing is a calculated view under Curves & prices, and both interval
screens share a reusable table/plot widget with optional explicit refresh. Swap
Side payment schedules also consume that widget, retaining exact table values
and separate formatted displays alongside per-payment cash/PV plots.
[Interval data](interval-data.md) records contracts, measured workloads and limits.

### Swap entry and private intake reconciliation

The financial swap ticket explicitly labels its retained one-month notional as a
total applied independently to each side's delivery period. Price observation
weights do not repeat that quantity. Managed review rows apply each payment's
explicit allocation, membership direction and external quantity once. Normalized
unit calculations and exact lineage remain inspectable separately.

Independent fixed delivery dates do not depend on floating price coverage and are
never copied from another side. Fixed-price entry explicitly chooses agreed or
par pricing; calculation precision belongs only to the par calculation. These
presentation changes preserve existing stored definitions and trade economics.

Correction-following contexts select eligible final index observations; a later
preliminary correction cannot displace a final. Exact saved snapshot pins and
ordinary risk inputs remain frozen under the existing observation contract.

Private managed reference capture/import commands are included as a bounded
consumer of existing inactive intake storage. Source documents, credentials,
activation and certified publication remain separate. Current execution policies,
index-derived fixing schedules, connected valuations and durable result pins are
retained; no parallel fixing store or earlier migration sequence is introduced.

## D213 — Explicit model alias approval

Allow a model capability version to declare `fixed` or `alias` explicitly. This
supersedes P27's blanket non-repointing requirement for an administrator-approved
alias. The existing sourced identifier fields describe the selected policy. An
alias is permission to request that vendor name as it evolves, not evidence of
fixed model weights or permission to change provider, capability facts or prices.

Both policies accept only the requested identifier and the exact served identifiers
listed in that capability version. Unknown served identifiers fail before output;
no wildcard, prefix match or automatic discovery changes the allowed set. Calls
retain their requested identity, exact capability and price versions and the
identifier actually reported by the provider. A reported alias cannot establish an
underlying snapshot. Normal current-authority and account-attestation checks apply.

Migration 0105 labels existing versions `fixed`; its compatibility default and the
original command preserve the old fixed-only contract and exact retained retries.
New UI submissions require a choice; the extended command rejects null or unknown
policies. Existing versions stay immutable, and policy edits create a version so
running agents retain the existing `model_changed` boundary. This local change
does not authorize publishing, a hosted schema update or real-provider calls.

## D214 — Explicit financial swap quantity schedules

Agreed scope extends the fixing-capable monthly swap through explicit multi-month
quantities and recurring payments, calendar-day rates and declared total-contract
allocation, then MW integrated over selected delivery intervals. Physical gas and
power obligations are planned separately and do not gate financial booking.

Implement a new scheduled instrument/side version, preserving the original
one-period definitions and events. Reuse primitive monthly price/fixing/payment
plans; each SideSchedule owns the normalized quantity allocations used by PV.
Normalize sides independently and retain cross-side size as an exact rational
membership term. Apply the external first-side total once. Keep authored quantity
meaning and interval/clock evidence in immutable trade-revision records, separate
from reusable normalized pricing.

The initial supported relationship explicitly multiplies period-average price by
period quantity. It does not infer price weights from delivery power or allocate
a total from a unit/curve. Monthly payment records and side ranges remain
independent. Retained intervals and allocations support replay without current
clock regeneration; current authority and certified cutoffs still apply.

Migration 0106 adds exact typed relationships and explicit version boundaries.
Existing booked PV is not rewritten. Scheduled amendments stop before any retained
fixing/payment is due; due-payment processing, interval-price settlement and
implicit legacy upgrades remain unsupported. See [quantity contracts and numerical
checks](financial-swap-quantities.md). Standalone physical delivery remains later scope.

## D216 — Qualified swap authoring, intra-trade allocations and contractual P&L changes

Extend qualified monthly financial-swap authoring through the shared observation
capability catalog and explicit published projection references. Bulk edits name
the source, selected target periods and selected term groups before applying;
delivery dates and unselected terms remain independent. Observation quantities
are authored for their exact planned intervals, never inferred from delivery.

The bounded netting contract requires one trade, identical exact legal parties,
currency revision, contractual precision/lifecycle policies and contribution due
instant. Every gross allocation is explicit and direction/balance constrained.
A zero-net discharge is a retained noncash settlement record. Its gross
allocations reverse exactly; it creates no cash receipt or trading profit.

Under `closing_context_contract_changes_v1`, contractual changes use the selected
closing market and certified context. New-trade economics are its closing
remaining value plus its contractual cash. Future undetermined amendments are
revised minus prior terms valued in that same context. Continuing-contract
movement is evaluated independently using the prior terms; no residual is labeled
market P&L. Book transfers, settled-period rewrites and ambiguous continuity remain
unsupported. D215's gross rounding, finality and correction policies are retained.
Future-amendment qualification examines every intervening revision, including
new-trade amendments and unchanged endpoint round trips. A changed period must
start after the amendment's effective instant (UTC midnight for date-only terms;
retained first delivery interval for clocked power), and must not already have a
determination. Exact currency and quantity basis remain unchanged through the
whole chain. Prior/revised definition bundles resolve independently under closing
authority. Capability documents record the implemented gates and limits.

## D217 — Explicit temporal browsing defaults and shared floating context

User-authorized usability change, implemented locally. Initial market and PPA
browsing selects the uniquely latest offered market instant. Equal-time exact
closes remain an explicit choice; an existing selected close is never replaced
because another close becomes available. Working reads retain current knowledge;
the floating context distinguishes working, unapplied and retained selections.

Changing an effective date requests `end_of_day` with the user's display time
zone. The managed resolver selects the latest occurrence of that local day's
final microsecond and caps it at certified current knowledge. Missing time zones,
nonexistent local times and uncertified historical cutoffs remain structured
errors. The resolved cutoff is editable and historical selection still requires
Apply. Market close dates, delivery windows and effective dates remain distinct.

Market/reference/trading screens share the existing context operation in a
floating panel. Interval data, interval prices, delivery prices and PPA use the
same panel presentation over their native explicit cutoff/window contracts;
showing a panel does not enable unsupported historical-session operations.
Interval data initially reads a trailing 24-hour window ending at the newest
non-withdrawn interval of its selected series under its certified cutoff.
Delivery browsing selects the source with the latest available delivery date and
reads that one market day. Source and date choices remain visible and editable;
empty catalogs/series return an explicit no-records state. Metadata availability
and historical cutoff validation remain authorized backend operations.

The Assistant launcher floats at the lower right. Hiding it is a remembered
per-profile browser presentation preference, reversible in Profile. It changes
neither agent authorization nor retained conversations.

## D218 — Generic trade bindings and typed instrument PV

Implement the existing Trade → Instrument/Quantity architecture directly in the
managed model. Trade owns stable occurrence bindings with external signed typed
quantities; futures-specific execution facts are separate immutable values and
contain no second booked quantity. Existing storage identifiers, public booking
contracts and economic identity versions remain unchanged.

Portfolio and risk evaluation call `Instrument.PV(UnitValuationContext)` and
validate its typed money/quantity result and exact originating context. Instrument
contracts own product-specific binding validation, workload estimates and
lifecycle valuation dispatch. The scenario engine does not branch on concrete
instrument types. Linear booked-size scaling is an explicit capability.

A managed cash-settled `ForwardContract` uses canonical long indexed-minus-fixed
unit economics and existing independent sides. Signed trade quantity expresses
direction. Complete equivalent definitions can be interned; shared sides can
reuse valuation only under the same scenario, dependency fence and resolved
discount provider. New persisted forward booking and futures settlement PV remain
outside this increment. See [implementation contracts](instrument-contracts.md).


## D224 — Typed curve evaluation and explicit saved lookup policies

Implement `CurveBase<TRequest,TValue>` with asset-price and discount-factor
families. Keep exact contract identity, delivery containment and point dates
separate. Select lookup method, date axis, span, boundaries and implementation
explicitly. Linear price interpolation uses exact integer day weights in the
existing connected graph so scenario risk follows the actual endpoints.

Extend custom recipes with a typed panel/date `lookup` operator and explicit
daily delivery output semantics. Persist its policies on the exact recipe
version; retain native and published parent graph dependencies. Existing daily
custom-projection bindings provide the booking and risk consumer. Do not create
market contracts for date queries. Existing saved graph canonical evidence is
preserved when no new behavior is declared.

Delegate payment discounting to `RatesCurve` using the existing rate construction
and log-discount policies. Keep interpolation selection on the existing currency
mapping until a separate persisted-contract migration is supported. Broader rate
construction and rate scenario coordinates remain unimplemented. See
[curve evaluation](curve-evaluation.md) for executable coverage and limits.


## D226 — Supported reference demos and retirement of disconnected catalogs

Remove the unused cash-rounding reference table and its viewer category with an
atomic migration that refuses retained rows, stale snapshots and unexpected
schema dependencies. Preserve the executable explicit settlement-rounding policy.

Populate supported rate reference data through a separate, explicitly pinned
synthetic market bundle. Per-category reference browsing can inspect that bundle
without replacing existing valuation references. Supplemental reads, booking,
retained terms, discount mapping and risk must use the selected exact references;
frontend commodity/calendar/clock choices remain scoped to that market.
Single-period entry and draft pricing support these exact supplemental references
as scheduled entry does; supported sampling determines new-entry eligibility.

Inbound FK counts alone do not classify a table as unused. Preserve child tables
with validated outbound FKs and named runtime consumers. See
[reference coverage](reference-data.md#reference-coverage-and-retirement).

## D227 — Typed contractual quantity factors

An explicitly selected `factor_scheduled_financial_swap_v1` composes two floating
scheduled sides in distinct commodities and one exact currency. Its positive
`contractual_quantity_factor_v1` has exact numerator and denominator quantity
references and pins `consistent_exact_unit_ratios_v1`. First-side quantity entry
uses the existing supported schedule bases; `factor_of_first_side_v1` derives
the second side without a second authored amount or allocation.

The managed compiler converts each first-side quantity to the denominator,
multiplies the contractual scalar, rebases to the numerator and converts to the
second quote's native quantity. Conversion paths, commodity, measurement basis
and dimension come from the immutable database reference version. Calculations
retain exact rational values through final cash rounding. Aligned delivery
periods and equal normalized shares enforce the factor in every period. Each
side retains its independent observation and payment terms. Market shocks never
change contractual sizing. Factor units/value and implementation versions enter
economic and settlement identity; equivalent unit expressions retain distinct
selected contracts. Prior swap versions and their identities remain unchanged.

Supplemental swap markets resolve their reference revision from the selected
sealed model's exact foreign key, under the same certified knowledge context and
current authorization. Reference caching requires an identical catalog/revision
pair and rechecks authority. Publishing a compatible unit/reference version and
an explicitly configured consuming model is data/configuration work. New
conversion algorithms or schedule semantics still require a versioned release;
missing paths or ambiguous financial behavior raise structured errors.

See [factor contracts and qualification](factor-swaps.md).


## D228 — Booked terms and explicit change review

An unavailable editor choice identifies the value saved on the trade and explains
that its absence from the selected context does not change the booked terms.
Amendment previews compare the authorized saved revision with validated proposed
terms on the backend. The ticket shows booked and proposed values, the revision
and the required amendment action. A saved custom projection is a trade term in
the present storage contract; replacing it uses the same amendment workflow.
No generic acceptance can bypass that operation. New input invalidates the review.

Display metadata, market observations, valuation configuration and contractual
changes have separate lifecycles. This increment introduces no economic-index
publication, cross-version migration or valuation-configuration acceptance
operation. A future publication workflow must classify changes, qualify their
effective/knowledge coordinates and show affected trades before offering an
authorized action. Earlier trade revisions and calculation contexts remain intact.

## D229 — Shared PPA portfolios and dimensional heat-rate sides

Admit PPA bindings into the common blotter and connected portfolio-risk report,
using their independent interval sides and the exact admitted interval market.
Select `estimate_until_actual_v1`: estimate pending quantities until the meter
deadline, then require actuals. Retain this policy and certified interval read
contexts with queued runs. Label the monetary measure “Value excluding overdue
cash” and retain complete PV, overdue known and overdue estimated companions;
do not present it as an ordinary outstanding PV. Whole-scope exact input-bundle
compatibility remains required. Worker reads require the exact live bound attempt
and current service and represented-user authority.

Use the D227 `factor_scheduled_financial_swap_v1` contract for heat-rate demo
trades. Its typed quantity factor and qualified unit conversions derive the
second floating side's schedule. Both sides retain independent price and payment
terms and share the existing managed PV and settlement operations. Present these
bindings as heat rates in the common blotter and risk evidence. Do not introduce
a second quantity-conversion contract.

The standard isolated preview seeds 37 synthetic bindings, including eight
heat-rate and five PPA bindings. See [demo instruments](demo-instruments.md) for
coverage, numerical evidence and remaining limits.

## D230 — The delta ladder shows the server's factor totals as returned

After a risk run, the desk's **Ladder** lens shows the report's per-factor
totals: one row for each shocked factor, grouped by its curve, and one column for
each book, valuation measure and currency the run returns. A cell is the server's
delta, up P&L or down P&L for that factor, book and measure, displayed through the
backend amount formatter. The ladder never sums rows, books, measures or
currencies, converts units, or shows a missing total as zero: rows are separate
shocks and can repeat one exposure at two levels of the curve graph (an input
quote and a computed point built from it). A factor returned twice, or two totals
for one column, refuses the ladder. A row opens a Contract panel listing the
positions its shock moves, from the same report. Totals across books, a choice of
one curve level and quantity-equivalent units need server-side contracts and
remain work. See [the delta ladder](delta-ladder.md).

## D239 — The page assistant reads the person's own jobs on Jobs & Infrastructure

Decided by the owner on 2026-10-26 ([devops-assistant.md](devops-assistant.md)).
Jobs & Infrastructure becomes a page the assistant reads. Read jobs returns only
jobs owned by the signed-in person, as the delegator of the turn's grant: their
terms, state, version, editability and runs, audit actions and command receipts.
That holds for a job manager or installation operator too; another person's job
reads like an unknown one (`job_not_found`). Jobs are current operational
records: the read binds no market context, is labeled with its read instant and
is refused under a historical view (`jobs_current_only`). The database holds no
container output, and this decision adds none. Runtime set `turns_v6` adds
`jobs.list_v1` and `jobs.history_v1`; migration 0119 implements them.

## D240 — Agents stay read-only; job changes are prepared and confirmed by the person

Decided by the owner on 2026-10-26. The agent may prepare a create, update,
pause, resume or cancel. The change takes effect only when the person confirms it
on the page, through the ordinary job operation under their own sign-in. The
job's owner is the person and it carries no delegation. Executing a change after
an in-turn confirmation under a delegated `RunJobs` was considered and rejected.
Not yet implemented.

## D241 — ReadJobs

Decided by the owner on 2026-10-26; amends D42 and ai-assistant.md §5.4. A new
permission, `ReadJobs`, is held by the six human role templates that hold
`RunJobs` and by the agent reader role. The agent reader role then holds exactly
`ReadJobs`, `ReadMarketData`, `ReadTrades` and `ReadTradeParties`. Agents are
never granted `RunJobs`, `ManageJobs`, `LaunchWorkloads` or `ManageInfrastructure`.
Job reads record the access `job_access` under `ReadJobs` in no catalog. A job's
owner and scope never change, which a trigger refuses for any ordinary update
(a superuser can still disable triggers), so the permission in the
workspace is all the source-access and replay rechecks need.

## D242 — Proposal provenance and single use

Decided by the owner on 2026-10-26. An executed proposal records an exact foreign
key to its preparation step and whether the person confirmed it unchanged in
`automation.audit`. A proposal is used at most once, keyed to the request identity
that used it, and can be reviewed for one hour after its step. Not yet implemented.

## D243 — The changes an agent may prepare

Decided by the owner on 2026-10-26. Create proposals cover `worker_check` and
`swarm_job`. Update, pause, resume and cancel cover the person's own jobs. An
agent never prepares changes to collection schedules or infrastructure, run again
or the resolution of an uncertain launch. Not yet implemented.

## D244 — Prepared job changes state every term

Decided by the owner on 2026-10-26. A preparation names every term explicitly.
Nothing is defaulted or inferred, and a missing or conflicting term is a
structured refusal. Not yet implemented.

## D245 — Each agent version sets its time per read

Each agent version states a time per read (`readSeconds`), set in Settings › AI with its
other limits: 1 to 30 seconds and never more than its time per turn. The database
enforces both bounds; a save without the field is refused. A tool read runs until it
returns, its time per read passes, or its turn's deadline, whichever comes first. A read
stopped at its time per read ends the turn `limit_tool_read`, a code of its own; one
stopped at the deadline ends it `limit_elapsed`; any other failed read stays
`tool_failed`. Each stopped read records its step with the instant and accesses it read
under and is logged once as a warning naming the bound, its tool and its operation,
never its input. A read that fails once its bound has fired counts as stopped by it. The
database holds the same bound: an agent read's transaction sets `statement_timeout` and
`idle_in_transaction_session_timeout` to the time per read before it binds, so a read
whose API can no longer cancel it ends its transaction, and releases the epoch row,
within twice its time per read. The agent read pools' command timeout is 30 seconds,
the hard maximum, so no single command of a read is cut off sooner; the other pools keep
5 seconds.
Migration 0120 adds the field. Versions saved before it ran under runtime sets whose
`ToolRead` bounded every read at 5 seconds, so each takes 5 seconds, or its time per
turn when that is less. The runtime set becomes `turns_v7`, which every new turn records.
This replaces the runtime set's `ToolRead` that D184 describes.

On the hosted pilot, Blotter and trade reads of the page assistant exceeded 5 seconds:
the page's own Blotter read took about 2.7 seconds and one swap's editor read about 3.1
seconds, before the agent's read adds its own session and amount display. Every such
read was cancelled and reported only as a generic failure, which the API did not log.
A read cannot simply be bounded by its turn alone: it holds the security epoch row
`FOR SHARE` until its transaction ends, and access-control changes (grant, revoke,
Retire, role changes) take that row `FOR UPDATE`. The hard maximum bounds how long one
read can hold such a change back. A change sent while a read holds the row can still
fail on its own 5-second command timeout and must be sent again. The epoch is one row
for the whole system and `FOR SHARE` holds do not conflict with each other, so
overlapping reads in any workspace could keep it held long enough to starve such a
change; a longer time per read makes that more likely. This has not been measured.
Faster reads, and reads that do not hold the epoch row throughout, remain separate
work.

## D246 — Other-version PPAs in the Blotter

A PPA trade is booked under exact definition and reference versions, which every
binding of its revision names. Market data selected at a close uses one pair of
versions. Since PPAs joined the Blotter, a book holding one PPA booked under other
versions refused the whole read with `pilot_close_inputs`. On the hosted pilot,
eleven synthetic PPAs installed with their own reference catalog made every Blotter
read that included their book fail.

The Blotter is a report, so it follows D220 instead. A PPA trade any of whose
bindings names other versions than the selected market data is listed whole, so no
total holds part of a trade. Each of its positions has `unavailableReason`
`other_definition_versions`, shown as **Not valued** with "Booked under other
reference data versions". Its present value, complete PV and overdue balances are
null, never zero. Its booked facts are those it was booked with: trade, binding,
revision, book, parties, delivery, coverage and contract MW, with its quantity
specification and currency named under its own reference revision and its own
definition revision. They are never translated into the selected versions, so a pivot
keeps them in their own groups. No curves are read for such a PPA, so each is its own
market, `ppa_instrument:<instrument id>`, labelled "PPA · " and its instrument
reference. A reference catalog the reader cannot access refuses the read with
`access_denied`, as before. A booked reference revision the close's context does not
see, because it is not in force at the close's date or not yet known by its cutoff,
refuses the read with `pilot_close_inputs` and the reason
`reference_not_visible_at_close`, naming the trade's bindings, once access has been
checked again; no check covers that case yet. PPAs booked under the selected versions are valued as before, in the same
read.
Valuation totals leave the listed trades out and count them in `unvaluedPositions`;
trade counts and quantities count them as booked facts.

Whole-book consumers keep their contract. Close risk, a portfolio risk run and a
generated pilot close still refuse a scope holding such a PPA with
`pilot_close_inputs` and the affected bindings, so the Blotter's Risk report over that
scope is refused. Leaving such PPAs out of risk runs, as D220 does for futures and
swaps, is not implemented. Valuing a PPA under its own versions beside positions
valued under others is not implemented either.

## D247 — Tables in answers

Agent instructions ask for small comparison tables, but the panel drew only paragraphs,
lists and code, so a table showed as lines of pipes. The panel now draws a pipe table
with a header row, a separator row of dashes for each column and rows of the header's
width as a table. Each cell holds text nodes, with the same emphasis and code spans as
a paragraph. Pipe text in any other shape stays literal. Nothing else changes: HTML,
links and images stay text, URLs are not linked, and charts remain cards (§10.2), which
are not implemented.

## D249 — Each agent version sets its history window

A follow-up sent every earlier turn of its conversation (§7.6), so a long conversation
eventually crossed its version's input limit and failed `limit_input_tokens`, and could
only start over. Each agent version now states a history window (`historyTurns`), set
in Settings › AI with its other limits: 0 to 1,000 earlier turns. The API refuses a save
without it and the database enforces both bounds.

At ask, the database considers only the conversation's latest `historyTurns` earlier
turns by ordinal. Of those, each is included or withheld as before: withheld when its
owner can no longer read a source it used or the covering grant no longer lists a
permission those sources needed, and named by a step. The turn records the included
turns (`included_turns`) once, with its other ask facts, so a repeated ask answers with
the same record. A turn before the window is neither sent nor reported withheld. An
included turn's sources still join the new turn's source set and every recheck,
together with the sources of the turns it included, which may lie before the window. A
window of 0 sends no earlier turn.

The window bounds history by turns, not tokens. The version's input-token limit still
bounds what each turn sends, checked against reported usage as before; a turn whose
window, tool results or own question exceed it still fails `limit_input_tokens`, and the
panel still offers a new conversation. Nothing is estimated or trimmed to fit.

Migration 0121 adds the field. Versions saved before it sent every earlier turn, so each
takes 1,000, the largest window, which still sends every earlier turn of a conversation
of up to 1,001 turns. The migration refuses to run if any conversation already holds
more than 1,000 turns, whose next turn would otherwise send fewer than before. A
conversation of an earlier version that later grows past 1,001 turns sends its latest
1,000. The worker and runtime set are unchanged.

The window that applies is the asked version's, over earlier turns of any version. It bounds
what is sent, not the work of an ask: the source walk behind withholding and every
recheck still follows included turns back through the conversation, so an ask's cost
grows with the conversation's length. This has not been measured for very long
conversations.

## D250 — PPA bookings in the page assistant

On the hosted pilot, Read current page on PPA bookings returned `page_not_supported`,
so a question about the PPA a person had open got no answer. The page's two views are
already reads the assistant has: the inspected trade is the editor read the Blotter's
trade view of a PPA makes (`TradeReads.PpaTrade`), and the listed book is the page's
directory read. So both go through Read trades, which an agent version that reads the
Blotter already has switched on, with no new tool, permission or migration.

- The page reports the inspected PPA as Read trades' trade view with instrument type
  `ppa` and its revision, or the listed book as `{"view":"ppa_book","bookIds":[book]}`
  with every other member null. Before a book is listed it reports no book, Looking at
  says to load one, and nothing is asked; ask refuses such a report
  `page_report_invalid`. A trade view of another instrument type is refused too.
- `trades.ppa_book_v1` reads the whole book in one directory read of at most 64 trades
  and rechecks the book's scope, as the page's own read does. A larger book is
  `result_too_large`, never a partial list. The result lists each trade as the page
  does, with its revision, event, exact definition and reference versions and number of
  bindings, and the cutoff; never the booking request, actor, reason or memo. Its step
  records every reference catalog the read checked, so a revoked one hides the answer.
- Both bind the turn's pinned context, cutoff and frame. The PPA reads take the context
  the trading read already bound instead of capturing one, so neither these reads nor
  the existing PPA trade read capture or take the capture lock (D184). PPA bookings
  keeps its own market inputs and knowledge cutoff, which the read does not follow;
  Looking at says the read is at the context shown below it. A PPA the pinned
  knowledge holds at another revision is `page_selection_unavailable`; a page report names the inspected revision. A book or
  PPA trade the pinned knowledge does not show at all is refused `access_denied`, as
  the database's PPA scope read does not tell it from one the reader may not read.
- The database's navigation targets list only the pages Navigate opened before, so
  PPA bookings is readable but not navigable: Navigate refuses the page and any target a
  PPA book read issues `tool_arguments_invalid`. An inspected PPA's target opens it in
  the Blotter, which reads the same trade.

Delivery prices stays unsupported: its read, `power_data.read_prices`, captures a
date context, which tool reads never do (D184), so it needs a read that binds the
pinned context without capturing.

## D251 — Saved views in the sidebar

People asked for the views they keep in the Blotter to sit in the sidebar like pages,
and for PPAs to be one of those views rather than a page of their own.

- A saved view records whether the sidebar lists it, as `sidebar` in layout version 5.
  Save view has a Show in the sidebar choice, unchecked for a new view. The API and the
  page refuse a layout without a true or false `sidebar`, and a stored version 4 view is
  listed as an earlier format, as earlier versions are; nothing is migrated.
- The sidebar lists the flagged views of the person signed in under the Blotter, in the
  order the API returns them (by name), read when the workspace opens and again whenever
  the Blotter reads its views. Choosing one opens the Blotter on that view, read afresh,
  as choosing it from the saved-view list does; a view deleted elsewhere is refused with
  a message instead of opening another. The entry of the view the Blotter shows is
  marked; while it shows no listed view, the Blotter is.
- PPA bookings keeps its page, opened from New trade's Open PPA bookings, and its group,
  Trading, but has no sidebar entry. The Blotter lists, values and shows PPA positions;
  its PPA panel says that forward amendments and termination are made in PPA bookings,
  which New trade opens. A reader who cannot book no longer reaches that page.

## D252 — PPAs the market data cannot price, in the Blotter

The Blotter prices the PPAs of a read together, so a single PPA whose delivery the
selected market data has no price for refused the whole read, futures and swaps
included. Since New trade needs that read (D251), it also closed booking. In the owned
PPA fixture a one-day profile PPA in 2031 did exactly that: at the later closes no
period block of its shaped curve covers that day. The PPA page's own book valuation
refuses the same book at those closes, so the refusal is the market data's, not the
Blotter's.

- The refusals that mean the market data has no price for part of a delivery are:
  `interval_block_coverage` (no period block of a shaped curve covers an interval),
  `interval_curve_coverage` (a marked curve has no interval there),
  `interval_actual_missing` (a due actual was not collected), and a frame that leaves
  out a projection a trade needs (`pilot_close_inputs` naming those trades with
  `missing_curve_view`). An interval two period blocks cover is an ambiguous
  definition, so it keeps `interval_block_partition` and is not one of them, nor is a
  shape or quantity series integrity error (`interval_shape_coverage`).
- A refusal naming its trades lists those trades. Price-source admission attributes
  other supported market refusals to their declared coverage. Those are listed whole, as
  D246 lists other-version trades, with `unavailableReason` `unpriced_delivery`,
  shown as **Not valued** with "No price for part of its delivery at this close". The
  rest are then priced together as before, so their values are those of a read
  without the refused trades. Pricing a trade depends only on its own spans, so a
  trade refused alone is refused in any read that holds it.
- The owner amendment of 2026-10-29 adds two retained-artifact refusals:
  `side_schedule_missing` and `side_schedule_unsupported` (an unsupported retained
  schema or generator). Every binding of an affected trade is listed with
  `unavailableReason` `needs_rebooking`, shown as **Needs rebooking**, and distinct
  exact `unavailableCodes`. Its monetary values remain null. No booked schedule
  is regenerated or loaded through a compatibility path.
- An affected group's complete valuation total remains null with
  `valuationUnavailableReason` `needs_rebooking`, even when healthy positions in
  that group have exact values. Healthy position values, unaffected groups and
  booked counts remain available. Existing explicitly excluded market-coverage
  subtotals retain their D246/D252 policy.
- Any other refusal, including corrupt manifests/content, ambiguous geometry,
  missing authority or cancellation, or a market refusal that appears only when
  trades are priced together, still refuses the read. Nothing is priced from a default.
- Existing other-version and market-coverage subtotals leave those trades out and count them in
  `unvaluedPositions`, as D246 does. Like an other-version trade, each listed trade is
  its own market, `ppa_instrument:<instrument id>`, with no curves, so a pivot by
  market does not group it with its curve. Whole-book consumers (close risk, portfolio
  risk runs and the PPA page's book valuation) keep refusing such a scope.
- Report admission collects artifact refusals once per unique instrument and
  attributes market refusals using price sources, before quantity history and the
  valuation graph are prepared. Identical declared price windows share a probe
  within the same pinned operation; native source reads may be reused only under
  that exact curve, context and coverage. The remaining whole trades share one
  quantity/valuation preparation and valuation pass. There is no retry which fully
  prepares and prices every trade on its own.
- These exclusions belong only to the Blotter report. Direct PPA valuation,
  portfolio/close risk and their full-scope admission remain strict under D197.
  Current authority is rechecked when immutable schedule content is reused;
  report recovery from a missing artifact does not recover permission or integrity errors.

## D253 — One side model: parameters, a whole-term schedule, rows read from the graph

Status: decided by the owner (2026-10-28) as the target model; not implemented. This
record states the contract the implementation must meet and the evidence that
qualifies it. Until then the current side families below remain the implemented
behaviour.

### Why

A PPA is a sided instrument like a swap, but today it is priced through a separate
side family. `PpaFixedSide` and `PpaFloatingSide` restate what `FixedSide` and
`FloatingSide` express, with their own price terms and an interval schedule that
holds no rows. Swaps, for their part, keep one delivery period and one payment per
`FixedSide` or `FloatingSide`, and express a multi-period swap as a side made of
other sides (`MonthlySwapSide`, `ScheduledSwapSide`). Two row types,
`SidePaymentPlan` and the PPA's prepared interval rows, describe the same thing.
This keeps PPA booking apart from New trade and makes one side editor impossible.
D84 already asks for small, behaviour-based side families; this record says what a
side of each family is.

### The model

- **A Side is its parameters for the whole term of the trade.** It holds:
  - delivery terms: the term's start and end, a granularity (month, day or
    interval) and the clock or calendar that granularity is counted on;
  - a quantity rule: a fixed quantity per period, an explicit quantity per period,
    metered volume, a profile, or accredited capacity, each with its unit;
  - a price term: fixed (flat, escalated or stepped) or index (curve, multiplier
    and adder);
  - for an index price, an observation rule: the monthly index, equal or weighted
    daily fixings, or the curve's price for each delivery interval;
  - a payment rule, as today's payment conventions express it.
- **No side is made of other sides.** `MonthlySwapSide` and `ScheduledSwapSide`
  are removed: a monthly swap is one side with monthly granularity, and an
  explicitly scheduled swap is one side whose quantity rule lists a quantity per
  period.
- **The side families are the D84 families.** `FixedSide` and `FloatingSide` price
  payments for swaps and PPAs alike. Physical delivery stays its own family
  (`PowerPhysicalSide`, `CertificateDeliverySide`, `CapacityDeliverySide`), as a
  delivery is not a payment; those sides follow the same parameters, schedule and
  rows pattern. `PpaFixedSide`, `PpaFloatingSide` and the PPA-only price term
  types are removed; escalated and stepped prices become price terms of
  `FixedSide`, available to swaps too.
- **`Side.GenerateSchedule()` builds the schedule for the whole term from the
  side's parameters alone**, deterministically. A schedule is a list of
  `ScheduleRow`s, one per delivery period or interval. A row holds:
  - its delivery window, and for an index price its observation window;
  - its payment date, from the payment rule;
  - references to the graph nodes it reads: the price point or points it
    observes, the source of its quantity when the quantity is data (metered or
    profile), and the discount factor of its payment date.
  A row holds no market value. `SidePaymentPlan` and the PPA's prepared rows are
  replaced by `ScheduleRow`.
- **Values are graph lookups.** A row's price, quantity, amount and PV are read
  from the calculation graph (`GraphSession`) of one market scenario. A shock is
  applied to the graph, never to a row, a side or a trade; the session recomputes
  only the nodes downstream of it, and each row re-reads its nodes. The schedule
  shown for a scenario is therefore the schedule evaluated under that scenario,
  and its rows change exactly where the shocked inputs reach.
- **PV.** `Side.PV(context)` evaluates its rows under the context's scenario and
  sums them: price × quantity, discounted to the row's payment date.
  `Instrument.PV` sums coefficient × `Side.PV` over its memberships, and the trade
  scales by its booked quantity once, as §3.2 already requires. The multiplication
  belongs to the side, which owns the price term and quantity rule; the schedule is
  the shared description of the term that settlement, dependency declaration, the
  Blotter's detail rows and risk all read, so they never disagree about the rows.
- **Risk** stays base, up and down PVs of the same instruments under shared
  scenarios. A side declares its dependencies from its rows' graph references, so
  a scenario that touches none of them reuses the side's value.

### Every sided instrument, and every schedule shown

Amended by the owner (2026-10-28).

- **Every sided instrument follows this pattern.** Every instrument that is a
  `SidedInstrument` (forwards, financial swaps, monthly and scheduled swaps,
  cross-commodity swaps such as heat rates, and PPAs) is made of sides that each
  generate their schedule; none keeps a pricing path of its own. A futures
  contract stays a leaf instrument with no sides, as D110 decides.
- **A sided position shows the schedule of each of its sides.** The position's detail
  lists its instrument's sides, and each side's schedule as rows evaluated under
  the selected close or scenario: the delivery window, the observed price, the
  quantity, the amount, the discount factor and the PV. Each side's rows sum to
  its side PV, and the sides with their coefficients sum to the instrument's PV
  as the Blotter reports it. It reads the same schedule the valuation reads, from
  the backend; the page does no pricing. How a long schedule (a PPA's tens of
  thousands of intervals) is shown, for example by month with a month's rows on
  request, is open with the row-node question above.
- **The detail opens under the position's row.** Choosing any position, a future's
  included, expands its
  detail beneath that row in the Blotter, in place of the docked side panel's
  Position mode. New trade, Risk report and the assistant keep the side panel.

Amended by the owner (2026-10-29): the inline control says **Close position
view**. Double-clicking an individual trade row opens a separate, read-only
window for its Terms, History and schedules. That window retains the selected
snapshot, resolved knowledge context, trade binding and revision; every backend
read still checks current authority. It does not advance the parent workspace's
context or expose booking, amendment or settlement actions. A missing context or
denied read clears the detail rather than substituting current data.

### Bounds and choices still open

- Whether each row is itself a graph node (its amount depending on its price,
  quantity and discount nodes), or the side reads node values and sums. Row nodes
  give drill-down and recomputation directly from the session; a PPA has tens of
  thousands of interval rows, so this needs a bound on graph size or month-level
  row aggregation, and a benchmark that names rows, nodes, scenarios and memory.
- How existing trades move to the new definitions. The economic identities of the
  sides change, so stored swaps and PPAs are rebooked under new definition versions
  (no migration shims, per the repository's no-backwards-compatibility rule). On the
  hosted pilot this is a reseed.
- Futures are unchanged: `FuturesContract` stays a leaf instrument (D110), and
  `ForwardContract` composes the new sides.

### Qualification

- Every current swap and PPA valuation check gives the same exact PV under the new
  model, row by row where rows are comparable, before any old type is removed.
- Schedule generation is checked from parameters alone, independently of
  valuation; valuation is checked row by row.
- A shock check shows that only the rows reading the shocked nodes change, and
  that the side and instrument totals change by the sum of those rows.
- Each sided position's detail shows every side's rows, whose amounts sum to the side
  PV and, with the coefficients, to the PV the Blotter reports.
- Independent adversarial review, as the change touches numerical correctness and
  scenario reuse.

### What follows

New trade offers a PPA as a third product, with one side editor for swaps and PPAs
(D251's follow-up), and amend and terminate move into the PPA's detail under its
row. That work
starts after this model is implemented and qualified.

### Rows and roots

Amended by the owner (2026-10-28, D258). A schedule's rows are D258's logical rows:
observations, applications, averages, quantity steps, amounts and discount and
valuation steps, joined by edges, rather than one row per period. Each delivery
period or interval has one contribution. `Side.PV` sums the side's PV contributions
and roots only, and the Blotter's detail shows contribution rows, whose amounts sum
to the side PV. The schedule is retained when the side is booked and loaded for
valuation.

### Cutover without legacy paths

Amended by the owner (2026-10-28). Implement the new whole-term side and retained
schedule contracts directly. Remove compatibility wrappers, dual-format readers,
old-definition fallbacks and legacy booking routes. Existing definitions are
explicitly rebooked or retired; missing financial terms are not invented to keep
an obsolete definition executable. Private one-time rebooking tools are not part
of the application or public repository. Historical migrations remain the
immutable schema record.

An archive-and-rebook release preserves the original database and records a
certified trading-history boundary in the replacement database. The boundary
references an exact knowledge context containing the complete rebooked trading
publication prefix. Earlier trading knowledge, or an incomplete prefix at the
same knowledge time, refuses as `trading_history_archived`; it must not present
only surviving product families as a complete historical book. This applies to
individual trades and explicit PPA scopes as well as the Blotter and risk.
Market and reference history remain available at their original cutoffs. Only
the schema-release operator can record the immutable boundary; an identical
operation is a no-op, and a conflicting boundary refuses. Archived display
reference numbers remain reserved when obsolete active records are removed.

## D256 — A turn's pinned As of frame may hold up to 256 KiB

A page-assistant turn asked over an As of frame stores the frame's exact pins (§7.2).
0072 refused pins whose JSON exceeded 64 KiB with `turn_input`. A close that pins about
a thousand prices writes about 86 KB, so on the hosted pilot every turn over such a
frame failed before its model was called. With the owner's approval the pilot's
`assistant.pin_turn` was replaced in place with a bound of 262,144 bytes on 2026-10-27.

Migration 0122 makes that the reviewed definition: 0072's, with the bound raised to
256 KiB and nothing else changed. It fails closed unless the function it replaces is
0072's or already its own, with the same owner and privileges, so it applies both to a
database at 0121 and to the pilot after its hotfix. A frame's pins between 64 KiB and
256 KiB are now kept as given; larger ones are still refused `turn_input`. The bound
stays a fixed value in the function.


## D257 — Settlement selection and the price node

Status: decided by the owner (2026-10-28). Local implementation and qualification
are in progress. Refines D165 and D253,
and supersedes D165's retention of projection-only legacy sides, since sides are
rebooked under D253.

### Why

Every curve declares a settlement target (`market_data.curves.settlement_target_code`),
and fixings are stored per index and observation period
(`market_data.index_fixing_observations`). PPA period curves already return a
contract's published fixing from the curve lookup once it is due. Swaps do not: a
floating side stores its own index, which booking and valuation require to equal the
curve's (`SwapStore.ResolveFloating`, `CurveModel.RequireIndexProjection`), holds the
fixing schedule itself (`FloatingSide.Fixings`) and passes one `IndexFixing` to each
lookup, and decides between projection and fixing in `SideValuationContext.Resolve`
with a subset of the checks in `IndexFixingPricing.Resolve`. The same decision is
made in two places, and a side cannot settle against a different index from its
curve.

### Scope

This decision covers observations of date-only period contracts: swap observations
(monthly or daily) and period-curve observations of PPA sides. Interval-curve
observations (marked or shaped) are unchanged: their nodes return collected
actuals, marks, frozen anchors or estimates under D174 and their actual rules, and
they take no settlement selection.

### The decision

- **Settlement selection.** Each `FloatingSide` index price term (D253) observing
  period contracts states one of:
  - `curve_target`: the curve's declared settlement target, which must be a single
    index;
  - `override`: a named price index from the pinned reference revision, different
    from the curve's target.

  There is no default. A target of kind `index_difference` or `future_series` is
  refused. The selection, and for an override `forward_curve_no_basis_v1`, are
  identity fields of the side's economic definition.
- **The price node.** A row's price reference is the curve, the contract and the
  settlement selection. Its node is a derived selector downstream of the curve's
  mark points, never a shock coordinate. For the selected index's observation
  period it returns:
  - before the fixing instant: the side's bound projection, which is the curve's
    forward mark or the bound custom projection output;
  - at or after the fixing instant: that index's published final fixing, selected
    under the context's market cutoff. It is a constant: its node depends only on
    the fixing observation, contributes cash and carries no quote risk;
  - between the contract's expiry and the fixing instant: no value, refused with
    `source_expired` at valuation and `swap_projection` at booking, under every
    pending-actuals policy, as today. The last mark is never used;
  - for a due fixing that is not selected: a structured error.

  Node dependencies are built per resolved context: a live node depends on its
  projection, a due one only on its fixing observation, so an expired mark is never
  requested.
- **Fixing schedules.** For `curve_target`, today's rule is kept: a period with no
  fixing row is priced from its projection until expiry and refused after it. For
  `override`, the override index must have a fixing observation for every observed
  period, with the side's quote, unit and currency and a series the model can pin;
  otherwise the side is refused when booked and the valuation fails. For daily
  observations the curve must also have one contract per observation day. The
  schedule's identity comes from the side's pinned definition and reference
  revisions; a later revision does not change a booked side's valuation without a
  rebooking.
- **Override projection.** An override projects its unfixed observations from the
  curve's forward marks, as the recorded choice `forward_curve_no_basis_v1`. The
  side records both the curve's target and the override index. Any difference
  between the two indexes is not modelled; its exposure is to the curve's marks.
  Valuation receipts and risk lineage carry the choice, and the Risk report labels
  the exposure as curve risk with an unmodelled basis to the override index. An
  override with a custom projection is refused until it is decided separately.
  Another projection, such as a curve that settles to the override index or a
  declared basis input, needs its own decision.
- **The spread.** Today's swap spread, added to the fixing or the projection inside
  the side, becomes the price term's adder (D253); the node returns the raw index
  price.
- **One node.** Swaps and period curves use the same price node and
  `IndexFixingPricing`'s eligibility. The side's own projection-or-fixing decision
  is removed.

### Qualification

- For the synthetic swaps and PPA period-curve sides settling to their curve's
  target, PV, cash and risk equal today's at valuation instants before, at and after
  each fixing instant; between expiry and the fixing instant both refuse as today.
  Swaps that the full `IndexFixingPricing` checks newly refuse are listed. Booking
  evidence selects fixings observed at or before valuation and valuation at or before
  the market cutoff; which rule both share is open and decided before step 1 of its
  plan.
- An override side settles to the override index's fixings, projects from the
  curve's marks before them, records `forward_curve_no_basis_v1` in its definition,
  receipts and risk lineage, and is refused when the override index lacks a
  matching fixing schedule, equals the curve's target, or carries a custom
  projection.
- A settled observation's node is unchanged by every scenario shift, and a shift
  that targets a price node is refused.
- Independent adversarial review, as the change touches temporal and numerical
  behaviour.

## D258 — Persist each booked side's complete schedule

Status: decided by the owner (2026-10-28). Local implementation and qualification
are in progress. Refines D253, whose
rows become this record's logical rows, and D195, whose normalization moves to the
side. The schedule `Side.GenerateSchedule()` builds is retained in PostgreSQL when
the side is booked, and valuation loads it rather than regenerating it.

Amended for the local implementation trial on 2026-10-28: retain native-interval
calculations as compact, versioned vector operations grouped by payment period,
with exact grids and typed columns. A logical calculation is retained without a
separate SQL record for each cell's arithmetic. PostgreSQL remains stock; no
columnar storage extension is introduced. The [payment-block
experiment](side-block-experiment.md) supplies bounded arithmetic and storage
evidence; application integration and its release qualification remain in progress.
The cutover rebooks definitions and introduces no backward-compatibility loader.
The 2026-10-29 D252 reporting amendment permits the Blotter to list a trade
whose retained artifact is missing or unsupported as needing rebooking. Direct
valuation remains refused, corrupt content remains a hard error, and the report
never repairs or regenerates the stored artifact.

### Why

D253's schedule is the shared description of a side's term that valuation,
settlement, dependency declaration, the Blotter's detail rows and risk all read.
Regenerating it from current code at each valuation cannot reproduce a historical
result exactly (context §7), and settlement needs obligations whose identity
survives an amendment of later periods. A retained schedule serves both.

### The decision

- **Everything logical is retained.** A schedule holds every logical row of the
  side's pricing: observations, observation applications to their target periods,
  averages and weights, quantity calculations and normalization, amounts, discount
  and valuation steps. Rows have explicit roles (source, intermediate,
  contribution, root), and named roots identify the cashflows, quantities, PV and
  other supported measures. `Side.PV` and the Blotter's detail read only the roots
  and contribution rows their valuation contract selects, never every row, so an
  intermediate is never summed with a result.
  Native operations act on aligned columns: a monthly quantity input,
  normalization, price application or sum is one versioned operation over its
  grid and explicit selection. Scalar dated operations use the same typed
  contract. Full observation, normalization and shaping scopes remain explicit;
  selecting part of a payment cannot redefine those scopes.
- **Definitions, not values.** A row states how its value is obtained: a
  contractual literal, an exact contract, a typed binding or a versioned backend
  operation with explicit arguments. A fixed price is a row bound to its literal; a
  settled observation is a row bound to its fixing definition, whose value the price
  node resolves under each context (D257). Prices, quantities, discount factors and
  PVs evaluated under a context or scenario are valuation results, retained with
  their receipts where a consumer needs them, never written into the schedule. Rows
  stay in the schedule after their periods pass.
- **Tables.** One common structure for every side family:

  | Table | Contents |
  | --- | --- |
  | `trading.side_schedules` | The exact side foreign key, generator/schema versions, content counts and canonical manifest; the parent `trading.sides` holds the exact definition/reference revision foreign keys |
  | `trading.side_schedule_rows` | Every scalar or vector operation: stable local identity, role, operation and version, output type with its exact unit or currency, delivery, observation and value dates, closed parameters, and its exact grid where applicable |
  | `trading.side_schedule_edges` | Ordered, named inputs between rows; a repeated use of one observation is several edges |
  | `trading.side_schedule_grids` | Exact UTC geometry and selections, a typed clock foreign key, and packed local date/time/occurrence metadata; contractual literals live in closed operation parameters, and dynamic samples remain context inputs |
  | `trading.side_schedule_bindings` | Typed exact-version references from operations to supported contracts/curve members, computed outputs, fixing definitions/series, quantity sources, assets and declared sibling inputs |
  | `trading.side_schedule_roots` | Named output operations and their valuation measures |

  Operation rows carry their unit/currency foreign keys. Separate typed port,
  output-set and composition tables bind cross-side quantities. Calendar
  provenance remains in the exact immutable side terms. Payment discount inputs
  resolve under the selected valuation context and retain rate construction in
  the receipt; the retained binding table does not claim a rate-curve foreign key.

  The immutable content tables carry `catalog_id` and inherit publication and
  effective coordinates from their append-only publication header. Their restrictive
  `temporal_read` policy follows that header; unpublished staged content is invisible.
  Final publication appends the small header and instrument mappings, without
  stamping or copying the child rows. This refines the original per-child metadata
  layout for the authorized local implementation trial. Physical layout (partitioning, index choice,
  compact interval storage) is chosen from measurements at the largest supported
  workload, without changing the logical contract or these policies.
- **Parameters are typed.** Core relationships use exact-version foreign keys;
  edges reference their endpoints. A row's parameters are a closed key set per
  operation version, with exact numbers as canonical reduced rational numerator/denominator strings
  (positive denominator), or exact decimal strings where a source contract requires
  representability. The database validates the key set and literal types. Any
  reference goes through a binding table, never a name in the parameters. No
  embedded graph, executable script or evaluated value is stored.
- **Reads.** Valuation finds a schedule only through the trade revision and its
  binding visible at the context's knowledge cutoff, never as the latest schedule
  of a side. Within retained trading-history coverage, a cutoff before a booking
  or rebooking sees the earlier schedule or none. A cutoff outside an explicit
  archive-and-rebook boundary refuses as `trading_history_archived`. Schedule
  rows are read only through an authorized trade or position read,
  so the Blotter detail, valuation and agents use the same operations, and loading
  re-checks current authority.
- **Reuse.** An identical retained schedule is reused within one catalog after full
  canonical comparison, not a hash alone; changed terms create a new immutable
  definition. Sharing a schedule shares no settlement state: settlement is held per
  trade binding and membership.
- **Occurrence identity.** Only obligation rows (roots and contributions) carry an
  occurrence key. It is built from the obligation's own contractual coordinates
  (its role, payment rule and value date, and its delivery window or interval on its
  grid), never from the side's identity, together with a digest of that
  obligation's own terms. An amendment of later periods creates a new side and
  schedule; an unchanged earlier obligation has the same key and digest, so its
  settlement continues. An amendment of an earlier obligation changes its digest,
  and the lifecycle decides how that obligation's settlement proceeds.
  A vector contribution exposes the same identity separately for every obligation
  cell. Neither a block ordinal nor its complete manifest enters the cell's own
  key or terms digest, so a later-period amendment cannot change an unchanged
  earlier obligation.
- **Side completeness.** A side's parameters hold every economic input its schedule
  needs. Normalization assets and basis (D195), held on the PPA instrument today,
  become a shared immutable normalization specification that each side references;
  the sides of one instrument must reference the same specification, so the
  instrument still divides once by one denominator. A row that consumes another
  side's output declares a typed input port (quantity type, unit and structural
  schedule); the instrument binds the port to an exact membership and output root by
  foreign key. No binding or cadence is inferred from a slot name, and the
  instrument's combined graph is checked for missing bindings, incompatible geometry
  and cycles.
- **Dates per lookup.** Each binding declares which date selects its terms: a
  service date, an observation window or a payment date. No one date predicate is
  applied to every table.
- **Booking.** The managed backend generates the schedule from the side's terms;
  booking never accepts rows from a client, and request bodies stay within their
  existing limits. Booking authorizes, generates in bounded batches, validates,
  publishes the schedule with the trade binding and returns an idempotent receipt
  (D238). A schedule larger than one short transaction is written first as unpublished
  staged rows keyed by the request id, invisible to every read. One short fenced
  transaction then validates and publishes the staged schedule with the binding. The stage and immutable seal bind the current actor, catalog, exact command,
  expected head, compiler versions and complete content manifest. Changed chunks
  conflict. Final publication rechecks current authority and expected versions in
  the same transaction as the trade binding and idempotent receipt. A retry with
  the same request id resumes the exact stage or returns the first outcome;
  abandoned unpublished stages remain outside historical business reads. No booking commits without its
  schedule, and nothing raises the command deadline. The earlier expanded-format
  701,280-interval write exceeded the 5 s command timeout (D173); that historical
  result does not measure the compact layout.
- **Validation.** The database checks foreign keys, ownership, allowed operation
  and schema versions, parameter key sets and literal types, required ports and
  roots, edge endpoints and ordering, uniqueness, publication and authorization. The
  managed compiler checks that the rows faithfully express the side's terms;
  independent behavioural checks qualify it. The manifest digest, computed by the
  backend, proves integrity, not fidelity.
- **Loading.** Valuation loads a booked side's rows, edges and bindings for its
  dependency closure in batches. A missing row or binding, an unsupported version or
  an inconsistent manifest is a structured refusal; a booked schedule is never
  regenerated from current code or reference data at valuation. Drafts use the same
  generator and evaluator on a transient schedule and persist nothing.
- **Retention.** Retained rows are never updated or deleted while any trade
  revision, receipt or settlement references them. Rebooking adds a schedule and
  never replaces one. The current swap schedule tables
  (`swap_observation_schedules`, `swap_scheduled_periods`,
  `swap_power_quantity_intervals` and related rows) are superseded by rebooking under
  D253's definitions, with no compatibility path.
- **Execution (owner amendment, 2026-10-29).** PostgreSQL retains the complete
  executable schedule for every supported Side family, including dated and
  interval sides. Booking constructs and validates the operation order, input
  bindings, scalar/vector layouts, reductions, payment and lifecycle selectors,
  sibling-port mappings and row/output maps. The managed backend evaluates that
  retained program directly. A booked read does not regenerate schedules,
  reconstruct a topology from Side parameters, lower expressions, or compile a
  replacement execution graph. Deserialization reads the published instructions;
  integrity, supported-version and current-authority checks remain mandatory.
  Missing or inconsistent executable content refuses; it never triggers a builder.
  The same executable contract serves all sided instruments, rather than a
  PPA-specific valuation shortcut.
  A valuation binds one resolved financial context and immutable scenario, then
  resolves the program's price, fixing, discount and dynamic-quantity inputs.
  Independent input reads execute concurrently in bounded batches, with shared
  source requests deduplicated. Each read preserves the selected temporal cut,
  exact input versions and current authorization. Dependency-ordered arithmetic,
  normalization and shaping evaluate the stored instructions; changed inputs do
  not change the stored schedule. No market samples or scenario values become
  part of the reusable executable definition. Lineage maps evaluated values back
  to the same published rows and roots.
  Loading does not reconstruct the superseded scalar-operation DAG. Bounded
  interval detail is a view over the retained vector operations and exact cells.
  Grid alignment, units and source dependencies are reusable column contracts;
  each financial or physical operation retains its own supported version and
  semantics. This does not authorize a generic scripting or matrix-expression
  language.
- **Executable storage checkpoint.** One immutable artifact belongs to each
  retained schedule, with complete typed correspondence checked before sealing.
  Booking seals bytes and typed content atomically; direct loading reads the
  authorized artifact without generators. The installation boundary and remaining
  valuation integration are recorded in [booked programs](booked-side-programs.md).
- **Evaluated row state (owner clarification, 2026-10-29).** Row state is resolved
  under the user's selected financial context, alongside prices and quantities;
  it is not a mutable field on the published schedule. Price selection (forward,
  actual or fixed), delivery progress, contractual expiry and payment state are
  separate facts. Expiry or a fixed price never establishes payment. Settlement
  facts belong to the trade binding and obligation occurrence, so trades sharing
  a Side can have different payment states. Historical views use lifecycle facts
  visible at their effective time and knowledge cutoff. Missing lifecycle support
  is reported explicitly rather than inferred from a date. The common execution
  path must preserve these distinctions in its evaluated schedule; this
  clarification does not claim additional lifecycle commands are implemented.

### Qualification

- Today's valuation checks for every supported swap and PPA give the same exact
  values under loaded schedules, and a loaded schedule evaluates identically to
  the freshly generated one it was booked from.
- Intermediate applications, weights and averages are checked independently;
  every root reconciles to its measure, and summing all rows is never a result.
- An amendment of later periods keeps earlier occurrence keys and digests; two
  trades sharing a schedule keep separate settlement state.
- Booking is atomic and idempotent: a retry or an identical definition adds no row,
  staged rows are invisible, and cancellation publishes no partial schedule.
- Within retained trading-history coverage, a historical read before a booking
  sees no schedule; a read before a rebooking sees the earlier one. An archived
  cutoff refuses rather than returning an incomplete historical book.
- A missing or inconsistent retained structure refuses rather than regenerates;
  unauthorized references and withdrawn grants refuse.
- Storage measured at the largest supported workload: logical rows and edges,
  table and index size, staging and publication duration, cold load, compilation,
  and base and scenario evaluation.
- Independent adversarial review, as the change touches persistence,
  authorization, temporal behaviour and numerical correctness.


## D260 — The page assistant saves custom-curve drafts

Status: decided 2026-10-29. Migration 0128 is the database groundwork; the tool is implemented in
runtime set `turns_v9`. Supersedes D193's "never grants agent writes" for this one tool only;
every other agent tool stays read-only, and the assistant still never publishes,
changes activations, runs risk or navigates by itself.

- **Context.** Curve builder opens a curve on its outputs, with its graph,
  configuration, versions and lineage as tabs a person may arrange; its tab dock
  serves this page only. A new curve starts from the assistant, which prepares,
  previews and saves a draft for the person to review, rather than from a wizard;
  New curve, the templates and Combine curves remain. The arrangement is the
  viewer's presentation state, kept in the browser, never in the database.
- **Tool.** `save_custom_curve_draft` (operation `custom_curves.save_draft_v1`) takes
  what `prepare_custom_curve` takes and a required `curveId`, null for a new curve
  or the id of its own draft to revise, and runs the same managed save as the person's Save draft. It ships in
  runtime set `turns_v9`: `turns_v8` is reserved for the installation limits (P42).
- **Authority.** `assistant.agent_tools()` lists it with `ReadMarketData` and
  `WriteMarketDefinitions`, so a version's allowed roles must hold both and a grant
  lists both. The agent never holds `WriteMarketDefinitions`: a new permission,
  `DraftCustomCurves`, held only by the agent reader role, is its own draft
  authority (decided 2026-10-29).
  Under an agent context the save first claims the one curve version a step of its
  running turn saves (`security.claim_custom_curve_draft`), fencing the turn as its
  worker's writes do (`assistant.fenced_turn`: its token and the current worker
  generation, refused `agent_runtime_unavailable`). The claim holds the turn row, so a cancel waits for the save, and
  the version row, so a person's save or a publication waits too, and it remembers
  the generation it saw; a new curve's claim is its version 1, refused
  `custom_curve_catalog_limit` once the catalog holds 64 curves, counted across the
  catalog whatever the agent can see, and a second claim
  in one context is refused `custom_curve_agent_claimed`. Every write to the
  ordinary save's tables then needs, at that moment: the agent holding
  `DraftCustomCurves`; its grant current, the running turn's, listing
  `WriteMarketDefinitions` and given for a version that switches on Save curve
  draft, as the turn's version does; its delegator currently holding
  `WriteMarketDefinitions` and `CreateDelegations` in the turn's catalog's
  workspace; and the claimed curve version. Each agent write also queues a check,
  run at commit (or at the end of its statement once constraints are immediate),
  that repeats the authority and requires the claim to have saved exactly one new
  draft generation, recorded as the assistant's for the claimed turn and a
  successful `save_custom_curve_draft` step of it. A revocation, cancellation or
  loss of authority before commit therefore rolls the draft back. Missing authority
  refuses `delegation_denied` and writes nothing. Publishing, activation, other versions,
  other curves and every other market definition keep their author permissions,
  which an agent never holds. A person's save is unchanged.
- **Scope.** `security.custom_curve_agent_writable(catalog, curve, version, turn)`,
  asked only by the claim (the runtime cannot call it), is true for a curve the catalog does not have yet, or for a draft whose latest
  generation the assistant saved in a turn of the same conversation. A person's
  generation, a generation with no recorded saver and a published version are
  never the assistant's; it refuses `custom_curve_agent_scope`.
- **Provenance.** `market_data.custom_curve_generation_savers` records who saved
  each draft generation: `person`, or `assistant` with its turn and step. A record
  names the draft's current generation and never changes. Only an author records
  their own save as a person's, undelegated; the assistant's record names the turn
  and step its agent context claimed. The step reference is checked
  at commit, so an assistant draft commits only with its recorded step. The
  managed save records the saver of every generation it writes. A curve read shows
  only who saved each version's generation, `saver.savedBy`: `person`,
  `assistant`, or null for a generation saved before savers were recorded, never a
  person's by default. The turn and step stay stored and are not returned, and the
  turn's token is never stored or returned. An assistant save is
  identical to the current generation when its graph (code, name, output and every
  node's content, in any order, not the layout) and the exact source, calendar and
  schedule versions it resolves to are both unchanged; otherwise it writes a new
  generation pinned to the selected references.
- **Hand-off.** A `draft` target on `custom_curves` has exactly `{turnId, step}`
  and is valid only for a successful `save_custom_curve_draft` step of that turn
  that recorded the assistant as a generation's saver. The person opens it; the
  page's leave prompt protects unsaved edits. It opens the curve and version whose
  saver names that step, as the person's own context reads it, so its answer is
  bounded by that person's own row-level security and needs no separate ownership
  check: a caller learns at most that a turn maps to a draft they can already open.
  An identical save writes no saver of its own, so its step issues a Curve builder
  page target instead, and the database refuses a `draft` target for it.
- **One transaction.** The claim, the store's writes, the saver row and the
  successful step's `record_tool_step` run in one transaction on one connection
  under the agent's context, which holds only `ReadMarketData`; any refusal rolls
  them all back and the worker records the refused step as any other. A historical
  pin is refused `historical_read_only` before any claim. The pinned references
  are those the curve resolves to at the turn's pinned context.
- **Idempotent (D238).** The key is turn and step: the request identity is
  UUIDv5(turn, step) and a new curve's identity is name-based on the same key. The
  worker never retries a save step; a re-run of the same key returns its first
  outcome from the store's receipt before writing while the draft is still the
  assistant's, since the claim precedes the replay (a person's save in between
  makes the re-run `custom_curve_agent_scope`). A second save in one turn is a new
  generation. Saving a definition
  identical to the current generation (see Provenance: the same graph on the same
  exact references) returns it unchanged and writes nothing at
  all, neither a request receipt nor a saver record; its retry evaluates to the same
  no-op. Any agent write under a claim, a receipt included,
  needs exactly one new generation at commit.
- **Refusals.** `historical_read_only` for a historical or As-of pin,
  `custom_curve_open_unsaved` when the page reports that curve open with unsaved
  edits, `custom_curve_agent_scope`, `custom_curve_catalog_limit` for a new curve
  in a catalog that already holds 64 curves, `custom_curve_conflict` when pinned
  references changed since preparation, and `delegation_denied`.
