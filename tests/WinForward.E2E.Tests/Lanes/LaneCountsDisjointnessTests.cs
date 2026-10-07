using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using WinForward.E2E.Client.Arms;
using WinForward.E2E.Client.Lanes;
using Xunit;

namespace WinForward.E2E.Tests.Lanes;

/// <summary>
/// The one-truth-per-number contract (D18.1, D18.5 #13): no name a <see cref="LaneCounts"/> member
/// carries may also appear on the policy — neither as a counter property nor as a private field, since
/// deleting the property and keeping the field is the regression the reflection pair exists to catch.
/// The second fact is the negative control: the same check has to see a collision it is given.
/// </summary>
public sealed class LaneCountsDisjointnessTests
{
    [Fact]
    public void NoLaneCountsNameIsAlsoAPolicyCounter()
    {
        var collisions = Collisions(typeof(LaneCounts), typeof(LanePolicyFake));

        Assert.Empty(collisions);

        // ...and the check is not vacuous: the policy really does expose counters and does own private
        // state, so the intersection above was taken over two non-empty name sets.
        var policy = typeof(LanePolicyFake);
        var properties = policy.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.Contains("AcceptedSends", Array.ConvertAll(properties, property => property.Name));
        Assert.Contains("InFlight", Array.ConvertAll(properties, property => property.Name));
        Assert.True(policy.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Length > 0);
    }

    [Fact]
    public void NoLaneCountsNameIsAlsoARealPolicyCounter()
    {
        // The two lane policies of the latency arm: the send side is the engine's (LaneCounts) and the
        // receive side is theirs (D18.1). Both halves of the check apply to each — a counter property
        // under an engine name, or the private field left behind after renaming one, would be two
        // truths about one number.
        AssertDisjoint(typeof(LatencyTcpState), "Started", "Received", "RemoteClosed", "Pending", "InFlight");
        AssertDisjoint(typeof(UdpLatencyState), "Started", "Received", "ForeignConnection", "Pending", "InFlight");
    }

    [Fact]
    public void TheCheckSeesBothACollidingPropertyAndACollidingPrivateField()
    {
        var collisions = Collisions(typeof(LaneCounts), typeof(CollidingCounters));

        Assert.Contains("Supplied", collisions);
        Assert.Contains("_sentOk", collisions);
    }

    /// <summary>
    /// The disjointness pair over one real state type, with its non-vacuity half: the intersection has
    /// to have been taken over two non-empty name sets, or a state that had simply renamed everything
    /// away would read as compliant.
    /// </summary>
    private static void AssertDisjoint(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties | DynamicallyAccessedMemberTypes.NonPublicFields)] Type state,
        params string[] expectedCounters)
    {
        Assert.Empty(Collisions(typeof(LaneCounts), state));

        var properties = Array.ConvertAll(state.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), property => property.Name);
        foreach (var counter in expectedCounters)
        {
            Assert.Contains(counter, properties);
        }

        Assert.True(state.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Length > 0, $"{state.Name} has to own private state for the field half of the check to bite");
    }

    /// <summary>
    /// The names a <see cref="LaneCounts"/> member carries, as seen on another type: instance properties
    /// (internal ones included, because that is what a policy's counters are) plus instance private
    /// fields, with a leading underscore ignored so a leftover <c>_sentOk</c> cannot hide behind it.
    /// </summary>
    private static string[] Collisions(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type counts,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties | DynamicallyAccessedMemberTypes.NonPublicFields)] Type policy)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in counts.GetProperties())
        {
            names.Add(property.Name);
        }

        var found = new List<string>();
        foreach (var property in policy.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (names.Contains(property.Name.TrimStart('_')))
            {
                found.Add(property.Name);
            }
        }

        foreach (var field in policy.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (names.Contains(field.Name.TrimStart('_')))
            {
                found.Add(field.Name);
            }
        }

        return [.. found];
    }

    /// <summary>
    /// The negative control: a type that carries one <see cref="LaneCounts"/> name as a property and one
    /// as a private field with no property in front of it. Both halves have to be found, or the check
    /// above could pass on a policy that had merely renamed its property.
    /// </summary>
    private sealed class CollidingCounters
    {
        private readonly long _sentOk;

        internal CollidingCounters()
        {
            _sentOk = 0;
        }

        // ReSharper disable once ConvertToAutoProperty // This half of the control owns a private field under a LaneCounts name on purpose: folding the getter into an auto-property would delete the very member the field check is aimed at.
        internal long Supplied => _sentOk;
    }
}
