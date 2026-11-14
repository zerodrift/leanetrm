using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class GasPhysicalProjectionChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(RiskJobs.Json)!;var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Native physical projection checks require an owned disposable cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        await database.WithIdentity("trader","RunScenario",async(connection,ct)=>
        {
            var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,fixture.Id("contextId"),ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,context,ct);
            var at=fixture.Instant("at");await TradingContext.Bind(connection,manifest.CatalogId,context,at,ct);
            var pins=fixture.GetProperty("pins").EnumerateArray().Select(p=>p.GetGuid()).ToImmutableArray();
            var contracts=fixture.GetProperty("contracts").EnumerateArray().Select(p=>p.GetGuid()).ToImmutableArray();
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic native physical economic source",at,PublishedSchedules.ContextDate(context),pins,
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("nymex_ng",contracts),context.ContextId);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);
            var part=new MarketSourceInputs(CatalogueSource.From(data),pins,[],frame.CurveContracts,["nymex_ng"]);
            frame=frame with{Source=part.Source,SourceInputs=ImmutableDictionary<Guid,MarketSourceInputs>.Empty.Add(data.DefinitionRevision.RevisionId,part)};
            var snapshot=data.Snapshot(frame).WithKnowledge(context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
            var physical=fixture.GetProperty("parents").EnumerateArray().Select(p=>new FuturesPhysicalValuationSelection(p.Id("tradeId"),p.Id("bindingId"),
                FuturesPhysicalBasisValueBinding.Version,FuturesPhysicalBasisValueBinding.ValuationProxy)).ToImmutableArray();
            var pricing=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesEconomicValuationPolicy.Version,
                SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version){Economic=new(FuturesEconomicValuationPolicy.Version,
                    FuturesMarginValueBinding.FlatReference,[])},GasOwned:new(GasOwnedValuationPolicy.ProfileVersion,GasOwnedValuationPolicy.WholeBooks,
                    GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version){PrincipalCarrying=GasOwnedValuationPolicy.SavedPrincipal});
            var opening=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,context,[fixture.Id("bookId")],pricing,[],[],ct);
            try
            {
                _=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,context,[fixture.Id("bookId")],pricing with{
                    NativeFutures=pricing.NativeFutures! with{Economic=pricing.NativeFutures.Economic! with{Physical=physical}}},[],[],ct);
                throw new Exception("Completed original futures must not acquire unused physical valuation selections.");
            }
            catch(MarketDataException error)when(error.Code=="portfolio_policy"){}
            var ratePins=snapshot.Selections.Where(s=>s.Observation.Role==ObservationRole.Parameter).Select(s=>s.Observation.Key.Id).ToImmutableArray();
            var cashOnlyFrame=frame with{Id=Guid.NewGuid(),ObservationIds=ratePins,SourceInputs=frame.SourceInputs!.ToImmutableDictionary(p=>p.Key,p=>p.Value with{ObservationIds=ratePins})};
            var cashOnlyData=await MarketInputSelection.Root(database,connection,cashOnlyFrame,ct);
            var cashOnlySource=cashOnlyData.Snapshot(cashOnlyFrame).WithKnowledge(context.ContextId.Value,snapshot.Knowledge!.KnownAt);
            var originalFuture=physical[0];var originalTrade=opening.Sources.SelectMany(s=>s.Trades).Single(t=>t.Id==originalFuture.TradeId);
            var remainingPolicy=pricing.NativeFutures! with{Economic=pricing.NativeFutures.Economic! with{Physical=[]}};
            var remainingPortfolio=await FuturesMarginPortfolio.Read(connection,database,cashOnlyData,cashOnlyFrame,cashOnlySource,[originalTrade],remainingPolicy,context,ct);
            if(remainingPortfolio.Portfolio.Setup.FuturesValueBindings.Values.Any(b=>b.Physical is not null)||!remainingPortfolio.Inputs.IsEmpty||
                cashOnlySource.Selections.Any(s=>s.Observation.Role!=ObservationRole.Parameter))
                throw new Exception("A discharged original future must not require its obsolete inventory mark while independently owned delivery/stock remains outside this explicit trade scope.");
            Console.WriteLine("PASS actual completed future remaining value: exact accepted final and surviving VM cash with no obsolete physical mark input");
            var profiles=fixture.GetProperty("gasProfiles").Deserialize<ImmutableArray<GasOwnedProjectionSelection>>(RiskJobs.Json);
            var request=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version2,"synthetic_native_physical_projection",1,
                PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                    LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),profiles)
                {CashAccumulationSelections=[]};
            var native=await GasPhysicalKnownAttachmentStore.Read(connection,database,frame,snapshot,[fixture.Id("bookId")],ct);
            var prepared=await LifecyclePortfolioProjection.Prepare(connection,database,opening,request,null,ct);
            var values=prepared.Projections.SelectMany(p=>p.Evaluate("native_physical_no_shock",[]).Values).ToArray();
            if(values.Count(v=>v.Owner.Owner.Kind==AccountingOwnerKind.NativeHolding)<opening.OwnedGas!.Positions.Length||prepared.AdditionalOwners.IsEmpty)
                throw new Exception("Native physical projection must preserve independent initial and source-known future title/cash owners.");
            var horizon=prepared.Projections[0].Horizon;
            foreach(var attachment in native.Attachments)
            {
                var remaining=new Quantity(attachment.OriginalQuantity.Amount-attachment.Performances.Where(p=>!p.Retracted&&p.PerformedAt<=horizon)
                    .Aggregate(default(ExactDecimal),(sum,p)=>sum+p.SignedQuantity.Amount),attachment.Terms.Quantity);
                var initial=opening.OwnedGas.Positions.Single(p=>p.Position==attachment.DeliveryOwner).Quantity;
                var asset=Price(attachment.Terms.Mark);
                Require(attachment.DeliveryOwner,asset.ExactTimes(initial).Subtract(attachment.CurrentFinal.Price.ExactTimes(initial)),
                    asset.ExactTimes(remaining).Subtract(attachment.CurrentFinal.Price.ExactTimes(remaining)),ExactMoney.Parse("0",asset.Unit.Currency));
                foreach(var cash in attachment.Cash)
                {
                    var flows=cash.State.Allocations.Where(f=>f.KnownAt<=snapshot.Knowledge!.KnownAt).GroupBy(f=>f.AllocationId).Select(g=>g.MaxBy(f=>f.Revision)!).ToArray();
                    ExactMoney Unpaid(DateTimeOffset time)=>cash.EffectiveAt>time?ExactMoney.Parse("0",cash.Instrument.Currency):
                        flows.Where(f=>f.EffectiveAt<=time).Aggregate(ExactMoney.FromMoney(new(cash.Quantity.Amount,cash.Instrument.Currency)),(sum,f)=>sum.Subtract(f.Amount));
                    var bank=flows.Where(f=>f.EffectiveAt>at&&f.EffectiveAt<=horizon).Aggregate(ExactMoney.Parse("0",cash.Instrument.Currency),(sum,f)=>sum.Add(f.Amount));
                    Require(cash.Owner,Unpaid(at),Unpaid(horizon),bank);
                }
            }
            foreach(var stock in native.Attachments.SelectMany(a=>a.Stocks).DistinctBy(s=>s.Owner))
            {
                Quantity Remaining(DateTimeOffset time)=>new(stock.Lot.TitleAt>time?default:stock.Lot.Quantity.Amount-
                    stock.Consumptions.Where(c=>c.At<=time).Aggregate(default(ExactDecimal),(sum,c)=>sum+c.Quantity.Amount),stock.Lot.Quantity.Specification);
                var price=Price(stock.OriginalInstrument.Mark);
                Require(stock.Owner,price.ExactTimes(Remaining(at)),price.ExactTimes(Remaining(horizon)),ExactMoney.Parse("0",price.Unit.Currency));
            }
            Console.WriteLine("PASS independent per-owner physical wealth: signed remaining exchange, exact native stock, rounded principal and latest actual bank allocations");
            ExactUnitPrice Price(CurveRequest mark)
            {
                var market=opening.OwnedGas.Markets[mark.Curve.Revision.RevisionId];
                return ExactUnitPrice.FromPrice(market.Data.Model.Build(market.Snapshot,[mark]).Values.Single().Price);
            }
            void Require(AccountingPositionKey owner,ExactMoney initial,ExactMoney residual,ExactMoney bank)
            {
                var value=values.Single(v=>v.Owner==owner);
                if(value.OpeningValue!=initial||value.ResidualValue!=residual||value.PeriodCash!=bank||value.EconomicPnl!=residual.Add(bank).Subtract(initial))
                    throw new Exception($"Native physical wealth differs for {owner}: expected {initial.ToExactString()} / {residual.ToExactString()} / {bank.ToExactString()}, actual {value.OpeningValue.ToExactString()} / {value.ResidualValue.ToExactString()} / {value.PeriodCash.ToExactString()}.");
            }
            Console.WriteLine("PASS actual native physical economic source: active obligations, exact future title/supply and independent principal owners");
            var scope=new PortfolioRiskCatalogRequest(new(frame.Id,TradingBlotter.Convention,"retained",context.ContextId),[fixture.Id("bookId")],[],null);
            var history=new PortfolioHistoryRequest(scope,pricing,new(RiskHistoryPolicy.Version1,CurveModel.NumericalPolicy,RiskHistoryPolicy.Raw,
                RiskHistoryPolicy.Heads,RiskHistoryPolicy.ExactCloses,RiskHistoryPolicy.Absolute,RiskHistoryPolicy.Complete,RiskHistoryPolicy.Fixed,2),
                new(data.ReferenceRevision,"source_business_days"),"2026-10-14","2026-10-17","16:00:00","reject","reject");
            await EconomicWorkerChecks.Submit(database,connection,data,frame,scope,pricing,request,prepared,path,ct,history);
            return true;
        },default);
    }
}
