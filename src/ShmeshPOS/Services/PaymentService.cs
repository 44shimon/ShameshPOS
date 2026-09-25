using Microsoft.EntityFrameworkCore;
using ShmeshPOS.Data;
using ShmeshPOS.Models;

namespace ShmeshPOS.Services;

public interface IPaymentService
{
    /// <summary>Active payment methods in display order. Loaded once and cached.</summary>
    Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(CancellationToken cancellationToken = default);
}

public class PaymentService(IDbContextFactory<PosDbContext> dbFactory) : IPaymentService
{
    private IReadOnlyList<PaymentMethod>? _cache;

    public async Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null)
            return _cache;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        _cache = await db.PaymentMethods.AsNoTracking()
            .Where(m => m.IsActive)
            .OrderBy(m => m.SortOrder).ThenBy(m => m.Name)
            .ToListAsync(cancellationToken);
        return _cache;
    }
}
