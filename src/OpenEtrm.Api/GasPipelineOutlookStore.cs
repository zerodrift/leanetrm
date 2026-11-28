using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasPipelineOutlookRequest(FacilityQuantityContext Context,string From,string To,string Period,Guid? EntitlementId=null,string? CapacityFrom=null,string? CapacityTo=null);

internal sealed class GasPipelineOutlookStore(PreviewDatabase database)
{
    // Resolve the authoritative trade head and every physical member through the same admission as the Blotter.
    const string Obligations="""
        SELECT row_to_json(x)::text FROM (
          SELECT o.id,c.slot,c.code,c.direction,c.ratio_n::text,c.ratio_d::text
          FROM trading.gas_delivery_obligations o JOIN trading.gas_component_terms c USING(catalog_id,instrument_id,slot)
          WHERE(o.catalog_id,o.trade_id,o.origin_revision,o.instrument_id)=($1,$2,$3,$4) AND c.role='physical'
        ) x
        """;

    internal async Task<object> Read(NpgsqlConnection c,GasPipelineOutlookRequest request,ScheduleContext? selected,CancellationToken ct)
    {
        var from=DateWorkspace.Date(request.From);var to=DateWorkspace.Date(request.To);
        if(to<=from||to.DayNumber-from.DayNumber>366||request.Period is not ("day" or "month"))
            throw new MarketDataException("gas_outlook_window","Choose daily or monthly totals and a forward window of one to 366 gas days.");
        await new GasTransportCapacityStore(database).Capture(c,null,ct);
        var business=new OperationalBusinessContext(database);
        var scope=await business.Resolve(c,request.Context,selected,"gas_outlook_context",ct);
        await business.BindResolved(c,scope,ct);
        bool InWindow(JsonElement member)=>DateWorkspace.Date(member.Text("delivery_from"))<to&&DateWorkspace.Date(member.Text("delivery_to"))>from;
        var positions=(await GasPositions.Read(c,database.Manifest,[],[],ct))
            .Where(p=>p.Physical.Any(InWindow)).ToArray();
        var obligationsByTrade=new Dictionary<Guid,ImmutableArray<JsonElement>>();
        foreach(var position in positions.Where(p=>p.Head.GetProperty("composition").GetBoolean()))
        {
            var held=await PreviewDatabase.Rows(c,Obligations,[database.Manifest.CatalogId,position.Trade,
                int.Parse(position.Head.Text("revision"),CultureInfo.InvariantCulture),position.Binding],ct);
            if(held.Length!=position.Physical.Length)throw new MarketDataException("gas_outlook_source","Each physical Side requires its exact retained obligation.");
            obligationsByTrade.Add(position.Trade,held);
        }
        // One bound history read covers the exact resolved obligations. Planning
        // this protected join once per trade dominates a multi-trade outlook.
        var submissions=obligationsByTrade.Count>0?await PreviewDatabase.Rows(c,"""
            SELECT row_to_json(x)::text FROM (
             SELECT DISTINCT ON (s.obligation_id,r.day) s.obligation_id,r.day,r.id,r.quantity::text AS quantity,
               jsonb_agg(jsonb_build_object('externalReference',n.external_reference,'submittedAt',n.submitted_at,'accountId',n.account_id)
                         ORDER BY n.external_reference,n.id) AS requests
             FROM trading.gas_quantity_series s JOIN trading.gas_quantity_revisions r ON (r.catalog_id,r.series_id)=(s.catalog_id,s.id)
             JOIN trading.gas_nomination_requests n ON (n.catalog_id,n.nomination_id,n.day)=(r.catalog_id,r.id,r.day)
             WHERE s.catalog_id=$1 AND s.obligation_id=ANY($2::uuid[]) AND s.kind='nominated' AND r.day>=$3 AND r.day<$4
             GROUP BY s.obligation_id,r.day,r.id,r.quantity,r.revision ORDER BY s.obligation_id,r.day,r.revision DESC
            ) x
            """,[database.Manifest.CatalogId,obligationsByTrade.Values.SelectMany(items=>items).Select(o=>o.Id("id")).ToArray(),from,to],ct):[];
        var submittedHeads=submissions.ToDictionary(h=>(h.Id("obligation_id"),DateWorkspace.Date(h.Text("day"))));
        var schedules=new RetainedScheduleStore(database);var quantities=new GasDailyQuantities(database);
        var rows=new List<GasOutlookOccurrence>();var sources=new List<object>();var operatingDays=new HashSet<(Guid Obligation,DateOnly Day)>();
        foreach(var position in positions)
        {
            var physical=position.Physical.Where(InWindow).ToArray();
            var trade=position.Trade;var instrument=position.Binding;
            var revision=int.Parse(position.Head.Text("revision"),CultureInfo.InvariantCulture);
            var composed=position.Head.GetProperty("composition").GetBoolean();
            var obligations=composed?obligationsByTrade[trade]:[];
            var programs=await schedules.LoadPrograms(c,instrument,scope.Context.ContextId,scope.At,ct);
            var heads=composed?await quantities.QuantityRows(c,trade,"heads",false,ct):[];
            var byDay=heads.ToLookup(h=>(h.Id("obligation_id"),DateWorkspace.Date(h.Text("day"))));
            var booked=ScheduleNumber.Parse(position.Head.Text("quantity"));
            foreach(var member in physical)
            {
                var slot=int.Parse(member.Text("slot"),CultureInfo.InvariantCulture);
                var obligation=composed?obligations.Single(o=>o.Text("slot")==member.Text("slot")):default;
                var direction=composed?obligation.Text("direction"):position.Head.Text("direction");
                var code=composed?obligation.Text("code"):"physical-1";
                var scale=composed?new ExactRatio(BigInteger.Parse(obligation.Text("ratio_n"),CultureInfo.InvariantCulture)*booked.Numerator,
                    BigInteger.Parse(obligation.Text("ratio_d"),CultureInfo.InvariantCulture)*booked.Denominator):new ExactRatio(booked.Numerator,booked.Denominator);
                var side=programs.Single(p=>p.Slot==slot);
                foreach(var day in GasDailyQuantitySelection.ReadContract(side.Program,scale).Where(d=>d.Day>=from&&d.Day<to))
                {
                    if(day.Quantity.Specification!=new ReferenceKey(position.Reference,member.Text("quantity_code")))throw new MarketDataException("gas_outlook_source","Use the exact retained quantity specification of the physical Side.");
                    var path=new GasOutlookPath(position.Reference.CatalogId,position.Reference.RevisionId,member.Text("pipeline_code"),member.Text("zone_code"),
                        member.Text("point_code"),member.Text("product_code"),day.Quantity.Specification.Code,member.Text("unit"),direction=="1"?"receive":"deliver");
                    if(composed)operatingDays.Add((obligation.Id("id"),day.Day));
                    var facts=composed?byDay[(obligation.Id("id"),day.Day)].ToDictionary(h=>h.Text("kind")):null;
                    GasOutlookEvidence? Stage(string kind)=>facts is not null&&facts.TryGetValue(kind,out var fact)?new(fact.Text("status"),fact.OptionalText("quantity")):null;
                    var submission=composed?submittedHeads.GetValueOrDefault((obligation.Id("id"),day.Day)):default;
                    GasOutlookEvidence? submitted=submission.ValueKind==System.Text.Json.JsonValueKind.Object?new("submitted",submission.Text("quantity")):null;
                    rows.Add(new GasOutlookOccurrence(path,day.Day,trade,revision,code,day.Quantity.Numerator,day.Quantity.Denominator,
                        Stage("nominated"),Stage("confirmed"),Stage("scheduled"),Stage("allocated")){Submitted=submitted});
                    if(request.Period=="day")sources.Add(new{path,day.Day,day.Start,day.End,tradeId=trade,revision,instrumentId=instrument,side=code,
                        reference=TradingReference.Trade(position.Head),contracted=day.Quantity.ToExactString(),
                        submitted,requests=submitted is null?null:(System.Text.Json.JsonElement?)submission.GetProperty("requests"),nominated=Stage("nominated"),confirmed=Stage("confirmed"),scheduled=Stage("scheduled"),allocated=Stage("allocated")});
                }
            }
        }
        var entitlements=await LifecycleNative.Read(c,"SELECT security.gas_transport_capacity_directory($1)::text",ct,database.Manifest.CatalogId);
        object? capacity=null;
        if(request.EntitlementId is {} id)
        {
            if(!entitlements.EnumerateArray().Any(h=>h.Id("id")==id))throw new MarketDataException("gas_outlook_entitlement","Select a visible transport entitlement.");
            var facts=await LifecycleNative.Read(c,"SELECT security.read_gas_transport_capacity($1,$2)::text",ct,database.Manifest.CatalogId,id);
            GasTransportCapacityStore.RequireSource(facts,scope.Context,scope.At,id);
            var ready=!facts.GetProperty("hasFutureHeads").GetBoolean();
            var assessment=ready?GasTransportCapacityAssessments.Current(facts):null;
            if(request.CapacityFrom is null||request.CapacityTo is null)throw new MarketDataException("gas_outlook_window","Select the independent UTC date window for commercial capacity.");
            var first=DateWorkspace.Date(request.CapacityFrom);var last=DateWorkspace.Date(request.CapacityTo);
            if(last<=first||last.DayNumber-first.DayNumber>366)throw new MarketDataException("gas_outlook_window","Select one to 366 UTC dates for commercial capacity.");
            var start=new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));var end=new DateTimeOffset(last.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
            if(assessment is not null)assessment=assessment with{Intervals=[..assessment.Intervals.Where(i=>i.From<end&&i.Through>start)]};
            var entitlement=facts.GetProperty("entitlement");
            await using var unit=ScheduleStore.Command(c,"SELECT u.symbol FROM ref_data.quantity_specifications q JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code) WHERE(q.catalog_id,q.revision_id,q.code)=($1,$2,$3)",
                entitlement.Id("reference_catalog_id"),entitlement.Id("reference_revision_id"),entitlement.Text("quantity_code"));
            capacity=new{ready,unit=await unit.ExecuteScalarAsync(ct) as string??throw new MarketDataException("gas_outlook_source","The entitlement requires its exact quantity unit."),
                tables=GasTransportCapacityPresentation.Tables(assessment,facts,null)};
        }
        var balances=await Balances(c,from,to,scope.At,operatingDays,ct);
        return new{context=scope.Context,scope.At,balances,entitlements,capacity,pipelines=GasPipelineOutlook.Summarize(rows,request.Period,byPipeline:true),rows=GasPipelineOutlook.Summarize(rows,request.Period),sources};
    }
    async Task<List<object>> Balances(NpgsqlConnection c,DateOnly from,DateOnly to,DateTimeOffset at,HashSet<(Guid Obligation,DateOnly Day)> operatingDays,CancellationToken ct)
    {
        var days=operatingDays.ToArray();
        var reported=await PreviewDatabase.Rows(c,"""
            WITH statements AS MATERIALIZED(
              SELECT DISTINCT account_id,day FROM trading.gas_balance_statements WHERE catalog_id=$1 AND day>=$2 AND day<$3
            ), days AS(
              SELECT * FROM unnest($4::uuid[],$5::date[]) AS d(obligation,day)
            ), eligible AS(
              SELECT a.id,days.day FROM trading.gas_service_accounts a JOIN days ON days.obligation=a.origin_obligation_id
              CROSS JOIN LATERAL(SELECT active,valid_from,valid_to FROM trading.gas_service_versions v
                WHERE(v.catalog_id,v.account_id)=(a.catalog_id,a.id) ORDER BY revision DESC LIMIT 1)v
              WHERE a.catalog_id=$1 AND v.active AND days.day>=v.valid_from AND days.day<v.valid_to
              UNION SELECT account_id,day FROM statements
            )
            SELECT row_to_json(x)::text FROM (
             SELECT e.day,a.id,a.trade_id,a.external_reference,a.pipeline_code,a.reference_catalog_id,a.reference_revision_id,u.symbol,
                    s.account_id IS NOT NULL AS reported
             FROM eligible e JOIN trading.gas_service_accounts a ON(a.catalog_id,a.id)=($1,e.id)
             JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,a.quantity_code)
             JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
             LEFT JOIN statements s ON(s.account_id,s.day)=(e.id,e.day)
             ORDER BY e.day,a.external_reference,a.id
            ) x
            """,[database.Manifest.CatalogId,from,to,days.Select(d=>d.Obligation).ToArray(),days.Select(d=>d.Day).ToArray()],ct);
        var result=new List<object>();var operations=new GasDeliveryOperationsStore(database);
        foreach(var group in reported.GroupBy(r=>(Day:DateWorkspace.Date(r.Text("day")),Trade:r.Id("trade_id"))))
        {
            var accounts=group.ToDictionary(r=>r.Id("id"));
            var statements=group.Where(r=>r.GetProperty("reported").GetBoolean()).Select(r=>r.Id("id")).ToHashSet();
            var balances=statements.Count==0?[]:await operations.Balances(c,await operations.State(c,group.Key.Trade,group.Key.Day,ct),group.Key.Day,at,statements,ct);
            foreach(var pair in accounts)
            {
                var account=pair.Value;var balance=balances.SingleOrDefault(b=>b.AccountId==pair.Key);
                result.Add(new{day=group.Key.Day,pipeline=account.Text("pipeline_code"),account=account.Text("external_reference"),accountId=pair.Key,
                    referenceCatalogId=account.Id("reference_catalog_id"),referenceRevisionId=account.Id("reference_revision_id"),unit=account.Text("symbol"),
                    amount=balance?.Amount,reason=balance?.Refusal??(!statements.Contains(pair.Key)?"Awaiting service-account statement.":balance is null?"Service terms unavailable for this day.":null),statementId=balance?.StatementId,movementId=balance?.MovementId});
            }
        }
        return result;
    }

}
