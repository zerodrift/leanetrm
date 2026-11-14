using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasPrincipalCarryingChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Principal carrying checks require an owned disposable cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,fixture.Id("contextId"),ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,context,ct);
            var at=fixture.Instant("at");await TradingContext.Bind(connection,manifest.CatalogId,context,at,ct);
            var pins=fixture.GetProperty("pins").EnumerateArray().Select(v=>v.GetGuid()).ToImmutableArray();
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic saved principal carrying",at,DateWorkspace.Date(context.EffectiveOn),pins,
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("nymex_ng",[fixture.Id("markContract")]),context.ContextId);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);
            var snapshot=data.Snapshot(frame).WithKnowledge(context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
            var policy=JsonSerializer.Deserialize<GasOwnedValuationPolicy>("""
                {"implementation":"gas_owned_mark_to_market_v2","scope":"all_independent_owners_in_selected_books_v1",
                 "cashDiscounting":"captured_currency_default_v1","timingPolicy":"linear_utc_day_discount_ratio_v1",
                 "principalCarrying":"saved_delivery_profile_principal_carrying_v1"}
                """,LifecycleNative.Json)!;
            // The selected policy must be executable through the same ordinary
            // portfolio preparation as inventory and other native cash.
            policy.Validate();
            foreach(var invalid in new[]{policy with{PrincipalCarrying=null},policy with{Implementation=GasOwnedValuationPolicy.Version},policy with{PrincipalCarrying="unknown"}})
            {
                try{invalid.Validate();throw new Exception("A principal carrying policy silently selected a financial law.");}
                catch(MarketDataException error)when(error.Code=="gas_owned_policy"){}
            }
            var held=await GasOwnedHoldings.Read(connection,database,frame,snapshot,[fixture.Id("bookId")],ct,[(data,frame)],true,policy);
            var prepared=await GasOwnedPortfolio.Prepare(connection,database,held,policy,ct);
            await Reject(held with{PrincipalCarryingImplementation=null});
            await Reject(held with{Principal=held.Principal.SetItem(0,held.Principal[0] with{PrincipalCarrying=null})});
            var first=held.Principal[0];var lineage=first.PrincipalCarrying!;
            await Reject(held with{Principal=held.Principal.SetItem(0,first with{PrincipalCarrying=lineage with{KnownAt=snapshot.Knowledge!.KnownAt.AddSeconds(1)}})});
            await Reject(held with{Principal=held.Principal.SetItem(0,first with{PrincipalCarrying=lineage with{PositionId=Guid.NewGuid()}})});
            await Reject(held with{Principal=held.Principal.SetItem(0,first with{PrincipalCarrying=lineage with{DueAt=lineage.DueAt.AddSeconds(1)}})});
            await Reject(held with{Principal=held.Principal.SetItem(0,first with{PrincipalCarrying=lineage with{Carrying=(OpenEtrm.MarketData.Gas.GasHoldingCarrying)123}})});
            var values=prepared.SelectMany(p=>RiskEngine.CalculateExact(RiskEngine.Prepare(p.Data.Model,p.Snapshot,[p.Portfolio]),[],RiskEngine.ExactNumericalPolicy,
                detail:RiskDetail.Holdings).Groups.SelectMany(g=>g.BasePositions)).ToArray();
            var actual=values.Where(v=>held.Principal.Any(p=>p.Position.Owner==v.Owner&&p.Position.BindingId==v.BindingId)).Select(v=>v.Value).Aggregate((a,b)=>a.Add(b));
            var outstanding=held.Principal.Select(p=>p.State.Outstanding(snapshot)).Aggregate((a,b)=>a.Add(b));
            if(fixture.Text("carrying")=="nominal_mark_v1"&&actual!=outstanding)
                throw new Exception("Nominal saved-profile principal was discounted by the unrelated currency-default cash law.");
            if(fixture.Text("carrying")=="currency_curve_v1"&&(actual==outstanding||actual.Numerator=="0"))
                throw new Exception("Captured-curve principal lost its nonzero native rate exposure.");
            if(fixture.Text("carrying")=="nominal_mark_v1")
            {
                var reduced=frame with{Id=Guid.NewGuid(),ObservationIds=[..snapshot.Selections.Where(s=>s.Observation.Role!=ObservationRole.Parameter).Select(s=>s.Observation.Key.Id)]};
                var reducedData=await MarketInputSelection.Root(database,connection,reduced,ct);
                var reducedSource=reducedData.Snapshot(reduced).WithKnowledge(context.ContextId.Value,snapshot.Knowledge!.KnownAt);
                var nominal=await GasOwnedHoldings.Read(connection,database,reduced,reducedSource,[fixture.Id("bookId")],ct,[(reducedData,reduced)],true,policy);
                var nominalPortfolios=await GasOwnedPortfolio.Prepare(connection,database,nominal,policy,ct);
                var projection=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version2,"synthetic",1,
                    PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                    new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                        LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),[])
                        {CashAccumulationSelections=[]};
                var principalBindings=nominal.Principal.Select(p=>new MeasureBinding(p.Position.Owner,p.Position.BindingId)).ToImmutableHashSet();
                var sources=nominalPortfolios.Select(p=>new PortfolioProductSource(p.Data,p.Frame,p.Snapshot,p.Portfolio,p.Measure)).ToImmutableArray();
                if(!LifecyclePortfolioProjection.ResidualCurrencies(sources,projection,at.AddDays(1),principalBindings).IsEmpty||
                    nominalPortfolios.Any(p=>!p.DiscountInputs.IsEmpty)||reducedSource.Selections.Any(s=>s.Observation.Role==ObservationRole.Parameter))
                    throw new Exception("Nominal actual physical holdings must not demand an unused residual currency curve or replace it with portfolio cash funding.");
                var nominalValue=nominalPortfolios.SelectMany(p=>RiskEngine.CalculateExact(RiskEngine.Prepare(p.Data.Model,p.Snapshot,[p.Portfolio]),[],RiskEngine.ExactNumericalPolicy,
                    detail:RiskDetail.Holdings).Groups.SelectMany(g=>g.BasePositions)).Where(v=>nominal.Principal.Any(p=>p.Position.Owner==v.Owner&&p.Position.BindingId==v.BindingId))
                    .Select(v=>v.Value).Aggregate((a,b)=>a.Add(b));
                if(nominalValue!=actual)throw new Exception("Removing unused rate pins changed explicitly nominal native principal PV.");
            }
            if(held.Principal.Length<2||prepared.SelectMany(p=>p.Evidence).Any(e=>string.IsNullOrWhiteSpace(e.InstrumentReference)))
                throw new Exception("Principal carrying lost independent cash memberships or native business references.");
            if(prepared.Any(p=>p.Measure!=GasOwnedPortfolio.ProfileMeasure||p.Evidence.Any(e=>e.Measure!=GasOwnedPortfolio.ProfileMeasure)))
                throw new Exception("Saved-profile carrying must retain its explicitly named ordinary valuation measure.");
            var legacy=policy with{Implementation=GasOwnedValuationPolicy.Version,PrincipalCarrying=null};
            var legacyHeld=await GasOwnedHoldings.Read(connection,database,frame,snapshot,[fixture.Id("bookId")],ct,[(data,frame)],true,legacy);
            var prior=await GasOwnedPortfolio.Prepare(connection,database,legacyHeld,legacy,ct);
            if(JsonSerializer.Serialize(legacy,LifecycleNative.Json).Contains("principalCarrying",StringComparison.Ordinal)||
                prior.Any(p=>p.Measure!=GasOwnedPortfolio.Measure||!p.Portfolio.Setup.BindingDiscounting.IsEmpty)||
                legacyHeld.Principal.Any(p=>p.PrincipalCarrying is not null))
                throw new Exception("The existing currency-default cash policy must preserve its wire and named financial measure.");
            var setup=JsonSerializer.SerializeToElement(await new PortfolioRiskPreview(database).EconomicSetup(connection,data,frame,
                new(new(frame.Id,TradingBlotter.Convention,"retained",context.ContextId),[fixture.Id("bookId")],[]),ct),RiskJobs.Json);
            var selected=setup.GetProperty("pricing").EnumerateArray().Single(p=>p.Text("value")=="native_whole_books_saved_principal");
            if(selected.GetProperty("policy").GetProperty("gasOwned").Deserialize<GasOwnedValuationPolicy>(RiskJobs.Json)!=policy)
                throw new Exception("The financial setup must offer the exact explicit saved-profile principal law without choosing it for the user.");
            return true;
            async Task Reject(GasOwnedPortfolioRead incomplete)
            {try{_=await GasOwnedPortfolio.Prepare(connection,database,incomplete,policy,ct);}catch(MarketDataException e)when(e.Code=="gas_principal_profile_source"){return;}throw new Exception("Saved-profile carrying accepted missing native lineage.");}
        },default);
        Console.WriteLine("PASS ordinary principal carrying: explicit saved-profile law, exact native memberships and surviving recorded payments");
    }
}
