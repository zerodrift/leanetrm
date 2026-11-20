using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class GasOperatorPhysicalValueChecks
{
    internal static async Task Run(string path,bool ordinaryAndZero=false)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var f=document.RootElement;
        var manifest=f.GetProperty("manifest").Deserialize<PreviewManifest>(RiskJobs.Json)!;
        var host=Path.GetFullPath(f.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Operator value checks require their owned disposable database.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Pooling=false,Timezone="UTC"};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var pricing=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesMarginValuationPolicy.Convention,
            SwapStore.Convention,RateCurveMath.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,
            NativeFutures:new(FuturesMarginValuationPolicy.CurrencyDefault,RateDiscountTiming.Version),
            GasOwned:new(GasOwnedValuationPolicy.Version,GasOwnedValuationPolicy.WholeBooks,GasOwnedValuationPolicy.CurrencyDefault,RateDiscountTiming.Version));
        var cases=ordinaryAndZero?f.GetProperty("cases").EnumerateArray().ToArray():[f];
        var books=cases.Select(p=>p.Id("bookId")).ToImmutableArray();var at=f.Instant("at");
        AccountingPositionKey Owner(JsonElement item,string name)=>new(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,item.Id(name)),item.Id(name),item.Id("bookId"));
        var expectedOwners=cases.SelectMany(item=>ordinaryAndZero&&item.GetProperty("zero").GetBoolean()?
            new[]{Owner(item,"fundingId"),Owner(item,"negativeClaimId")}:
            new[]{Owner(item,"receiptId"),Owner(item,"fundingId"),Owner(item,"negativeClaimId")}).ToHashSet();
        foreach(var historical in ordinaryAndZero?new[]{false}:new[]{true,false})
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var context=f.Id(historical?"originalContextId":"contextId");
            var source=(await new KnowledgeResolver(manifest.CatalogId).Read(connection,context,ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,source,ct);await TradingContext.Bind(connection,manifest.CatalogId,source,at,ct);
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic complete operator physical book",at,new(2020,12,20),
                [f.Id("markId"),..f.GetProperty("rateIds").EnumerateArray().Select(p=>p.GetGuid())],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[f.Id("contractId")]),context);
            var data=await MarketInputSelection.Root(database,connection,frame,ct);
            var snapshot=data.Snapshot(frame).WithKnowledge(context,SchedulePreview.ParseAt(source.KnownAt));
            var opening=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,source,books,pricing,[],[],ct);
            var ordinary=PortfolioValuation.Calculate(opening,[],[],RiskDetail.Holdings,ct);
            var native=opening.OwnedGas??throw new Exception("The whole book lost its actual native gas source.");
            var selectedOwners=ordinary.Positions.Select(p=>new AccountingPositionKey(p.Owner,p.BindingId,p.BookId)).ToArray();
            if(selectedOwners.Length!=expectedOwners.Count||!selectedOwners.ToHashSet().SetEquals(expectedOwners)||!opening.Excluded.IsEmpty||
                native.Positions.Length!=expectedOwners.Count||!native.Fees.IsEmpty||!native.Principal.IsEmpty)
                throw new Exception("Operator PV must retain the exact complete physical book, with no fake claim, cash or omitted owner.");
            var expectedQuantities=new Dictionary<AccountingPositionKey,string>();
            var remainingQuantities=new Dictionary<AccountingPositionKey,string>();
            foreach(var item in cases)
            {
                var zero=ordinaryAndZero&&item.GetProperty("zero").GetBoolean();
                if(!zero)
                {
                    expectedQuantities.Add(Owner(item,"receiptId"),ordinaryAndZero?"8":historical?"10":"9");
                    remainingQuantities.Add(Owner(item,"receiptId"),"7");
                }
                var funding=ordinaryAndZero&&!zero?"1.75":historical?"1":"1.5";
                var debt=ordinaryAndZero&&!zero?"-1.75":historical?"-1":"-1.5";
                expectedQuantities.Add(Owner(item,"fundingId"),funding);expectedQuantities.Add(Owner(item,"negativeClaimId"),debt);
                remainingQuantities.Add(Owner(item,"fundingId"),funding);remainingQuantities.Add(Owner(item,"negativeClaimId"),debt);
            }
            foreach(var owner in expectedOwners)
            {
                var held=native.Positions.Single(p=>p.Position==owner);
                var value=ordinary.BasePositions.Single(p=>p.Position.Owner==owner.Owner&&p.Position.BindingId==owner.BindingId&&p.Position.BookId==owner.BookId).Position.Value;
                var quantity=ExactDecimal.Parse(expectedQuantities[owner]);
                if(held.Quantity.Amount!=quantity||value!=ExactMoney.Parse(f.Text("unitValue"),value.Currency).Scale(quantity))
                    throw new Exception("Ordinary operator PV changed an independently stated corrected stock/debt quantity or its exact marked value.");
            }
            await GasOwnedKnownHistoryStore.RequireScope(connection,database,frame,books,true,ct);
            var known=await GasOwnedKnownHistoryStore.Read(connection,database,frame,snapshot,books,ct);
            foreach(var item in cases)
            {
                var zero=ordinaryAndZero&&item.GetProperty("zero").GetBoolean();
                var title=known.Settlements.Single(p=>p.Id==item.Id("receiptSettlementId"));
                var dispatch=known.Settlements.Single(p=>p.Id==item.Id("dispatchSettlementId"));
                if(title.PositionId!=item.Id("positiveClaimId")||title.ReceiptLotId!=item.Id("receiptId")||title.ConsumptionId is not null||
                    title.Quantity.Amount!=ExactDecimal.Parse(zero?"0":ordinaryAndZero?"8":historical?"10":"9")||
                    dispatch.PositionId!=item.Id("negativeClaimId")||dispatch.ConsumptionId!=item.Id("consumptionId")||dispatch.ReceiptLotId is not null||
                    dispatch.Quantity.Amount!=ExactDecimal.Parse(ordinaryAndZero&&!zero?"1.25":historical?"2":"1.5")||
                    known.Quantities.Positions[item.Id("receiptId")].Revision!=(ordinaryAndZero?3:historical?1:2)||
                    known.Quantities.Consumptions[item.Id("consumptionId")].Revision!=(ordinaryAndZero&&!zero?3:historical?1:2))
                    throw new Exception("Known history lost its exact selected settlement, receipt/use financial revision or original native identity.");
                if(ordinaryAndZero&&(known.Quantities.Positions[item.Id("receiptId")].Retracted!=zero||
                    known.Quantities.Positions[item.Id("positiveClaimId")].Retracted!=zero||
                    known.Quantities.Consumptions[item.Id("futureLossId")].Revision!=2||
                    known.Quantities.Consumptions[item.Id("futureLossId")].Retracted!=zero))
                    throw new Exception("Ordinary-use and all-zero heads must retain exact revisions and retracted ancestry.");
                var loss=known.Consumptions.SingleOrDefault(p=>p.Id==item.Id("futureLossId"));
                if(zero?loss is not null:loss is null||loss.LotId!=item.Id("receiptId")||
                    loss.Quantity.Amount!=ExactDecimal.Parse(ordinaryAndZero?"1":"2")||loss.At!=f.Instant("lossAt")||loss.At<=at)
                    throw new Exception("The exact active future loss must remain known without early effectiveness; a withdrawn zero use must not reactivate.");
            }
            if(historical)return true;
            var selected=f.GetProperty("profiles").EnumerateArray().Select(p=>new GasOwnedProjectionSelection(p.Id("accountId"),p.Id("profileId"),p.GetProperty("revision").GetInt32())).ToImmutableArray();
            var request=new PortfolioEconomicProjectionRequest(PortfolioEconomicProjectionRequest.Version,f.Text("economicSession"),1,
                PortfolioEconomicProjectionRequest.SourceKnownSchedule,RateDiscountTiming.Version,PortfolioEconomicProjectionRequest.SelectedContractMarkets,
                new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.Nominal,
                    LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),selected);
            var prepared=await LifecyclePortfolioProjection.Prepare(connection,database,opening,request,null,ct);
            var results=prepared.Projections.Select(p=>p.Evaluate("operator_known_loss",[],ct)).ToArray();
            var values=results.SelectMany(p=>p.Values).ToArray();
            if(results.Any(p=>p.Horizon!=f.Instant("horizon"))||values.Length!=expectedOwners.Count||!values.Select(p=>p.Owner).ToHashSet().SetEquals(expectedOwners)||
                !prepared.AdditionalOwners.IsEmpty||prepared.Factors.Length!=1)
                throw new Exception("The full economic scope must reach its explicit loss-crossing horizon with exactly its actual native owners.");
            foreach(var value in values)
            {
                var original=ordinary.BasePositions.Single(p=>p.Position.Owner==value.Owner.Owner&&p.Position.BindingId==value.Owner.BindingId).Position.Value;
                var remaining=remainingQuantities[value.Owner];
                if(value.OpeningValue!=original||value.ResidualValue!=ExactMoney.Parse(f.Text("unitValue"),value.Currency).Scale(ExactDecimal.Parse(remaining))||
                    value.PeriodCash.Numerator!="0"||value.EconomicPnl!=value.ResidualValue.Subtract(value.OpeningValue))
                    throw new Exception("Known loss must reduce only the active receipt; funding/debt keep their independent owners and no bank cash is invented.");
            }
            var factor=prepared.Factors.Single();
            var shifted=prepared.Projections.SelectMany(p=>p.Evaluate("operator_price_up",
                [new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("0.1"),QuoteShift.Convention)],ct).Values).ToArray();
            foreach(var value in values)
            {
                var actual=shifted.Single(p=>p.Owner==value.Owner);var quantity=remainingQuantities[value.Owner];
                var delta=ExactUnitPrice.Parse("0.1",factor.Unit).ExactTimes(new(ExactDecimal.Parse(quantity),factor.Unit.Quantity));
                if(actual.OpeningValue!=value.OpeningValue||actual.PeriodCash!=value.PeriodCash||actual.ResidualValue!=value.ResidualValue.Add(delta)||
                    actual.EconomicPnl!=value.EconomicPnl.Add(delta))
                    throw new Exception("Shared market shocks must value each exact remaining stock/debt once, preserving original opening wealth and cash.");
            }
            if(ordinaryAndZero)
            {
                var currency=values[0].Currency;var unit=ExactMoney.Parse(f.Text("unitValue"),currency);
                var sourceTotal=values.Select(p=>p.OpeningValue).Aggregate((a,b)=>a.Add(b));
                var endTotal=values.Select(p=>p.ResidualValue).Aggregate((a,b)=>a.Add(b));
                if(sourceTotal!=unit.Scale(ExactDecimal.Parse("8"))||endTotal!=unit.Scale(ExactDecimal.Parse("7"))||
                    endTotal.Subtract(sourceTotal)!=unit.Scale(ExactDecimal.Parse("-1")))
                    throw new Exception("The complete five-owner active/zero scope must open at8P, close at7P and lose exactlyP without phantom zero owners.");
            }
            await EconomicRiskNativeChecks.VerifyGasSamples(database,connection,data,frame,books,pricing,request,f.Text("expectedHistoricalVar"),ct);
            await EconomicWorkerChecks.Submit(database,connection,data,frame,new(new(frame.Id,TradingBlotter.Convention,"retained",context),books,[],null),
                pricing,request,prepared,path,ct,includeCancellation:true);
            return true;
        },default);
        Console.WriteLine(ordinaryAndZero?
            "PASS whole-book operator ordinary/zero value: five exact owners, ordinary8P opening, known loss7P horizon, retained zero ancestry, signed per-owner shocks and complete worker requests":
            "PASS whole-book corrected operator value: historical/current native heads, ordinary opening equality, exact 9→7 known loss, signed stock/debt shocks and retained worker requests");
    }
}
