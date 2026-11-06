using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api.Presentation;

namespace OpenEtrm.Api;

public sealed partial class PreviewDatabase
{
    internal const int MaximumObservationWindows=16384;
    internal async Task<PreviewData> ReadObservationWindows(NpgsqlConnection connection,PreviewData metadata,
        ImmutableArray<ObservationWindow> windows,CancellationToken ct,ImmutableArray<Guid> exactPins=default)
    {
        if(windows.Length>MaximumObservationWindows||windows.Any(window=>window.From>window.To))throw new ArgumentException("Observation windows require ordered bounds and at most 16,384 declared samples.");
        var requested=JsonSerializer.Serialize(windows.Distinct().Select(window=>new{series=window.SeriesId,source=window.SourceCode,role=window.Role,from=window.From,to=window.To}));
        var rows=await Rows(connection,"""
            WITH requested AS MATERIALIZED(SELECT * FROM jsonb_to_recordset($3::jsonb)
                r(series uuid,source text,role text,"from" timestamptz,"to" timestamptz)),
            spans AS MATERIALIZED(SELECT series,source,role,min("from") AS "from",max("to") AS "to"
                FROM requested GROUP BY series,source,role),
            selected AS(
                SELECT o.* FROM market_data.observations o JOIN spans s
                  ON o.series_id=s.series AND o.source_code=s.source AND o.role=s.role
                    AND o.observed_at>=s."from" AND o.observed_at<=s."to"
                WHERE o.catalog_id=$1 AND o.definition_revision_id=$2 AND EXISTS(SELECT 1 FROM requested r
                    WHERE o.series_id=r.series AND o.source_code=r.source AND o.role=r.role
                      AND o.observed_at>=r."from" AND o.observed_at<=r."to")
                UNION
                SELECT * FROM market_data.observations WHERE catalog_id=$1 AND definition_revision_id=$2 AND id=ANY($4::uuid[])
            ) SELECT row_to_json(t)::text FROM(SELECT * FROM selected ORDER BY observed_at,received_at,id)t
            """,[metadata.DefinitionRevision.CatalogId,metadata.DefinitionRevision.RevisionId,requested,
                (exactPins.IsDefault?ImmutableArray<Guid>.Empty:exactPins).ToArray()],ct);
        Interlocked.Add(ref observationRowsRead,rows.Length);
        return metadata.WithObservations(rows);
    }

    // One SQL statement preserves working-data visibility across the frames.
    // The API holds one frame's returned observations at a time.
    internal async IAsyncEnumerable<PreviewData> ReadFrameObservations(NpgsqlConnection connection,PreviewData metadata,
        ImmutableArray<InputFrame> frames,ObservationReadScope scope,[EnumeratorCancellation] CancellationToken ct,ImmutableArray<Guid>? seriesIds=null)
    {
        if(scope is not(ObservationReadScope.ExactPins or ObservationReadScope.FactHistory))throw new ArgumentOutOfRangeException(nameof(scope));
        if(scope==ObservationReadScope.FactHistory&&seriesIds is null)
            throw new ArgumentException("Fact history requires declared series.",nameof(seriesIds));
        if(frames.IsEmpty)yield break;
        var requests=JsonSerializer.Serialize(frames.Select((frame,ordinal)=>new{ordinal,pins=frame.ObservationIds}));
        var sql=scope==ObservationReadScope.FactHistory?"""
            WITH requested AS MATERIALIZED(SELECT * FROM jsonb_to_recordset($3::jsonb)r(ordinal integer,pins jsonb)),
            requested_pins AS MATERIALIZED(SELECT r.ordinal,jsonb_array_elements_text(r.pins)::uuid AS id FROM requested r),
            visible_pins AS MATERIALIZED(
                SELECT p.ordinal,o.id,o.series_id,o.source_code,o.source_event_id,o.observed_at,o.role FROM requested_pins p
                JOIN market_data.observations o ON o.catalog_id=$1 AND o.definition_revision_id=$2 AND o.id=p.id
            ),
            completeness AS MATERIALIZED(SELECT ordinal,count(*) AS visible FROM visible_pins GROUP BY ordinal),
            versions AS MATERIALIZED(
                SELECT * FROM market_data.observations WHERE catalog_id=$1 AND definition_revision_id=$2 AND series_id=ANY($4::uuid[])
            ),
            samples AS(
                SELECT p.ordinal,jsonb_agg(to_jsonb(o) ORDER BY o.observed_at,o.received_at,o.id) AS rows
                FROM visible_pins p JOIN versions o ON(o.series_id,o.source_code,o.source_event_id,o.observed_at,o.role)=
                    (p.series_id,p.source_code,p.source_event_id,p.observed_at,p.role)
                WHERE p.series_id=ANY($4::uuid[]) GROUP BY p.ordinal
            )
            SELECT r.ordinal,coalesce(s.rows,'[]'::jsonb)::text,coalesce(c.visible,0)=jsonb_array_length(r.pins)
            FROM requested r LEFT JOIN completeness c ON c.ordinal=r.ordinal LEFT JOIN samples s ON s.ordinal=r.ordinal ORDER BY r.ordinal
            """:seriesIds is not null?"""
            WITH requested AS MATERIALIZED(SELECT * FROM jsonb_to_recordset($3::jsonb)r(ordinal integer,pins jsonb)),
            visible_pins AS MATERIALIZED(
                SELECT r.ordinal,o.id,o.observed_at,o.received_at,
                    CASE WHEN o.series_id=ANY($4::uuid[]) THEN to_jsonb(o) END AS sample
                FROM requested r CROSS JOIN LATERAL security.read_observation_pins($1,$2,
                    ARRAY(SELECT jsonb_array_elements_text(r.pins)::uuid))o
            ),
            samples AS(SELECT ordinal,count(*) AS visible,
                jsonb_agg(sample ORDER BY observed_at,received_at,id) FILTER(WHERE sample IS NOT NULL) AS rows
                FROM visible_pins GROUP BY ordinal)
            SELECT r.ordinal,coalesce(s.rows,'[]'::jsonb)::text,coalesce(s.visible,0)=jsonb_array_length(r.pins)
            FROM requested r LEFT JOIN samples s ON s.ordinal=r.ordinal ORDER BY r.ordinal
            """:"""
            -- Sort the small request rows before evaluating their correlated payloads.
            -- The barrier keeps a payload sort from buffering every frame before the first read.
            SELECT r.ordinal,coalesce((
                WITH anchors AS MATERIALIZED(
                    SELECT r.ordinal,ARRAY(SELECT jsonb_array_elements_text(r.pins)::uuid) AS ids
                )
                SELECT jsonb_agg(to_jsonb(o) ORDER BY o.observed_at,o.received_at,o.id)
                FROM anchors a CROSS JOIN LATERAL(
                    SELECT * FROM security.read_observation_pins($1,$2,a.ids) OFFSET 0
                )o
            ),'[]'::jsonb)::text
            FROM(
                SELECT * FROM jsonb_to_recordset($3::jsonb)r(ordinal integer,pins jsonb)
                ORDER BY r.ordinal OFFSET 0
            )r ORDER BY r.ordinal
            """;
        object[] parameters=seriesIds is not null?
            [metadata.DefinitionRevision.CatalogId,metadata.DefinitionRevision.RevisionId,requests,seriesIds!.Value.ToArray()]:
            [metadata.DefinitionRevision.CatalogId,metadata.DefinitionRevision.RevisionId,requests];
        var payloadColumns=scope==ObservationReadScope.FactHistory||seriesIds is not null?3:2;
        if(scope==ObservationReadScope.ExactPins)
        {
            DateTimeOffset until;
            await using(var authority=ScheduleStore.Command(connection,"SELECT security.authority_until('ReadMarketData')"))
            await using(var clock=await authority.ExecuteReaderAsync(ct))
            {
                if(!await clock.ReadAsync(ct)||clock.IsDBNull(0))throw new PreviewDeniedException();
                until=clock.GetFieldValue<DateTimeOffset>(0);
            }
            // Point calls recheck current access, but a later-starting grant
            // must not extend this batch. The barrier evaluates each payload
            // before its DB-clock check, without buffering all frame payloads.
            var deadline=seriesIds is null?"$4":"$5";
            sql=$"SELECT batch.*,clock_timestamp()<{deadline}::timestamptz FROM({sql} OFFSET 0)batch ORDER BY batch.ordinal";
            parameters=[..parameters,until];
        }
        await using var command=ScheduleStore.Command(connection,sql,parameters);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if(scope==ObservationReadScope.ExactPins&&!reader.GetBoolean(payloadColumns))throw new PreviewDeniedException();
            using var json=JsonDocument.Parse(reader.GetString(1));
            var rows=json.RootElement.EnumerateArray().Select(row=>ReferenceJson.PreserveNumericLexemes(row.GetRawText())).ToImmutableArray();
            Interlocked.Add(ref observationRowsRead,rows.Length);
            yield return metadata.WithObservations(rows,payloadColumns<3||reader.GetBoolean(2));
        }
    }
}

internal sealed record ObservationWindow(Guid SeriesId,string SourceCode,string Role,DateTimeOffset From,DateTimeOffset To);
