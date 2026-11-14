using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasOwnedSetupChecks
{
    internal static async Task RunNative(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(GasOwnedOperations.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))throw new Exception("Use an owned disposable database.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Pooling=false,Timezone="UTC"};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        foreach(var name in new[]{"before","after"})
        {
            var setup=await database.WithIdentity("trader","ReadGasOwnedHoldings",(connection,ct)=>GasOwnedSetup.Read(connection,database,
                new(new(fixture.Id(name),fixture.Instant("at"),new(2020,12,20)),new("gas_owned",fixture.Id("accountId"),fixture.Id("accountId"))),ct),default);
            var actual=setup.Forms["gas.operator.settle"].Fields.Single(f=>f.Name=="sourceObservationId").Choices.Select(c=>c.Id).ToHashSet();
            if(!actual.SetEquals(name=="before"?[fixture.Text("observation")]:Array.Empty<string>()))
                throw new Exception("Cashout form offered an unrelated contract, indicative head, ambiguous source or a stale final fallback.");
        }
        Console.WriteLine("PASS cashout form sources: exact account/definition/contract/quote/source/role, retained old view and preliminary-head refusal");
    }
    internal static void Run()
    {
        var account=Guid.NewGuid();var evidence=Guid.NewGuid();var later=Guid.NewGuid();var agreement=Guid.NewGuid();var policy=Guid.NewGuid();
        var delivery=Guid.NewGuid();var loss=Guid.NewGuid();var returned=Guid.NewGuid();var statement=Guid.NewGuid();
        var context=new LifecycleReadContext(Guid.NewGuid(),DateTimeOffset.Parse("2020-12-21T16:00:00Z"),new(2020,12,21));
        var state=JsonSerializer.SerializeToElement(new{accounts=new[]{new{id=account,code="synthetic_inventory",kind="owned_inventory",book_name="Synthetic book"}},
            evidence=new[]{new{id=evidence,account_id=account,source_key="synthetic_title",revision=1,kind="title_receipt",occurred_at="2020-12-20T16:00:00Z"},
                new{id=later,account_id=account,source_key="synthetic_title",revision=2,kind="title_receipt",occurred_at="2020-12-22T16:00:00Z"},
                new{id=agreement,account_id=account,source_key="synthetic_loan",revision=1,kind="loan_agreement",occurred_at="2020-12-20T16:00:00Z"},
                new{id=delivery,account_id=account,source_key="synthetic_delivery",revision=1,kind="physical_delivery",occurred_at="2020-12-20T16:00:00Z"},
                new{id=loss,account_id=account,source_key="synthetic_loss",revision=1,kind="physical_loss",occurred_at="2020-12-20T16:00:00Z"},
                new{id=returned,account_id=account,source_key="synthetic_return",revision=1,kind="loan_return",occurred_at="2020-12-20T16:00:00Z"},
                new{id=statement,account_id=account,source_key="synthetic_statement",revision=1,kind="operator_statement",occurred_at="2020-12-20T16:00:00Z"}},
            positions=Array.Empty<object>(),lots=Array.Empty<object>(),reservations=Array.Empty<object>(),loans=Array.Empty<object>(),fees=Array.Empty<object>(),
            policies=new[]{new{account_id=account,action="gas.owned.receive_title",revision=1,policy_id=policy,policy_revision=1}}},GasOwnedOperations.Json);
        var policies=new[]{JsonSerializer.SerializeToElement(new{id=policy,revision=1,action="gas.owned.receive_title",mode="review",from_at="2020-01-01T00:00:00Z",until_at="2021-01-01T00:00:00Z"})};
        var setup=GasOwnedSetup.Build(context,null,state,[..policies],[],[],[],[],[],[],state,[]);
        var newer=policies[0].EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.Clone());newer["revision"]=JsonSerializer.SerializeToElement(2);
        var pinned=GasOwnedSetup.Build(context,null,state,[..policies,JsonSerializer.SerializeToElement(newer)],[],[],[],[],[],[],state,[]);
        if(pinned.Policies is not [{Revision:1}]||pinned.Policies[0].Id!=policy.ToString())
            throw new Exception("A newer unbound policy version cannot remove the exact saved version still bound to this account.");
        var form=setup.Forms["gas.owned.receive_title"];
        if(form.Fields.Single(f=>f.Name=="evidenceId").Choices.Any(c=>c.Id==evidence.ToString()))throw new Exception("A future-dated evidence head must block its older title evidence choice.");
        if(setup.Forms["gas.loan.borrow"].Fields.Single(f=>f.Name=="agreementId").Choices is not [{Id:var selected}]||selected!=agreement.ToString())
            throw new Exception("A gas loan must choose an existing accepted loan-agreement evidence head, never an invented agreement identifier.");
        foreach(var (action,id) in new[]{("gas.owned.dispatch",delivery),("gas.owned.loss",loss),("gas.loan.repay",returned),("gas.operator.record",statement)})
            if(setup.Forms[action].Fields.Single(f=>f.Name=="evidenceId").Choices is not [{Id:var choice}]||choice!=id.ToString())
                throw new Exception("Gas action evidence choices must match the native operation's exact evidence kind.");
        if(form.Fields.Single(f=>f.Name=="quantity").Kind!="text"||form.Fields.Single(f=>f.Name=="titleAt").Kind!="instant")
            throw new Exception("Gas forms must preserve financial decimal text and explicitly resolve observed user clocks.");
        if(setup.Configuration["account"].Fields.Single(f=>f.Name=="legalEntity").Members is not ["legalEntityId","legalEntityRevision"])
            throw new Exception("A legal-entity selection must transport its exact version together.");
        if(setup.Policies.Single().Revision!=1||setup.Context!=context)throw new Exception("Gas form choices lost their exact source or policy version.");
        if(setup.Configuration["mark_binding"].Permission!="ConfigureGasOwnedHoldings"||setup.Configuration["projection_profile"].Permission!="ConfigureGasOwnedHoldings"||
            setup.Configuration["evidence"].Permission!="RecordGasOwnedHoldings")throw new Exception("Gas forms must preserve the native distinction between configuring policy and recording observed evidence.");
        var cashout=setup.Forms["gas.operator.record"].Fields.Single(f=>f.Name=="cashout").Fields;
        if(!cashout.Single(f=>f.Name=="role").Choices.Select(c=>c.Id).ToHashSet().SetEquals(["daily_settlement","final_contract_settlement"])||
            cashout.Single(f=>f.Name=="rounding").Choices is not [{Id:"payment_total_half_even_2dp_v1"}])
            throw new Exception("Operator cashout choices must match its explicit supported final roles and cumulative rounding contract.");
        var profile=new{profileId=Guid.NewGuid(),expectedRevision=0,implementation="gas_quantity_and_cashout_lifecycle_projection_v2",quantityDisposition="retain_quantity_obligations_v1",
            effectiveAt=context.AsOf,performances=Array.Empty<object>(),cashouts=new[]{new{positionId=Guid.NewGuid(),scenarioDefinitionRevisionId=Guid.NewGuid(),scenarioCurve="synthetic_mark",scenarioContractId=Guid.NewGuid(),
                priceInterpretation="selected_valuation_mark_is_cashout_expectation_v1",cashCarrying="explicit_nominal_cashout_cash_v1"}}};
        if(GasOwnedOperations.Selection("projection_profile",JsonSerializer.SerializeToElement(profile,GasOwnedOperations.Json)).GetProperty("cashouts").GetArrayLength()!=1)
            throw new Exception("Cashout projection configuration must retain its exact typed scenario and explicit cash carrying.");
        var legacy=JsonSerializer.SerializeToElement(profile with{implementation="gas_quantity_lifecycle_projection_v1",cashouts=profile.cashouts.Take(0).ToArray()},GasOwnedOperations.Json);
        if(GasOwnedOperations.Selection("projection_profile",legacy).TryGetProperty("cashouts",out _))throw new Exception("An explicit empty cashout choice changed the legacy quantity-only fact identity.");
        var missing=JsonSerializer.SerializeToElement(JsonSerializer.SerializeToElement(profile,GasOwnedOperations.Json).EnumerateObject().Where(p=>p.Name!="cashouts").ToDictionary(p=>p.Name,p=>p.Value));
        try{GasOwnedOperations.Selection("projection_profile",missing);throw new Exception("A v2 cashout policy accepted missing financial selections.");}
        catch(MarketDataException error)when(error.Code=="gas_owned_input"){}
        var accepted=profile with{implementation="gas_accepted_quantity_lifecycle_projection_v3",cashouts=profile.cashouts.Take(0).ToArray()};
        if(GasOwnedOperations.Selection("projection_profile",JsonSerializer.SerializeToElement(accepted,GasOwnedOperations.Json)).GetProperty("cashouts").GetArrayLength()!=0)
            throw new Exception("The accepted-quantity model lost its explicit empty cashout selection.");
        var omitted=JsonSerializer.SerializeToElement(accepted,GasOwnedOperations.Json).EnumerateObject().Where(p=>p.Name!="cashouts").ToDictionary(p=>p.Name,p=>p.Value);
        try{GasOwnedOperations.Selection("projection_profile",JsonSerializer.SerializeToElement(omitted));throw new Exception("A v3 accepted-quantity policy silently accepted an omitted cashout selection.");}
        catch(MarketDataException error)when(error.Code=="gas_owned_input"){}
        if(!setup.Configuration["projection_profile"].Fields.Single(f=>f.Name=="implementation").Choices.Any(c=>c.Id==profile.implementation)||
            !setup.Configuration["projection_profile"].Fields.Single(f=>f.Name=="implementation").Choices.Any(c=>c.Id==accepted.implementation)||
            setup.Configuration["projection_profile"].Fields.Single(f=>f.Name=="cashouts").Fields.Single(f=>f.Name=="scenarioMark").Members is not ["scenarioDefinitionRevisionId","scenarioCurve","scenarioContractId"])
            throw new Exception("Gas settings must offer a complete typed cashout model and exact scenario market tuple.");
        var holding=Guid.NewGuid();var performance=Guid.NewGuid();var consumption=Guid.NewGuid();var lot=Guid.NewGuid();
        var correctionState=state.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.Clone());
        correctionState["principalVersions"]=JsonSerializer.SerializeToElement(new[]{new{position_id=holding,revision=5}});
        var correctionKnown=state.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.Clone());
        correctionKnown["positions"]=JsonSerializer.SerializeToElement(new[]{new{id=lot,account_id=account,kind="owned_stock",evidence_id=evidence,effective_at="2020-12-20T16:00:00Z"}});
        correctionKnown["lots"]=JsonSerializer.SerializeToElement(new[]{new{id=lot}});
        var correction=JsonSerializer.SerializeToElement(new{position=new{id=holding,account_id=account},performance=new{id=performance,revision=3},
            supply=new[]{new{ordinal=1,consumption_id=consumption,financial_revision=2,retracted=false,quantity="0.125",lot_id=lot,evidence_id=delivery,reservation_id=(Guid?)null}}});
        var correctionSetup=GasOwnedSetup.Build(context,new(GasFuturesReconciliationActions.Handler,holding,performance),
            JsonSerializer.SerializeToElement(correctionState),[..policies],[],[],[],[],[],[],JsonSerializer.SerializeToElement(correctionKnown),[],correction);
        var fields=correctionSetup.Forms[GasFuturesReconciliationActions.Action].Fields;
        if(fields.Single(f=>f.Name=="observedPerformance").Members is not ["positionId","performanceId","expectedRevision"]||
            fields.Single(f=>f.Name=="principalVersion").Members is not ["expectedPrincipalVersion"]||
            fields.Single(f=>f.Name=="retracted").Choices.Select(c=>c.Value).OfType<bool>().Order().ToArray() is not [false,true]||
            fields.Any(f=>f.Kind=="instant"))throw new Exception("Quantity correction must choose exact retained versions and explicit retraction without editing observed clocks.");
        var stockUse=fields.Single(f=>f.Name=="supply").Fields.Single(f=>f.Name=="stockUse");
        if(stockUse.Members is not ["consumptionId","expectedRevision"]||stockUse.Choices.Length!=2||
            !stockUse.Choices.Any(c=>c.Id==consumption.ToString()&&c.Label.Contains("synthetic_inventory",StringComparison.Ordinal)&&c.Label.Contains("quantity 0.125",StringComparison.Ordinal))||!stockUse.Choices.Any(c=>c.Id=="new"))
            throw new Exception("Quantity correction requires the selected performance's exact stock-use revisions or an explicit new allocation.");
        var linked=JsonSerializer.SerializeToElement(new{corrections=new[]{101,102}.Select(number=>new{
            accountId=account,positionId=Guid.NewGuid(),performanceId=Guid.NewGuid(),reference="Synthetic shared reference",quantity="0.125",financialRevision=3,
            parentTradeReference=number,deliverySeries="synthetic_monthly_future",deliveryProfile="Synthetic delivery",deliveryProfileRevision=2})});
        var linkedSetup=GasOwnedSetup.Build(context,new(GasFuturesQuantityBatchActions.Handler,account,account),
            JsonSerializer.SerializeToElement(correctionState),[..policies],[],[],[],[],[],[],state,[],batch:linked);
        var variants=linkedSetup.Forms[GasFuturesReconciliationActions.Action].Fields.Single().Variants!.Value;
        if(variants.Select(v=>v.Label).Distinct().Count()!=2||!variants[0].Label.Contains("TR-000101",StringComparison.Ordinal)||
            !variants[1].Label.Contains("TR-000102",StringComparison.Ordinal)||variants.Any(v=>!v.Label.Contains("synthetic_monthly_future",StringComparison.Ordinal)))
            throw new Exception("Identical performance references on different physical parents require readable exact originating trades and delivery contracts.");
        Console.WriteLine("PASS typed gas form source: exact source and financial revisions, explicit physical correction choices, decimal text and user clocks");
    }
}
