using Npgsql;

namespace OpenEtrm.Api;

internal static class RiskWorkerConnection
{
    internal static void Validate(NpgsqlConnectionStringBuilder connection)
    {
        if(string.IsNullOrWhiteSpace(connection.Host)||connection.Host.Contains(',')||connection.Multiplexing||connection.NoResetOnClose)
            throw new InvalidOperationException("A risk worker requires one explicit database host, exclusive physical operations and reset pooled sessions.");
        if(!Path.IsPathFullyQualified(connection.Host)&&
            (connection.SslMode!=SslMode.VerifyFull||connection.GssEncryptionMode!=GssEncryptionMode.Disable||
             string.IsNullOrWhiteSpace(connection.RootCertificate)||!Path.IsPathFullyQualified(connection.RootCertificate)))
            throw new InvalidOperationException("Network risk workers require VerifyFull TLS, GSS Encryption Mode=Disable and an explicit absolute root certificate file.");
    }
}
