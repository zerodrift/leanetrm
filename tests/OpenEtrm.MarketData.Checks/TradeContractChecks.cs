using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class TradeContractChecks
{
    public static int Run()
    {
        var count=0;
        void Check(string name,Action test){test();count++;Console.WriteLine("PASS trade contract: "+name);}
        void Reject(string code,Action test)
        {try{test();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
        var f=new Fixture();var request=new CurveRequest(f.C("future"),f.K(1));
        var currency=f.R("currency");var quantity=f.R("gas_quantity");var book=Guid.NewGuid();
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null);
        Check("a PV-only instrument serves repeated bindings and books without a product adapter",()=>
        {
            var calls=0;
            var instrument=new PvOnly(Guid.NewGuid(),currency,quantity,request,context=>
            {calls++;var p=context.Inputs.ExactPrice(request);return new(p.ExactTimes(new(ExactDecimal.Parse("1"),quantity)),quantity);});
            TradeInstrument Binding(string amount)=>new(Guid.NewGuid(),instrument,new(ExactDecimal.Parse(amount),quantity));
            var trade=new Trade(Guid.NewGuid(),book,[Binding("2"),Binding("-1")]);
            var second=new Trade(Guid.NewGuid(),Guid.NewGuid(),[Binding("3")]);
            var portfolio=ValuationPortfolio.FromTrades([trade,second],setup);
            var factor=f.Model().QuoteFactors(f.Snapshot()).Single(p=>p.Input.Series==f.S(1));
            var run=RiskEngine.Calculate(f.Model(),f.Snapshot(),portfolio,
                [new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("0.1"),QuoteBump.Convention)],RiskEngine.NumericalPolicy);
            if(calls!=3||run.Work.UniqueInstruments!=1||run.Work.PositionCount!=3||run.Work.UnitPricerCalls!=3)
                throw new Exception("One unit PV per scenario must serve all external quantities.");
            if(run.Factors.Single().Totals.Sum(t=>decimal.Parse(t.Base.Amount.ToString(),System.Globalization.CultureInfo.InvariantCulture))!=12m)
                throw new Exception("Independent oracle: (2-1+3)*3 = 12.");
            Reject("trade_binding",()=>new TradeInstrument(Guid.NewGuid(),instrument,new(ExactDecimal.Parse("1"),f.R("power_quantity"))));
            Reject("trade_definition",()=>new Trade(Guid.NewGuid(),book,[trade.Instruments[0],trade.Instruments[0]]));
            Reject("valuation_measure",()=>ValuationPortfolio.FromTrades([trade],setup with{Measure=ValuationMeasure.ExecutionPnl}));
            Reject("valuation_measure",()=>ValuationPortfolio.FromTrades([trade],null!));
        });
        Check("typed PV rejects another output quantity basis",()=>
        {
            var invalid=new PvOnly(Guid.NewGuid(),currency,quantity,request,_=>new(new Money(default,currency),f.R("power_quantity")));
            var portfolio=ValuationPortfolio.FromTrades([new(Guid.NewGuid(),book,[new(Guid.NewGuid(),invalid,new(ExactDecimal.Parse("1"),quantity))])],setup);
            Reject("risk_quantity",()=>RiskEngine.Calculate(f.Model(),f.Snapshot(),portfolio,[],RiskEngine.NumericalPolicy));
        });
        Check("stale typed results and undeclared size scaling are refused",()=>
        {
            var stale=new PvOnly(Guid.NewGuid(),currency,quantity,request,c=>new(c.Inputs.Price(request).ExactTimes(new(ExactDecimal.Parse("1"),quantity)),quantity)){ReuseResult=true};
            var portfolio=ValuationPortfolio.FromTrades([new(Guid.NewGuid(),book,[new(Guid.NewGuid(),stale,new(ExactDecimal.Parse("1"),quantity))])],setup);
            var factor=f.Model().QuoteFactors(f.Snapshot()).Single(p=>p.Input.Series==f.S(1));
            Reject("instrument_context",()=>RiskEngine.Calculate(f.Model(),f.Snapshot(),portfolio,
                [new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("0.1"),QuoteBump.Convention)],RiskEngine.NumericalPolicy));
            var unsupported=new PvOnly(Guid.NewGuid(),currency,quantity,request,_=>throw new Exception("Unsupported size must reject before pricing."),supportsScaling:false);
            Reject("risk_quantity_scaling",()=>new TradeInstrument(Guid.NewGuid(),unsupported,new(ExactDecimal.Parse("2"),quantity)));
        });
        Check("forwards share complete identities and floating sides while keeping strikes distinct",()=>
        {
            var source=new IndexFixingChecks.FixingFixture(monthly:true);var swap=source.Swap();
            var forward=new ForwardContract(Guid.NewGuid(),swap.Fixed,swap.Floating,false);
            var same=new ForwardContract(Guid.NewGuid(),swap.Fixed,swap.Floating,false);
            var identities=new InstrumentIdentityMap(Guid.NewGuid());
            if(!ReferenceEquals(identities.Intern(forward),identities.Intern(same)))throw new Exception("Equivalent forwards must intern.");
            var fixedSide=new FixedSide(Guid.NewGuid(),new(ExactDecimal.Parse("16"),source.Quote),swap.UnitQuantity,
                swap.Fixed.DeliveryFrom,swap.Fixed.DeliveryTo,swap.Fixed.PaymentAt,FixedSide.Implementation,source.Quote);
            var other=new ForwardContract(Guid.NewGuid(),fixedSide,swap.Floating,false);
            if(forward.EconomicIdentity==other.EconomicIdentity||!ReferenceEquals(forward.Floating,other.Floating))
                throw new Exception("Strike changes the complete payoff; floating work stays shareable.");
            var snapshot=source.Snapshot(source.Fixings[0].FixingAt.AddDays(-2));
            var provider=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
            Trade Hold(ForwardContract instrument)=>new(Guid.NewGuid(),book,[new(Guid.NewGuid(),instrument,new(ExactDecimal.Parse("2"),instrument.UnitQuantity.Specification))]);
            var portfolio=ValuationPortfolio.FromTrades([Hold(forward),Hold(other)],setup with{Discounting=provider});
            var result=RiskEngine.CalculateExact(RiskEngine.Prepare(source.Model,snapshot,[portfolio]),[],RiskEngine.ExactNumericalPolicy);
            // The projected index is 11; strikes are 15 and 16, each for quantity 2.
            if(result.Groups.Single().BaseValues.Single().Value.ToMoney().Amount.ToString()!="-18")throw new Exception("Forward unit quantities or direction applied incorrectly.");
            if(result.Work.UniqueSideCalculations!=3)throw new Exception("The common floating side must evaluate once alongside two fixed sides.");
            var opposite=new Trade(Guid.NewGuid(),book,[new(Guid.NewGuid(),forward,new(ExactDecimal.Parse("-2"),forward.UnitQuantity.Specification))]);
            var net=ValuationPortfolio.FromTrades([Hold(forward),opposite],setup with{Discounting=provider});
            var offset=RiskEngine.CalculateExact(RiskEngine.Prepare(source.Model,snapshot,[net]),[],RiskEngine.ExactNumericalPolicy);
            if(offset.Work.UniqueInstruments!=1||offset.Work.UnitPricerCalls!=1||offset.Groups.Single().BaseValues.Single().Value.Numerator!="0")
                throw new Exception("Opposing signed quantities must reuse one canonical forward and cancel exactly.");
            var delayed=new FixedSide(Guid.NewGuid(),swap.Fixed.Price,swap.UnitQuantity,swap.Fixed.DeliveryFrom,swap.Fixed.DeliveryTo,
                swap.Fixed.PaymentAt.AddDays(1),FixedSide.Implementation,source.Quote);
            Reject("forward_definition",()=>new ForwardContract(Guid.NewGuid(),delayed,swap.Floating,false));
            var a=new ForwardContract(Guid.NewGuid(),swap.Fixed,swap.Floating,true);
            var b=new ForwardContract(Guid.NewGuid(),fixedSide,swap.Floating,true);
            var isolated=ValuationPortfolio.FromTrades([Hold(a),Hold(b)],setup with{InstrumentDiscounting=
                new Dictionary<Guid,ISideDiscounting>{{a.Id,new ExplicitDiscounting(ExactDecimal.Parse("0.5"),DiscountFactorSource.Explicit)},
                    {b.Id,new ExplicitDiscounting(ExactDecimal.Parse("0.75"),DiscountFactorSource.Explicit)}}.ToImmutableDictionary()});
            var independent=RiskEngine.CalculateExact(RiskEngine.Prepare(source.Model,snapshot,[isolated]),[],RiskEngine.ExactNumericalPolicy);
            if(independent.Groups.Single().BaseValues.Single().Value.ToMoney().Amount.ToString()!="-11.5"||independent.Work.UniqueSideCalculations!=4)
                throw new Exception("Different discount contexts cannot share a side valuation.");
        });
        return count;
    }

    [EconomicDefinition("synthetic_pv_only_v1")]
    private sealed record PvOnly:Instrument
    {
        public override bool SupportsQuantityScaling=>supportsScaling;
        [IdentityField] public CurveRequest Request{get;}
        public override ReferenceKey Currency{get;}
        public override Quantity UnitQuantity{get;}
        private readonly Func<UnitValuationContext,UnitMoney> evaluate;
        private readonly bool supportsScaling;
        private UnitValuation? previous;
        public bool ReuseResult{get;init;}
        public PvOnly(Guid id,ReferenceKey currency,ReferenceKey quantity,CurveRequest request,Func<UnitValuationContext,UnitMoney> evaluate,bool supportsScaling=true):base(id)
        {Currency=currency;UnitQuantity=new(ExactDecimal.Parse("1"),quantity);Request=request;this.evaluate=evaluate;this.supportsScaling=supportsScaling;}
        public override UnitPricingContract Declare(UnitValuationContext context)=>new(Id,"synthetic_pv_only_v1",[Request],UnitQuantity.Specification,Currency);
        public override UnitValuation PV(UnitValuationContext context)
        {
            if(ReuseResult&&previous is not null)return previous;
            var value=evaluate(context);return previous=new(context,value.Money,value.Quantity);
        }
    }
}
