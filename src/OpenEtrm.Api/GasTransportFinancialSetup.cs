using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class GasTransportFinancialSetup
{
    internal static async Task<GasSetup> Read(NpgsqlConnection c,PreviewDatabase database,LifecycleDiscoveryRequest request,CancellationToken ct)
    {
        if(request.Target is not {Handler:GasTransportFinancialActions.Handler} target||request.Context is null)throw GasTransportFinancialStore.Source();
        // Optional operating discovery keeps its ORIGINAL absence/deadline. A
        // grant beginning while financial sources are read cannot add choices.
        await using var optional=ScheduleStore.Command(c,"SELECT nullif(security.authority_until('ReadGasOperations'),'-infinity'::timestamptz)");
        var value=await optional.ExecuteScalarAsync(ct);
        DateTimeOffset? operatingUntil=value switch{null or DBNull=>null,DateTime at=>new DateTimeOffset(at),DateTimeOffset at=>at,_=>throw GasTransportFinancialStore.Source()};
        var admission=await new GasTransportFinancialStore(database).Capture(c,target.ScopeId,null,ct);
        var context=await LifecycleNative.Source(c,database,request.Context,ct,bindWorkspaceForRead:true);
        await using(var save=ScheduleStore.Command(c,"SAVEPOINT transport_financial_setup"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await PinnedContext.BindExact(c,database.Manifest.CatalogId,context,ct);
            await TradingContext.Bind(c,database.Manifest.CatalogId,context,request.Context.AsOf,ct);
            var source=await GasTransportFinancialStore.Read(c,database,request.Context,target.ScopeId,ct);
            GasTransportFinancialActions.RequireTarget(target,source);
            await using var owner=ScheduleStore.Command(c,"SELECT security.gas_transport_financial_owner($1)",database.Manifest.CatalogId);
            var ownerId=(string)(await owner.ExecuteScalarAsync(ct))!;
            var marks=await MarkChoices(c,database,source,request.Context,ct);
            var versions=ImmutableArray<GasFormChoice>.Empty;
            if(operatingUntil is {} until)
            {
                var operating=new GasTransportCapacityAdmission(database.Manifest.CatalogId,null,until<admission.Until?until:admission.Until);
                database.RegisterCommitAdmission(c,operating);await operating.RequireCommit(c,ct);
                var facts=await GasTransportFinancialCapacity.ReadOne(c,database,request.Context,source.GetProperty("terms").Id("entitlementId"),ct);
                var current=facts.GetProperty("version");
                if(current.ValueKind==JsonValueKind.Object)versions=[Choice(current.Id("id").ToString(),"Capacity revision "+current.GetProperty("revision").GetInt32(),new{capacityVersionId=current.Id("id")})];
            }
            return Build(database.Manifest.CatalogId,request.Context,target,source,ownerId,marks,versions);
        }
        finally
        {
            await using var restore=ScheduleStore.Command(c,"ROLLBACK TO SAVEPOINT transport_financial_setup;RELEASE SAVEPOINT transport_financial_setup");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    static async Task<ImmutableArray<GasFormChoice>> MarkChoices(NpgsqlConnection c,PreviewDatabase database,JsonElement agreement,LifecycleReadContext context,CancellationToken ct)
    {
        var terms=agreement.GetProperty("terms");
        var rows=await PreviewDatabase.Rows(c,"""
            SELECT jsonb_build_object('marketDefinitionRevisionId',curve.revision_id,'curveCode',curve.code,'contractId',series.contract_id,'seriesId',series.id,
                'observationId',observation.id,'source',observation.source_code,'quote',series.quote_code,
                'label',curve.name||' · '||trim_scale(observation.value)::text||' '||currency.display_code||' · '||observation.source_code)::text
            FROM market_data.curves curve
            JOIN market_data.curve_members member ON(member.catalog_id,member.revision_id,member.curve_code)=(curve.catalog_id,curve.revision_id,curve.code)
            JOIN market_data.series series ON(series.catalog_id,series.revision_id,series.contract_id,series.quote_code)=(curve.catalog_id,curve.revision_id,member.contract_id,curve.quote_code)
            JOIN market_data.contracts contract ON(contract.catalog_id,contract.revision_id,contract.id)=(series.catalog_id,series.revision_id,series.contract_id)
            JOIN market_data.observations observation ON(observation.catalog_id,observation.definition_revision_id,observation.series_id)=(series.catalog_id,series.revision_id,series.id)
            JOIN ref_data.quote_specifications quote ON(quote.catalog_id,quote.revision_id,quote.code)=(curve.reference_catalog_id,curve.reference_revision_id,series.quote_code)
            JOIN ref_data.currencies currency ON(currency.catalog_id,currency.revision_id,currency.code)=(quote.catalog_id,quote.revision_id,quote.currency_code)
            WHERE(curve.catalog_id,curve.revision_id)=($1,$2) AND curve.rule='direct' AND curve.direct_role='trader_mark'
                AND quote.kind='outright' AND quote.multiplier=1 AND(quote.quantity_code,quote.currency_code)=($3,$4)
                AND observation.observed_at=$5 AND observation.role='trader_mark' AND observation.publication_state='final'
                AND contract.expiry_at>$5
                AND observation.source_code=curve.direct_source_code
                AND NOT EXISTS(WITH RECURSIVE corrections AS(
                    SELECT o.* FROM market_data.observations o WHERE(o.catalog_id,o.corrects_id)=(observation.catalog_id,observation.id)
                    UNION ALL SELECT o.* FROM market_data.observations o JOIN corrections p ON o.corrects_id=p.id AND o.catalog_id=p.catalog_id)
                    SELECT 1 FROM corrections corrected WHERE security.market_fact_visible(corrected.catalog_id,corrected.definition_revision_id,corrected.id))
            ORDER BY curve.code,series.contract_id,observation.source_code,observation.id LIMIT 257
            """,[database.Manifest.CatalogId,terms.Id("definitionRevisionId"),terms.Text("packageQuantity"),terms.Text("currency"),context.AsOf],ct);
        if(rows.Length>256)throw new MarketDataException("gas_transport_financial_selection","This source needs a searched mark selection before its complete eligible list can be shown.");
        return [..rows.Select(row=>Choice(row.Id("observationId").ToString(),row.Text("label"),new{
            marketDefinitionRevisionId=row.Id("marketDefinitionRevisionId"),curveCode=row.Text("curveCode"),contractId=row.Id("contractId"),seriesId=row.Id("seriesId"),
            observationId=row.Id("observationId"),source=row.Text("source"),quote=row.Text("quote")}))];
    }

    internal static GasSetup Build(Guid catalog,LifecycleReadContext context,LifecycleTarget target,JsonElement source,string ownerId,
        ImmutableArray<GasFormChoice> marks,ImmutableArray<GasFormChoice> versions)
    {
        GasTransportFinancialActions.RequireTarget(target,source);
        GasTransportFinancialStore.RequireSource(source,context,target.ScopeId,source.Instant("knownAt"));
        var agreement=GasTransportFinancialAdapter.Read(catalog,source);
        if(string.IsNullOrWhiteSpace(ownerId)||marks.IsDefault||versions.IsDefault)throw GasTransportFinancialStore.Source();
        var forms=new Dictionary<string,GasForm>();var configuration=new Dictionary<string,GasForm>();
        if(agreement.State is {Status:"live"} state)
        {
            forms.Add(GasTransportFinancialActions.Expire,new("Expire Transport rights","AmendTrade",[
                Reference("financialState","Captured live financial rights",["expectedFinancialStateId"],
                    [Choice(state.Id.ToString(),"Financial revision "+state.Revision,new{expectedFinancialStateId=state.Id})]),Text("evidence","Expiry evidence")]));
            if(context.AsOf<state.Through)
            {
                var prior=source.GetProperty("markHistory").EnumerateArray().Where(m=>m.Id("financialStateId")==state.Id&&m.Instant("valuationAt")==context.AsOf)
                    .OrderBy(m=>m.GetProperty("revision").GetInt32()).LastOrDefault();
                Guid? previous=prior.ValueKind==JsonValueKind.Undefined?null:prior.Id("id");
                configuration.Add("adopt_rights_mark",new("Adopt signed whole-rights market value","AmendTrade",[
                    Reference("financialState","Captured financial state and value time",["kind","tradeId","financialStateId","expectedPreviousMarkId","valuationAt","effectiveAt"],
                        [Choice(state.Id.ToString(),"Financial revision "+state.Revision,new{kind="adopt_rights_mark",tradeId=target.ScopeId,financialStateId=state.Id,
                            expectedPreviousMarkId=previous,valuationAt=LifecycleNative.Instant(context.AsOf),effectiveAt=LifecycleNative.Instant(context.AsOf)})]),
                    Reference("mark","Published signed value for one whole package",["marketDefinitionRevisionId","curveCode","contractId","seriesId","observationId","source","quote"],marks),
                    Text("evidence","Market value adoption evidence")]));
                var available=versions.Where(v=>JsonSerializer.SerializeToElement(v.Value,GasOwnedOperations.Json).Id("capacityVersionId")!=state.Rights.CapacityVersionId).ToImmutableArray();
                if(!available.IsEmpty)configuration.Add("adopt_capacity_version",new("Adopt a capacity version financially","AmendTrade",[
                    Reference("financialState","Captured financial rights",["kind","tradeId","expectedFinancialStateId"],
                        [Choice(state.Id.ToString(),"Financial revision "+state.Revision,new{kind="adopt_capacity_version",tradeId=target.ScopeId,expectedFinancialStateId=state.Id})]),
                    Reference("capacityVersion","Exact replacement capacity version",["capacityVersionId"],available),Instant("effectiveAt","Financial amendment effective at"),Text("evidence","Amendment evidence")]));
            }
        }
        if(agreement.Cash is {} cash&&context.SnapshotId is {} snapshot&&snapshot!=Guid.Empty)
        {
            var allocations=ImmutableArray.CreateBuilder<GasFormChoice>();var created=Guid.NewGuid();
            allocations.Add(Choice("new:"+created,"New observed bank payment",new{allocationId=created,expectedRevision=0}));
            foreach(var payment in cash.State.Allocations.GroupBy(p=>p.AllocationId).Select(g=>g.MaxBy(p=>p.Revision)!).OrderBy(p=>p.AllocationId))
                allocations.Add(Choice(payment.AllocationId.ToString(),"Recorded payment · revision "+payment.Revision,new{allocationId=payment.AllocationId,expectedRevision=payment.Revision}));
            configuration.Add("payment",new("Record independent fee payment","AmendTrade",[
                Reference("cashOwner","Independent fixed-fee cash",["snapshotId","knowledgeMode","tradeId","bindingId"],
                    [Choice(cash.BindingId.ToString(),"Fixed fee · "+cash.Quantity.Amount+" "+cash.Instrument.Currency.Code,new{snapshotId=snapshot,knowledgeMode="current",tradeId=target.ScopeId,bindingId=cash.BindingId})]),
                Reference("allocation","Observed bank payment allocation",["allocationId","expectedRevision"],allocations.ToImmutable()),
                Instant("paidAt","Observed payment time"),Text("amount","Observed signed bank amount"),
                Select("policy","Recorded cash policy",[Choice(RecordedCashValuation.Policy,"Explicit observed payments",RecordedCashValuation.Policy)])]));
        }
        var policies=source.GetProperty("policies").EnumerateArray().Where(p=>p.Text("action")==GasTransportFinancialActions.Expire&&p.Text("mode") is "manual" or "review"&&
            p.Instant("from_at")<=context.AsOf&&context.AsOf<p.Instant("until_at"))
            .Select(p=>new GasFormPolicy(p.Id("id").ToString(),(p.Text("mode")=="manual"?"Manual":"Review")+" expiry · revision "+p.GetProperty("revision").GetInt32(),
                p.GetProperty("revision").GetInt32(),GasTransportFinancialActions.Expire,target.ScopeId.ToString())).ToImmutableArray();
        return new(context,[],policies,forms.ToImmutableDictionary(StringComparer.Ordinal),configuration.ToImmutableDictionary(StringComparer.Ordinal)){Target=target,OwnerId=ownerId};
    }

    static GasFormChoice Choice(string id,string label,object value)=>new(id,label,value);
    static GasFormField Reference(string name,string label,string[] members,ImmutableArray<GasFormChoice> choices)=>new(name,label,"reference",false,choices,[],[..members]);
    static GasFormField Select(string name,string label,ImmutableArray<GasFormChoice> choices)=>new(name,label,"select",false,choices,[],[]);
    static GasFormField Text(string name,string label)=>new(name,label,"text",false,[],[],[]);
    static GasFormField Instant(string name,string label)=>new(name,label,"instant",false,[],[],[]);
}
