using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class PnlArtifactChecks
{
    internal static void Run()
    {
        using var source=JsonDocument.Parse("""{"amount":"1/3","label":"Δ + <price>"}""");
        var report=source.RootElement;
        var stored=JsonSerializer.SerializeToElement(new{report},RiskJobs.Json).GetProperty("report");
        if(PnlReportArtifacts.ResultHash(report)!=PnlReportArtifacts.ResultHash(stored))
            throw new Exception("Embedding a report changed its financial content digest.");
        using var changed=JsonDocument.Parse("""{"amount":"1/4","label":"Δ + <price>"}""");
        if(PnlReportArtifacts.ResultHash(report)==PnlReportArtifacts.ResultHash(changed.RootElement))
            throw new Exception("An altered financial value retained its content digest.");
        Console.WriteLine("PASS P&L content identity survives JSON embedding and escaping, and detects changed exact values");

        // One endpoint's discount evidence states each rate-curve definition once, and for each payment its own
        // factor and two bracketing pillars. It names the market snapshot; it repeats neither that snapshot's inputs
        // nor the definition for every payment.
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var references=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        var usd=new ReferenceKey(references,"usd");var rateSource=new ReferenceKey(references,"source");
        var at=new DateTimeOffset(2030,1,1,0,0,0,TimeSpan.Zero);var date=new DateOnly(2030,1,1);
        // A flat 4% curve of monthly tenors, with every tenor's rate selected in the snapshot.
        (MarketSnapshot Snapshot,CurrencyDiscountCurve Curve) Market(int tenors)
        {
            var series=Enumerable.Range(0,tenors).Select(_=>new SeriesKey(revision,Guid.NewGuid())).ToArray();
            return(new(Guid.NewGuid(),revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
                    [..series.Select(s=>new SelectedObservation(new(s,InputUse.Variable),new(new(revision.CatalogId,Guid.NewGuid()),s,rateSource,
                        ObservationRole.Parameter,PublicationState.Final,at,ExactDecimal.Parse("0.04"))))]),
                new(Guid.NewGuid(),usd,new(new(revision,"usd_rates"),usd,rateSource,RateConventionSet.Parse("actual_365_fixed","simple","log_linear_discount","none","decimal_per_year"),
                    [..series.Select((s,n)=>new RateTenor($"{n+1}M",s,date.AddDays(30*(n+1))))],Guid.NewGuid(),1)));
        }
        PortfolioDiscountEvidence Payment(int days,MarketSnapshot snapshot,CurrencyDiscountCurve? curve)
        {
            var key=new SidePaymentKey(Guid.NewGuid(),0,at.AddDays(days),usd);
            return new(key.SideId,key,PaymentDiscount.Resolve(curve is not null,usd,date.AddDays(days),snapshot,curve));
        }
        // Three payments between the one- and two-month pillars.
        JsonElement Evidence(MarketSnapshot snapshot,CurrencyDiscountCurve? curve)=>
            JsonSerializer.SerializeToElement(TradingPnlReport.DiscountEvidence([Payment(40,snapshot,curve),Payment(45,snapshot,curve),Payment(50,snapshot,curve)]),RiskJobs.Json);
        var two=Market(2);var forty=Market(40);var small=Evidence(two.Snapshot,two.Curve);var wide=Evidence(forty.Snapshot,forty.Curve);
        var curves=small.GetProperty("curves").EnumerateArray().ToArray();var payments=small.GetProperty("payments").EnumerateArray().ToArray();
        if(curves.Length!=1||curves[0].GetProperty("mappingId").GetGuid()!=two.Curve.Id||curves[0].GetProperty("definition").GetProperty("tenors").GetArrayLength()!=2||
            wide.GetProperty("curves").GetArrayLength()!=1||wide.GetProperty("curves")[0].GetProperty("definition").GetProperty("tenors").GetArrayLength()!=40)
            throw new Exception("Discount evidence must state each rate-curve definition exactly once.");
        // At 4% simple, ACT/365F, the pillars discount by 1/(1+0.04*30/365) and 1/(1+0.04*60/365). Day 40 interpolates
        // their logarithms a third of the way: 0.9956367545638644933..., computed independently to 50 digits.
        if(payments.Length!=3||payments.Any(p=>p.GetProperty("input") is var input&&(input.GetProperty("snapshotId").GetGuid()!=two.Snapshot.Id||
                input.GetProperty("mappingId").GetGuid()!=two.Curve.Id||!input.GetProperty("enabled").GetBoolean()||input.GetProperty("pillars").GetArrayLength()!=2||
                input.TryGetProperty("snapshot",out _)||input.TryGetProperty("definition",out _)))||
            Math.Abs(decimal.Parse(payments[0].GetProperty("input").GetProperty("factor").GetString()!,System.Globalization.CultureInfo.InvariantCulture)-0.995636754563864493m)>0.000000000000000001m||
            payments[0].GetProperty("input").GetProperty("paymentDate").GetString()!="2030-02-10")
            throw new Exception("Each payment's discount evidence must keep its factor, date, pillars and curve and snapshot identities.");
        if(wide.GetProperty("payments").GetRawText().Length!=small.GetProperty("payments").GetRawText().Length)
            throw new Exception("A payment's discount evidence grew with its curve's definition or its snapshot's inputs.");
        var undiscounted=Evidence(two.Snapshot,null);
        if(undiscounted.GetProperty("curves").GetArrayLength()!=0||undiscounted.GetProperty("payments").EnumerateArray().Any(p=>
                p.GetProperty("input").GetProperty("enabled").GetBoolean()||p.GetProperty("input").GetProperty("factor").GetString()!="1"))
            throw new Exception("Undiscounted payments state a unit factor and no curve.");
        // One mapping cannot stand for two definitions at an endpoint.
        var republished=two.Curve with{Definition=two.Curve.Definition with{DefinitionVersion=2}};
        try{TradingPnlReport.DiscountEvidence([Payment(40,two.Snapshot,two.Curve),Payment(45,two.Snapshot,republished)]);throw new Exception("Conflicting definitions of one discount mapping were retained as one.");}
        catch(MarketDataException e)when(e.Code=="pnl_discount_evidence"){}
        Console.WriteLine("PASS P&L discount evidence states each curve definition once and names its snapshot without repeating the snapshot's inputs");

        // A completed result is the caller's to present whether or not it can be kept for a later Save.
        using var unopened=Npgsql.NpgsqlDataSource.Create("Host=127.0.0.1;Database=synthetic_unopened;Username=synthetic");
        using var artifacts=new PnlReportArtifacts(new PreviewDatabase(PreviewDataChecks.Fixture(1).Manifest,unopened),
            new EphemeralDataProtectionProvider().CreateProtector(ApplicationDataProtection.PnlPurpose));
        var owner=new PnlArtifactOwner(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());var now=DateTimeOffset.UtcNow;
        PnlCalculated Result(int characters)=>new(JsonSerializer.SerializeToElement(new{id=Guid.NewGuid(),values=new string('1',characters)},RiskJobs.Json),[]);
        var displayed=Result(16);var kept=artifacts.Seal(owner,displayed,now);
        if(kept.Refusal is not null||kept.Artifact is null||
            !JsonElement.DeepEquals(artifacts.Open(kept.Artifact,owner,now,true).Calculation.Report,displayed.Report))
            throw new Exception("A result within capacity must be kept for its owner's later Save.");
        var oversized=artifacts.Seal(owner,Result(1_400_000),now);
        if(oversized.Artifact is not null||oversized.Refusal?.Code!="pnl_result_capacity")
            throw new Exception("A result too large to keep must be returned without a save handle, with its reason.");
        // Twenty-three results of 1.39 MB fill the 32 MB memo. The next cannot be admitted, and is still returned.
        for(var n=0;n<23;n++)
            if(artifacts.Seal(owner,Result(1_390_000),now).Artifact is null)throw new Exception("A result within the memo budget was not kept.");
        var crowded=artifacts.Seal(owner,Result(1_390_000),now);
        if(crowded.Artifact is not null||crowded.Refusal?.Code!="pnl_result_capacity"||crowded.Refusal.Message==oversized.Refusal.Message)
            throw new Exception("A result the full memo cannot admit must be returned without a save handle, with its own reason.");
        Console.WriteLine("PASS P&L results that cannot be kept for saving are returned unsaved with a stated reason");
    }
}
