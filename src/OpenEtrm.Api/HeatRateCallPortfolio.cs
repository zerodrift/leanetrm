using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Power.Structured;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Commands;

namespace OpenEtrm.Api;

internal sealed record HeatRateCallPortfolioInput(PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot,
    ValuationPortfolio Portfolio,ImmutableArray<PortfolioPositionEvidence> Evidence);

// Booked options become HeatRateCallInstruments from their retained rows and the spread marks in force under
// the selected context. A strip whose months are not all marked stays unbound, so its valuation refuses.
internal static class HeatRateCallPortfolio
{
    internal static bool Unpriced(MarketDataException error)=>error.Code is "heat_rate_call_marks" or "heat_rate_call_exercise" or "spread_option_inputs";
    internal static async Task<ImmutableArray<HeatRateCallPortfolioInput>> Prepare(NpgsqlConnection connection,
        PreviewDatabase database,InputFrame frame,ImmutableArray<HeatRateCallPosition> positions,
        ImmutableArray<(PreviewData Data,InputFrame Frame)> supplied,bool requirePinnedSources,CancellationToken ct)
    {
        if(positions.IsDefault||supplied.IsDefault||frame.ContextId is not {} contextId)
            throw Invalid("Select exact option holdings and admitted sources under a certified context.");
        if(positions.IsEmpty)return [];
        var context=PinnedContext.Exact((await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,contextId,ct)).Context);
        var result=ImmutableArray.CreateBuilder<HeatRateCallPortfolioInput>();
        foreach(var group in positions.GroupBy(p=>p.Definition).OrderBy(g=>g.Key))
        {
            var references=group.Select(p=>p.Reference).Distinct().ToArray();if(references.Length!=1)throw Invalid("One held definition requires one exact reference revision.");
            var source=await MarketInputSelection.Resolve(database,connection,frame,new(group.Key,references[0].CatalogId,references[0].RevisionId),supplied,requirePinnedSources,ct);
            MarketInputSelection.Require(source,frame,new(group.Key,references[0].CatalogId,references[0].RevisionId));
            var snapshot=MarketSnapshotBinding.Bind(source.Data.Snapshot(source.Frame),contextId,SchedulePreview.ParseAt(context.KnownAt));
            var marks=new Dictionary<(string Power,string Fuel),ImmutableDictionary<DateOnly,SpreadCallMark>>();
            foreach(var pair in group.GroupBy(p=>(p.PowerCurve,p.FuelCurve)))
                marks.Add(pair.Key,await Marks(connection,database.Manifest.CatalogId,group.Key,pair.Key.PowerCurve,pair.Key.FuelCurve,[..pair.SelectMany(p=>p.Months).Distinct().Order()],ct));
            var trades=ImmutableArray.CreateBuilder<Trade>();var evidence=ImmutableArray.CreateBuilder<PortfolioPositionEvidence>();
            foreach(var position in group)
            {
                var instrument=Instrument(source.Data,position,marks[(position.PowerCurve,position.FuelCurve)]);
                var quantity=new Quantity(position.SignedQuantity,position.Capacity);var book=position.Book.Id("id");
                trades.Add(new(position.Trade,book,[new(position.Binding,instrument,quantity)]));
                evidence.Add(new(position.Trade,int.Parse(position.Head.Text("revision"),CultureInfo.InvariantCulture),position.Binding,position.Binding,position.Binding,book,
                    HeatRateCallPositions.Measure,group.Key,int.Parse(position.Head.Text("publication_order"),CultureInfo.InvariantCulture),quantity)
                    {TradeReference=TradingReference.Trade(position.Head),InstrumentReference=TradingReference.Instrument(position.Head),InstrumentType=HeatRateCallPositions.Type});
            }
            // No discount binding is retained with the option yet: cash is valued undiscounted, stated here rather than assumed downstream.
            var portfolio=ValuationPortfolio.FromTrades(trades.ToImmutable(),new(ValuationMeasure.PresentValue,
                ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
            result.Add(new(source.Data,source.Frame,snapshot,portfolio,evidence.ToImmutable()));
        }
        return result.ToImmutable();
    }
    internal static HeatRateCallInstrument Instrument(PreviewData data,HeatRateCallPosition position,ImmutableDictionary<DateOnly,SpreadCallMark> marks)
    {
        var terms=position.Terms;var revision=data.DefinitionRevision;
        QuoteSpecification Quote(string field)=>data.QuoteRows(terms.Text(field)).SingleOrDefault()??throw Invalid("A held option needs its exact power and fuel quotes.");
        var power=Quote("power_quote_code");var fuel=Quote("fuel_quote_code");
        CurveRequest Request(string curve,JsonElement row,string contract)=>new(new(revision,curve),new(revision,row.Id(contract)));
        var exercises=position.Exercises.EnumerateArray().Select(row=>
        {
            var day=DateWorkspace.Date(row.Text("delivery_day"));
            return new HeatRateCallExercise(day,row.Instant("exercise_at"),row.Instant("payment_at"),ExactDecimal.Parse(row.Text("energy_per_unit")),
                Request(position.PowerCurve,row,"power_contract_id"),Request(position.FuelCurve,row,"fuel_contract_id"),HeatRateCallSchedule.MonthBucket(new(day.Year,day.Month,1)));
        }).ToImmutableArray();
        var retained=new HeatRateCallTerms(position.Capacity,power,fuel,
            new QuantityFactor(ExactDecimal.Parse(terms.Text("heat_rate")),fuel.Quantity,power.Quantity,QuantityFactor.Implementation),
            new UnitPrice(ExactDecimal.Parse(terms.Text("strike")),PriceUnit.FromQuote(power)),terms.Text("exercise_style"),terms.Text("model"),terms.Text("day_count"),exercises);
        var months=position.Months;
        return new(position.Binding,retained,months.All(marks.ContainsKey)?new HeatRateCallMarks([..months.Select(m=>marks[m])],HeatRateCallMarks.HeldLogReturn):null);
    }
    // Each stored mark revision names both volatility inputs, so every option on the pair and month shares them.
    private static async Task<ImmutableDictionary<DateOnly,SpreadCallMark>> Marks(NpgsqlConnection connection,Guid catalog,Guid definition,string power,string fuel,DateOnly[] months,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"SELECT value::text FROM jsonb_array_elements(security.read_spread_marks($1,$2,$3,$4,$5::date[]))",[catalog,definition,power,fuel,months],ct);
        return rows.ToImmutableDictionary(row=>DateOnly.ParseExact(row.Text("month")+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture),row=>
        {
            if(row.Text("convention")!=HeatRateCallMarks.HeldLogReturn)throw Invalid("A stored spread mark must use the selected marks convention.");
            var month=DateOnly.ParseExact(row.Text("month")+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture);var id=row.Id("markId");
            VolatilityInput Volatility(string field,int leg)=>new(OperationIdentity.For("spread_mark_volatility_v1",id,leg),row.Text(field),VolatilityInput.Annualized);
            return new SpreadCallMark(HeatRateCallSchedule.MonthBucket(month),Volatility("longVolatility",0),Volatility("shortVolatility",1),row.Text("correlation"));
        });
    }
    internal static Task<CustomCurves.PreparedMarket> PrepareMarket(NpgsqlConnection connection,PreviewDatabase database,
        HeatRateCallPortfolioInput group,ScheduleContext context,CancellationToken ct)
    {
        var dependencies=group.Portfolio.Declare(group.Snapshot).SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
        return CustomCurves.PrepareMarket(connection,database,group.Data,group.Frame,group.Snapshot,context,
            group.Portfolio.Instruments,dependencies,CustomCurves.MarketPreparation.ValuationDependencies,ct,activePrices:[]);
    }
    internal static async Task<ExactMoney> Value(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,HeatRateCallPosition position,CancellationToken ct)
    {
        var group=(await Prepare(connection,database,frame,[position],[],false,ct)).Single();
        var context=PinnedContext.Exact((await new KnowledgeStore(database.Manifest.CatalogId).Read(connection,frame.ContextId!.Value,ct)).Context);
        var market=await PrepareMarket(connection,database,group,context,ct);
        var prepared=RiskEngine.PrepareConnected(market.Valuation,market.Bindings,[group.Portfolio],market.Authority,ct);
        return RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy,ct,RiskDetail.Holdings).Groups.Single().BasePositions.Single().Value;
    }
    private static MarketDataException Invalid(string message)=>new("heat_rate_call_market_context",message);
}
