using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

public sealed record PpaBreakEvenResult(ExactUnitPrice Price,ExactMoney Sensitivity,ExactMoney ZeroValue,ExactMoney OneValue,
    ExactMoney InputValue,string Quote,bool QuoteIsExact,bool ExactPriceEnterable,ExactMoney QuoteResidual,
    ValueMeasureSet ZeroMeasures,ValueMeasureSet OneMeasures,ImmutableArray<SidePaymentFactor> Factors);

// Only this plan creates the two immutable basis instruments. No caller-supplied
// value pair can claim a common context or a supported linear price relationship.
public sealed class PpaBreakEvenPlan
{
    public const string Implementation="ppa_flat_strike_break_even_v1";
    public PpaInstrument Original { get; }
    public SideSlot SelectedSide { get; }
    public DeliveryCoverage Coverage { get; }
    public ExactDecimal Mw { get; }
    public string Measure { get; }
    public int QuoteDecimalPlaces { get; }
    public string QuoteRounding { get; }
    public ImmutableArray<PpaInstrument> Basis { get; }
    private readonly FlatPrice price;
    private static MarketDataException Error(string code,string message)=>new("ppa_break_even_"+code,message);
    private PpaBreakEvenPlan(PpaInstrument original,SideSlot side,DeliveryCoverage coverage,ExactDecimal mw,string measure,int places,string rounding,FlatPrice selected)
    {
        Original=original;SelectedSide=side;Coverage=coverage;Mw=mw;Measure=measure;QuoteDecimalPlaces=places;QuoteRounding=rounding;price=selected;
        PpaInstrument At(ExactDecimal value)=>new(Guid.NewGuid(),original.Definitions,original.Reference,
            [..original.Memberships.Select(m=>m.Slot==side?m with{Side=new PpaFixedSide(m.Side.Id,m.Side.Terms,new FlatPrice(new(value,selected.Unit)))}:m)],
            original.NormalizationAssets,original.Discounting,PpaInstrument.Implementation);
        Basis=[At(default),At(ExactDecimal.Parse("1"))];
    }
    public static PpaBreakEvenPlan Create(PpaInstrument instrument,SideSlot side,DeliveryCoverage coverage,ExactDecimal mw,string measure,int quoteDecimalPlaces,string quoteRounding)
    {
        if(measure is not("ValueExcludingOverdue" or "CompletePv"))throw Error("measure","Select ValueExcludingOverdue or CompletePv explicitly.");
        if(quoteDecimalPlaces is <0 or >18 || quoteRounding!="half_even_v1")throw Error("quote","Select 0–18 quote decimals and explicit half_even_v1 rounding.");
        if(instrument is null || instrument.Capability is not("fixed_physical" or "virtual_fixed_for_floating") || side is null || side.Role!=PpaRole.StrikePayment ||
            instrument.Memberships.SingleOrDefault(m=>m.Slot==side)?.Side is not PpaFixedSide {Price:FlatPrice flat})
            throw Error("side","Select exactly one flat fixed-strike payment membership of a fixed physical or virtual fixed-for-floating PPA.");
        if(coverage is null || coverage.From>=coverage.To || mw.CompareTo(default)<=0)throw Error("scope","Supply explicit nonempty delivery coverage and positive contract MW.");
        return new(instrument,side,coverage,mw,measure,quoteDecimalPlaces,quoteRounding,flat);
    }
    public PpaBreakEvenResult Solve(IntervalValuationSession session,ISideDiscounting discounting,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        var zero=session.Value(Basis[0],Coverage,discounting);
        var factors=zero.Sides.SelectMany(s=>((IntervalSideValuation)s.Value).Receipt.Factors).DistinctBy(f=>f.Payment).ToImmutableArray();
        var frozen=new FrozenFactors(session.Scenario.Snapshot,factors);
        ct.ThrowIfCancellationRequested();var one=session.Value(Basis[1],Coverage,frozen);ct.ThrowIfCancellationRequested();
        var zm=ValueMeasures.From(zero).Scale(Mw);var om=ValueMeasures.From(one).Scale(Mw);
        ExactMoney Selected(ValueMeasureSet values)=>Measure=="ValueExcludingOverdue"?values.ValueExcludingOverdue:
            values.CompletePv??throw Error("unavailable","CompletePv is unavailable because the selected coverage contains overdue cash; no root is reported.");
        var z=Selected(zm);var o=Selected(om);var sensitivity=o.Subtract(z);
        var sn=BigInteger.Parse(sensitivity.Numerator);var sd=BigInteger.Parse(sensitivity.Denominator);
        if(sn.IsZero)throw Error("zero_sensitivity","The selected strike has zero sensitivity in this valuation measure; no unique break-even price exists.");
        var n=-BigInteger.Parse(z.Numerator)*sd;var d=BigInteger.Parse(z.Denominator)*sn;
        if(d.Sign<0){n=-n;d=-d;}
        if(BigInteger.Abs(n)>=d*BigInteger.Pow(10,24))throw Error("range","The model root exceeds the supported source-price magnitude.");
        var root=ExactUnitPrice.FromFraction(n,d,price.Unit);
        var rounded=IntegerRounding.RoundQuotient(n*BigInteger.Pow(10,QuoteDecimalPlaces),d,MidpointRounding.ToEven);
        // Source terms accept at most18 decimals and magnitude strictly below10^24.
        var quote=ExactDecimal.FromScaled(rounded*BigInteger.Pow(10,18-QuoteDecimalPlaces));
        var enterable=(n*ExactDecimal.WorkingScale)%d==0;
        var quoteExact=n*ExactDecimal.WorkingScale==quote.ScaledValue*d;
        return new(root,sensitivity,z,o,z.Add(sensitivity.Scale(price.Price.Value)),quote.ToString(),quoteExact,enterable,
            z.Add(sensitivity.Scale(quote)),zm,om,factors);
    }
    private sealed class FrozenFactors(MarketSnapshot snapshot,ImmutableArray<SidePaymentFactor> factors):ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> keys,MarketSnapshot selected)
        {
            if(!selected.HasSameInputs(snapshot))throw Error("context","Strike basis evaluations must retain one exact market snapshot.");
            return [..keys.Select(k=>factors.SingleOrDefault(f=>f.Payment==k)??throw Error("context","The strike perturbation changed a required discount key."))];
        }
    }
}
