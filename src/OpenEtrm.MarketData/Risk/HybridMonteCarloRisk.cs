using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenEtrm.MarketData.Risk;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record HybridMonteCarloPolicy(string Convention,string ValidationPolicy,int ValidationPaths,string ValidationSeed,string FallbackScope)
{
    public const string Version1="pca_delta_gamma_sampled_pv_validation_v1",IndexPanel="sha256_ranked_indices_independent_of_loss_v1",
        WholePortfolio="whole_portfolio_full_pv_v1";
}

/// <summary>A recorded sample of indices for observed validation, never a bound on unpriced losses.</summary>
public static class HybridMonteCarloRisk
{
    public static void Validate(HybridMonteCarloPolicy execution,MonteCarloRiskPolicy simulation,PcaHybridPolicy fitting)
    {
        MonteCarloRisk.ValidatePolicy(simulation);PcaHybridRisk.ValidatePolicy(fitting);
        if(simulation.Convention!=MonteCarloRiskPolicy.Hybrid||fitting.ValuationPolicy is not(PcaHybridPolicy.PanelQualification or PcaHybridPolicy.PanelFallbackQualification)||
            execution is null||execution.Convention!=HybridMonteCarloPolicy.Version1||execution.ValidationPolicy!=HybridMonteCarloPolicy.IndexPanel||
            execution.FallbackScope!=HybridMonteCarloPolicy.WholePortfolio||execution.ValidationPaths<2||execution.ValidationPaths>simulation.Paths||
            !ulong.TryParse(execution.ValidationSeed,NumberStyles.None,CultureInfo.InvariantCulture,out var seed)||seed.ToString(CultureInfo.InvariantCulture)!=execution.ValidationSeed)
            throw new MarketDataException("risk_hybrid_policy","Select the explicit hybrid valuation, complete fitting and seeded index-validation policy, 2–N validation paths, and whole-portfolio fallback scope. Fallback consent is separate.");
    }
    public static ImmutableArray<MonteCarloSample> SelectPanel(MonteCarloGeneration generation,HybridMonteCarloPolicy execution,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(execution is null||execution.Convention!=HybridMonteCarloPolicy.Version1||execution.ValidationPolicy!=HybridMonteCarloPolicy.IndexPanel||
            execution.FallbackScope!=HybridMonteCarloPolicy.WholePortfolio||execution.ValidationPaths<2||execution.ValidationPaths>generation.Policy.Paths||
            !ulong.TryParse(execution.ValidationSeed,NumberStyles.None,CultureInfo.InvariantCulture,out var seed)||seed.ToString(CultureInfo.InvariantCulture)!=execution.ValidationSeed)
            throw new MarketDataException("risk_hybrid_policy","Supply the complete explicit validation index-selection policy.");
        MonteCarloRisk.ValidatePolicy(generation.Policy);
        if(generation.Policy.Convention!=MonteCarloRiskPolicy.Hybrid||!generation.Available||generation.Samples.Length!=generation.Policy.Paths||generation.Samples.Where((s,i)=>s.Index!=i||s.Id!="mc_"+i.ToString("D5",CultureInfo.InvariantCulture)).Any())
            throw new MarketDataException("risk_monte_carlo_samples","Hybrid validation requires every original simulation index exactly once.");
        string Score(MonteCarloSample sample)
        {
            cancellation.ThrowIfCancellationRequested();
            Span<byte> input=stackalloc byte[40];input.Clear();Encoding.ASCII.GetBytes("OpenETRM hybrid panel v1",input);
            BinaryPrimitives.WriteUInt64LittleEndian(input[24..],seed);BinaryPrimitives.WriteInt32LittleEndian(input[32..],sample.Index);
            return Convert.ToHexStringLower(SHA256.HashData(input));
        }
        return [..generation.Samples.Select(s=>(Sample:s,Score:Score(s))).OrderBy(x=>x.Score,StringComparer.Ordinal).ThenBy(x=>x.Sample.Index)
            .Take(execution.ValidationPaths).Select(x=>x.Sample).OrderBy(s=>s.Index)];
    }
}
