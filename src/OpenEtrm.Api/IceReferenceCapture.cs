using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record IceReportedExpiry(string ContractSymbol,DateOnly? FirstTradingDate,DateOnly? LastTradingDate,
    DateOnly? FirstNoticeDate,DateOnly? LastNoticeDate,DateOnly? FirstDeliveryDate,DateOnly? LastDeliveryDate,
    DateOnly? FinalSettlementDate,string? ReportedMegawattHours);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IceReferenceFile(string ProductId,string Exchange,string Kind,string File,string Url,
    DateTimeOffset RetrievedAtUtc,string Sha256);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IceReferenceSelection(int MaximumDocumentBytes,int MaximumCaptureBytes,ImmutableArray<IceReferenceFile> Files);

public static class IceReferenceCapture
{
    static readonly string[] Headers=["CONTRACT SYMBOL","FTD","LTD","FND","LND","FDD","LDD","FSD"];
    public static ImmutableArray<IceReportedExpiry> ExpiryCsv(byte[] content)
    {
        try
        {
            using var reader=new StringReader(new UTF8Encoding(false,true).GetString(content).TrimStart('\uFEFF'));
            using var parser=new TextFieldParser(reader){TextFieldType=FieldType.Delimited,HasFieldsEnclosedInQuotes=true,TrimWhiteSpace=false};
            parser.SetDelimiters(",");var headers=parser.ReadFields();
            if(headers is null||!(headers.SequenceEqual(Headers)||headers.SequenceEqual(Headers.Append("MWHS"))))throw Invalid();
            var rows=ImmutableArray.CreateBuilder<IceReportedExpiry>();var keys=new HashSet<string>(StringComparer.Ordinal);
            while(!parser.EndOfData)
            {
                var row=parser.ReadFields();if(row is null)throw Invalid();
                // The gas export emits one unnamed empty field after its eight named columns.
                // Accept that exact serialization; a nonempty unnamed value is ambiguous.
                if(headers.Length==8&&row.Length==9&&row[8]=="")row=row[..8];
                if(row.Length!=headers.Length)throw Invalid();
                var label=row[0];
                // The exchange export wraps text as an Excel string literal. Never evaluate formulas.
                if(label.StartsWith("=\"",StringComparison.Ordinal)&&label.EndsWith('"'))label=label[2..^1];
                if(!Regex.IsMatch(label,"^[A-Za-z0-9][A-Za-z0-9 ./_-]{0,79}$",RegexOptions.CultureInvariant)||!keys.Add(label))throw Invalid();
                DateOnly? Date(int column)
                {
                    if(row[column]=="")return null;
                    if(!DateOnly.TryParseExact(row[column],"MM/dd/yyyy",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)||date>=new DateOnly(9999,12,31))throw Invalid();
                    return date;
                }
                var first=Date(1);var last=Date(2);var deliveryFrom=Date(5);var deliveryThrough=Date(6);
                if(first>last||deliveryFrom>deliveryThrough)throw Invalid();
                string? mwh=null;
                if(row.Length==9&&row[8]!="")
                {
                    // Use the existing 18-digit/1e24 source-capture bound explicitly.
                    // No approved unit-reference binding exists yet; this is not a domain amount.
                    var amount=ExactDecimal.Parse(row[8]);if(amount.CompareTo(ExactDecimal.Parse("0"))<=0)throw Invalid();mwh=amount.ToString();
                }
                rows.Add(new(label,first,last,Date(3),Date(4),deliveryFrom,deliveryThrough,Date(7),mwh));
                if(rows.Count>100000)throw Invalid();
            }
            return rows.ToImmutable();
        }
        catch(Exception e) when(e is MalformedLineException or DecoderFallbackException or FormatException or ArgumentException)
        {throw Invalid();}
        catch(MarketDataException e) when(e.Code!="reference_response"){throw Invalid();}
    }

    public static async Task<ReferenceCapturePackage> Files(IceReferenceSelection selection,CancellationToken ct)
    {
        if(selection.Files.IsDefaultOrEmpty||selection.Files.Length>1024||selection.Files.Any(f=>f is null)||selection.MaximumDocumentBytes is <1 or >16*1024*1024||
            selection.MaximumCaptureBytes<selection.MaximumDocumentBytes||selection.MaximumCaptureBytes>256*1024*1024)
            throw new MarketDataException("reference_configuration","Supply explicit files and bounded document/capture limits.");
        var start=DateTimeOffset.UtcNow;var bytes=0;var records=ImmutableArray.CreateBuilder<ReferenceSourceRecord>();
        var documents=ImmutableArray.CreateBuilder<ReferenceSourceDocument>();var seen=new HashSet<(string,string)>();
        foreach(var file in selection.Files)
        {
            if(!Regex.IsMatch(file.ProductId??"","^[0-9]{1,18}$",RegexOptions.CultureInvariant)||file.Exchange!="IFED"||
                file.Kind is not("expiry_csv" or "specification_pdf")||!seen.Add((file.ProductId!,file.Kind))||
                file.RetrievedAtUtc.Offset!=TimeSpan.Zero||file.RetrievedAtUtc<new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero)||file.RetrievedAtUtc>DateTimeOffset.UtcNow)
                throw Invalid();
            var expected=$"https://www.ice.com/api/productguide/spec/{file.ProductId}/"+(file.Kind=="expiry_csv"?"expiry/csv":"pdf");
            if(file.Url!=expected)throw Invalid();
            if(!Path.IsPathFullyQualified(file.File)||new FileInfo(file.File).LinkTarget is not null)throw Invalid();
            await using var stream=System.IO.File.OpenRead(file.File);
            var content=await CmeReferenceCapture.Read(stream,selection.MaximumDocumentBytes,ct);bytes=checked(bytes+content.Length);
            if(bytes>selection.MaximumCaptureBytes)throw new MarketDataException("reference_limit","The capture exceeds its explicit byte limit.");
            var hash=Convert.ToHexStringLower(SHA256.HashData(content));if(hash!=file.Sha256)throw Invalid();
            documents.Add(new(file.Url,file.RetrievedAtUtc,hash,Convert.ToBase64String(content),"operator_reported_download"));
            if(file.Kind=="expiry_csv")
                foreach(var row in ExpiryCsv(content))records.Add(new(new("ICE",file.Exchange,file.ProductId!,row.ContractSymbol),JsonSerializer.SerializeToElement(row),hash));
            else
            {
                if(!content.AsSpan().StartsWith("%PDF-"u8))throw Invalid();
                records.Add(new(new("ICE",file.Exchange,file.ProductId!,null),JsonSerializer.SerializeToElement(new{evidenceKind="specification_pdf",termsParsed=false}),hash));
            }
        }
        return new("exchange_reference_capture_v1","ICE","website_downloads","unqualified",false,false,start,DateTimeOffset.UtcNow,records.ToImmutable(),documents.ToImmutable());
    }
    static MarketDataException Invalid()=>new("reference_response","The ICE download, selected source identity or native expiry fields are incomplete or conflicting.");
}
