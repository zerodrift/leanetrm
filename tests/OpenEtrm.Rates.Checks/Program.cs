using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.Rates;
using OpenEtrm.MarketData;

if(args is ["--monthly-swap-benchmark"]){MonthlySwapRateChecks.Benchmark();return;}

var passed=0;
passed+=RatesCurveChecks.Run();
void Check(string label,Action test){test();Console.WriteLine("PASS "+label);passed++;}
void Reject(string code,Action test){try{test();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var reference=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
ReferenceKey R(string code)=>new(reference,code);
var series=new SeriesKey(revision,Guid.NewGuid());
var observation=new MarketObservation(new(revision.CatalogId,Guid.NewGuid()),series,R("source"),ObservationRole.Parameter,PublicationState.Final,DateTimeOffset.Parse("2021-01-01T00:00:00Z",CultureInfo.InvariantCulture),ExactDecimal.Parse("0.05"));
var definition=new ZeroRateDefinition(new(revision,"rates"),R("spec"),R("currency"),"actual_365_fixed","simple","openetrm_rates_v1","valuation_utc_date",R("source"),[new(series,new(2022,1,1))]);
var snapshot=new MarketSnapshot(Guid.NewGuid(),revision,observation.ObservedAt,observation.ObservedAt,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[new(new(series,InputUse.Variable),observation)]);
ZeroRateBuild Build(MarketObservation? value=null,ZeroRateDefinition? terms=null,MarketSnapshot? context=null)=>new ZeroRateCurve(terms??definition).Build(context??snapshot with {Selections=[new(new(series,InputUse.Variable),value??observation)]});
double DF(ZeroRateBuild result)=>double.Parse(result.Values[0].DiscountFactor,CultureInfo.InvariantCulture);
void Near(double actual,double expected){if(Math.Abs(actual-expected)>1e-15)throw new Exception($"Expected {expected}, got {actual}");}
Check("simple annual discount factor",()=>{Near(DF(Build()),1/1.05);if(Build().NumericalPolicy!=ZeroRateCurve.Policy)throw new Exception("Missing numerical policy");});
Check("zero rate discounts to one",()=>Near(DF(Build(observation with {Value=ExactDecimal.Parse("0")})),1));
Check("negative rates produce a factor above one",()=>Near(DF(Build(observation with {Value=ExactDecimal.Parse("-0.01")})),1/.99));
Check("actual365 fixed counts leap days explicitly",()=>{var start=DateTimeOffset.Parse("2020-01-01T00:00:00Z",CultureInfo.InvariantCulture);Near(DF(Build(terms:definition with {Pillars=[new(series,new(2021,1,1))]},context:snapshot with {ValuationAt=start,MarketCutoff=start,Selections=[new(new(series,InputUse.Variable),observation with {ObservedAt=start})]})),1/(1+.05*366/365));});
Check("unsupported day count is never silently replaced",()=>Reject("unsupported_rate_convention",()=>Build(terms:definition with {DayCount="unknown"})));
Check("unsupported compounding is never silently replaced",()=>Reject("unsupported_rate_convention",()=>Build(terms:definition with {Compounding="continuous"})));
Check("unsupported implementation rejected",()=>Reject("unsupported_rate_convention",()=>Build(terms:definition with {Implementation="next"})));
Check("missing exact rate observation rejected",()=>Reject("missing_observation",()=>Build(context:snapshot with {Selections=[]})));
Check("wrong source rejected",()=>Reject("rate_observation",()=>Build(observation with {Source=R("other")})));
Check("preliminary rate cannot masquerade as final",()=>Reject("rate_observation",()=>Build(observation with {State=PublicationState.Preliminary})));
Check("rate newer than cutoff rejected",()=>Reject("rate_observation",()=>Build(observation with {ObservedAt=observation.ObservedAt.AddMinutes(1)})));
Check("duplicate selected input rejected",()=>Reject("observation_conflict",()=>Build(context:snapshot with {Selections=snapshot.Selections.Add(snapshot.Selections[0])})));
Check("future rate knowledge cannot enter a past valuation",()=>Reject("rate_snapshot",()=>Build(context:snapshot with {MarketCutoff=snapshot.ValuationAt.AddDays(1)})));
Check("non-UTC context rejected",()=>Reject("rate_snapshot",()=>Build(context:snapshot with {ValuationAt=snapshot.ValuationAt.ToOffset(TimeSpan.FromHours(1))})));
Check("unsupported rate date rejected",()=>Reject("rate_date",()=>Build(context:snapshot with {ValuationAt=new(2300,1,1,0,0,0,TimeSpan.Zero)})));
Check("matured pillar cannot silently roll",()=>Reject("rate_maturity",()=>Build(terms:definition with {Pillars=[new(series,new(2021,1,1))]})));
Check("ambiguous maturity order rejected",()=>Reject("rate_definition",()=>Build(terms:definition with {Pillars=[new(series,new(2022,1,1)),new(new(revision,Guid.NewGuid()),new(2021,6,1))]})));
Check("unsupported rate domain rejected",()=>Reject("rate_domain",()=>Build(observation with {Value=ExactDecimal.Parse("2")})));
Check("nonpositive implied factor rejected",()=>Reject("rate_domain",()=>Build(observation with {Value=ExactDecimal.Parse("-0.1")},terms:definition with {Pillars=[new(series,new(2042,1,1))]})));
Check("rational division has an exact declared 18-digit result",()=>{var v=Build().Values[0];if(v.DiscountFactor!="0.952380952380952381"||v.Days!=365)throw new Exception("Incorrect exact factor");});
Check("display division uses six-digit even ties directly from rational",()=>{var v=Build(observation with {Value=ExactDecimal.Parse("0.024")}).Values[0];if(v.DiscountFactor!="0.9765625"||v.DisplayDiscountFactor!="0.976562")throw new Exception("Incorrect display tie");});
Check("18-digit rational division uses ties to even",()=>{var v=Build(observation with {Value=ExactDecimal.Parse("0.34217728")}).Values[0];if(v.DiscountFactor!="0.745058059692382812")throw new Exception("Incorrect 18-digit tie");});
Check("day count is the exact half-open date difference across leap day",()=>{var v=RateConventions.SimpleActual365Fixed(new(2020,2,28),new(2020,3,1),ExactDecimal.Parse("0"));if(v.Days!=2||v.Factor!="1"||v.DisplayFactor!="1.000000")throw new Exception("Incorrect leap day count");});
Check("undefined simple discount factor pole is rejected",()=>Reject("rate_domain",()=>RateConventions.SimpleActual365Fixed(new(2021,1,1),new(2031,1,1),ExactDecimal.Parse("-0.1"))));
Check("nonpositive maturity is rejected by convention implementation",()=>Reject("rate_maturity",()=>RateConventions.SimpleActual365Fixed(new(2021,1,1),new(2021,1,1),ExactDecimal.Parse("0"))));
Check("display rounding avoids double rounding from the stored factor",()=>{var v=RateConventions.SimpleActual365Fixed(new(2021,1,1),new(2021,1,11),ExactDecimal.Parse("0.875999999999999999"));if(v.Factor!="0.9765625"||v.DisplayFactor!="0.976563")throw new Exception("Double rounding changed presentation");});
Check("exact zero denominator is rejected",()=>Reject("rate_domain",()=>RateConventions.SimpleActual365Fixed(new(2021,1,1),new DateOnly(2021,1,1).AddDays(3650),ExactDecimal.Parse("-0.1"))));
Check("near-pole positive factor retains exact large magnitude",()=>{var v=RateConventions.SimpleActual365Fixed(new(2021,1,1),new DateOnly(2021,1,1).AddDays(3650),ExactDecimal.Parse("-0.099999999999999999"));if(v.Factor!="100000000000000000")throw new Exception("Lost near-pole precision");});
Check("parallel repeatability has no global evaluation date",()=>{var expected=Build().Values[0].DiscountFactor;Parallel.For(0,500,_=>{if(Build().Values[0].DiscountFactor!=expected)throw new Exception("Nonrepeatable result");});});
Check("log discount interpolation matches independent high precision midpoint and unequal-day oracles",()=>{
    var start=new DateOnly(2040,1,1);
    string At(string a,string b,int n,int d)=>DiscountInterpolation.Factor(start,ExactDecimal.Parse(a),start.AddDays(d),ExactDecimal.Parse(b),start.AddDays(n),DiscountInterpolation.Policy).ToString();
    if(At("0.99","0.95",1,2)!="0.969793792514676802" || At("0.99","0.95",17,31)!="0.967860375643978351" ||
       At("1.01","1.05",3,7)!="1.026952802995532224" || At("1","0.81",1,2)!="0.9" ||
       At("0.952380952380952381","0.909090909090909091",30,181)!="0.94506585697270284")throw new Exception("Incorrect geometric interpolation");
});
Check("log interpolation retains endpoints and small exact geometric roots",()=>{
    var start=new DateOnly(2040,1,1);var end=start.AddDays(2);
    string At(string a,string b,int n)=>DiscountInterpolation.Factor(start,ExactDecimal.Parse(a),end,ExactDecimal.Parse(b),start.AddDays(n),DiscountInterpolation.Policy).ToString();
    if(At("0.999999999999999999","0.81",0)!="0.999999999999999999" || At("1","0.81",2)!="0.81" ||
       At("0.000000000000000001","0.000000000000000004",1)!="0.000000000000000002" ||
       At("0.000000000000000002","0.000000000000000008",1)!="0.000000000000000004")throw new Exception("Endpoint or small factor changed");
});
Check("log interpolation rejects extrapolation missing policy nonpositive factors and oversized spans",()=>{
    var start=new DateOnly(2040,1,1);var one=ExactDecimal.Parse("1");
    Reject("discount_interpolation",()=>DiscountInterpolation.Factor(start,one,start.AddDays(2),one,start.AddDays(3),DiscountInterpolation.Policy));
    Reject("discount_interpolation",()=>DiscountInterpolation.Factor(start,one,start.AddDays(2),one,start,""));
    Reject("discount_interpolation",()=>DiscountInterpolation.Factor(start,default,start.AddDays(2),one,start.AddDays(1),DiscountInterpolation.Policy));
    Reject("discount_interpolation",()=>DiscountInterpolation.Factor(start,one,start,one,start,DiscountInterpolation.Policy));
    Reject("discount_interpolation",()=>DiscountInterpolation.Factor(start,one,start.AddDays(DiscountInterpolation.MaximumPillarDays+1),one,start.AddDays(1),DiscountInterpolation.Policy));
});
Check("discounting false uses one without any default mapping or rate observations",()=>{
    var value=PaymentDiscount.Resolve(false,R("currency"),new(2021,7,1),snapshot with {Selections=[]},null);
    if(value.Factor.ToString()!="1"||value.MappingId is not null||value.Curve is not null||!value.Observations.IsEmpty)throw new Exception("Unexpected discount input");
});
Check("currency default discount retains exact mapping curve snapshot and selected pillar evidence",()=>{
    var mapping=new CurrencyDiscountCurve(Guid.NewGuid(),R("currency"),definition,DiscountInterpolation.Policy);
    var value=PaymentDiscount.Resolve(true,R("currency"),new(2022,1,1),snapshot,mapping);
    if(value.Factor.ToString()!="0.952380952380952381"||value.MappingId!=mapping.Id||value.Curve!=definition.Key||
       value.Snapshot!=snapshot||value.Observations.Single()!=observation.Key||value.RateNumericalPolicy!=ZeroRateCurve.Policy||value.InterpolationPolicy!=DiscountInterpolation.Policy)throw new Exception("Lost exact discount evidence");
});
Check("discount default does not cross currency definition context or coverage",()=>{
    var mapping=new CurrencyDiscountCurve(Guid.NewGuid(),R("currency"),definition,DiscountInterpolation.Policy);
    Reject("discount_curve_missing",()=>PaymentDiscount.Resolve(true,R("currency"),new(2022,1,1),snapshot,null));
    Reject("discount_curve",()=>PaymentDiscount.Resolve(true,R("other"),new(2022,1,1),snapshot,mapping));
    Reject("rate_snapshot",()=>PaymentDiscount.Resolve(true,R("currency"),new(2022,1,1),snapshot with {Definitions=new(Guid.NewGuid(),Guid.NewGuid())},mapping));
    Reject("discount_coverage",()=>PaymentDiscount.Resolve(true,R("currency"),new(2021,7,1),snapshot,mapping));
    Reject("discount_coverage",()=>PaymentDiscount.Resolve(true,R("currency"),new(2023,1,1),snapshot,mapping));
    Reject("discount_curve",()=>PaymentDiscount.Resolve(true,R("currency"),new(2022,1,1),snapshot,mapping with {Id=Guid.Empty}));
});
Check("currency default interpolates only bracketing factors in the complete selected rate context",()=>{
    var nextSeries=new SeriesKey(revision,Guid.NewGuid());
    var first=new DateOnly(2022,1,1);var second=first.AddDays(181);
    var terms=definition with {Pillars=[new(series,first),new(nextSeries,second)]};
    var selected=snapshot with {Selections=snapshot.Selections.Add(new(new(nextSeries,InputUse.Variable),observation with {Key=new(revision.CatalogId,Guid.NewGuid()),Series=nextSeries,Value=ExactDecimal.Parse("0.1")}))};
    var mapping=new CurrencyDiscountCurve(Guid.NewGuid(),R("currency"),terms,DiscountInterpolation.Policy);
    var rates=new ZeroRateCurve(terms).Build(selected);
    var expected=DiscountInterpolation.Factor(first,ExactDecimal.Parse(rates.Values[0].DiscountFactor),second,ExactDecimal.Parse(rates.Values[1].DiscountFactor),first.AddDays(30),DiscountInterpolation.Policy);
    var value=PaymentDiscount.Resolve(true,R("currency"),first.AddDays(30),selected,mapping);
    if(value.Factor!=expected||value.Observations.Length!=2||value.Snapshot!=selected)throw new Exception("Lost bracketing inputs");
    Reject("missing_observation",()=>PaymentDiscount.Resolve(true,R("currency"),first.AddDays(30),snapshot,mapping));
});
Check("undiscounted helper still requires structurally exact currency context and future payment",()=>{
    Reject("discount_context",()=>PaymentDiscount.Resolve(false,default,new(2022,1,1),snapshot,null));
    Reject("discount_context",()=>PaymentDiscount.Resolve(false,R("currency"),new(2022,1,1),snapshot with {Id=Guid.Empty},null));
    Reject("discount_context",()=>PaymentDiscount.Resolve(false,R("currency"),new(2022,1,1),snapshot with {ValuationAt=snapshot.ValuationAt.ToOffset(TimeSpan.FromHours(1))},null));
    Reject("discount_context",()=>PaymentDiscount.Resolve(false,R("currency"),new(2020,1,1),snapshot,null));
});
passed += SwapValuationChecks.Run();
passed += MonthlySwapRateChecks.Run();
passed += DiscountLookupChecks.Run();
passed += ZeroRateLookupChecks.Run();
passed += DiscountInterpolationChecks.Run();
Console.WriteLine($"{passed} rate convention checks passed.");
