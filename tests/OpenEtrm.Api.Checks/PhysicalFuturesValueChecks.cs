using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class PhysicalFuturesValueChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(RiskJobs.Json)!;var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Physical source checks require their owned disposable cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,fixture.Id("contextId"),ct)).Context;
            var pins=fixture.GetProperty("pins");var primary=manifest.RevisionId;var secondary=fixture.Id("secondaryRevision");
            ImmutableArray<Guid> Selected(Guid revision)=>[..pins.GetProperty(revision.ToString()).EnumerateArray().Select(p=>p.GetGuid())];
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic two-source physical future",fixture.Instant("at"),PublishedSchedules.ContextDate(context),Selected(primary),
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("nymex_ng",[fixture.Id("contractId")]),context.ContextId);
            await PinnedContext.BindExact(connection,manifest.CatalogId,context,ct);await TradingContext.Bind(connection,manifest.CatalogId,context,frame.At,ct);
            var data=await database.Read(connection,ct,selectedRevision:primary,observationIds:Selected(primary),includeCalendarDays:false,observationScope:ObservationReadScope.ExactPins);
            var foreign=await database.Read(connection,ct,selectedRevision:secondary,observationIds:Selected(secondary),includeCalendarDays:false,observationScope:ObservationReadScope.ExactPins);
            var own=new MarketSourceInputs(CatalogueSource.From(data),Selected(primary),[],frame.CurveContracts,["nymex_ng"]);
            var other=new MarketSourceInputs(CatalogueSource.From(foreign),Selected(secondary),[],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("synthetic_inventory",[fixture.Id("markContractId")]),["synthetic_inventory"]);
            frame=frame with{Source=own.Source,SourceInputs=ImmutableDictionary<Guid,MarketSourceInputs>.Empty.Add(primary,own).Add(secondary,other)};
            var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
            var books=ImmutableArray.Create(fixture.Id("bookId"));var trades=ImmutableArray.Create(fixture.Id("tradeId"));
            var scope=await RiskTradeScope.Resolve(connection,manifest.CatalogId,books,trades,ct);
            var pricing=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesEconomicValuationPolicy.Version,
                SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version){Economic=new(FuturesEconomicValuationPolicy.Version,
                    FuturesMarginValueBinding.FlatReference,[new(fixture.Id("tradeId"),fixture.Id("bindingId"),FuturesPhysicalBasisValueBinding.Version,FuturesPhysicalBasisValueBinding.ValuationProxy)])});
            async Task Refuse(string code,InputFrame selectedFrame,PortfolioValuationPolicy selectedPolicy)
            {
                try{await new PortfolioValuation(database).Prepare(connection,data,selectedFrame,snapshot,context,books,selectedPolicy,[],[],ct,selection:scope);}
                catch(MarketDataException e)when(e.Code==code){return;}
                throw new Exception("The full physical source must refuse "+code+" before a successful portfolio.");
            }
            await Refuse("market_inputs_missing",frame with{SourceInputs=frame.SourceInputs!.Remove(secondary)},pricing);
            await Refuse("portfolio_policy",frame,pricing with{NativeFutures=pricing.NativeFutures! with{
                Economic=pricing.NativeFutures.Economic! with{Physical=[]}}});
            await Refuse("portfolio_policy",frame,pricing with{NativeFutures=pricing.NativeFutures! with{
                Economic=pricing.NativeFutures.Economic! with{Physical=pricing.NativeFutures.Economic.Physical.Add(
                    new(Guid.NewGuid(),Guid.NewGuid(),FuturesPhysicalBasisValueBinding.Version,FuturesPhysicalBasisValueBinding.ValuationProxy))}}});
            var prepared=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,context,books,pricing,[],[],ct,selection:scope);
            var position=prepared.Positions.Single();var held=prepared.Sources.Single().Portfolio;
            var key=new MeasureBinding(position.Owner,position.BindingId);var selected=held.Setup.FuturesValueBindings[key];
            var physicalBinding=selected.Physical??throw new Exception("The held physical model is missing.");
            var future=held.Instruments.OfType<FuturesContract>().Single();
            var source=prepared.InputMarkets.Single();var assetSnapshot=source.Snapshot;
            var physical=selected.Terms.Policy.Delivery!.Terms;
            var discount=prepared.InstantDiscountInputs.Where(d=>d.Input.Snapshot.Definitions.RevisionId==secondary).Select(d=>d.Input).ToArray();
            var assetDf=discount.Single(d=>d.Payment.PaymentAt==physical.From).DiscountFactor;
            var principalDf=discount.Single(d=>d.Payment.PaymentAt==((OpenEtrm.MarketData.Gas.GasPhysicalDeliveryTerms)physical).CashDueAt).DiscountFactor;
            var marginDf=prepared.InstantDiscountInputs.Single(d=>d.InstrumentId==future.Id).Input.DiscountFactor;
            var a=fixture.Number("futurePrice");var b=fixture.Number("inventoryPrice");
            var unit=prepared.FuturesMargin.Single().Value.State.SettledBasis.Unit;
            ExactMoney Leg(ExactDecimal price,ExactDecimal df)=>ExactUnitPrice.Parse(price.ToString(),unit).ExactTimes(position.Quantity).Scale(df);
            var expected=Leg(a-ExactDecimal.Parse("3"),marginDf).Add(Leg(b,assetDf)).Subtract(Leg(a,principalDf));
            var report=PortfolioValuation.Calculate(prepared,[],[],RiskDetail.Holdings,ct);
            if(report.Totals.Single().Value!=expected||prepared.Positions.Length!=1||prepared.Markets.Length!=2||prepared.Factors.Length!=2||
                !physicalBinding.Asset.Snapshot.HasSameInputs(assetSnapshot)||prepared.InputMarkets.Single().Market.Valuation.Scenario.Depth!=0)
                throw new Exception("Full physical PV must retain one original holding, exact A/B sources and independently discounted VM and delivery legs.");
            foreach(var factor in prepared.Factors)
            {
                var axis=CurveRiskAxes.Quote(prepared,factor,ct);var change=PortfolioValuation.Calculate(prepared,[new(axis,ExactDecimal.Parse("0.1"))],[],RiskDetail.Holdings,ct);
                var df=factor.Input.Series.Revision.RevisionId==secondary?assetDf:marginDf-principalDf;
                var pnl=ExactUnitPrice.Parse("0.1",unit).ExactTimes(position.Quantity).Scale(df);
                if(change.Risk.Single().UpPnl!=pnl||change.Risk.Single().DownPnl!=pnl.Scale(ExactDecimal.Parse("-1")))
                    throw new Exception("A/B shocks must use their independent exact native source and carrying factors once.");
            }
            Console.WriteLine("PASS actual full physical future: exact independent inventory source, one original holding, nonunit A/B carrying and independent signed scenario effects");
            var projection=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version,"synthetic_physical_economic",1,
                PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Linear,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                    LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.FreezeNativeExpiry,LifecycleProjectionPolicy.RetainPhysical),[]);
            var economic=await LifecyclePortfolioProjection.Prepare(connection,database,prepared,projection,null,ct);
            if(economic.Factors.Length!=2||economic.HistoryPortfolio.Markets.Length!=2||economic.Projections.Length!=1||economic.Projections.Single().Markets.Length!=2)
                throw new Exception("The economic source must retain both primitive market partitions without a fabricated B holding.");
            var noShock=economic.Projections.Single().Evaluate("two_source_no_shock",[]).Values.Single();
            if(noShock.OpeningValue!=expected||noShock.PeriodCash.Numerator!="0")throw new Exception("Economic opening must equal the selected ordinary full physical value.");
            Console.WriteLine("PASS actual physical economic source: complete A/B factor and retained input-only source graphs, same ordinary opening value");
            var scopeRequest=new PortfolioRiskCatalogRequest(new(frame.Id,TradingBlotter.Convention,"retained",context.ContextId),books,[],trades);
            var history=new PortfolioHistoryRequest(scopeRequest,pricing,new(RiskHistoryPolicy.Version1,CurveModel.NumericalPolicy,RiskHistoryPolicy.Raw,
                RiskHistoryPolicy.Heads,RiskHistoryPolicy.ExactCloses,RiskHistoryPolicy.Absolute,RiskHistoryPolicy.Complete,RiskHistoryPolicy.Fixed,2),
                new(data.ReferenceRevision,"source_business_days"),"2026-10-14","2026-10-17","16:00:00","reject","reject");
            await EconomicWorkerChecks.Submit(database,connection,data,frame,scopeRequest,pricing,projection,economic,path,ct,history);
            return true;
        },default);
    }
}
