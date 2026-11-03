using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.Api;

// Clients choose references and policies. Resolved calendar days, offsets and
// observation evidence are server-owned and never accepted in this request.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortfolioHistoryRequest(PortfolioRiskCatalogRequest Scope,PortfolioValuationPolicy PricingPolicy,RiskHistoryPolicy Policy,
    ReferenceKey Calendar,string From,string To,string CloseTime,string AmbiguousTime,string InvalidTime,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]string? ExpectedManifestId=null);

public sealed partial class PortfolioRiskPreview
{
    private const int MaximumHistoryBytes=32*1024*1024;
    private static IEnumerable<PreviewData> HistoryReferences(PortfolioValuation.Prepared p)=>p.Markets.Select(m=>m.Data).DistinctBy(d=>d.ReferenceRevision);
    private static object HistoryCapabilities(PortfolioValuation.Prepared p)=>new{
        profile=new{convention=RiskHistoryPolicy.Version1,numericalPolicy=CurveModel.NumericalPolicy,basis=RiskHistoryPolicy.Raw,revisions=RiskHistoryPolicy.Heads,alignment=RiskHistoryPolicy.ExactCloses,
            transform=RiskHistoryPolicy.Absolute,missing=RiskHistoryPolicy.Complete,regimes=RiskHistoryPolicy.Fixed},
        clockPolicy=new{ambiguousTime="reject",invalidTime="reject",resolution=RiskHistoryClock.Captured},
        calendars=HistoryReferences(p).SelectMany(d=>d.References["calendars"].Select(c=>new{reference=new ReferenceKey(d.ReferenceRevision,c.Text("code")),
            label=c.Text("code").Replace('_',' '),from=c.Text("coverage_from"),to=c.Text("coverage_to"),clock=c.Text("clock_code"),
            clockRule=ReferenceClockJson.Read(d.References["clock_rules"].Single(r=>r.Text("code")==c.Text("clock_code")))}))
            .OrderBy(c=>c.reference.Revision.CatalogId).ThenBy(c=>c.reference.Revision.RevisionId).ThenBy(c=>c.reference.Code,StringComparer.Ordinal),
        limits=new{maxFactors=FactorHistory.MaximumFactors,maxSessions=FactorHistory.MaximumSessions,maxCells=FactorHistory.MaximumCells,
            maxObservations=FactorHistory.MaximumObservations,maxCalendarDays=FactorHistory.MaximumCalendarDays,maxResponseBytes=MaximumHistoryBytes}};

    public async Task<object> History(NpgsqlConnection connection,PreviewData data,InputFrame frame,PortfolioHistoryRequest body,CancellationToken ct)
    {
        if(body.Scope is null||body.Policy is null)throw new MarketDataException("risk_history_policy","Choose the retained scope and explicit dated-factor history policies.");
        FactorHistory.ValidatePolicy(body.Policy);
        var from=DateWorkspace.Date(body.From);var to=DateWorkspace.Date(body.To);
        if(from>=to||to.DayNumber-from.DayNumber>FactorHistory.MaximumCalendarDays)
            throw new MarketDataException("risk_history_calendar","Choose a nonempty history window of at most 3660 calendar days.");
        var (portfolio,bookIds,_)=await PreparePortfolio(connection,data,frame,body.Scope.BookIds,body.Scope.PortfolioIds,body.PricingPolicy,ct,tradeIds:body.Scope.TradeIds);
        var manifest=await BuildHistory(connection,portfolio,frame,body,ct);
        var context=portfolio.Context;var contextId=context.ContextId!.Value;var markets=portfolio.Markets;
        var scope=body.Scope with{Context=new(body.Scope.Context.SnapshotId,TradingBlotter.Convention,"retained",contextId),BookIds=bookIds,PortfolioIds=[],
            TradeIds=body.Scope.TradeIds is {} ids?ids.Order().ToImmutableArray():null};
        var captured=body with{Scope=scope,ExpectedManifestId=manifest.Id};
        var sources=markets.Select(m=>m.Data).ToImmutableArray();
        var result=new{manifest,request=captured,context,asOf=frame.At,snapshotId=portfolio.SnapshotId,engineVersion=RiskJobs.EngineVersion,
            pricingPolicy=body.PricingPolicy,selection=body.Scope with{Context=scope.Context,TradeIds=scope.TradeIds},scope=new{bookIds,portfolioIds=ImmutableArray<Guid>.Empty,scope.TradeIds},excludedPositions=portfolio.Excluded,
            columns=manifest.Columns.Select(c=>new{c.Id,factor=Factor(sources,CurveRiskAxes.Quote(portfolio,c.Mapping.Factor,ct)),state=c.Mapping.State.ToString()}),
            coverage=new{available=manifest.Available&&portfolio.Excluded.IsEmpty,historyAvailable=manifest.Available,
                portfolioComplete=portfolio.Excluded.IsEmpty,factors=manifest.Columns.Length,sessions=manifest.Sessions.Length,samples=manifest.Samples.Length,
                rejected=manifest.Rejected.Length,minimumSamples=manifest.Policy.MinimumSamples,manifest.UnavailableReason},
            limits=new{maxResponseBytes=MaximumHistoryBytes}};
        // Bound the complete retained evidence before returning a successful result.
        if(JsonSerializer.SerializeToUtf8Bytes(result,RiskJobs.Json).Length>MaximumHistoryBytes)
            throw new MarketDataException("risk_history_capacity","The history manifest exceeds 32 MiB. Reduce the window or select a smaller explicit scope.");
        await RequireHistoryAccess(connection,portfolio,ct);
        return result;
    }

    private async Task<AlignedFactorHistory> BuildHistory(NpgsqlConnection connection,PortfolioValuation.Prepared portfolio,InputFrame frame,
        PortfolioHistoryRequest body,CancellationToken ct,bool statistical=false)
    {
        FactorHistory.ValidatePolicy(body.Policy);var from=DateWorkspace.Date(body.From);var to=DateWorkspace.Date(body.To);
        if(from>=to||to.DayNumber-from.DayNumber>FactorHistory.MaximumCalendarDays)
            throw new MarketDataException("risk_history_calendar","Choose a nonempty history window of at most 3660 calendar days.");
        var reference=body.Calendar;
        var calendarData=HistoryReferences(portfolio).SingleOrDefault(d=>d.ReferenceRevision==reference.Revision)
            ??throw new MarketDataException("risk_history_calendar","Choose a calendar from the selected portfolio's exact reference bundles.");
        var stored=await new ScheduleStore(database.Manifest.CatalogId,reference.Revision.CatalogId,reference.Revision.RevisionId)
            .Calendar(connection,new(reference.Revision.CatalogId,reference.Revision.RevisionId,reference.Code),ct);
        if(from<stored.CoverageFrom||to>stored.CoverageTo)throw new MarketDataException("risk_history_calendar","The selected exact calendar must cover the whole history window.");
        var cropped=stored with{CoverageFrom=from,CoverageTo=to,Days=[..stored.Days.Where(d=>d.Date>=from&&d.Date<to)]};
        var calendar=new RiskHistoryCalendar(reference,from,to,[..DateWorkspace.Calendar(cropped).Dates]);
        var calendarRow=calendarData.References["calendars"].Single(c=>c.Text("code")==reference.Code);
        var clockCode=calendarRow.Text("clock_code");
        var clock=FactorHistory.CaptureClock(calendar,new(reference.Revision,clockCode),PpaQuantitiesStore.Clock(calendarData,clockCode),body.CloseTime,body.AmbiguousTime,body.InvalidTime);
        var context=portfolio.Context;var contextId=context.ContextId!.Value;
        int marketPrefix;
        await using(var prefix=ScheduleStore.Command(connection,"SELECT known_through FROM market_data.market_contexts WHERE catalog_id=$1 AND context_id=$2",database.Manifest.CatalogId,contextId))
            marketPrefix=(int)(await prefix.ExecuteScalarAsync(ct)??throw new PreviewDeniedException());
        var markets=portfolio.Markets;
        if(markets.IsEmpty||markets.Any(m=>m.Snapshot.ValuationAt!=frame.At||m.Snapshot.MarketCutoff!=markets[0].Snapshot.MarketCutoff))
            throw new MarketDataException("risk_history_context","All selected market partitions must state one exact forecast valuation and market cutoff.");
        var forecast=new RiskHistoryForecast(contextId,database.Manifest.CatalogId,frame.At,markets[0].Snapshot.MarketCutoff,SchedulePreview.ParseAt(context.KnownAt),marketPrefix,
            [..markets.Select(m=>new RiskHistoryMarket(m.Snapshot.Definitions,m.Snapshot.Id,m.Connected.Scenario.Plan.ManifestId))]);
        // This is the held dependency closure, not every quote offered by a snapshot.
        var factors=portfolio.Factors.Select(f=>new RiskHistoryFactor(f,markets.Single(m=>m.Data.DefinitionRevision==f.Input.Series.Revision)
            .Snapshot.Selections.Single(s=>s.Observation.Key==f.Observation).Observation.State)).ToImmutableArray();
        var prepared=FactorHistory.Prepare(forecast,body.Policy,calendar,clock,factors,ct);
        if(statistical)ParametricRisk.Admit(prepared.Columns.Length,prepared.Sessions.Length-1);
        var observations=await HistoryObservations(connection,prepared,markets,ct);
        await RequireHistoryAccess(connection,portfolio,ct);
        var manifest=FactorHistory.Align(prepared,observations,ct);
        if(body.ExpectedManifestId is {} expected&&expected!=manifest.Id)
            throw new MarketDataException("risk_history_manifest","The authorized history no longer matches the requested captured manifest. Review a new result before exporting.");
        return manifest;
    }

    private static async Task<ImmutableArray<RiskHistoryObservation>> HistoryObservations(NpgsqlConnection connection,PreparedFactorHistory prepared,
        ImmutableArray<PortfolioValuation.MarketPartition> markets,CancellationToken ct)
    {
        var evidence=ImmutableArray.CreateBuilder<RiskHistoryObservation>();
        foreach(var group in prepared.Columns.GroupBy(c=>c.Mapping.Factor.Input.Series.Revision))
        {
            var data=markets.Single(m=>m.Data.DefinitionRevision==group.Key).Data;
            var series=group.Select(c=>c.Mapping.Factor.Input.Series.Id).Distinct().ToArray();
            var anchors=group.Select(c=>c.Mapping.Factor.Observation.Id).Distinct().ToArray();
            var remaining=FactorHistory.MaximumObservations-evidence.Count;
            var rows=await PreviewDatabase.Rows(connection,"""
                WITH RECURSIVE anchor_chain(id,corrects_id) AS(
                  SELECT o.id,o.corrects_id FROM market_data.observations o
                  WHERE o.catalog_id=$1 AND o.definition_revision_id=$2 AND o.id=ANY($6::uuid[])
                  UNION
                  SELECT p.id,p.corrects_id FROM market_data.observations p JOIN anchor_chain a ON a.corrects_id=p.id
                  WHERE p.catalog_id=$1 AND p.definition_revision_id=$2
                ),anchor_ids AS MATERIALIZED(SELECT id FROM anchor_chain LIMIT $9)
                SELECT row_to_json(t)::text FROM(
                  SELECT o.id,o.series_id,o.source_code,o.role,o.publication_state,o.observed_at,o.value,o.corrects_id,
                    e.event_order AS history_order,k.known_from AS history_known
                  FROM market_data.observations o
                  JOIN market_data.knowledge_events e ON(e.catalog_id,e.revision_id,e.observation_id)=(o.catalog_id,o.definition_revision_id,o.id)
                  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
                  WHERE o.catalog_id=$1 AND o.definition_revision_id=$2 AND o.series_id=ANY($3::uuid[])
                    AND ((o.observed_at>=$4 AND o.observed_at<=$5) OR o.id IN(SELECT id FROM anchor_ids))
                    AND o.observed_at<=$10 AND e.event_order<=$7 AND k.known_from<=$8
                  ORDER BY o.observed_at,o.id LIMIT $9
                )t
                """,[group.Key.CatalogId,group.Key.RevisionId,series,prepared.Sessions[0].Close.AddDays(-1),prepared.Sessions[^1].Close.AddDays(1),anchors,
                    prepared.Forecast.MarketKnownThrough,prepared.Forecast.KnownAt,remaining+1,prepared.Forecast.MarketCutoff],ct);
            if(rows.Length>remaining)throw new MarketDataException("risk_history_capacity","Too many observation revisions are required for this history window.");
            evidence.AddRange(ReadHistoryEvidence(data,rows,ct));
        }
        return evidence.ToImmutable();
    }
    internal static ImmutableArray<RiskHistoryObservation> ReadHistoryEvidence(PreviewData data,ImmutableArray<JsonElement> rows,CancellationToken ct)
    {
        var selected=rows.Select(r=>r.Id("series_id")).ToHashSet();
        var quotes=data.Quotes.ToDictionary(q=>q.Key);
        var mappings=data.Definitions["series"].Where(s=>selected.Contains(s.Id("id"))).ToDictionary(s=>s.Id("id"),s=>
        {
            var quote=new ReferenceKey(data.ReferenceRevision,s.Text("quote_code"));
            return(Quote:quote,Unit:PriceUnit.FromQuote(quotes[quote]),Contract:new ContractKey(data.DefinitionRevision,s.Id("contract_id")));
        });
        return [..rows.Select(row=>
        {
            ct.ThrowIfCancellationRequested();var mapping=mappings[row.Id("series_id")];
            return new RiskHistoryObservation(data.ReadObservation(row),mapping.Quote,mapping.Unit,mapping.Contract,
                row.OptionalId("corrects_id") is {} prior?new(data.DefinitionRevision.CatalogId,prior):null,
                int.Parse(row.Text("history_order"),System.Globalization.CultureInfo.InvariantCulture),row.Instant("history_known"));
        })];
    }
    private static async Task RequireHistoryAccess(NpgsqlConnection connection,PortfolioValuation.Prepared portfolio,CancellationToken ct)
    {
        var catalog=portfolio.Markets[0].Data.DefinitionRevision.CatalogId;
        await using(var access=ScheduleStore.Command(connection,"SELECT security.market_access($1,'ReadMarketData') AND security.trading_access($1,'ReadTrades') AND security.has_permission('RunScenario')",catalog))
            if(!Equals(await access.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        foreach(var data in HistoryReferences(portfolio))
        {
            await using var reference=ScheduleStore.Command(connection,"SELECT security.reference_access($1,'ReadMarketData') AND security.reference_version_visible($1,$2)",data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId);
            if(!Equals(await reference.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        }
    }
}
