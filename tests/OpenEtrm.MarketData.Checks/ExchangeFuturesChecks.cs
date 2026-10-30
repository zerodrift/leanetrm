using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;

internal static class ExchangeFuturesChecks
{
 public static int Run()
 {
  var count=0;var f=new Fixture();var original=f.Contracts[0];var quote=f.Quotes[0];
  var first=Fixture.Utc("2040-01-01T00:00:00Z");
  FuturesSeriesSpecification Series(string size,LotBasis basis,string settlement,FuturesLotResolution? resolution=null)=>new(
   f.R("future_jan"),quote.Quantity,quote.Currency,quote.Multiplier,quote.TradingTick,f.R("location"),f.R("uniform"),
   original.DeliveryFrom,original.DeliveryTo,original.Expiry,basis==LotBasis.PerDeliveryHour?f.R("power_quantity"):quote.Quantity,
   ExactDecimal.Parse(size),basis,first,settlement,resolution);
  void Check(string name,Action work){work();count++;Console.WriteLine("PASS exchange futures: "+name);}
  void Equal(string actual,string expected){if(actual!=expected)throw new Exception($"Expected {expected}, got {actual}.");}
  void Reject(Action work){try{work();}catch(MarketDataException){return;}throw new Exception("Expected an explicit unsupported/missing lot error.");}
  UnitPrice Price(string n)=>new(ExactDecimal.Parse(n),quote);
  Check("physical NG uses its 10000 MMBtu lot without requiring a cash-settlement label",()=>{
   var instrument=new FuturesContract(Series("10000",LotBasis.FixedTotal,"physical"));
   Equal(instrument.LotSize.Amount.ToString(),"10000");
   Equal(instrument.ExecutionPnl(Price("3.1"),Price("3"),ExactDecimal.Parse("2")).Amount.ToString(),"2000");
   Reject(()=>instrument.PV((SideValuationContext)null!));
  });
  Check("a directly observed basis future retains settlement references without upstream price exposure",()=>{
   var basisQuote=quote with {Kind=QuoteKind.Basis};
   var series=Series("2500",LotBasis.FixedTotal,"cash") with {BasisSettlement=new(f.R("regional_index"),f.R("benchmark_series"),"index_minus_future_v1")};
   var binding=new FuturesMarketBinding(Guid.NewGuid(),new(new(original.Key.Revision,"basis"),original.Key),original,series,basisQuote,"cash",FuturesMarketBinding.Implementation);
   Equal(binding.ExecutionPnl(new(ExactDecimal.Parse("-0.2"),basisQuote),new(ExactDecimal.Parse("-0.3"),basisQuote),ExactDecimal.Parse("2")).Amount.ToString(),"500");
   if(binding.Dependencies.Length!=1||binding.Dependencies[0]!=binding.Market)throw new Exception("A directly observed basis mark must expose only its selected factor.");
   Equal(binding.Convention,FuturesMarketBinding.Implementation);
   Reject(()=>new FuturesMarketBinding(Guid.NewGuid(),binding.Market,original,series with {BasisSettlement=null},basisQuote,"cash",FuturesMarketBinding.Implementation));
   Reject(()=>new FuturesMarketBinding(Guid.NewGuid(),binding.Market,original,series,quote,"cash",FuturesMarketBinding.Implementation));
   Reject(()=>new FuturesMarketBinding(Guid.NewGuid(),binding.Market,original,series,basisQuote,"cash","fixed_total_cash_execution_pnl_v1"));
  });
  foreach(var hours in new[]{384,416,432})Check($"published {hours} MWh lot applies once to signed lots and quote price",()=>{
   var lot=new FuturesLotResolution(new(ExactDecimal.Parse(hours.ToString()),quote.Quantity),ExactDecimal.Parse(hours.ToString()),"published_contract_size_v1");
   var instrument=new FuturesContract(Series("1",LotBasis.PerDeliveryHour,"cash",lot));
   Equal(instrument.LotSize.Amount.ToString(),hours.ToString());
   Equal(instrument.LotSize.Specification.Code,quote.Quantity.Code);
   Equal(instrument.ExecutionPnl(Price("31"),Price("30"),ExactDecimal.Parse("-2")).Amount.ToString(),(-2*hours).ToString());
  });
  Check("calendar-day rate names its rule and retains the complete period quantity",()=>{
   var days=original.DeliveryTo.DayNumber-original.DeliveryFrom.DayNumber;
   var lot=new FuturesLotResolution(new(ExactDecimal.Parse((2500*days).ToString()),quote.Quantity),ExactDecimal.Parse(days.ToString()),"calendar_delivery_days_v1");
   Equal(new FuturesContract(Series("2500",LotBasis.PerDeliveryDay,"cash",lot)).LotSize.Amount.ToString(),(2500*days).ToString());
  });
  Check("final valid cash survives an intermediate price difference outside the working decimal range",()=>{
   var series=Series("0.01",LotBasis.FixedTotal,"physical");
   var wide=quote with {Minimum=ExactDecimal.Parse("-999999999999999999999999"),Maximum=ExactDecimal.Parse("999999999999999999999999")};
   var binding=new FuturesMarketBinding(Guid.NewGuid(),new(new(original.Key.Revision,"boundary"),original.Key),original,series,wide,"physical",FuturesMarketBinding.Implementation);
   Equal(binding.ExecutionPnl(new(ExactDecimal.Parse("900000000000000000000000"),wide),new(ExactDecimal.Parse("-900000000000000000000000"),wide),ExactDecimal.Parse("1")).Amount.ToString(),"18000000000000000000000");
  });
  Check("variable lots reject missing factors, inconsistent totals, wrong exact units and unselected rules",()=>{
   Reject(()=>new FuturesContract(Series("1",LotBasis.PerDeliveryHour,"cash")));
   var good=new FuturesLotResolution(new(ExactDecimal.Parse("432"),quote.Quantity),ExactDecimal.Parse("432"),"published_contract_size_v1");
   Reject(()=>new FuturesContract(Series("2",LotBasis.PerDeliveryHour,"cash",good)));
   Reject(()=>new FuturesContract(Series("1",LotBasis.PerDeliveryHour,"cash",good with {Quantity=new(ExactDecimal.Parse("432"),f.R("other_quantity"))})));
   Reject(()=>new FuturesContract(Series("1",LotBasis.PerDeliveryHour,"cash",good with {Implementation=""})));
   Reject(()=>new FuturesContract(Series("1",LotBasis.FixedTotal,"cash",good)));
  });
  Check("risk and holdings agree when only the final scaled execution P&L fits",()=>{
   foreach(var multiplier in new[]{"1","100"})
   {
   var series=Series(multiplier=="1"?"0.01":"0.0001",LotBasis.FixedTotal,"physical") with {Multiplier=ExactDecimal.Parse(multiplier)};
   var wide=quote with {Multiplier=ExactDecimal.Parse(multiplier),Minimum=ExactDecimal.Parse("-999999999999999999999999"),Maximum=ExactDecimal.Parse("999999999999999999999999")};
   var model=new CurveModel(original.Key.Revision,wide.Key.Revision,[f.Quantities.Single(q=>q.Key==wide.Quantity)],[wide],[original],[f.Curves[0]],[series],[],[new(f.S(1),original.Key,wide.Key,null)]);
   var selected=f.Snapshot();var snapshot=selected with {Selections=[selected.Selections[0] with {Observation=selected.Selections[0].Observation with {Value=ExactDecimal.Parse("900000000000000000000000")}}]};
   var binding=model.BindFutures(Guid.NewGuid(),new(f.C("future"),original.Key),"physical",FuturesMarketBinding.Implementation);
   var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[FuturesExecution.Bind(Guid.NewGuid(),binding,ExactDecimal.Parse("1"),new(ExactDecimal.Parse("-900000000000000000000000"),wide),snapshot.ValuationAt,FuturesSettlementState.Unprocessed)]);
   var result=FuturesRisk.Calculate([trade],snapshot,model,[new(new(f.S(1),InputUse.Variable),wide.Key,PriceUnit.FromQuote(wide),ExactDecimal.Parse("1"),QuoteBump.Convention)],FuturesRisk.Implementation,RiskEngine.NumericalPolicy).Factors.Single().Positions.Single();
   Equal(result.Base.Amount.ToString(),"18000000000000000000000");Equal(result.UpPnl.Amount.ToString(),"0.01");Equal(result.DownPnl.Amount.ToString(),"-0.01");Equal(result.Delta.Value.ToString(),"0.01");Equal(result.Gamma.Value.ToString(),"0");
   }
  });
  return count;
 }
}
