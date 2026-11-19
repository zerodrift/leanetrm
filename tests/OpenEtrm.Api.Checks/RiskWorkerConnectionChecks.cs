using Npgsql;
using OpenEtrm.Api;

internal static class RiskWorkerConnectionChecks
{
    internal static void Run()
    {
        RiskWorkerConnection.Validate(new("Host=/tmp/synthetic;Database=synthetic;Username=synthetic_risk;SSL Mode=Disable"));
        const string network="Host=database.synthetic.invalid;Database=synthetic;Username=synthetic_risk;";
        RiskWorkerConnection.Validate(new(network+"SSL Mode=VerifyFull;Root Certificate=/run/secrets/database-root.pem;GSS Encryption Mode=Disable"));
        foreach(var invalid in new[]{network+"SSL Mode=Prefer",network+"SSL Mode=Require",
            network+"SSL Mode=VerifyFull",network+"SSL Mode=VerifyFull;Root Certificate=relative.pem",
            network+"SSL Mode=VerifyFull;Root Certificate=/run/secrets/database-root.pem;GSS Encryption Mode=Prefer",
            network+"SSL Mode=VerifyFull;Root Certificate=/run/secrets/database-root.pem;GSS Encryption Mode=Require",
            "Host=first.synthetic.invalid,second.synthetic.invalid;Database=synthetic;Username=synthetic_risk;SSL Mode=VerifyFull;Root Certificate=/run/secrets/database-root.pem",
            "Host=/tmp/synthetic;Database=synthetic;Username=synthetic_risk;Multiplexing=true",
            "Host=/tmp/synthetic;Database=synthetic;Username=synthetic_risk;No Reset On Close=true"})
        {
            try{RiskWorkerConnection.Validate(new NpgsqlConnectionStringBuilder(invalid));}
            catch(InvalidOperationException){continue;}
            throw new Exception("A risk worker accepted an ambiguous, unverified or multiplexed database boundary.");
        }
        Console.WriteLine("PASS risk worker pool connection: owned sockets or one verified TLS host/root, no multiplexing or retained session state");
    }
}
