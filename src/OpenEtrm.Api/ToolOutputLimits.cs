using System.Text.Json;

namespace OpenEtrm.Api;

public sealed record ToolOutputLimits(int Rows, int Bytes)
{
    public static ToolOutputLimits From(JsonElement admitted)
    {
        var rows = admitted.GetProperty("toolResultRows").GetInt32();
        var bytes = admitted.GetProperty("toolResultBytes").GetInt32();
        if (rows < 1 || bytes < 1) throw new InvalidOperationException("The turn's admitted output limits are invalid.");
        return new(rows, bytes);
    }
}
