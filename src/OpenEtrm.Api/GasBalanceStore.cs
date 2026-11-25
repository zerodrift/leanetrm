using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class GasBalanceStore
{
    internal static async Task<ImmutableArray<SlotBalances>> Read(NpgsqlConnection connection, Guid catalog,
        GasCompositionExecution held, DateTimeOffset at, CancellationToken ct)
    {
        if (!held.Members.Any(member => member.Balance is not null)) return [];
        await using var query = ScheduleStore.Command(connection,
            "SELECT security.read_gas_valuation_balances($1,$2,$3)::text", catalog, held.TradeId, held.InstrumentId);
        using var document = JsonDocument.Parse((string)(await query.ExecuteScalarAsync(ct))!);
        var result = ImmutableArray.CreateBuilder<SlotBalances>();
        foreach (var slot in document.RootElement.EnumerateArray())
        {
            var number = slot.GetProperty("slot").GetInt32();
            var member = held.Members.Single(member => member.Slot == number);
            var policy = held.Members.First(entry => entry.Source == member.Code && entry.Balance is not null).Balance!.StatementPolicy;
            var accounts = ImmutableDictionary.CreateBuilder<DateOnly, ImmutableArray<Guid>>();
            var balances = ImmutableArray.CreateBuilder<AccountDayBalance>();
            var heads = ImmutableArray.CreateBuilder<AccountStatementEvidence>();
            var months = ImmutableHashSet.CreateBuilder<DateOnly>();
            foreach (var row in slot.GetProperty("days").EnumerateArray())
            {
                var day = DateWorkspace.Date(row.Text("day"));
                var scope = row.GetProperty("accounts").EnumerateArray().ToArray();
                accounts.Add(day, [..scope.Select(account => account.Id("accountId"))]);
                foreach (var account in scope)
                {
                    var unit = new ReferenceKey(new(account.Id("referenceCatalogId"), account.Id("referenceRevisionId")), account.Text("quantityCode"));
                    if (unit != member.Assignment.Quote.Quantity)
                        throw new MarketDataException("gas_imbalance_input", "The service account must retain the physical source's exact native unit.");
                    ExactQuantity Quantity(string value) => ExactQuantity.FromQuantity(new(ExactDecimal.Parse(value), unit));
                    ImmutableArray<GasBalanceMovement> movements = [..account.GetProperty("movements").EnumerateArray().Select(movement =>
                        new GasBalanceMovement(movement.Id("id"), movement.GetProperty("ordinal").GetInt32(), Quantity(movement.Text("quantity"))))];
                    if (!movements.IsEmpty) months.Add(new(day.Year, day.Month, 1));
                    // The native reader returns the latest visible revision regardless
                    // of status. Policy is tested afterwards; no older head is available.
                    var statement = account.GetProperty("statement");
                    if (statement.ValueKind == JsonValueKind.Null)
                    {
                        heads.Add(new(account.Id("accountId"),day,account.Id("serviceVersionId"),null,"missing",null,account.Text("serviceEvidence")));
                        continue;
                    }
                    var status = statement.Text("status");
                    heads.Add(new(account.Id("accountId"),day,account.Id("serviceVersionId"),statement.Id("id"),status,
                        statement.OptionalText("balance") is {} reported?Quantity(reported):null,statement.Text("evidence")));
                    if (status == "withdrawn" || policy == GasImbalanceTrading.FinalPolicy && status != "final") continue;
                    var current = GasImbalanceTrading.Balance(new(statement.Id("id"), Quantity(statement.Text("balance")), status,
                        statement.OptionalId("includedMovementId")), movements, policy);
                    balances.Add(new(account.Id("accountId"), day, current, statement.Id("id"), status, statement.Text("evidence")));
                }
            }
            result.Add(new(number, accounts.ToImmutable(), balances.ToImmutable(), months.ToImmutable()){StatementHeads=heads.ToImmutable()});
        }
        return result.ToImmutable();
    }
}
