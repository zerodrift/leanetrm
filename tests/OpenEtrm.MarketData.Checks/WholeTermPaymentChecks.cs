using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class WholeTermPaymentChecks
{
    internal static int Run()
    {
        static ExactDecimal D(string value)=>ExactDecimal.Parse(value);
        static void Reject(string code,Action action)
        {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
        var f=new CommodityLookupFixture(2,monthly:true);var unit=new Quantity(D("1"),f.Quote.Quantity);
        var originals=Enumerable.Range(0,2).Select(n=>f.Swap(monthly:true,monthOrdinal:n)).ToArray();
        var jan=originals[0].Fixed.DeliveryFrom;var feb=jan.AddMonths(1);var mar=feb.AddMonths(1);
        var payment=originals[1].Fixed.PaymentAt;
        UnitPrice Price(string value)=>new(D(value),f.Quote);
        FixedSide Fixed(FixedPriceTerm price)=>new(Guid.NewGuid(),price,f.Quote,unit,
            [..originals.Select(p=>p.Fixed.Periods[0] with{PaymentAt=payment,PaymentSchedule=null})]);
        var floating=new FloatingSide(Guid.NewGuid(),originals[0].Floating.Settlement,f.Quote,unit,
            [..originals.Select(p=>p.Floating.Periods[0] with{PaymentAt=payment,PaymentSchedule=null})]);
        FinancialSwapInstrument Swap(FixedSide side,FloatingSide? second=null)=>new(Guid.NewGuid(),
            [new(side,D("-1")),new(second??floating,D("1"))],FinancialSwapInstrument.Implementation,true);
        Reject("side_price_cadence",()=>Fixed(new SteppedPrice([new(jan,Price("4")),new(jan.AddDays(8),Price("5")),new(jan.AddDays(15),Price("4"))])));
        Reject("side_price_cadence",()=>Fixed(new SteppedPrice([new(jan,Price("4")),new(jan.AddDays(8),Price("3")),new(jan.AddDays(15),Price("4"))])));
        Reject("side_price_cadence",()=>Fixed(new EscalatedPrice(Price("4"),D("0.1"),jan.AddYears(-1).AddDays(14),
            OpenEtrm.Shared.Temporal.ContractYearBasis.AnniversaryStep)));
        var before=Fixed(new SteppedPrice([new(jan,Price("4")),new(feb,Price("5"))]));
        _=Fixed(new SteppedPrice([new(jan,Price("4")),new(jan.AddDays(8),Price("4"))]));
        _=Fixed(new EscalatedPrice(Price("4"),D("0"),jan.AddYears(-1).AddDays(14),
            OpenEtrm.Shared.Temporal.ContractYearBasis.AnniversaryStep));
        var after=Fixed(new SteppedPrice([new(jan,Price("4")),new(feb,Price("6"))]));
        string Hash(FixedSide side,int n)
        {var instrument=Swap(side);return SwapPaymentCalculation.ContractIdentity(instrument,instrument.Sides[0],side.GenerateDatedSchedule().Payments[n],unit);}
        if(Hash(before,0)!=Hash(after,0)||Hash(before,1)==Hash(after,1))
            throw new Exception("A future strike amendment must preserve the earlier occurrence and change only its own occurrence.");
        var plain=Fixed(new FlatPrice(Price("4")));
        if(Hash(plain,0)!=Hash(before,0))throw new Exception("An unchanged contractual row rate must preserve its occurrence when a flat term becomes a future stepped term.");
        var swap=Swap(plain);var market=f.Build(floating.Dependencies);
        if(swap.Price(market,new(D("10"),unit.Specification),D("0.5"),FinancialSwapInstrument.Discounted).Amount.ToString()!="-7.495")
            throw new Exception("Two independent monthly differences must each receive the one explicit common discount factor and quantity.");
        var otherDate=new FloatingSide(Guid.NewGuid(),floating.Settlement,f.Quote,unit,
            floating.Periods.SetItem(1,floating.Periods[1] with{PaymentAt=payment.AddDays(1)}));
        Reject("swap_valuation",()=>Swap(plain,otherDate).Price(f.Build(otherDate.Dependencies),new(D("10"),unit.Specification),D("0.5"),FinancialSwapInstrument.Discounted));
        var retainedDefinitions=swap.Sides.ToDictionary(m=>m.Side.Id,m=>ScheduleDefinitionCompiler.Generate(m.Side));
        var fixedDefinition=retainedDefinitions[plain.Id];var storedRows=fixedDefinition.Rows.ToArray();
        var storedQuantity=Array.FindIndex(storedRows,row=>row.Operation=="quantity_literal");
        storedRows[storedQuantity]=storedRows[storedQuantity] with{Parameters=storedRows[storedQuantity].Parameters.SetItem("numerator","2")};
        retainedDefinitions[plain.Id]=new(fixedDefinition.SideId,fixedDefinition.Family,fixedDefinition.EconomicIdentity,fixedDefinition.PricingBindingIdentity,
            fixedDefinition.SchemaVersion,fixedDefinition.GeneratorVersion,storedRows,fixedDefinition.Edges,fixedDefinition.Bindings,fixedDefinition.Roots,fixedDefinition.Ports,fixedDefinition.Grids);
        var booked=RetainedSideSchedules.Attach(swap,retainedDefinitions);
        if(booked.Price(market,new(D("10"),unit.Specification),D("0.5"),FinancialSwapInstrument.Discounted).Amount.ToString()!="-27.495")
            throw new Exception("A booked side must value the retained quantity expression rather than regenerating its original period parameters.");
        var dailyFixture=new CommodityLookupFixture(31);var daily=dailyFixture.Swap();var dailyMarket=dailyFixture.Build(daily.Dependencies);
        var dailyDefinitions=daily.Sides.ToDictionary(m=>m.Side.Id,m=>ScheduleDefinitionCompiler.Generate(m.Side));
        var dailyCopy=RetainedSideSchedules.Attach(daily,dailyDefinitions);
        foreach(var membership in daily.Sides)
        {
            var copied=dailyCopy.Sides.Single(m=>m.Side.Id==membership.Side.Id);
            var originalRow=((SidePaymentSchedule)membership.Side.GenerateSchedule()).Payments[0];var copiedRow=((SidePaymentSchedule)copied.Side.GenerateSchedule()).Payments[0];
            if(SwapPaymentCalculation.ContractIdentity(daily,membership,originalRow,daily.UnitQuantity)!=SwapPaymentCalculation.ContractIdentity(dailyCopy,copied,copiedRow,daily.UnitQuantity))
                throw new Exception("Retaining an equivalent rational observation average must preserve its occurrence identity.");
        }
        var observationDefinition=dailyDefinitions[daily.Floating.Id];var observations=observationDefinition.Rows.ToArray();var application=0;
        for(var n=0;n<observations.Length;n++)if(observations[n].Operation=="price_application")
            observations[n]=observations[n] with{Parameters=observations[n].Parameters.SetItem("numerator","1").SetItem("denominator",application++==0?"16":"32")};
        dailyDefinitions[daily.Floating.Id]=new(observationDefinition.SideId,observationDefinition.Family,observationDefinition.EconomicIdentity,observationDefinition.PricingBindingIdentity,
            observationDefinition.SchemaVersion,observationDefinition.GeneratorVersion,observations,observationDefinition.Edges,observationDefinition.Bindings,observationDefinition.Roots,observationDefinition.Ports,observationDefinition.Grids);
        var changedAverage=RetainedSideSchedules.Attach(daily,dailyDefinitions);
        if(changedAverage.Price(dailyMarket,new(D("31"),daily.UnitQuantity.Specification),D("1"),FinancialSwapInstrument.Undiscounted).Amount.ToString()!="-22.79953125")
            throw new Exception("Stored rational observation weights must determine the dated average exactly.");
        var ppaFixture=new PpaAcceptanceFixture();var ppa=ppaFixture.Physical();var ppaMarket=ppaFixture.Market(ppaFixture.From.AddDays(-2));
        var ppaSchedules=ppa.Memberships.ToDictionary(m=>m.Side.Id,m=>ScheduleDefinitionCompiler.Generate(m.Side));
        var strike=ppa.Memberships[1].Side;var strikeDefinition=ppaSchedules[strike.Id];var strikeRows=strikeDefinition.Rows.ToArray();
        var fixedPrice=Array.FindIndex(strikeRows,row=>row.Operation=="constant_price");
        strikeRows[fixedPrice]=strikeRows[fixedPrice] with{Parameters=strikeRows[fixedPrice].Parameters.SetItem("numerator","41")};
        ppaSchedules[strike.Id]=new(strikeDefinition.SideId,strikeDefinition.Family,strikeDefinition.EconomicIdentity,strikeDefinition.PricingBindingIdentity,
            strikeDefinition.SchemaVersion,strikeDefinition.GeneratorVersion,strikeRows,strikeDefinition.Edges,strikeDefinition.Bindings,strikeDefinition.Roots,strikeDefinition.Ports,strikeDefinition.Grids);
        var storedPpa=RetainedSideSchedules.Attach(ppa,ppaSchedules);var storedInputs=ppaFixture.Prepare(ppaMarket,[storedPpa]);
        var storedValue=ValueMeasures.From(storedInputs.Session(ppaMarket.Build).Value(storedPpa,new(ppaFixture.From,ppaFixture.To),
            new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted)));
        if(storedValue.ValueExcludingOverdue.ToExactString()!="721")
            throw new Exception("Native payment PV must consume the retained fixed-price expression.");
        var physicalDefinition=ppaSchedules[ppa.Memberships[0].Side.Id];var physicalRows=physicalDefinition.Rows.ToArray();
        var profileLiteral=Array.FindIndex(physicalRows,row=>row.Operation=="profile_quantity");
        physicalRows[profileLiteral]=physicalRows[profileLiteral] with{Operation="quantity_literal",Parameters=physicalRows[profileLiteral].Parameters.Add("numerator","2").Add("denominator","1")};
        ppaSchedules[physicalDefinition.SideId]=new(physicalDefinition.SideId,physicalDefinition.Family,physicalDefinition.EconomicIdentity,physicalDefinition.PricingBindingIdentity,
            physicalDefinition.SchemaVersion,physicalDefinition.GeneratorVersion,physicalRows,physicalDefinition.Edges,physicalDefinition.Bindings,physicalDefinition.Roots,physicalDefinition.Ports,physicalDefinition.Grids);
        var changedProfile=RetainedSideSchedules.Attach(ppa,ppaSchedules);var changedInputs=ppaFixture.Prepare(ppaMarket,[changedProfile]);
        var changedValue=ValueMeasures.From(changedInputs.Session(ppaMarket.Build).Value(changedProfile,new(ppaFixture.From,ppaFixture.To),
            new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted)));
        if(changedValue.ValueExcludingOverdue.ToExactString()!="1442")throw new Exception("A retained2MWh quantity column must feed all721 native cells and its sibling payment through the declared input port.");
        var indexedSide=new FloatingSide(Guid.NewGuid(),ppa.Memberships[1].Side.Terms,new(ppaFixture.C("synthetic_hub"),D("1"),new(default,ppaFixture.EnergyPrice),null));
        var indexed=new PpaInstrument(Guid.NewGuid(),ppaFixture.Definitions,ppaFixture.Reference,
            [ppa.Memberships[0],new(new(PpaRole.IndexedPayment),indexedSide,D("-1"))],false,PpaInstrument.Implementation);
        var indexedDefinitions=indexed.Memberships.ToDictionary(m=>m.Side.Id,m=>ScheduleDefinitionCompiler.Generate(m.Side));var indexedDefinition=indexedDefinitions[indexedSide.Id];
        indexedDefinitions[indexedSide.Id]=new(indexedDefinition.SideId,indexedDefinition.Family,indexedDefinition.EconomicIdentity,indexedDefinition.PricingBindingIdentity,
            indexedDefinition.SchemaVersion,indexedDefinition.GeneratorVersion,indexedDefinition.Rows,indexedDefinition.Edges,
            [..indexedDefinition.Bindings.Select(binding=>binding.Kind=="curve_interval"?binding with{Code="synthetic_energy"}:binding)],indexedDefinition.Roots,indexedDefinition.Ports,indexedDefinition.Grids);
        var storedIndexed=RetainedSideSchedules.Attach(indexed,indexedDefinitions);var indexedInputs=ppaFixture.Prepare(ppaMarket,[storedIndexed]);
        if(ValueMeasures.From(indexedInputs.Session(ppaMarket.Build).Value(storedIndexed,new(ppaFixture.From,ppaFixture.To),
            new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted))).ValueExcludingOverdue.ToExactString()!="0")
            throw new Exception("A retained native curve binding must determine the selected market dependency.");
        foreach(var original in new[]{ppaFixture.Physical(volume:true),ppaFixture.WithCertificate(),ppaFixture.CapacityAgreement("2")})
        {
            var sealedCopy=RetainedSideSchedules.Attach(original,original.Memberships.ToDictionary(m=>m.Side.Id,m=>ScheduleDefinitionCompiler.Generate(m.Side)));
            if(sealedCopy.Sides.Any(m=>m.Side.GenerateSchedule().Rows.Any(row=>row.RetainedReference is null)))
                throw new Exception("Every booked native row must retain its explicit stored logical root.");
            var originalInputs=ppaFixture.Prepare(ppaMarket,[original]);var sealedInputs=ppaFixture.Prepare(ppaMarket,[sealedCopy]);
            foreach(var build in new[]{ppaMarket.Build,ppaMarket.Model.Scenarios(ppaMarket.Build,[ppaMarket.Bumps["synthetic_energy"]]).Single().Up,
                ppaMarket.Model.Scenarios(ppaMarket.Build,[ppaMarket.Bumps["synthetic_energy"]]).Single().Down})
            {
                var discount=new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted);
                var firstValue=ValueMeasures.From(originalInputs.Session(build).Value(original,new(ppaFixture.From,ppaFixture.To),discount));
                var secondValue=ValueMeasures.From(sealedInputs.Session(build).Value(sealedCopy,new(ppaFixture.From,ppaFixture.To),discount));
                if(firstValue.ValueExcludingOverdue!=secondValue.ValueExcludingOverdue||
                    !firstValue.Rows.Select(row=>(row.Row.Ordinal,row.Quantity,row.Amount,row.PresentValue)).SequenceEqual(secondValue.Rows.Select(row=>(row.Row.Ordinal,row.Quantity,row.Amount,row.PresentValue))))
                    throw new Exception("Retained volume, certificate and capacity expressions must preserve exact row values and shared-scenario propagation.");
            }
        }
        var scaledMarket=ppaFixture.Market(ppaFixture.From.AddDays(-2),energy:"0.042",energyQuoteMultiplier:D("1000"));
        var rawUnit=new PriceUnit(ppaFixture.EnergyPrice.Currency,ppaFixture.EnergyPrice.Quantity,D("1000"));
        var scaledAssignments=ppaFixture.Assignments.Select(assignment=>assignment.Attribute==OpenEtrm.MarketData.Power.ValuedAttribute.Energy?
            assignment with{Unit=rawUnit,Adjustment=D("0.002")}:assignment).ToImmutableArray();
        var scaledCopy=RetainedSideSchedules.Attach(ppa,ppa.Memberships.ToDictionary(m=>m.Side.Id,m=>ScheduleDefinitionCompiler.Generate(m.Side)));
        foreach(var selected in new[]{ppa,scaledCopy})
        {
            var prepared=ppaFixture.Prepare(scaledMarket,[selected],assignments:scaledAssignments);
            var amount=ValueMeasures.From(prepared.Session(scaledMarket.Build).Value(selected,new(ppaFixture.From,ppaFixture.To),
                new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted))).ValueExcludingOverdue;
            if(amount.ToExactString()!="2884")throw new Exception("Canonical physical lookup output must preserve exact raw quote-scale and adjustment conversion: (0.042+0.002)*1000 minus40, across721hours.");
        }
        RetainedScheduleAdmissionChecks.Run();
        Console.WriteLine("PASS whole-term payments: price cadence, occurrence continuity, common-date factors and retained dated/native expression authority");
        return 8;
    }
}
