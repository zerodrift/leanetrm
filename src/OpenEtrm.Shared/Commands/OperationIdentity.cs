using System.Security.Cryptography;
using System.Text;

namespace OpenEtrm.Shared.Commands;

/// <summary>Stable technical identities within an explicitly named operation scope.</summary>
public static class OperationIdentity
{
    public static Guid For(string domain,Guid scope,int ordinal=0)=>Guid.ParseExact(Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{domain}:{scope:D}:{ordinal}"))))[..32],"N");
}
