using CanFx.Domain.Auditing;
using CanFx.Domain.Events;
using CanFx.Security.Entities;

namespace CanFx.Security.Tests;

public sealed class UserBaseClassTests
{
    private sealed class PlainUser : User<Guid>
    {
        public PlainUser(string email) : base(Guid.CreateVersion7(), email) { }

        public string FirstName { get; set; } = string.Empty;
    }

    private sealed record UserRenamed(Guid UserId, string FirstName) : DomainEvent;

    private sealed class EventUser : UserAggregateRoot<Guid>
    {
        public EventUser(string email) : base(Guid.CreateVersion7(), email) { }

        public string FirstName { get; private set; } = string.Empty;

        public void Rename(string firstName)
        {
            FirstName = firstName;
            RaiseDomainEvent(new UserRenamed(Id, firstName));
        }
    }

    [Fact]
    public void User_is_fully_audited_but_has_no_domain_events()
    {
        var user = new PlainUser("ada@example.com") { FirstName = "Ada" };

        Assert.IsAssignableFrom<IFullAudited>(user);
        Assert.False(user is IHasDomainEvents);
        Assert.Equal("ADA@EXAMPLE.COM", user.NormalizedEmail);
    }

    [Fact]
    public void UserAggregateRoot_keeps_user_behaviour_and_raises_domain_events()
    {
        var user = new EventUser("ada@example.com");

        user.Rename("Ada");

        Assert.IsAssignableFrom<User<Guid>>(user);
        Assert.IsAssignableFrom<IFullAudited>(user);
        UserRenamed raised = Assert.IsType<UserRenamed>(Assert.Single(user.DomainEvents));
        Assert.Equal("Ada", raised.FirstName);

        user.ClearDomainEvents();
        Assert.Empty(user.DomainEvents);
    }
}
