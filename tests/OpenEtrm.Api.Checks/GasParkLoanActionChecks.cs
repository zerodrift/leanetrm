using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasParkLoanActionChecks
{
    internal static void Run()
    {
        var id=Guid.Parse("a1000000-0000-0000-0000-000000000101");
        var at=new DateTimeOffset(2031,1,2,12,0,0,TimeSpan.Zero);
        var request=new LifecycleActionRequest(id,new(id,at,new(2031,1,2)),new(GasParkLoanActions.Handler,id,id),GasParkLoanActions.Advance,id,1,false,new('0',64),
            JsonSerializer.SerializeToElement(new{expectedVersion=4,agreementEvidenceId=id.ToString().ToUpperInvariant(),evidenceId=id,
                claimEvidenceId=(Guid?)null,lotId=(Guid?)null,reservationId=(Guid?)null,effectiveAt="2031-01-02T12:00:00.1234560+00:00"}));
        var normalized=GasParkLoanActions.Normalize(request);
        if(normalized.Selection.GetProperty("effectiveAt").GetString()!="2031-01-02T12:00:00.123456Z"||
            normalized.Selection.GetProperty("agreementEvidenceId").GetString()!=id.ToString()||
            normalized.Selection.GetProperty("lotId").ValueKind!=JsonValueKind.Null)
            throw new Exception("The real UI's exact UTC candidate must normalize to the closed native instant and identity contract.");
        void Reject(JsonElement body)
        {
            try{GasParkLoanActions.Normalize(request with{Selection=body});}
            catch(MarketDataException){return;}
            throw new Exception("Missing, unknown or non-UTC service action inputs must refuse before native mutation.");
        }
        Reject(JsonSerializer.SerializeToElement(new{expectedVersion=4,agreementEvidenceId=id,evidenceId=id,lotId=(Guid?)null,reservationId=(Guid?)null,effectiveAt="2031-01-02T12:00:00Z"}));
        Reject(JsonSerializer.SerializeToElement(new{expectedVersion=4,agreementEvidenceId=id,evidenceId=id,claimEvidenceId=(Guid?)null,lotId=(Guid?)null,reservationId=(Guid?)null,effectiveAt="2031-01-02T12:00:00Z",unreviewed="x"}));
        Reject(JsonSerializer.SerializeToElement(new{expectedVersion=4,agreementEvidenceId=id,evidenceId=id,claimEvidenceId=(Guid?)null,lotId=(Guid?)null,reservationId=(Guid?)null,effectiveAt="2031-01-02T06:00:00-06:00"}));
        Reject(JsonSerializer.SerializeToElement(new{expectedVersion=4,agreementEvidenceId=id,evidenceId=id,claimEvidenceId=(Guid?)null,lotId=(Guid?)null,reservationId=(Guid?)null,effectiveAt="2031-01-02T12:00:00.00000001Z"}));
        var determination=request with{Action=GasParkLoanActions.Determine,Selection=JsonSerializer.SerializeToElement(new{
            expectedVersion=4,previousDeterminationId=(Guid?)null,through="2031-01-02T12:00:00.1234560+00:00",effectiveAt="2031-01-02T12:00:00.1234560+00:00",businessKey="synthetic_fee",evidence="Synthetic fee determination"})};
        var canonical=GasParkLoanActions.Normalize(determination);
        if(canonical.Selection.Text("through")!="2031-01-02T12:00:00.123456Z"||canonical.Selection.GetProperty("previousDeterminationId").ValueKind!=JsonValueKind.Null)
            throw new Exception("A fee determination requires its explicit observed horizon, current version and nullable previous checkpoint.");
        foreach(var body in new[]{
            JsonSerializer.SerializeToElement(new{expectedVersion=4,through="2031-01-02T12:00:00Z",effectiveAt="2031-01-02T12:00:00Z",businessKey="synthetic_fee",evidence="Synthetic fee determination"}),
            JsonSerializer.SerializeToElement(new{expectedVersion=4,previousDeterminationId=(Guid?)null,through="2031-01-02T12:00:00Z",effectiveAt="2031-01-02T12:00:00Z",businessKey="synthetic_fee",evidence="Synthetic fee determination",amount="999"})})
        {
            try{GasParkLoanActions.Normalize(determination with{Selection=body});}catch(MarketDataException){continue;}
            throw new Exception("Fee determination must neither assume a previous checkpoint nor trust a submitted cash amount.");
        }
        if(!GasParkLoanActions.Refusal("gas_balance_fee_cash_range").Contains("cash amount",StringComparison.Ordinal)||
            !GasParkLoanActions.Refusal("gas_balance_fee_window").Contains("cutoff",StringComparison.Ordinal))
            throw new Exception("The selected fee profile must explain numerical-capacity and observed-window refusals.");
        Console.WriteLine("PASS Park and Loan lifecycle transport: canonical UTC candidates, exact identities, explicit nulls and closed typed selections");
    }
}
