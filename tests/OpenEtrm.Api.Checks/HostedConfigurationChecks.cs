using System.Text.Json;
using OpenEtrm.Api;

// The hosted page assistant's installation contract and session binding (docs/ai-assistant.md §11): host.json names both
// credential key files, and an application session belongs to the sign-in session it was opened under.
static class HostedConfigurationChecks
{
    public static void Run()
    {
        PageSessionChecks.Run();
        var root=Directory.CreateTempSubdirectory("openetrm-hosted-configuration-");
        try
        {
            string At(string name)=>Path.Combine(root.FullName,name);
            var complete=new Dictionary<string,object>{["installationId"]=Guid.NewGuid(),["publicOrigin"]="https://pilot.synthetic.invalid",
                ["stateDirectory"]=At("state"),["webDirectory"]=At("web"),["manifestDirectory"]=At("private"),
                ["runtimeConnectionFile"]=At("private/runtime.connection"),["authenticatorConnectionFile"]=At("private/authenticator.connection"),
                ["listenPort"]=8080,["trustedProxyNetwork"]="127.0.0.1/32",["protectionCertificateFile"]=At("private/protection.pfx"),
                ["credentialKeyFile"]=At("private/credential.key"),["credentialRetryKeyFile"]=At("private/credential-retry.key"),
                ["limits"]=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText("deploy/process-limits.example.json"))};
            HostedConfiguration Read(Dictionary<string,object> values)
            {
                var file=At("host.json");
                File.WriteAllText(file,JsonSerializer.Serialize(values));
                return HostedConfiguration.Read(file);
            }
            var read=Read(complete);
            var selected=read.Limits with {RequestSeconds=6,ExtendedRequestSeconds=11,ResponseGraceSeconds=3,PageSessions=8};
            selected.Validate();
            Require(JsonSerializer.SerializeToElement(selected.RequestDeadlines).GetProperty("normalMilliseconds").GetInt32()==9000
                &&JsonSerializer.SerializeToElement(selected.RequestDeadlines).GetProperty("extendedMilliseconds").GetInt32()==14000
                &&selected.Digest!=read.Limits.Digest,"client budgets and process identity derive from the selected configuration");
            foreach(var invalid in new[]{read.Limits with{RequestSeconds=0},read.Limits with{RequestBytes=32769},
                read.Limits with{PasswordHashers=5},read.Limits with{AgentConnections=4},read.Limits with{ExtendedRequestSeconds=14},
                read.Limits with{IntervalRevisionBytes=1024}})
            {
                try{invalid.Validate();throw new Exception("An incomplete or incompatible process budget was accepted.");}
                catch(InvalidOperationException){}
            }
            Refused(()=>Read(new(complete){["limits"]=null!}),"missing process limits");
            var connectionFile=At("private/database.connection");Directory.CreateDirectory(Path.GetDirectoryName(connectionFile)!);
            File.WriteAllText(connectionFile,"Host=database.synthetic.invalid;Database=synthetic;Username=application;Timeout=2;Command Timeout=3;Maximum Pool Size=4");
            var connection=HostedConfiguration.Connection(connectionFile);
            Require(connection.Timeout==2&&connection.CommandTimeout==3&&connection.MaxPoolSize==4,
                "explicit database budgets must be honored rather than replaced by operating literals");
            Require(read.CredentialKeyFile==At("private/credential.key")&&read.CredentialRetryKeyFile==At("private/credential-retry.key"),
                "a hosted configuration names both credential key files");
            foreach(var key in new[]{"credentialKeyFile","credentialRetryKeyFile"})
            {
                // A configuration from before the page assistant ran hosted names neither file, so the refusal names both.
                Refused(()=>Read(complete.Where(p=>p.Key!=key).ToDictionary()),$"a hosted configuration without {key}","credentialKeyFile and credentialRetryKeyFile");
                Refused(()=>Read(new(complete){[key]="private/credential.key"}),$"a relative {key}");
                Refused(()=>Read(new(complete){[key]=At("web/credential.key")}),$"a {key} among the served assets");
            }
            // One file as both keys would make the retry key the key-encryption key (§5.2).
            Refused(()=>Read(new(complete){["credentialRetryKeyFile"]=At("private/../private/credential.key")}),"one file as both credential keys","two different files");
            Console.WriteLine("PASS hosted configuration requires two different credential key files, at absolute paths outside the served assets, and names them when absent");
        }
        finally{root.Delete(true);}

        var manifest=new PreviewManifest("hosted-pilot",Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),[],null,FuturesConvention:"contract_lot_execution_pnl_v2");
        var digest=HostedAccess.TokenDigest(HostedAccess.NewToken());
        var signIn=new HostedSignIn(digest,DateTimeOffset.UtcNow.AddMinutes(12));
        var bound=new PreviewSession(manifest,signIn);
        Require(bound.Digest==digest&&bound.Ends==signIn.Ends,"an application session names itself by its sign-in session's digest and ends with it");
        var later=new PreviewSession(manifest,signIn with{Ends=DateTimeOffset.UtcNow.AddHours(2)});
        Require(later.Ends>later.Created+PreviewSession.Lifetime,"a hosted page uses the sign-in row’s authoritative expiry without a separate duration policy");
        var (preview,other)=(new PreviewSession(manifest),new PreviewSession(manifest));
        Require(preview.Ends==preview.Created+PreviewSession.Lifetime&&preview.Digest.Length==64&&preview.Digest.All(char.IsAsciiHexDigitLower)
            &&preview.Digest!=other.Digest,"a preview session without a sign-in names itself by a random digest and ends with its lifetime");
        Console.WriteLine("PASS a hosted application session takes its sign-in session's digest and ends no later than it");
    }

    static void Refused(Func<HostedConfiguration> read,string description,string? explanation=null)
    {
        try{read();}
        catch(InvalidOperationException e) when(explanation is null||e.Message.Contains(explanation,StringComparison.Ordinal)){return;}
        throw new Exception($"Accepted {description}, or refused it without saying so");
    }

    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
}
