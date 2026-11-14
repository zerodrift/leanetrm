using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioCashAccumulationSelection([property:JsonRequired]ReferenceKey Currency,[property:JsonRequired]Guid SnapshotId,
    [property:JsonRequired]CurveKey Curve,[property:JsonRequired]Guid MappingId,[property:JsonRequired]Guid DefinitionId,[property:JsonRequired]int DefinitionVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioEconomicProjectionRequest([property:JsonRequired]string Implementation,[property:JsonRequired]string Session,
    [property:JsonRequired]int SessionVersion,[property:JsonRequired]string HorizonPolicy,[property:JsonRequired]string RateTimingPolicy,
    [property:JsonRequired]string EventMarketPolicy,[property:JsonRequired]LifecycleProjectionPolicy Policy,[property:JsonRequired]ImmutableArray<GasOwnedProjectionSelection> GasOwned)
{
    public const string Version="native_portfolio_economic_lifecycle_v1",Version2="native_portfolio_economic_lifecycle_v2",SourceKnownSchedule="source_known_effective_session_schedule_v1",
        SelectedContractMarkets="selected_contract_projection_curves_v1";
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<PortfolioCashAccumulationSelection>? CashAccumulationSelections{get;init;}
    public void Validate()
    {
        if(Implementation is not(Version or Version2)||string.IsNullOrWhiteSpace(Session)||SessionVersion<1||HorizonPolicy!=SourceKnownSchedule||
            RateTimingPolicy!=RateDiscountTiming.Version||EventMarketPolicy!=SelectedContractMarkets)
            throw new MarketDataException("projection_policy","Select the exact source-known session schedule, opening version, rate timing and economic projection profile.");
        LifecycleProjection.Validate(Policy);
        if(Implementation==Version?CashAccumulationSelections is not null:
            CashAccumulationSelections is not {} selections||selections.IsDefault||
            (Policy.CashAccumulation==LifecycleProjectionPolicy.Nominal?!selections.IsEmpty:selections.IsEmpty)||
            selections.Length>256||selections.Any(s=>s is null||s.Currency.Revision.CatalogId==Guid.Empty||s.Currency.Revision.RevisionId==Guid.Empty||
                string.IsNullOrWhiteSpace(s.Currency.Code)||s.SnapshotId==Guid.Empty||s.Curve.Revision.CatalogId==Guid.Empty||
                s.Curve.Revision.RevisionId==Guid.Empty||string.IsNullOrWhiteSpace(s.Curve.Code)||s.MappingId==Guid.Empty||s.DefinitionId==Guid.Empty||s.DefinitionVersion<1)||
            selections.Select(s=>s.Currency).Distinct().Count()!=selections.Length)
            throw new MarketDataException("projection_cash_policy","Select explicit empty nominal funding or one exact native accumulation source per portfolio currency.");
        if(GasOwned.IsDefault||GasOwned.Any(s=>s is null||s.AccountId==Guid.Empty||s.ProfileId==Guid.Empty||s.Revision<1)||
            GasOwned.Select(s=>s.AccountId).Distinct().Count()!=GasOwned.Length)
            throw new MarketDataException("projection_gas_policy","Select exact saved gas projection profiles, or explicitly select an empty gas profile list.");
    }
}

internal sealed record LifecycleHorizonSource(LifecycleProjectionHorizon Horizon,JsonElement Evidence);
internal sealed record PreparedLifecyclePortfolio(PortfolioEconomicProjectionRequest Request,JsonElement HorizonEvidence,
    ImmutableArray<PreparedLifecycleProjection> Projections,PortfolioValuation.Prepared HistoryPortfolio,
    ImmutableArray<JsonElement> ProductEvidence,ImmutableArray<FuturesOptionCalibrationReceipt> CalibrationReceipts,ImmutableArray<JsonElement> FinalEvidence)
{
    internal ImmutableArray<LifecycleEconomicOwnerDescription> AdditionalOwners{get;init;}=[];
    internal ImmutableArray<QuoteFactor> Factors=>LifecycleEconomicRisk.Factors(Projections);
    internal JsonElement? CashAccumulationEvidence{get;init;}
    internal ImmutableArray<(PreviewData Data,InputFrame Frame)> CashAccumulationInputs{get;init;}=[];
}

internal static partial class LifecyclePortfolioProjection
{
    private sealed record SourceAdmission(Guid Catalog,Guid Context,DateTimeOffset At,DateOnly Day,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var command=ScheduleStore.Command(connection,"SELECT security.require_admitted_business_source($1,$2,$3,$4,$5)",Catalog,Context,At,Day,Until);
            await command.ExecuteNonQueryAsync(ct);
        }
    }
    internal static async Task<JsonElement> ReadSessions(NpgsqlConnection connection,PreviewDatabase database,ScheduleContext source,DateTimeOffset at,CancellationToken ct)
    {
        var context=PinnedContext.Exact(source);var day=PublishedSchedules.ContextDate(context);
        if(!TradingInstant.IsSupported(at))throw new MarketDataException("projection_source","Select an exact supported UTC source instant.");
        var value=await ModelProviders.Read(connection,"SELECT security.lifecycle_projection_sessions($1,$2,$3,$4)::text",
            [database.Manifest.CatalogId,context.ContextId!.Value,at,day],ct);
        database.RegisterCommitAdmission(connection,new SourceAdmission(database.Manifest.CatalogId,context.ContextId.Value,at,day,value.Instant("authorityUntil")));
        if(value.Id("contextId")!=context.ContextId||value.Instant("knownAt")!=SchedulePreview.ParseAt(context.KnownAt)||value.Instant("asOf")!=at||
            DateWorkspace.Date(value.Text("businessDate"))!=day||value.GetProperty("sessions").ValueKind!=JsonValueKind.Array)
            throw new MarketDataException("projection_horizon_evidence","Session choices must retain their exact selected business and knowledge source.");
        return JsonSerializer.SerializeToElement(value.EnumerateObject().Where(p=>p.Name!="authorityUntil").ToDictionary(p=>p.Name,p=>p.Value.Clone()),RiskJobs.Json);
    }
    internal static async Task<LifecycleHorizonSource> ReadHorizon(NpgsqlConnection connection,PreviewDatabase database,
        PortfolioValuation.Prepared opening,PortfolioEconomicProjectionRequest request,CancellationToken ct)
    {
        request.Validate();var context=PinnedContext.Exact(opening.Context);var day=PublishedSchedules.ContextDate(context);
        if(opening.Sources.IsDefaultOrEmpty||!opening.Excluded.IsEmpty||opening.Sources.Select(s=>s.Snapshot.ValuationAt).Distinct().Count()!=1)
            throw new MarketDataException("projection_scope","Prepare complete exact-source product holdings at one opening clock without exclusions.");
        var at=opening.Sources[0].Snapshot.ValuationAt;var known=SchedulePreview.ParseAt(context.KnownAt);
        foreach(var source in opening.Sources)
        {
            source.Frame.Time.RequireDate(day);
            if(source.Frame.ContextId!=context.ContextId||source.Snapshot.Knowledge is not {} knowledge||knowledge.ContextId!=context.ContextId||knowledge.KnownAt!=known)
                throw new MarketDataException("projection_source","Every product source must retain the selected opening business day and certified knowledge context.");
        }
        var value=await ModelProviders.Read(connection,"SELECT security.read_lifecycle_projection_horizon($1,$2,$3,$4,$5,$6)::text",
            [database.Manifest.CatalogId,request.Session,request.SessionVersion,context.ContextId!.Value,at,day],ct);
        var until=value.Instant("authorityUntil");
        database.RegisterCommitAdmission(connection,new SourceAdmission(database.Manifest.CatalogId,context.ContextId.Value,at,day,until));
        if(value.Text("implementation")!=request.HorizonPolicy||value.Id("contextId")!=context.ContextId||value.Instant("knownAt")!=known||
            value.Instant("openingAt")!=at||DateWorkspace.Date(value.Text("businessDate"))!=day)
            throw new MarketDataException("projection_horizon_evidence","The configured horizon must retain its exact certified opening coordinates.");
        LifecycleProjectionSession Session(JsonElement session)=>new(session.Id("catalogId"),session.Text("code"),session.GetProperty("version").GetInt32());
        var original=Session(value.GetProperty("openingSession"));var selected=Session(value.GetProperty("session"));
        if(original.CatalogId!=database.Manifest.CatalogId||original.Code!=request.Session||original.Version!=request.SessionVersion||
            selected.CatalogId!=original.CatalogId||selected.Code!=original.Code||selected.Version<1)
            throw new MarketDataException("projection_horizon_evidence","The next close must retain the exact selected session and source-known effective version chain.");
        var calendar=value.GetProperty("calendar");
        var reference=new ReferenceKey(new(calendar.Id("catalogId"),calendar.Id("revisionId")),calendar.Text("code"));
        var closes=value.GetProperty("closes").EnumerateArray().Select(c=>new RiskHistoryResolvedClose(DateWorkspace.Date(c.Text("date")),c.Instant("instant"),c.GetProperty("offsetSeconds").GetInt32())).ToImmutableArray();
        // The immutable proof excludes the current authority deadline. Each
        // replay retains a fresh original-read deadline in its transaction.
        var properties=value.EnumerateObject().Where(p=>p.Name!="authorityUntil").ToDictionary(p=>p.Name,p=>p.Value.Clone());
        return new(new(selected,reference,closes),JsonSerializer.SerializeToElement(properties,RiskJobs.Json));
    }
}
