using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

static class ReferenceImportChecks
{
    public static async Task Run(string? output=null)
    {
        var document=Encoding.UTF8.GetBytes("\"CONTRACT SYMBOL\",\"FTD\",\"LTD\",\"FND\",\"LND\",\"FDD\",\"LDD\",\"FSD\",\"MWHS\"\n\"=\"\"Nov40\"\"\",\"10/02/2040\",\"11/30/2040\",\"\",\"\",\"\",\"\",\"12/04/2040\",35.125\n");
        var hash=Convert.ToHexStringLower(SHA256.HashData(document));var now=DateTimeOffset.UtcNow;
        var row=IceReferenceCapture.ExpiryCsv(document).Single();
        var capture=new ReferenceCapturePackage("exchange_reference_capture_v1","ICE","website_downloads","unqualified",false,false,now,now,
            [new(new("ICE","IFED","123456",row.ContractSymbol),JsonSerializer.SerializeToElement(row),hash)],
            [new("https://www.ice.com/api/productguide/spec/123456/expiry/csv",now,hash,Convert.ToBase64String(document),"operator_reported_download")]);
        var reference=new ReferenceRevision(Guid.Parse("10000000-0000-0000-0000-000000000001"),Guid.Parse("20000000-0000-0000-0000-000000000099"));
        var settings=new ReferenceImportOptions(new(reference,"synthetic_power_mwh"),[new("123456","electricity","synthetic_hub","Synthetic hub","hub","Synthetic power future")],null);
        var plan=await ReferenceImportProtocol.Prepare(capture,settings,CancellationToken.None);
        Require(plan.Products.Length==1&&plan.Series.Length==1,"source products and actual dated rows retained");
        Require(plan.Series[0].FirstTradingDate==new DateOnly(2040,10,2)&&plan.Series[0].FirstDeliveryDate is null,"native dates stay date-only and unknown delivery dates stay unknown");
        Require(plan.Series[0].ReportedEnergy?.Amount.ToString()=="35.125"&&plan.Series[0].ReportedEnergy?.Specification==settings.EnergyQuantity,"source MWh uses an exact pinned Quantity");
        Require(plan.Products[0].Code==ReferenceImportIdentity.Product("ICE","IFED","123456")&&plan.Series[0].Code==ReferenceImportIdentity.Series("ICE","IFED","123456","Nov40"),"source lookup keys map to exact reusable reference keys");
        Require(ReferenceImportIdentity.Series("ICE","IFED","123456","Nov40")!=ReferenceImportIdentity.Series("ICE","IFED","123456","nov40"),"identity preserves source case");
        Require(plan.Series[0].Code.Length<=64&&plan.Products[0].Code.Length<=64,"canonical reference keys fit the existing database domain");
        async Task Reject(ReferenceCapturePackage package,ReferenceImportOptions options)
        {
            try{await ReferenceImportProtocol.Prepare(package,options,CancellationToken.None);throw new Exception("Accepted conflicting reference package");}
            catch(MarketDataException e) when(e.Code=="reference_import"){}
        }
        await Reject(capture with{TradingEnabled=true},settings);
        await Reject(capture with{Records=[capture.Records[0] with{Identity=new("ICE","IFED","123456","Dec40")} ]},settings);
        await Reject(capture with{Documents=[capture.Documents[0] with{Sha256=new string('0',64)}]},settings);
        await Reject(capture with{Documents=[capture.Documents[0],capture.Documents[0]]},settings);
        await Reject(capture,settings with{Products=[]});
        await Reject(capture,settings with{EnergyQuantity=null});
        await Reject(capture,settings with{Products=[settings.Products[0] with{Commodity="gas"}]});
        var empty=Encoding.UTF8.GetBytes("\"CONTRACT SYMBOL\",\"FTD\",\"LTD\",\"FND\",\"LND\",\"FDD\",\"LDD\",\"FSD\"\n");
        var emptyHash=Convert.ToHexStringLower(SHA256.HashData(empty));
        var emptyPlan=await ReferenceImportProtocol.Prepare(capture with{Records=[],Documents=[capture.Documents[0] with{ContentBase64=Convert.ToBase64String(empty),Sha256=emptyHash}]},settings,CancellationToken.None);
        Require(emptyPlan.Products.Length==1&&emptyPlan.Series.Length==0,"an empty daily report retains an incomplete product without invented maturities");
        // Independent RFC 4648 base32/SHA-256 vector, calculated with Python hashlib/base64.
        Require(ReferenceImportIdentity.Series("ICE","IFED","123456","Nov40")=="r_zgxxiwfo4ec4qabvphpzjr6zkvptmrauobuaviktgdmgxiavg2jq","independent identity encoding vector");
        var duplicateSource=capture with{Records=[capture.Records[0],capture.Records[0] with{Identity=new("ICE","IFED","654321",row.ContractSymbol)}],
            Documents=[capture.Documents[0],capture.Documents[0] with{Url="https://www.ice.com/api/productguide/spec/654321/expiry/csv"}]};
        var two=await ReferenceImportProtocol.Prepare(duplicateSource,settings with{Products=[settings.Products[0],settings.Products[0] with{ProductId="654321"}]},CancellationToken.None);
        Require(two.Series.Length==2&&two.Series[0].DocumentCode!=two.Series[1].DocumentCode,"identical bytes remain scoped to their actual source product");
        await Reject(capture with{Records=[capture.Records[0] with{Reported=JsonSerializer.SerializeToElement(row with{ReportedMegawattHours="36"})}]},settings);
        await Reject(capture with{Records=[capture.Records[0] with{Reported=JsonSerializer.SerializeToElement(row with{FirstTradingDate=new DateOnly(2040,10,1)})}]},settings);
        if(output is not null)
        {
            await File.WriteAllTextAsync(output,plan.Payload());
            await File.WriteAllTextAsync(output+".capture.json",JsonSerializer.Serialize(capture,ReferenceImportProtocol.Json));
            await File.WriteAllTextAsync(output+".mapping.json",JsonSerializer.Serialize(settings,ReferenceImportProtocol.Json));
        }
        Console.WriteLine("PASS reference import planning: source verification, exact identity, pinned quantities, native dates and explicit missing coverage");
    }
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
}
