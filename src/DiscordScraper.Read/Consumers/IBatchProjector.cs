namespace DiscordScraper.Read.Consumers;

/// <summary>
/// Projects a single event of type <typeparamref name="TEvent"/> into zero or more entities
/// of type <typeparamref name="TEntity"/>. Implementations are stateless and suitable for
/// singleton registration.
/// </summary>
public interface IBatchProjector<in TEvent, out TEntity>
    where TEvent : class
    where TEntity : class
{
    IEnumerable<TEntity> Project(TEvent evt);
}
