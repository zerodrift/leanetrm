using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenEtrm.Api;

// A provider key is sealed under the preview's private key files and opens only for the exact
// provider, vendor and credential version it was sealed for; every other case is credential_unavailable.
static class CredentialCustodyChecks
{
    public static void Run()
    {
        var directory=Directory.CreateTempSubdirectory("openetrm-custody-");
        try
        {
            string KeyFile(string name,int length=32,UnixFileMode mode=UnixFileMode.UserRead|UnixFileMode.UserWrite)
            {
                if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Credential key files rely on Unix file modes.");
                var path=Path.Combine(directory.FullName,name);
                File.WriteAllBytes(path,RandomNumberGenerator.GetBytes(length));File.SetUnixFileMode(path,mode);
                return path;
            }
            var (encryption,retry)=(KeyFile("credential.key"),KeyFile("credential-retry.key"));
            var custody=new CredentialCustody(encryption,retry);
            var (provider,key)=(Guid.NewGuid(),"sk-synthetic-"+Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
            var sealedKey=custody.Seal(provider,"anthropic",1,key);
            var again=custody.Seal(provider,"anthropic",1,key);
            Require(custody.Open(provider,"anthropic",1,sealedKey)==key,"a sealed key opens for its exact provider, vendor and version");
            Require(sealedKey.Nonce.Length==12&&sealedKey.Tag.Length==16&&!sealedKey.Nonce.SequenceEqual(again.Nonce)&&!sealedKey.Ciphertext.SequenceEqual(again.Ciphertext),
                "every seal uses a fresh 96-bit nonce and a 128-bit tag");
            Console.WriteLine("PASS a sealed key opens only as sealed, under a fresh nonce each time");
            Unavailable("another provider",()=>custody.Open(Guid.NewGuid(),"anthropic",1,sealedKey));
            Unavailable("another vendor",()=>custody.Open(provider,"openai",1,sealedKey));
            Unavailable("another credential version",()=>custody.Open(provider,"anthropic",2,sealedKey));
            Unavailable("a flipped nonce byte",()=>custody.Open(provider,"anthropic",1,sealedKey with{Nonce=Flip(sealedKey.Nonce)}));
            Unavailable("a flipped ciphertext byte",()=>custody.Open(provider,"anthropic",1,sealedKey with{Ciphertext=Flip(sealedKey.Ciphertext)}));
            Unavailable("a flipped tag byte",()=>custody.Open(provider,"anthropic",1,sealedKey with{Tag=Flip(sealedKey.Tag)}));
            Unavailable("another encryption key file",()=>new CredentialCustody(KeyFile("other.key"),retry).Open(provider,"anthropic",1,sealedKey));

            var (tenant,workspace,operation)=(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
            // A link reports its target path's length as its own, so a 32-character target leaves only the link check to refuse it.
            var linked=Path.Combine(directory.FullName,"linked.key");
            File.CreateSymbolicLink(linked,Path.GetFileName(KeyFile(new string('t',28)+".key")));
            foreach(var (name,path) in new[]{("an unset key file",(string?)null),("an empty key file path",""),("a missing key file",Path.Combine(directory.FullName,"absent.key")),
                ("a short key file",KeyFile("short.key",31)),("a long key file",KeyFile("long.key",33)),
                ("a group-readable key file",KeyFile("shared.key",mode:UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.GroupRead)),
                ("a linked key file",linked)})
            {
                Unavailable(name+" when sealing",()=>new CredentialCustody(path,retry).Seal(provider,"anthropic",1,key));
                Unavailable(name+" when digesting a retry",()=>new CredentialCustody(encryption,path).Digest(tenant,workspace,operation,"anthropic",key));
            }
            // Opening a FIFO blocks until a writer appears, so a FIFO path must be refused before it is opened.
            var fifo=Path.Combine(directory.FullName,"fifo.key");
            using(var made=Process.Start("mkfifo",[fifo])!){made.WaitForExit();Require(made.ExitCode==0,"mkfifo creates the FIFO fixture");}
            Require(Task.Run(()=>Unavailable("a FIFO key file",()=>new CredentialCustody(fifo,retry).Seal(provider,"anthropic",1,key))).Wait(TimeSpan.FromSeconds(10)),
                "custody refuses a FIFO key file without waiting for a writer");
            var digest=custody.Digest(tenant,workspace,operation,"anthropic",key);
            Require(Regex.IsMatch(digest,"^[0-9a-f]{64}$")&&digest==custody.Digest(tenant,workspace,operation,"anthropic",key),"a retry digest is stable lowercase hex");
            Require(new[]{custody.Digest(Guid.NewGuid(),workspace,operation,"anthropic",key),custody.Digest(tenant,Guid.NewGuid(),operation,"anthropic",key),
                    custody.Digest(tenant,workspace,Guid.NewGuid(),"anthropic",key),custody.Digest(tenant,workspace,operation,"openai",key),
                    custody.Digest(tenant,workspace,operation,"anthropic",key+"x"),
                    new CredentialCustody(encryption,KeyFile("other-retry.key")).Digest(tenant,workspace,operation,"anthropic",key)}.All(other=>other!=digest),
                "a retry digest is keyed and bound to its tenant, workspace, operation, vendor and key");
            Console.WriteLine("PASS a retry digest is stable, keyed and bound to its tenant, workspace, operation, vendor and key");
        }
        finally{directory.Delete(true);}
    }

    // The preview's stored credentials, read with its own key-file environment: each opens to the key
    // submitted for it, and each audit digest is the retry digest of the request that stored it.
    public static void Stored(string fixturePath)
    {
        using var fixture=JsonDocument.Parse(File.ReadAllText(fixturePath));
        var custody=CredentialCustody.FromEnvironment();
        string Text(JsonElement row,string name)=>row.GetProperty(name).GetString()!;
        var credentials=fixture.RootElement.GetProperty("credentials").EnumerateArray().ToList();
        foreach(var row in credentials)
        {
            var credential=new SealedCredential(Convert.FromHexString(Text(row,"nonce")),Convert.FromHexString(Text(row,"ciphertext")),Convert.FromHexString(Text(row,"tag")));
            Require(custody.Open(row.GetProperty("providerId").GetGuid(),Text(row,"vendor"),row.GetProperty("version").GetInt32(),credential)==Text(row,"key"),
                "a stored credential opens to the key submitted for it");
        }
        Console.WriteLine($"PASS each of {credentials.Count} stored credentials opens to its submitted key under the preview's key files");
        var audit=fixture.RootElement.GetProperty("audit").EnumerateArray().ToList();
        foreach(var row in audit)
            Require(custody.Digest(row.GetProperty("tenantId").GetGuid(),row.GetProperty("workspaceId").GetGuid(),row.GetProperty("operationId").GetGuid(),
                Text(row,"vendor"),Text(row,"key"))==Text(row,"digest"),"an audit digest identifies its own request");
        Console.WriteLine($"PASS each of {audit.Count} audited replacements keeps the retry digest of its own request");
    }

    static byte[] Flip(byte[] bytes){var copy=(byte[])bytes.Clone();copy[^1]^=1;return copy;}

    static void Unavailable<T>(string name,Func<T> work)
    {
        try{work();}
        catch(ModelAdministrationException e) when(e.Code=="credential_unavailable"){Console.WriteLine("PASS custody refuses "+name);return;}
        throw new Exception($"Custody accepted {name}.");
    }

    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
}
