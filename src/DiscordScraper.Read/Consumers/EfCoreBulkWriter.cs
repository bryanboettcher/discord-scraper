using System.Reflection;
using DiscordScraper.Read.Data;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Dispatches BulkInsertOrUpdateAsync&lt;T&gt; for each entity type via MakeGenericMethod because
/// EFCore.BulkExtensions exposes only a generic overload — no IList&lt;object&gt; entry point exists.
/// The MethodInfo cache is module-level; MakeGenericMethod results are not cached because the
/// closed methods are JIT-cached by the runtime after first call per type.
/// </summary>
internal sealed class EfCoreBulkWriter : IReadBulkWriter
{
    // DbContextBulkExtensions.BulkInsertOrUpdateAsync<T>(DbContext, IEnumerable<T>, BulkConfig, Action<decimal>, Type, CancellationToken)
    private static readonly MethodInfo _openUpsertMethod = FindOpenUpsertMethod();

    public async Task WriteAsync(
        ReadDbContext db,
        IDbContextTransaction transaction,
        IReadOnlyDictionary<Type, IList<object>> entitiesByType,
        CancellationToken ct)
    {
        foreach (var (entityType, boxedList) in entitiesByType)
        {
            // Build a properly-typed List<TEntity> so BulkExtensions' internal type assertion passes.
            var typedList = BuildTypedList(entityType, boxedList);

            var closedMethod = _openUpsertMethod.MakeGenericMethod(entityType);
            var task = (Task)closedMethod.Invoke(
                obj: null,
                parameters: [db, typedList, null!, null!, null!, ct])!;

            await task.ConfigureAwait(false);
        }
    }

    private static object BuildTypedList(Type entityType, IList<object> boxedList)
    {
        var typedListType = typeof(List<>).MakeGenericType(entityType);
        var typedList = Activator.CreateInstance(typedListType, boxedList.Count)!;
        var addMethod = typedListType.GetMethod("Add")!;
        foreach (var item in boxedList)
            addMethod.Invoke(typedList, [item]);
        return typedList;
    }

    private static MethodInfo FindOpenUpsertMethod()
    {
        // Locate the overload: (DbContext, IEnumerable<T>, BulkConfig, Action<decimal>, Type, CancellationToken)
        var method = typeof(DbContextBulkExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m =>
                m.Name == nameof(DbContextBulkExtensions.BulkInsertOrUpdateAsync)
                && m.IsGenericMethodDefinition
                && m.GetParameters() is { Length: 6 } ps
                && ps[2].ParameterType == typeof(BulkConfig)
                && ps[5].ParameterType == typeof(CancellationToken));

        return method;
    }
}
