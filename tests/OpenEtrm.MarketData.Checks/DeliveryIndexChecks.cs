using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class DeliveryIndexChecks
{
 public static void Run()
 {
  var f=new IndexFixingChecks.FixingFixture(monthly:true,delayedMonthlyPublication:true);
  var p=f.Floating().Periods[0];var day=f.From.AddDays(10);
  var period=p with{DeliveryFrom=day,DeliveryTo=day.AddDays(1),Averaging=FloatingAveraging.CalendarDayArithmetic,Allocation=ExactRatio.One,Spread=new UnitPrice(ExactDecimal.Parse("0"),f.Quote),ObservationImplementation="delivery_index_fixings_v1"};
  var side=new FloatingSide(Guid.NewGuid(),f.Quote,new(ExactDecimal.Parse("1"),f.Quote.Quantity),[period],"delivery_index_fixings_v1");
  var row=((SidePaymentSchedule)side.GenerateSchedule()).Payments.Single();
  if(row.DeliveryFrom!=day||row.DeliveryTo!=day.AddDays(1)||row.Prices.Single().Lookup!.Fixing!=f.Fixings[0])throw new Exception("Daily delivery must retain the original monthly fixing.");
  var lookup=row.Prices.Single().Lookup!;
  var before=f.Snapshot(f.Fixings[0].FixingAt.AddDays(-1));
  if(side.DependenciesAt(before).Single()!=f.Projections[0].Market)throw new Exception("Unpublished month must project.");
  var fixedAt=f.Snapshot(f.Fixings[0].FixingAt,1);
  if(side.DependenciesAt(fixedAt).Length!=0)throw new Exception("Published month must stop consuming forwards.");
  var earlyAt=f.Fixings[0].FixingAt.AddDays(-3);
  var early=f.Snapshot(earlyAt,1);early=early with{Selections=[..early.Selections.Select(s=>s.Input.Use==InputUse.FrozenFact?s with{Observation=s.Observation with{ObservedAt=earlyAt}}:s)]};
  try{_=side.DependenciesAt(early);throw new Exception("A final before its minimum eligible boundary was accepted.");}
  catch(MarketDataException e) when(e.Code=="index_fixing_selection"){}
  var beforeExpected=f.Snapshot(f.Fixings[0].FixingAt.AddHours(1),1);
  if(side.DependenciesAt(beforeExpected).Length!=0)throw new Exception("An eligible final must fix without waiting for a later expected release.");
  var late=f.Snapshot(f.Fixings[0].FixingAt.AddHours(-1));
  if(side.DependenciesAt(late).Length!=1)throw new Exception("A missing publication must not become an actual at forward expiry.");
  var value=side.PV(new SideValuationContext(f.Model.Build(fixedAt,[]),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
  if(value.PresentValue.ToMoney().Amount!=ExactDecimal.Parse("10"))throw new Exception("Monthly publication must price the daily quantity exactly once.");
  void Reject(FloatingSidePeriod invalid)
  {
   try{_=new FloatingSide(Guid.NewGuid(),f.Quote,side.UnitQuantity,[invalid],"delivery_index_fixings_v1");}
   catch(MarketDataException e) when(e.Code=="delivery_index_terms"){return;}
   throw new Exception("Invalid delivery index contract was accepted.");
  }
  Reject(period with{DeliveryFrom=f.To,DeliveryTo=f.To.AddDays(1)});
  Reject(period with{Fixings=[]});
  Reject(period with{Fixings=[f.Fixings[0] with{To=f.To.AddDays(1)}]});
  Mixed();
  Console.WriteLine("PASS delivery index: exact monthly fixing on daily delivery, publication boundary, no forward after fixing, malformed coverage refused");
 }
 static void Mixed()
 {
  var f=new IndexFixingChecks.FixingFixture(monthly:true,delayedMonthlyPublication:true);var quote=f.Quote;
  var day=f.From.AddDays(-1);var key=new ContractKey(f.Base.Revision,Guid.NewGuid());
  var contract=new MarketContract(key,ContractKind.Forward,day,f.From,Fixture.Utc("2041-01-01T18:00:00Z"),null);
  var series=new MarketSeries(new(f.Base.Revision,Guid.NewGuid()),key,quote.Key,null);
  var curve=new Curve(f.Base.C("daily_publication"),quote.Key,f.Base.R("location"),f.Base.R("uniform"),new(SettlementKind.Index,f.Base.R("daily_index"),null),CurveRule.Direct,[key],[],ObservationRole.TraderMark,f.Base.R("source"),null);
  var fixing=new IndexFixing(f.Base.R("daily_index"),quote.Key,day,f.From,Fixture.Utc("2040-12-30T23:00:00Z"),series.Key,f.Base.R("actual_source"),IndexFixing.SupportedImplementation);
  var model=new CurveModel(f.Base.Revision,f.Base.Reference,f.Base.Quantities,[quote],[contract,f.Projections[0].Contract],[curve,f.Curve],[],[],[series,f.Series[0]]);
  var monthlyFixing=f.Fixings[0] with{FixingAt=Fixture.Utc("2041-01-01T00:00:00Z")};
  var monthly=f.Floating().Periods[0] with{Fixings=[monthlyFixing],FirstFixingAt=monthlyFixing.FixingAt,Averaging=FloatingAveraging.CalendarDayArithmetic,Allocation=ExactRatio.One,Spread=new UnitPrice(ExactDecimal.Parse("0"),quote),ObservationImplementation=FloatingSide.DeliveryIndexImplementation};
  var periods=ImmutableArray.CreateBuilder<FloatingSidePeriod>();
  periods.Add(monthly with{DeliveryFrom=day,DeliveryTo=f.From,FirstFixingAt=fixing.FixingAt,Fixings=[fixing],Projections=[new(new(curve.Key,key),contract)]});
  for(var d=f.From;d<f.To;d=d.AddDays(1))periods.Add(monthly with{DeliveryFrom=d,DeliveryTo=d.AddDays(1)});
  var side=new FloatingSide(Guid.NewGuid(),quote,new(ExactDecimal.Parse("1"),quote.Quantity),periods.ToImmutable(),FloatingSide.DeliveryIndexImplementation);
  if(side.GenerateDatedSchedule().Payments.Length!=32||side.GenerateDatedSchedule().Payments.Select(p=>p.AllocatedQuantity).Aggregate((a,b)=>a.Add(b)).Numerator!="32")throw new Exception("Dated legs must conserve all32 daily quantities.");
  var at=Fixture.Utc("2041-01-04T18:00:00Z");
  SelectedObservation Actual(MarketSeries s,string price,DateTimeOffset issued)=>new(new(s.Key,InputUse.FrozenFact),new(new(f.Base.Revision.CatalogId,Guid.NewGuid()),s.Key,f.Base.R("actual_source"),ObservationRole.IndexFixing,PublicationState.Final,issued,ExactDecimal.Parse(price)));
  var frozen=Actual(f.Series[0],"3.2",at);
  var snapshot=f.Snapshot(at) with{Selections=[Actual(series,"2.9",fixing.FixingAt),frozen]};
  ExactDecimal Value(MarketSnapshot input)=>side.PV(new SideValuationContext(model.Build(input,side.DependenciesAt(input)),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted))).PresentValue.ToMoney().Amount;
  if(Value(snapshot)!=ExactDecimal.Parse("102.1")||side.DependenciesAt(snapshot).Length!=0)throw new Exception("Daily publication plus31 monthly-priced days must equal102.10 with no live curve exposure.");
  var corrected=snapshot with{Selections=[snapshot.Selections[0],Actual(f.Series[0],"3.25",at)]};
  if(Value(corrected)!=ExactDecimal.Parse("103.65")||Value(snapshot)!=ExactDecimal.Parse("102.1"))throw new Exception("A selected correction must not mutate the earlier snapshot or double-count monthly quantities.");
 }

}
