using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class EconomicFutureWorkerChecks
{
    internal static async Task Submit(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(RiskJobs.Json)!;var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Economic futures checks require their owned disposable cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,fixture.Id("contextId"),ct)).Context;
            var frame=new InputFrame(fixture.TryGetProperty("snapshotId",out var selectedSnapshot)?selectedSnapshot.GetGuid():Guid.NewGuid(),"Synthetic economic futures source",fixture.Instant("at"),PublishedSchedules.ContextDate(context),
                [..fixture.GetProperty("pins").EnumerateArray().Select(v=>v.GetGuid())],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[fixture.Id("contractId")]),context.ContextId);
            if(fixture.TryGetProperty("sourceInputs",out var matrix))frame=frame with{Source=new(manifest.RevisionId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId),
                SourceInputs=matrix.Deserialize<ImmutableDictionary<Guid,MarketSourceInputs>>(RiskJobs.Json)};
            await PinnedContext.BindExact(connection,manifest.CatalogId,context,ct);await TradingContext.Bind(connection,manifest.CatalogId,context,frame.At,ct);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);
            var part=new MarketSourceInputs(CatalogueSource.From(data),frame.ObservationIds,[],frame.CurveContracts,["future"]);
            frame=frame with{Source=part.Source,SourceInputs=frame.SourceInputs??ImmutableDictionary<Guid,MarketSourceInputs>.Empty.Add(data.DefinitionRevision.RevisionId,part)};
            var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
            var books=ImmutableArray.Create(fixture.Id("bookId"));var trades=ImmutableArray.Create(fixture.Id("tradeId"));
            var scope=await RiskTradeScope.Resolve(connection,manifest.CatalogId,books,trades,ct);
            var pricing=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesMarginValuationPolicy.Convention,
                SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version));
            var fullPricing=pricing with{FuturesConvention=FuturesEconomicValuationPolicy.Version,NativeFutures=pricing.NativeFutures! with{
                Economic=new(FuturesEconomicValuationPolicy.Version,FuturesMarginValueBinding.FlatReference,[])}};
            var full=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,context,books,fullPricing,[],[],ct,selection:scope);
            var axis=CurveRiskAxes.Quote(full,full.Factors.Single(),ct);
            var report=PortfolioValuation.Calculate(full,[new(axis,ExactDecimal.Parse("0.1"))],[],RiskDetail.Holdings,ct);
            var margin=full.FuturesMargin.Single().Value;var holding=full.Sources.SelectMany(s=>s.Portfolio.Positions).Single();
            var input=full.InstantDiscountInputs.Single(p=>p.InstrumentId==holding.ValuationId).Input;
            var unit=margin.State.SettledBasis.Unit;
            var expected=ExactUnitPrice.Parse("0.1",unit).ExactTimes(holding.Quantity).Scale(input.DiscountFactor);
            if(input.DiscountFactor==ExactDecimal.Parse("1")||report.Risk.Single().UpPnl!=expected||report.Risk.Single().DownPnl!=expected.Scale(ExactDecimal.Parse("-1"))||
                report.Totals.Single().Measure!=FuturesEconomicValuationPolicy.Version||report.ModelContracts.Single().ModelVersion!=FuturesMarginValueBinding.FlatReference||
                full.Sources.Single().Portfolio.Setup.FuturesValueBindings.Count!=1||full.Positions.Length!=1)
                throw new Exception("Full native ordinary PV must use the exact saved next margin due and captured nonunit discount once, with one original holding.");
            Console.WriteLine("PASS actual full native cash-future PV: explicit v2 law, original owner/terms and nonunit next-due symmetric PV changes");
            var opening=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,context,books,fullPricing,[],[],ct,selection:scope);
            var projection=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version,fixture.Text("session"),1,
                PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Linear,LifecycleProjectionPolicy.PayAtDue,LifecycleProjectionPolicy.Nominal,
                    LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),[]);
            if(fixture.TryGetProperty("cashAccumulation",out var accumulation))projection=projection with{
                Implementation=PortfolioEconomicProjectionRequest.Version2,Policy=projection.Policy with{CashAccumulation=LifecycleProjectionPolicy.Accumulated},
                CashAccumulationSelections=[accumulation.Deserialize<PortfolioCashAccumulationSelection>(RiskJobs.Json)!]};
            var prepared=await LifecyclePortfolioProjection.Prepare(connection,database,opening,projection,null,ct);
            if(prepared.Factors.Length!=1||prepared.Projections.Single().Components.Single() is not FuturesLifecycleProjection)
                throw new Exception("The future worker must prepare its native adopted owner, margin state and full primitive factor closure.");
            if(fixture.TryGetProperty("fundingOracle",out var oracle))
            {
                var actual=prepared.Projections.Single().Evaluate("explicit_cash_funding",[]).Values.Single().PeriodCash;
                if(actual.CompareTo(ExactMoney.Parse(oracle.GetString()!,actual.Currency))!=0)
                    throw new Exception($"Independent funding oracle {oracle.GetString()} differs from {actual.ToExactString()}.");
                if(prepared.CashAccumulationInputs.Length!=1||prepared.HistoryPortfolio.Markets.Length!=1||prepared.CashAccumulationEvidence is null)
                    throw new Exception("Rate-only funding created a price portfolio/graph or omitted its native input proof.");
                Console.WriteLine("PASS native explicit funding: independent signed rational cash oracle and rate-only source custody without another holding or price factor");
            }
            await EconomicWorkerChecks.Submit(database,connection,data,frame,new(new(frame.Id,TradingBlotter.Convention,"retained",context.ContextId),books,[],trades),
                fullPricing,projection,prepared,path,ct);
            return true;
        },default);
        Console.WriteLine("PASS managed futures economic source and complete retained historical/Monte Carlo requests");
    }
}
