using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

// A European call and an American call on the same synthetic cash-settled future, read from an owned disposable
// database (tools/check_european_option.py): the catalog states the style, each series is calibrated by its own model
// under the stated policy, the European holding is worth its settlement, and a policy without the European member
// refuses the book.
internal static class EuropeanOptionReadChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(RiskJobs.Json)!;var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("European option checks require their owned disposable cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        await database.WithIdentity("trader","RunScenario",async(connection,ct)=>
        {
            var context=(await new KnowledgeResolver(manifest.CatalogId).Read(connection,fixture.Id("contextId"),ct)).Context;
            await PinnedContext.BindExact(connection,manifest.CatalogId,context,ct);await TradingContext.Bind(connection,manifest.CatalogId,context,fixture.Instant("at"),ct);
            var pins=fixture.GetProperty("pins").EnumerateArray().Select(p=>p.GetGuid()).ToImmutableArray();
            ImmutableArray<Guid> Contracts(string name)=>[..fixture.GetProperty(name).EnumerateArray().Select(c=>c.GetGuid())];
            var curves=new[]{"ice_cfb","synthetic_cash_option","synthetic_european_option"};
            var contracts=ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add(curves[0],Contracts("futureContracts"))
                .Add(curves[1],Contracts("americanContracts")).Add(curves[2],Contracts("europeanContracts"));
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic European option",fixture.Instant("at"),PublishedSchedules.ContextDate(context),pins,contracts,context.ContextId){
                Source=new(manifest.RevisionId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId)};
            var data=await MarketInputSelection.Root(database,connection,frame,ct);
            frame=frame with{SourceInputs=ImmutableDictionary<Guid,MarketSourceInputs>.Empty.Add(manifest.RevisionId,new(CatalogueSource.From(data),pins,[],contracts,[..curves]))};
            var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));

            var european=fixture.GetProperty("european");var american=fixture.GetProperty("american");
            var listed=JsonSerializer.SerializeToElement(FuturesOptionStore.Series(data,frame),RiskJobs.Json).EnumerateArray().ToDictionary(s=>s.Text("series"));
            if(listed[european.Text("series")].Text("exerciseStyle")!=FuturesOptionTerms.European||listed[american.Text("series")].Text("exerciseStyle")!=FuturesOptionTerms.American||
                listed[european.Text("series")].Text("settlementMethod")!="exercise_into_future"||listed[european.Text("series")].GetProperty("contraryInstructionsAllowed").GetBoolean())
                throw new Exception("The catalog must state each series' own exercise style and settlement method from its product row.");

            var books=ImmutableArray.Create(fixture.Id("bookId"));var trades=ImmutableArray.Create(european.Id("tradeId"),american.Id("tradeId"));
            var scope=await RiskTradeScope.Resolve(connection,manifest.CatalogId,books,trades,ct);
            var stated=PortfolioRiskPreview.AccountingPricingPolicy;
            var prepared=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,context,books,stated,[],[],ct,selection:scope);
            if(!prepared.Excluded.IsEmpty)throw new Exception($"Every held series must be valued; left out: {string.Join(", ",prepared.Excluded.Select(e=>e.Reason))}.");
            var receipts=prepared.OptionCalibrations.ToDictionary(r=>r.Series.Code);
            var (formula,tree)=(receipts[european.Text("series")],receipts[american.Text("series")]);
            if(formula.NumericalPolicy!=OptionNumericalPolicies.Black76||tree.NumericalPolicy!=OptionNumericalPolicies.CoxRossRubinstein||
                formula.Mode!=FuturesOptionPricing.SolvedMode||tree.Mode!=FuturesOptionPricing.SolvedMode)
                throw new Exception("Under one stated policy the European series is calibrated by the formula and the American series on the tree.");

            // The base value equals the settlement within the calibration's half increment: settlement × multiplier × lots.
            var report=PortfolioValuation.Calculate(prepared,[],[],RiskDetail.Holdings,ct);
            var (settlement,multiplier,lots,increment)=(ExactDecimal.Parse(european.Text("settlement")),ExactDecimal.Parse(european.Text("multiplier")),
                ExactDecimal.Parse(european.Text("lots")),ExactDecimal.Parse(european.Text("clearingIncrement")));
            var value=report.BasePositions.Single(p=>p.Position.BindingId==european.Id("bindingId")).Position.Value;
            var expected=ExactMoney.Parse((settlement*multiplier*lots).ToString(),value.Currency);
            var bound=ExactMoney.Parse((increment*multiplier*lots).ToString(),value.Currency).DivideBy(ExactDecimal.Parse("2"));
            if(value.Subtract(expected).CompareTo(bound)>0||expected.Subtract(value).CompareTo(bound)>0)
                throw new Exception($"The European holding is worth {value.ToExactString()}, not its settlement value {expected.ToExactString()} within {bound.ToExactString()}.");
            if(report.ModelContracts.Single(c=>c.Id==formula.InstrumentId) is not {ModelVersion:OptionNumericalPolicies.Black76,PriceSteps.IsEmpty:true})
                throw new Exception("A European option names the formula as its model and declares no price step.");

            // The lifecycle list refuses the European holding's exercise by its style before anything is configured for it;
            // its expiry, and the American holding's exercise, wait for configuration like any other action.
            var actions=new OptionLifecycleActions(database,_=>{});var read=new LifecycleReadContext(context.ContextId!.Value,fixture.Instant("at"),PublishedSchedules.ContextDate(context));
            async Task<ImmutableDictionary<string,LifecycleActionOffer>> Offers(JsonElement held)=>(await actions.Discover(connection,read,
                new("listed_option",held.Id("tradeId"),held.Id("bindingId")),new(ImmutableHashSet<string>.Empty),ct)).ToImmutableDictionary(o=>o.Action);
            var (offered,control)=(await Offers(european),await Offers(american));
            if(offered["option.exercise"] is not {Eligible:false,Reason:"option_exercise_style"}||offered["option.expiry"].Reason!="lifecycle_configuration_required"||
                control["option.exercise"].Reason!="lifecycle_configuration_required")
                throw new Exception("The lifecycle list must give a European holding's exercise its own reason, and no other action that reason.");

            // The policy stated before European series were admitted names no model for one: the book is refused, by series.
            var earlier=stated with{FuturesOptions=FuturesOptionValuationPolicy.Release1};
            try{_=await new PortfolioValuation(database).Prepare(connection,data,frame,snapshot,context,books,earlier,[],[],ct,selection:scope);
                throw new Exception("A book holding a European series was prepared under a policy without the European member.");}
            catch(MarketDataException error)when(error.Code=="option_policy"&&error.Message.Contains(european.Text("series"),StringComparison.Ordinal)){}
            return 0;
        },default);
        Console.WriteLine("PASS European option read: the catalog states the style and settlement, a European and an American series on one future are each calibrated by their own model, the European holding is worth its settlement, the lifecycle list refuses its exercise by style and a policy without the European member refuses the book");
    }
}
