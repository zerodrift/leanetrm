using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

public readonly record struct IndexFixingKey(Guid CatalogId,Guid Id);

// Index fixing identity is independent of a forward contract or mark series.
public sealed record IndexFixingObservation(IndexFixingKey Key,ReferenceKey Index,ReferenceKey Source,
    QuoteSpecification Quote,DateOnly From,DateOnly To,DateTimeOffset PublishedAt,DateTimeOffset KnownAt,
    PublicationState State,UnitPrice Price,int Revision,IndexFixingKey? Corrects);

[EconomicDefinition("latest_final_index_fixing_requirement_v1")]
public sealed record IndexFixingRequirement(
    [property:IdentityField] ReferenceKey Index,
    [property:IdentityField] QuoteSpecification Quote,
    [property:IdentityField] ReferenceKey Source,
    [property:IdentityField] DateOnly From,
    [property:IdentityField] DateOnly To,
    [property:IdentityField] DateTimeOffset FixingAt,
    [property:IdentityField] string RevisionPolicy)
{
    public const string LatestEligibleFinalByContext="latest_eligible_final_by_context_v1";

    internal void Validate()
    {
        Money.RequireKey(Index);Money.RequireKey(Source);
        if(Quote is null||Index.Revision!=Quote.Key.Revision||Source.Revision!=Index.Revision||From>=To||
            !TradingInstant.IsSupported(FixingAt)||Quote.Kind!=QuoteKind.Outright||RevisionPolicy!=LatestEligibleFinalByContext)
            throw new MarketDataException("index_fixing_rule","Supply exact compatible index, source, outright quote, observation interval and UTC fixing boundary.");
    }
    internal IndexFixingObservation Resolve(MarketSnapshot snapshot)
    {
        Validate();
        if(snapshot.FixingKnowledgeCutoff is not {} known||!TradingInstant.IsSupported(known)||snapshot.IndexFixings.IsDefault)
            throw new MarketDataException("index_fixing_context","Actual fixing resolution requires a resolved knowledge cutoff and explicit pinned fixing inputs.");
        var visible=snapshot.IndexFixings.Where(f=>f.Index==Index&&f.Source==Source&&f.Quote==Quote&&f.From==From&&f.To==To&&f.KnownAt<=known).ToArray();
        if(visible.GroupBy(f=>f.Revision).Any(g=>g.Count()!=1))
            throw new MarketDataException("index_fixing_ambiguous","An index/source/interval revision must have one immutable fixing identity.");
        foreach(var row in visible)
        {
            if(row.Revision<1||row.Revision==1&&row.Corrects is not null||row.PublishedAt>row.KnownAt)
                throw new MarketDataException("index_fixing_context","Fixing revisions require ordered publication/knowledge coordinates and an explicit correction chain.");
            if(row.Revision>1&&(!visible.Any(p=>p.Key==row.Corrects&&p.Revision==row.Revision-1&&p.KnownAt<=row.KnownAt&&p.PublishedAt<=row.PublishedAt)))
                throw new MarketDataException("index_fixing_context","Every fixing correction must retain its preceding revision in the same exact source fact.");
        }
        var matches=visible.Where(f=>f.State==PublicationState.Final&&f.PublishedAt<=snapshot.ValuationAt).OrderByDescending(f=>f.Revision).ToArray();
        if(matches.Length==0)throw new MarketDataException("index_fixing_required","A due observation requires an eligible final fixing from its declared source; a forward mark cannot replace it.");
        var actual=matches[0];
        if(actual.Key.CatalogId!=snapshot.Definitions.CatalogId||actual.Key.Id==Guid.Empty||actual.Price.Unit!=PriceUnit.FromQuote(Quote)||
            !TradingInstant.IsSupported(actual.PublishedAt)||!TradingInstant.IsSupported(actual.KnownAt))
            throw new MarketDataException("index_fixing_context","The retained fixing identity, exact price unit and publication/knowledge coordinates must agree.");
        actual.Price.RequireQuote(Quote);
        return actual;
    }
}

[EconomicDefinition("fixing_aware_floating_side_v1")]
public sealed record FixingAwareFloatingSide : FloatingSide
{
    public new const string Implementation="index_actual_projected_v1";
    [IdentityField] public FloatingSide Projection { get; }
    [IdentityField] public ImmutableArray<IndexFixingRequirement> ResetFixings { get; }
    public override ImmutableArray<CurveRequest> Dependencies=>Projection.Dependencies;
    public override ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot)
    {
        foreach(var due in ResetFixings.Where(r=>r.FixingAt<=snapshot.ValuationAt))_=due.Resolve(snapshot);
        return Projection.Projections.Where((p,n)=>snapshot.ValuationAt<ResetFixings[n].FixingAt).Select(p=>p.Market).Distinct().ToImmutableArray();
    }

    public FixingAwareFloatingSide(FloatingSide projection,ImmutableArray<IndexFixingRequirement> resetFixings,string implementation)
        :base(RequireProjection(projection))
    {
        if(projection is null||implementation!=Implementation||resetFixings.IsDefault||resetFixings.Length!=projection.Projections.Length||resetFixings.Any(r=>r is null))
            throw new MarketDataException("index_fixing_rule","Provide one explicit fixing requirement for every projected observation.");
        foreach(var rule in resetFixings)rule.Validate();
        if(resetFixings.Where((r,n)=>r.Index!=projection.Index||r.Quote!=projection.Quote||r.From!=projection.Projections[n].Contract.DeliveryFrom||
            r.To!=projection.Projections[n].Contract.DeliveryTo||r.FixingAt>=projection.PaymentAt).Any()||resetFixings.Min(r=>r.FixingAt)!=projection.FirstFixingAt)
            throw new MarketDataException("index_fixing_rule","Fixing requirements must match each exact observation, the declared first boundary and the side payment.");
        Projection=projection;ResetFixings=resetFixings;
    }
    private static FloatingSide RequireProjection(FloatingSide? projection)=>projection??throw new MarketDataException("index_fixing_rule","Supply the independent projected side terms.");
    public override SidePaymentSchedule GenerateSchedule()
    {
        var original=Projection.GenerateSchedule().Payments.Single();
        var prices=original.Prices.Select((p,n)=>new SchedulePriceTerm(p.Weight,null,p.Lookup,p.Spread,ResetFixings[n])).ToImmutableArray();
        return new(new(Id,original.DeliveryFrom,original.DeliveryTo,original.PaymentAt,original.Currency,original.UnitQuantity,
            null,original.Averaging,original.Calendar,prices,Implementation,original.GeneratedDate,original.PaymentConvention));
    }
    public override SideValuation PV(SideValuationContext context)=>GenerateSchedule().Evaluate(context);
}
