using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;

namespace GridGate.Money.Tests;

/// <summary>The one paid join at a time per person and group that the groups modules keep.</summary>
public class JoinsInProgressTests
{
    private static readonly UUID Ann = TestHelpers.ParseTail(0x611), Bob = TestHelpers.ParseTail(0x612);
    private static readonly UUID Club = TestHelpers.ParseTail(0x621), Guild = TestHelpers.ParseTail(0x622);

    [Fact]
    public void A_second_join_of_the_same_person_to_the_same_group_is_turned_away_until_the_first_ends()
    {
        var joins = new JoinsInProgress();

        Assert.True(joins.TryBegin(Ann, Club));
        Assert.False(joins.TryBegin(Ann, Club));

        joins.End(Ann, Club);

        Assert.True(joins.TryBegin(Ann, Club));
    }

    [Fact]
    public void Another_person_or_another_group_is_not_held_up()
    {
        var joins = new JoinsInProgress();
        Assert.True(joins.TryBegin(Ann, Club));

        Assert.True(joins.TryBegin(Bob, Club));
        Assert.True(joins.TryBegin(Ann, Guild));
    }

    [Fact]
    public async Task Of_many_threads_beginning_at_once_exactly_one_goes_ahead()
    {
        var joins = new JoinsInProgress();
        var winners = 0;
        using var start = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            start.Wait();
            if (joins.TryBegin(Ann, Club)) Interlocked.Increment(ref winners);
        })).ToArray();

        start.Set();
        await Task.WhenAll(threads).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, winners);
    }
}
