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
            var models=new Dictionary<Guid,PreviewData>{{data.DefinitionRevision.RevisionId,data}};
            if(fixture.TryGetProperty("sources",out var supplemental))
            foreach(var selected in supplemental.EnumerateArray())
            {
                var revision=selected.Id("revisionId");var observations=selected.GetProperty("pins").EnumerateArray().Select(p=>p.GetGuid()).ToImmutableArray();
                var input=await database.Read(connection,ct,selectedRevision:revision,observationIds:observations,includeCalendarDays:false,
                    observationScope:ObservationReadScope.ExactPins);
                var curves=ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add(selected.Text("curve"),[selected.Id("contractId")]);
                frame=frame with{SourceInputs=frame.SourceInputs!.Add(revision,new(CatalogueSource.From(input),observations,[],curves,[selected.Text("curve")]))};
                models.Add(revision,input);
            }
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
                var cashOnlyFrame=frame with{Id=Guid.NewGuid(),ObservationIds=ratePins,SourceInputs=ImmutableDictionary<Guid,MarketSourceInputs>.Empty.Add(
                    data.DefinitionRevision.RevisionId,part with{ObservationIds=ratePins})};
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
            if(fixture.Text("mode")=="foreign")
            {
                if(snapshot.Selections.Any(s=>s.Observation.Role!=ObservationRole.Parameter))
                    throw new Exception("Accepted-final A futures must retain their financial source without an obsolete A price pin.");
                var foreign=opening.Markets.Single(m=>m.Snapshot.Definitions.RevisionId!=data.DefinitionRevision.RevisionId);
                foreach(var invalid in new[]{opening with{InputMarkets=[],Markets=opening.Markets.Remove(foreign)},
                    opening with{InputMarkets=[],Markets=opening.Markets.Replace(foreign,foreign with{Snapshot=foreign.Snapshot with{Id=Guid.NewGuid()}})}})
                {
                    try{_=await LifecyclePortfolioProjection.Prepare(connection,database,invalid,request,null,ct);throw new Exception("Active native physical continuation accepted a missing or substituted foreign source.");}
                    catch(MarketDataException error)when(error.Code=="projection_delivery_source"){}
                }
                if(prepared.HistoryPortfolio.Markets.Select(m=>m.Snapshot.Definitions.RevisionId).Distinct().Count()!=2||
                    prepared.Factors.Length!=1||prepared.Factors.Single().Input.Series.Revision.RevisionId==data.DefinitionRevision.RevisionId)
                    throw new Exception("Active A/B continuation must retain both exact native sources and the independent surviving B factor.");
            }
            var values=prepared.Projections.SelectMany(p=>p.Evaluate("native_physical_no_shock",[]).Values).ToArray();
            if(values.Count(v=>v.Owner.Owner.Kind==AccountingOwnerKind.NativeHolding)<(opening.OwnedGas?.Positions.Length??0)||!closed&&prepared.AdditionalOwners.IsEmpty)
                throw new Exception("Native physical projection must preserve independent initial and source-known future title/cash owners.");
            foreach(var source in opening.Sources)
            foreach(var value in RiskEngine.CalculateExact(RiskEngine.Prepare(source.Data.Model,source.Snapshot,[source.Portfolio]),[],
                RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings).Groups.SelectMany(g=>g.BasePositions))
                if(values.Single(v=>v.Owner==new AccountingPositionKey(value.Owner,value.BindingId,value.BookId)).OpeningValue!=value.Value)
                    throw new Exception("Economic opening must exactly retain the ordinary selected native wealth measure for each original owner.");
            var horizon=prepared.Projections[0].Horizon;
            var priceQuantities=new Dictionary<AccountingPositionKey,Quantity>();
            if(fixture.Text("mode")=="foreign")
            {
                if(native.Attachments.Any(a=>a.EffectiveAt>horizon||a.Stocks.Any(s=>s.Lot.TitleAt>horizon)||a.Cash.Any(c=>c.EffectiveAt>horizon)))
                    throw new Exception("This foreign owner-set oracle requires every retained native birth within its exact horizon.");
                var expected=opening.Positions.Select(p=>new AccountingPositionKey(p.Owner,p.BindingId,p.BookId))
                    .Concat(native.Attachments.Where(a=>a.EffectiveAt>at).Select(a=>a.DeliveryOwner))
                    .Concat(native.Attachments.SelectMany(a=>a.Stocks).Where(s=>s.Lot.TitleAt>at).Select(s=>s.Owner))
                    .Concat(native.Attachments.SelectMany(a=>a.Cash).Where(c=>c.EffectiveAt>at).Select(c=>c.Owner)).ToHashSet();
                if(values.Length!=expected.Count||!expected.SetEquals(values.Select(v=>v.Owner)))
                    throw new Exception("The result must contain exactly the ordinary native opening owners and explicitly activated native descendants, with no extra constant-cash owner.");
            }
            if(futureOrigin&&((opening.OwnedGas?.Positions.Length??0)!=0||opening.FuturesMargin.Values.Any(r=>
                r.State.Status!=FuturesMarginStatus.Open||!r.History.Determinations.IsEmpty)||native.Attachments.Any(a=>a.EffectiveAt<=at||a.EffectiveAt>horizon)))
                throw new Exception("The future-origin oracle must open before its real native finals with no invented accepted margin or gas ownership.");
            foreach(var attachment in native.Attachments)
            {
                var remaining=new Quantity((attachment.EffectiveAt>horizon?default:attachment.OriginalQuantity.Amount)-attachment.Performances.Where(p=>!p.Retracted&&p.PerformedAt<=horizon)
                    .Aggregate(default(ExactDecimal),(sum,p)=>sum+p.SignedQuantity.Amount),attachment.Terms.Quantity);
                var initial=opening.OwnedGas?.Positions.SingleOrDefault(p=>p.Position==attachment.DeliveryOwner)?.Quantity??new(default(ExactDecimal),attachment.Terms.Quantity);
                priceQuantities.Add(attachment.DeliveryOwner,remaining);
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
                priceQuantities.Add(stock.Owner,Remaining(horizon));
                if(stock.Lot.TitleAt<=at&&Remaining(at).Amount==default&&Remaining(horizon).Amount==default)
                {
                    if(values.Any(v=>v.Owner==stock.Owner))throw new Exception("Fully consumed stock must not be fabricated as an opening holding.");
                    continue;
                }
                var price=Price(stock.OriginalInstrument.Mark);
                Require(stock.Owner,price.ExactTimes(Remaining(at)),price.ExactTimes(Remaining(horizon)),ExactMoney.Parse("0",price.Unit.Currency));
            }
            if(fixture.Text("mode")=="foreign")
            {
                var factor=prepared.Factors.Single();var change=ExactDecimal.Parse("0.1");
                var shifted=prepared.Projections.SelectMany(p=>p.Evaluate("foreign_inventory_up",[new(factor.Input,factor.Quote,factor.Unit,change,QuoteShift.Convention)]).Values).ToArray();
                foreach(var value in values)
                {
                    var actual=shifted.Single(v=>v.Owner==value.Owner);
                    var delta=priceQuantities.TryGetValue(value.Owner,out var quantity)?ExactUnitPrice.Parse("0.1",factor.Unit).ExactTimes(quantity):ExactMoney.Parse("0",value.ResidualValue.Currency);
                    if(actual.OpeningValue!=value.OpeningValue||actual.PeriodCash!=value.PeriodCash||actual.ResidualValue!=value.ResidualValue.Add(delta)||
                        actual.EconomicPnl!=value.EconomicPnl.Add(delta))
                        throw new Exception("A foreign inventory shock must affect exactly each surviving native asset quantity, with fixed principal and opening wealth unchanged.");
                }
                Console.WriteLine("PASS independent active A/B continuation: exact foreign source, zero-rate B versus nonzero A, signed per-owner inventory shock and source substitution refusal");
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
                var selected=models[mark.Curve.Revision.RevisionId];var selectedFrame=frame.SourceInputs![mark.Curve.Revision.RevisionId].Project(frame);
                var selectedSource=selected.Snapshot(selectedFrame).WithKnowledge(context.ContextId!.Value,snapshot.Knowledge!.KnownAt);
                return ExactUnitPrice.FromPrice(selected.Model.Build(selectedSource,[mark]).Values.Single().Price);
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
