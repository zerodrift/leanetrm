namespace OpenEtrm.Shared.Configuration;

// A transport budget carries no credentials or selected defaults. The owning
// process supplies both its operating values and its qualified ceiling.
public sealed record ConnectionBudget(int ConnectSeconds,int CommandSeconds,int PoolSize)
{
    public bool IsWithin(ConnectionBudget ceiling) =>
        ConnectSeconds>0 && ConnectSeconds<=ceiling.ConnectSeconds &&
        CommandSeconds>0 && CommandSeconds<=ceiling.CommandSeconds &&
        PoolSize>0 && PoolSize<=ceiling.PoolSize;
}
