using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Settlement;

public sealed record PaymentOccurrenceIdentity(Guid Catalog,Guid Trade,Guid Lineage,DateOnly Month);
public sealed record PaymentContributionKey(Guid Catalog,Guid Trade,Guid OriginalBinding,Guid OriginalInstrument,SideSlot OriginalMembership,DateTimeOffset Start,DateTimeOffset End);
public sealed record GrossPaymentBinding(Guid BindingId,int OriginRevision,PpaInstrument Instrument,SideSlot Slot,Guid SideId,DeliveryCoverage Coverage,ExactDecimal Mw);
public sealed record PlannedPaymentContribution(PaymentContributionKey Key,GrossPaymentBinding Binding,PowerInterval Interval,ExactUnitPrice? Strike,DateTimeOffset DueAt);
public sealed record FinalMeterCell(VolumeSourceKey Source,Guid HeadId,string State,QuantityCell Reading,ReferenceKey Energy,QuantityStage Stage,string ClockFingerprint,DateTimeOffset EffectiveAt,DateTimeOffset KnownAt);
public sealed record GrossPaymentComponent(PlannedPaymentContribution Origin,NormalizedQuantity Normalization,ExactQuantity Quantity,ExactMoney Cash)
{public IntervalPriceResolution? Index { get; init; } public ExactUnitPrice? PaymentPrice { get; init; }}
public sealed record GrossPaymentPreview(MonthlyGrossPlan Plan,ImmutableArray<GrossPaymentComponent> Components,ExactQuantity Quantity,ExactMoney ExactCash,ExactMoney RoundedCash,ExactMoney RoundingDifference);

// Only the validated planner constructs a complete immutable manifest. Consumers
// cannot supply a subset of contributions and label it a whole occurrence.
public sealed class MonthlyGrossPlan
{
    public PaymentOccurrenceIdentity Identity { get; }
    public GrossPaymentBinding Original { get; }
    public ImmutableArray<PlannedPaymentContribution> Contributions { get; }
    public DateTimeOffset DueAt { get; }
    public DateTimeOffset ValuationAt { get; }
    internal MonthlyGrossPlan(PaymentOccurrenceIdentity identity,GrossPaymentBinding original,ImmutableArray<PlannedPaymentContribution> contributions,DateTimeOffset due,DateTimeOffset valuationAt)
    {Identity=identity;Original=original;Contributions=contributions;DueAt=due;ValuationAt=valuationAt;}
}

public static class OccurrenceCash
{
    public const string Rounding="cash_2dp_half_even_per_occurrence_v1";
    public static ExactMoney Round(ExactMoney amount)
    {
        var cents=IntegerRounding.RoundQuotient(BigInteger.Parse(amount.Numerator)*100,BigInteger.Parse(amount.Denominator),MidpointRounding.ToEven);
        return ExactMoney.FromFraction(cents,100,amount.Currency);
    }
}

public static class MonthlyGrossPreview
{
    public const string Implementation="ppa_monthly_gross_fixed_v1";
    public const string IndexedImplementation="ppa_monthly_gross_indexed_v1";
    public const int MaximumBindings=32,MaximumAssets=4,MaximumCells=3100,MaximumMeterCells=12400;
    private static MarketDataException Invalid(string code,string message)=>new("settlement_"+code,message);
    public static PpaMembership Member(GrossPaymentBinding b)
    {
        var member=b.Instrument.Memberships.SingleOrDefault(m=>m.Slot==b.Slot && m.Side.Id==b.SideId);
        if(member is null || !(b.Instrument.Capability=="fixed_physical" && b.Slot.Role==PpaRole.StrikePayment && member.Side is FixedSide ||
            b.Instrument.Capability=="indexed_physical" && b.Slot.Role==PpaRole.IndexedPayment && member.Side is FloatingSide) || b.Mw.CompareTo(default)<=0 || b.BindingId==Guid.Empty || b.OriginRevision<1)
            throw Invalid("incompatible","An exact original fixed or indexed physical payment membership and positive original MW are required.");
        return member;
    }
    public static VolumeQuantity Volume(GrossPaymentBinding b)
    {
        var side=Member(b).Side;
        if(side.Terms.Quantity is VolumeQuantity direct)return direct;
        if(side.Terms.Quantity is SlotQuantity {Slots.Length:1} slots &&
            b.Instrument.Memberships.SingleOrDefault(m=>m.Slot==slots.Slots[0]) is {Side:PowerPhysicalSide {Terms.Quantity:VolumeQuantity volume}})
            return volume;
        throw Invalid("incompatible","This gross payment requires one metered whole-asset energy quantity, directly or through one physical delivery membership.");
    }
    public static MonthlyGrossPlan Plan(PaymentOccurrenceIdentity identity,GrossPaymentBinding original,ImmutableArray<GrossPaymentBinding> bindings,
        bool terminated,DateTimeOffset? terminationAt,DateTimeOffset valuationAt,PowerClock clock)
    {
        if(identity.Catalog!=original.Instrument.Definitions.CatalogId || identity.Catalog==Guid.Empty || identity.Trade==Guid.Empty || identity.Lineage==Guid.Empty || identity.Month.Day!=1 || identity.Month.Year is <2000 or >2099 || original.OriginRevision!=1)
            throw Invalid("occurrence","Select an exact original lineage and one supported operating month.");
        if(bindings.IsDefaultOrEmpty || bindings.Length>MaximumBindings || bindings.Select(b=>b.BindingId).Distinct().Count()!=bindings.Length)
            throw Invalid("manifest","Supply the complete distinct binding partition of one certified head.");
        var baseline=Member(original);var delivery=original.Instrument.Delivery;var month=identity.Month;
        var grid=PowerHours.Compile(month,month.AddMonths(1),clock,TimeOfUse.All(delivery.IntervalMinutes),[],delivery.IntervalMinutes).ToImmutableArray();
        if(grid.Length>MaximumCells || valuationAt<grid[^1].End)throw Invalid("month_not_complete","The entire operating month must end before preview, including after early termination.");
        var ordered=bindings.OrderBy(b=>b.Coverage.From).ToArray();
        var end=terminated?terminationAt??throw Invalid("manifest","Termination requires its exact retained boundary."):original.Coverage.To;
        if(!terminated && terminationAt is not null || end<original.Coverage.From || end>original.Coverage.To ||
            ordered[0].Coverage.From!=original.Coverage.From || ordered[^1].Coverage.To!=end ||
            ordered.Zip(ordered.Skip(1)).Any(p=>p.First.Coverage.To!=p.Second.Coverage.From))
            throw Invalid("manifest","Retained coverage must be contiguous, with only an explicitly terminated suffix removed.");
        var generated=baseline.Side.Terms.DateRule.Generate(month,month.AddMonths(1));
        var due=new DateTimeOffset(generated.Date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
        var rows=ImmutableArray.CreateBuilder<PlannedPaymentContribution>();
        foreach(var b in ordered)
        {
            var member=Member(b);var d=b.Instrument.Delivery;var volume=Volume(b);
            if(b.Instrument.Definitions!=original.Instrument.Definitions || b.Instrument.Reference!=original.Instrument.Reference ||
                member.Slot.Role!=baseline.Slot.Role || d.Clock!=delivery.Clock || d.IntervalMinutes!=delivery.IntervalMinutes || member.Side.Terms.Currency!=baseline.Side.Terms.Currency || member.Coefficient!=baseline.Coefficient ||
                b.Coverage.From>=b.Coverage.To || b.Instrument.NormalizationAssets.Length is <1 or >MaximumAssets || volume.Sources.Length!=b.Instrument.NormalizationAssets.Length ||
                member.Side.Terms.DateRule.Generate(month,month.AddMonths(1))!=generated)
                throw Invalid("incompatible","Every retained segment must keep the exact currency, clock, native cadence, direction, monthly due terms and compatible revision pins.");
            var cells=grid.Where(i=>i.Start>=b.Coverage.From && i.End<=b.Coverage.To).ToArray();
            var from=b.Coverage.From>grid[0].Start?b.Coverage.From:grid[0].Start;var to=b.Coverage.To<grid[^1].End?b.Coverage.To:grid[^1].End;
            if(from<to && (cells.Length==0 || cells[0].Start!=from || cells[^1].End!=to))throw Invalid("manifest","Every contribution must use whole native cells.");
            foreach(var interval in cells)
            {
                var key=new PaymentContributionKey(identity.Catalog,identity.Trade,original.BindingId,original.Instrument.Id,original.Slot,interval.Start,interval.End);
                rows.Add(new(key,b,interval,(member.Side as FixedSide)?.Price.At(interval.OperatingDate),due));
            }
        }
        if(rows.Count==0 || rows.Count>MaximumCells || rows.Select(r=>r.Key).Distinct().Count()!=rows.Count)throw Invalid("manifest","The selected month needs nonempty exclusive complete coverage.");
        return new(identity,original,rows.ToImmutable(),due,valuationAt);
    }
    public static GrossPaymentPreview Calculate(MonthlyGrossPlan plan,ImmutableArray<GenerationAsset> assets,ImmutableArray<VolumeSourceDefinition> sources,
        QuantitySpecification energy,ImmutableArray<FinalMeterCell> meters,VolumeContext context,CancellationToken cancellationToken=default,GrossIndexedPrices? prices=null)
    {
        if(prices is not null && (!ReferenceEquals(prices.Plan,plan) || prices.KnownAt!=context.KnownAt))throw Invalid("context","Indexed prices must belong to this exact plan and knowledge cutoff.");
        if(context.ValuationAt!=plan.ValuationAt || context.Definitions!=plan.Original.Instrument.Definitions || context.Reference!=plan.Original.Instrument.Reference)throw Invalid("context","The plan and evidence must share exact valuation and revision coordinates.");
        if(meters.IsDefault || meters.Length>MaximumMeterCells || meters.Select(m=>(m.Source,m.Reading.Start,m.Reading.End)).Distinct().Count()!=meters.Length)
            throw Invalid("meter_manifest","Final meter evidence must be distinct and bounded.");
        var required=plan.Contributions.SelectMany(r=>Volume(r.Binding).Sources.Select(s=>(s,r.Interval.Start,r.Interval.End))).ToHashSet();
        if(!required.SetEquals(meters.Select(m=>(m.Source,m.Reading.Start,m.Reading.End))))throw Invalid("meter_manifest","Every required asset-cell needs exactly one final head; missing and extra evidence refuse.");
        var components=ImmutableArray.CreateBuilder<GrossPaymentComponent>();
        foreach(var group in plan.Contributions.GroupBy(r=>r.Binding.BindingId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows=group.ToArray();var binding=rows[0].Binding;var volume=Volume(binding);var intervals=rows.Select(r=>r.Interval).ToArray();
            var selectedAssets=binding.Instrument.NormalizationAssets.Select(key=>assets.SingleOrDefault(a=>a.Key==key)??throw Invalid("incompatible","A declared capacity basis is missing.")).ToImmutableArray();
            var all=ImmutableArray.CreateBuilder<AssetVolume>();
            foreach(var key in volume.Sources)
            {
                var source=sources.SingleOrDefault(s=>s.Key==key)??throw Invalid("incompatible","A declared source is missing.");
                var asset=selectedAssets.SingleOrDefault(a=>a.Key==source.Asset)??throw Invalid("incompatible","Each source must map to a declared asset.");
                var starts=intervals.Select(i=>i.Start).ToHashSet();var selected=meters.Where(m=>m.Source==key && starts.Contains(m.Reading.Start)).ToArray();
                if(selected.Any(m=>m.HeadId==Guid.Empty || m.State!="final" || m.Reading.ContextId==Guid.Empty || m.Energy!=energy.Key || m.Stage!=source.MeteredStage ||
                    m.ClockFingerprint!=asset.OperatingClock.Fingerprint || m.EffectiveAt<m.Reading.End || m.EffectiveAt>context.ValuationAt ||
                    m.KnownAt>context.KnownAt || m.Reading.KnownAt>m.KnownAt))throw Invalid("finality","Only compatible explicitly final heads visible at both temporal coordinates qualify.");
                var forecast=new QuantitySeries(source.ForecastSeries,"capacity_factor_forecast",source.Clock,source.IntervalMinutes,null,[]);
                var actual=new QuantitySeries(source.MeteredSeries,"metered_energy",source.Clock,source.IntervalMinutes,energy.Key,[..selected.Select(m=>m.Reading)]);
                all.AddRange(AssetVolumes.Compute(source,asset,volume.Stage,intervals,forecast,actual,context,energy));
            }
            foreach(var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized=CapacityNormalization.PerUnit(selectedAssets,[..all.Where(v=>v.Interval==row.Interval.Delivery)],row.Interval.Delivery,volume.Stage,context);
                var index=row.Strike is null?(prices??throw Invalid("price_missing","The explicit index requires complete eligible native prices.")).At(row):null;
                var paymentPrice=row.Strike ?? index!.Price!.ScaleFraction(((FloatingSide)Member(binding).Side).Price.Multiplier.ScaledValue,ExactDecimal.WorkingScale).Add(ExactUnitPrice.FromPrice(((FloatingSide)Member(binding).Side).Price.Adder));
                var quantity=normalized.PerUnit.Scale(binding.Mw);var cash=quantity.Times(paymentPrice).Scale(Member(binding).Coefficient);
                components.Add(new(row,normalized,quantity,cash){Index=index,PaymentPrice=paymentPrice});
            }
        }
        var result=components.ToImmutable();var total=result.Select(c=>c.Cash).Aggregate((a,b)=>a.Add(b));var rounded=OccurrenceCash.Round(total);
        return new(plan,result,result.Select(c=>c.Quantity).Aggregate((a,b)=>a.Add(b)),total,rounded,rounded.Subtract(total));
    }
}
