using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

/// <summary>Private source acquisition only; no database or trading credentials are accepted.</summary>
public static class ReferenceCaptureCommand
{
    static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=false,WriteIndented=true};
    public static async Task<int> Run(string[] args)
    {
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(30));
            ReferenceCapturePackage result;string output;
            if(args is ["cme-products",var config,var destination])
            {
                using var http=Client();var settings=Read<CmeReferenceSettings>(config,true);
                result=await new CmeReferenceCapture(http,settings).Products(timeout.Token);output=destination;
            }
            else if(args is ["cme-contracts",var configPath,var selectionPath,var destinationPath])
            {
                using var http=Client();var settings=Read<CmeReferenceSettings>(configPath,true);
                result=await new CmeReferenceCapture(http,settings).Contracts(Read<ImmutableArray<CmeProductSelection>>(selectionPath,false),timeout.Token);output=destinationPath;
            }
            else if(args is ["ice-files",var manifest,var target])
            {result=await IceReferenceCapture.Files(Read<IceReferenceSelection>(manifest,false),timeout.Token);output=target;}
            else throw new MarketDataException("reference_command","Choose cme-products CONFIG OUTPUT, cme-contracts CONFIG SELECTION OUTPUT, or ice-files MANIFEST OUTPUT.");
            await WriteNew(output,result,timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(new{state=result.State,provider=result.Provider,records=result.Records.Length,documents=result.Documents.Length,tradingEnabled=false},Json));
            return 0;
        }
        catch(MarketDataException e){Console.Error.WriteLine(JsonSerializer.Serialize(new{code=e.Code,error=e.Message}));return 1;}
        catch(OperationCanceledException){Console.Error.WriteLine("{\"code\":\"reference_timeout\",\"error\":\"Capture interrupted; no complete package was accepted.\"}");return 1;}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {Console.Error.WriteLine("{\"code\":\"reference_file\",\"error\":\"Use valid private inputs and a new output path in a private directory.\"}");return 1;}
    }
    static HttpClient Client()=>new(new HttpClientHandler{AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.None}){Timeout=TimeSpan.FromSeconds(60)};
    static T Read<T>(string path,bool secret)
    {
        if(!Path.IsPathFullyQualified(path)||new FileInfo(path).LinkTarget is not null||new FileInfo(path).Length>1024*1024)throw new IOException();
        if(secret)Private(path);
        using var stream=File.OpenRead(path);using var document=JsonDocument.Parse(stream);
        CmeReferenceCapture.RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Deserialize<T>(Json)??throw new JsonException();
    }
    static void Private(string path)
    {
        if(OperatingSystem.IsWindows())throw new IOException("Private capture currently requires Unix file permissions.");
        if((File.GetUnixFileMode(path)&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute))!=0)
            throw new IOException();
    }
    public static async Task WriteNew(string path,ReferenceCapturePackage package,CancellationToken ct)
    {
        if(OperatingSystem.IsWindows())throw new IOException("Private capture currently requires Unix file permissions.");
        if(!Path.IsPathFullyQualified(path)||File.Exists(path))throw new IOException();
        var parent=Path.GetDirectoryName(path)!;Private(parent);
        var temporary=Path.Combine(parent,".reference-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using(var file=new FileStream(temporary,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}))
            {await JsonSerializer.SerializeAsync(file,package,Json,ct);await file.FlushAsync(ct);file.Flush(true);}
            File.Move(temporary,path,false);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
