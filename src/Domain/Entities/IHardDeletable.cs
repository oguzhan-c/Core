namespace Domain.Entities;

/// <summary>
/// Opt-out marker that switches an entity to physical (hard) deletion.
/// </summary>
/// <remarks>
/// Because soft deletion is the default, this marker is how an entity opts out: deleting it
/// removes the row from the database. For hard-deletable entities the persistence layer does
/// not map <see cref="IEntityTimestamps.DeletedDate"/> (so there is no unused column) and does
/// not apply the soft-delete query filter.
/// <para>
/// Implementing both <see cref="IHardDeletable"/> and <see cref="ISoftDeletable"/> is
/// contradictory and is rejected at model-building time.
/// </para>
/// </remarks>
/// <example>
/// A one-time token row that should be physically removed once consumed:
/// <code>
/// public sealed class OneTimeToken : Entity&lt;Guid&gt;, IHardDeletable
/// {
///     public string Value { get; private set; } = default!;
/// }
/// </code>
/// </example>
public interface IHardDeletable
{
}