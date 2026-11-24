using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.Shared.Temporal;

internal static class RecForwardChecks
{
 internal static int Run(Action<SidedInstrument, SideValuationContext>? inspect = null)
 {
  var f=new Fixture();var one=ExactDecimal.Parse("1");var q=new QuantitySpecification(f.R("cert_count"),f.R("cert"),f.R("issued"),f.R("rec"),f.R("number"));
  var quote=new QuoteSpecification(f.R("cert_price"),QuoteKind.Outright,f.R("currency"),q.Key,one,ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.001"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
  var unit=new Quantity(one,q.Key);var date=new DateOnly(2041,1,15);var interval=new DeliveryInterval(Fixture.Utc("2041-01-15T00:00:00Z"),Fixture.Utc("2041-01-16T00:00:00Z"),date,0,0,1);
  var cal=new SideCalendarPolicy(CalendarBinding.NotApplicable(),CalendarBinding.NotApplicable(),CalendarBinding.NotApplicable(),SideCalendarPolicy.Implementation);var monthly=new DateOnly(2041,1,1);
  var rule=new PaymentRule(PaymentFrequency.Monthly,PaymentDateRule.DaysAfterPeriodEnd,10,null,PaymentDayBasis.CalendarDays,null,DateRoll.Unadjusted,null,PaymentRule.Implementation);
  OperatingSideTerms Terms(ComponentKind kind,CurveRequest? projection)=>new(unit,quote,kind==ComponentKind.Payment?rule:null,kind,[new(interval,monthly,monthly.AddMonths(1),new(null,unit),projection)],OperatingSideTerms.SupportedImplementation,cal);
  var curve=f.Direct("rec",f.K(5),quote.Key,SettlementKind.Index);
  var model=new CurveModel(f.Revision,f.Reference,[q],[quote],[f.Contracts[4]],[curve],[],[],[new MarketSeries(f.S(7),f.K(5),quote.Key,null)]);
  var raw=f.Snapshot() with{Selections=[f.Select(5,QuoteKind.Outright,"cert_price","50")]};var snapshot=raw.WithKnowledge(Guid.NewGuid(),raw.ValuationAt);
  var physical=new FloatingSide(Guid.NewGuid(),Terms(ComponentKind.Delivery,new(f.C("rec"),f.K(5))));
  var cash=new FixedSide(Guid.NewGuid(),Terms(ComponentKind.Payment,null),new FlatPrice(new(ExactDecimal.Parse("40"),quote)));
  var instrument=new RecForwardInstrument(Guid.NewGuid(),f.R("product_a"),new(2040,1,1),new(2041,1,1),[new(physical,one),new(cash,ExactDecimal.Parse("-1"))]);
  var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(Guid.NewGuid(),instrument,new(ExactDecimal.Parse("100"),q.Key))]);
  var setup=new ValuationSetup(ValuationMeasure.PresentValue,[],new ExplicitDiscounting(one,DiscountFactorSource.Undiscounted));
  var portfolio=ValuationPortfolio.FromTrades([trade],setup);var bump=new QuoteBump(new(f.S(7),InputUse.Variable),quote.Key,PriceUnit.FromQuote(quote),one,QuoteBump.Convention);
  var risk=RiskEngine.Calculate(model,snapshot,portfolio,[bump],RiskEngine.NumericalPolicy).Factors.Single().Totals.Single();
  if(risk.Base.Amount!=ExactDecimal.Parse("1000")||risk.Up.Amount!=ExactDecimal.Parse("1100")||risk.Down.Amount!=ExactDecimal.Parse("900"))throw new Exception("One hundred certificates must scale independent 50-minus-40 sides once and shock the selected market consistently.");
  var retained=new RetainedRecForwardInstrument(Guid.NewGuid(),instrument.Product,instrument.VintageFrom,instrument.VintageTo,unit,quote.Currency,
   [..instrument.Sides.Select(s=>new RetainedOperatingMember(s.Side.Id,s.Coefficient,BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(s.Side))))]);
  RetainedInstrumentContractChecks.Sides(retained,[..retained.Members.Select(m=>(m.SideId,m.Program,m.Coefficient,(OpenEtrm.Shared.Numerics.ExactRatio?)null))]);
  void Refuse(Side physicalSide,Side cashSide)
  {
   try{_=new RetainedRecForwardInstrument(Guid.NewGuid(),instrument.Product,instrument.VintageFrom,instrument.VintageTo,unit,quote.Currency,
    [new(physicalSide.Id,one,BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(physicalSide))),new(cashSide.Id,ExactDecimal.Parse("-1"),BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(cashSide)))]);}
   catch(MarketDataException error)when(error.Code=="rec_forward"){return;}
   throw new Exception("Retained REC admission accepted a different unit quantity or delivery/cash interval.");
  }
  var doubled=physical.OperatingDefinition! with{Periods=[new(interval,monthly,monthly.AddMonths(1),new(null,new(ExactDecimal.Parse("2"),q.Key)),new(f.C("rec"),f.K(5)))]};
  Refuse(new FloatingSide(Guid.NewGuid(),doubled),cash);
  var nextDay=new DeliveryInterval(interval.End,interval.End.AddDays(1),date.AddDays(1),0,0,1);
  var mismatched=cash.OperatingDefinition! with{Periods=[new(nextDay,monthly,monthly.AddMonths(1),new(null,unit),null)]};
  Refuse(physical,new FixedSide(Guid.NewGuid(),mismatched,new FlatPrice(new(ExactDecimal.Parse("40"),quote))));
  var physicalProgram=retained.Members[0].Program;var market=model.Build(snapshot,instrument.Dependencies);
  inspect?.Invoke(instrument, new SideValuationContext(market, setup.Discounting!));
  inspect?.Invoke(retained, SideValuationContext.ForRetainedPrograms(market, market, setup.Discounting!));
  var execution=ScheduleMarketSources.ForOperating(market).Evaluate(physicalProgram,physical.Id,market,new ExplicitDiscounting(one,DiscountFactorSource.Undiscounted));
  var inspection=SideScheduleSnapshot.FromProgram(physical.Id,snapshot.Id,new("physical_delivery",execution,physicalProgram,ExactDecimal.Parse("100"),new(-1,1),[]),d=>new(d.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero));
  var detail=inspection.Read(0,8).Rows.Single();
  if(detail.Kind!="Delivery"||detail.PaymentAt is not null||detail.PaymentTiming is not null||detail.Lifecycle.Cash is not null)
   throw new Exception("A retained certificate delivery remains a physical Side contribution without a cash payment or settlement law.");
  if(detail.Quantity!="-100"||detail.Amount!="-5000"||detail.PresentValue!="-5000"||detail.Start!=interval.Start||detail.End!=interval.End)
   throw new Exception("Native Side inspection must show exactly one hundred signed certificates and their five-thousand gross mark.");
  var retainedTrade=new Trade(Guid.NewGuid(),trade.BookId,[new(Guid.NewGuid(),retained,new(ExactDecimal.Parse("100"),q.Key))]);
  var reopened=RiskEngine.Calculate(model,snapshot,ValuationPortfolio.FromTrades([retainedTrade],setup),[bump],RiskEngine.NumericalPolicy).Factors.Single().Totals.Single();
  if(reopened.Base!=risk.Base||reopened.Up!=risk.Up||reopened.Down!=risk.Down)throw new Exception("Retained certificate schedules must preserve native unit scaling and factor scenarios.");
  if(retained.Sides[0].Side.GenerateSchedule().Rows.Single().Key.Timing!=ValueTimingKind.Instant)
   throw new Exception("A retained physical output must keep the saved instant carrying key; its scalar row is not a UTC cash-payment day.");
  RetainedInstrumentContractChecks.Rec(retained);
  try{retained.PV(new SideValuationContext(snapshot with{ValuationAt=interval.Start},setup.Discounting!));throw new Exception("A started REC instrument must reject direct Side-context PV.");}
  catch(MarketDataException error)when(error.Code=="rec_delivery_lifecycle"){}
  if(physical.GenerateSchedule().Rows.Single().Key.Timing!=ValueTimingKind.Instant)throw new Exception("Certificate delivery is a physical mark with an instant boundary.");
  Console.WriteLine("PASS REC forward: exact product/vintage, independently retained physical/cash sides and one-time native count scaling");return 1;
 }
}
