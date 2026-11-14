using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class GasAcceptedNativeChecks
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
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic accepted stock movement",at,new(2020,12,20),pins,
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[f.Id("contractId")]),context);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);var snapshot=data.Snapshot(frame).WithKnowledge(context,SchedulePreview.ParseAt(source.KnownAt));
            var pricing=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesMarginValuationPolicy.Convention,
                SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version),
                GasOwned:new(GasOwnedValuationPolicy.Version,GasOwnedValuationPolicy.WholeBooks,GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version));
            var opening=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,source,[f.Id("bookId")],pricing,[],[],ct);
            var selected=f.GetProperty("profiles").EnumerateArray().Select(p=>new GasOwnedProjectionSelection(p.Id("accountId"),p.Id("profileId"),p.GetProperty("revision").GetInt32())).ToImmutableArray();
            var request=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version,"synthetic_gas_economic",1,
                PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,
                    LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),selected);
            var prepared=await LifecyclePortfolioProjection.Prepare(connection,database,opening,request,null,ct);
            var result=prepared.Projections.SelectMany(p=>p.Evaluate("accepted_loss_no_shock",[],ct).Values).ToArray();
            var actual=result.Select(p=>p.EconomicPnl).Aggregate((a,b)=>a.Add(b));
            var future=f.GetProperty("futureOwnerIds").EnumerateArray().Select(p=>p.GetGuid()).Select(id=>
                new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,id),id,f.Id("bookId"))).ToArray();
            var owners=opening.Positions.Select(p=>new AccountingPositionKey(p.Owner,p.BindingId,p.BookId)).Concat(future).ToHashSet();
            if(actual!=ExactMoney.Parse(f.Text("expectedEconomicPnl"),actual.Currency)||!result.Select(p=>p.Owner).ToHashSet().SetEquals(owners)||
                future.Any(owner=>result.Single(p=>p.Owner==owner).OpeningValue.Numerator!="0"))
                throw new Exception("Accepted loss did not retain complete independent owners and its exact original marked quantity counterfact.");
            if(!prepared.Projections.SelectMany(p=>p.Components).Any(c=>c.Events.Any(e=>e.Kind=="gas_accepted_quantity_transition_v1")))
                throw new Exception("The real API did not include its source-known accepted quantity event.");
            if(!prepared.AdditionalOwners.Select(p=>p.Owner).ToHashSet().SetEquals(future)||
                prepared.AdditionalOwners.Any(p=>p.Reference!="synthetic_partial_operator_receipt"||p.Instrument!="owned_stock"))
                throw new Exception("A future receipt must retain its exact native title business reference and holding kind without a fabricated opening position.");
            await EconomicRiskNativeChecks.VerifyGasSamples(database,connection,data,frame,[f.Id("bookId")],pricing,request,f.Text("expectedHistoricalVar"),ct,[..future]);
            await EconomicWorkerChecks.Submit(database,connection,data,frame,
                new(new(frame.Id,TradingBlotter.Convention,"retained",context),[f.Id("bookId")],[],null),pricing,request,prepared,path,ct);
            return true;
        },default);
        Console.WriteLine("PASS accepted native gas API: exact actual movements and stated remainders, original and zero-opening receipt owners, independent carry/tail oracle and retained worker submission");
    }
}
