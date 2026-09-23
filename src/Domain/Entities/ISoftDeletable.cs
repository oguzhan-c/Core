namespace Domain.Entities;

/// <summary>
/// Explicit opt-in marker for soft deletion.
/// </summary>
/// <remarks>
/// Soft deletion is the framework default: every entity already exposes
/// <see cref="IEntityTimestamps.DeletedDate"/>, and an entity is soft-deleted unless it is
/// marked <see cref="IHardDeletable"/>. Implementing this interface therefore changes no
/// behavior — it only makes the intent explicit in the type's signature and lets tooling
/// detect it. Prefer it on entities where "this data is never physically removed" is an
/// important, documented decision.
/// <para>
/// Implementing both <see cref="ISoftDeletable"/> and <see cref="IHardDeletable"/> is
/// contradictory and is rejected at model-building time.
/// </para>
/// </remarks>
public interface ISoftDeletable
{
}