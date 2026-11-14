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
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("nymex_ng",contracts),context.ContextId){
                Source=new(manifest.RevisionId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId)};
            var data=await MarketInputSelection.Root(database,connection,frame,ct);
            var part=new MarketSourceInputs(CatalogueSource.From(data),pins,[],frame.CurveContracts,["nymex_ng"]);
            frame=frame with{Source=part.Source,SourceInputs=ImmutableDictionary<Guid,MarketSourceInputs>.Empty.Add(data.DefinitionRevision.RevisionId,part)};
            var snapshot=data.Snapshot(frame).WithKnowledge(context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
            var physical=fixture.GetProperty("parents").EnumerateArray().Select(p=>new FuturesPhysicalValuationSelection(p.Id("tradeId"),p.Id("bindingId"),
                FuturesPhysicalBasisValueBinding.Version,FuturesPhysicalBasisValueBinding.ValuationProxy)).ToImmutableArray();
            var futureOrigin=fixture.Text("mode")=="future";var closed=fixture.Text("mode")=="closed";
            var pricing=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesEconomicValuationPolicy.Version,
                SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
                NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version){Economic=new(FuturesEconomicValuationPolicy.Version,
                    FuturesMarginValueBinding.FlatReference,futureOrigin?physical:[])},GasOwned:new(GasOwnedValuationPolicy.ProfileVersion,GasOwnedValuationPolicy.WholeBooks,
                    GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version){PrincipalCarrying=GasOwnedValuationPolicy.SavedPrincipal});
            var opening=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,context,[fixture.Id("bookId")],pricing,[],[],ct);
            if(!futureOrigin)
            {
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
            }
            var profiles=fixture.GetProperty("gasProfiles").Deserialize<ImmutableArray<GasOwnedProjectionSelection>>(RiskJobs.Json);
            var request=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version2,"synthetic_native_physical_projection",1,
                PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                    LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),profiles)
                {CashAccumulationSelections=[]};
            var native=await GasPhysicalKnownAttachmentStore.Read(connection,database,frame,snapshot,[fixture.Id("bookId")],ct);
            var prepared=await LifecyclePortfolioProjection.Prepare(connection,database,opening,request,null,ct);
            var values=prepared.Projections.SelectMany(p=>p.Evaluate("native_physical_no_shock",[]).Values).ToArray();
            if(values.Count(v=>v.Owner.Owner.Kind==AccountingOwnerKind.NativeHolding)<(opening.OwnedGas?.Positions.Length??0)||!closed&&prepared.AdditionalOwners.IsEmpty)
                throw new Exception("Native physical projection must preserve independent initial and source-known future title/cash owners.");
            foreach(var source in opening.Sources)
            foreach(var value in RiskEngine.CalculateExact(RiskEngine.Prepare(source.Data.Model,source.Snapshot,[source.Portfolio]),[],
                RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings).Groups.SelectMany(g=>g.BasePositions))
                if(values.Single(v=>v.Owner==new AccountingPositionKey(value.Owner,value.BindingId,value.BookId)).OpeningValue!=value.Value)
                    throw new Exception("Economic opening must exactly retain the ordinary selected native wealth measure for each original owner.");
            var horizon=prepared.Projections[0].Horizon;
            if(futureOrigin&&((opening.OwnedGas?.Positions.Length??0)!=0||opening.FuturesMargin.Values.Any(r=>
                r.State.Status!=FuturesMarginStatus.Open||!r.History.Determinations.IsEmpty)||native.Attachments.Any(a=>a.EffectiveAt<=at||a.EffectiveAt>horizon)))
                throw new Exception("The future-origin oracle must open before its real native finals with no invented accepted margin or gas ownership.");
            foreach(var attachment in native.Attachments)
            {
                var remaining=new Quantity((attachment.EffectiveAt>horizon?default:attachment.OriginalQuantity.Amount)-attachment.Performances.Where(p=>!p.Retracted&&p.PerformedAt<=horizon)
                    .Aggregate(default(ExactDecimal),(sum,p)=>sum+p.SignedQuantity.Amount),attachment.Terms.Quantity);
                var initial=opening.OwnedGas?.Positions.SingleOrDefault(p=>p.Position==attachment.DeliveryOwner)?.Quantity??new(default(ExactDecimal),attachment.Terms.Quantity);
                if(initial.Amount==default&&remaining.Amount==default&&attachment.EffectiveAt<=at)
                {
                    if(values.Any(v=>v.Owner==attachment.DeliveryOwner))throw new Exception("A fully discharged native obligation must not become a zero placeholder owner.");
                }
                else
                {
                    var asset=Price(attachment.Terms.Mark);
                    Require(attachment.DeliveryOwner,asset.ExactTimes(initial).Subtract(attachment.CurrentFinal.Price.ExactTimes(initial)),
                        asset.ExactTimes(remaining).Subtract(attachment.CurrentFinal.Price.ExactTimes(remaining)),ExactMoney.Parse("0",asset.Unit.Currency));
                }
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
                if(stock.Lot.TitleAt<=at&&Remaining(at).Amount==default&&Remaining(horizon).Amount==default)
                {
                    if(values.Any(v=>v.Owner==stock.Owner))throw new Exception("Fully consumed stock must not be fabricated as an opening holding.");
                    continue;
                }
                var price=Price(stock.OriginalInstrument.Mark);
                Require(stock.Owner,price.ExactTimes(Remaining(at)),price.ExactTimes(Remaining(horizon)),ExactMoney.Parse("0",price.Unit.Currency));
            }
            if(closed)
            {
                if(values.Any(v=>v.EconomicPnl.Numerator!="0"||v.OpeningValue!=v.ResidualValue||v.PeriodCash.Numerator!="0"))
                    throw new Exception("Fixed unpaid native cash has no fabricated price risk, time carry or bank payment under the selected nominal law.");
                try
                {
                    _=await LifecyclePortfolioProjection.Prepare(connection,database,opening,request with{
                        Implementation=PortfolioEconomicProjectionRequest.Version,CashAccumulationSelections=null},null,ct);
                    throw new Exception("The legacy residual-input contract must retain its original rate requirements.");
                }
                catch(MarketDataException error)when(error.Code=="missing_observation"){}
            }
            if(closed&&(!snapshot.Selections.IsEmpty||!prepared.Factors.IsEmpty||(opening.OwnedGas?.Positions.Length??0)!=0||
                !request.GasOwned.IsEmpty||!prepared.AdditionalOwners.IsEmpty))
                throw new Exception("Fully performed native parents must retain actual cash with no unused price/rate inputs, gas quantity assumptions or synthetic owners.");
            Console.WriteLine("PASS independent per-owner physical wealth: signed remaining exchange, exact native stock, rounded principal and latest actual bank allocations");
            ExactUnitPrice Price(CurveRequest mark)
            {
                return ExactUnitPrice.FromPrice(data.Model.Build(snapshot,[mark]).Values.Single().Price);
            }
            void Require(AccountingPositionKey owner,ExactMoney initial,ExactMoney residual,ExactMoney bank)
            {
                var value=values.Single(v=>v.Owner==owner);
                if(value.OpeningValue!=initial||value.ResidualValue!=residual||value.PeriodCash!=bank||value.EconomicPnl!=residual.Add(bank).Subtract(initial))
                    throw new Exception($"Native physical wealth differs for {owner}: expected {initial.ToExactString()} / {residual.ToExactString()} / {bank.ToExactString()}, actual {value.OpeningValue.ToExactString()} / {value.ResidualValue.ToExactString()} / {value.PeriodCash.ToExactString()}.");
            }
            Console.WriteLine(futureOrigin?"PASS actual source-known final activation: no opening gas owners, exact native obligation/cash identities and later resource facts":
                closed?"PASS fully discharged native parents: surviving nominal cash, no unused marks/rates and no placeholder ownership":
                "PASS actual native physical economic source: active obligations, exact future title/supply and independent principal owners");
            var scope=new PortfolioRiskCatalogRequest(new(frame.Id,TradingBlotter.Convention,"retained",context.ContextId),[fixture.Id("bookId")],[],null);
            var history=new PortfolioHistoryRequest(scope,pricing,new(RiskHistoryPolicy.Version1,CurveModel.NumericalPolicy,RiskHistoryPolicy.Raw,
                RiskHistoryPolicy.Heads,RiskHistoryPolicy.ExactCloses,RiskHistoryPolicy.Absolute,RiskHistoryPolicy.Complete,RiskHistoryPolicy.Fixed,2),
                new(data.ReferenceRevision,"source_business_days"),"2026-10-14","2026-10-17","16:00:00","reject","reject");
            await EconomicWorkerChecks.Submit(database,connection,data,frame,scope,pricing,request,prepared,path,ct,history);
            return true;
        },default);
    }
}
