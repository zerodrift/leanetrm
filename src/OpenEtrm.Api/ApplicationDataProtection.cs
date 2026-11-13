using Microsoft.AspNetCore.DataProtection;

namespace OpenEtrm.Api;

// An owned preview's private state directory has the same lifetime as its
// database. Durable authenticated artifacts retain their keys across restarts.
internal static class ApplicationDataProtection
{
    internal const string PreviewApplication="OpenEtrm:OwnedPreview";
    internal const string PnlPurpose="OpenEtrm.TradingPnl.Artifact.v1";
    internal static DirectoryInfo PreviewKeys(string root)=>Directory.CreateDirectory(Path.Combine(Path.GetFullPath(root),"keys"));
    internal static void ConfigurePreview(IDataProtectionBuilder builder,string root)=>
        builder.SetApplicationName(PreviewApplication).PersistKeysToFileSystem(PreviewKeys(root));
}
