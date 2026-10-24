using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenEtrm.MarketData.Risk;

public sealed record QuoteBump(InputKey Input,ReferenceKey Quote,PriceUnit Unit,ExactDecimal Size,string ConventionId)
{
    public const string Convention="absolute_quote_input_symmetric_v1";
}
public sealed record QuoteShock(QuoteBump Bump,bool Up)
{
    public ExactDecimal AppliedSize=>Up?Bump.Size:default(ExactDecimal)-Bump.Size;
}
// The signed amount is added to the immediate parent's effective quote. Rebuild
// affected curves; this convention never edits observed facts or built outputs.
public sealed record QuoteShift(InputKey Input,ReferenceKey Quote,PriceUnit Unit,ExactDecimal Size,string ConventionId)
{
    public const string Convention="absolute_quote_input_parent_additive_rebuild_v1";
}

public sealed class QuoteScenario
{
    public const string IdentityVersion="pinned_quote_scenario_v1";
    public const string FixingIdentityVersion="pinned_quote_fixing_scenario_v1";
    public string Id{get;}
    public string? ParentId{get;}
    public int Depth{get;}
    public bool IsBase=>ParentId is null;
    public ImmutableArray<QuoteShift> Shifts{get;}
    public ImmutableArray<QuoteShift> EffectiveShifts{get;}
    private readonly ImmutableDictionary<InputKey,QuoteShift> effective;
    private QuoteScenario(string id,string? parentId,int depth,ImmutableArray<QuoteShift> shifts,ImmutableDictionary<InputKey,QuoteShift> effective)
    {
        Id=id;ParentId=parentId;Depth=depth;Shifts=shifts;this.effective=effective;
        EffectiveShifts=Sort(effective.Values).ToImmutableArray();
    }
    internal ExactDecimal Offset(InputKey input)=>effective.TryGetValue(input,out var shift)?shift.Size:default;
    internal static QuoteScenario Base(MarketSnapshot snapshot)
    {
        var observations=snapshot.Selections.OrderBy(s=>s.Input.Series.Revision.CatalogId).ThenBy(s=>s.Input.Series.Revision.RevisionId)
            .ThenBy(s=>s.Input.Series.Id).ThenBy(s=>s.Input.Use).Select(s=>new
            {
                input=s.Input,observation=s.Observation.Key,series=s.Observation.Series,source=s.Observation.Source,
                role=s.Observation.Role,state=s.Observation.State,at=s.Observation.ObservedAt,value=s.Observation.Value.ToString()
            });
        var quoteInputs=new{version=IdentityVersion,snapshot=snapshot.Id,definitions=snapshot.Definitions,
            valuationAt=snapshot.ValuationAt,cutoff=snapshot.MarketCutoff,selection=snapshot.SelectionPolicy,
            numericalPolicy=CurveModel.NumericalPolicy,observations};
        // Keep projection-only receipts stable; actual-aware contexts have their
        // own identity contract, including immutable correction and price inputs.
        var id=snapshot.FixingContextId is null&&snapshot.FixingKnowledgeCutoff is null&&snapshot.IndexFixings.IsEmpty?
            Digest(quoteInputs):Digest(new{version=FixingIdentityVersion,quoteInputs,snapshot.FixingContextId,snapshot.FixingKnowledgeCutoff,
                fixings=snapshot.IndexFixings.OrderBy(f=>f.Key.CatalogId).ThenBy(f=>f.Key.Id).Select(f=>new{
                    f.Key,f.Index,f.Source,f.From,f.To,f.PublishedAt,f.KnownAt,f.State,f.Revision,f.Corrects,
                    price=f.Price.Value.ToString(),unit=new{f.Price.Unit.Currency,f.Price.Unit.Quantity,multiplier=f.Price.Unit.Multiplier.ToString()},
                    quote=new{f.Quote.Key,f.Quote.Kind,f.Quote.Currency,f.Quote.Quantity,multiplier=f.Quote.Multiplier.ToString(),
                        tick=f.Quote.TradingTick.ToString(),clearingIncrement=f.Quote.ClearingIncrement.ToString(),minimum=f.Quote.Minimum.ToString(),maximum=f.Quote.Maximum.ToString()}
                })});
        return new(id,null,0,[],ImmutableDictionary<InputKey,QuoteShift>.Empty);
    }
    internal QuoteScenario Child(ImmutableArray<QuoteShift> shifts)
    {
        var ordered=Sort(shifts).ToImmutableArray();var next=effective.ToBuilder();
        foreach(var shift in ordered)
        {
            var total=Offset(shift.Input)+shift.Size;
            if(total==default)next.Remove(shift.Input);else next[shift.Input]=shift with{Size=total};
        }
        var id=Digest(new{version=IdentityVersion,parent=Id,shifts=ordered.Select(s=>new
        {
            input=s.Input,quote=s.Quote,currency=s.Unit.Currency,quantity=s.Unit.Quantity,
            multiplier=s.Unit.Multiplier.ToString(),size=s.Size.ToString(),convention=s.ConventionId
        })});
        return new(id,Id,Depth+1,ordered,next.ToImmutable());
    }
    private static IOrderedEnumerable<QuoteShift> Sort(IEnumerable<QuoteShift> shifts)=>shifts
        .OrderBy(s=>s.Input.Series.Revision.CatalogId).ThenBy(s=>s.Input.Series.Revision.RevisionId)
        .ThenBy(s=>s.Input.Series.Id).ThenBy(s=>s.Input.Use);
    private static string Digest<T>(T value)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
public sealed record QuoteFactor(InputKey Input,ReferenceKey Quote,PriceUnit Unit,ContractKey Contract,
    ObservationKey Observation,ReferenceKey Source,ObservationRole Role,ExactDecimal BaseValue,ImmutableArray<CurveKey> Curves,ExactDecimal TradingTick);
public sealed record FactorScenarios(QuoteBump Bump,CurveBuild Up,CurveBuild Down);
public sealed record CurveScenarioSet
{
    public CurveBuild Base{get;}
    public ImmutableArray<FactorScenarios> Factors{get;}
    internal CurveScenarioSet(CurveBuild @base,ImmutableArray<FactorScenarios> factors){Base=@base;Factors=factors;}
}
