using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

internal static class PpaBreakEvenChecks
{
    internal static void Run()
    {
        var f=new PpaAcceptanceFixture();var market=f.Market(f.From.AddDays(-1));var coverage=new DeliveryCoverage(f.From,f.To);
        var slot=new SideSlot(PpaRole.StrikePayment);var noDiscount=new ExplicitDiscounting(D("1"),DiscountFactorSource.Undiscounted);
        static ExactDecimal D(string s)=>ExactDecimal.Parse(s);
        void Equal(string label,string actual,string expected){if(actual!=expected)throw new Exception(label+": "+actual+" != "+expected);Console.WriteLine("PASS break-even: "+label);}
        void Refuse(string label,Action action,string? code=null){try{action();}catch(MarketDataException e)when(code is null || e.Code==code){Console.WriteLine("PASS break-even refusal: "+label);return;}throw new Exception(label);}
        PpaBreakEvenPlan Plan(PpaInstrument i,string measure="ValueExcludingOverdue",string mw="80",int decimals=2)=>PpaBreakEvenPlan.Create(i,slot,coverage,D(mw),measure,decimals,"half_even_v1");
        PpaBreakEvenResult Solve(PpaBreakEvenPlan p,PpaAcceptanceFixture.MarketInputs? m=null,ISideDiscounting? discount=null,QuantitySeries? forecast=null)
        {
            var selected=m??market;var inputs=f.Prepare(selected,p.Basis,forecastOverride:forecast);
            return p.Solve(inputs.Session(selected.Build),discount??noDiscount);
        }
        var physical=f.Physical();var plan=Plan(physical);var result=Solve(plan);
        Equal("physical root",result.Price.Numerator,"42");Equal("physical sensitivity",result.Sensitivity.Numerator,"-57680");
        Equal("original draft value",result.InputValue.Numerator,"115360");Equal("exact quote residual",result.QuoteResidual.Numerator,"0");
        var graphs=PpaPricingProfile.Create(plan,result);
        var energy=graphs.Periods.Single(p=>p.Kind=="Delivery");var payment=graphs.Periods.Single(p=>p.Kind=="Payment");
        Equal("monthly energy server aggregate",energy.Quantity.Numerator,"57680");
        Equal("physical value stays separate",energy.Amount.Numerator,"2422560");
        Equal("gross strike cash",payment.Amount.Numerator,"-2307200");
        Equal("market profile weighted price",energy.EffectivePrice!.Numerator,"42");
        Equal("strike profile weighted price",payment.EffectivePrice!.Numerator,"40");
        Equal("curve marks exact root",graphs.ValueCurve.Single(p=>p.Label=="Model break-even").Value.Numerator,"0");
        Equal("curve upper root point",graphs.ValueCurve.Single(p=>p.Label=="Break-even + 1 quote unit").Value.Numerator,"-57680");
        Equal("quantity scales sensitivity only",Solve(Plan(physical,mw:"40")).Sensitivity.Numerator,"-28840");
        Equal("quantity leaves root fixed",Solve(Plan(physical,mw:"40")).Price.Numerator,"42");
        var certificates=Plan(f.WithCertificate());var withCertificates=Solve(certificates);
        Equal("unchanged certificate economics contribute to selected contract root",withCertificates.Price.Numerator,"44");
        var certificateProfile=PpaPricingProfile.Create(certificates,withCertificates);
        Equal("certificate quantity keeps its own units",certificateProfile.Periods.First(p=>p.Slot.Role==PpaRole.CertificateDelivery).Quantity.Specification.Code,"synthetic_rec");
        var reversed=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[..physical.Memberships.Select(m=>m with{Coefficient=m.Coefficient*D("-1")})],[],false,PpaInstrument.Implementation);
        Equal("opposite direction",Solve(Plan(reversed)).Sensitivity.Numerator,"57680");
        var fixedSide=new PpaFixedSide(Guid.NewGuid(),f.Terms(f.Profile),new FlatPrice(new(D("40"),f.EnergyPrice)));
        var floating=new PpaFloatingSide(Guid.NewGuid(),f.Terms(f.Profile),new(f.C("synthetic_hub"),D("1"),new(default,f.EnergyPrice)));
        var virtualPpa=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[new(slot,fixedSide,D("-1")),new(new(PpaRole.IndexedPayment),floating,D("1"))],[],false,PpaInstrument.Implementation);
        Equal("virtual floating root",Solve(Plan(virtualPpa)).Price.Numerator,"30");
        var negative=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[..virtualPpa.Memberships.Select(m=>m with{Coefficient=D("1")})],[],false,PpaInstrument.Implementation);
        Equal("negative root allowed",Solve(Plan(negative)).Price.Numerator,"-30");
        var cash=(PpaFixedSide)physical.Memberships[1].Side;
        var triple=new PpaFixedSide(cash.Id,cash.Terms,new FlatPrice(new(D("40"),new PriceUnit(f.EnergyPrice.Currency,f.EnergyPrice.Quantity,D("3")))));
        var fractional=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[physical.Memberships[0],physical.Memberships[1] with{Side=triple}],[],false,PpaInstrument.Implementation);
        var frac=Solve(Plan(fractional),f.Market(f.From.AddDays(-1),energy:"62"));
        Equal("exact nonterminating root numerator",frac.Price.Numerator,"62");Equal("exact nonterminating root denominator",frac.Price.Denominator,"3");
        Equal("explicit quote",frac.Quote,"20.67");Equal("quote residual numerator",frac.QuoteResidual.Numerator,"-2884");Equal("quote residual denominator",frac.QuoteResidual.Denominator,"5");
        if(frac.ExactPriceEnterable || frac.QuoteIsExact)throw new Exception("Nonterminating root labelled exact quote");
        var laterPhysical=new PowerPhysicalSide(physical.Memberships[0].Side.Id,f.Terms(f.Profile,20),f.Asset.Location);
        var discounted=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[physical.Memberships[0] with{Side=laterPhysical},physical.Memberships[1]],[],true,PpaInstrument.Implementation);
        Equal("distinct payment dates and exact discounting",Solve(Plan(discounted),discount:new DatedFactors()).Price.Numerator,"189");
        Equal("discounted root denominator",Solve(Plan(discounted),discount:new DatedFactors()).Price.Denominator,"4");
        var mutable=new ChangingFactors();var frozen=Solve(Plan(discounted),discount:mutable);
        Equal("discount resolution frozen across strike basis",mutable.Calls.ToString(),"1");Equal("frozen discount root",frozen.Price.Numerator,"42");
        foreach(var (mark,sign,quote) in new[]{("1.005","-1","1"),("1.015","-1","1.02"),("1.005","1","-1"),("1.015","1","-1.02")})
        {
            var signed=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[physical.Memberships[0],physical.Memberships[1] with{Coefficient=D(sign)}],[],false,PpaInstrument.Implementation);
            var tie=Solve(Plan(signed),f.Market(f.From.AddDays(-1),energy:mark));
            Equal("signed half-even "+mark+" "+sign,tie.Quote,quote);
            if(!tie.ExactPriceEnterable || tie.QuoteIsExact)throw new Exception("Source precision differs from quote precision");
        }
        var hugeFloating=new PpaFloatingSide(floating.Id,floating.Terms,new(f.C("synthetic_hub"),D("100000000000000000000000"),new(default,f.EnergyPrice)));
        var huge=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[virtualPpa.Memberships[0],virtualPpa.Memberships[1] with{Side=hugeFloating}],[],false,PpaInstrument.Implementation);
        Refuse("root beyond source-price magnitude",()=>Solve(Plan(huge)),"ppa_break_even_range");
        Equal("new market context changes root",Solve(plan,f.Market(f.From.AddDays(-1),energy:"43")).Price.Numerator,"43");
        Equal("earlier prepared context remains unchanged",Solve(plan).Price.Numerator,"42");
        Refuse("zero strike sensitivity",()=>Solve(Plan(f.Physical(volume:true)),forecast:f.Forecast("0")),"ppa_break_even_zero_sensitivity");
        Refuse("unknown measure",()=>Plan(physical,"net_cash"),"ppa_break_even_measure");
        Refuse("missing side",()=>PpaBreakEvenPlan.Create(physical,new(PpaRole.StrikePayment,1),coverage,D("80"),"CompletePv",2,"half_even_v1"),"ppa_break_even_side");
        var stepped=new PpaFixedSide(cash.Id,cash.Terms,new SteppedPrice([new(f.Month,new(D("40"),f.EnergyPrice))]));
        var unsupported=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[physical.Memberships[0],physical.Memberships[1] with{Side=stepped}],[],false,PpaInstrument.Implementation);
        Refuse("selected non-flat price",()=>Plan(unsupported),"ppa_break_even_side");
        Refuse("unknown rounding",()=>PpaBreakEvenPlan.Create(physical,slot,coverage,D("80"),"CompletePv",2,"implicit"),"ppa_break_even_quote");
        Refuse("unprepared variants",()=>plan.Solve(f.Prepare(market,[physical]).Session(market.Build),noDiscount),"ppa_context");
        var missing=market with{Curves=market.Curves.Where(p=>p.Key!=f.C("synthetic_energy")).ToDictionary(p=>p.Key,p=>p.Value)};
        Refuse("missing market coverage",()=>Solve(plan,missing));
        var overdue=f.Market(f.To.AddDays(20),actuals:_=>"42");
        Refuse("complete PV unavailable for overdue rows",()=>Solve(Plan(physical,"CompletePv"),overdue),"ppa_break_even_unavailable");
        Refuse("excluded overdue strike has zero sensitivity",()=>Solve(plan,overdue),"ppa_break_even_zero_sensitivity");
    }
    private sealed class ChangingFactors:ISideDiscounting
    {
        internal int Calls;
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> keys,MarketSnapshot snapshot)
        {Calls++;return [..keys.Select(k=>new SidePaymentFactor(k,ExactDecimal.Parse(Calls==1?"0.9":"0.1"),snapshot,DiscountFactorSource.Explicit))];}
    }
    private sealed class DatedFactors:ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> keys,MarketSnapshot snapshot)=>
            [..keys.Select(k=>new SidePaymentFactor(k,ExactDecimal.Parse(k.PaymentAt.Day==20?"0.9":"0.8"),snapshot,DiscountFactorSource.Explicit))];
    }
}
