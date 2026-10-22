using System.Security.Cryptography;
using System.Text;

namespace OpenEtrm.Api;

public sealed class ModelAdministrationException(string code,string message):Exception(message)
{
    public string Code {get;}=code;
}

public sealed record SealedCredential(byte[] Nonce,byte[] Ciphertext,byte[] Tag);

// Write-only model-provider keys (docs/ai-assistant.md §5.2). A key is sealed with AES-GCM under the
// installation's key-encryption key, bound to its provider identity, vendor and credential version, and a
// retry of one request is recognized by an HMAC-SHA-256 digest under the separate retry key.
// tools/run_preview.py writes both 32-byte keys once per disposable database and tools/provision_hosted.py once
// per hosted installation (§11); nothing is generated here. A missing or malformed key file and a failed tag are
// credential_unavailable.
public sealed class CredentialCustody(string? encryptionKeyFile,string? retryKeyFile)
{
    public static CredentialCustody FromEnvironment()=>new(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_CREDENTIAL_KEY"),
        Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_CREDENTIAL_RETRY_KEY"));

    public SealedCredential Seal(Guid provider,string vendor,int version,string key)
    {
        var (encryption,plaintext)=(Read(encryptionKeyFile),Encoding.UTF8.GetBytes(key));
        var (nonce,ciphertext,tag)=(RandomNumberGenerator.GetBytes(NonceBytes),new byte[plaintext.Length],new byte[TagBytes]);
        try
        {
            using var aes=new AesGcm(encryption,TagBytes);
            aes.Encrypt(nonce,plaintext,ciphertext,tag,AssociatedData(provider,vendor,version));
            return new(nonce,ciphertext,tag);
        }
        catch(CryptographicException){throw Unavailable();}
        finally{CryptographicOperations.ZeroMemory(encryption);CryptographicOperations.ZeroMemory(plaintext);}
    }

    public string Open(Guid provider,string vendor,int version,SealedCredential credential)
    {
        var (encryption,plaintext)=(Read(encryptionKeyFile),new byte[credential.Ciphertext.Length]);
        try
        {
            if(credential.Nonce.Length!=NonceBytes||credential.Tag.Length!=TagBytes)throw Unavailable();
            using var aes=new AesGcm(encryption,TagBytes);
            aes.Decrypt(credential.Nonce,credential.Ciphertext,credential.Tag,plaintext,AssociatedData(provider,vendor,version));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch(CryptographicException){throw Unavailable();}
        finally{CryptographicOperations.ZeroMemory(encryption);CryptographicOperations.ZeroMemory(plaintext);}
    }

    // Bound to one request in one workspace, so equal keys entered elsewhere never share a digest.
    public string Digest(Guid tenant,Guid workspace,Guid operation,string vendor,string key)
    {
        var (retry,message)=(Read(retryKeyFile),Encoding.UTF8.GetBytes($"openetrm-credential-retry-v1\n{tenant:D}\n{workspace:D}\n{operation:D}\n{vendor}\n{key}"));
        try{return Convert.ToHexStringLower(HMACSHA256.HashData(retry,message));}
        finally{CryptographicOperations.ZeroMemory(retry);CryptographicOperations.ZeroMemory(message);}
    }

    const int KeyBytes=32,NonceBytes=12,TagBytes=16;
    static byte[] AssociatedData(Guid provider,string vendor,int version)=>Encoding.UTF8.GetBytes($"openetrm-credential-v1\n{provider:D}\n{vendor}\n{version}");

    // A key file is exactly 32 bytes that no one but its owner may read or write, and not a symbolic
    // link. Its length is checked before opening, so a FIFO or device is never opened, and its mode
    // and length again on the handle it is read from. Owner-only modes need Unix.
    static byte[] Read(string? path)
    {
        try
        {
            if(OperatingSystem.IsWindows()||string.IsNullOrEmpty(path)||new FileInfo(path) is not {Exists:true,LinkTarget:null,Length:KeyBytes})throw Unavailable();
            using var handle=File.OpenHandle(path,FileMode.Open,FileAccess.Read);
            if((File.GetUnixFileMode(handle)&~(UnixFileMode.UserRead|UnixFileMode.UserWrite))!=0||RandomAccess.GetLength(handle)!=KeyBytes)throw Unavailable();
            var key=new byte[KeyBytes];
            if(RandomAccess.Read(handle,key,0)==KeyBytes)return key;
            CryptographicOperations.ZeroMemory(key);
            throw Unavailable();
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or NotSupportedException){throw Unavailable();}
    }

    static ModelAdministrationException Unavailable()=>new("credential_unavailable","Provider keys cannot be sealed or opened with this installation's key files.");
}
