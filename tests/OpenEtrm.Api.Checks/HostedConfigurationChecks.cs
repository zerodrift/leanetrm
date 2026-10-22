using System.Text.Json;
using OpenEtrm.Api;

// The hosted page assistant's installation contract and session binding (docs/ai-assistant.md §11): host.json names both
// credential key files, and an application session belongs to the sign-in session it was opened under.
static class HostedConfigurationChecks
{
    public static void Run()
    {
        var root=Directory.CreateTempSubdirectory("openetrm-hosted-configuration-");
        try
        {
            string At(string name)=>Path.Combine(root.FullName,name);
            var complete=new Dictionary<string,object>{["installationId"]=Guid.NewGuid(),["publicOrigin"]="https://pilot.synthetic.invalid",
                ["stateDirectory"]=At("state"),["webDirectory"]=At("web"),["manifestDirectory"]=At("private"),
                ["runtimeConnectionFile"]=At("private/runtime.connection"),["authenticatorConnectionFile"]=At("private/authenticator.connection"),
                ["listenPort"]=8080,["trustedProxyNetwork"]="127.0.0.1/32",["protectionCertificateFile"]=At("private/protection.pfx"),
                ["credentialKeyFile"]=At("private/credential.key"),["credentialRetryKeyFile"]=At("private/credential-retry.key")};
            HostedConfiguration Read(Dictionary<string,object> values)
            {
                var file=At("host.json");
                File.WriteAllText(file,JsonSerializer.Serialize(values));
                return HostedConfiguration.Read(file);
            }
            var read=Read(complete);
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

        var manifest=new PreviewManifest("hosted-pilot",Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),[],null);
        var digest=HostedAccess.TokenDigest(HostedAccess.NewToken());
        var signIn=new HostedSignIn(digest,DateTimeOffset.UtcNow.AddMinutes(12));
        var bound=new PreviewSession(manifest,signIn);
        Require(bound.Digest==digest&&bound.Ends==signIn.Ends,"an application session names itself by its sign-in session's digest and ends with it");
        var later=new PreviewSession(manifest,signIn with{Ends=DateTimeOffset.UtcNow.AddHours(2)});
        Require(later.Ends==later.Created+PreviewSession.Lifetime,"an application session never outlives its own lifetime");
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
