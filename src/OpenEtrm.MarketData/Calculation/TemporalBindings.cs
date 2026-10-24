using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.Calculation;

// Coordinates and manifest identity supplied by an already-authorized resolver.
// This value neither certifies a knowledge cutoff nor grants access to its inputs.
// The manifest identifies the exact inputs, models and numerical policies.
public sealed record ResolvedCalculationContext
{
    public Guid ContextId{get;}
    public string ManifestId{get;}
    public DateTimeOffset EffectiveAt{get;}
    public DateTimeOffset KnownAt{get;}
    public DateTimeOffset ValuationAt{get;}
    public DateTimeOffset MarketAsOf{get;}
    public ResolvedCalculationContext(Guid contextId,string manifestId,DateTimeOffset effectiveAt,
        DateTimeOffset knownAt,DateTimeOffset valuationAt,DateTimeOffset marketAsOf)
    {
        // This prototype supports ordinary historical valuation only. Lifecycle
        // projection and hindsight require a separately specified contract.
        if(contextId==Guid.Empty||string.IsNullOrWhiteSpace(manifestId)||manifestId!=manifestId.Trim()||
            new[]{effectiveAt,knownAt,valuationAt,marketAsOf}.Any(at=>!TemporalCurveBindings.IsInstant(at))||
            effectiveAt!=valuationAt||marketAsOf>valuationAt)
            throw new MarketDataException("temporal_context","Supply exact context and manifest identities, UTC coordinates, equal effective/valuation instants and a market cutoff no later than valuation.");
        ContextId=contextId;ManifestId=manifestId;EffectiveAt=effectiveAt;KnownAt=knownAt;
        ValuationAt=valuationAt;MarketAsOf=marketAsOf;
    }
}
public readonly record struct LogicalCurveKey(Guid CatalogId,string Code);
// Publication knowledge is distinct from the dates on which a logical binding
// selects this definition. An exact retained reference never follows activation.
public sealed record CurvePublication(CurveKey Definition,DateTimeOffset KnownFrom);
public sealed record TemporalCurveBinding(LogicalCurveKey Logical,CurveKey Definition,
    DateTimeOffset EffectiveFrom,DateTimeOffset EffectiveTo,DateTimeOffset KnownFrom,DateTimeOffset KnownTo);
public interface ICurveBindingResolver
{
    CurveKey Resolve(LogicalCurveKey logical,ResolvedCalculationContext context);
    void RequireVisible(CurveKey exact,ResolvedCalculationContext context);
}
public sealed class TemporalCurveBindings:ICurveBindingResolver
{
    public static DateTimeOffset OpenEnd=>BitemporalRange.OpenEnd;
    private readonly Dictionary<LogicalCurveKey,TemporalCurveBinding[]> history;
    private readonly Dictionary<CurveKey,DateTimeOffset> published;

    public TemporalCurveBindings(IEnumerable<TemporalCurveBinding> bindings,IEnumerable<CurvePublication> publications)
    {
        if(bindings is null||publications is null)
            throw new MarketDataException("temporal_binding","Supply explicit retained bindings and publication metadata.");
        published=new();
        foreach(var publication in publications)
        {
            if(publication is null)throw new MarketDataException("temporal_definition","A publication must identify an exact definition.");
            RequireKey(publication.Definition);
            if(!IsInstant(publication.KnownFrom))
                throw new MarketDataException("temporal_interval","Publication knowledge must be an explicit UTC instant before the reserved end.");
            if(!published.TryAdd(publication.Definition,publication.KnownFrom))
                throw new MarketDataException("temporal_definition_ambiguous","Each exact definition must have one retained publication coordinate.");
        }
        var grouped=new Dictionary<LogicalCurveKey,List<TemporalCurveBinding>>();
        foreach(var binding in bindings)
        {
            if(binding is null)throw new MarketDataException("temporal_binding","A binding must identify its logical curve and exact definition.");
            RequireKey(binding.Logical);RequireKey(binding.Definition);
            if(binding.Logical.CatalogId!=binding.Definition.Revision.CatalogId)
                throw new MarketDataException("temporal_binding","A logical curve selects an exact version in its own catalog.");
            RequireInterval(binding.EffectiveFrom,binding.EffectiveTo);RequireInterval(binding.KnownFrom,binding.KnownTo);
            if(!published.TryGetValue(binding.Definition,out var available)||available>binding.KnownFrom)
                throw new MarketDataException("temporal_definition_unavailable","A binding cannot precede knowledge of its exact definition.");
            if(!grouped.TryGetValue(binding.Logical,out var versions))grouped.Add(binding.Logical,versions=[]);
            // Histories are small per logical identity. Admission checks pairs
            // within that history, never across unrelated graph nodes.
            if(versions.Any(previous=>Overlaps(previous.EffectiveFrom,previous.EffectiveTo,binding.EffectiveFrom,binding.EffectiveTo)&&
                Overlaps(previous.KnownFrom,previous.KnownTo,binding.KnownFrom,binding.KnownTo)))
                throw new MarketDataException("temporal_binding_ambiguous","A logical curve cannot select two definitions at the same effective and knowledge coordinates.");
            versions.Add(binding);
        }
        // Copy once; subsequent lookups inspect only the requested identity's
        // retained assertions. No caller collection or mutable cursor is kept.
        history=grouped.ToDictionary(entry=>entry.Key,entry=>entry.Value.ToArray());
    }

    public CurveKey Resolve(LogicalCurveKey logical,ResolvedCalculationContext context)
    {
        RequireContext(context);RequireKey(logical);
        if(history.TryGetValue(logical,out var versions))
            foreach(var binding in versions)
                if(binding.EffectiveFrom<=context.EffectiveAt&&context.EffectiveAt<binding.EffectiveTo&&
                    binding.KnownFrom<=context.KnownAt&&context.KnownAt<binding.KnownTo)
                {
                    RequireVisible(binding.Definition,context);
                    return binding.Definition;
                }
        throw new MarketDataException("temporal_binding_missing","No retained binding covers the requested effective and knowledge coordinates.");
    }

    public void RequireVisible(CurveKey exact,ResolvedCalculationContext context)
    {
        RequireContext(context);RequireKey(exact);
        if(!published.TryGetValue(exact,out var knownFrom)||knownFrom>context.KnownAt)
            throw new MarketDataException("temporal_definition_unavailable","The exact definition is unavailable under this knowledge cutoff.");
    }

    internal static bool IsInstant(DateTimeOffset at)=>at.Offset==TimeSpan.Zero&&at<OpenEnd;
    private static void RequireContext(ResolvedCalculationContext context)
    {
        if(context is null)throw new MarketDataException("temporal_context","Supply an explicit resolved calculation context.");
    }
    private static void RequireInterval(DateTimeOffset from,DateTimeOffset to)
    {
        if(!IsInstant(from)||to.Offset!=TimeSpan.Zero||to>OpenEnd||from>=to)
            throw new MarketDataException("temporal_interval","Supply ordered half-open UTC intervals ending no later than the reserved finite bound.");
    }
    private static bool Overlaps(DateTimeOffset aFrom,DateTimeOffset aTo,DateTimeOffset bFrom,DateTimeOffset bTo)=>
        aFrom<bTo&&bFrom<aTo;
    private static void RequireKey(LogicalCurveKey key)
    {
        if(key.CatalogId==Guid.Empty||string.IsNullOrWhiteSpace(key.Code)||key.Code!=key.Code.Trim())
            throw new MarketDataException("temporal_binding","A logical curve requires an exact catalog and explicit identity.");
    }
    private static void RequireKey(CurveKey key)
    {
        if(key.Revision.CatalogId==Guid.Empty||key.Revision.RevisionId==Guid.Empty||string.IsNullOrWhiteSpace(key.Code)||key.Code!=key.Code.Trim())
            throw new MarketDataException("temporal_definition","A definition requires an exact catalog, revision and curve identity.");
    }
}
