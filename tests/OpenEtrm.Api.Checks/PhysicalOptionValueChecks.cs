using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class PhysicalOptionValueChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(RiskJobs.Json)!;var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Physical option checks require their owned disposable cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        Func<NpgsqlConnection,CancellationToken,Task>? readonlyPreparation=null,readonlyRiskPreparation=null,readonlySetup=null,submitProof=null;
        var finiteGasRole=Guid.NewGuid();
        await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,fixture.Id("contextId"),ct)).Context;
            var pins=fixture.GetProperty("pins");var primary=manifest.RevisionId;var secondary=fixture.Id("secondaryRevision");
            ImmutableArray<Guid> Selected(Guid revision)=>[..pins.GetProperty(revision.ToString()).EnumerateArray().Select(p=>p.GetGuid())];
            var contracts=ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("nymex_ng",[fixture.Id("contractId")]).Add("nymex_on",[fixture.Id("optionContractId")]);
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic two-source physical option",fixture.Instant("at"),PublishedSchedules.ContextDate(context),Selected(primary),contracts,context.ContextId);
            await PinnedContext.BindExact(connection,manifest.CatalogId,context,ct);await TradingContext.Bind(connection,manifest.CatalogId,context,frame.At,ct);
            var data=await database.Read(connection,ct,selectedRevision:primary,observationIds:Selected(primary),includeCalendarDays:false,observationScope:ObservationReadScope.ExactPins);
            var foreign=await database.Read(connection,ct,selectedRevision:secondary,observationIds:Selected(secondary),includeCalendarDays:false,observationScope:ObservationReadScope.ExactPins);
            var own=new MarketSourceInputs(CatalogueSource.From(data),Selected(primary),[],contracts,["nymex_ng","nymex_on"]);
            var other=new MarketSourceInputs(CatalogueSource.From(foreign),Selected(secondary),[],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("synthetic_inventory",[fixture.Id("markContractId")]),["synthetic_inventory"]);
            frame=frame with{Source=own.Source,SourceInputs=ImmutableDictionary<Guid,MarketSourceInputs>.Empty.Add(primary,own).Add(secondary,other)};
            var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
            var books=ImmutableArray.Create(fixture.Id("bookId"));var trades=ImmutableArray.Create(fixture.Id("tradeId"));
            var scope=await RiskTradeScope.Resolve(connection,manifest.CatalogId,books,trades,ct);
            var model=new PhysicalOptionPolicy(PhysicalOptionPolicy.Version,PhysicalOptionPolicy.FrozenBasis,PhysicalOptionPolicy.ReferenceStopping,
                PhysicalOptionPolicy.ReferenceCalibration,PhysicalOptionPolicy.IntrinsicStrategy);
            var selection=new FuturesOptionPhysicalValuationSelection(fixture.Id("tradeId"),fixture.Id("bindingId"),model,FuturesPhysicalBasisValueBinding.ValuationProxy);
            var policy=PortfolioRiskPreview.AccountingPricingPolicy with{FuturesOptions=FuturesOptionValuationPolicy.Release1 with{
                Physical=new(FuturesOptionPhysicalValuationPolicy.Version,[selection])}};
            readonlyPreparation=async(other,token)=>{_=await new PortfolioValuation(database).Prepare(other,data,frame,snapshot,context,books,policy,[],[],token,selection:scope);};
            var prepared=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,context,books,policy,[],[],ct,selection:scope);
            var selected=prepared.Sources.Single().Portfolio.Setup.PhysicalOptionValueBindings;
            if(selected.Count!=1||!selected.ContainsKey(new(fixture.Id("tradeId"),fixture.Id("bindingId"))))
                throw new Exception("An explicit physical option selection must reach its exact real held pricing binding instead of vanilla valuation.");
            if(prepared.Positions.Length!=2||prepared.Markets.Length!=2||prepared.Factors.Length!=2||prepared.InputMarkets.Length!=1)
                throw new Exception("Physical option value must retain its original right and premium, plus the exact input-only delivery source.");
            var report=PortfolioValuation.Calculate(prepared,[],[],RiskDetail.Holdings,ct);
            var declaration=report.ModelContracts.Single(c=>c.Id==fixture.Id("bindingId"));
            if(declaration.ModelVersion!=PhysicalOptionPolicy.Version||declaration.StaticDerivatives is null)
                throw new Exception("The selected physical model must retain its declared pricing and derivative capability.");
            var assetFactor=prepared.Factors.Single(f=>f.Input.Series.Revision.RevisionId==secondary);
            var axis=CurveRiskAxes.Quote(prepared,assetFactor,ct);
            var shifted=PortfolioValuation.Calculate(prepared,[],[new(axis,ExactDecimal.Parse("0.1"))],RiskDetail.Holdings,ct);
            if(shifted.Totals.Single().Value==report.Totals.Single().Value)
                throw new Exception("The native delivery-mark scenario must affect the selected physical option value.");
            void RefuseDerivatives()
            {
                try{PortfolioValuation.Calculate(prepared,[new(axis,ExactDecimal.Parse("0.1"))],[],RiskDetail.Holdings,ct);}
                catch(MarketDataException error)when(error.Code=="risk_static_derivatives_unavailable"){return;}
                throw new Exception("A full-PV physical option consumer cannot silently claim unqualified static derivatives.");
            }
            RefuseDerivatives();
            async Task Refuse(string code,InputFrame selectedFrame,PortfolioValuationPolicy selectedPolicy)
            {
                try{await new PortfolioValuation(database).Prepare(connection,data,selectedFrame,snapshot,context,books,selectedPolicy,[],[],ct,selection:scope);}
                catch(MarketDataException error)when(error.Code==code){return;}
                throw new Exception("Selected physical option preparation must refuse "+code+".");
            }
            await Refuse("market_inputs_missing",frame with{SourceInputs=frame.SourceInputs!.Remove(secondary)},policy);
            await Refuse("physical_option_source",frame,policy with{FuturesOptions=policy.FuturesOptions! with{Physical=new(FuturesOptionPhysicalValuationPolicy.Version,[])}});
            await Refuse("physical_mark_interpretation",frame,policy with{FuturesOptions=policy.FuturesOptions! with{
                Physical=new(FuturesOptionPhysicalValuationPolicy.Version,[selection with{MarkInterpretation=FuturesPhysicalBasisValueBinding.SameDeliverable}])}});
            var vanilla=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,context,books,policy with{
                FuturesOptions=policy.FuturesOptions! with{Physical=null}},[],[],ct,selection:scope);
            if(!vanilla.Sources.Single().Portfolio.Setup.PhysicalOptionValueBindings.IsEmpty||vanilla.Markets.Length!=1)
                throw new Exception("Legacy explicit vanilla reference valuation cannot inherit the physical source or selected model.");
            var scopeRequest=new PortfolioRiskCatalogRequest(new(frame.Id,TradingBlotter.Convention,"retained",context.ContextId),books,[],trades);
            readonlySetup=async(other,token)=>
            {
                await PinnedContext.BindExact(other,manifest.CatalogId,context,token);
                await TradingContext.Bind(other,manifest.CatalogId,context,frame.At,token);
                var visible=JsonSerializer.SerializeToElement(await new PortfolioRiskPreview(database).EconomicSetup(other,data,frame,scopeRequest,token),RiskJobs.Json);
                PhysicalOptionPolicyChecks.RequireBindingFlags(visible);
                if(visible.GetProperty("physicalBindings").GetProperty("options").GetArrayLength()!=0||
                    visible.GetProperty("pricing").EnumerateArray().Any(p=>p.GetProperty("requiresOptionBindings").GetBoolean()))
                    throw new Exception("Setup without initial Gas access must retain base choices without disclosing physical binding metadata.");
            };
            var named=new PortfolioJointRiskRequest(PortfolioJointRiskRequest.Version1,
                new("synthetic_asset_shift","1",ConnectedRiskScenarioSet.Independent,[new("asset","Independent asset mark",[new(axis.Request,"0.1",CurveShift.ParentAdditive)])]),null);
            readonlyRiskPreparation=async(other,token)=>
            {
                await PinnedContext.BindExact(other,manifest.CatalogId,context,token);
                await TradingContext.Bind(other,manifest.CatalogId,context,frame.At,token);
                _=await new PortfolioRiskPreview(database).Prepare(other,data,frame,
                    new(scopeRequest.Context,books,[],[],PortfolioRiskPreview.Convention,RiskEngine.ExactNumericalPolicy,policy,"holdings",[],trades,Joint:named),token);
            };
            var setup=JsonSerializer.SerializeToElement(await new PortfolioRiskPreview(database).EconomicSetup(connection,data,frame,scopeRequest,ct),RiskJobs.Json);
            PhysicalOptionPolicyChecks.RequireBindingFlags(setup);
            var optionGroup=setup.GetProperty("physicalBindings").GetProperty("options").EnumerateArray().Single();
            var offered=optionGroup.GetProperty("choices").EnumerateArray().Single().GetProperty("selection").Deserialize<FuturesOptionPhysicalValuationSelection>(RiskJobs.Json)!;
            if(setup.GetProperty("physicalBindings").GetProperty("futures").GetArrayLength()!=0||offered!=selection||
                !setup.GetProperty("pricing").EnumerateArray().Any(p=>p.TryGetProperty("requiresOptionBindings",out var required)&&required.GetBoolean()&&
                    p.GetProperty("policy").GetProperty("futuresOptions").GetProperty("physical").GetProperty("options").GetArrayLength()==0))
                throw new Exception("Setup must retain exact live physical option selection and explicit empty full-model templates without calibrating a price.");
            submitProof=async(current,token)=>
            {
                await PinnedContext.BindExact(current,manifest.CatalogId,context,token);
                await TradingContext.Bind(current,manifest.CatalogId,context,frame.At,token);
                var fresh=await database.Read(current,token,selectedRevision:primary,observationIds:Selected(primary),includeCalendarDays:false,observationScope:ObservationReadScope.ExactPins);
                await PhysicalOptionWorkerChecks.Submit(database,current,fresh,frame,scopeRequest,policy,path,token);
            };
            Console.WriteLine("PASS actual ordinary physical option: exact native option/profile and independent asset source, PV, named asset scenario, static derivative refusal and explicit complete scope");
            return true;
        },default);
        await database.WithIdentity("viewer",null,async(connection,ct)=>{await readonlySetup!(connection,ct);return true;},default);
        try
        {
            await database.WithIdentity("viewer",null,async(connection,ct)=>{await readonlyPreparation!(connection,ct);return true;},default);
            throw new Exception("A physical option request succeeded without its original Gas read authority.");
        }
        catch(PreviewDeniedException){}
        await using(var owner=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_owner"}.ConnectionString))
        {
            await owner.OpenAsync();var role=finiteGasRole;
            await using var grants=ScheduleStore.Command(owner,"WITH created AS(INSERT INTO security.roles VALUES($1,'synthetic_option_finite_gas','Synthetic finite option Gas source reader') RETURNING id), "+
                "permission AS(INSERT INTO security.role_permissions SELECT id,'ReadGasOwnedHoldings' FROM created RETURNING role_id), "+
                "clocks AS MATERIALIZED(SELECT clock_timestamp() AS at), people AS MATERIALIZED(SELECT principal_id FROM security.identity_bindings WHERE issuer=$4 AND subject='viewer') "+
                "INSERT INTO security.role_grants SELECT gen_random_uuid(),people.principal_id,permission.role_id,$2,$3,clocks.at,clocks.at+interval '3 seconds',false "+
                "FROM permission CROSS JOIN people CROSS JOIN clocks UNION ALL SELECT gen_random_uuid(),people.principal_id,permission.role_id,$2,$3,clocks.at+interval '4 seconds',TIMESTAMPTZ '9999-12-31',false "+
                "FROM permission CROSS JOIN people CROSS JOIN clocks",role,manifest.TenantId,manifest.WorkspaceId,manifest.IdentityIssuer);
            await grants.ExecuteNonQueryAsync();
        }
        try
        {
            await database.WithIdentity("viewer",null,async(connection,ct)=>
            {
                await FuturesOptionPhysicalPermissionAdmission.Capture(connection,database,ct);
                await Task.Delay(TimeSpan.FromSeconds(4.5),ct);
                if(!await PreviewDatabase.HasPermission(connection,"ReadGasOwnedHoldings",ct))
                    throw new Exception("The original-deadline counterexample must have a positive current successor permission.");
                return true;
            },default);
            throw new Exception("A successor grant renewed the original physical option read admission.");
        }
        catch(PostgresException error)when(error.SqlState=="42501"&&error.MessageText=="physical_valuation_read_denied"){}
        await BlockedRiskRead(database,login,manifest,finiteGasRole,readonlyRiskPreparation!);
        await database.WithIdentity("trader",null,async(connection,ct)=>{await submitProof!(connection,ct);return true;},default);
        Console.WriteLine("PASS physical option original authority: missing permission refuses actual preparation; prepublished successor cannot renew the committed read admission");
    }
    private static async Task BlockedRiskRead(PreviewDatabase database,NpgsqlConnectionStringBuilder login,PreviewManifest manifest,Guid role,
        Func<NpgsqlConnection,CancellationToken,Task> prepare)
    {
        await using var owner=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_owner"}.ConnectionString);
        await owner.OpenAsync();
        await using(var renew=ScheduleStore.Command(owner,"WITH retired AS(UPDATE security.role_grants SET revoked=true WHERE role_id=$1 RETURNING role_id), "+
            "selected AS(SELECT DISTINCT role_id FROM retired), clocks AS MATERIALIZED(SELECT clock_timestamp() AS at), "+
            "people AS MATERIALIZED(SELECT principal_id FROM security.identity_bindings WHERE issuer=$4 AND subject='viewer') "+
            "INSERT INTO security.role_grants SELECT gen_random_uuid(),people.principal_id,selected.role_id,$2,$3,clocks.at,clocks.at+interval '3 seconds',false "+
            "FROM selected CROSS JOIN people CROSS JOIN clocks UNION ALL SELECT gen_random_uuid(),people.principal_id,selected.role_id,$2,$3,clocks.at+interval '4 seconds',TIMESTAMPTZ '9999-12-31',false "+
            "FROM selected CROSS JOIN people CROSS JOIN clocks",role,manifest.TenantId,manifest.WorkspaceId,manifest.IdentityIssuer))await renew.ExecuteNonQueryAsync();
        var started=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read=database.WithIdentity("viewer",null,async(connection,ct)=>{started.SetResult(connection.ProcessID);await resume.Task;await prepare(connection,ct);return true;},default);
        if(await Task.WhenAny(started.Task,read)!=started.Task){await read;throw new Exception("The risk source probe did not enter its owned request.");}
        await using var held=await owner.BeginTransactionAsync();
        await using(var block=ScheduleStore.Command(owner,"LOCK TABLE trading.books IN ACCESS EXCLUSIVE MODE"))await block.ExecuteNonQueryAsync();
        resume.SetResult(true);
        var backend=await started.Task;var blocked=false;
        for(var attempt=0;attempt<40&&!blocked;attempt++)
        {
            await using(var refresh=ScheduleStore.Command(owner,"SELECT pg_stat_clear_snapshot()"))await refresh.ExecuteNonQueryAsync();
            await using var status=ScheduleStore.Command(owner,"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=$1 AND wait_event_type='Lock' AND query LIKE '%FROM trading.books b JOIN trading.portfolios%')",backend);
            blocked=Equals(await status.ExecuteScalarAsync(),true);if(!blocked)await Task.Delay(50);
        }
        if(!blocked){await held.RollbackAsync();await read;throw new Exception("The original Gas deadline probe must block the actual pre-portfolio book query.");}
        await Task.Delay(TimeSpan.FromSeconds(4.5));await held.CommitAsync();
        await using(var successor=ScheduleStore.Command(owner,"SELECT security.principal_has_permission(principal_id,$2,$3,'ReadGasOwnedHoldings',clock_timestamp()) "+
            "FROM security.identity_bindings WHERE issuer=$1 AND subject='viewer'",manifest.IdentityIssuer,manifest.TenantId,manifest.WorkspaceId))
            if(!Equals(await successor.ExecuteScalarAsync(),true))throw new Exception("The blocked source probe requires a positive current successor Gas grant.");
        try{await read;throw new Exception("A successor Gas grant renewed risk preparation after its initial book query waited.");}
        catch(PostgresException error)when(error.SqlState=="42501"&&error.MessageText=="physical_valuation_read_denied"){}
        Console.WriteLine("PASS selected option risk entry: original Gas admission precedes blocked book read and a prepublished successor cannot renew it");
    }
}
