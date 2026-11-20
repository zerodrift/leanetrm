using System.Collections.Immutable;
using System.Globalization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record OperatingPortfolioInput(PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot,
    ValuationPortfolio Portfolio,ImmutableArray<PortfolioPositionEvidence> Evidence);

internal static class OperatingPortfolio
{
    internal static async Task<ImmutableArray<OperatingPortfolioInput>> Prepare(NpgsqlConnection connection,
        PreviewDatabase database,InputFrame frame,ImmutableArray<OperatingPosition> positions,
        ImmutableArray<(PreviewData Data,InputFrame Frame)> supplied,bool requirePinnedSources,CancellationToken ct)
    {
        if(positions.IsDefault||supplied.IsDefault||frame.ContextId is not {} contextId)
            throw Invalid("Select exact operating holdings and admitted sources under a certified context.");
        if(positions.IsEmpty)return [];
        var context=PinnedContext.Exact((await new KnowledgeResolver(database.Manifest.CatalogId).Read(connection,contextId,ct)).Context);
        var result=ImmutableArray.CreateBuilder<OperatingPortfolioInput>();var retained=new RetainedScheduleStore(database);
        foreach(var group in positions.GroupBy(p=>p.Definition).OrderBy(g=>g.Key))
        {
            var references=group.Select(p=>p.Reference).Distinct().ToArray();if(references.Length!=1)throw Invalid("One held definition requires one exact reference revision.");
            var source=await MarketInputSelection.Resolve(database,connection,frame,new(group.Key,references[0].CatalogId,references[0].RevisionId),supplied,requirePinnedSources,ct);
            MarketInputSelection.Require(source,frame,new(group.Key,references[0].CatalogId,references[0].RevisionId));
            var snapshot=MarketSnapshotBinding.Bind(source.Data.Snapshot(source.Frame),contextId,SchedulePreview.ParseAt(context.KnownAt));
            var instruments=new Dictionary<Guid,RetainedOperatingInstrument>();var trades=ImmutableArray.CreateBuilder<Trade>();
            var evidence=ImmutableArray.CreateBuilder<PortfolioPositionEvidence>();
            foreach(var position in group)
            {
                if(!instruments.TryGetValue(position.Binding,out var instrument))
                {
                    var programs=await retained.LoadPrograms(connection,position.Binding,contextId,frame.At,ct);
                    var members=position.Head.GetProperty("members").EnumerateArray().ToArray();
                    if(programs.IsDefaultOrEmpty||programs.Length!=members.Length||!programs.Select(p=>p.Slot).Order().SequenceEqual(Enumerable.Range(0,programs.Length)))
                        throw Invalid("An operating holding requires every independently retained program.");
                    var selected=programs.OrderBy(p=>p.Slot).Select(p=>
                    {
                        var member=members.Single(m=>int.Parse(m.Text("slot"),CultureInfo.InvariantCulture)==p.Slot);
                        if(member.Id("sideId")!=p.SideId)throw Invalid("A retained operating Side must match its owned membership.");
                        return new RetainedOperatingMember(p.SideId,ExactDecimal.Parse(member.Text("coefficient")),p.Program);
                    }).ToImmutableArray();
                    instrument=new(position.Binding,new(new(database.Manifest.CatalogId,group.Key),position.Physical.Text("facility_code")),
                        new(ExactDecimal.Parse("1"),position.Quantity),position.Currency,selected);instruments.Add(position.Binding,instrument);
                }
                var quantity=instrument.UnitQuantity;var book=position.Book.Id("id");trades.Add(new(position.Trade,book,[new(position.Binding,instrument,quantity)]));
                evidence.Add(new(position.Trade,int.Parse(position.Head.Text("revision"),CultureInfo.InvariantCulture),position.Binding,position.Binding,position.Binding,book,
                    OperatingPositions.Measure,group.Key,int.Parse(position.Head.Text("publication_order"),CultureInfo.InvariantCulture),quantity)
                    {TradeReference=TradingReference.Trade(position.Head),InstrumentReference=TradingReference.Instrument(position.Head),InstrumentType=OperatingPositions.Type});
            }
            var portfolio=ValuationPortfolio.FromTrades(trades.ToImmutable(),new(ValuationMeasure.PresentValue,
                ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
            result.Add(new(source.Data,source.Frame,snapshot,portfolio,evidence.ToImmutable()));
        }
        return result.ToImmutable();
    }
    internal static Task<CustomCurves.PreparedMarket> PrepareMarket(NpgsqlConnection connection,PreviewDatabase database,
        OperatingPortfolioInput group,ScheduleContext context,CancellationToken ct)
    {
        var dependencies=group.Portfolio.Declare(group.Snapshot).SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
        return CustomCurves.PrepareMarket(connection,database,group.Data,group.Frame,group.Snapshot,context,
            group.Portfolio.Instruments,dependencies,CustomCurves.MarketPreparation.ValuationDependencies,ct,activePrices:[]);
    }
    internal static async Task<ImmutableDictionary<Guid,ExactMoney>> Values(NpgsqlConnection connection,PreviewDatabase database,
        InputFrame frame,ImmutableArray<OperatingPosition> positions,CancellationToken ct)
    {
        var groups=await Prepare(connection,database,frame,positions,[],false,ct);var values=ImmutableDictionary.CreateBuilder<Guid,ExactMoney>();
        if(groups.IsEmpty)return values.ToImmutable();
        var context=PinnedContext.Exact((await new KnowledgeResolver(database.Manifest.CatalogId).Read(connection,frame.ContextId!.Value,ct)).Context);
        foreach(var group in groups)
        {
            var market=await PrepareMarket(connection,database,group,context,ct);
            var prepared=RiskEngine.PrepareConnected(market.Valuation,market.Bindings,[group.Portfolio],market.Authority,ct);
            var run=RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy,ct,RiskDetail.Holdings);
            foreach(var value in run.Groups.Single().BasePositions)values.Add(value.BindingId,value.Value);
        }
        return values.ToImmutable();
    }
    private static MarketDataException Invalid(string message)=>new("operating_market_context",message);
}
