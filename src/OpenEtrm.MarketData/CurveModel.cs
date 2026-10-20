using System.Collections.Frozen;
using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData;

// Pure managed core. No database, clock, network, provider SDK or ambient state.
// Reference leaves arrive resolved through the database's exact-version FKs.
public sealed partial class CurveModel
{
    public const string NumericalPolicy = "exact_18_fractional_abs_lt_1e24_v1";
    private static readonly DateTimeOffset End = new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly EndDate = new(9999, 12, 31);
    private readonly DefinitionRevision revision;
    private readonly ReferenceRevision reference;
    private readonly ImmutableDictionary<ReferenceKey, QuantitySpecification> quantities;
    private readonly ImmutableDictionary<ReferenceKey, QuoteSpecification> quotes;
    private readonly ImmutableDictionary<ContractKey, MarketContract> contracts;
    private readonly ImmutableDictionary<CurveKey, Curve> curves;
    private readonly FrozenDictionary<CurveKey, FrozenSet<ContractKey>> curveMembers;
    private readonly FrozenDictionary<(CurveKey, InputSlot, ContractKey), ContractKey> mappingSources;
    private readonly ImmutableDictionary<ReferenceKey, FuturesSeriesSpecification> futures;

    private readonly ImmutableDictionary<ReferenceKey, ValueSpecification> valueSpecs;
    private readonly ImmutableDictionary<SeriesKey, MarketSeries> series;
    private readonly ImmutableDictionary<(ContractKey, ReferenceKey), SeriesKey> priceSeries;

    public CurveModel(DefinitionRevision revision, ReferenceRevision reference,
        IEnumerable<QuantitySpecification> quantities, IEnumerable<QuoteSpecification> quotes,
        IEnumerable<MarketContract> contracts, IEnumerable<Curve> curves,
        IEnumerable<FuturesSeriesSpecification> futures,
        IEnumerable<ValueSpecification> valueSpecs, IEnumerable<MarketSeries> series)
    {
        this.revision = revision;
        this.reference = reference;
        this.quantities = Unique(quantities, q => q.Key);
        this.quotes = Unique(quotes, q => q.Key);
        this.contracts = Unique(contracts, c => c.Key);
        this.curves = Unique(curves, c => c.Key);
        this.futures = Unique(futures, f => f.Key);
        this.valueSpecs = Unique(valueSpecs, v => v.Key);
        this.series = Unique(series, v => v.Key);
        priceSeries = Unique(this.series.Values.Where(v => v.Quote is not null && v.Contract is not null),
            v => (v.Contract!.Value, v.Quote!.Value)).ToImmutableDictionary(v => v.Key, v => v.Value.Key);
        curveMembers = this.curves.ToFrozenDictionary(pair => pair.Key,
            pair => (pair.Value.Constituents.IsDefault ? [] : pair.Value.Constituents).ToFrozenSet());
        Validate();
        mappingSources = this.curves.Values.SelectMany(curve => curve.Inputs.SelectMany(input =>
            input.Mappings.Select(map => (Key: (curve.Key, input.Slot, map.Target), map.Source))))
            .ToFrozenDictionary(map => map.Key, map => map.Source);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<ReferenceKey,FuturesContract> contractInstruments = new();
    public FuturesContract FuturesContract(ContractKey member)
    {
        if (!contracts.TryGetValue(member, out var projection) || projection.Kind != ContractKind.Future ||
            projection.FuturesSeries is not {} key || !futures.TryGetValue(key, out var terms))
            throw new MarketDataException("futures_definition", "An exact dated futures constituent is required.");
        return FuturesContract(key);
    }
    public FuturesContract FuturesContract(ReferenceKey key)
    {
        if (!futures.TryGetValue(key, out var terms))
            throw new MarketDataException("futures_definition", "The exact futures-series primary key is unavailable.");
        return contractInstruments.GetOrAdd(key, _ => new(terms));
    }
    public FuturesMarketBinding BindFutures(Guid bindingId, CurveRequest request, string settlementMethod,string implementation,DateTimeOffset? eligibleFrom=null)
    {
        var instrument = FuturesContract(request.Contract);
        if (!curves.TryGetValue(request.Curve, out var curve))
            throw new MarketDataException("futures_definition", "An exact pricing curve is required.");
        var quote = Quote(curve);
        RequireFuturesDefinition(request, contracts[request.Contract], instrument.Series, quote);
        return new(bindingId, request, contracts[request.Contract], instrument.Series, quote,
            settlementMethod, implementation, instrument,eligibleFrom);
    }

    private static ImmutableDictionary<K, V> Unique<K, V>(IEnumerable<V> values, Func<V, K> key) where K : notnull
    {
        var result = ImmutableDictionary.CreateBuilder<K, V>();
        foreach (var value in values)
            if (!result.TryAdd(key(value), value)) Fail("duplicate_definition", "Definition key is repeated.");
        return result.ToImmutable();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code, string message) => throw new MarketDataException(code, message);
    private void Ref(ReferenceKey key)
    {
        if (key.Revision != reference || string.IsNullOrWhiteSpace(key.Code))
            Fail("reference_pin", "Reference must match the exact selected reference revision.");
    }
    private static void EnumValue<T>(T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) Fail("unsupported_kind", $"Unsupported {typeof(T).Name}.");
    }
    private static bool Positive(ExactDecimal value) => value.CompareTo(default) > 0;
    internal void RequireFuturesDefinition(CurveRequest request, MarketContract contract,
        FuturesSeriesSpecification specification, QuoteSpecification quote)
    {
        if (!contracts.TryGetValue(contract.Key, out var retainedContract) || retainedContract != contract ||
            !futures.TryGetValue(specification.Key, out var retainedSeries) || retainedSeries != specification ||
            !quotes.TryGetValue(quote.Key, out var retainedQuote) || retainedQuote != quote ||
            !curves.TryGetValue(request.Curve, out var curve) || !curveMembers[curve.Key].Contains(contract.Key) ||
            request.Contract != contract.Key || curve.Quote != quote.Key)
            Fail("futures_definition", "Held futures terms must equal the exact definitions used to build the market scenario.");
    }
    private QuoteSpecification Quote(Curve c) => quotes.TryGetValue(c.Quote, out var q)
        ? q : throw new MarketDataException("missing_reference", "Quote definition is absent.");
    internal void RequireIndexProjection(CurveRequest request, MarketContract contract,
        ReferenceKey index, QuoteSpecification quote)
    {
        if (!contracts.TryGetValue(contract.Key, out var retained) || retained != contract ||
            !quotes.TryGetValue(quote.Key, out var retainedQuote) || retainedQuote != quote ||
            !curves.TryGetValue(request.Curve, out var curve) || request.Contract != contract.Key ||
            !curveMembers[curve.Key].Contains(contract.Key) || curve.Quote != quote.Key ||
            curve.Settlement.Kind != SettlementKind.Index || curve.Settlement.IndexA != index ||
            curve.Settlement.IndexB is not null || quote.Kind != QuoteKind.Outright || contract.Kind != ContractKind.Forward)
            Fail("swap_definition", "A floating projection requires the exact model-owned outright index contract and quote.");
    }
    internal void RequireIndexFixing(IndexPriceLookup lookup, IndexFixing fixing)
    {
        if (fixing.Index != lookup.Index || fixing.Quote != lookup.Quote.Key ||
            !series.TryGetValue(fixing.Series, out var actual) || actual.Quote != lookup.Quote.Key ||
            actual.Contract is not { } key || !contracts.TryGetValue(key, out var contract) ||
            contract.Kind != ContractKind.Forward || contract.DeliveryFrom != fixing.From || contract.DeliveryTo != fixing.To)
            Fail("index_fixing_definition", "The actual series must retain the exact model-owned quote and observation period.");
    }
    private QuantitySpecification Quantity(QuoteSpecification q) => quantities.TryGetValue(q.Quantity, out var x)
        ? x : throw new MarketDataException("missing_reference", "Quantity definition is absent.");
    private static bool SamePriceUnit(QuoteSpecification a, QuoteSpecification b) =>
        PriceUnit.FromQuote(a) == PriceUnit.FromQuote(b);

    private void Validate()
    {
        if (revision.CatalogId == Guid.Empty || revision.RevisionId == Guid.Empty ||
            reference.CatalogId == Guid.Empty || reference.RevisionId == Guid.Empty)
            Fail("missing_identity", "Definition and reference revision identities are required.");
        foreach (var q in quantities.Values)
            foreach (var key in new[] { q.Key, q.Commodity, q.MeasurementBasis, q.Unit, q.Dimension }) Ref(key);
        foreach (var q in quotes.Values)
        {
            foreach (var key in new[] { q.Key, q.Currency, q.Quantity }) Ref(key);
            EnumValue(q.Kind); Quantity(q);
            if (!Positive(q.Multiplier) || !Positive(q.TradingTick) || !Positive(q.ClearingIncrement) ||
                q.Minimum.CompareTo(q.Maximum) > 0) Fail("quote_specification", "Invalid quote bounds or increments.");
        }
        foreach (var v in valueSpecs.Values)
        {
            Ref(v.Key); v.Validate();
            foreach (var key in new[] { v.NumeratorQuantity, v.DenominatorQuantity }.OfType<ReferenceKey>())
            { Ref(key); if (!quantities.ContainsKey(key)) Fail("missing_reference", "Ratio quantity specification is absent."); }
        }
        foreach (var v in series.Values)
        {
            if (v.Key.Revision != revision || v.Key.Id == Guid.Empty) Fail("definition_pin", "Series must belong to the selected definition revision.");
            if (v.Quote is { } quote)
            {
                Ref(quote);
                if (v.Contract is null || !contracts.ContainsKey(v.Contract.Value) || !quotes.ContainsKey(quote) || v.ValueSpecification is not null)
                    Fail("series_type", "Price series requires an exact contract and quote specification.");
            }
            else if (v.Contract is not null || v.ValueSpecification is null || !valueSpecs.ContainsKey(v.ValueSpecification.Value))
                Fail("series_type", "Numeric series requires a value specification and no fictional contract.");
        }
        foreach (var contract in contracts.Values)
        {
            EnumValue(contract.Kind);
            if (contract.Key.Revision != revision || contract.Key.Id == Guid.Empty)
                Fail("definition_pin", "Contract must belong to the selected definition revision.");
            if (contract.DeliveryFrom >= contract.DeliveryTo || contract.DeliveryTo >= EndDate || contract.Expiry.Offset != TimeSpan.Zero ||
                contract.Expiry >= End) Fail("contract_dates", "Invalid delivery interval or UTC expiry.");
            if ((contract.Kind == ContractKind.Future) != (contract.FuturesSeries is not null))
                Fail("contract_kind", "Exactly futures contracts require a dated series reference.");
            if (contract.FuturesSeries is { } series)
            {
                Ref(series);
                if (!futures.TryGetValue(series, out var spec)) Fail("missing_reference", "Resolved dated futures specification is required.");
                foreach (var key in new[] { spec.Key, spec.QuoteQuantity, spec.Currency, spec.Location, spec.DeliveryProfile, spec.LotQuantity }) Ref(key);
                EnumValue(spec.LotBasis);
                if(spec.BasisSettlement is {} basis)
                {
                    Ref(basis.Index); Ref(basis.BenchmarkSeries);
                    if(basis.Implementation!="index_minus_future_v1" || !futures.TryGetValue(basis.BenchmarkSeries,out var benchmark) ||
                        basis.BenchmarkSeries==spec.Key || benchmark.BasisSettlement is not null ||
                        benchmark.QuoteQuantity!=spec.QuoteQuantity || benchmark.Currency!=spec.Currency || benchmark.Multiplier!=spec.Multiplier ||
                        benchmark.DeliveryFrom!=spec.DeliveryFrom || benchmark.DeliveryTo!=spec.DeliveryTo)
                        Fail("futures_specification","Basis settlement requires an exact compatible dated outright benchmark.");
                }
                if (!quantities.ContainsKey(spec.QuoteQuantity) || !quantities.TryGetValue(spec.LotQuantity, out var lot) ||
                    spec.SettlementMethod is not ("cash" or "physical") || !Positive(spec.ContractSize) || !Positive(spec.Multiplier) || !Positive(spec.TradingTick) ||
                    spec.FirstTradingAt is {} first&&(first.Offset != TimeSpan.Zero || first.Ticks % 10 != 0 || first>=spec.LastTradingAt) ||
                    lot.Commodity != quantities[spec.QuoteQuantity].Commodity ||
                    contract.DeliveryFrom != spec.DeliveryFrom || contract.DeliveryTo != spec.DeliveryTo || contract.Expiry != spec.LastTradingAt)
                    Fail("futures_specification", "Dated contract conflicts with its authoritative futures specification.");
            }
        }
        foreach (var curve in curves.Values)
        {
            if (curve.Key.Revision != revision || string.IsNullOrWhiteSpace(curve.Key.Code))
                Fail("definition_pin", "Curve must belong to the selected definition revision.");
            foreach (var key in new[] { curve.Quote, curve.Location, curve.DeliveryProfile }) Ref(key);
            EnumValue(curve.Rule); EnumValue(curve.Settlement.Kind);
            var output = Quote(curve);
            if (curve.Constituents.IsDefaultOrEmpty || curve.Inputs.IsDefault)
                Fail("missing_members", "Explicit constituents and input collection are required.");
            if (curveMembers[curve.Key].Count != curve.Constituents.Length)
                Fail("duplicate_member", "A constituent cannot appear twice in one curve.");
            foreach (var member in curve.Constituents)
            {
                if (!contracts.ContainsKey(member)) Fail("missing_contract", "Curve constituent is absent.");
                if (contracts[member].FuturesSeries is { } series)
                {
                    var spec = futures[series];
                    if (curve.Rule != CurveRule.Direct || curve.DirectRole is not (ObservationRole.DailySettlement or ObservationRole.TraderMark) ||
                        curve.Settlement.Kind != SettlementKind.FutureSeries || output.Kind != (spec.BasisSettlement is null?QuoteKind.Outright:QuoteKind.Basis) ||
                        output.Quantity != spec.QuoteQuantity || output.Currency != spec.Currency || output.Multiplier != spec.Multiplier ||
                        output.TradingTick != spec.TradingTick || curve.Location != spec.Location || curve.DeliveryProfile != spec.DeliveryProfile)
                        Fail("futures_specification", "Futures curve must preserve resolved series/product specifications.");
                }
            }
            var settlement = curve.Settlement;
            if (settlement.IndexA is { } a) Ref(a);
            if (settlement.IndexB is { } b) Ref(b);
            if (settlement.Kind == SettlementKind.FutureSeries &&
                (settlement.IndexA is not null || settlement.IndexB is not null ||
                 curve.Constituents.Any(k => contracts[k].Kind != ContractKind.Future)) ||
                settlement.Kind == SettlementKind.Index && (settlement.IndexA is null || settlement.IndexB is not null) ||
                settlement.Kind == SettlementKind.IndexDifference && (settlement.IndexA is null || settlement.IndexB is null || settlement.IndexA == settlement.IndexB))
                Fail("settlement_target", "Settlement target does not match its declared kind.");
            if (curve.Rule == CurveRule.Direct)
            {
                if (curve.Inputs.Length != 0 || curve.DirectRole is null || curve.DirectSource is null || curve.Multiplier is not null)
                    Fail("rule_arity", "Direct curves require source/role and no calculated inputs.");
                EnumValue(curve.DirectRole.Value); Ref(curve.DirectSource.Value);
                foreach (var member in curve.Constituents)
                    if (!priceSeries.ContainsKey((member, curve.Quote))) Fail("missing_series", "Direct curve requires an exact price series for every member.");
                if (curve.DirectRole is not ObservationRole.TraderMark and not ObservationRole.DailySettlement)
                    Fail("unsupported_role", "Live price inputs require an explicitly supported price observation role.");
            }
            else
            {
                if (curve.Inputs.Length != 2 || curve.Inputs.Select(x => x.Slot).Distinct().Count() != 2 ||
                    curve.DirectRole is not null || curve.DirectSource is not null)
                    Fail("rule_arity", "Composite curves require exactly base and spread inputs.");
                if (output.Kind != QuoteKind.Outright) Fail("quote_kind", "Composite output must be outright.");
            }
            foreach (var input in curve.Inputs)
            {
                EnumValue(input.Slot); EnumValue(input.ExpiryRule);
                if (!curves.TryGetValue(input.Source, out var source))
                    throw new MarketDataException("missing_curve", "Source curve is absent.");
                var sourceQuote = Quote(source);
                if (source.DeliveryProfile != curve.DeliveryProfile)
                    Fail("unsupported_mapping", "First mapping subset requires identical delivery profiles.");
                if (input.Slot == InputSlot.Spread &&
                    (sourceQuote.Kind != QuoteKind.Basis || !SamePriceUnit(output, sourceQuote)))
                    Fail("spread_type", "Spread must have the exact output currency, quantity and multiplier.");
                if (input.Slot == InputSlot.Base && sourceQuote.Kind != QuoteKind.Outright)
                    Fail("base_type", "Base input must be an outright price.");
                if (curve.Rule == CurveRule.Add && input.Slot == InputSlot.Base && !SamePriceUnit(output, sourceQuote))
                    Fail("unit_mismatch", "Addition requires the exact same price unit.");
                if (input.Mappings.IsDefault || input.Mappings.Length != curve.Constituents.Length ||
                    input.Mappings.Select(m => m.Target).Distinct().Count() != curve.Constituents.Length)
                    Fail("mapping_coverage", "Every target requires exactly one explicit source mapping per slot.");
                foreach (var map in input.Mappings)
                {
                    if (!curveMembers[curve.Key].Contains(map.Target) || !curveMembers[source.Key].Contains(map.Source))
                        Fail("mapping_member", "Mapped constituents must belong to the named curves.");
                    var targetContract = contracts[map.Target]; var sourceContract = contracts[map.Source];
                    if (targetContract.DeliveryFrom != sourceContract.DeliveryFrom || targetContract.DeliveryTo != sourceContract.DeliveryTo)
                        Fail("unsupported_mapping", "First subset supports explicitly mapped equal delivery periods only.");
                    if (input.ExpiryRule == SourceExpiryRule.FreezeFinalFuture &&
                        (source.Rule != CurveRule.Direct || sourceContract.Kind != ContractKind.Future))
                        Fail("unsupported_expiry", "Frozen-final source must be a directly observed future.");
                }
            }
            if (curve.Rule == CurveRule.ScaleAdd)
            {
                var input = curve.Multiplier ?? throw new MarketDataException("missing_multiplier", "Explicit typed multiplier input is required.");
                var spec = NumericSpecification(input);
                var source = Quote(curves[curve.Inputs.Single(x => x.Slot == InputSlot.Base).Source]);
                if (source.Currency != output.Currency || source.Multiplier != output.Multiplier ||
                    spec.Kind == NumericKind.QuantityRatio && (source.Quantity != spec.NumeratorQuantity || output.Quantity != spec.DenominatorQuantity) ||
                    spec.Kind == NumericKind.Dimensionless && source.Quantity != output.Quantity)
                    Fail("multiplier_type", "Multiplier must map exact input/output quantities and compatible currencies.");
            }
            else if (curve.Multiplier is not null) Fail("unexpected_multiplier", "Multiplier requires the scale-add rule.");
        }
        foreach (var group in curves.Values.SelectMany(c => c.Constituents.Select(k => (Contract: k, Curve: c))).GroupBy(x => x.Contract))
        {
            var first = group.First().Curve;
            foreach (var membership in group)
                if (membership.Curve.Location != first.Location || membership.Curve.DeliveryProfile != first.DeliveryProfile ||
                    !SamePriceUnit(Quote(membership.Curve), Quote(first)))
                    Fail("incompatible_membership", "A dated contract cannot change quantity, currency, location or profile between curves.");
        }
        var done = new HashSet<CurveKey>(); var visiting = new HashSet<CurveKey>();
        foreach (var key in curves.Keys) Visit(key);
        void Visit(CurveKey key)
        {
            if (done.Contains(key)) return;
            if (!visiting.Add(key)) Fail("dependency_cycle", "Curve inputs contain a cycle.");
            foreach (var input in curves[key].Inputs) Visit(input.Source);
            visiting.Remove(key); done.Add(key);
        }
    }

    private static void Domain(ExactDecimal value, ExactDecimal minimum, ExactDecimal maximum)
    {
        if (value.CompareTo(minimum) < 0 || value.CompareTo(maximum) > 0) Fail("price_domain", "Value is outside its declared domain.");
    }
    private ValueSpecification NumericSpecification(NumericInput input)
    {
        ReferenceKey key;
        if (input.Series is { } id)
        {
            if (input.Constant is not null || input.ConstantSpecification is not null || input.Role != ObservationRole.Parameter || input.Source is null ||
                !series.TryGetValue(id, out var definition) || definition.ValueSpecification is null)
                throw new MarketDataException("numeric_input", "A numeric series binding requires exact role/source and no constant.");
            Ref(input.Source.Value); key = definition.ValueSpecification.Value;
        }
        else
        {
            if (input.Constant is null || input.ConstantSpecification is null || input.Role is not null || input.Source is not null)
                throw new MarketDataException("numeric_input", "A constant requires explicit type/value and no series source/role.");
            key = input.ConstantSpecification.Value;
        }
        if (!valueSpecs.TryGetValue(key, out var spec)) throw new MarketDataException("missing_reference", "Numeric value specification is absent.");
        if (input.Constant is { } value) Domain(value, spec.Minimum, spec.Maximum);
        return spec;
    }

    public CurveBuild Build(MarketSnapshot snapshot, ImmutableArray<CurveRequest> requests,CancellationToken cancellation=default)
        => Build(snapshot,requests,null,[],cancellation);
    private CurveBuild Build(MarketSnapshot snapshot,ImmutableArray<CurveRequest> requests,Risk.QuoteShock? shock,
        ImmutableArray<CurveEvaluation> reusable,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        EnumValue(snapshot.SelectionPolicy);
        if (snapshot.Id == Guid.Empty || snapshot.Definitions != revision)
            Fail("snapshot_pin", "Snapshot must name this exact definition revision.");
        if (snapshot.ValuationAt.Offset != TimeSpan.Zero || snapshot.MarketCutoff.Offset != TimeSpan.Zero ||
            snapshot.MarketCutoff > snapshot.ValuationAt || snapshot.ValuationAt >= End || snapshot.Selections.IsDefault)
            Fail("snapshot_context", "Explicit UTC valuation/cutoff context and selected inputs are required.");
        if (requests.IsDefault || requests.Distinct().Count() != requests.Length)
            Fail("request", "Supply distinct explicit output requests.");
        var inputs = Unique(snapshot.Selections, s => s.Input);
        foreach (var group in inputs.Values.GroupBy(s => s.Observation.Key))
            if (group.Select(s => s.Observation).Distinct().Count() != 1)
                Fail("observation_conflict", "An observation identity cannot name different contents.");
        var memo = new Dictionary<(CurveRequest, InputUse), BuiltValue>();
        var evaluations = ImmutableArray.CreateBuilder<CurveEvaluation>();
        foreach(var evaluation in reusable)
        {memo.Add((evaluation.Result.Request,evaluation.Use),evaluation.Result);evaluations.Add(evaluation);}
        foreach (var selected in inputs.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            EnumValue(selected.Input.Use);
            var obs = selected.Observation;
            EnumValue(obs.Role); EnumValue(obs.State); Ref(obs.Source);
            if (obs.Role == ObservationRole.IndexFixing && selected.Input.Use != InputUse.FrozenFact)
                Fail("index_fixing_use", "Index actuals are immutable frozen facts, never variable curve inputs.");
            if (!series.TryGetValue(obs.Series, out var definition) || selected.Input.Series != obs.Series ||
                obs.Key.CatalogId != revision.CatalogId || obs.Key.Id == Guid.Empty)
                Fail("observation_type", "Observation identity/type does not match the selected series.");
            if (obs.ObservedAt.Offset != TimeSpan.Zero || obs.ObservedAt > snapshot.MarketCutoff)
                Fail("observation_cutoff", "Observation must be UTC and no later than market cutoff.");
            if (definition.Quote is { } quoteKey)
            {
                if (obs.Role == ObservationRole.Parameter) Fail("series_type", "Price series cannot use numeric parameter observations.");
                var q = quotes[quoteKey];
                Domain(obs.Value, q.Minimum, q.Maximum);
                if (obs.Role is ObservationRole.DailySettlement or ObservationRole.FinalContractSettlement && !obs.Value.IsMultipleOf(q.ClearingIncrement))
                    Fail("clearing_precision", "Settlement observation violates its explicit clearing increment.");
            }
            else
            {
                var spec = valueSpecs[definition.ValueSpecification!.Value]; Domain(obs.Value, spec.Minimum, spec.Maximum);
                if (obs.Role != ObservationRole.Parameter || selected.Input.Use != InputUse.Variable)
                    Fail("series_type", "Numeric inputs require the explicit parameter observation role and variable use.");
            }
            if (selected.Input.Use == InputUse.FrozenFact &&
                (obs.Role is not (ObservationRole.FinalContractSettlement or ObservationRole.IndexFixing) || obs.State != PublicationState.Final))
                Fail("frozen_fact", "Frozen inputs require final contract settlement or an explicit final index fixing.");
        }
        var result = requests.Select(r => Evaluate(r, InputUse.Variable)).ToImmutableArray();
        return new CurveBuild(this, snapshot, NumericalPolicy, result, inputs, memo.Count-reusable.Length, evaluations.ToImmutable(),reusable.Length,shock);

        BuiltValue Evaluate(CurveRequest request, InputUse use)
        {
            cancellation.ThrowIfCancellationRequested();
            if (memo.TryGetValue((request, use), out var cached)) return cached;
            if (!curves.TryGetValue(request.Curve, out var curve) || !curveMembers[curve.Key].Contains(request.Contract))
                throw new MarketDataException("request_member", "Requested curve constituent is absent.");
            var contract = contracts[request.Contract];
            if (use == InputUse.Variable && snapshot.ValuationAt >= contract.Expiry)
                throw new MarketDataException("source_expired", "A live constituent expired; an explicit supported transition is required.");
            var quote = Quote(curve);
            BuiltValue value;
            var dependencies = ImmutableArray.CreateBuilder<CurveEvaluationInput>();
            NumericValue? appliedMultiplier = null;
            ObservationKey? multiplierObservation = null;
            if (curve.Rule == CurveRule.Direct)
            {
                var key = new InputKey(priceSeries[(request.Contract, curve.Quote)], use);
                if (!inputs.TryGetValue(key, out var selection))
                    throw new MarketDataException("missing_observation", "Exact required variable/frozen input is absent.");
                var obs = selection.Observation;
                var expectedRole = use == InputUse.FrozenFact ? ObservationRole.FinalContractSettlement : curve.DirectRole;
                if (obs.Source != curve.DirectSource || obs.Role != expectedRole || obs.State != PublicationState.Final)
                    throw new MarketDataException("selection_mismatch", "Selected quote, source, role or finality is incompatible.");
                if (use == InputUse.FrozenFact && (contract.Kind != ContractKind.Future || obs.ObservedAt < contract.Expiry))
                    throw new MarketDataException("frozen_fact", "Final future settlement must be observed at or after its explicit expiry.");
                var scenarioValue=shock is not null && key==shock.Bump.Input ? obs.Value+shock.AppliedSize : obs.Value;
                value = new BuiltValue(request, quote, new UnitPrice(scenarioValue, quote), ImmutableHashSet.Create(obs.Key));
            }
            else
            {
                if (use != InputUse.Variable) throw new MarketDataException("unsupported_expiry", "Composite final settlements are not implemented.");
                BuiltValue Read(InputSlot slot)
                {
                    var input = curve.Inputs.Single(x => x.Slot == slot);
                    var sourceKey = mappingSources[(request.Curve, slot, request.Contract)];
                    var source = contracts[sourceKey];
                    var sourceUse = snapshot.ValuationAt < source.Expiry ? InputUse.Variable :
                        input.ExpiryRule == SourceExpiryRule.FreezeFinalFuture ? InputUse.FrozenFact :
                        throw new MarketDataException("source_expired", "Source expired and its policy requires a live value.");
                    var sourceRequest = new CurveRequest(input.Source, sourceKey);
                    dependencies.Add(new(slot, sourceRequest, sourceUse));
                    return Evaluate(sourceRequest, sourceUse);
                }
                var baseValue = Read(InputSlot.Base); var spread = Read(InputSlot.Spread);
                var price = baseValue.Price;
                var lineage = baseValue.Lineage.Union(spread.Lineage);
                if (curve.Rule == CurveRule.ScaleAdd)
                {
                    var binding = curve.Multiplier!;
                    ExactDecimal factor;
                    if (binding.Series is { } numericSeries)
                    {
                        if (!inputs.TryGetValue(new(numericSeries, InputUse.Variable), out var selected))
                            throw new MarketDataException("missing_observation", "Explicit multiplier series observation is absent.");
                        var observation = selected.Observation;
                        if (observation.Source != binding.Source || observation.Role != binding.Role || observation.State != PublicationState.Final)
                            throw new MarketDataException("selection_mismatch", "Multiplier source, role or finality does not match.");
                        factor = observation.Value; lineage = lineage.Add(observation.Key); multiplierObservation = observation.Key;
                    }
                    else factor = binding.Constant!.Value;
                    appliedMultiplier = new NumericValue(factor, NumericSpecification(binding));
                    price = price.Times(appliedMultiplier);
                }
                value = new BuiltValue(request, quote, price.Add(spread.Price), lineage);
            }
            memo.Add((request, use), value);
            evaluations.Add(new(value, use, dependencies.ToImmutable(), appliedMultiplier, multiplierObservation));
            return value;
        }
    }
}
