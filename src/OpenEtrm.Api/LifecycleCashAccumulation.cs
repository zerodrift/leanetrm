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
                var retained=await Read(connection,database,opening,request,choice,until,root,ct);
                curves.Add(choice.Currency,retained.Curve);evidence.Add(retained.Evidence);
                var prior=inputs.Where(p=>p.Data.DefinitionRevision==retained.Input.Data.DefinitionRevision).ToArray();
                if(prior.Length==0)inputs.Add(retained.Input);
                else if(prior.Length!=1||!prior[0].Data.Snapshot(prior[0].Frame).WithKnowledge(source.Knowledge!.ContextId,source.Knowledge.KnownAt).HasSameInputs(retained.Evidence.Source))
                    throw Error("One retained rate definition requires one complete original input selection.");
            }
        }
        var selection=new LifecycleCashAccumulation(LifecycleCashAccumulation.Version,curves.ToImmutable());
        return new(selection,JsonSerializer.SerializeToElement(new{implementation=selection.Implementation,
            currencies=currencies.OrderBy(c=>c.Revision.CatalogId).ThenBy(c=>c.Revision.RevisionId).ThenBy(c=>c.Code,StringComparer.Ordinal),
            selections=evidence.ToImmutable()},RiskJobs.Json),inputs.ToImmutable());
    }
    private sealed record RetainedChoice(IProjectionCurrencyCurve Curve,PortfolioCashAccumulationEvidence Evidence,(PreviewData Data,InputFrame Frame) Input);
    private static async Task<RetainedChoice> Read(NpgsqlConnection connection,PreviewDatabase database,PortfolioValuation.Prepared opening,
        PortfolioEconomicProjectionRequest request,PortfolioCashAccumulationSelection choice,DateTimeOffset until,JsonElement root,CancellationToken ct)
    {
        var scope=opening.OpeningScope??throw Error("Retain the original opening input frame and complete source matrix.");
        var source=scope.Snapshot;
        if(choice.SnapshotId!=scope.Frame.Id||choice.Curve.Revision.CatalogId!=database.Manifest.CatalogId)
            throw Error("The selected funding curve must retain the original native snapshot and market catalogue.");
        if(choice.Curve.Revision!=scope.Data.DefinitionRevision&&scope.Frame.SourceInputs?.ContainsKey(choice.Curve.Revision.RevisionId)!=true)
            throw Error("An independent funding source requires its explicit part in the original complete input matrix.");
        var admission=new Admission(database.Manifest.CatalogId,root,choice,until);database.RegisterCommitAdmission(connection,admission);
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
        await admission.RequireCommit(connection,ct);
        return new(curve,new(choice,snapshot,definition),input);
    }
    internal static async Task<JsonElement> Choices(NpgsqlConnection connection,PreviewDatabase database,PortfolioValuation.Prepared opening,
        PortfolioEconomicProjectionRequest request,ImmutableArray<ReferenceKey> currencies,CancellationToken ct)
    {
        request.ValidateCashFundingRead();
        if(currencies.IsDefaultOrEmpty||currencies.Distinct().Count()!=currencies.Length)
            throw Error("Retain the complete distinct currencies of every prepared economic component, including zero-cash paths.");
        var scope=opening.OpeningScope??throw Error("Retain the original opening input frame and complete source matrix.");
        var until=await TradingContext.AdmitSource(database,connection,opening.Context,scope.Snapshot.ValuationAt,ct);
        var exactFrame=scope.Frame with{Source=scope.Frame.Source??CatalogueSource.From(scope.Data)};
        var root=JsonSerializer.SerializeToElement(RetainedFrames.Record(exactFrame),RiskJobs.Json);
        var revisions=(scope.Frame.SourceInputs?.Keys??Enumerable.Empty<Guid>()).Append(scope.Data.DefinitionRevision.RevisionId).Distinct().Order().ToArray();
        var rows=await PreviewDatabase.Rows(connection,"SELECT row_to_json(t)::text FROM(SELECT d.*,c.definition_id,c.definition_version,c.name currency_curve_name,cc.name currency_name "+
            "FROM trading.currency_discount_defaults d JOIN market_data.rate_curves c ON(c.catalog_id,c.revision_id,c.code)=(d.catalog_id,d.definition_revision_id,d.curve_code) "+
            "JOIN ref_data.currencies cc ON(cc.catalog_id,cc.revision_id,cc.code)=(d.reference_catalog_id,d.reference_revision_id,d.currency_code) "+
            "CROSS JOIN LATERAL(SELECT coalesce($5::jsonb->'sourceInputs'->d.definition_revision_id::text,CASE WHEN d.definition_revision_id=($5::jsonb#>>'{source,definitionRevisionId}')::uuid THEN $5::jsonb END) part)captured "+
            "WHERE d.catalog_id=$1 AND d.definition_revision_id=ANY($2::uuid[]) AND d.effective_at<=$3 AND security.trading_visible(d.catalog_id,d.publication_order,d.effective_at) "+
            "AND EXISTS(SELECT 1 FROM jsonb_array_elements($4::jsonb)currency WHERE currency->>'code'=d.currency_code "+
            "AND(currency#>>'{revision,catalogId}')::uuid=d.reference_catalog_id AND(currency#>>'{revision,revisionId}')::uuid=d.reference_revision_id) "+
            "AND EXISTS(SELECT 1 FROM market_data.rate_curve_tenors t WHERE(t.catalog_id,t.revision_id,t.curve_code)=(c.catalog_id,c.revision_id,c.code)) "+
            "AND NOT EXISTS(SELECT 1 FROM market_data.rate_curve_tenors t WHERE(t.catalog_id,t.revision_id,t.curve_code)=(c.catalog_id,c.revision_id,c.code) "+
            "AND(SELECT count(*) FROM market_data.observations o WHERE(o.catalog_id,o.definition_revision_id,o.series_id,o.source_code,o.role,o.publication_state)= "+
            "(c.catalog_id,c.revision_id,t.series_id,c.source_code,'parameter','final') AND o.observed_at<=$3 AND captured.part->'observationIds' @> jsonb_build_array(o.id) "+
            "AND NOT(captured.part->'frozenObservationIds' @> jsonb_build_array(o.id)) AND security.market_fact_visible(o.catalog_id,o.definition_revision_id,o.id))<>1) "+
            "ORDER BY d.definition_revision_id,d.reference_catalog_id,d.reference_revision_id,d.currency_code,d.publication_order,d.id LIMIT 257)t",
            [database.Manifest.CatalogId,revisions,scope.Frame.At,JsonSerializer.Serialize(currencies,RiskJobs.Json),root.GetRawText()],ct);
        if(rows.Length>256)throw new MarketDataException("projection_cash_capacity","The complete funding choice set exceeds this consumer's 256-choice limit.");
        var choices=new List<object>();
        foreach(var row in rows)
        {
            var selection=new PortfolioCashAccumulationSelection(new(new(row.Id("reference_catalog_id"),row.Id("reference_revision_id")),row.Text("currency_code")),scope.Frame.Id,
                new(new(database.Manifest.CatalogId,row.Id("definition_revision_id")),row.Text("curve_code")),row.Id("id"),row.Id("definition_id"),int.Parse(row.Text("definition_version"),System.Globalization.CultureInfo.InvariantCulture));
            _=await Read(connection,database,opening,request,selection,until,root,ct);
            choices.Add(new{selection,label=row.Text("currency_curve_name"),currencyLabel=row.Text("currency_name"),
                sourceLabel=$"{row.Text("currency_curve_name")} · definition {selection.DefinitionVersion}",effectiveAt=row.Instant("effective_at")});
        }
        await using(var require=ScheduleStore.Command(connection,"SELECT security.require_admitted_business_source($1,$2,$3,$4,$5)",
            database.Manifest.CatalogId,opening.Context.ContextId!.Value,scope.Frame.At,scope.Frame.Time.Date,until))
            await require.ExecuteNonQueryAsync(ct);
        return JsonSerializer.SerializeToElement(new{context=opening.Context,snapshotId=scope.Frame.Id,currencies,choices},RiskJobs.Json);
    }
    private static MarketDataException Error(string message)=>new("projection_cash_policy",message);
}
