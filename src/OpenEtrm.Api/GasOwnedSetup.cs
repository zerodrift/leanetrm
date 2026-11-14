using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

internal sealed record GasFormChoice(string Id,string Label,object Value);
internal sealed record GasFormField(string Name,string Label,string Kind,bool Nullable,
    ImmutableArray<GasFormChoice> Choices,ImmutableArray<GasFormField> Fields,ImmutableArray<string> Members);
internal sealed record GasForm(string Label,string Permission,ImmutableArray<GasFormField> Fields);
internal sealed record GasFormPolicy(string Id,string Label,int Revision,string Action,string AccountId);
internal sealed record GasSetup(LifecycleReadContext Context,ImmutableArray<GasFormChoice> Accounts,ImmutableArray<GasFormPolicy> Policies,
    ImmutableDictionary<string,GasForm> Forms,ImmutableDictionary<string,GasForm> Configuration);

// Presentation metadata for the closed, native gas operations. Choices retain
// exact foreign-key tuples; financial interpretation stays in native producers.
internal static class GasOwnedSetup
{
    internal static async Task<GasSetup> Read(NpgsqlConnection connection,PreviewDatabase database,LifecycleDiscoveryRequest request,CancellationToken ct)
    {
        if(request.Context is null)throw GasOwnedOperations.Error("Select an exact certified gas source.");
        var catalog=database.Manifest.CatalogId;
        await using var capture=ScheduleStore.Command(connection,"SELECT security.gas_futures_delivery_discovery_authority($1)",catalog);
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        var admission=new GasOwnedOperationReadAdmission(catalog,until);database.RegisterCommitAdmission(connection,admission);
        var source=await LifecycleNative.Source(connection,database,request.Context,ct);
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT gas_form_source"))await save.ExecuteNonQueryAsync(ct);
        try
        {
            await PinnedContext.BindExact(connection,catalog,source,ct);
            await TradingContext.Bind(connection,catalog,source,request.Context.AsOf,ct);
            var state=request.Target?.Handler==GasFuturesDeliveryActions.Handler?
                await LifecycleNative.Read(connection,"SELECT security.read_gas_futures_delivery_actions($1,$2,$3)::text",ct,catalog,request.Context.ContextId,request.Context.AsOf):
                await GasOwnedOperations.Read(connection,database,request.Context,null,ct);
            var bookIds=state.GetProperty("accounts").EnumerateArray().Select(a=>a.Id("book_id")).Distinct().ToArray();
            var known=bookIds.Length==0?state:await LifecycleNative.Read(connection,"SELECT security.read_gas_owned_known_history($1,$2,$3,$4::uuid[])::text",ct,catalog,request.Context.ContextId,request.Context.AsOf,bookIds);
            ImmutableArray<JsonElement> profiles=[];
            if(bookIds.Length!=0)
            {
                var configured=await LifecycleNative.Read(connection,"SELECT security.read_gas_owned_projection_choices($1,$2,$3,$4::uuid[])::text",ct,catalog,request.Context.ContextId,request.Context.AsOf,bookIds);
                profiles=[..configured.GetProperty("accounts").EnumerateArray().SelectMany(a=>a.GetProperty("profiles").EnumerateArray().Select(p=>
                    JsonSerializer.SerializeToElement(new{profileId=p.Id("profileId"),revision=Int(p,"revision"),accountId=a.Id("accountId"),accountCode=a.Text("code")})))];
            }
            var catalogue=await new CatalogueStore(catalog).ReadBound(connection,ct,request.Context.BusinessDate);
            var selected=catalogue.Curves.Select(c=>new{definition=c.DefinitionRevisionId,curve=c.Code})
                .Concat(state.GetProperty("accounts").EnumerateArray().Select(a=>new{definition=a.Id("definition_revision_id"),curve=a.Text("mark_curve_code")}))
                .Concat(known.TryGetProperty("markBindings",out var knownMarks)?knownMarks.EnumerateArray().Select(m=>new{definition=m.Id("definition_revision_id"),curve=m.Text("mark_curve_code")}):[]).Distinct().ToArray();
            var marks=await PreviewDatabase.Rows(connection,"""
                SELECT jsonb_build_object('definitionRevisionId',c.revision_id,'markCurve',c.code,'markContractId',m.contract_id,
                 'referenceCatalogId',c.reference_catalog_id,'referenceRevisionId',c.reference_revision_id,'quantity',q.quantity_code,
                 'location',c.location_code,'currency',q.currency_code,'label',c.name||' · '||k.delivery_from::text||' to '||k.delivery_to::text||' · '||q.quantity_code||' · '||q.currency_code)::text
                FROM jsonb_to_recordset($2::jsonb)s(definition uuid,curve text)
                JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=($1,s.definition,s.curve)
                JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code)
                JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
                JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
                WHERE q.kind='outright' ORDER BY c.name,k.delivery_from,k.delivery_to,c.revision_id,m.contract_id
                """,[catalog,JsonSerializer.Serialize(selected)],ct);
            var books=await PreviewDatabase.Rows(connection,"SELECT row_to_json(b)::text FROM trading.books b WHERE b.catalog_id=$1 ORDER BY b.name,b.id",[catalog],ct);
            var policies=await PreviewDatabase.Rows(connection,"SELECT row_to_json(p)::text FROM lifecycle.action_policies p WHERE p.catalog_id=$1 ORDER BY p.id,p.revision",[catalog],ct);
            var entities=await TradeParties.Catalog(connection,catalog,true,ct);var counterparties=await TradeParties.Catalog(connection,catalog,false,ct);
            var references=marks.Select(m=>new{catalog=m.Id("referenceCatalogId"),revision=m.Id("referenceRevisionId")}).Distinct().ToArray();
            var sources=await PreviewDatabase.Rows(connection,"SELECT row_to_json(v)::text FROM(SELECT DISTINCT s.code,replace(s.code,'_',' ') name FROM jsonb_to_recordset($1::jsonb)r(catalog uuid,revision uuid) JOIN ref_data.sources s ON(s.catalog_id,s.revision_id)=(r.catalog,r.revision) ORDER BY name,s.code)v",
                [JsonSerializer.Serialize(references)],ct);
            var observations=await PreviewDatabase.Rows(connection,"""
                WITH candidates AS MATERIALIZED(SELECT DISTINCT o.* FROM jsonb_to_recordset($2::jsonb)t(position_id uuid,source_code text,role text,observed_at timestamptz)
                 JOIN jsonb_to_recordset($3::jsonb)p(id uuid,account_id uuid,mark_curve_code text,mark_contract_id uuid) ON p.id=t.position_id
                 JOIN jsonb_to_recordset($4::jsonb)a(id uuid,definition_revision_id uuid) ON a.id=p.account_id
                 JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=($1,a.definition_revision_id,p.mark_curve_code)
                 JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.contract_id,s.quote_code)=($1,a.definition_revision_id,p.mark_contract_id,c.quote_code)
                 JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.series_id,o.source_code,o.role,o.observed_at)=
                  ($1,a.definition_revision_id,s.id,t.source_code,t.role,t.observed_at)
                 WHERE o.observed_at<=$5),
                heads AS(SELECT o.* FROM candidates o WHERE NOT EXISTS(SELECT 1 FROM market_data.observations n WHERE(n.catalog_id,n.corrects_id)=(o.catalog_id,o.id))),
                counted AS(SELECT h.*,count(*) OVER(PARTITION BY catalog_id,definition_revision_id,series_id,source_code,role,observed_at) head_count FROM heads h)
                SELECT row_to_json(o)::text FROM counted o WHERE head_count=1 AND publication_state='final' ORDER BY observed_at,id
                """,[catalog,state.GetProperty("cashoutTerms").GetRawText(),state.GetProperty("positions").GetRawText(),state.GetProperty("accounts").GetRawText(),request.Context.AsOf],ct);
            var value=Build(request.Context,request.Target,state,policies,marks,books,entities,counterparties,sources,observations,known,profiles);
            await admission.RequireCommit(connection,ct);return value;
        }
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT gas_form_source;RELEASE SAVEPOINT gas_form_source");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
    internal static GasSetup Build(LifecycleReadContext context,LifecycleTarget? target,JsonElement state,ImmutableArray<JsonElement> policies,
        ImmutableArray<JsonElement> marks,ImmutableArray<JsonElement> books,ImmutableArray<TradeParties.Choice> entities,
        ImmutableArray<TradeParties.Choice> counterparties,ImmutableArray<JsonElement> sources,ImmutableArray<JsonElement> observations,JsonElement known,
        ImmutableArray<JsonElement> profiles)
    {
        JsonElement[] Rows(string part)=>state.GetProperty(part).EnumerateArray().ToArray();
        var accounts=Rows("accounts");var positions=Rows("positions");
        var knownPositions=known.GetProperty("positions").EnumerateArray().Where(p=>accounts.Any(a=>a.Id("id")==p.Id("account_id"))).ToArray();
        var allEvidence=Rows("evidence").Concat(known.GetProperty("evidence").EnumerateArray()).DistinctBy(e=>e.Id("id")).ToArray();
        Guid? accountId=target?.Handler switch{
            GasOwnedOperations.Handler=>target.ScopeId,
            GasFuturesDeliveryActions.Handler=>positions.FirstOrDefault(p=>p.Id("id")==target.ScopeId) is {ValueKind:JsonValueKind.Object} physical?physical.Id("account_id"):null,
            null=>null,_=>throw GasOwnedOperations.Error("Select a native gas lifecycle target.")};
        if(target is not null&&(accountId is null||!accounts.Any(a=>a.Id("id")==accountId)))
            throw GasOwnedOperations.Error("The selected gas holding is unavailable at this source.");
        var heads=policies.GroupBy(p=>p.Id("id")).Select(g=>g.MaxBy(p=>Int(p,"revision"))).ToArray();
        var evidence=Rows("evidence").GroupBy(e=>(e.Id("account_id"),e.Text("source_key")))
            .Select(g=>g.MaxBy(e=>Int(e,"revision"))).Where(e=>e.Instant("occurred_at")<=context.AsOf).ToArray();
        var bindingHeads=Rows("policies").GroupBy(p=>(p.Id("account_id"),p.Text("action")))
            .Select(g=>g.MaxBy(p=>Int(p,"revision"))).Where(p=>heads.Any(h=>h.Id("id")==p.Id("policy_id")&&Int(h,"revision")==Int(p,"policy_revision"))).ToArray();
        var choices=ImmutableArray.CreateBuilder<GasFormPolicy>();
        foreach(var binding in bindingHeads.Where(p=>accountId is null||p.Id("account_id")==accountId))
        {
            var policy=heads.Single(p=>p.Id("id")==binding.Id("policy_id"));
            choices.Add(new(policy.Id("id").ToString(),$"{Words(policy.Text("action"))} · {Words(policy.Text("mode"))} · revision {Int(policy,"revision")}",
                Int(policy,"revision"),policy.Text("action"),(target?.Handler==GasFuturesDeliveryActions.Handler?target.ScopeId:binding.Id("account_id")).ToString()));
        }
        string Account(Guid id)=>accounts.Single(a=>a.Id("id")==id).Text("code");
        string Position(JsonElement p)
        {
            var source=evidence.FirstOrDefault(e=>e.Id("id")==p.Id("evidence_id"));
            // Original applied evidence remains a label even when superseded.
            if(source.ValueKind==JsonValueKind.Undefined)source=allEvidence.Single(e=>e.Id("id")==p.Id("evidence_id"));
            return $"{Account(p.Id("account_id"))} · {Words(p.Text("kind"))} · {source.Text("source_key")}";
        }
        ImmutableArray<GasFormChoice> Ids(IEnumerable<JsonElement> rows,Func<JsonElement,string> label)=>[..rows.Select(r=>Choice(r.Id("id").ToString(),label(r),r.Id("id")))];
        var positionChoices=Ids(positions.Where(p=>(accountId is null||p.Id("account_id")==accountId)&&
            (target?.Handler!=GasFuturesDeliveryActions.Handler||p.Id("id")==target.ScopeId)),Position);
        var futurePositionChoices=Ids(knownPositions.Where(p=>accountId is null||p.Id("account_id")==accountId),p=>Position(p)+" · effective "+p.Text("effective_at"));
        var knownLots=Ids(known.GetProperty("lots").EnumerateArray().Where(l=>knownPositions.Any(p=>p.Id("id")==l.Id("id"))),l=>Position(knownPositions.Single(p=>p.Id("id")==l.Id("id"))));
        var lotChoices=Ids(Rows("lots"),l=>Position(positions.Single(p=>p.Id("id")==l.Id("id"))));
        var reservationChoices=Ids(Rows("reservations"),r=>$"{Position(positions.Single(p=>p.Id("id")==r.Id("lot_id")))} · reservation {r.Text("business_key")}");
        var accountChoices=Ids(accounts,a=>$"{a.Text("code")} · {Words(a.Text("kind"))} · {a.Text("book_name")}");
        var markMembers=new[]{"definitionRevisionId","markCurve","markContractId"};
        var accountMarkMembers=new[]{"referenceCatalogId","referenceRevisionId","quantity","location","definitionRevisionId","markCurve","markContractId","currency"};
        ImmutableArray<GasFormChoice> Marks(string[] members)=>[..marks.Select((m,i)=>Choice(i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            m.Text("label"),members.ToDictionary(k=>k,k=>m.GetProperty(k).Clone(),StringComparer.Ordinal)))];
        ImmutableArray<GasFormChoice> Parties(ImmutableArray<TradeParties.Choice> values,string prefix)=>[..values.Select(p=>Choice(p.Id+":"+p.Revision,
            $"{p.Code} · {p.Name} · revision {p.Revision}",new Dictionary<string,object>{{prefix+"Id",p.Id},{prefix+"Revision",p.Revision}}))];
        var newProfile=Guid.NewGuid();
        ImmutableArray<GasFormField> Fields(Type type,string operation)
        {
            var result=ImmutableArray.CreateBuilder<GasFormField>();var covered=new HashSet<string>(StringComparer.Ordinal);
            void Add(GasFormField field){result.Add(field);foreach(var member in field.Kind=="reference"?field.Members:[field.Name])covered.Add(member);}
            GasFormField Ref(string name,string label,bool nullable,string[] members,ImmutableArray<GasFormChoice> values)=>new(name,label,"reference",nullable,values,[],[..members]);
            if(type==typeof(GasOwnedAccountSelection))Add(Ref("market","Inventory mark and units",false,accountMarkMembers,Marks(accountMarkMembers)));
            else if(type.GetProperty("DefinitionRevisionId") is not null)Add(Ref("mark","Exact inventory mark",false,markMembers,Marks(markMembers)));
            if(type==typeof(GasOwnedCashoutProjectionSelection))Add(Ref("scenarioMark","Selected cashout expectation market",false,
                ["scenarioDefinitionRevisionId","scenarioCurve","scenarioContractId"],[..marks.Select((m,i)=>Choice(i.ToString(System.Globalization.CultureInfo.InvariantCulture),m.Text("label"),
                    new{scenarioDefinitionRevisionId=m.Id("definitionRevisionId"),scenarioCurve=m.Text("markCurve"),scenarioContractId=m.Id("markContractId")}))]));
            if(type.GetProperty("LegalEntityId") is not null)Add(Ref("legalEntity","Legal entity",false,["legalEntityId","legalEntityRevision"],Parties(entities,"legalEntity")));
            if(type.GetProperty("CounterpartyId") is not null)Add(Ref("counterparty","Counterparty",true,["counterpartyId","counterpartyRevision"],Parties(counterparties,"counterparty")));
            if(type.GetProperty("LenderId") is not null)Add(Ref("lender","Lender",false,["lenderId","lenderRevision"],Parties(counterparties,"lender")));
            if(type==typeof(GasOwnedPolicySelection))Add(Ref("policy","Saved action policy",false,["action","policyId","policyRevision"],
                [..heads.Where(p=>p.Text("action").StartsWith("gas.",StringComparison.Ordinal)).Select(p=>Choice(p.Id("id")+":"+Int(p,"revision"),
                    $"{Words(p.Text("action"))} · {Words(p.Text("mode"))} · revision {Int(p,"revision")}",new{action=p.Text("action"),policyId=p.Id("id"),policyRevision=Int(p,"revision")}))]));
            if(type==typeof(GasOwnedProjectionProfileSelection))Add(Ref("profile","Projection profile",false,["profileId","expectedRevision"],
                [Choice(newProfile.ToString(),"Create a new projection profile",new{profileId=newProfile,expectedRevision=0}),
                 ..profiles.Where(p=>accountId is null||p.Id("accountId")==accountId).GroupBy(p=>p.Id("profileId")).Select(g=>g.MaxBy(p=>Int(p,"revision")))
                    .Select(p=>Choice(p.Id("profileId").ToString(),$"{p.Text("accountCode")} · saved projection · revision {Int(p,"revision")}",new{profileId=p.Id("profileId"),expectedRevision=Int(p,"revision")}))]));
            if(type==typeof(GasOwnedBorrowSelection))Add(Ref("returnMark","Return obligation mark",false,["returnMarkCurve","returnMarkContractId"],
                [..marks.Where(m=>accountId is null||m.Id("definitionRevisionId")==accounts.Single(a=>a.Id("id")==accountId).Id("definition_revision_id"))
                    .Select((m,i)=>Choice(i.ToString(System.Globalization.CultureInfo.InvariantCulture),m.Text("label"),new{returnMarkCurve=m.Text("markCurve"),returnMarkContractId=m.Id("markContractId")}))]));
            if(type==typeof(GasOwnedProjectionPerformanceSelection))Add(Ref("inventoryMark","Projected receipt mark",true,
                ["inventoryDefinitionRevisionId","inventoryCurve","inventoryContractId"],[..marks.Select((m,i)=>Choice(i.ToString(System.Globalization.CultureInfo.InvariantCulture),m.Text("label"),
                    new{inventoryDefinitionRevisionId=m.Id("definitionRevisionId"),inventoryCurve=m.Text("markCurve"),inventoryContractId=m.Id("markContractId")}))]));
            var nulls=new NullabilityInfoContext();
            foreach(var property in type.GetProperties())
            {
                var name=JsonNamingPolicy.CamelCase.ConvertName(property.Name);if(covered.Contains(name))continue;
                var underlying=Nullable.GetUnderlyingType(property.PropertyType)??property.PropertyType;
                var nullable=Nullable.GetUnderlyingType(property.PropertyType) is not null||nulls.Create(property).ReadState==NullabilityState.Nullable;
                var label=name switch{
                    "evidenceId" or "titleEvidenceId"=>"Observed evidence","quantity"=>"Observed quantity","titleAt"=>"Title transferred at",
                    "availableAt"=>"Available for use at","originPolicy"=>"Title origin","carrying"=>"Valuation","sourceKey"=>"Evidence reference",
                    "businessKey" or "businessReference"=>"Business reference","expectedRevision"=>"Current revision (0 for a new record)",
                    "expectedVersion"=>"Current version (0 for a new record)","sourceObservationId"=>"Final cashout observation",_=>Words(name)};
                ImmutableArray<GasFormChoice>? selected=name switch{
                    "bookId"=>Ids(books,b=>b.Text("name")),
                    "evidenceId" or "titleEvidenceId"=>Ids(evidence.Where(e=>
                        (accountId is null||e.Id("account_id")==accountId||type==typeof(GasOwnedSettlementSelection)||type==typeof(GasFuturesSupplySelection))&&
                        (type==typeof(GasOwnedClaimSelection)?e.Text("kind")=="operator_statement":
                         type==typeof(GasOwnedDispatchSelection)?e.Text("kind")== (operation=="loss"?"physical_loss":"physical_delivery"):
                         type==typeof(GasOwnedRepaymentSelection)?e.Text("kind")=="loan_return":
                         type==typeof(GasOwnedSettlementSelection)?new[]{"cashout_determination","title_receipt","physical_delivery"}.Contains(e.Text("kind")):
                         type==typeof(GasFuturesSupplySelection)?e.Text("kind")=="physical_delivery":e.Text("kind")=="title_receipt")),
                        e=>$"{Account(e.Id("account_id"))} · {e.Text("source_key")} · revision {Int(e,"revision")} · {Words(e.Text("kind"))}"),
                    "positionId" when type==typeof(GasOwnedProjectionPerformanceSelection)=>futurePositionChoices,
                    "positionId" when type==typeof(GasOwnedCashoutProjectionSelection)=>Ids(knownPositions.Where(p=>(accountId is null||p.Id("account_id")==accountId)&&p.Text("kind")=="operator_claim"&&p.Text("settlement_mode")=="cashout_final_price_v1"),Position),
                    "incomingPositionId"=>futurePositionChoices,"positionId"=>positionChoices,
                    "lotId" when type==typeof(GasOwnedProjectionSupplySelection)=>knownLots,"lotId"=>lotChoices,"reservationId"=>reservationChoices,
                    "ownedAccountId" or "destinationAccountId"=>[..accountChoices.Where(c=>accounts.Single(a=>a.Id("id").ToString()==c.Id).Text("kind")=="owned_inventory")],
                    "loanId"=>Ids(Rows("loans").Where(l=>accountId is null||l.Id("account_id")==accountId),l=>$"{Account(l.Id("account_id"))} · loan quantity {l.Text("quantity")} · return {l.Text("return_at")}"),
                    "feeId"=>Ids(Rows("fees"),f=>$"{Position(positions.Single(p=>p.Id("id")==f.Id("position_id")))} · fee due {f.Text("due_at")}"),
                    "agreementId"=>Ids(evidence.Where(e=>(accountId is null||e.Id("account_id")==accountId)&&e.Text("kind")=="loan_agreement"),
                        e=>$"{Account(e.Id("account_id"))} · {e.Text("source_key")} · revision {Int(e,"revision")} · loan agreement"),
                    "sourceObservationId"=>Ids(observations,r=>$"{r.Text("source_code")} · {Words(r.Text("role"))} · {r.Text("observed_at")}"),
                    "cashId"=>Ids(Rows("deliveryCash").Where(r=>target is null||r.Id("id")==target.BindingId),r=>$"{r.Text("amount")} · principal due {r.Text("due_at")}"),
                    "carrying"=>Codes(("nominal_mark_v1","Nominal marked value"),("currency_curve_v1","Discount using the selected currency curve")),
                    "kind" when type==typeof(GasOwnedAccountSelection)=>Codes(("owned_inventory","Owned inventory"),("operator_balance","Operator claim or debt")),
                    "kind" when type==typeof(GasOwnedEvidenceSelection)=>Codes(("title_receipt","Observed title receipt"),("physical_delivery","Observed physical delivery"),("physical_loss","Observed physical loss"),
                        ("operator_statement","Operator statement"),("cashout_determination","Cashout determination"),("loan_agreement","Title-transfer loan agreement"),("loan_return","Observed loan return")),
                    "titlePolicy"=>Codes(("explicit_accepted_title_v1","Only explicitly accepted title")),
                    "lossPolicy"=>Codes(("separate_evidenced_loss_v1","Record separately evidenced loss")),
                    "originPolicy"=>Codes(("opening_owned_stock_v1","Explicit opening owned stock")),
                    "settlementMode"=>Codes(("return_quantity_v1","Return physical quantity"),("cashout_final_price_v1","Final-price cashout")),
                    "rounding" when type==typeof(GasOwnedCashoutSelection)=>Codes(("payment_total_half_even_2dp_v1","Cumulative payment total · two decimal places · ties to even")),
                    "implementation"=>Codes(("gas_quantity_lifecycle_projection_v1","Gas quantity lifecycle projection · version 1"),
                        ("gas_quantity_and_cashout_lifecycle_projection_v2","Gas quantity and operator cashout projection · version 2"),
                        ("gas_accepted_quantity_lifecycle_projection_v3","Known physical movements first; preserve stated allocations · version 3")),
                    "priceInterpretation"=>Codes(("selected_valuation_mark_is_cashout_expectation_v1","The selected valuation mark is the expected cashout price")),
                    "cashCarrying"=>Codes(("explicit_nominal_cashout_cash_v1","Explicit nominal cashout cash"),("captured_currency_curve_cashout_cash_v1","Carry cash on the claim’s captured currency curve")),
                    "quantityDisposition"=>Codes(("retain_quantity_obligations_v1","Retain the quantity obligation"),("perform_full_quantity_at_contractual_due_v1","Project the full stated remainder at contractual due time")),
                    "availability"=>Codes(("available_at_projected_title_v1","Available when projected title passes")),
                    "source"=>[..sources.GroupBy(s=>s.Text("code")).Select(g=>Choice(g.Key,g.First().Text("name"),g.Key))],
                    "role" when type==typeof(GasOwnedCashoutSelection)=>Codes(("daily_settlement","Official daily settlement"),("final_contract_settlement","Final contract settlement")),_=>null};
                if(selected is {} values){Add(new(name,label,"select",nullable,values,[],[]));continue;}
                if(underlying==typeof(Guid))throw GasOwnedOperations.Error($"The gas form requires exact native choices for {name}.");
                if(underlying==typeof(DateTimeOffset)){Add(new(name,label,"instant",nullable,[],[],[]));continue;}
                if(underlying==typeof(int)){Add(new(name,label,"integer",nullable,[],[],[]));continue;}
                if(underlying==typeof(string)){Add(new(name,label,"text",nullable,[],[],[]));continue;}
                if(underlying.IsGenericType&&underlying.GetGenericTypeDefinition()==typeof(ImmutableArray<>))
                    Add(new(name,label,"array",false,[],Fields(underlying.GetGenericArguments()[0],operation),[]));
                else if(underlying.Namespace==typeof(GasOwnedSetup).Namespace)Add(new(name,label,"object",nullable,[],Fields(underlying,operation),[]));
                else throw GasOwnedOperations.Error("This gas selection has no supported typed form.");
            }
            return result.ToImmutable();
        }
        var forms=GasOwnedOperations.Commands.ToImmutableDictionary(p=>p.Key,p=>new GasForm(GasOwnedActions.Label(p.Key),"RecordGasOwnedHoldings",Fields(GasOwnedOperations.SelectionTypes[p.Value],p.Value)),StringComparer.Ordinal);
        if(target?.Handler==GasFuturesDeliveryActions.Handler)forms=new Dictionary<string,GasForm>{
            ["gas.futures.delivery"]=new("Record observed futures delivery","RecordGasOwnedHoldings",Fields(typeof(GasFuturesPerformanceSelection),"performance")),
            ["gas.cash.record_payment"]=new("Record delivery principal payment","RecordGasOwnedHoldings",Fields(typeof(GasFuturesPaymentSelection),"payment"))}.ToImmutableDictionary(StringComparer.Ordinal);
        var configuration=GasOwnedOperations.ConfigurationCommands.ToImmutableDictionary(c=>c,c=>new GasForm(c switch{
            "account"=>"Account","evidence"=>"Observed evidence","bind_policy"=>"Account action policy","mark_binding"=>"Inventory mark","projection_profile"=>"Quantity projection policy",_=>throw GasOwnedOperations.Error("Unknown gas configuration.")},
            c=="evidence"?"RecordGasOwnedHoldings":"ConfigureGasOwnedHoldings",Fields(GasOwnedOperations.SelectionTypes[c],c)),StringComparer.Ordinal);
        return new(context,accountChoices,choices.ToImmutable(),forms,configuration);
    }
    private static int Int(JsonElement row,string key)=>int.Parse(row.Text(key),System.Globalization.CultureInfo.InvariantCulture);
    private static GasFormChoice Choice(string id,string label,object value)=>new(id,label,value);
    private static ImmutableArray<GasFormChoice> Codes(params (string Code,string Label)[] values)=>[..values.Select(v=>Choice(v.Code,v.Label,v.Code))];
    private static string Words(string value)
    {
        var text=Regex.Replace(value.Replace('_',' ').Replace('.',' '),"([a-z])([A-Z])","$1 $2");
        return text.Length==0?text:char.ToUpperInvariant(text[0])+text[1..];
    }
}
