using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcessLimits(string Implementation,int RequestSeconds,int ExtendedRequestSeconds,int ResponseGraceSeconds,
    int RequestBytes,int IntervalRevisionBytes,int PageSessions,int SignInAttemptsPerMinute,int PasswordHashers,int AgentConnections,
    int ProviderConnectSeconds,int ProviderIdleSeconds,int ProviderResponseBytes)
{
    public static ProcessLimits Read(string path)
    {
        var value=JsonSerializer.Deserialize<ProcessLimits>(File.ReadAllText(path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})
            ??throw new InvalidOperationException("Complete explicit process limits are required.");
        value.Validate();return value;
    }
    public void Validate()
    {
        if(Implementation!="bounded_process_limits_v1")throw new InvalidOperationException("Select limits implementation bounded_process_limits_v1.");
        foreach(var (name,value,min,max) in new[]{
            ("requestSeconds",RequestSeconds,1,15),("extendedRequestSeconds",ExtendedRequestSeconds,1,20),
            ("responseGraceSeconds",ResponseGraceSeconds,1,5),("requestBytes",RequestBytes,1024,32768),
            ("intervalRevisionBytes",IntervalRevisionBytes,1024,8*1024*1024),("pageSessions",PageSessions,1,64),
            ("signInAttemptsPerMinute",SignInAttemptsPerMinute,1,12),("passwordHashers",PasswordHashers,1,4),
            ("agentConnections",AgentConnections,5,6),("providerConnectSeconds",ProviderConnectSeconds,1,5),
            ("providerIdleSeconds",ProviderIdleSeconds,1,30),("providerResponseBytes",ProviderResponseBytes,1024,4*1024*1024)})
            if(value<min||value>max)throw new InvalidOperationException($"Supply limits.{name} between {min} and {max}.");
        if(ExtendedRequestSeconds<RequestSeconds||IntervalRevisionBytes<RequestBytes)
            throw new InvalidOperationException("Extended request and interval revision limits must cover the ordinary limits.");
    }
    [JsonIgnore] public string Digest=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase})));
    [JsonIgnore] public object RequestDeadlines=>new{normalMilliseconds=(RequestSeconds+ResponseGraceSeconds)*1000,extendedMilliseconds=(ExtendedRequestSeconds+ResponseGraceSeconds)*1000};
}
