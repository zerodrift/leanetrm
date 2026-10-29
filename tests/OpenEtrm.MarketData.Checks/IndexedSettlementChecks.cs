using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Settlement;
using OpenEtrm.MarketData.Trading;

internal static class IndexedSettlementChecks
{
    internal static void Run()
    {
        static void Check(string name,bool value){if(!value)throw new Exception(name);Console.WriteLine("PASS indexed settlement: "+name);}
        static void Reject(string name,Action action){try{action();}catch(MarketDataException){Console.WriteLine("PASS indexed settlement refusal: "+name);return;}throw new Exception(name);}
        foreach(var minutes in new[]{60,15})
        {
            var f=new PpaAcceptanceFixture(minutes);var physical=f.Physical(volume:true).Memberships[0];
            var side=new FloatingSide(Guid.NewGuid(),f.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)]),normalization:physical.Side.Terms.Normalization),new(f.C("synthetic_hub"),PpaAcceptanceFixture.D("2"),new(PpaAcceptanceFixture.D("3"),f.EnergyPrice),null));
            var instrument=new PpaInstrument(Guid.NewGuid(),f.Definitions,f.Reference,[physical,new(new(PpaRole.IndexedPayment),side,PpaAcceptanceFixture.D("-1"))],false,PpaInstrument.Implementation);
            var binding=new GrossPaymentBinding(Guid.NewGuid(),1,instrument,new(PpaRole.IndexedPayment),side.Id,new(f.From,f.To),PpaAcceptanceFixture.D("80"));
            var identity=new PaymentOccurrenceIdentity(f.Definitions.CatalogId,Guid.NewGuid(),Guid.NewGuid(),f.Month);
            var plan=MonthlyGrossPreview.Plan(identity,binding,[binding],false,null,f.To,f.Clock);
            var meters=f.Hours.Select((h,i)=>new FinalMeterCell(f.Source.Key,Guid.NewGuid(),"final",new(h.Start,h.End,PpaAcceptanceFixture.D(minutes==60?(i%2==0?"100":"50"):(i%2==0?"25":"12.5")),1,f.To,f.QuantityKnowledge),f.Energy.Key,QuantityStage.Generated,f.Clock.Fingerprint,f.To,f.To)).ToImmutableArray();
            var context=new VolumeContext(f.Definitions,f.Reference,f.To,f.To,PendingActualsPolicy.RequireActuals);
            string Price(PowerInterval h)=>(((int)((h.Start-f.From).TotalMinutes/minutes))%7-3).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var market=f.Market(f.To,actuals:Price);
            var curves=new Dictionary<CurveKey,PreparedCurve>{{side.Price.Curve,market.Curves[side.Price.Curve]}};
            var prices=GrossIndexedPrices.Resolve(plan,curves,market.Build,f.To);
            var value=MonthlyGrossPreview.Calculate(plan,[f.Asset],[f.Source],f.Energy,meters,context,prices:prices);
            Check(minutes+" unequal native quantities, negative indexes, multiplier and adder",value.ExactCash.Numerator==(minutes==60?"-64920":"-64890") && value.ExactCash.Denominator=="1");
            Check(minutes+" collected source is retained without final claim",value.Components.All(c=>c.Index is {Publication:"collected",State:IntervalPriceState.Actual,Actual:not null}));
            Check(minutes+" repeated civil hours stay distinct",value.Components.Select(c=>c.Origin.Key).Distinct().Count()==f.Hours.Length && value.Components.Count(c=>c.Origin.Interval.Occurrence==2)==60/minutes);
            Reject("missing index evidence",()=>MonthlyGrossPreview.Calculate(plan,[f.Asset],[f.Source],f.Energy,meters,context));
            Reject("physical location curve cannot substitute",()=>GrossIndexedPrices.Resolve(plan,new Dictionary<CurveKey,PreparedCurve>{{f.C("synthetic_energy"),market.Curves[f.C("synthetic_energy")]}},market.Build,f.To));
            Reject("price context cannot cross cutoff",()=>GrossIndexedPrices.Resolve(plan,curves,market.Build,f.To.AddTicks(1)));
            Reject("same-looking different plan cannot reuse",()=>MonthlyGrossPreview.Calculate(MonthlyGrossPreview.Plan(identity,binding,[binding],false,null,f.To,f.Clock),[f.Asset],[f.Source],f.Energy,meters,context,prices:prices));
            Reject("missing meter even with zero index",()=>MonthlyGrossPreview.Calculate(plan,[f.Asset],[f.Source],f.Energy,meters.RemoveAt(3),context,prices:prices));
            Reject("missing due index never becomes zero",()=>f.Market(f.To.AddDays(8),actuals:h=>h.Start==f.From?null:Price(h)));
            var zeroMarket=f.Market(f.To,actuals:_=>"0");
            var zero=GrossIndexedPrices.Resolve(plan,new Dictionary<CurveKey,PreparedCurve>{{side.Price.Curve,zeroMarket.Curves[side.Price.Curve]}},zeroMarket.Build,f.To);
            Check("zero index retains the explicit adder",MonthlyGrossPreview.Calculate(plan,[f.Asset],[f.Source],f.Energy,meters,context,prices:zero).Components[0].PaymentPrice!.Numerator=="3");
            Reject("unresolved not-yet-due price is not an estimate fallback",()=>
            {
                var unresolved=f.Market(f.To,actuals:h=>h.End==f.To?null:Price(h));
                GrossIndexedPrices.Resolve(plan,new Dictionary<CurveKey,PreparedCurve>{{side.Price.Curve,unresolved.Curves[side.Price.Curve]}},unresolved.Build,f.To);
            });
        }
    }
}
