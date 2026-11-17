using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed partial class GasTransportFinancialStore
{
    private static (JsonElement Command,DateTimeOffset EffectiveAt) RequireRequest(GasTransportFinancialWriteRequest request)
    {
        if(request is null||request.OperationId==Guid.Empty||request.Context is not {} context||context.ContextId==Guid.Empty||
            context.BusinessDate==default||!TradingInstant.IsSupported(context.AsOf))throw Source();
        return(GasTransportFinancialCommands.Normalize(request.Command),TradingInstant.Parse(request.EffectiveAt,"gas_transport_financial_input"));
    }

    private Task<JsonElement> RecordNative(NpgsqlConnection c,GasTransportFinancialWriteRequest request,JsonElement command,
        DateTimeOffset effective,CancellationToken ct)=>Native(c,
        "SELECT security.record_gas_transport_financial($1,$2,$3::jsonb,$4,$5,$6)::text",ct,
        Catalog,request.OperationId,command.GetRawText(),effective,request.Context.ContextId,request.Context.AsOf);

    public async Task<JsonElement> Preview(NpgsqlConnection c,GasTransportFinancialWriteRequest request,CancellationToken ct)
    {
        var(command,effective)=RequireRequest(request);
        await Capture(c,command.Id("tradeId"),command.Text("kind"),ct);
        await RequireOwner(c,request.OwnerId,ct);
        await RequireCoordinates(c,Catalog,request.Context,ct);
        var prior=await Native(c,"SELECT security.gas_transport_financial_status($1,$2)::text",ct,Catalog,request.OperationId);
        JsonElement outcome;
        await using(var save=ScheduleStore.Command(c,"SAVEPOINT transport_financial_preview"))await save.ExecuteNonQueryAsync(ct);
        try{outcome=await RecordNative(c,request,command,effective,ct);}
        finally
        {
            await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT transport_financial_preview;RELEASE SAVEPOINT transport_financial_preview");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        if(outcome.Id("tradeId")!=command.Id("tradeId"))throw Source();
        var acknowledgement=prior.GetProperty("found").GetBoolean()||!outcome.GetProperty("changed").GetBoolean();
        var action=command.Text("kind") switch{
            "adopt_capacity_version"=>"Adopt the selected capacity version as this agreement's financial rights",
            "adopt_rights_mark"=>"Adopt the selected signed market value for the current whole rights package",
            "expire"=>"Close the rights at the contractual capacity window end",_=>throw Source()};
        var sections=await ReviewSections(c,request,command,effective,outcome,acknowledgement,action,ct);
        return JsonSerializer.SerializeToElement(new{request.OperationId,request.OwnerId,tradeId=command.Id("tradeId"),kind=command.Text("kind"),
            request.Context,requestedCommand=request.Command,requestedEffectiveAt=request.EffectiveAt,command,effectiveAt=effective,acknowledgement,outcome,
            overviewSections=sections},GasOwnedOperations.Json);
    }

    private async Task<object[]> ReviewSections(NpgsqlConnection c,GasTransportFinancialWriteRequest request,JsonElement command,DateTimeOffset effective,
        JsonElement outcome,bool acknowledgement,string action,CancellationToken ct)
    {
        var values=new List<object>{new{label="Operation",value=acknowledgement?"Acknowledge the accepted fact":action},
            new{label="Effective at",value=LifecycleNative.Instant(effective),kind="instant"}};
        if(acknowledgement)
        {
            values.Add(new{label=command.Text("kind")=="adopt_rights_mark"?"Accepted mark revision":"Accepted rights revision",value=outcome.GetProperty("revision").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)});
            values.Add(new{label="Effect",value="Retain the first accepted outcome"});
        }
        else
        {
            // Fresh effect facts use the SAME explicit certificate as the
            // admitted command. An acknowledgement never resolves new sources.
            var context=await LifecycleNative.Source(c,database,request.Context,ct);
            await using(var save=ScheduleStore.Command(c,"SAVEPOINT transport_financial_review_source"))await save.ExecuteNonQueryAsync(ct);
            try
            {
                await PinnedContext.BindExact(c,Catalog,context,ct);
                await TradingContext.Bind(c,Catalog,context,request.Context.AsOf,ct);
                var financial=await Read(c,database,request.Context,command.Id("tradeId"),ct);
                var state=financial.GetProperty("financialState");
                if(command.Text("kind")=="adopt_rights_mark")
                {
                    var rows=await PreviewDatabase.Rows(c,"""
                        SELECT jsonb_build_object('value',trim_scale(o.value)::text,'currency',currency.display_code,'observedAt',o.observed_at,'name',curve.name)::text
                        FROM market_data.observations o
                        JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id)
                        JOIN market_data.curves curve ON(curve.catalog_id,curve.revision_id,curve.code)=(o.catalog_id,o.definition_revision_id,$4)
                        JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(o.reference_catalog_id,o.reference_revision_id,s.quote_code)
                        JOIN ref_data.currencies currency ON(currency.catalog_id,currency.revision_id,currency.code)=(q.catalog_id,q.revision_id,q.currency_code)
                        WHERE(o.catalog_id,o.definition_revision_id,o.id)=($1,$2,$3)
                        """,[Catalog,command.Id("marketDefinitionRevisionId"),command.Id("observationId"),command.Text("curveCode")],ct);
                    if(rows.Length!=1)throw Source();
                    var mark=rows[0];
                    values.Add(new{label="Signed value for one whole rights package",value=mark.Text("value")+" "+mark.Text("currency")});
                    values.Add(new{label="Market source",value=mark.Text("name")});
                    values.Add(new{label="Value at",value=mark.Text("observedAt"),kind="instant"});
                    values.Add(new{label="Financial revision",value=state.GetProperty("revision").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)});
                }
                else if(command.Text("kind")=="adopt_capacity_version")
                {
                    var capacity=await GasTransportFinancialCapacity.ReadOne(c,database,request.Context,financial.GetProperty("terms").Id("entitlementId"),ct);
                    var version=capacity.GetProperty("version");
                    if(version.ValueKind!=JsonValueKind.Object||version.Id("id")!=command.Id("capacityVersionId"))throw Source();
                    values.Add(new{label="Current financial capacity",value="Capacity revision "+state.GetProperty("capacityVersionRevision").GetInt32()});
                    values.Add(new{label="Selected financial capacity",value="Capacity revision "+version.GetProperty("revision").GetInt32()});
                    values.Add(new{label="Rights value after amendment",value="A new signed whole-rights mark must be adopted for the new financial state"});
                }
                else values.Add(new{label="Rights value after accepted expiry",value="Zero remaining service value"});
            }
            finally
            {
                await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT transport_financial_review_source;RELEASE SAVEPOINT transport_financial_review_source");
                await restore.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
        values.Add(new{label="Independent obligations",value="Fee payments, physical activity and gas ownership retain their own records"});
        return [new{label="Transport financial review",values=values.ToArray()}];
    }

    public async Task<JsonElement> Record(NpgsqlConnection c,GasTransportFinancialWriteRequest request,CancellationToken ct)
    {
        var(command,effective)=RequireRequest(request);
        await Capture(c,command.Id("tradeId"),command.Text("kind"),ct);
        await RequireOwner(c,request.OwnerId,ct);
        await RequireCoordinates(c,Catalog,request.Context,ct);
        // The native operation proves exact/semantic recovery before consulting
        // fresh heads. Managed recovery adds no source refresh or renewed clock.
        var outcome=await RecordNative(c,request,command,effective,ct);
        if(outcome.Id("tradeId")!=command.Id("tradeId"))throw Source();
        return outcome;
    }
}
