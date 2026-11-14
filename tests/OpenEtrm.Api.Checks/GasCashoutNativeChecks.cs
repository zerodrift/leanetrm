using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class GasCashoutNativeChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var f=document.RootElement;
        var manifest=f.GetProperty("manifest").Deserialize<PreviewManifest>(GasOwnedOperations.Json)!;var host=Path.GetFullPath(f.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))throw new Exception("Use an owned disposable gas database.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Pooling=false,Timezone="UTC"};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var context=f.Id("contextId");var at=f.Instant("at");var source=(await new KnowledgeStore(manifest.CatalogId).Read(connection,context,ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);await TradingContext.Bind(connection,manifest.CatalogId,source,at,ct);
            var pins=ImmutableArray.Create(f.Id("markId")).AddRange(f.GetProperty("rateIds").EnumerateArray().Select(p=>p.GetGuid()));
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic explicit operator cashout",at,new(2020,12,20),pins,
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[f.Id("contractId")]),context);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);var snapshot=data.Snapshot(frame).WithKnowledge(context,SchedulePreview.ParseAt(source.KnownAt));
            var pricing=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesMarginValuationPolicy.Convention,
                SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version),
                GasOwned:new(GasOwnedValuationPolicy.Version,GasOwnedValuationPolicy.WholeBooks,GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version));
            var opening=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,source,[f.Id("bookId")],pricing,[],[],ct);
            var selected=ImmutableArray.Create(new GasOwnedProjectionSelection(f.Id("accountId"),f.Id("profileId"),1));
            foreach(var disposition in new[]{LifecycleProjectionPolicy.PayAtDue,LifecycleProjectionPolicy.Outstanding})
            {
                var request=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version,"synthetic_gas_economic",1,
                    PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                    new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,disposition,LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,
                        LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),selected);
                var prepared=await LifecyclePortfolioProjection.Prepare(connection,database,opening,request,null,ct);
                var result=prepared.Projections.SelectMany(p=>p.Evaluate("native_cashout",[],ct).Values).Single();
                var expected=ExactMoney.Parse(f.Text("expectedCash"),result.Currency);var marked=ExactMoney.Parse(f.Text("expectedOpening"),result.Currency);
                if(result.Owner.Owner.Id!=f.Id("positionId")||result.OpeningValue!=marked||result.PeriodCash!=(disposition==LifecycleProjectionPolicy.PayAtDue?expected:ExactMoney.Parse("0",result.Currency))||
                    result.ResidualValue!=(disposition==LifecycleProjectionPolicy.Outstanding?expected:ExactMoney.Parse("0",result.Currency))||result.EconomicPnl!=expected.Subtract(marked))
                    throw new Exception("Native cashout projection lost exact marked opening, signed rounded cash or the selected payment disposition.");
                if(prepared.ProductEvidence.Any(p=>p.GetRawText().Contains("authorityUntil",StringComparison.Ordinal)))throw new Exception("A retained gas projection included ephemeral authorization.");
                if(disposition==LifecycleProjectionPolicy.Outstanding)
                    await EconomicWorkerChecks.Submit(database,connection,data,frame,
                        new(new(frame.Id,TradingBlotter.Convention,"retained",context),[f.Id("bookId")],[],null),pricing,request,prepared,path,ct);
            }
            return true;
        },default);
        Console.WriteLine("PASS native operator cashout economic API: exact saved expectation/carrying, ordinary opening, rounded cash, paid/outstanding and retained owner");
    }
}
