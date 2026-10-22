using System.Collections.Immutable;

namespace OpenEtrm.MarketData;

public readonly record struct ReferenceRevision(Guid CatalogId, Guid RevisionId);
public readonly record struct ReferenceKey(ReferenceRevision Revision, string Code);
public readonly record struct DefinitionRevision(Guid CatalogId, Guid RevisionId);
public readonly record struct ContractKey(DefinitionRevision Revision, Guid Id);
public readonly record struct CurveKey(DefinitionRevision Revision, string Code);
public readonly record struct SeriesKey(DefinitionRevision Revision, Guid Id);
public readonly record struct ObservationKey(Guid CatalogId, Guid Id);

public enum ContractKind { Future = 1, Forward }
public enum QuoteKind { Outright = 1, Basis }
public enum CurveRule { Direct = 1, Add, ScaleAdd }
public enum InputSlot { Base = 1, Spread }
public enum SourceExpiryRule { RequireLive = 1, FreezeFinalFuture }
public enum ObservationRole { TraderMark = 1, DailySettlement, FinalContractSettlement, Parameter, IndexFixing }
public enum PublicationState { Preliminary = 1, Final }
public enum InputUse { Variable = 1, FrozenFact }
public enum InputSelectionPolicy { ExactPinnedFinalObservationsNoAgeFiltering = 1 }
public enum SettlementKind { FutureSeries = 1, Index, IndexDifference }
public enum LotBasis { FixedTotal = 1, PerDeliveryDay, PerDeliveryHour }

public sealed record QuantitySpecification(
    ReferenceKey Key, ReferenceKey Commodity, ReferenceKey MeasurementBasis,
    ReferenceKey Unit, ReferenceKey Dimension);

public sealed record QuoteSpecification(
    ReferenceKey Key, QuoteKind Kind, ReferenceKey Currency,
    ReferenceKey Quantity, ExactDecimal Multiplier, ExactDecimal TradingTick,
    ExactDecimal ClearingIncrement, ExactDecimal Minimum, ExactDecimal Maximum);

public sealed record SettlementTarget(
    SettlementKind Kind, ReferenceKey? IndexA, ReferenceKey? IndexB);

// An exact UTC delivery interval with the market clock's operating date and local labels;
// Occurrence tells the two copies of a repeated local hour apart.
public sealed record DeliveryInterval(DateTimeOffset Start, DateTimeOffset End, DateOnly OperatingDate,
    int HourBeginning, int Minute, int Occurrence);

public sealed record MarketContract(
    ContractKey Key, ContractKind Kind, DateOnly DeliveryFrom, DateOnly DeliveryTo,
    DateTimeOffset Expiry, ReferenceKey? FuturesSeries, DeliveryInterval? Interval = null);

// Resolved from the exact dated series/product/milestone reference links. This
// preserves the authoritative specification when constructing an in-memory model.
public sealed record FuturesLotResolution(Quantity Quantity,ExactDecimal Factor,string Implementation);
public sealed record FuturesBasisSettlement(ReferenceKey Index,ReferenceKey BenchmarkSeries,string Implementation);
public sealed record FuturesSeriesSpecification(
    ReferenceKey Key, ReferenceKey QuoteQuantity, ReferenceKey Currency,
    ExactDecimal Multiplier, ExactDecimal TradingTick, ReferenceKey Location,
    ReferenceKey DeliveryProfile, DateOnly DeliveryFrom, DateOnly DeliveryTo,
    DateTimeOffset LastTradingAt, ReferenceKey LotQuantity,
    ExactDecimal ContractSize, LotBasis LotBasis, DateTimeOffset? FirstTradingAt, string SettlementMethod,FuturesLotResolution? LotResolution=null,FuturesBasisSettlement? BasisSettlement=null);

// Identity mapping is the first supported delivery mapping. Explicitly named
// pairs are required even where source and target delivery periods are equal.
public sealed record ConstituentMapping(ContractKey Target, ContractKey Source);
public sealed record CurveInput(
    InputSlot Slot, CurveKey Source, SourceExpiryRule ExpiryRule,
    ImmutableArray<ConstituentMapping> Mappings);

public enum NumericKind { QuantityRatio = 1, Dimensionless }
public sealed record ValueSpecification(
    ReferenceKey Key, NumericKind Kind, ReferenceKey? NumeratorQuantity,
    ReferenceKey? DenominatorQuantity, ExactDecimal Minimum, ExactDecimal Maximum)
{
    internal void Validate()
    {
        Money.RequireKey(Key);
        if (!Enum.IsDefined(Kind)) throw new MarketDataException("unsupported_kind", "Unsupported numeric kind.");
        if (Minimum.CompareTo(Maximum) > 0 ||
            Kind == NumericKind.Dimensionless && (NumeratorQuantity is not null || DenominatorQuantity is not null) ||
            Kind == NumericKind.QuantityRatio && (NumeratorQuantity is null || DenominatorQuantity is null))
            throw new MarketDataException("value_specification", "Numeric domain and explicit numerator/denominator must match the selected kind.");
        foreach (var key in new[] { NumeratorQuantity, DenominatorQuantity }.OfType<ReferenceKey>())
        {
            Money.RequireKey(key);
            if (key.Revision != Key.Revision)
                throw new MarketDataException("value_specification", "Numeric specification and quantity references must share the exact revision.");
        }
    }
}
public sealed record MarketSeries(
    SeriesKey Key, ContractKey? Contract, ReferenceKey? Quote, ReferenceKey? ValueSpecification);
// Exactly one of a dated series or an explicitly typed constant.
public sealed record NumericInput(
    SeriesKey? Series, ReferenceKey? ConstantSpecification, ExactDecimal? Constant,
    ObservationRole? Role, ReferenceKey? Source);

public sealed record Curve(
    CurveKey Key, ReferenceKey Quote, ReferenceKey Location,
    ReferenceKey DeliveryProfile, SettlementTarget Settlement,
    CurveRule Rule, ImmutableArray<ContractKey> Constituents,
    ImmutableArray<CurveInput> Inputs, ObservationRole? DirectRole,
    ReferenceKey? DirectSource, NumericInput? Multiplier);

public sealed record MarketObservation(
    ObservationKey Key, SeriesKey Series,
    ReferenceKey Source, ObservationRole Role, PublicationState State,
    DateTimeOffset ObservedAt, ExactDecimal Value);

public readonly record struct InputKey(SeriesKey Series, InputUse Use);
public sealed record SelectedObservation(InputKey Input, MarketObservation Observation);

// These are explicitly selected inputs, not a "latest" or wall-clock historical
// resolver. Snapshot identities supplied here have no authorization meaning.
public sealed record MarketSnapshot(
    Guid Id, DefinitionRevision Definitions, DateTimeOffset ValuationAt,
    DateTimeOffset MarketCutoff, InputSelectionPolicy SelectionPolicy,
    ImmutableArray<SelectedObservation> Selections)
{
    public bool HasCoherentSelections() => !Selections.IsDefault &&
        Selections.All(s => s is not null && s.Observation is not null) &&
        Selections.Select(s => s.Input).Distinct().Count() == Selections.Length &&
        Selections.GroupBy(s => s.Observation.Key).All(g => g.Select(s => s.Observation).Distinct().Count() == 1);

    public bool HasSameInputs(MarketSnapshot? other) => other is not null &&
        Id == other.Id && Definitions == other.Definitions && ValuationAt == other.ValuationAt && MarketCutoff == other.MarketCutoff &&
        SelectionPolicy == other.SelectionPolicy && !Selections.IsDefault && !other.Selections.IsDefault &&
        Selections.Length == other.Selections.Length && (ReferenceEquals(this, other) || Selections.ToHashSet().SetEquals(other.Selections));
}

public readonly record struct CurveRequest(CurveKey Curve, ContractKey Contract);
public sealed record BuiltValue
{
    public CurveRequest Request { get; }
    public ReferenceKey Quote { get; }
    public UnitPrice Price { get; }
    public ImmutableHashSet<ObservationKey> Lineage { get; }
    public BuiltValue(CurveRequest request, QuoteSpecification quote, UnitPrice price, ImmutableHashSet<ObservationKey> lineage)
    {
        price.RequireQuote(quote);
        Request = request; Quote = quote.Key; Price = price; Lineage = lineage;
    }
}
public sealed record CurveEvaluationInput(InputSlot Slot, CurveRequest Source, InputUse Use);
public sealed record CurveEvaluation(BuiltValue Result, InputUse Use,
    ImmutableArray<CurveEvaluationInput> Inputs, NumericValue? Multiplier, ObservationKey? MultiplierObservation);
public sealed record CurveBuild
{
    internal CurveModel Model { get; }
    internal ImmutableDictionary<InputKey, SelectedObservation> SelectedInputs { get; }
    public MarketSnapshot Snapshot { get; }
    public Guid SnapshotId => Snapshot.Id;
    public DefinitionRevision Definitions => Snapshot.Definitions;
    public string NumericalPolicy { get; }
    public ImmutableArray<BuiltValue> Values { get; }
    public int EvaluatedConstituents { get; }
    public ImmutableArray<CurveEvaluation> Evaluations { get; }
    public int ReusedConstituents { get; }
    public Risk.QuoteShock? Shock { get; }
    public Risk.QuoteScenario Scenario { get; }
    public Calculation.ConnectedScenario Graph { get; }
    internal Calculation.ConnectedCurveExport Compiled { get; }
    internal ImmutableArray<CurveRequest> Requests { get; }
    private readonly Lazy<Calculation.ConnectedValuation> valuation;
    private readonly Lazy<Trading.PreparedValuationMarket> pricing;
    internal Trading.PreparedValuationMarket Pricing=>pricing.Value;
    internal CurveBuild(CurveModel model,MarketSnapshot snapshot,string numericalPolicy,ImmutableArray<BuiltValue> values,
        ImmutableDictionary<InputKey, SelectedObservation> selectedInputs,int evaluatedConstituents,ImmutableArray<CurveEvaluation> evaluations,
        Calculation.ConnectedScenario graph,Calculation.ConnectedCurveExport compiled,ImmutableArray<CurveRequest> requests,CurveBuild? parent,
        int reusedConstituents=0,Risk.QuoteShock? shock=null,
        Risk.QuoteScenario? scenario=null)
    {
        Model=model; Snapshot=snapshot; NumericalPolicy=numericalPolicy; Values=values; SelectedInputs=selectedInputs;
        EvaluatedConstituents=evaluatedConstituents; Evaluations=evaluations;
        ReusedConstituents=reusedConstituents; Shock=shock; Scenario=scenario??Risk.QuoteScenario.Base(snapshot);
        Graph=graph;Compiled=compiled;Requests=requests;
        valuation=new(()=>parent is null?Calculation.ConnectedValuation.Prepare(Graph,Snapshot):parent.valuation.Value.WithScenario(Graph));
        pricing=new(()=>parent is null?valuation.Value.Market(requests.ToImmutableDictionary(r=>r,r=>compiled.Points[(r,InputUse.Variable)]),this,default):
            Calculation.ConnectedValuation.WithScenario(parent.Pricing,valuation.Value));
    }
}
