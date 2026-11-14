using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class PhysicalLifecycleProjectionChecks
{
    internal static void Run()
    {
        var explicitPolicy=new PhysicalFixture("1").Policy;
        var policyJson=System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(explicitPolicy))!.AsObject();
        policyJson.Remove("PhysicalDelivery");
        try{System.Text.Json.JsonSerializer.Deserialize<LifecycleProjectionPolicy>(policyJson.ToJsonString());throw new Exception("An omitted physical-delivery assumption was accepted.");}
        catch(System.Text.Json.JsonException){}
        foreach(var signed in new[]{"1","-1"})
        foreach(var path in new[]{LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Linear})
        {
            var f=new PhysicalFixture(signed);
            var prepared=f.Prepare(f.Policy with{PricePath=path});
            var shifts=prepared.Factors.Select(p=>new QuoteShift(p.Input,p.Quote,p.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)).ToImmutableArray();
            var value=prepared.Evaluate("physical_retained",shifts);
            var result=value.Values.Single();var sign=signed=="1"?"":"-";
            Equal(result.OpeningValue,sign+"80");Equal(result.EconomicPnl,sign+"80");
            Equal(result.PeriodCash,sign+(path==LifecycleProjectionPolicy.Hold?"80":"20"));
            Equal(result.ResidualValue,sign+(path==LifecycleProjectionPolicy.Hold?"80":"140"));
            var unchanged=prepared.Evaluate("unchanged_wealth",[]).Values.Single();
            Equal(unchanged.OpeningValue,sign+"80");Equal(unchanged.ResidualValue,sign+"80");Equal(unchanged.EconomicPnl,"0");
            var a=prepared.Factors.Single(p=>p.Curves.Contains(f.Component.SettlementProjection.Curve));
            Equal(prepared.Evaluate("futures_only",[new(a.Input,a.Quote,a.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]).Values.Single().EconomicPnl,"0");
            var remaining=value.States.Single().Residuals.Single();
            if(remaining.Instruments.Single() is not GasPhysicalDeliveryObligationInstrument||
                remaining.Positions.Single().Owner!=f.Component.Owner.Owner||remaining.Positions.Single().BindingId!=f.Component.Owner.BindingId||
                remaining.Setup.BindingStates.ContainsKey(new(f.Component.Owner.Owner,f.Component.Owner.BindingId)))
                throw new Exception("Final margin must leave exactly the ordinary signed delivery obligation under its original economic root, without title or accepted state.");
            if(prepared.Factors.Length!=2)throw new Exception("The future and surviving delivery mark must both enter the complete prepared factor union.");
        }
        foreach(var path in new[]{LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Linear})
        {
            var foreign=new PhysicalFixture("1",GasHoldingCarrying.CurrencyCurve,crossDefinition:true,prepareForeign:true);
            var b=foreign.DeliveryMarket!;
            var prepared=foreign.Prepare(foreign.Policy with{PricePath=path});
            if(prepared.Factors.Length!=2||prepared.Markets.Length!=2)throw new Exception("Both native source graphs and all physical branch factors must be retained before sampling.");
            var value=prepared.Evaluate("foreign_physical",[..prepared.Factors.Select(p=>new QuoteShift(p.Input,p.Quote,p.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention))]).Values.Single();
            // B's conditional consideration factor is .6/.8; its original
            // price/rate evidence is never relabelled as an A observation.
            Equal(value.ResidualValue,path==LifecycleProjectionPolicy.Hold?"130":"175");
            Equal(value.PeriodCash,path==LifecycleProjectionPolicy.Hold?"80":"20");
            Equal(value.OpeningValue,"128");
            Equal(value.EconomicPnl,path==LifecycleProjectionPolicy.Hold?"82":"67");
        }
        var conversion=new PhysicalFixture("1",GasHoldingCarrying.CurrencyCurve,execution:"2",delayedVm:true);
        var finalAt=conversion.Component.Events.Single(e=>e.Kind=="final_margin").At;
        var terminal=conversion.Horizon with{Closes=[new(new(2040,12,27),finalAt,0)]};
        var atFinal=conversion.Prepare(horizon:terminal).Evaluate("unchanged_final_conversion",[]).Values.Single();
        // Opening and final: VM40*.95 plus (B5-A3*.6)*40.
        // The accepted/path margin cash survives unpaid at its own later due.
        Equal(atFinal.OpeningValue,"166");Equal(atFinal.ResidualValue,"166");Equal(atFinal.PeriodCash,"0");Equal(atFinal.EconomicPnl,"0");
        var discounted=new PhysicalFixture("1",GasHoldingCarrying.CurrencyCurve);
        var discountedPath=discounted.Prepare();
        var discountResult=discountedPath.Evaluate("dated_delivery_carry",[..discountedPath.Factors.Select(p=>new QuoteShift(p.Input,p.Quote,p.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention))]);
        // At H the physical mark is seven; the fixed five-unit consideration
        // is still due later, with conditional factor .6/.8=.75.
        Equal(discountResult.Values.Single().OpeningValue,"128");
        Equal(discountResult.Values.Single().ResidualValue,"130");Equal(discountResult.Values.Single().EconomicPnl,"82");
        var missingCurve=new PhysicalFixture("1",GasHoldingCarrying.CurrencyCurve,provideCurve:false);
        Reject("projection_delivery_discount",()=>LifecycleProjection.Prepare(missingCurve.Model,missingCurve.Source,missingCurve.Horizon,missingCurve.Policy,[missingCurve.Component],[]));
        var cross=new PhysicalFixture("1",crossDefinition:true);
        Reject("projection_delivery_source",()=>LifecycleProjection.Prepare(cross.Model,cross.Source,cross.Horizon,cross.Policy,[cross.Component],[]));
        var beforeFinal=cross.Horizon with{Closes=[new(new(2040,12,27),cross.Source.ValuationAt.AddHours(1),0)]};
        Reject("projection_delivery_source",()=>LifecycleProjection.Prepare(cross.Model,cross.Source,beforeFinal,cross.Policy,[cross.Component],[]));
        Reject("projection_policy",()=>LifecycleProjection.Prepare(cross.Model,cross.Source,beforeFinal,cross.Policy with{PhysicalDelivery=""},[cross.Component],[]));
        var actualFinal=new PhysicalFixture("1",nativeFinal:true);
        Reject("futures_delivery_transition",()=>LifecycleProjection.Prepare(actualFinal.Model,actualFinal.Source,actualFinal.Horizon,actualFinal.Policy,[actualFinal.Component],[]));
        var native=new PhysicalFixture("1",nativeFinal:true,nativeLink:true);
        var nativeValue=native.Prepare().Evaluate("accepted_native_delivery",[]);
        Equal(nativeValue.Values.Single().OpeningValue,"80");Equal(nativeValue.Values.Single().ResidualValue,"80");Equal(nativeValue.Values.Single().EconomicPnl,"0");
        var connected=new GasPhysicalLifecycleProjection(native.Component,native.NativeAttachment!);
        var continued=native.Prepare(projection:connected).Evaluate("separate_native_economic_roots",[]);
        var oldRoot=continued.Values.Single(v=>v.Owner==native.Component.Owner);var nativeRoot=continued.Values.Single(v=>v.Owner==native.NativeOwner);
        Equal(oldRoot.OpeningValue,"80");Equal(oldRoot.ResidualValue,"0");Equal(oldRoot.EconomicPnl,"-80");
        Equal(nativeRoot.OpeningValue,"0");Equal(nativeRoot.ResidualValue,"80");Equal(nativeRoot.EconomicPnl,"80");
        Equal(continued.Values.Aggregate(ExactMoney.Parse("0",native.Component.Currency),(sum,value)=>sum.Add(value.EconomicPnl)),"0");
        SharedNativeParents(native);
        ActiveNativeParent();
        PrincipalCashDemand(native);
        var title=Title(native.NativeAttachment!);
        ProjectedOperatorReceipt(native);
        Reject("gas_projection_scope",()=>native.Prepare(projection:TitleQuantities(title,native.NativeFact!)));
        var active=ActivePhysicalQuantity(title);
        Reject("gas_projection_scope",()=>LifecycleProjection.Prepare(native.Model,active.SourceSnapshot,native.Horizon,native.Policy,[active],[]));
        var titled=native.Prepare(projection:new GasPhysicalLifecycleProjection(native.Component,title,TitleQuantities(title,native.NativeFact!),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)))
            .Evaluate("accepted_title_and_unpaid_principal",[]);
        Equal(titled.Values.Single(v=>v.Owner==native.NativeOwner).ResidualValue,"60");
        Equal(titled.Values.Single(v=>v.Owner==title.Stocks.Single().Owner).OpeningValue,"0");
        Equal(titled.Values.Single(v=>v.Owner==title.Stocks.Single().Owner).ResidualValue,"50");
        Equal(titled.Values.Single(v=>v.Owner==title.Cash.Single().Owner).ResidualValue,"-30");
        Equal(titled.Values.Aggregate(ExactMoney.Parse("0",native.Component.Currency),(sum,value)=>sum.Add(value.EconomicPnl)),"0");
        var retired=RetireTitle(title);
        var retiredValue=native.Prepare(projection:new GasPhysicalLifecycleProjection(native.Component,retired,TitleQuantities(retired,native.NativeFact!),
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted))).Evaluate("retracted_title_preserves_principal_and_refund",[]);
        Equal(retiredValue.Values.Single(v=>v.Owner==native.NativeOwner).ResidualValue,"80");
        Equal(retiredValue.Values.Aggregate(ExactMoney.Parse("0",native.Component.Currency),(sum,value)=>sum.Add(value.ResidualValue)),"95");
        Equal(retiredValue.Values.Aggregate(ExactMoney.Parse("0",native.Component.Currency),(sum,value)=>sum.Add(value.PeriodCash)),"-15");
        Equal(retiredValue.Values.Aggregate(ExactMoney.Parse("0",native.Component.Currency),(sum,value)=>sum.Add(value.EconomicPnl)),"0");
        if(retiredValue.Values.Any(v=>v.Owner==title.Stocks.Single().Owner))throw new Exception("A retracted title cannot leave active owned gas.");
        var returned=native.Prepare(projection:new GasPhysicalLifecycleProjection(native.Component,title,TitleQuantities(title,native.NativeFact!,receiveThenReturn:true),
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted))).Evaluate("accepted_physical_receipt_funds_native_loan_return",[]);
        Equal(returned.Values.Single(v=>v.Owner==title.Stocks.Single().Owner).ResidualValue,"30");
        Equal(returned.Values.Aggregate(ExactMoney.Parse("0",native.Component.Currency),(sum,value)=>sum.Add(value.EconomicPnl)),"0");
        var shortNative=new PhysicalFixture("-1",nativeFinal:true,nativeLink:true);var shortTitle=Title(shortNative.NativeAttachment!);
        var dispatched=shortNative.Prepare(projection:new GasPhysicalLifecycleProjection(shortNative.Component,shortTitle,
            TitleQuantities(shortTitle,shortNative.NativeFact!),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)))
            .Evaluate("accepted_identified_stock_dispatch",[]);
        Equal(dispatched.Values.Single(v=>v.Owner==shortNative.NativeOwner).ResidualValue,"-60");
        Equal(dispatched.Values.Single(v=>v.Owner==shortTitle.Stocks.Single().Owner).OpeningValue,"50");
        Equal(dispatched.Values.Single(v=>v.Owner==shortTitle.Stocks.Single().Owner).ResidualValue,"0");
        Equal(dispatched.Values.Single(v=>v.Owner==shortTitle.Cash.Single().Owner).ResidualValue,"30");
        Equal(dispatched.Values.Aggregate(ExactMoney.Parse("0",shortNative.Component.Currency),(sum,value)=>sum.Add(value.EconomicPnl)),"0");
        var foreignNative=new PhysicalFixture("1",nativeFinal:true,nativeLink:true,crossDefinition:true,prepareForeign:true);
        ProjectedOperatorReceipt(foreignNative);
        var foreignTitle=Title(foreignNative.NativeAttachment!);
        Reject("gas_projection_source",()=>TitleQuantities(foreignTitle,foreignNative.NativeFact!));
        var foreignValue=foreignNative.Prepare(projection:new GasPhysicalLifecycleProjection(foreignNative.Component,foreignTitle,
            TitleQuantities(foreignTitle,foreignNative.NativeFact!,foreignNative.DeliveryMarket!.Source),
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted))).Evaluate("accepted_foreign_inventory_title",[]);
        Equal(foreignValue.Values.Aggregate(ExactMoney.Parse("0",foreignNative.Component.Currency),(sum,value)=>sum.Add(value.EconomicPnl)),"0");
        var carried=new PhysicalFixture("1",GasHoldingCarrying.CurrencyCurve,nativeFinal:true,nativeLink:true,crossDefinition:true,prepareForeign:true);
        PrincipalCashDemand(carried);
        var carriedTitle=Title(carried.NativeAttachment!);
        var carriedQuantity=TitleQuantities(carriedTitle,carried.NativeFact!,carried.DeliveryMarket!.Source,physicalCarrying:carried.PrincipalCurve);
        var carriedValue=carried.Prepare(projection:new GasPhysicalLifecycleProjection(carried.Component,carriedTitle,carriedQuantity,carried.PrincipalCurve))
            .Evaluate("profile_carrying_preserved_at_native_title",[]);
        Equal(carriedValue.Values.Single(v=>v.Owner==carried.NativeOwner).ResidualValue,"82.5");
        Equal(carriedValue.Values.Single(v=>v.Owner==carriedTitle.Cash.Single().Owner).ResidualValue,"-22.5");
        Equal(carriedValue.Values.Aggregate(ExactMoney.Parse("0",carried.Component.Currency),(sum,v)=>sum.Add(v.ResidualValue)),"110");
        Equal(carriedValue.Values.Aggregate(ExactMoney.Parse("0",carried.Component.Currency),(sum,v)=>sum.Add(v.EconomicPnl)),"-18");
        Reject("gas_physical_projection",()=>new GasPhysicalLifecycleProjection(carried.Component,carriedTitle,carriedQuantity,
            new HeldCurve(carried.DeliveryMarket.Source,carried.Component.Currency,carried.Horizon.Closes[0].Instant,carriedTitle.Terms.CashDueAt)));
        foreach(var(hours,expectedCash,expectedPeriod)in new[]{(0,"-30","0"),(1,"0","-30")})
        {
            var dated=new PhysicalFixture("1",nativeFinal:true,nativeLink:true,cashDueHours:hours);var performance=Title(dated.NativeAttachment!);
            var value=dated.Prepare(projection:new GasPhysicalLifecycleProjection(dated.Component,performance,TitleQuantities(performance,dated.NativeFact!),
                new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted))).Evaluate("principal_origin_and_due",[]);
            Equal(value.Values.Single(v=>v.Owner==performance.Cash.Single().Owner).ResidualValue,expectedCash);
            Equal(value.Values.Single(v=>v.Owner==performance.Cash.Single().Owner).PeriodCash,expectedPeriod);
            Equal(value.Values.Aggregate(ExactMoney.Parse("0",dated.Component.Currency),(sum,v)=>sum.Add(v.EconomicPnl)),"0");
        }
        var missingCash=title with{Cash=[]};
        Reject("gas_physical_projection",()=>new GasPhysicalLifecycleProjection(native.Component,missingCash,TitleQuantities(missingCash,native.NativeFact!),
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
        var missingPrincipal=title with{PrincipalRuns=[],Cash=[]};
        Reject("gas_physical_projection",()=>new GasPhysicalLifecycleProjection(native.Component,missingPrincipal,TitleQuantities(missingPrincipal,native.NativeFact!),
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
        var wrongAmount=new Quantity(ExactDecimal.Parse("-31"),native.Component.Currency);var originalCash=title.Cash.Single();
        var wrongPrincipal=title with{PrincipalRuns=[title.PrincipalRuns.Single() with{Steps=[title.PrincipalRuns.Single().Steps.Single() with{
            ExactCumulative=ExactMoney.Parse("-31",native.Component.Currency),RoundedCumulative=new(wrongAmount.Amount,native.Component.Currency),
            Contribution=new(wrongAmount.Amount,native.Component.Currency),Adjustment=new(wrongAmount.Amount,native.Component.Currency)}]}],
            Cash=[originalCash with{Quantity=wrongAmount,State=new(originalCash.Owner,originalCash.Instrument,wrongAmount,[],RecordedCashValuation.Policy,
                native.Source.Knowledge!.ContextId,native.Source.Knowledge.KnownAt)}]};
        Reject("gas_physical_projection",()=>new GasPhysicalLifecycleProjection(native.Component,wrongPrincipal,TitleQuantities(wrongPrincipal,native.NativeFact!),
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
        var sameInstantTitle=Title(native.NativeAttachment!,0);
        var sameInstantValue=native.Prepare(projection:new GasPhysicalLifecycleProjection(native.Component,sameInstantTitle,
            TitleQuantities(sameInstantTitle,native.NativeFact!),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted))).Evaluate("final_before_same_instant_title",[]);
        Equal(sameInstantValue.Values.Aggregate(ExactMoney.Parse("0",native.Component.Currency),(sum,v)=>sum.Add(v.EconomicPnl)),"0");
        Reject("gas_projection_source",()=>new GasPhysicalLifecycleProjection(native.Component,sameInstantTitle,
            TitleQuantities(sameInstantTitle,native.NativeFact!,transitionChange:t=>t with{Requires=[]}),
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
        Reject("gas_physical_projection",()=>new GasPhysicalLifecycleProjection(native.Component,title));
        Reject("gas_projection_scope",()=>new GasPhysicalLifecycleProjection(native.Component,title,TitleQuantities(title,native.NativeFact!,anotherPhysicalParent:true),
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
        foreach(var change in new Func<GasKnownQuantityTransition,GasKnownQuantityTransition>[] {
            t=>t with{Id=Guid.NewGuid()},t=>t with{KnownAt=native.NativeFact!.KnownAt}})
            Reject("gas_projection_source",()=>new GasPhysicalLifecycleProjection(native.Component,title,
                TitleQuantities(title,native.NativeFact!,transitionChange:change),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
        Reject("gas_physical_projection",()=>new GasPhysicalLifecycleProjection(native.Component,title,TitleQuantities(title,native.NativeFact!),
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Explicit)));
        Reject("gas_projection_source",()=>new GasPhysicalLifecycleProjection(native.Component,title,
            TitleQuantities(title,native.NativeFact!,native.Source with{Id=Guid.NewGuid()}),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
        if(nativeValue.States.Single().Residuals.Single().Positions.Single().Owner!=native.NativeOwner!.Value.Owner||
            !nativeValue.States.Single().KnownResidualOwners.Contains(native.NativeOwner.Value))
            throw new Exception("An accepted final must activate its exact predeclared native delivery owner, without another hypothetical obligation.");
        var probe=new FinalProbe(new(Guid.NewGuid(),Guid.NewGuid(),native.Component.Owner.BookId),native.Source,native.Component.Currency,
            native.Component.SettlementProjection,native.Horizon.Closes[0].Instant);
        var withProbe=native.Prepare(extra:[probe]);var primitive=withProbe.Factors.Single(f=>f.Curves.Contains(native.Component.SettlementProjection.Curve));
        var fixedInput=withProbe.Evaluate("shared_native_actual_final",[new(primitive.Input,primitive.Quote,primitive.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]);
        Equal(fixedInput.Values.Single(v=>v.Owner==probe.Owner).PeriodCash,"3");
        var originalNative=native.NativeAttachment!;var prior=originalNative.CurrentFinal with{DeterminationId=Guid.NewGuid(),MarginRevision=2,
            Receipt=new(1,originalNative.CurrentFinal.Receipt.KnownAt.AddMinutes(-1))};
        var current=originalNative.CurrentFinal with{MarginRevision=3,PreviousDeterminationId=prior.DeterminationId};
        var corrected=native.NativeFact! with{RunRevision=3,PreviousDeterminationId=prior.DeterminationId};
        var ancestry=originalNative with{InitialDeterminationId=prior.DeterminationId,CurrentFinal=current,Finals=[current,prior],Receipt=prior.Receipt};
        var correction=KnownFuturesPhysicalFinal.Create(ancestry,corrected);
        if(correction.InitialDeterminationId!=prior.DeterminationId||correction.Determination!=corrected)
            throw new Exception("Native final ancestry must preserve the exact initial link and selected corrected head independently of array order.");
        Reject("projection_native_delivery",()=>KnownFuturesPhysicalFinal.Create(originalNative with{Finals=originalNative.Finals.Add(prior)},native.NativeFact!));
        Reject("projection_native_delivery",()=>KnownFuturesPhysicalFinal.Create(originalNative,native.NativeFact! with{
            Observation=native.NativeFact!.Observation with{Role=ObservationRole.DailySettlement}}));
        foreach(var invalid in new Func<GasPhysicalKnownAttachment,GasPhysicalKnownAttachment>[] {
            a=>a with{DeliveryOwner=new(a.FutureOwner.TradeId,Guid.NewGuid(),a.FutureOwner.BookId)},
            a=>a with{DeliveryOwner=a.DeliveryOwner with{BookId=Guid.NewGuid()}},
            a=>a with{InitialDeterminationId=Guid.NewGuid()},
            a=>a with{Receipt=a.Receipt with{PublicationOrder=7},CurrentFinal=a.CurrentFinal with{Receipt=a.CurrentFinal.Receipt with{PublicationOrder=7}},
                Finals=[a.CurrentFinal with{Receipt=a.CurrentFinal.Receipt with{PublicationOrder=7}}]},
            a=>a with{OriginalQuantity=new(ExactDecimal.Parse("80"),a.OriginalQuantity.Specification)},
            a=>a with{CurrentFinal=a.CurrentFinal with{Price=ExactUnitPrice.Parse(a.CurrentFinal.Price.ToExactString(),new PriceUnit(a.CurrentFinal.Price.Unit.Currency,a.CurrentFinal.Price.Unit.Quantity,ExactDecimal.Parse("2")))},
                Finals=[..a.Finals.Select(f=>f with{Price=ExactUnitPrice.Parse(f.Price.ToExactString(),new PriceUnit(f.Price.Unit.Currency,f.Price.Unit.Quantity,ExactDecimal.Parse("2")))})]},
            a=>a with{ProfileId=Guid.NewGuid()},
            a=>a with{SourceSnapshot=a.SourceSnapshot with{ValuationAt=a.SourceSnapshot.ValuationAt.AddHours(1)}}})
            Reject("projection_native_delivery",()=>new PhysicalFixture("1",nativeFinal:true,nativeLink:true,nativeTransform:invalid));
        Console.WriteLine("PASS physical lifecycle projection: signed final VM plus ordinary retained delivery wealth, full branch factors and no assumed title");
    }
    private static void Equal(ExactMoney value,string expected)
    {if(value!=ExactMoney.Parse(expected,value.Currency))throw new Exception($"Expected physical wealth {expected}, got {value.ToExactString()}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected physical projection refusal "+code);}

    private static void SharedNativeParents(PhysicalFixture longSide)
    {
        var shortSide=new PhysicalFixture("-1",nativeFinal:true,nativeLink:true,shared:longSide);
        var receipt=Title(longSide.NativeAttachment!);var dispatch=Title(shortSide.NativeAttachment!,2);
        var received=receipt.Stocks.Single();var oldSupply=dispatch.Performances.Single().Supply.Single();
        var consumed=new GasStockConsumption(oldSupply.ConsumptionId,received.Owner.Owner.Id,oldSupply.Quantity,GasStockUse.Delivery,
            dispatch.Performances.Single().PerformedAt,oldSupply.Receipt.KnownAt,oldSupply.EvidenceId,null);
        var stock=received with{Consumptions=[consumed]};
        receipt=receipt with{Stocks=[stock]};
        dispatch=dispatch with{Stocks=[stock],Performances=[dispatch.Performances.Single() with{Supply=[oldSupply with{StockOwner=stock.Owner}]}]};
        var account=new GasProjectionAccount(stock.AccountId,stock.Owner.BookId,stock.LegalOwner,receipt.Terms.Location);
        GasProjectionHolding Obligation(GasPhysicalKnownAttachment n)=>new(n.DeliveryOwner,account,GasProjectionHoldingKind.PhysicalDelivery,
            (GasPhysicalDeliveryObligationInstrument)n.Terms.CreateObligation(n.CurrentFinal.Price),n.OriginalQuantity,null,ImmutableDictionary<Guid,ExactDecimal>.Empty,null);
        var incoming=new GasProjectionHolding(stock.Owner,account,GasProjectionHoldingKind.OwnedStock,stock.OriginalInstrument,stock.Lot.Quantity,
            stock.Lot.AvailableAt,ImmutableDictionary<Guid,ExactDecimal>.Empty,null);
        var longOrigin=LifecycleProjectionEvent.ScopedId(receipt.FutureOwner,receipt.FinalOccurrence.Id);
        var shortOrigin=LifecycleProjectionEvent.ScopedId(dispatch.FutureOwner,dispatch.FinalOccurrence.Id);
        var titleId=LifecycleProjectionEvent.ScopedId(receipt.DeliveryOwner,receipt.Performances.Single().Id);
        var dispatchId=LifecycleProjectionEvent.ScopedId(dispatch.DeliveryOwner,dispatch.Performances.Single().Id);
        var quantity=new Quantity(ExactDecimal.Parse("10"),receipt.Terms.Quantity);
        var known=ImmutableArray.Create(
            new GasKnownQuantityTransition(longOrigin,receipt.EffectiveAt,receipt.CurrentFinal.Receipt.KnownAt,[],[],[],[]){
                PhysicalOrigins=[new(KnownFuturesPhysicalFinal.Create(receipt,longSide.NativeFact!),Obligation(receipt))]},
            new GasKnownQuantityTransition(shortOrigin,dispatch.EffectiveAt,dispatch.CurrentFinal.Receipt.KnownAt,[],[],[],[]){
                PhysicalOrigins=[new(KnownFuturesPhysicalFinal.Create(dispatch,shortSide.NativeFact!),Obligation(dispatch))]},
            new GasKnownQuantityTransition(titleId,receipt.Performances.Single().PerformedAt,receipt.Performances.Single().Receipt.KnownAt,[longOrigin],[],
                [new(receipt.DeliveryOwner,quantity)],[new(receipt.DeliveryOwner,incoming)]),
            new GasKnownQuantityTransition(dispatchId,consumed.At,dispatch.Performances.Single().Receipt.KnownAt,[shortOrigin,titleId],
                [new(stock.Owner,new(consumed.Id,consumed.At,GasKnownStockUse.Consume,null,quantity),dispatch.DeliveryOwner)],
                [new(dispatch.DeliveryOwner,quantity)],[]));
        var quantities=new GasQuantityLifecycleProjection(ImmutableDictionary<Guid,GasQuantityProjectionPolicy>.Empty.Add(account.Id,
            new(GasQuantityProjectionPolicy.KnownVersion,GasQuantityProjectionPolicy.Retain)),longSide.Source,[],[],known);
        var nominal=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        ImmutableArray<GasPhysicalProjectionParent> parents=[new(longSide.Component,receipt,KnownFuturesPhysicalFinal.Create(receipt,longSide.NativeFact!),nominal),
            new(shortSide.Component,dispatch,KnownFuturesPhysicalFinal.Create(dispatch,shortSide.NativeFact!),nominal)];
        var forgedStock=incoming with{Instrument=new GasCommodityHoldingInstrument(Guid.NewGuid(),
            receipt.Terms.Mark with{Contract=new(receipt.Terms.Mark.Contract.Revision,Guid.NewGuid())},receipt.Terms.Quantity,receipt.Terms.Currency,null,GasHoldingCarrying.Nominal)};
        var forged=known.SetItem(2,known[2] with{Receipts=[new(receipt.DeliveryOwner,forgedStock)]});
        var forgedQuantities=new GasQuantityLifecycleProjection(ImmutableDictionary<Guid,GasQuantityProjectionPolicy>.Empty.Add(account.Id,
            new(GasQuantityProjectionPolicy.KnownVersion,GasQuantityProjectionPolicy.Retain)),longSide.Source,[],[],forged);
        Reject("gas_projection_source",()=>new GasPhysicalLifecycleProjection(parents,forgedQuantities));
        var movement=known[3].Stock.Single();
        var wrongReservation=known.SetItem(3,known[3] with{Stock=[movement with{Operation=movement.Operation with{ReservationId=Guid.NewGuid()}}]});
        var wrongSupply=new GasQuantityLifecycleProjection(ImmutableDictionary<Guid,GasQuantityProjectionPolicy>.Empty.Add(account.Id,
            new(GasQuantityProjectionPolicy.KnownVersion,GasQuantityProjectionPolicy.Retain)),longSide.Source,[],[],wrongReservation);
        Reject("gas_projection_source",()=>new GasPhysicalLifecycleProjection(parents,wrongSupply));
        foreach(var badLot in new[]{stock.Lot with{AccountId=Guid.NewGuid()},stock.Lot with{EvidenceId=Guid.NewGuid()}})
            Reject("gas_projection_source",()=>new GasPhysicalLifecycleProjection([parents[0] with{Native=receipt with{Stocks=[stock with{Lot=badLot}] }},parents[1]],quantities));
        var projection=new GasPhysicalLifecycleProjection(parents,quantities);
        var reordered=new GasPhysicalLifecycleProjection([parents[1],parents[0]],quantities);
        if(projection.Owner!=reordered.Owner||!projection.SourceSnapshot.HasSameInputs(quantities.SourceSnapshot)||
            !projection.SourceSnapshot.HasSameInputs(reordered.SourceSnapshot)||!projection.Dependencies.SequenceEqual(reordered.Dependencies)||
            !projection.EconomicOwners.SequenceEqual(reordered.EconomicOwners))throw new Exception("Parent order changed the shared resource component's source or identity.");
        var result=longSide.Prepare(projection:projection).Evaluate("shared_native_stock_across_two_physical_parents",[]);
        Equal(result.Values.Single(v=>v.Owner==receipt.DeliveryOwner).ResidualValue,"60");
        Equal(result.Values.Single(v=>v.Owner==dispatch.DeliveryOwner).ResidualValue,"-60");
        Equal(result.Values.Single(v=>v.Owner==receipt.Cash.Single().Owner).ResidualValue,"-30");
        Equal(result.Values.Single(v=>v.Owner==dispatch.Cash.Single().Owner).ResidualValue,"30");
        Equal(result.Values.Single(v=>v.Owner==stock.Owner).ResidualValue,"0");
        Equal(result.Values.Aggregate(ExactMoney.Parse("0",longSide.Component.Currency),(a,b)=>a.Add(b.EconomicPnl)),"0");
        Reject("gas_projection_scope",()=>new GasPhysicalLifecycleProjection([parents[0]],quantities));
        Reject("gas_physical_projection",()=>new GasPhysicalLifecycleProjection([parents[0],parents[0]],quantities));
        Console.WriteLine("PASS shared physical continuation: two native futures, one accepted receipt and one identified stock dispatch, principal retained once");
    }
    private static void ActiveNativeParent()
    {
        foreach(var overdue in new[]{false,true})
        {
            var fixture=new PhysicalFixture("1",overdue?GasHoldingCarrying.CurrencyCurve:GasHoldingCarrying.Nominal,nativeFinal:true,nativeLink:true,cashDueHours:overdue?1:null);
            var first=Title(fixture.NativeAttachment!);var original=first.Performances.Single();var oldStock=first.Stocks.Single();
            var openingAt=original.PerformedAt.AddMinutes(30);
            var source=first.SourceSnapshot with{Id=Guid.NewGuid(),ValuationAt=openingAt,MarketCutoff=openingAt};
            var at=original.PerformedAt.AddHours(1);var receipt=new GasPhysicalKnownReceipt(4,source.Knowledge!.KnownAt);
            var quantity=new Quantity(ExactDecimal.Parse("5"),first.Terms.Quantity);var cumulative=new Quantity(ExactDecimal.Parse("15"),quantity.Specification);
            var stockId=Guid.NewGuid();var stockOwner=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,stockId),stockId,first.DeliveryOwner.BookId);
            var stock=oldStock with{Owner=stockOwner,OriginalInstrument=new GasCommodityHoldingInstrument(Guid.NewGuid(),oldStock.OriginalInstrument.Mark,first.Terms.Quantity,first.Terms.Currency,null,GasHoldingCarrying.Nominal),
                Lot=oldStock.Lot with{Id=stockId,Quantity=quantity,TitleAt=at,AvailableAt=at},Receipt=receipt};
            var performance=original with{Id=Guid.NewGuid(),BusinessReference="synthetic later title",SignedQuantity=quantity,PerformedAt=at,
                ReceiptStockOwner=stockOwner,TitleEvidenceId=Guid.NewGuid(),Receipt=receipt};
            var firstStep=first.PrincipalRuns.Single().Steps.Single();var retainedStep=firstStep with{Id=Guid.NewGuid(),PreviousDeterminationId=firstStep.Id,
                Adjustment=new(default,first.Terms.Currency)};
            var nextStep=new GasPhysicalKnownPrincipalStep(Guid.NewGuid(),performance.Id,null,retainedStep.Id,cumulative,ExactMoney.Parse("-45",first.Terms.Currency),
                new(ExactDecimal.Parse("-45"),first.Terms.Currency),new(ExactDecimal.Parse("-15"),first.Terms.Currency),new(ExactDecimal.Parse("-15"),first.Terms.Currency),1);
            var cashId=Guid.NewGuid();var cashOwner=new AccountingPositionKey(first.DeliveryOwner.Owner,cashId,first.DeliveryOwner.BookId);
            var cashQuantity=new Quantity(ExactDecimal.Parse("-15"),first.Terms.Currency);var instrument=first.Cash.Single().Instrument;
            var cash=new GasPhysicalKnownCash(cashId,nextStep.Id,cashOwner,instrument,cashQuantity,
                new(cashOwner,instrument,cashQuantity,[],RecordedCashValuation.Policy,source.Knowledge.ContextId,source.Knowledge.KnownAt),at,receipt);
            stock=stock with{Lot=stock.Lot with{EvidenceId=performance.TitleEvidenceId!.Value}};
            var native=first with{SourceSnapshot=source,Performances=[original,performance],Stocks=[oldStock,stock],Cash=first.Cash.Add(cash),
                PrincipalRuns=first.PrincipalRuns.Add(new(2,first.CurrentFinal.DeterminationId,source.Knowledge.ContextId,at,[retainedStep,nextStep],receipt))};
            var account=new GasProjectionAccount(oldStock.AccountId,oldStock.Owner.BookId,oldStock.LegalOwner,first.Terms.Location);
            var obligation=new GasProjectionHolding(first.DeliveryOwner,account,GasProjectionHoldingKind.PhysicalDelivery,
                (GasPhysicalDeliveryObligationInstrument)first.Terms.CreateObligation(first.CurrentFinal.Price),new(ExactDecimal.Parse("30"),first.Terms.Quantity),null,
                ImmutableDictionary<Guid,ExactDecimal>.Empty,null);
            GasProjectionHolding Stock(GasPhysicalKnownStock held)=>new(held.Owner,account,GasProjectionHoldingKind.OwnedStock,held.OriginalInstrument,
                held.Lot.Quantity,held.Lot.AvailableAt,ImmutableDictionary<Guid,ExactDecimal>.Empty,null);
            var transition=new GasKnownQuantityTransition(LifecycleProjectionEvent.ScopedId(native.DeliveryOwner,performance.Id),at,receipt.KnownAt,[],[],
                [new(native.DeliveryOwner,quantity)],[new(native.DeliveryOwner,Stock(stock))]);
            var quantities=new GasQuantityLifecycleProjection(ImmutableDictionary<Guid,GasQuantityProjectionPolicy>.Empty.Add(account.Id,
                new(GasQuantityProjectionPolicy.KnownVersion,GasQuantityProjectionPolicy.Retain)),source,[obligation,Stock(oldStock)],[],[transition]);
            var accepted=KnownFuturesPhysicalFinal.Create(native,fixture.NativeFact!);
            var parent=new GasPhysicalProjectionParent(null,native,accepted,overdue?null:new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
            var projection=new GasPhysicalLifecycleProjection([parent],quantities);
            var prepared=LifecycleProjection.Prepare(fixture.Model,source,fixture.Horizon,fixture.Policy,[projection],[]);
            var result=prepared.Evaluate(overdue?"active_overdue_principal":"active_and_future_principal",[]);
            Equal(result.Values.Single(v=>v.Owner==native.DeliveryOwner).OpeningValue,"60");
            Equal(result.Values.Single(v=>v.Owner==native.DeliveryOwner).ResidualValue,"50");
            Equal(result.Values.Single(v=>v.Owner==native.Cash[0].Owner).OpeningValue,"-30");
            Equal(result.Values.Single(v=>v.Owner==native.Cash[0].Owner).ResidualValue,"-30");
            Equal(result.Values.Single(v=>v.Owner==cashOwner).OpeningValue,"0");
            Equal(result.Values.Single(v=>v.Owner==cashOwner).ResidualValue,"-15");
            Equal(result.Values.Aggregate(ExactMoney.Parse("0",native.Terms.Currency),(a,b)=>a.Add(b.EconomicPnl)),"0");
            if(result.Values.Any(v=>v.Owner==native.FutureOwner)||prepared.Factors.Length!=1)
                throw new Exception("An already active native obligation cannot manufacture an opening future or expired future price dependency.");
            var factor=prepared.Factors.Single();
            var shifted=prepared.Evaluate("active_inventory_move",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("1"),QuoteShift.Convention)]);
            Equal(shifted.Values.Aggregate(ExactMoney.Parse("0",native.Terms.Currency),(a,b)=>a.Add(b.EconomicPnl)),"40");
            Reject("gas_physical_projection",()=>new GasPhysicalLifecycleProjection([parent with{Native=native with{Cash=[]}}],quantities));
            if(!overdue)
            {
                GasPhysicalKnownCash Paid(GasPhysicalKnownCash claim,DateTimeOffset paidAt)=>claim with{State=new(claim.Owner,claim.Instrument,claim.Quantity,
                    [new(Guid.NewGuid(),1,claim.Owner,paidAt,source.Knowledge.KnownAt,
                        ExactMoney.FromMoney(new(claim.Quantity.Amount,native.Terms.Currency)),AccountingFlowKind.EconomicCash)],
                    RecordedCashValuation.Policy,source.Knowledge.ContextId,source.Knowledge.KnownAt)};
                var paid=native with{Cash=[Paid(native.Cash[0],openingAt.AddMinutes(-15)),Paid(cash,at.AddMinutes(15))]};
                var noUnusedProvider=new GasPhysicalLifecycleProjection([parent with{Native=paid,PrincipalDiscounting=null}],quantities);
                var paidResult=LifecycleProjection.Prepare(fixture.Model,source,fixture.Horizon,fixture.Policy,[noUnusedProvider],[])
                    .Evaluate("already_paid_and_known_future_paid_principal",[]);
                Equal(paidResult.Values.Single(v=>v.Owner==native.Cash[0].Owner).OpeningValue,"0");
                Equal(paidResult.Values.Single(v=>v.Owner==native.Cash[0].Owner).ResidualValue,"0");
                Equal(paidResult.Values.Single(v=>v.Owner==cashOwner).ResidualValue,"0");
                Equal(paidResult.Values.Aggregate(ExactMoney.Parse("0",native.Terms.Currency),(a,b)=>a.Add(b.EconomicPnl)),"0");
                Reject("gas_physical_projection",()=>new GasPhysicalLifecycleProjection([parent with{PrincipalDiscounting=null}],quantities));
            }
        }
        Console.WriteLine("PASS active physical continuation: exact partial opening obligation, past and future principal, overdue claims and independent native owner");
    }
    private static void PrincipalCashDemand(PhysicalFixture fixture)
    {
        var native=Title(fixture.NativeAttachment!);var cash=native.Cash.Single();
        var paidAt=cash.EffectiveAt.AddMinutes(30);
        var paid=cash with{State=new(cash.Owner,cash.Instrument,cash.Quantity,
            [new(Guid.NewGuid(),1,cash.Owner,paidAt,native.SourceSnapshot.Knowledge!.KnownAt,
                ExactMoney.Parse("-30",native.Terms.Currency),AccountingFlowKind.EconomicCash)],RecordedCashValuation.Policy,
            native.SourceSnapshot.Knowledge.ContextId,native.SourceSnapshot.Knowledge.KnownAt)};
        var paidNative=native with{Cash=[paid]};
        GasQuantityLifecycleProjection Quantities(GasPhysicalKnownAttachment selected)=>TitleQuantities(selected,fixture.NativeFact!,fixture.DeliveryMarket?.Source,
            physicalCarrying:selected.Terms.Carrying==GasHoldingCarrying.CurrencyCurve?fixture.PrincipalCurve:null);
        var projection=new GasPhysicalLifecycleProjection(fixture.Component,paidNative,Quantities(paidNative),null);
        var result=fixture.Prepare(projection:projection).Evaluate("known_future_paid_principal_needs_no_curve",[]);
        Equal(result.Values.Single(v=>v.Owner==cash.Owner).OpeningValue,"0");
        Equal(result.Values.Single(v=>v.Owner==cash.Owner).ResidualValue,"0");
        // Curve-carried opening128 becomes delivery82.5 + stock50, with
        // actual nominal payment-30: 102.5-128=-25.5. Nominal carrying stays80.
        Equal(result.Values.Aggregate(ExactMoney.Parse("0",native.Terms.Currency),(a,b)=>a.Add(b.EconomicPnl)),
            native.Terms.Carrying==GasHoldingCarrying.CurrencyCurve?"-25.5":"0");
        var unpaid=new GasPhysicalLifecycleProjection(fixture.Component,native,Quantities(native),null);
        _=unpaid.EventsFor(cash.EffectiveAt.AddMinutes(-1));
        Reject("gas_physical_projection",()=>unpaid.EventsFor(cash.EffectiveAt));
        // Even with a known payment later, the selected H first exposes this
        // unpaid, not-yet-due claim and therefore requires its saved carrying.
        Reject("gas_physical_projection",()=>projection.EventsFor(paidAt.AddMinutes(-1)));
        var partial=paidNative with{Cash=[paid with{State=new(cash.Owner,cash.Instrument,cash.Quantity,
            [paid.State.Allocations.Single() with{Amount=ExactMoney.Parse("-15",native.Terms.Currency)}],RecordedCashValuation.Policy,
            native.SourceSnapshot.Knowledge.ContextId,native.SourceSnapshot.Knowledge.KnownAt)}]};
        var partialProjection=new GasPhysicalLifecycleProjection(fixture.Component,partial,Quantities(partial),null);
        Reject("gas_physical_projection",()=>partialProjection.EventsFor(fixture.Horizon.Closes[0].Instant));
        Console.WriteLine("PASS physical principal demand: exact unpaid opening/H balances require carrying; paid and not-yet-effective native claims do not");
    }
    private static void ProjectedOperatorReceipt(PhysicalFixture fixture)
    {
        var native=Title(fixture.NativeAttachment!);var source=fixture.DeliveryMarket?.Source??fixture.Source;
        var quantities=TitleQuantities(native,fixture.NativeFact!,fixture.DeliveryMarket?.Source,projectedOperator:true);
        var owner=quantities.EconomicOwners.Single(p=>p!=native.DeliveryOwner&&p!=native.Stocks.Single().Owner);
        var projection=new GasPhysicalLifecycleProjection(fixture.Component,native,quantities,
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
        var prepared=fixture.Prepare(projection:projection);var result=prepared.Evaluate("physical_with_projected_operator_receipt",[]);
        Equal(result.Values.Single(v=>v.Owner==owner).OpeningValue,"20");
        Equal(result.Values.Single(v=>v.Owner==owner).ResidualValue,"20");
        Equal(result.Values.Aggregate(ExactMoney.Parse("0",native.Terms.Currency),(a,b)=>a.Add(b.EconomicPnl)),"0");
        var inventory=prepared.Factors.Single(f=>f.Curves.Contains(native.Terms.Mark.Curve));
        var shifted=prepared.Evaluate("physical_and_operator_inventory_shift",[new(inventory.Input,inventory.Quote,inventory.Unit,ExactDecimal.Parse("1"),QuoteShift.Convention)]);
        Equal(shifted.Values.Aggregate(ExactMoney.Parse("0",native.Terms.Currency),(a,b)=>a.Add(b.EconomicPnl)),"44");
        var stock=result.States.Single().Members[owner].Residuals.Single();
        var position=new AccountingPositionKey(owner.Owner,stock.Positions.Single().BindingId,owner.BookId);
        if(position==owner||!projection.ValuationSource(position,stock.Instruments.Single()).HasSameInputs(source))
            throw new Exception("Projected operator title must retain its original economic owner and exact declared gas source under the new stock binding.");
        Reject("gas_physical_projection",()=>projection.ValuationSource(position with{BindingId=Guid.NewGuid()},stock.Instruments.Single()));
        Reject("gas_physical_projection",()=>projection.ValuationSource(position with{BookId=Guid.NewGuid()},stock.Instruments.Single()));
    }
    private static GasPhysicalKnownAttachment Title(GasPhysicalKnownAttachment native,double hours=1)
    {
        var at=native.EffectiveAt.AddHours(hours);var receipt=new GasPhysicalKnownReceipt(3,native.SourceSnapshot.Knowledge!.KnownAt);
        var positive=native.OriginalQuantity.Amount.CompareTo(default)>0;
        var quantity=new Quantity(ExactDecimal.Parse("10"),native.Terms.Quantity);var performance=Guid.NewGuid();var stock=Guid.NewGuid();
        var owner=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,stock),stock,native.DeliveryOwner.BookId);
        var evidence=Guid.NewGuid();var account=Guid.NewGuid();var cashId=Guid.NewGuid();var determination=Guid.NewGuid();
        var cashOwner=new AccountingPositionKey(native.DeliveryOwner.Owner,cashId,native.DeliveryOwner.BookId);
        var instrument=new TimedCashPayment(native.Terms.Currency,native.Terms.CashDueAt);var amount=new Quantity(ExactDecimal.Parse(positive?"-30":"30"),native.Terms.Currency);
        var state=new RecordedCashValuation(cashOwner,instrument,amount,[],RecordedCashValuation.Policy,native.SourceSnapshot.Knowledge.ContextId,native.SourceSnapshot.Knowledge.KnownAt);
        var consumption=new GasStockConsumption(Guid.NewGuid(),stock,quantity,GasStockUse.Delivery,at,receipt.KnownAt,evidence,null);
        var stockAt=positive?at:native.SourceSnapshot.ValuationAt.AddDays(-1);
        return native with{
            Performances=[new(performance,"synthetic title",positive?quantity:new(-quantity.Amount,quantity.Specification),at,positive?evidence:null,positive?owner:null,
                positive?[]:[new(1,consumption.Id,owner,evidence,null,quantity,1,false,receipt)],1,false,GasSourceAllocation.Initial,receipt)],
            Stocks=[new(owner,account,new(Guid.NewGuid(),Guid.NewGuid(),1),new(Guid.NewGuid(),native.Terms.Mark,native.Terms.Quantity,native.Terms.Currency,null,GasHoldingCarrying.Nominal),
                new(stock,account,quantity,stockAt,stockAt,receipt.KnownAt,evidence),[],[],[],positive?[]:[consumption],1,false,receipt)],
            PrincipalRuns=[new(1,native.CurrentFinal.DeterminationId,native.SourceSnapshot.Knowledge.ContextId,at,
                [new(determination,performance,null,null,positive?quantity:new(-quantity.Amount,quantity.Specification),ExactMoney.Parse(amount.Amount.ToString(),native.Terms.Currency),new(amount.Amount,native.Terms.Currency),
                    new(amount.Amount,native.Terms.Currency),new(amount.Amount,native.Terms.Currency),1)],receipt)],
            Cash=[new(cashId,determination,cashOwner,instrument,amount,state,at,receipt)]};
    }
    private static GasQuantityLifecycleProjection TitleQuantities(GasPhysicalKnownAttachment native,KnownFuturesMargin final,MarketSnapshot? markSource=null,bool receiveThenReturn=false,ISideDiscounting? physicalCarrying=null,
        Func<GasKnownQuantityTransition,GasKnownQuantityTransition>? transitionChange=null,bool anotherPhysicalParent=false,bool projectedOperator=false)
    {
        var stock=native.Stocks.Single();var performance=native.Performances.Single();
        var account=new GasProjectionAccount(stock.AccountId,native.FutureOwner.BookId,stock.LegalOwner,native.Terms.Location);
        var obligation=new GasProjectionHolding(native.DeliveryOwner,account,GasProjectionHoldingKind.PhysicalDelivery,
            (GasPhysicalDeliveryObligationInstrument)native.Terms.CreateObligation(native.CurrentFinal.Price),native.OriginalQuantity,null,
            ImmutableDictionary<Guid,ExactDecimal>.Empty,physicalCarrying){Source=markSource};
        var incoming=new GasProjectionHolding(stock.Owner,account,GasProjectionHoldingKind.OwnedStock,stock.OriginalInstrument,stock.Lot.Quantity,
            stock.Lot.AvailableAt,ImmutableDictionary<Guid,ExactDecimal>.Empty,null){Source=markSource};
        var originId=LifecycleProjectionEvent.ScopedId(native.FutureOwner,native.FinalOccurrence.Id);
        var origin=new GasKnownQuantityTransition(originId,native.EffectiveAt,native.CurrentFinal.Receipt.KnownAt,[],[],[],[]){
            PhysicalOrigins=[new(KnownFuturesPhysicalFinal.Create(native,final),obligation)]};
        var title=performance.Retracted?null:new GasKnownQuantityTransition(LifecycleProjectionEvent.ScopedId(native.DeliveryOwner,performance.Id),performance.PerformedAt,performance.Receipt.KnownAt,[originId],
            performance.SignedQuantity.Amount.CompareTo(default)>0?[]:[new(stock.Owner,new(stock.Consumptions.Single().Id,performance.PerformedAt,
                GasKnownStockUse.Consume,null,stock.Lot.Quantity),native.DeliveryOwner)],
            [new(native.DeliveryOwner,new(ExactDecimal.Parse("10"),native.Terms.Quantity))],performance.SignedQuantity.Amount.CompareTo(default)>0?[new(native.DeliveryOwner,incoming)]:[]);
        if(transitionChange is not null&&title is not null)title=transitionChange(title);
        ImmutableArray<GasProjectionHolding> opening=native.OriginalQuantity.Amount.CompareTo(default)>0?[]:[incoming];
        ImmutableArray<GasKnownQuantityTransition> known=title is null?[origin]:[origin,title];
        if(anotherPhysicalParent)
        {
            var parent=new AccountingPositionKey(Guid.NewGuid(),native.FutureOwner.BindingId,native.FutureOwner.BookId);
            var owner=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),native.DeliveryOwner.BookId);
            var id=Guid.NewGuid();var current=native.CurrentFinal with{DeterminationId=id};
            var extra=native with{FutureOwner=parent,DeliveryOwner=owner,InitialDeterminationId=id,CurrentFinal=current,Finals=[current]};
            known=known.Insert(1,new(LifecycleProjectionEvent.ScopedId(parent,native.FinalOccurrence.Id),native.EffectiveAt,native.CurrentFinal.Receipt.KnownAt,[],[],[],[]){
                PhysicalOrigins=[new(KnownFuturesPhysicalFinal.Create(extra,final with{DeterminationId=id}),obligation with{Position=owner})]});
        }
        if(receiveThenReturn)
        {
            var at=performance.PerformedAt.AddHours(1);var id=Guid.NewGuid();var quantity=new Quantity(ExactDecimal.Parse("4"),native.Terms.Quantity);
            var returnOwner=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,id),id,native.DeliveryOwner.BookId);
            var liability=new GasProjectionHolding(returnOwner,account,GasProjectionHoldingKind.LoanReturn,
                new GasCommodityHoldingInstrument(Guid.NewGuid(),native.Terms.Mark,native.Terms.Quantity,native.Terms.Currency,at,GasHoldingCarrying.Nominal),
                new(-quantity.Amount,quantity.Specification),null,ImmutableDictionary<Guid,ExactDecimal>.Empty,null){Source=markSource};
            opening=opening.Add(liability);
            known=known.Add(new(GasQuantityLifecycleProjection.AcceptedEventIdentity(native.SourceSnapshot.Definitions.CatalogId,4,at),at,
                performance.Receipt.KnownAt,[title!.Id],[new(stock.Owner,new(Guid.NewGuid(),at,GasKnownStockUse.Consume,null,quantity),returnOwner)],
                [new(returnOwner,quantity)],[]));
        }
        var policies=ImmutableDictionary<Guid,GasQuantityProjectionPolicy>.Empty.Add(account.Id,new(GasQuantityProjectionPolicy.KnownVersion,GasQuantityProjectionPolicy.Retain));
        ImmutableArray<GasQuantityPerformance> projected=[];
        if(projectedOperator)
        {
            var operatorAccount=account with{Id=Guid.NewGuid()};var at=performance.PerformedAt.AddHours(1);var id=Guid.NewGuid();
            var owner=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,id),id,account.BookId);
            var quantity=new Quantity(ExactDecimal.Parse("4"),native.Terms.Quantity);
            opening=opening.Add(new(owner,operatorAccount,GasProjectionHoldingKind.OperatorClaim,
                new GasCommodityHoldingInstrument(Guid.NewGuid(),native.Terms.Mark,native.Terms.Quantity,native.Terms.Currency,at,GasHoldingCarrying.Nominal),
                quantity,null,ImmutableDictionary<Guid,ExactDecimal>.Empty,null){Source=markSource});
            projected=[new(Guid.NewGuid(),owner,at,quantity,[],new(account,stock.OriginalInstrument,at){Source=markSource},[])];
            policies=policies.Add(operatorAccount.Id,new(GasQuantityProjectionPolicy.KnownVersion,GasQuantityProjectionPolicy.AtDue));
        }
        return new(policies,native.SourceSnapshot,opening,projected,known);
    }
    private static GasPhysicalKnownAttachment RetireTitle(GasPhysicalKnownAttachment native)
    {
        var performance=native.Performances.Single();var stock=native.Stocks.Single();var original=native.Cash.Single();var first=native.PrincipalRuns.Single();
        var zero=new Quantity(default,native.Terms.Quantity);var receipt=first.Receipt with{PublicationOrder=4};
        var id=Guid.NewGuid();var cashId=Guid.NewGuid();var refundOwner=new AccountingPositionKey(native.DeliveryOwner.Owner,cashId,native.DeliveryOwner.BookId);
        var refundQuantity=new Quantity(ExactDecimal.Parse("30"),native.Terms.Currency);
        var paid=new AccountingFlow(Guid.NewGuid(),1,original.Owner,performance.PerformedAt.AddHours(1),receipt.KnownAt,
            ExactMoney.Parse("-15",native.Terms.Currency),AccountingFlowKind.EconomicCash);
        var oldState=new RecordedCashValuation(original.Owner,original.Instrument,original.Quantity,[paid],RecordedCashValuation.Policy,
            native.SourceSnapshot.Knowledge!.ContextId,native.SourceSnapshot.Knowledge.KnownAt);
        return native with{Performances=[performance with{SignedQuantity=zero,FinancialRevision=2,Retracted=true,SourceAllocation=GasSourceAllocation.Withdraw,Receipt=receipt}],
            Stocks=[stock with{Lot=stock.Lot with{Quantity=zero},FinancialRevision=2,Retracted=true,Receipt=receipt}],
            PrincipalRuns=[first,new(2,native.CurrentFinal.DeterminationId,first.SourceContextId,first.AsOf,
                [new(id,performance.Id,first.Steps.Single().Id,null,zero,ExactMoney.Parse("0",native.Terms.Currency),new(default,native.Terms.Currency),
                    new(default,native.Terms.Currency),new(refundQuantity.Amount,native.Terms.Currency),2)],receipt)],
            Cash=[original with{State=oldState},new(cashId,id,refundOwner,original.Instrument,refundQuantity,
                new(refundOwner,original.Instrument,refundQuantity,[],RecordedCashValuation.Policy,native.SourceSnapshot.Knowledge.ContextId,native.SourceSnapshot.Knowledge.KnownAt),
                original.EffectiveAt,receipt)]};
    }
    private static GasQuantityLifecycleProjection ActivePhysicalQuantity(GasPhysicalKnownAttachment native)
    {
        var stock=native.Stocks.Single();var performance=native.Performances.Single();var at=native.EffectiveAt.AddMinutes(30);
        var source=(native.SourceSnapshot with{Id=Guid.NewGuid(),ValuationAt=at,MarketCutoff=at}).WithKnowledge(Guid.NewGuid(),native.SourceSnapshot.Knowledge!.KnownAt);
        var account=new GasProjectionAccount(stock.AccountId,native.DeliveryOwner.BookId,stock.LegalOwner,native.Terms.Location);
        var physical=new GasProjectionHolding(native.DeliveryOwner,account,GasProjectionHoldingKind.PhysicalDelivery,
            (GasPhysicalDeliveryObligationInstrument)native.Terms.CreateObligation(native.CurrentFinal.Price),native.OriginalQuantity,null,
            ImmutableDictionary<Guid,ExactDecimal>.Empty,null);
        var received=new GasProjectionHolding(stock.Owner,account,GasProjectionHoldingKind.OwnedStock,stock.OriginalInstrument,stock.Lot.Quantity,
            stock.Lot.AvailableAt,ImmutableDictionary<Guid,ExactDecimal>.Empty,null);
        var title=new GasKnownQuantityTransition(LifecycleProjectionEvent.ScopedId(native.DeliveryOwner,performance.Id),performance.PerformedAt,performance.Receipt.KnownAt,[],[],
            [new(native.DeliveryOwner,performance.SignedQuantity)],[new(native.DeliveryOwner,received)]);
        return new(ImmutableDictionary<Guid,GasQuantityProjectionPolicy>.Empty.Add(account.Id,new(GasQuantityProjectionPolicy.KnownVersion,GasQuantityProjectionPolicy.Retain)),
            source,[physical],[],[title]);
    }
    private sealed class PhysicalFixture
    {
        private readonly Fixture definitions;
        public CurveModel Model{get;}
        public MarketSnapshot Source{get;}
        public LifecycleProjectionHorizon Horizon{get;}
        public LifecycleProjectionPolicy Policy{get;}
        public FuturesLifecycleProjection Component{get;}
        public FuturesDeliveryProjectionMarket? DeliveryMarket{get;}
        public AccountingPositionKey? NativeOwner{get;}
        public GasPhysicalKnownAttachment? NativeAttachment{get;}
        public KnownFuturesMargin? NativeFact{get;}
        private IProjectionCurrencyCurve CashCurve{get;}
        public ISideDiscounting PrincipalCurve=>DeliveryMarket?.Discounting??CashCurve;
        public PreparedLifecycleProjection Prepare(LifecycleProjectionPolicy? policy=null,LifecycleProjectionHorizon? horizon=null,ImmutableArray<ILifecycleProjection> extra=default,ILifecycleProjection? projection=null)
        {
            var components=ImmutableArray.Create<ILifecycleProjection>(projection??Component).AddRange(extra.IsDefault?[]:extra);
            if(DeliveryMarket is not {} selected)return LifecycleProjection.Prepare(Model,Source,horizon??Horizon,policy??Policy,components,[],cashCurves:ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(CashCurve.Currency,CashCurve));
            var a=Model.Build(Source,[..Component.Dependencies.Where(d=>d.Curve.Revision==Source.Definitions)]);
            var b=selected.Model.Build(selected.Source,[selected.Terms.Mark]);var context=a.Graph.Plan.Context;
            var exportA=Model.Compile(Source,[..a.Values.Select(v=>v.Request)],false,context);
            var exportB=selected.Model.Compile(selected.Source,[selected.Terms.Mark],false,context);
            var bindings=exportA.Points.Concat(exportB.Points).ToImmutableDictionary(p=>p.Key.Request,p=>p.Value);
            var plan=ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(exportA.Definitions.Concat(exportB.Definitions)),[..bindings.Values],context,new Visible());
            var valuation=ConnectedValuation.Prepare(ConnectedScenario.Base(plan),Source,[selected.Source]);
            return LifecycleProjection.Prepare(valuation,bindings,a,horizon??Horizon,policy??Policy,components,[],cashCurves:ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(CashCurve.Currency,CashCurve),additionalMarkets:[new(b,
                selected.Discounting is null?ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty:ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(selected.Terms.Currency,selected.Discounting),[])]);
        }
        public PhysicalFixture(string signed,GasHoldingCarrying carrying=GasHoldingCarrying.Nominal,bool crossDefinition=false,bool provideCurve=true,bool nativeFinal=false,bool prepareForeign=false,string execution="3",bool delayedVm=false,bool nativeLink=false,double? cashDueHours=null,
            Func<GasPhysicalKnownAttachment,GasPhysicalKnownAttachment>? nativeTransform=null,PhysicalFixture? shared=null)
        {
            var f=definitions=shared?.definitions??new Fixture();var quote=f.Quotes[0];var contract=f.Contracts[0];var at=contract.Expiry;
            var series=new FuturesSeriesSpecification(f.R("future_jan"),quote.Quantity,quote.Currency,quote.Multiplier,quote.TradingTick,
                f.R("location"),f.R("uniform"),contract.DeliveryFrom,contract.DeliveryTo,at,quote.Quantity,ExactDecimal.Parse("40"),
                LotBasis.FixedTotal,Fixture.Utc("2040-01-01T00:00:00Z"),"physical");
            var inventory=f.Direct("inventory",f.K(2),quote.Key,SettlementKind.Index);
            Model=new(f.Revision,f.Reference,f.Quantities,[quote],[contract,f.Contracts[1]],[f.Curves[0],inventory],[series],[],
                [new(f.S(1),contract.Key,quote.Key,null),new(f.S(8),f.K(2),quote.Key,null)]);
            var sourceAt=at.AddHours(-12);var selected=f.Select(1,QuoteKind.Outright,"gas_out","3");
            var mark=new SelectedObservation(new(f.S(8),InputUse.Variable),new(new(f.Revision.CatalogId,Guid.NewGuid()),f.S(8),f.R("source"),
                ObservationRole.TraderMark,PublicationState.Final,sourceAt,ExactDecimal.Parse("5")));
            Source=(f.Snapshot() with{ValuationAt=sourceAt,MarketCutoff=sourceAt,Selections=[selected,mark]}).WithKnowledge(Guid.NewGuid(),nativeFinal?at.AddHours(36):sourceAt);
            if(shared is not null)Source=shared.Source;
            var market=new FuturesMarketBinding(Guid.NewGuid(),new(f.C("future"),contract.Key),contract,series,quote,"physical",FuturesMarketBinding.Implementation);
            var binding=Guid.NewGuid();var trade=new Trade(Guid.NewGuid(),shared?.Component.Owner.BookId??Guid.NewGuid(),[FuturesExecution.Bind(binding,market,ExactDecimal.Parse(signed),
                new(ExactDecimal.Parse(execution),quote),sourceAt.AddDays(-1),FuturesSettlementState.Unprocessed)]);
            var holding=trade.Instruments.Single();
            var deliveryRevision=crossDefinition?new DefinitionRevision(f.Revision.CatalogId,Guid.NewGuid()):f.Revision;
            var terms=new GasPhysicalDeliveryTerms(Guid.NewGuid(),series.Key,quote.Quantity,quote.Currency,series.Location,at,at.AddDays(30),
                cashDueHours is {} hours?at.AddHours(hours):at.AddDays(35),new(new(deliveryRevision,inventory.Key.Code),new(deliveryRevision,f.K(2).Id)),carrying,GasPhysicalDeliveryTerms.Version);
            var margin=new FuturesMarginPolicy(Guid.NewGuid(),1,contract.Key,f.C("future"),at.AddDays(-3),at.AddDays(2),
                [new(Guid.NewGuid(),at,delayedVm?at.AddDays(1):at,FuturesMarginPhase.Final)],new(quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal),
                FuturesMargin.HeldBindingRounding,FuturesMarginOpening.AfterOpeningInstant,FuturesMarginPolicy.DeliveryVersion){Delivery=new(Guid.NewGuid(),1,terms)};
            var state=new FuturesMarginState(trade.Id,binding,trade.BookId,holding.Quantity,market.Series.Key,ExactUnitPrice.FromPrice(new(ExactDecimal.Parse(execution),quote)),
                sourceAt.AddDays(-1),sourceAt,FuturesMarginState.Version,FuturesMarginStatus.Open,null,Source.Knowledge!.ContextId,Source.Knowledge.KnownAt,1,1);
            CashCurve=new HeldCurve(Source,quote.Currency,at.AddHours(36),terms.CashDueAt,delayedVm?at.AddDays(1):null);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(market.Instrument.Id,market),
                CashCurve){
                BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),state)};
            var history=new FuturesMarginHistory(market.Instrument.ProposeMarginTerms(trade,binding,margin,Model,Source),Source.Knowledge.ContextId,Source.Knowledge.KnownAt,[]);
            var accepted=nativeFinal?new FuturesKnownHistory(new(trade.Id,binding,trade.BookId),history.Terms,Source.Knowledge.ContextId,Source.Knowledge.KnownAt,
                [new(margin.Occurrences.Single(),Guid.NewGuid(),null,null,2,2,at.AddHours(1),state.SettledBasis,
                    shared?.NativeFact?.Observation??new(new(f.Revision.CatalogId,Guid.NewGuid()),f.S(1),f.R("source"),ObservationRole.FinalContractSettlement,PublicationState.Final,at,ExactDecimal.Parse("3")),
                    at.AddHours(1),ExactMoney.Parse("0",quote.Currency),new(default,quote.Currency))],[]):null;
            if(prepareForeign)
            {
                var selectedContract=f.Contracts[1] with{Key=terms.Mark.Contract};var selectedSeries=new SeriesKey(deliveryRevision,Guid.NewGuid());
                var selectedCurve=inventory with{Key=terms.Mark.Curve,Constituents=[selectedContract.Key]};
                var selectedModel=new CurveModel(deliveryRevision,f.Reference,f.Quantities,[quote],[selectedContract],[selectedCurve],[],[],[new(selectedSeries,selectedContract.Key,quote.Key,null)]);
                var selectedMark=mark with{Input=new(selectedSeries,InputUse.Variable),Observation=mark.Observation with{Series=selectedSeries,Key=new(deliveryRevision.CatalogId,Guid.NewGuid())}};
                var selectedSource=(Source with{Id=Guid.NewGuid(),Definitions=deliveryRevision,Selections=[selectedMark]}).WithKnowledge(Source.Knowledge!.ContextId,Source.Knowledge.KnownAt);
                DeliveryMarket=new(terms,selectedModel,selectedSource,carrying==GasHoldingCarrying.CurrencyCurve?new HeldCurve(selectedSource,quote.Currency,at.AddHours(36),terms.CashDueAt):null);
            }
            if(!crossDefinition||DeliveryMarket is not null)
            {
                var selectedModel=DeliveryMarket?.Model??Model;var selectedSource=DeliveryMarket?.Source??Source;
                var captured=selectedModel.Build(selectedSource,[terms.Mark]);
                setup=setup with{FuturesValueBindings=setup.FuturesValueBindings.Add(new(trade.Id,binding),new(history.Terms,[],FuturesMarginValueBinding.FlatReference){
                    Physical=new(FuturesPhysicalBasisValueBinding.Version,FuturesPhysicalBasisValueBinding.ValuationProxy,margin.Delivery.ProfileId,margin.Delivery.ProfileRevision,
                        new(terms.Mark,captured.Connected,captured.PricingBindings[terms.Mark]),carrying==GasHoldingCarrying.CurrencyCurve&&provideCurve?
                            DeliveryMarket?.Discounting??setup.Discounting:null)})};
            }
            KnownFuturesPhysicalFinal? native=null;
            if(nativeLink)
            {
                var fact=accepted!.Determinations.Single();var receipt=new GasPhysicalKnownReceipt(fact.PublicationOrder,fact.KnownAt);
                var final=new GasPhysicalKnownFinal(fact.DeterminationId,fact.RunRevision,fact.PreviousDeterminationId,fact.Observation.Key.Id,
                    ExactUnitPrice.FromPrice(new(fact.Observation.Value,quote)),receipt);
                NativeOwner=new(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),trade.BookId);
                var attachment=new GasPhysicalKnownAttachment(Source,new(trade.Id,binding,trade.BookId),NativeOwner.Value,1,margin.Id,margin.Revision,
                    margin.Delivery!.ProfileId,margin.Delivery.ProfileRevision,terms,margin.Rounding,GasDeliveryPrincipal.Scope,"contractual_principal_due_v1",
                    margin.Occurrences.Single(),holding.Quantity,fact.DeterminationId,final,[final],at,receipt,[],[],[],[]);
                NativeAttachment=attachment;NativeFact=fact;
                native=KnownFuturesPhysicalFinal.Create(nativeTransform?.Invoke(attachment)??attachment,fact);
            }
            Component=new(trade,binding,state,history,setup,market.Market,Model,Source,accepted,DeliveryMarket,native);
            Policy=new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,LifecycleProjectionPolicy.Nominal,
                LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
            Horizon=new(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(new(2040,12,29),at.AddHours(36),0)]);
        }
    }
    private sealed class Visible:ICurveBindingResolver
    {public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext context)=>throw new NotSupportedException();public void RequireVisible(CurveKey key,ResolvedCalculationContext context){}}
    private sealed record FinalProbe(AccountingPositionKey Owner,MarketSnapshot SourceSnapshot,ReferenceKey Currency,CurveRequest Request,DateTimeOffset At):ILifecycleProjection
    {
        public string Implementation=>"synthetic_known_final_probe_v1";
        public ImmutableArray<CurveRequest> Dependencies=>[Request];
        public LifecycleProjectionState Opening=>new([],[]);
        public ImmutableArray<LifecycleProjectionEvent> Events=>[new(LifecycleProjectionEvent.ScopedId(Owner,Guid.Parse("f0000000-0000-0000-0000-000000000001")),At,[],"synthetic_final_read")];
        public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)=>[..Events.Where(e=>e.At<=horizon)];
        public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
        {
            var price=context.Fix(new(Request,ObservationRole.IndexFixing,At)).Price;
            return state with{Cash=state.Cash.Add(new(occurrence.Id,At,At,price.ExactTimes(new(ExactDecimal.Parse("1"),price.Unit.Quantity))))};
        }
    }
    private sealed class HeldCurve(MarketSnapshot source,ReferenceKey currency,DateTimeOffset horizon,DateTimeOffset due,DateTimeOffset? vmDue=null):IProjectionCurrencyCurve
    {
        public ReferenceKey Currency=>currency;
        public MarketSnapshot SourceSnapshot=>source;
        public ProjectionCurrencyCurveKey CurveIdentity{get;}=new(new(source.Definitions,"synthetic_rates"),Guid.NewGuid(),Guid.NewGuid(),1);
        public string TimingPolicy=>RateDiscountTiming.Version;
        private ExactDecimal Factor(DateTimeOffset at)=>ExactDecimal.Parse(at==due?"0.6":at==vmDue?"0.95":at==horizon?"0.8":"1");
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot selected)=>
            [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt),selected,DiscountFactorSource.Explicit))];
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> payments,MarketSnapshot selected,DateTimeOffset at,string? clockPolicy)=>
            [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt).DivideExact(Factor(at)),selected,DiscountFactorSource.Explicit))];
    }
}
