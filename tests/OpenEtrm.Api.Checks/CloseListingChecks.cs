using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;

internal static class CloseListingChecks
{
    public static async Task Run(string file)
    {
        using var json=JsonDocument.Parse(File.ReadAllText(file));var root=json.RootElement;
        await using var connection=new NpgsqlConnection(root.GetProperty("connection").GetString());
        await connection.OpenAsync();await using var transaction=await connection.BeginTransactionAsync();
        await using(var use=new NpgsqlCommand("SELECT security.use_context($1)",connection))
        {use.Parameters.AddWithValue(root.GetProperty("token").GetGuid());await use.ExecuteNonQueryAsync();}
        var catalog=root.GetProperty("catalog").GetGuid();
        var retained=await MarketCloses.Frame(connection,catalog,root.GetProperty("retired").GetGuid(),null,default);
        var frames=await MarketCloses.Frames(connection,catalog,null,default,1);
        if(frames.Frames.Length!=1||frames.Frames[0].Close!.Session==retained.Close!.Session||frames.Frames[0].At>=retained.At)
            throw new Exception("A newer retired close consumed the current discovery limit.");
        if(retained.Id!=root.GetProperty("retired").GetGuid()||retained.SourceInputs?.Count!=2)
            throw new Exception("Retirement changed an exact retained source selection.");
        await transaction.CommitAsync();
        Console.WriteLine("PASS close discovery filters retirement before its limit and retains exact source reads");
    }
}
