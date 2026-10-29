using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;
using System.Collections.Immutable;

namespace OpenEtrm.Api;

public static class CloseRiskAdmission
{
    internal static long FutureVolumeWork(ImmutableArray<PpaBinding> bindings,IntervalReferenceData reference,
        IReadOnlyDictionary<CurveKey,PreparedCurve> curves,
        IReadOnlyDictionary<(CurveKey Curve,Guid Binding),HashSet<DateOnly>> requested,
        IReadOnlyDictionary<(CurveKey Curve,Guid Binding),HashSet<PriceSettlementSelection?>> settlements,
        CurveBuild market,ImmutableArray<QuoteFactor> factors)
    {
        // Refusal-only lower bound. Unproven scopes contribute zero; admitted
        // requests still execute complete input validation and exact IntervalWork.
        var values=market.Values.ToDictionary(v=>v.Request);long work=0;
        foreach(var binding in bindings.DistinctBy(b=>(b.Instrument.Id,b.Coverage)))
        {
            var instrument=binding.Instrument;var coverage=binding.Coverage;
            if(instrument.Memberships.Any(m=>m.Side is CapacityDeliverySide ||
                m.Side.Terms.Quantity is not (VolumeQuantity or SlotQuantity)))continue;
            var clock=reference.Clocks[instrument.Delivery.Clock];
            var months=new List<DateOnly>();
            for(var month=instrument.Delivery.From;month<instrument.Delivery.To;month=month.AddMonths(1))
                if(clock.StartOfDay(month)<coverage.To && clock.StartOfDay(month.AddMonths(1))>coverage.From)months.Add(month);
            if(months.Any(month=>instrument.Memberships.Any(m=>new DateTimeOffset(m.Side.Terms.DateRule.Generate(month,month.AddMonths(1))
                .Date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc))<=market.Snapshot.ValuationAt)))continue;
            bool ConstantAssignments(PpaMembership member)
            {
                var attribute=member.Side switch {PowerPhysicalSide=>ValuedAttribute.Energy,CertificateDeliverySide=>ValuedAttribute.Certificate,_=>(ValuedAttribute?)null};
                if(attribute is null)return true;
                foreach(var location in instrument.NormalizationAssets.Select(a=>reference.Assets[a].Location).Distinct())
                {
                    var assignments=reference.Assignments.Where(a=>a.Location==location && a.Attribute==attribute && a.From<coverage.To && a.To>coverage.From).ToArray();
                    if(assignments.Length!=1 || assignments[0].From>coverage.From || assignments[0].To<coverage.To)return false;
                }
                return true;
            }
            if(!instrument.Memberships.All(ConstantAssignments))continue;
            var dependencies=requested.Where(r=>r.Key.Binding==binding.BindingId)
                .SelectMany(r=>r.Value.Where(months.Contains).SelectMany(m=>
                    settlements.TryGetValue(r.Key,out var selected)?selected.SelectMany(s=>curves[r.Key.Curve].Dependencies(m,s)):
                    throw new MarketDataException("price_settlement_selection","Every requested binding must retain its explicit period or interval price selections."))).Distinct();
            var lineage=dependencies.SelectMany(d=>values[d].Lineage).ToHashSet();
            var count=factors.Where(f=>lineage.Contains(f.Observation)).Select(f=>f.Input).Distinct().Count();
            var rows=PpaCompiler.Work([(instrument,coverage)],[],_=>clock);
            work=checked(work+rows*(1L+2L*count));
        }
        return work;
    }
    public const long MaximumRowScenarios=2_000_000_000;
    public static void RequireRowScenarios(long work,int factors)
    {
        if(work<0||work>MaximumRowScenarios||factors is <0 or >4096)
            throw new MarketDataException("risk_size","The declared operation exceeds two billion row-scenarios or 4,096 native factors.");
    }
}
