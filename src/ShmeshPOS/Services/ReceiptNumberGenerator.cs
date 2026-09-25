using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ShmeshPOS.Data;

namespace ShmeshPOS.Services;

public interface IReceiptNumberGenerator
{
    /// <summary>
    /// Reserves the next receipt number for <paramref name="year"/>. Must be called inside the sale's
    /// database transaction: the counter row stays locked until commit (so concurrent terminals never
    /// collide) and a rollback releases the number (so there are no gaps).
    /// </summary>
    Task<string> NextAsync(PosDbContext db, int year, CancellationToken cancellationToken = default);
}

public class ReceiptNumberGenerator : IReceiptNumberGenerator
{
    public async Task<string> NextAsync(PosDbContext db, int year, CancellationToken cancellationToken = default)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Receipt numbers must be generated inside a transaction.");

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText =
            """
            INSERT INTO receipt_sequences (year, last_number) VALUES (@year, 1)
            ON CONFLICT (year) DO UPDATE SET last_number = receipt_sequences.last_number + 1
            RETURNING last_number
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "year";
        parameter.Value = year;
        command.Parameters.Add(parameter);

        var next = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        return Format(year, next);
    }

    public static string Format(int year, int number) => $"{year}-{number:D6}";
}
