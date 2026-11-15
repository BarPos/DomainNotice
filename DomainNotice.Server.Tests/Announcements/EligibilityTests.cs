using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Announcements;
using DomainNotice.Server.Data.Entities;

namespace DomainNotice.Server.Tests.Announcements;

/// <summary>The pure targeting rules (no database).</summary>
public class EligibilityTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ComputerId = Guid.NewGuid();
    private static readonly Guid ComputerAd = Guid.NewGuid();
    private static readonly Guid UserAd = Guid.NewGuid();
    private static readonly Guid UserGroup = Guid.NewGuid();
    private static readonly Guid ComputerGroup = Guid.NewGuid();
    private const string UserDn = "cn=alice,ou=staff,ou=sales,dc=corp,dc=example";
    private const string ComputerDn = "cn=pc1,ou=workstations,ou=it,dc=corp,dc=example";

    private static AnnouncementRule Rule(bool draft = false, int start = -60, int end = 60, int max = 1, int repeat = 0,
        params TargetRule[] targets) =>
        new(Guid.NewGuid(), draft, Now.AddMinutes(start), Now.AddMinutes(end), max, repeat, targets);

    private static ComputerFacts Computer(AdLinkState state = AdLinkState.Proven, bool revoked = false, bool replaced = false)
    {
        var linked = state is AdLinkState.Proven or AdLinkState.MatchedByName;
        return new ComputerFacts(ComputerId, state, revoked, replaced, state == AdLinkState.None ? null : ComputerAd,
            linked ? ComputerDn : null, linked ? new HashSet<Guid> { ComputerGroup } : new HashSet<Guid>());
    }

    private static UserFacts User(string name = @"CORP\alice") =>
        new(name, new HashSet<Guid> { UserAd }, [UserDn], new HashSet<Guid> { UserGroup });

    private static bool Due(AnnouncementRule rule, UserAnnouncementHistory? history = null, ComputerFacts? computer = null,
        UserFacts? user = null, AwaitingApprovalPolicy policy = AwaitingApprovalPolicy.EveryoneAndUser) =>
        Eligibility.IsDue(rule, history ?? UserAnnouncementHistory.None, computer ?? Computer(), user ?? User(), policy, Now);

    [Fact] public void Inside_window_is_due() => Assert.True(Due(Rule()));
    [Fact] public void Before_start_is_not_due() => Assert.False(Due(Rule(start: 1)));
    [Fact] public void After_end_is_not_due() => Assert.False(Due(Rule(start: -60, end: -1)));
    [Fact] public void Start_and_end_are_inclusive()
    {
        Assert.True(Due(Rule(start: 0, end: 10)));
        Assert.True(Due(Rule(start: -10, end: 0)));
    }
    [Fact] public void Draft_is_never_due() => Assert.False(Due(Rule(draft: true)));

    [Fact]
    public void Display_limit_counts_shown_deliveries()
    {
        Assert.False(Due(Rule(max: 1), new UserAnnouncementHistory(1, Now.AddDays(-1), false)));
        Assert.True(Due(Rule(max: 2, repeat: 0), new UserAnnouncementHistory(1, Now.AddDays(-1), false)));
        Assert.False(Due(Rule(max: 2), new UserAnnouncementHistory(2, Now.AddDays(-1), false)));
    }

    [Fact]
    public void Repeat_interval_is_measured_from_last_shown()
    {
        var rule = Rule(max: 3, repeat: 60);
        Assert.False(Due(rule, new UserAnnouncementHistory(1, Now.AddMinutes(-59), false)));
        Assert.True(Due(rule, new UserAnnouncementHistory(1, Now.AddMinutes(-60), false)));
        Assert.True(Due(rule, UserAnnouncementHistory.None));
    }

    [Fact]
    public void Unconfirmed_shown_delivery_elsewhere_blocks_a_second_one() =>
        Assert.False(Due(Rule(max: 5), new UserAnnouncementHistory(1, Now.AddDays(-1), true)));

    [Fact] public void No_targets_means_everyone() => Assert.True(Due(Rule()));

    [Fact]
    public void Any_target_matches()
    {
        var rule = Rule(targets: [new TargetRule(TargetKind.User, Guid.NewGuid()), new TargetRule(TargetKind.UserGroup, UserGroup)]);
        Assert.True(Due(rule));
        Assert.False(Due(Rule(targets: [new TargetRule(TargetKind.User, Guid.NewGuid())])));
    }

    [Fact]
    public void User_target_by_directory_guid() =>
        Assert.True(Due(Rule(targets: new TargetRule(TargetKind.User, UserAd))));

    [Theory]
    [InlineData("alice", @"CORP\alice", true)]
    [InlineData("alice", "alice@corp", true)]
    [InlineData("ALICE", @"corp\Alice", true)]
    [InlineData(@"CORP\alice", @"corp\alice", true)]
    [InlineData(@"CORP\alice", @"OTHER\alice", false)]
    [InlineData("bob", @"CORP\alice", false)]
    public void User_target_by_name_without_directory(string target, string reported, bool expected) =>
        Assert.Equal(expected, Due(Rule(targets: new TargetRule(TargetKind.User, Value: target)), user: UserFacts.NameOnly(reported)));

    [Fact]
    public void Computer_target_by_enrolled_id() =>
        Assert.True(Due(Rule(targets: new TargetRule(TargetKind.Computer, ComputerId: ComputerId)), computer: Computer(AdLinkState.None)));

    [Theory]
    [InlineData(AdLinkState.Proven, true)]
    [InlineData(AdLinkState.MatchedByName, true)]
    [InlineData(AdLinkState.KeptSeparate, false)]
    [InlineData(AdLinkState.AwaitingApproval, false)]
    public void Computer_target_by_ad_object_needs_a_link(AdLinkState state, bool expected) =>
        Assert.Equal(expected, Due(Rule(targets: new TargetRule(TargetKind.Computer, ComputerAd)), computer: Computer(state)));

    [Fact]
    public void Group_targets_use_transitive_membership()
    {
        Assert.True(Due(Rule(targets: new TargetRule(TargetKind.UserGroup, UserGroup))));
        Assert.True(Due(Rule(targets: new TargetRule(TargetKind.ComputerGroup, ComputerGroup))));
        Assert.False(Due(Rule(targets: new TargetRule(TargetKind.UserGroup, ComputerGroup))));
        Assert.False(Due(Rule(targets: new TargetRule(TargetKind.ComputerGroup, UserGroup))));
    }

    [Theory]
    [InlineData("ou=sales,dc=corp,dc=example", true)]          // user's parent's parent
    [InlineData("ou=staff,ou=sales,dc=corp,dc=example", true)] // user's parent
    [InlineData("ou=it,dc=corp,dc=example", true)]             // computer's subtree
    [InlineData("ou=xsales,dc=corp,dc=example", false)]        // suffix without the comma boundary
    [InlineData("ou=hr,dc=corp,dc=example", false)]
    public void Ou_target_matches_subtree(string ouDn, bool expected) =>
        Assert.Equal(expected, Due(Rule(targets: new TargetRule(TargetKind.OrganizationalUnit, Guid.NewGuid(), OrganizationalUnitDnLower: ouDn))));

    [Fact]
    public void Ou_equal_to_object_dn_matches() => Assert.True(Eligibility.IsInSubtree(UserDn, UserDn));

    [Fact]
    public void Awaiting_approval_default_policy_gets_everyone_and_user_only()
    {
        var c = Computer(AdLinkState.AwaitingApproval);
        Assert.True(Due(Rule(), computer: c));
        Assert.True(Due(Rule(targets: new TargetRule(TargetKind.User, UserAd)), computer: c));
        Assert.False(Due(Rule(targets: new TargetRule(TargetKind.Computer, ComputerId: ComputerId)), computer: c));
        Assert.False(Due(Rule(targets: new TargetRule(TargetKind.UserGroup, UserGroup)), computer: c));
        Assert.False(Due(Rule(targets: new TargetRule(TargetKind.OrganizationalUnit, Guid.NewGuid(), OrganizationalUnitDnLower: "dc=corp,dc=example")), computer: c));
    }

    [Fact]
    public void Awaiting_approval_policy_nothing_gets_nothing()
    {
        var c = Computer(AdLinkState.AwaitingApproval);
        Assert.False(Due(Rule(), computer: c, policy: AwaitingApprovalPolicy.Nothing));
        Assert.False(Due(Rule(targets: new TargetRule(TargetKind.User, UserAd)), computer: c, policy: AwaitingApprovalPolicy.Nothing));
    }

    [Theory]
    [InlineData(AdLinkState.None)]
    [InlineData(AdLinkState.KeptSeparate)]
    public void Unlinked_computers_ignore_group_and_ou_targets(AdLinkState state)
    {
        // Even when the directory facts would match, an unlinked computer gets only Everyone, User and ComputerId.
        var c = new ComputerFacts(ComputerId, state, false, false, ComputerAd, ComputerDn, new HashSet<Guid> { ComputerGroup });
        Assert.True(Due(Rule(), computer: c));
        Assert.True(Due(Rule(targets: new TargetRule(TargetKind.User, UserAd)), computer: c));
        Assert.True(Due(Rule(targets: new TargetRule(TargetKind.Computer, ComputerId: ComputerId)), computer: c));
        Assert.False(Due(Rule(targets: new TargetRule(TargetKind.ComputerGroup, ComputerGroup)), computer: c));
        Assert.False(Due(Rule(targets: new TargetRule(TargetKind.UserGroup, UserGroup)), computer: c));
        Assert.False(Due(Rule(targets: new TargetRule(TargetKind.OrganizationalUnit, Guid.NewGuid(), OrganizationalUnitDnLower: "dc=corp,dc=example")), computer: c));
        Assert.False(Due(Rule(targets: new TargetRule(TargetKind.Computer, ComputerAd)), computer: c));
    }

    [Fact]
    public void Revoked_and_replaced_computers_get_nothing()
    {
        Assert.False(Due(Rule(), computer: Computer(revoked: true)));
        Assert.False(Due(Rule(), computer: Computer(replaced: true)));
    }

    [Fact]
    public void FirstDue_respects_order()
    {
        var a = Rule(start: 1); // not started
        var b = Rule();
        var c = Rule();
        var due = Eligibility.FirstDue([a, b, c], _ => UserAnnouncementHistory.None, Computer(), User(), AwaitingApprovalPolicy.EveryoneAndUser, Now);
        Assert.Equal(b.Id, due!.Id);
    }
}
