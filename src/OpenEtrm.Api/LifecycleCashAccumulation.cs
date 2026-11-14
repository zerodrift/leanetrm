using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

internal sealed record PortfolioCashAccumulationEvidence(PortfolioCashAccumulationSelection Selection,MarketSnapshot Source,RateCurveDefinition Definition);
internal sealed record PreparedPortfolioCashAccumulation(LifecycleCashAccumulation? Selection,JsonElement? Evidence,
    ImmutableArray<(PreviewData Data,InputFrame Frame)> Inputs)
{
    internal LifecycleCashAccumulation? For(ImmutableArray<ILifecycleProjection> components)=>Selection is null?null:
        Selection with{Curves=Selection.Curves.Where(p=>components.Any(c=>c.Currency==p.Key)).ToImmutableDictionary()};
}

// Rate-only inputs are retained as native market inputs, without manufacturing
// a holding, a price curve, a scenario factor or a residual discount choice.
internal static class LifecycleCashAccumulationInputs
{
    private sealed record Admission(Guid Catalog,JsonElement Frame,PortfolioCashAccumulationSelection Selection,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var command=ScheduleStore.Command(connection,"SELECT security.read_projection_cash_curve($1,$2::jsonb,$3::jsonb,$4)",
                Catalog,Frame.GetRawText(),JsonSerializer.Serialize(Selection,RiskJobs.Json),Until);
            await command.ExecuteNonQueryAsync(ct);
        }
    }
    internal static async Task<PreparedPortfolioCashAccumulation> Prepare(NpgsqlConnection connection,PreviewDatabase database,
        PortfolioValuation.Prepared opening,PortfolioEconomicProjectionRequest request,ImmutableArray<ReferenceKey> currencies,CancellationToken ct)
    {
        request.Validate();
        if(request.Implementation==PortfolioEconomicProjectionRequest.Version)return new(null,null,[]);
        if(currencies.IsDefaultOrEmpty||currencies.Distinct().Count()!=currencies.Length)
            throw Error("Retain the complete distinct currencies of every prepared economic component, including zero-cash paths.");
        var selected=request.CashAccumulationSelections!.Value;
        if(request.Policy.CashAccumulation==LifecycleProjectionPolicy.Accumulated&&!currencies.ToHashSet().SetEquals(selected.Select(s=>s.Currency)))
            throw Error("Select one exact accumulation source for every prepared component currency.");
        var curves=ImmutableDictionary.CreateBuilder<ReferenceKey,IProjectionCurrencyCurve>();
        var evidence=ImmutableArray.CreateBuilder<PortfolioCashAccumulationEvidence>();var inputs=ImmutableArray.CreateBuilder<(PreviewData Data,InputFrame Frame)>();
        if(!selected.IsEmpty)
        {
            var scope=opening.OpeningScope??throw Error("Retain the original opening input frame and complete source matrix.");
            var source=scope.Snapshot;
            var until=await TradingContext.AdmitSource(database,connection,opening.Context,source.ValuationAt,ct);
            var exactFrame=scope.Frame with{Source=scope.Frame.Source??CatalogueSource.From(scope.Data)};
            var root=JsonSerializer.SerializeToElement(RetainedFrames.Record(exactFrame),RiskJobs.Json);
            foreach(var choice in selected.OrderBy(s=>s.Currency.Revision.CatalogId).ThenBy(s=>s.Currency.Revision.RevisionId).ThenBy(s=>s.Currency.Code,StringComparer.Ordinal))
            {
                if(choice.SnapshotId!=scope.Frame.Id||choice.Curve.Revision.CatalogId!=database.Manifest.CatalogId)
                    throw Error("The selected funding curve must retain the original native snapshot and market catalogue.");
                if(choice.Curve.Revision!=scope.Data.DefinitionRevision&&scope.Frame.SourceInputs?.ContainsKey(choice.Curve.Revision.RevisionId)!=true)
                    throw Error("An independent funding source requires its explicit part in the original complete input matrix.");
                var admission=new Admission(database.Manifest.CatalogId,root,choice,until);
                database.RegisterCommitAdmission(connection,admission);
                var native=await ModelProviders.Read(connection,"SELECT security.read_projection_cash_curve($1,$2::jsonb,$3::jsonb,$4)::text",
                    [database.Manifest.CatalogId,root.GetRawText(),JsonSerializer.Serialize(choice,RiskJobs.Json),until],ct);
                var input=await MarketInputSelection.DefinitionInput(database,connection,scope.Data,scope.Frame,choice.Curve.Revision.RevisionId,ct);
                var snapshot=input.Data.Snapshot(input.Frame).WithKnowledge(source.Knowledge!.ContextId,source.Knowledge.KnownAt);
                if(snapshot.Id!=choice.SnapshotId||snapshot.Definitions!=choice.Curve.Revision||snapshot.ValuationAt!=source.ValuationAt||snapshot.MarketCutoff!=source.MarketCutoff)
                    throw Error("The selected rate source differs from the original exact opening inputs.");
                var definition=await RateCurveResolver.Resolve(connection,input.Data,input.Frame,choice.Curve.Code,ct);
                if(native.Id("id")!=choice.MappingId||definition.Key!=choice.Curve||definition.Currency!=choice.Currency||
                    definition.DefinitionId!=choice.DefinitionId||definition.DefinitionVersion!=choice.DefinitionVersion)
                    throw Error("The selected currency mapping and rate definition must match their exact native versions.");
                var curve=new LifecycleCurrencyDiscounting(new(choice.MappingId,choice.Currency,definition),snapshot,request.RateTimingPolicy,ClockChange.FrozenInputs);
                curves.Add(choice.Currency,curve);evidence.Add(new(choice,snapshot,definition));
                var prior=inputs.Where(p=>p.Data.DefinitionRevision==input.Data.DefinitionRevision).ToArray();
                if(prior.Length==0)inputs.Add(input);
                else if(prior.Length!=1||!prior[0].Data.Snapshot(prior[0].Frame).WithKnowledge(source.Knowledge.ContextId,source.Knowledge.KnownAt).HasSameInputs(snapshot))
                    throw Error("One retained rate definition requires one complete original input selection.");
                await admission.RequireCommit(connection,ct);
            }
        }
        var selection=new LifecycleCashAccumulation(LifecycleCashAccumulation.Version,curves.ToImmutable());
        return new(selection,JsonSerializer.SerializeToElement(new{implementation=selection.Implementation,
            currencies=currencies.OrderBy(c=>c.Revision.CatalogId).ThenBy(c=>c.Revision.RevisionId).ThenBy(c=>c.Code,StringComparer.Ordinal),
            selections=evidence.ToImmutable()},RiskJobs.Json),inputs.ToImmutable());
    }
    private static MarketDataException Error(string message)=>new("projection_cash_policy",message);
}
