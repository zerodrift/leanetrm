using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Trading;

public sealed record PpaPricingPeriod(SideSlot Slot,DateOnly Month,string Kind,string QuantityState,string EconomicClass,
    string Coverage,DateTimeOffset ValueAt,ExactDecimal Coefficient,ExactQuantity Quantity,ExactMoney Amount,
    ExactMoney? PresentValue,ExactUnitPrice? EffectivePrice)
{
    public string SideRole=>Slot.Role switch {PpaRole.EnergyDelivery=>"Physical energy",PpaRole.StrikePayment=>"Fixed strike",PpaRole.IndexedPayment=>"Floating payment",_=>Slot.Role.ToString()};
}
public sealed record PpaStrikeValue(string Label,ExactUnitPrice Strike,ExactMoney Value);
public sealed record PpaPricingProfile(ImmutableArray<PpaPricingPeriod> Periods,ImmutableArray<PpaStrikeValue> ValueCurve)
{
    // Reconstruct the entered flat-strike schedule using the same exact affine
    // relationship as the solver. Existing engine period groups retain each side,
    // operating month, quantity state, economic class, coverage and payment date.
    public static PpaPricingProfile Create(PpaBreakEvenPlan plan,PpaBreakEvenResult result)
    {
        var selected=(FixedSide)plan.Original.Memberships.Single(m=>m.Slot==plan.SelectedSide).Side;
        var entered=((FlatPrice)selected.Price).Price;
        var one=result.OneMeasures.Periods.ToDictionary(p=>p.Key);
        var periods=result.ZeroMeasures.Periods.Select(z=>{
            var o=one[z.Key];var member=plan.Original.Memberships.Single(m=>m.Slot==z.Key.Slot);
            var amount=z.Amount.Add(o.Amount.Subtract(z.Amount).Scale(entered.Value));
            var pv=z.PresentValue is {} value?value.Add(o.PresentValue!.Subtract(value).Scale(entered.Value)):null;
            ExactUnitPrice? effective=null;
            // Gross value / unsigned physical quantity / membership coefficient.
            // This is an energy-weighted effective price, including declared adders
            // and multipliers, in currency per exact quantity unit (multiplier 1).
            var d=BigInteger.Parse(amount.Denominator)*BigInteger.Parse(z.Quantity.Numerator)*member.Coefficient.ScaledValue;
            if(!d.IsZero){var n=BigInteger.Parse(amount.Numerator)*BigInteger.Parse(z.Quantity.Denominator)*ExactDecimal.WorkingScale;
                if(d.Sign<0){n=-n;d=-d;}effective=ExactUnitPrice.FromFraction(n,d,new(amount.Currency,z.Quantity.Specification,ExactDecimal.Parse("1")));}
            return new PpaPricingPeriod(z.Key.Slot,z.Key.Month,z.Key.Kind.ToString(),z.Key.QuantityState.ToString(),z.Key.Class.ToString(),z.Key.Coverage.ToString(),z.Key.ValueAt,
                member.Coefficient,z.Quantity,amount,pv,effective);
        }).OrderBy(p=>p.Month).ThenBy(p=>p.Slot.Role).ThenBy(p=>p.Slot.Ordinal).ThenBy(p=>p.ValueAt).ToImmutableArray();
        var n=BigInteger.Parse(result.Price.Numerator);var d=BigInteger.Parse(result.Price.Denominator);
        var zero=ExactMoney.FromMoney(new(default,result.Sensitivity.Currency));
        var points=new[]{new PpaStrikeValue("Break-even - 1 quote unit",ExactUnitPrice.FromFraction(n-d,d,result.Price.Unit),result.Sensitivity.Scale(ExactDecimal.Parse("-1"))),
            new PpaStrikeValue("Model break-even",result.Price,zero),
            new PpaStrikeValue("Break-even + 1 quote unit",ExactUnitPrice.FromFraction(n+d,d,result.Price.Unit),result.Sensitivity),
            new PpaStrikeValue("Entered strike",ExactUnitPrice.FromFraction(entered.Value.ScaledValue,ExactDecimal.WorkingScale,entered.Unit),result.InputValue)};
        Array.Sort(points,(a,b)=>(BigInteger.Parse(a.Strike.Numerator)*BigInteger.Parse(b.Strike.Denominator)).CompareTo(BigInteger.Parse(b.Strike.Numerator)*BigInteger.Parse(a.Strike.Denominator)));
        return new(periods,[..points]);
    }
}
