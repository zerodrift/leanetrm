using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Settlement;
using OpenEtrm.MarketData.Trading;

internal static class SettlementPreviewChecks
{
    internal static void Run()
    {
        IndexedSettlementChecks.Run();
        var f=new PpaAcceptanceFixture();var instrument=f.Physical(volume:true);var originalId=Guid.NewGuid();
        var original=new GrossPaymentBinding(originalId,1,instrument,new(PpaRole.StrikePayment),instrument.Memberships[1].Side.Id,new(f.From,f.To),f.UnitMw.Amount*PpaAcceptanceFixture.D("80"));
        var identity=new PaymentOccurrenceIdentity(f.Definitions.CatalogId,Guid.NewGuid(),Guid.NewGuid(),f.Month);
        var plan=MonthlyGrossPreview.Plan(identity,original,[original],false,null,f.To,f.Clock);
        var context=new VolumeContext(f.Definitions,f.Reference,f.To,f.To,PendingActualsPolicy.RequireActuals);
        var cells=f.Hours.Select(h=>new FinalMeterCell(f.Source.Key,Guid.NewGuid(),"final",new(h.Start,h.End,PpaAcceptanceFixture.D("100"),1,f.To,f.QuantityKnowledge),f.Energy.Key,QuantityStage.Generated,f.Clock.Fingerprint,f.To,f.To)).ToImmutableArray();
        GrossPaymentPreview Value(ImmutableArray<FinalMeterCell> c)=>MonthlyGrossPreview.Calculate(plan,[f.Asset],[f.Source],f.Energy,c,context);
        void Equal(string name,string actual,string expected){if(actual!=expected)throw new Exception($"{name}: {actual} != {expected}");Console.WriteLine("PASS settlement arithmetic: "+name);}
        void Reject(string name,Action action){try{action();}catch(MarketDataException){Console.WriteLine("PASS settlement refusal: "+name);return;}throw new Exception(name);}
        var indexedPhysical=new FloatingSide(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)]),normalization:instrument.Normalization),new(f.C("synthetic_hub"),PpaAcceptanceFixture.D("2"),new(PpaAcceptanceFixture.D("3"),f.EnergyPrice),null));
        var indexedInstrument=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[instrument.Memberships[0],new(new(PpaRole.IndexedPayment),indexedPhysical,PpaAcceptanceFixture.D("-1"))],false,PpaInstrument.Implementation);
        var indexedBinding=original with{Instrument=indexedInstrument,Slot=new(PpaRole.IndexedPayment),SideId=indexedPhysical.Id};
        var indexedPlan=MonthlyGrossPreview.Plan(identity,indexedBinding,[indexedBinding],false,null,f.To,f.Clock);
        Equal("indexed physical complete manifest",indexedPlan.Contributions.Length.ToString(),"721");
        var result=Value(cells);Equal("721-hour month",result.Components.Length.ToString(),"721");Equal("gross fixed strike",result.ExactCash.Numerator,"-1153600");
        var one=cells.SetItem(0,cells[0] with{Reading=cells[0].Reading with{Value=PpaAcceptanceFixture.D("102")}});
        Equal("single-interval restatement",Value(one).RoundedCash.Numerator,"-1153632");
        var all=cells.Select(c=>c with{Reading=c.Reading with{Value=PpaAcceptanceFixture.D("102")}}).ToImmutableArray();
        Equal("whole-month restatement",Value(all).RoundedCash.Numerator,"-1176672");
        Equal("explicit zero retains capacity",Value(cells.Select(c=>c with{Reading=c.Reading with{Value=default}}).ToImmutableArray()).RoundedCash.Numerator,"0");
        Reject("missing is not zero",()=>Value(cells.RemoveAt(0)));
        Reject("withdrawn finality never falls back",()=>Value(cells.SetItem(0,cells[0] with{State="withdrawn"})));
        Reject("duplicate finality",()=>Value(cells.Add(cells[0])));
        Reject("later-known finality",()=>Value(cells.SetItem(0,cells[0] with{KnownAt=f.To.AddTicks(10)})));
        Reject("month completion after termination",()=>MonthlyGrossPreview.Plan(identity,original,[original with{Coverage=new(f.From,f.From.AddHours(2))}],true,f.From.AddHours(2),f.To.AddTicks(-1),f.Clock));
        Reject("interior omitted coverage",()=>MonthlyGrossPreview.Plan(identity,original,[original with{Coverage=new(f.From,f.From.AddHours(2))},original with{BindingId=Guid.NewGuid(),Coverage=new(f.From.AddHours(3),f.To)}],false,null,f.To,f.Clock));
        var direct=new FixedSide(Guid.NewGuid(),f.Terms(f.Volume),new FlatPrice(new(PpaAcceptanceFixture.D("40"),f.EnergyPrice)));
        var floating=new FloatingSide(Guid.NewGuid(),f.Terms(f.Volume),new(f.C("synthetic_hub"),PpaAcceptanceFixture.D("1"),new(default,f.EnergyPrice),null));
        var virtualInstrument=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[new(new(PpaRole.StrikePayment),direct,PpaAcceptanceFixture.D("-1")),new(new(PpaRole.IndexedPayment),floating,PpaAcceptanceFixture.D("1"))],false,PpaInstrument.Implementation);
        var virtualBinding=original with{Instrument=virtualInstrument,SideId=direct.Id};
        Reject("virtual is outside accepted fixed physical contract",()=>MonthlyGrossPreview.Plan(identity,virtualBinding,[virtualBinding],false,null,f.To,f.Clock));
        var suffixInstrument=f.Physical("50",volume:true);
        var prefix=original with{Coverage=new(f.From,f.From.AddHours(2))};
        var suffix=new GrossPaymentBinding(Guid.NewGuid(),2,suffixInstrument,new(PpaRole.StrikePayment),suffixInstrument.Memberships[1].Side.Id,new(f.From.AddHours(2),f.To),PpaAcceptanceFixture.D("60"));
        var amended=MonthlyGrossPreview.Plan(identity,original,[prefix,suffix],false,null,f.To,f.Clock);
        var amendedValue=MonthlyGrossPreview.Calculate(amended,[f.Asset],[f.Source],f.Energy,cells,context);
        Equal("amended original MW and strike",amendedValue.ExactCash.Numerator,"-1081700");
        var terminated=MonthlyGrossPreview.Plan(identity,original,[prefix,suffix with{Coverage=new(suffix.Coverage.From,f.From.AddHours(4))}],true,f.From.AddHours(4),f.To,f.Clock);
        Equal("terminated suffix removed only",MonthlyGrossPreview.Calculate(terminated,[f.Asset],[f.Source],f.Energy,[..cells.Take(4)],context).ExactCash.Numerator,"-6200");
        var tinyInstrument=f.Physical("0.01",volume:true);
        var tiny=new GrossPaymentBinding(Guid.NewGuid(),1,tinyInstrument,new(PpaRole.StrikePayment),tinyInstrument.Memberships[1].Side.Id,new(f.From,f.From.AddHours(2)),PpaAcceptanceFixture.D("1"));
        var tinyPlan=MonthlyGrossPreview.Plan(identity,tiny,[tiny],false,null,f.To,f.Clock);
        var tinyValue=MonthlyGrossPreview.Calculate(tinyPlan,[f.Asset],[f.Source],f.Energy,[..cells.Take(2)],context);
        Equal("two half-cent cells round once",tinyValue.RoundedCash.Numerator,"-1");Equal("round-once cents denominator",tinyValue.RoundedCash.Denominator,"100");
        var switchAt=f.Clock.StartOfDay(new(2031,11,15)).ToUniversalTime();
        var changedAsset=f.Asset with{Versions=[new(f.From,switchAt,PpaAcceptanceFixture.D("200"),null),new(switchAt,f.To,PpaAcceptanceFixture.D("100"),null)]};
        Equal("capacity versions in force",MonthlyGrossPreview.Calculate(plan,[changedAsset],[f.Source],f.Energy,cells,context).ExactCash.Numerator,"-1768000");
        var q=new PpaAcceptanceFixture(15);var qi=q.Physical(volume:true);
        var qb=new GrossPaymentBinding(Guid.NewGuid(),1,qi,new(PpaRole.StrikePayment),qi.Memberships[1].Side.Id,new(q.From,q.To),PpaAcceptanceFixture.D("80"));
        var qp=MonthlyGrossPreview.Plan(new(q.Definitions.CatalogId,Guid.NewGuid(),Guid.NewGuid(),q.Month),qb,[qb],false,null,q.To,q.Clock);
        var qm=q.Hours.Select(h=>new FinalMeterCell(q.Source.Key,Guid.NewGuid(),"final",new(h.Start,h.End,PpaAcceptanceFixture.D("25"),1,q.To,q.QuantityKnowledge),q.Energy.Key,QuantityStage.Generated,q.Clock.Fingerprint,q.To,q.To)).ToImmutableArray();
        var qv=MonthlyGrossPreview.Calculate(qp,[q.Asset],[q.Source],q.Energy,qm,new(q.Definitions,q.Reference,q.To,q.To,PendingActualsPolicy.RequireActuals));
        Equal("native quarter hours in repeated hour",qv.Components.Length.ToString(),"2884");Equal("MWh is not multiplied by quarter-hour duration",qv.ExactCash.Numerator,"-1153600");
        var otherAsset=f.Asset with{Key=new(f.Definitions,"synthetic_other"),Versions=[new(f.From,f.To,PpaAcceptanceFixture.D("100"),null)]};
        var otherSource=f.Source with{Key=new(f.Definitions,"synthetic_other_source"),Asset=otherAsset.Key,ForecastSeries=Guid.NewGuid(),MeteredSeries=Guid.NewGuid()};
        var normalization=f.Normalization([f.Asset.Key,otherAsset.Key]);
        var physical=new PowerPhysicalSide(Guid.NewGuid(),f.Terms(new VolumeQuantity([f.Source.Key,otherSource.Key],QuantityStage.Generated,f.Energy.Key),normalization:normalization),f.Asset.Location);
        var cashSide=new FixedSide(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)]),normalization:normalization),new FlatPrice(new(PpaAcceptanceFixture.D("40"),f.EnergyPrice)));
        var multi=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[new(new(PpaRole.EnergyDelivery),physical,PpaAcceptanceFixture.D("1")),new(new(PpaRole.StrikePayment),cashSide,PpaAcceptanceFixture.D("-1"))],false,PpaInstrument.Implementation);
        var multiBinding=original with{Instrument=multi,SideId=cashSide.Id};var multiPlan=MonthlyGrossPreview.Plan(identity,multiBinding,[multiBinding],false,null,f.To,f.Clock);
        var otherMeters=cells.Select(c=>c with{Source=otherSource.Key,HeadId=Guid.NewGuid(),Reading=c.Reading with{Value=default}}).ToImmutableArray();
        var multiple=MonthlyGrossPreview.Calculate(multiPlan,[f.Asset,otherAsset],[f.Source,otherSource],f.Energy,cells.AddRange(otherMeters),context);
        Equal("sum energy over full capacity denominator",multiple.ExactCash.Numerator,"-2307200");Equal("exact rational cash retained",multiple.ExactCash.Denominator,"3");
        Equal("one final rounding across assets",multiple.RoundedCash.Numerator,"-76906667");Equal("rounding difference is explicit rational",multiple.RoundingDifference.Numerator,"-1");Equal("rounding difference denominator",multiple.RoundingDifference.Denominator,"300");
        Reject("missing asset cannot shrink capacity",()=>MonthlyGrossPreview.Calculate(multiPlan,[f.Asset,otherAsset],[f.Source,otherSource],f.Energy,cells,context));
        Reject("plan cannot cross valuation context",()=>MonthlyGrossPreview.Calculate(plan,[f.Asset],[f.Source],f.Energy,cells,context with{ValuationAt=f.To.AddHours(1)}));
        // Spring civil grid has 743 elapsed hours in March 2031.
        var march=new DateOnly(2031,3,1);var mf=f.Clock.StartOfDay(march).ToUniversalTime();var mt=f.Clock.StartOfDay(march.AddMonths(1)).ToUniversalTime();
        var md=f.Delivery with{From=march,To=march.AddMonths(1)};
        var schedule=new IntervalScheduleParameters(md,f.Clock,IntervalScheduleCadence.NativeIntervals,new(BlockMembership.All,null,[]));
        var mp=new PowerPhysicalSide(Guid.NewGuid(),f.Terms(f.Volume) with{Delivery=md,Schedule=schedule},f.Asset.Location);
        var mc=new FixedSide(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)]),normalization:instrument.Normalization) with{Delivery=md,Schedule=schedule,
            InputPorts=[new(new(PpaRole.EnergyDelivery),f.Energy.Key,schedule)]},new FlatPrice(new(PpaAcceptanceFixture.D("40"),f.EnergyPrice)));
        var mi=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[new(new(PpaRole.EnergyDelivery),mp,PpaAcceptanceFixture.D("1")),new(new(PpaRole.StrikePayment),mc,PpaAcceptanceFixture.D("-1"))],false,PpaInstrument.Implementation);
        var mb=original with{Instrument=mi,SideId=mc.Id,Coverage=new(mf,mt)};var spring=MonthlyGrossPreview.Plan(identity with{Month=march},mb,[mb],false,null,mt,f.Clock);
        Equal("spring DST native coverage",spring.Contributions.Length.ToString(),"743");
        foreach(var (input,expected) in new[]{("1.005","1"),("1.015","51"),("-1.005","-1"),("-1.015","-51")})
            Equal("half-even "+input,OccurrenceCash.Round(ExactMoney.FromMoney(new(PpaAcceptanceFixture.D(input),f.R("synthetic_usd")))).Numerator,expected);
    }
}
