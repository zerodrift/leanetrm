using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class GasOwnedKnownHistoryChecks
{
    internal static async Task Run(string path,bool profile=false)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Known gas checks require an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var context=fixture.Id("contextId");var at=fixture.Instant("at");var source=(await new KnowledgeStore(manifest.CatalogId).Read(connection,context,ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);await TradingContext.Bind(connection,manifest.CatalogId,source,at,ct);
            var pins=ImmutableArray.Create(fixture.Id("markId"));
            if(profile)pins=pins.AddRange(fixture.GetProperty("rateIds").EnumerateArray().Select(r=>r.GetGuid()));
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic source-known future gas",at,profile?new(2020,12,20):new(2020,12,19),pins,
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[fixture.Id("contractId")]),context);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);
            var snapshot=data.Snapshot(frame).WithKnowledge(context,SchedulePreview.ParseAt(source.KnownAt));var books=ImmutableArray.Create(fixture.Id("bookId"));
            var opening=await GasOwnedHoldings.Read(connection,database,frame,snapshot,books,ct);
            if(!await GasOwnedKnownHistoryStore.RequireScope(connection,database,frame,books,true,ct))throw new Exception("An empty opening hid source-known future gas.");
            var known=await GasOwnedKnownHistoryStore.Read(connection,database,frame,snapshot,books,ct);
            if(JsonSerializer.SerializeToElement(known,RiskJobs.Json).GetRawText().Contains("authorityUntil",StringComparison.Ordinal))
                throw new Exception("Ephemeral authorization deadlines entered immutable gas source evidence.");
            var selection=ImmutableArray.Create(new GasOwnedProjectionSelection(fixture.Id("accountId"),fixture.Id("profileId"),1));
            var choices=await GasOwnedProjection.ReadChoices(connection,database,frame,books,ct);
            var choice=choices.Single(a=>a.AccountId==fixture.Id("accountId"));
            if(!choice.Profiles.Any(p=>p.ProfileId==fixture.Id("profileId")&&p.Revision==1)||(!profile&&choice.EffectiveAt<=snapshot.ValuationAt))
                throw new Exception("The source-known exact gas account/profile choices lost a future-effective account or its saved version.");
            if(profile)
            {
                var curves=opening.Positions.Select(p=>p.Instrument.Currency).Distinct().ToImmutableDictionary(c=>c,c=>(ISideDiscounting)new ExplicitDiscounting(ExactDecimal.Parse("0.95"),DiscountFactorSource.Explicit));
                var projected=await GasOwnedProjection.Prepare(connection,database,known,opening,snapshot,at.AddHours(8),selection,curves,ct);
                if(projected.Components.Length!=1||!projected.Owners.SetEquals(opening.Positions.Select(p=>p.Position))||projected.Components.Single().Opening.Members.Count!=opening.Positions.Length||
                    projected.ProfileEvidence.Any(p=>p.GetRawText().Contains("authorityUntil",StringComparison.Ordinal)))
                    throw new Exception("A saved connected gas profile omitted or reassigned an independently owned commodity position.");
                if(!known.Consumptions.Any(c=>c.At>at.AddHours(8)))throw new Exception("The horizon-boundary check lacks its source-known later physical fact.");
                if(fixture.TryGetProperty("economicSession",out var session))
                {
                    var pricing=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesMarginValuationPolicy.Convention,
                        SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                        NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version),
                        GasOwned:new(GasOwnedValuationPolicy.Version,GasOwnedValuationPolicy.WholeBooks,GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version));
                    var portfolio=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,source,books,pricing,[],[],ct);
                    var request=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version,session.GetString()!,1,
                        PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                        new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                            LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),selection);
                    var economic=await LifecyclePortfolioProjection.Prepare(connection,database,portfolio,request,null,ct);
                    var result=economic.Projections.SelectMany(p=>p.Evaluate("native_gas_no_shock",[],ct).Values).ToArray();
                    if(result.Length!=portfolio.Positions.Length||result.Any(p=>p.EconomicPnl.Numerator!="0")||
                        result.Select(p=>p.Owner).Distinct().Count()!=result.Length||result.Any(p=>p.Owner.Owner.Kind!=AccountingOwnerKind.NativeHolding))
                        throw new Exception("Complete gas economic projection must retain every independent commodity/cash owner and conserve no-shock wealth under zero captured rates.");
                    Console.WriteLine("PASS native gas economic API preparation: explicit account profiles, commodity and cash owners, selected next close and exact no-shock conservation");
                    await EconomicRiskNativeChecks.VerifyGasSamples(database,connection,data,frame,books,pricing,request,fixture.Text("expectedHistoricalVar"),ct);
                }
                try{await GasOwnedProjection.Prepare(connection,database,known,opening,snapshot,at.AddDays(3),selection,curves,ct);throw new Exception("A horizon-crossing native effect omitted its transition.");}
                catch(MarketDataException error)when(error.Code=="gas_projection_known_transition"){}
                return true;
            }
            if(!opening.Positions.IsEmpty)throw new Exception("A knowledge-only future stock became an opening holding.");
            var position=known.Positions[fixture.Id("positionId")];
            if(position.Time.EffectiveAt<=snapshot.ValuationAt||position.Quantity.Amount!=ExactDecimal.Parse("7")||!known.Source.HasSameInputs(snapshot)||
                known.Lots.Single(l=>l.Id==position.Owner.Owner.Id).TitleAt!=position.Time.EffectiveAt)
                throw new Exception("The gas source lost future title, exact quantities, original owner or certified opening coordinates.");
            try{await GasOwnedKnownHistoryStore.Read(connection,database,frame,snapshot with{Id=Guid.NewGuid()},books,ct);throw new Exception("A gas source accepted another coherent snapshot identity.");}
            catch(MarketDataException error)when(error.Code=="gas_owned_known_context"){}
            try{await GasOwnedProjection.Prepare(connection,database,known,opening,snapshot,at.AddDays(2),selection,ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty,ct);
                throw new Exception("A source-known future independent title invented zero capital and successful economic P&L.");}
            catch(MarketDataException error)when(error.Code=="gas_projection_capital"){}
            return true;
        },default);
        Console.WriteLine(profile?"PASS saved native gas projection profile: exact immutable selection and complete independent owner materialization":
            "PASS managed known gas source: empty opening, independently detected future title, exact native owner/time/quantity, forged snapshot and missing capital-counterfact refusal");
    }
}
