using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Calculation;

namespace OpenEtrm.MarketData.Risk;

public sealed record LifecycleProjectionPolicy(string Implementation,string PricePath,string CashDisposition,string CashAccumulation,string NumericalPolicy,string NonPriceInputs,
    [property:System.Text.Json.Serialization.JsonRequired]string InputExpiry)
{
    public const string Version="economic_lifecycle_projection_v1",Hold="shock_then_hold_native_quotes_v1",
        Linear="elapsed_linear_native_quotes_18_to_even_v1",PayAtDue="project_scheduled_cash_paid_at_due_v1",
        Outstanding="retain_scheduled_cash_outstanding_v1",Nominal="nominal_currency_cash_v1",
        Accumulated="captured_currency_curve_exact_cash_accumulation_v1",Numerics="exact_residual_plus_horizon_cash_less_opening_v1",
        HeldNonPrice="captured_rates_and_volatility_held_v1",DeclaredCurveExpiry="declared_curve_expiry_policy_v1",
        FreezeNativeExpiry="freeze_selected_native_mark_at_expiry_v1";
}
// The source adapter supplies the complete selected session's versioned close
// schedule. The pure calculation selects its first close after the opening.
public sealed record LifecycleProjectionSession(Guid CatalogId,string Code,int Version);
public sealed record LifecycleProjectionHorizon(LifecycleProjectionSession Session,ReferenceKey Calendar,
    ImmutableArray<RiskHistoryResolvedClose> Closes);
public sealed record LifecycleProjectionEvent(Guid Id,DateTimeOffset At,ImmutableArray<Guid> Requires,string Kind)
{
    // Configuration occurrence IDs may be reused by independent held owners.
    public static Guid ScopedId(AccountingPositionKey owner,Guid occurrence)
    {
        if(!owner.IsValid||occurrence==Guid.Empty)throw LifecycleProjection.Error("projection_events","A projected event requires its exact native owner and occurrence.");
        return ContractIdentity.FromCanonical($"lifecycle_event_v1:{(int)owner.Owner.Kind}:{owner.Owner.Id:D}:{owner.BindingId:D}:{owner.BookId:D}:{occurrence:D}");
    }
}
public sealed record ProjectedFixingKey(CurveRequest Market,ObservationRole Role,DateTimeOffset At);
// Deliberately no observation identity, accepted knowledge or publication. This
// value cannot enter an actual settlement command or source reader.
public sealed record ProjectedFixing(ProjectedFixingKey Key,ExactUnitPrice Price);
public sealed record KnownLifecycleFixing(ProjectedFixingKey Key,SelectedObservation Evidence);
// Source-known native final evidence is separate from a path-local fixing.
public sealed record KnownLifecycleInputFinal(CurveRequest OfficialMarket,MarketObservation Observation,DateTimeOffset KnownAt,Guid SourceContextId);
public sealed record LifecycleInputFinal(CurveRequest Market,DateTimeOffset At,KnownLifecycleInputFinal? Actual);
public sealed record LifecycleMarketUse(CurveRequest Market,DateTimeOffset Through);
public sealed record ProjectedCashContribution(Guid Id,DateTimeOffset EffectiveAt,DateTimeOffset DueAt,ExactMoney Amount);
public sealed record LifecycleProjectionState(ImmutableArray<ValuationPortfolio> Residuals,ImmutableArray<ProjectedCashContribution> Cash)
{
    // One qualified connected component may update several original economic
    // owners atomically. Only the exact top aggregates are valued, once.
    public ImmutableDictionary<AccountingPositionKey,LifecycleProjectionState> Members{get;init;}=ImmutableDictionary<AccountingPositionKey,LifecycleProjectionState>.Empty;
    public ImmutableArray<AccountingFlow> KnownPayments{get;init;}=[];
    // Native memberships created by a known future event are source evidence,
    // not active opening positions. Their payments remain immutable actuals.
    public ImmutableArray<RecordedCashValuation> KnownCashSources{get;init;}=[];
    public ImmutableDictionary<ProjectedFixingKey,ProjectedFixing> ProjectedFacts{get;init;}=ImmutableDictionary<ProjectedFixingKey,ProjectedFixing>.Empty;
    // Populated by the qualified opening-state adapter from committed facts.
    // A scheduled date alone never satisfies an event prerequisite.
    public ImmutableHashSet<Guid> CompletedEvents{get;init;}=ImmutableHashSet<Guid>.Empty;
    public static LifecycleProjectionState Compose(ImmutableDictionary<AccountingPositionKey,LifecycleProjectionState> members)
    {
        if(members is null||members.IsEmpty||members.Keys.Select(o=>o.BookId).Distinct().Count()!=1||members.Any(p=>!p.Key.IsValid||p.Value is null||
            p.Value.Members is null||!p.Value.Members.IsEmpty||p.Value.Residuals.IsDefault||p.Value.Cash.IsDefault||p.Value.KnownPayments.IsDefault||
            p.Value.KnownCashSources.IsDefault||p.Value.CompletedEvents is null))
            throw LifecycleProjection.Error("projection_composition","Declare one flat immutable set of exact economic members for this connected component.");
        var ordered=Order(members);
        return new([..ordered.SelectMany(p=>p.Value.Residuals)],[..ordered.SelectMany(p=>p.Value.Cash)]){
            Members=members,KnownPayments=[..ordered.SelectMany(p=>p.Value.KnownPayments)],
            KnownCashSources=[..ordered.SelectMany(p=>p.Value.KnownCashSources)],CompletedEvents=ordered.SelectMany(p=>p.Value.CompletedEvents).ToImmutableHashSet()};
    }
    internal static KeyValuePair<AccountingPositionKey,LifecycleProjectionState>[] Order(ImmutableDictionary<AccountingPositionKey,LifecycleProjectionState> members)=>
        [..members.OrderBy(p=>p.Key.Owner.Kind).ThenBy(p=>p.Key.Owner.Id).ThenBy(p=>p.Key.BindingId).ThenBy(p=>p.Key.BookId)];
}
public interface ILifecycleProjection
{
    string Implementation{get;}
    AccountingPositionKey Owner{get;}
    ImmutableArray<AccountingPositionKey> EconomicOwners=>[Owner];
    ReferenceKey Currency{get;}
    MarketSnapshot SourceSnapshot{get;}
    // Complete transition and every possible residual branch, before sampling.
    ImmutableArray<CurveRequest> Dependencies{get;}
    LifecycleProjectionState Opening{get;}
    ImmutableArray<LifecycleInputFinal> InputFinals=>[];
    ImmutableArray<LifecycleMarketUse> MarketUsesFor(DateTimeOffset horizon)=>[..Dependencies.Select(m=>new LifecycleMarketUse(m,horizon))];
    ImmutableArray<LifecycleProjectionEvent> Events{get;}
    ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon);
    LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context);
}
public sealed record LifecycleProjectionValue(AccountingPositionKey Owner,ReferenceKey Currency,ExactMoney OpeningValue,
    ExactMoney ResidualValue,ExactMoney PeriodCash,ExactMoney EconomicPnl);
public sealed record LifecycleProjectionResult(string Id,DateTimeOffset Horizon,ImmutableArray<LifecycleProjectionValue> Values,
    ImmutableArray<ProjectedFixing> Fixings,ImmutableArray<LifecycleProjectionState> States);

public sealed class LifecycleProjectionContext
{
    private readonly Func<DateTimeOffset,PreparedValuationEnvironment> market;
    private readonly ImmutableDictionary<CurveRequest,DateTimeOffset> permitted;
    private readonly Dictionary<ProjectedFixingKey,ProjectedFixing> projected;
    private readonly ImmutableDictionary<ProjectedFixingKey,ExactUnitPrice> known;
    private readonly Action<ProjectedFixing> freezeFinal;
    public MarketSnapshot SourceSnapshot{get;}
    public DateTimeOffset CalculationAt{get;internal set;}
    public DateTimeOffset Horizon{get;}
    public LifecycleProjectionPolicy Policy{get;}
    internal LifecycleProjectionContext(MarketSnapshot source,DateTimeOffset horizon,LifecycleProjectionPolicy policy,
        Func<DateTimeOffset,PreparedValuationEnvironment> market,ImmutableDictionary<CurveRequest,DateTimeOffset> permitted,
        ImmutableDictionary<ProjectedFixingKey,ExactUnitPrice> known,Dictionary<ProjectedFixingKey,ProjectedFixing> projected,Action<ProjectedFixing> freezeFinal)
    {SourceSnapshot=source;Horizon=horizon;Policy=policy;this.market=market;this.permitted=permitted;this.known=known;this.projected=projected;this.freezeFinal=freezeFinal;CalculationAt=source.ValuationAt;}
    public ExactUnitPrice Price(CurveRequest request)
    {
        if(!permitted.TryGetValue(request,out var through)||CalculationAt>through)throw LifecycleProjection.Error("projection_dependency","The transition requested an undeclared market input.");
        return market(CalculationAt).Price(request,default);
    }
    public ProjectedFixing Fix(ProjectedFixingKey key)
    {
        if(key is null||!TradingInstant.IsSupported(key.At)||!Enum.IsDefined(key.Role)||!permitted.TryGetValue(key.Market,out var through)||key.At>through)
            throw LifecycleProjection.Error("projection_fixing","Select an exact declared fixing market, role and event instant.");
        if(key.At>CalculationAt)throw LifecycleProjection.Error("projection_fixing","A fixing cannot be consumed before its effective event instant.");
        if(known.TryGetValue(key,out var actual))return new(key,actual);
        if(key.At<=SourceSnapshot.ValuationAt)throw LifecycleProjection.Error("projection_fixing","A source-known fixing requires captured actual evidence; it cannot be projected.");
        if(!projected.TryGetValue(key,out var value))
        {
            if(key.At!=CalculationAt)throw LifecycleProjection.Error("projection_fixing","Create each new path-local fixing at its declared event instant before later consumers read it.");
            value=new(key,market(key.At).Price(key.Market,default));projected.Add(key,value);
            if(key.Role==ObservationRole.FinalContractSettlement)freezeFinal(value);
        }
        return value;
    }
}

public static class LifecycleProjection
{
    public static PreparedLifecycleProjection Prepare(CurveModel model,MarketSnapshot source,LifecycleProjectionHorizon horizon,
        LifecycleProjectionPolicy policy,ImmutableArray<ILifecycleProjection> components,
        ImmutableArray<KnownLifecycleFixing> knownFixings,
        ImmutableDictionary<ReferenceKey,ISideDiscounting>? cashCurves=null,CancellationToken cancellation=default,ImmutableArray<LifecycleInputFinal> inputFinals=default)
        =>Create(model,source,horizon,policy,components,knownFixings,cashCurves,cancellation,null,null,null,inputFinals);
    public static PreparedLifecycleProjection Prepare(ConnectedValuation valuation,ImmutableDictionary<CurveRequest,CurvePointKey> bindings,
        CurveBuild authority,LifecycleProjectionHorizon horizon,LifecycleProjectionPolicy policy,ImmutableArray<ILifecycleProjection> components,
        ImmutableArray<KnownLifecycleFixing> knownFixings,ImmutableDictionary<ReferenceKey,ISideDiscounting>? cashCurves=null,CancellationToken cancellation=default,ImmutableArray<LifecycleInputFinal> inputFinals=default)
        =>Create(authority.Model,valuation.Snapshot,horizon,policy,components,knownFixings,cashCurves,cancellation,valuation,bindings,authority,inputFinals);
    private static PreparedLifecycleProjection Create(CurveModel model,MarketSnapshot source,LifecycleProjectionHorizon horizon,
        LifecycleProjectionPolicy policy,ImmutableArray<ILifecycleProjection> components,ImmutableArray<KnownLifecycleFixing> knownFixings,
        ImmutableDictionary<ReferenceKey,ISideDiscounting>? cashCurves,CancellationToken cancellation,ConnectedValuation? connected,
        ImmutableDictionary<CurveRequest,CurvePointKey>? bindings,CurveBuild? authority,ImmutableArray<LifecycleInputFinal> inputFinals)
    {
        Validate(policy);
        if(source is null||source.Knowledge is null||!TradingInstant.IsSupported(source.ValuationAt)||!source.HasCoherentSelections()||
            horizon is null||horizon.Session is null||horizon.Session.CatalogId==Guid.Empty||string.IsNullOrWhiteSpace(horizon.Session.Code)||
            horizon.Session.Version<1||horizon.Closes.IsDefaultOrEmpty||
            horizon.Closes.Any(c=>c is null||!TradingInstant.IsSupported(c.Instant))||
            horizon.Closes.Select(c=>c.Instant).Distinct().Count()!=horizon.Closes.Length||
            !horizon.Closes.SequenceEqual(horizon.Closes.OrderBy(c=>c.Instant)))
            throw Error("projection_horizon","Supply the certified source and complete ordered selected-session close schedule.");
        Money.RequireKey(horizon.Calendar);
        var next=horizon.Closes.FirstOrDefault(c=>c.Instant>source.ValuationAt)?.Instant??
            throw Error("projection_horizon","The selected session has no configured next close.");
        if(components.IsDefaultOrEmpty||components.Any(c=>c is null||string.IsNullOrWhiteSpace(c.Implementation)||
            !c.Owner.IsValid||c.Dependencies.IsDefault||
            c.Dependencies.Distinct().Count()!=c.Dependencies.Length||c.Events.IsDefault)||
            components.Select(c=>c.Owner).Distinct().Count()!=components.Length||components.Any(c=>c.EconomicOwners.IsDefaultOrEmpty||
                !c.EconomicOwners.Contains(c.Owner)||c.EconomicOwners.Any(o=>!o.IsValid||o.BookId!=c.Owner.BookId))||
            components.SelectMany(c=>c.EconomicOwners).Distinct().Count()!=components.Sum(c=>c.EconomicOwners.Length))
            throw Error("projection_scope","Supply complete distinct native economic owners and qualified projection handlers.");
        foreach(var component in components)Money.RequireKey(component.Currency);
        if(components.Any(c=>c.SourceSnapshot is null||!c.SourceSnapshot.HasSameInputs(source)))
            throw Error("projection_source","Every projection handler must retain this exact source certificate, inputs and opening clock.");
        // Actual fixing admission is exact captured evidence, never a forecast.
        if(knownFixings.IsDefault||knownFixings.Any(f=>f is null||f.Key is null||f.Evidence is null||f.Evidence.Input.Use!=InputUse.FrozenFact||
            !source.Selections.Contains(f.Evidence)||f.Evidence.Observation.State!=PublicationState.Final||f.Evidence.Observation.ObservedAt>source.ValuationAt))
            throw Error("projection_fixing","Actual fixing inputs must be captured final frozen observations.");
        var requests=components.SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
        var basis=connected is null?model.Build(source,requests,cancellation):authority!;
        var known=ImmutableDictionary.CreateBuilder<ProjectedFixingKey,ExactUnitPrice>();
        foreach(var actual in knownFixings)
        {
            var key=actual.Key;var selected=actual.Evidence;var curve=model.AssetDefinition(key.Market.Curve);
            if(!requests.Contains(key.Market)||curve.Rule!=CurveRule.Direct||curve.DirectSource!=selected.Observation.Source||
                curve.DirectRole!=key.Role||key.Role!=selected.Observation.Role||key.At!=selected.Observation.ObservedAt||
                !model.RequiredSeries([key.Market]).SetEquals([selected.Input.Series]))
                throw Error("projection_fixing","An actual fixing requires its exact direct market, native series, source, role and occurrence.");
            if(!known.TryAdd(key,ExactUnitPrice.FromPrice(new(selected.Observation.Value,model.QuoteFor(key.Market)))))
                throw Error("projection_fixing","An actual fixing has conflicting captured evidence.");
        }
        var curves=cashCurves??ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty;
        return connected is null?new(basis,next,policy,components,known.ToImmutable(),curves,cancellation,inputFinals):
            new(connected,bindings!,authority!,next,policy,components,known.ToImmutable(),curves,cancellation,inputFinals);
    }
    public static LifecycleInputExpiryTreatment ExpiryTreatment(LifecycleProjectionPolicy policy)=>policy.InputExpiry switch{
        LifecycleProjectionPolicy.DeclaredCurveExpiry=>LifecycleInputExpiryTreatment.DeclaredCurvePolicy,
        LifecycleProjectionPolicy.FreezeNativeExpiry=>LifecycleInputExpiryTreatment.FreezeSelectedNativeMark,
        _=>throw Error("projection_policy","Select an explicit supported input-expiry treatment.")};
    public static void Validate(LifecycleProjectionPolicy p)
    {
        if(p is null||p.Implementation!=LifecycleProjectionPolicy.Version||p.PricePath is not(LifecycleProjectionPolicy.Hold or LifecycleProjectionPolicy.Linear)||
            p.CashDisposition is not(LifecycleProjectionPolicy.PayAtDue or LifecycleProjectionPolicy.Outstanding)||
            p.CashAccumulation is not(LifecycleProjectionPolicy.Nominal or LifecycleProjectionPolicy.Accumulated)||p.NumericalPolicy!=LifecycleProjectionPolicy.Numerics||
            p.NonPriceInputs!=LifecycleProjectionPolicy.HeldNonPrice||p.InputExpiry is not(LifecycleProjectionPolicy.DeclaredCurveExpiry or LifecycleProjectionPolicy.FreezeNativeExpiry))
            throw Error("projection_policy","Select every supported versioned path, cash disposition, accumulation and numerical policy explicitly.");
    }
    internal static MarketDataException Error(string code,string message)=>new(code,message);
}
