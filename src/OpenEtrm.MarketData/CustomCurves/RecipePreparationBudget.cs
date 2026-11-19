using System.Text.Json;

namespace OpenEtrm.MarketData.CustomCurves;

public static partial class RecipeEngine
{
    // The envelope used by existing calculation contracts. Hosts may select a lower operating value for interactive previews.
    public static int MaximumSampleReferences { get; } = ReadMaximumSampleReferences();

    private static int ReadMaximumSampleReferences()
    {
        using var resource=typeof(RecipeEngine).Assembly.GetManifestResourceStream("OpenEtrm.RecipePreparation.v1")
            ??throw new InvalidOperationException("The versioned recipe preparation policy is missing.");
        using var document=JsonDocument.Parse(resource);
        var maximum=document.RootElement.GetProperty("maximumSampleReferences").GetInt32();
        return maximum>0?maximum:throw new InvalidOperationException("A positive recipe sample-reference ceiling is required.");
    }

    private sealed class PreparationBudget
    {
        private readonly int limit;
        private int remaining;
        internal PreparationBudget(int limit)
        {
            if(limit<=0||limit>MaximumSampleReferences)throw Missing("Select a supported sample-reference preparation limit.");
            this.limit=remaining=limit;
        }
        internal void Consume(long references)
        {
            if(references>remaining)throw Missing(FormattableString.Invariant($"The recipe exceeds its {limit:N0} cumulative sample-reference preparation limit."));
            remaining-=(int)references;
        }
    }
}
