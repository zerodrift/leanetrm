using System.Text.Json;
using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using Npgsql;

internal static class SwapSettlementAgentChecks
{
    public static async Task Run(string path)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new Exception("Use the owned preview runner.");
        await using var database=new PreviewDatabase(root);
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,json);
        var frame=fixture.GetProperty("frame").Deserialize<InputFrame>(json)!;
        var agent=new AgentIdentity(fixture.Text("principal"),fixture.Id("delegation"));
        var operations=new SwapSettlementOperations(database);
        await database.WithIdentity("admin","ManageAccess",async(connection,ct)=>
        {
            await using var read=ScheduleStore.Command(connection,"SELECT security.administration_directory()::text");
            var directory=JsonSerializer.Deserialize<JsonElement>((string)(await read.ExecuteScalarAsync(ct))!);
            if(directory.GetProperty("roles").EnumerateArray().Single(r=>r.Text("code")=="swap_settlement_operator").GetProperty("assignable").GetBoolean())
                throw new Exception("The agent-only financial role was offered to human accounts.");
            return true;
        },CancellationToken.None);
        try
        {
            await database.WithIdentity("admin","ManageAccess",async(connection,ct)=>
            {
                var payload=JsonSerializer.Serialize(new{operationId=Guid.NewGuid(),action="create",login="synthetic_invalid_agent_role",name="Synthetic invalid role assignment",kind="human",enabled=true,roles=new[]{"swap_settlement_operator"}},json);
                await using var write=ScheduleStore.Command(connection,"SELECT security.apply_administration($1::jsonb)",payload);
                return await write.ExecuteScalarAsync(ct);
            },CancellationToken.None,Admission.Administration);
            throw new Exception("A human account received an agent-only role.");
        }
        catch(PostgresException e)when(e.SqlState=="P0001"&&e.MessageText=="agent_managed"){}
        var context=new FuturesReadRequest(frame.Id,SwapStore.Convention,"current");
        var preview=new SwapSettlementPreviewRequest(context,fixture.Id("tradeId"),1,0,0,fixture.GetProperty("currency").Deserialize<ReferenceKey>(json),2,"currency_minor_units_to_even_v1");
        var result=Json(await operations.Preview(agent,frame,preview,CancellationToken.None));
        var command=new SwapSettlementDetermineRequest(preview,Guid.NewGuid(),0,result.GetProperty("value").Text("previewHash"),
            "swap_due_nominal_v1","swap_completed_period_trading_pnl_v1","swap_explicit_correction_due_v1",null);
        async Task Denied(Func<Task<object>> work,string? code=null)
        {
            try{await work();}catch(PreviewDeniedException){if(code is null)return;throw;}
            catch(PostgresException e)when(e.SqlState=="42501"&&code is null){return;}
            catch(MarketDataException e)when(e.Code==code){return;}
            throw new Exception("Expected controlled agent refusal "+code);
        }
        await Denied(()=>operations.Determine(new("synthetic_settlement_readonly",fixture.Id("readonlyDelegation")),frame,command,CancellationToken.None));
        await Denied(()=>operations.Determine(agent,frame,command with{PreviewHash="stale"},CancellationToken.None),"settlement_stale_preview");
        var accepted=Json(await operations.Determine(agent,frame,command,CancellationToken.None));
        var retry=Json(await operations.Determine(agent,frame,command,CancellationToken.None));
        if(accepted.GetRawText()!=retry.GetRawText())throw new Exception("Agent retries duplicated or changed the determination.");
        var status=Json(await operations.Status(agent,command.OperationId,CancellationToken.None));
        if(!status.GetProperty("found").GetBoolean()||status.Id("determinationId")!=accepted.Id("determinationId"))throw new Exception("The agent could not reconcile its committed operation.");
        await database.WithIdentity("trader","ReadTrades",async(connection,ct)=>
        {
            var view=await TradingView.Capture(connection,new KnowledgeStore(database.Manifest.CatalogId),frame,ct);
            return await new TradeReads(database).Read(connection,view,async data=>
            {
                var holdings=await new SwapStore(database).ReadRiskHoldings(connection,data,view.CalculationFrame,[],ct);
                var first=holdings.Single(h=>h.Row.Id("trade_id")==preview.TradeId);
                var state=first.Settlement??throw new Exception("Missing settlement state.");
                var zero=ExactMoney.Parse("0",first.Instrument.Currency);
                var secondState=new SwapSettlementInputs(Guid.NewGuid(),Guid.NewGuid(),state.Quantity,
                    state.Contributions.Select(c=>c with{Id=Guid.NewGuid(),DueAt=c.DueAt.AddDays(1)}).ToImmutableArray(),zero,zero,state.Completions,
                    "swap_due_nominal_v1","swap_completed_period_trading_pnl_v1");
                var second=first with{Settlement=secondState,Discounting=new CurrencyDefaultDiscounting(secondState.DiscountKeys(first.Instrument,first.Snapshot),first.Snapshot,false,null)};
                var receipt=SwapStore.DiscountEvidence([first,second]);
                foreach(var contribution in state.Contributions.Concat(secondState.Contributions))
                    if(!receipt.Any(d=>d.SettlementDeterminationId==contribution.Id&&d.Payment==contribution.DiscountKey))
                        throw new Exception("The shared instrument receipt dropped a trade-owned balance discount key.");
                Console.WriteLine("PASS shared-instrument receipt retains both trade-owned determination keys and distinct future due instants");
                return true;
            },ct);
        },CancellationToken.None);
        var payment=new SwapCashRequest(context,fixture.Id("tradeId"),Guid.NewGuid(),1,1,accepted.Id("determinationId"),accepted.Text("total"),preview.Currency,
            frame.At.ToString("O"),"Synthetic delegated contractual cash",null);
        await operations.RecordCash(agent,frame,payment,CancellationToken.None);
        var current=Json(await operations.Inspect(agent,frame,new(context,preview.TradeId,null,"swap_due_nominal_v1","swap_completed_period_trading_pnl_v1"),CancellationToken.None));
        if(current.GetProperty("value").GetProperty("settlement").GetProperty("version").GetInt32()!=2)throw new Exception("Agent cash did not share the durable settlement ledger.");
        var oldContext=context with{KnowledgeMode="retained",ContextId=result.GetProperty("context").Id("contextId")};
        var historical=Json(await operations.Inspect(agent,frame,new(oldContext,preview.TradeId,null,"swap_due_nominal_v1","swap_completed_period_trading_pnl_v1"),CancellationToken.None));
        if(historical.GetProperty("value").GetProperty("settlement").GetProperty("version").GetInt32()!=0)throw new Exception("Agent historical replay substituted current settlement.");
        await Denied(()=>operations.Determine(agent,frame,command with{Preview=preview with{Context=oldContext}},CancellationToken.None),"historical_read_only");
        await Denied(()=>operations.Inspect(agent with{Delegation=fixture.Id("revokedDelegation")},frame,
            new(context,preview.TradeId,null,"swap_due_nominal_v1","swap_completed_period_trading_pnl_v1"),CancellationToken.None));
        Console.WriteLine("PASS delegated agent settlement: shared preview/determination/payment, exact retry, stale refusal, historical replay and current delegation authority");
    }
}
