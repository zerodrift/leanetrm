using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

internal static partial class GasOwnedSetup
{
    private static async Task<GasSetup> ParkLoanSetup(NpgsqlConnection connection,PreviewDatabase database,LifecycleDiscoveryRequest request,CancellationToken ct)
    {
        var target=request.Target!;
        var source=await GasParkLoanTicketStore.Read(connection,database,request.Context,target.ScopeId,ct);
        var owned=await GasOwnedOperations.Read(connection,database,request.Context,source.Id("ownedAccountId"),ct);
        JsonElement? claim=source.OptionalId("claimAccountId") is {} claimId?
            await GasOwnedOperations.Read(connection,database,request.Context,claimId,ct):null;
        var ids=source.GetProperty("policies").EnumerateArray().Select(row=>row.Id("policy_id")).Distinct().ToArray();
        var policies=await PreviewDatabase.Rows(connection,
            "SELECT row_to_json(p)::text FROM lifecycle.action_policies p WHERE p.catalog_id=$1 AND p.id=ANY($2::uuid[]) ORDER BY p.id,p.revision",
            [database.Manifest.CatalogId,ids],ct);
        return BuildParkLoanSetup(request.Context,target,source,owned,claim,policies);
    }

    // Display metadata follows the native agreement's saved direction and exact
    // source rows. Admission and every quantity/cash effect remain native.
    internal static GasSetup BuildParkLoanSetup(LifecycleReadContext context,LifecycleTarget target,JsonElement source,
        JsonElement ownedState,JsonElement? claimState,ImmutableArray<JsonElement> policies)
    {
        var direction=Int(source.GetProperty("terms"),"principalDirection");
        var balance=source.Text("implementation")==GasParkLoanBalanceFeeTerms.Version;
        if(target.Handler!=GasParkLoanActions.Handler||source.Text("implementation") is not(GasParkAndLoanTerms.Version or GasParkLoanBalanceFeeTerms.Version)||
            source.Id("tradeId")!=target.ScopeId||source.Id("instrumentId")!=target.BindingId||
            source.Id("contextId")!=context.ContextId||source.Instant("asOf")!=context.AsOf||direction is not(1 or -1))
            throw GasOwnedOperations.Error("Retain the exact Park and Loan agreement, native target and certified source.");
        var receiver=direction==1;var owned=source.Id("ownedAccountId");var claim=source.OptionalId("claimAccountId");
        if(receiver!=(claim is null)||!receiver&&claimState is null)
            throw GasOwnedOperations.Error("Read the agreement's exact inventory and return-claim account sources.");
        JsonElement[] Rows(JsonElement state,string part)=>[..state.GetProperty(part).EnumerateArray()];
        var inventory=Rows(ownedState,"accounts").Single(row=>row.Id("id")==owned);
        var accounts=new[]{inventory}.Concat(claimState is {} claims?Rows(claims,"accounts").Where(row=>row.Id("id")==claim):[]).ToArray();
        if(inventory.Text("kind")!="owned_inventory"||!receiver&&accounts.Count(row=>row.Id("id")==claim&&row.Text("kind")=="operator_balance")!=1)
            throw GasOwnedOperations.Error("Retain the agreement's exact native account kinds.");
        var states=new[]{ownedState}.Concat(claimState is {} claimSource?[claimSource]:[]).ToArray();
        var evidence=states.SelectMany(state=>Rows(state,"evidence")).DistinctBy(row=>row.Id("id"))
            .GroupBy(row=>(row.Id("account_id"),row.Text("source_key"))).Select(group=>group.MaxBy(row=>Int(row,"revision")))
            .Where(row=>row.Instant("occurred_at")<=context.AsOf).ToArray();
        GasFormField Select(string name,string label,bool nullable,ImmutableArray<GasFormChoice> choices)=>new(name,label,"select",nullable,choices,[],[]);
        GasFormField Reference(string name,string label,bool nullable,string[] members,ImmutableArray<GasFormChoice> choices)=>new(name,label,"reference",nullable,choices,[],[..members]);
        GasFormField Evidence(string name,string label,Guid? account,string kind,bool nullable=false)=>Select(name,label,nullable,
            [..evidence.Where(row=>row.Id("account_id")==account&&row.Text("kind")==kind).Select(row=>Choice(row.Id("id").ToString(),
                $"{row.Text("source_key")} · source revision {Int(row,"revision")}",row.Id("id")))]);
        GasFormField Stock(bool applicable)
        {
            var choices=ImmutableArray.CreateBuilder<GasFormChoice>();
            var positions=Rows(ownedState,"positions");var documents=Rows(ownedState,"evidence");
            var releases=Rows(ownedState,"releases");
            if(applicable)foreach(var lot in Rows(ownedState,"lots").Where(row=>row.Instant("available_at")<=context.AsOf))
            {
                var position=positions.Single(row=>row.Id("id")==lot.Id("id"));
                if(position.Id("account_id")!=owned||position.Text("kind")!="owned_stock"||position.Instant("effective_at")>context.AsOf)continue;
                var label=$"{inventory.Text("code")} · {documents.Single(row=>row.Id("id")==position.Id("evidence_id")).Text("source_key")} · {lot.Id("id")}";
                choices.Add(Choice(lot.Id("id")+":none",label+" · no reservation",new{lotId=lot.Id("id"),reservationId=(Guid?)null}));
                foreach(var reservation in Rows(ownedState,"reservations").Where(row=>row.Id("lot_id")==lot.Id("id")&&row.Instant("effective_at")<=context.AsOf&&
                    !releases.Any(release=>release.Id("reservation_id")==row.Id("id")&&release.Instant("effective_at")<=context.AsOf)))
                    choices.Add(Choice(lot.Id("id")+":"+reservation.Id("id"),label+" · reservation "+reservation.Text("business_key"),new{lotId=lot.Id("id"),reservationId=(Guid?)reservation.Id("id")}));
            }
            return Reference("identifiedStock","Identified inventory and reservation",!applicable,["lotId","reservationId"],choices.ToImmutable());
        }
        GasFormField Version()=>Reference("serviceVersion","Captured agreement version",false,["expectedVersion"],
            [Choice(Int(source,"stateVersion").ToString(System.Globalization.CultureInfo.InvariantCulture),"Agreement version "+Int(source,"stateVersion"),new{expectedVersion=Int(source,"stateVersion")})]);
        GasFormField Text(string name,string label)=>new(name,label,"text",false,[],[],[]);
        GasFormField Instant(string name,string label)=>new(name,label,"instant",false,[],[],[]);
        var advance=source.GetProperty("advance");var fee=advance.ValueKind==JsonValueKind.Object?advance.OptionalId("feeId"):null;
        var determinations=balance?source.GetProperty("fee").GetProperty("determinations").EnumerateArray().ToArray():[];
        var paymentChoices=balance?determinations.Where(row=>row.OptionalId("feeId") is not null&&row.Number("unpaid")!=default)
            .Select(row=>Choice(row.Id("feeId").ToString(),"Independent fee cash · unpaid "+row.Text("unpaid")+" · "+row.Id("feeId"),row.Id("feeId"))).ToImmutableArray():
            fee is {} feeId?ImmutableArray.Create(Choice(feeId.ToString(),"Independent agreement fee · "+feeId,feeId)):[];
        var forms=new Dictionary<string,GasForm>{
            [GasParkLoanActions.Advance]=new("Accept principal advance","RecordGasOwnedHoldings",[
                Version(),Evidence("agreementEvidenceId","Accepted agreement evidence",owned,"loan_agreement"),
                Evidence("evidenceId",receiver?"Observed title receipt":"Observed principal delivery",owned,receiver?"title_receipt":"physical_delivery"),
                Evidence("claimEvidenceId","Observed return claim",claim,"operator_statement",receiver),Stock(!receiver),Instant("effectiveAt","Observed advance time")]),
            [GasParkLoanActions.Return]=new("Accept principal return","RecordGasOwnedHoldings",[
                Version(),Text("businessKey","Return business reference"),Evidence("evidenceId",receiver?"Observed principal return":"Observed returned title",owned,receiver?"loan_return":"title_receipt"),
                Stock(receiver),Text("quantity","Observed return quantity"),Instant("effectiveAt","Observed return time")]),
            [GasParkLoanActions.Payment]=new(balance?"Record independent fee payment or refund":"Record independent fixed-fee payment","RecordGasOwnedHoldings",[
                Version(),Select("feeId",balance?"Determined fee cash holding":"Agreement's fixed-fee cash holding",false,paymentChoices),
                Text("businessKey","Bank payment business reference"),new("expectedRevision","Bank payment revision (0 for a new fact)","integer",false,[],[],[]),
                Instant("paidAt","Observed bank payment time"),Text("amount","Observed signed fee payment"),Text("evidence","Bank payment evidence")])};
        if(balance)
        {
            var previous=determinations.LastOrDefault();var hasPrevious=previous.ValueKind==JsonValueKind.Object;
            forms.Add(GasParkLoanActions.Determine,new("Determine accrued service fee","RecordGasOwnedHoldings",[
                Version(),Reference("previousDetermination","Previous cumulative determination",!hasPrevious,["previousDeterminationId"],hasPrevious?
                    [Choice(previous.Id("id").ToString(),"Rounded cumulative fee "+previous.Text("roundedTotal")+" "+source.GetProperty("terms").Text("currency"),new{previousDeterminationId=previous.Id("id")})]:[]),
                Instant("through","Observed fee accrual through"),Instant("effectiveAt","Fee determination effective time"),
                Text("businessKey","Determination business reference"),Text("evidence","Determination evidence")]));
        }
        var bound=ImmutableArray.CreateBuilder<GasFormPolicy>();
        foreach(var action in forms.Keys)
        {
            var native=GasParkLoanActions.NativeAction(source,action);
            if(!source.GetProperty("policies").EnumerateArray().Any(row=>row.Id("account_id")==native.Account&&row.Text("action")==native.Action))continue;
            var binding=GasParkLoanActions.Policy(source,action);
            var policy=policies.Single(row=>row.Id("id")==binding.Id("policy_id")&&Int(row,"revision")==Int(binding,"policy_revision"));
            bound.Add(new(policy.Id("id").ToString(),$"{Words(policy.Text("mode"))} · {Words(native.Action)} · revision {Int(policy,"revision")}",
                Int(policy,"revision"),action,target.ScopeId.ToString()));
        }
        return new(context,[..accounts.Select(account=>Choice(account.Id("id").ToString(),account.Text("code"),account.Id("id")))],bound.ToImmutable(),
            forms.ToImmutableDictionary(StringComparer.Ordinal),ImmutableDictionary<string,GasForm>.Empty){Target=target};
    }
}
