using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;
using OpenEtrm.MarketData.Risk;

internal static class IndexFixingChecks
{
 public static int Run()
 {
  var f=new Fixture();var quote=f.Quotes[0];var unit=new Quantity(ExactDecimal.Parse("1"),quote.Quantity);
  var daily=DailyFixture.Create(f,quote,["90","20","30"]);
  var at=Fixture.Utc("2041-01-02T12:00:00Z");var known=Fixture.Utc("2041-01-05T00:00:00Z");
  var rules=daily.Projections.Select((p,n)=>new IndexFixingRequirement(f.R("index"),quote,f.R("source"),p.Contract.DeliveryFrom,p.Contract.DeliveryTo,
   Fixture.Utc($"2041-01-{n+2:00}T00:00:00Z"),IndexFixingRequirement.LatestEligibleFinalByContext)).ToImmutableArray();
  var actual=new IndexFixingObservation(new(f.Revision.CatalogId,Guid.NewGuid()),rules[0].Index,rules[0].Source,quote,rules[0].From,rules[0].To,
   Fixture.Utc("2041-01-02T10:00:00Z"),Fixture.Utc("2041-01-04T00:00:00Z"),PublicationState.Final,new UnitPrice(ExactDecimal.Parse("10"),quote),1,null);
  var snapshot=daily.Market.Snapshot with {ValuationAt=at,FixingKnowledgeCutoff=known,IndexFixings=[actual]};
  var projected=new FloatingSide(Guid.NewGuid(),f.R("index"),quote,unit,daily.From,daily.To,rules[0].FixingAt,Fixture.Utc("2041-02-18T00:00:00Z"),
   FloatingAveraging.CalendarDayArithmetic,daily.Projections,FloatingSide.Implementation,spread:new UnitPrice(ExactDecimal.Parse("1"),quote));
  var side=new FixingAwareFloatingSide(projected,rules,FixingAwareFloatingSide.Implementation);
  var count=0;
  void Check(string label,Action run){run();count++;Console.WriteLine("PASS index fixings: "+label);}
  void Reject(string code,Action run){try{run();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
  SideValuation Value(MarketSnapshot s)=>side.PV(new SideValuationContext(daily.Model.Build(s,side.DependenciesAt(s)),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
  Check("partially fixed average uses actual 10 and projected 20/30 plus contractual spread, once",()=>{
   var value=Value(snapshot);if(value.PresentValue.Scale(ExactDecimal.Parse("3")).ToMoney().Amount.ToString()!="63")throw new Exception("Expected (10+20+30+3)*3/3 = 63");
   var rows=value.Payments.Single().Resets;
   if(rows[0].Price.Kind!=SchedulePriceKind.Actual||rows[0].Price.Fixing?.Key!=actual.Key||rows.Skip(1).Any(r=>r.Price.Kind!=SchedulePriceKind.Projected))throw new Exception("Actual/projected evidence lost");
   if(side.DependenciesAt(snapshot).Length!=2||side.DependenciesAt(snapshot).Contains(daily.Projections[0].Market))throw new Exception("An actual fixing retained live curve exposure");
  });
  Check("dependency discovery cannot report complete risk inputs when a due fixing is missing",()=>
   Reject("index_fixing_required",()=>side.DependenciesAt(snapshot with{IndexFixings=[]})));
  Check("missing due fixing cannot fall back to an available forward mark",()=>Reject("index_fixing_required",()=>Value(snapshot with{IndexFixings=[]})));
  Check("preliminary, late publication, future knowledge and wrong source are not eligible actuals",()=>{
   Reject("index_fixing_required",()=>Value(snapshot with{IndexFixings=[actual with{State=PublicationState.Preliminary}]}));
   Reject("index_fixing_required",()=>Value(snapshot with{IndexFixings=[actual with{PublishedAt=at.AddSeconds(1)}]}));
   Reject("index_fixing_required",()=>Value(snapshot with{FixingKnowledgeCutoff=actual.KnownAt.AddTicks(-10)}));
   Reject("index_fixing_required",()=>Value(snapshot with{IndexFixings=[actual with{Source=f.R("other_source") }]}));
  });
  Check("later eligible final revisions change later contexts and historical replay retains the original",()=>{
   var corrected=actual with{Key=new(f.Revision.CatalogId,Guid.NewGuid()),Price=new UnitPrice(ExactDecimal.Parse("16"),quote),KnownAt=known.AddDays(1),Revision=2,Corrects=actual.Key};
   var inputs=snapshot with{IndexFixings=[actual,corrected]};
   if(Value(inputs).PresentValue.Scale(ExactDecimal.Parse("3")).ToMoney().Amount.ToString()!="63")throw new Exception("Later knowledge leaked into original context");
   var later=Value(inputs with{FixingKnowledgeCutoff=corrected.KnownAt});
   if(later.PresentValue.Scale(ExactDecimal.Parse("3")).ToMoney().Amount.ToString()!="69"||later.Payments.Single().Resets[0].Price.Fixing?.Key!=corrected.Key)throw new Exception("Eligible final correction was not resolved");
   if(Value(inputs).PresentValue.Scale(ExactDecimal.Parse("3")).ToMoney().Amount.ToString()!="63")throw new Exception("Replaying the original context changed its result");
   var preliminary=corrected with{State=PublicationState.Preliminary};
   if(Value(inputs with{IndexFixings=[actual,preliminary],FixingKnowledgeCutoff=corrected.KnownAt}).Payments.Single().Resets[0].Price.Fixing?.Key!=actual.Key)throw new Exception("A preliminary correction displaced the final fixing");
  });
  Check("fixing-aware wrappers preserve generated zero-lag payment semantics",()=>{
   var monthly=f.Contracts[1];
   var paymentRule=new PaymentRule(PaymentFrequency.Monthly,PaymentDateRule.DaysAfterPeriodEnd,0,null,PaymentDayBasis.CalendarDays,null,DateRoll.Unadjusted,null,PaymentRule.Implementation);
   var underlying=new FloatingSide(Guid.NewGuid(),f.R("index"),quote,unit,monthly.DeliveryFrom,monthly.DeliveryTo,rules[0].FixingAt,paymentRule,FloatingAveraging.MonthlyIndexPrice,
    [new(new(f.C("base_forward"),monthly.Key),monthly)],FloatingSide.Implementation);
   var requirement=rules[0] with{From=monthly.DeliveryFrom,To=monthly.DeliveryTo};
   var wrapped=new FixingAwareFloatingSide(underlying,[requirement],FixingAwareFloatingSide.Implementation);
   var inputs=snapshot with{IndexFixings=[actual with{From=monthly.DeliveryFrom,To=monthly.DeliveryTo}]};
   var value=wrapped.PV(new SideValuationContext(inputs,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
   if(value.Payments.Single().Plan.PaymentDate!=new DateOnly(2041,1,31)||value.Payments.Single().Plan.PaymentConvention!=paymentRule||value.PresentValue.ToMoney().Amount.ToString()!="10")throw new Exception("Generated payment semantics changed in the fixing-aware wrapper");
  });
  Check("ordinary shared market scenarios freeze actuals and shock only projected contributions",()=>{
   var baseMarket=daily.Model.Build(snapshot,side.DependenciesAt(snapshot));
   ExactMoney ValueIn(CurveBuild market)=>side.PV(new SideValuationContext(market,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted))).PresentValue.Scale(ExactDecimal.Parse("3"));
   QuoteShift Shift(int n)=>new(snapshot.Selections[n].Input,quote.Key,PriceUnit.FromQuote(quote),ExactDecimal.Parse("1"),QuoteShift.Convention);
   var actualShock=daily.Model.WithShifts(baseMarket,[Shift(0)]);
   var projectedShock=daily.Model.WithShifts(baseMarket,[Shift(1)]);
   if(ValueIn(actualShock).ToMoney().Amount.ToString()!="63"||ValueIn(projectedShock).ToMoney().Amount.ToString()!="64"||!snapshot.HasSameInputs(projectedShock.Snapshot))throw new Exception("Market shocks changed a resolved actual fixing");
  });
  Check("a fully fixed side can value without any commodity projection inputs",()=>{
   var allRules=rules.Select(r=>r with{FixingAt=rules[0].FixingAt}).ToImmutableArray();
   var fullyFixed=new FixingAwareFloatingSide(projected,allRules,FixingAwareFloatingSide.Implementation);
   var all=rules.Select((r,n)=>actual with{Key=new(f.Revision.CatalogId,Guid.NewGuid()),From=r.From,To=r.To,Price=new UnitPrice(ExactDecimal.Parse((10*(n+1)).ToString(System.Globalization.CultureInfo.InvariantCulture)),quote)}).ToImmutableArray();
   var inputs=snapshot with{IndexFixings=all};
   if(fullyFixed.DependenciesAt(inputs).Length!=0)throw new Exception("A fully fixed side requested projections");
   var value=fullyFixed.PV(new SideValuationContext(inputs,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
   if(value.PresentValue.Scale(ExactDecimal.Parse("3")).ToMoney().Amount.ToString()!="63")throw new Exception("Fully fixed arithmetic changed");
  });
  Check("same snapshot identity with changed actual pins cannot share valuation inputs",()=>{
   if(snapshot.HasSameInputs(snapshot with{IndexFixings=[actual with{Price=new UnitPrice(ExactDecimal.Parse("11"),quote)}]}))throw new Exception("Actual inputs omitted from context equality");
   var baseline=daily.Model.Build(snapshot,side.DependenciesAt(snapshot)).Scenario.Id;
   foreach(var changed in new[]{snapshot with{IndexFixings=[actual with{Price=new UnitPrice(ExactDecimal.Parse("11"),quote)}]},
    snapshot with{FixingContextId=Guid.NewGuid()},snapshot with{FixingKnowledgeCutoff=known.AddTicks(10)}})
    if(daily.Model.Build(changed,side.DependenciesAt(changed)).Scenario.Id==baseline)throw new Exception("Actual inputs omitted from scenario identity");
   var all=snapshot with{IndexFixings=[actual,actual with{Key=new(f.Revision.CatalogId,Guid.NewGuid()),From=rules[1].From,To=rules[1].To}]};
   if(daily.Model.Build(all,side.DependenciesAt(all)).Scenario.Id!=daily.Model.Build(all with{IndexFixings=all.IndexFixings.Reverse().ToImmutableArray()},side.DependenciesAt(all)).Scenario.Id)
    throw new Exception("Fixing input order changed scenario identity");
  });
  Check("legacy projection-only sides keep the original first-fixing rejection",()=>Reject("swap_fixing_required",()=>projected.PV(new SideValuationContext(daily.Model.Build(snapshot,projected.Dependencies),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)))));
  return count;
 }
}
