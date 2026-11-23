using GridGate.Launcher.Security;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>What the Region Server forms that ask for the owner's code say when two-factor isn't on yet.</summary>
public sealed class OwnerCodeStepWordsTests
{
    [Fact]
    public void Not_set_up_says_how_to_turn_it_on() =>
        Assert.Equal("This needs two-factor sign-in, which isn't on for your account on this computer. Run admin-link --two-factor on "
            + "this computer and open the link it prints to turn it on, then come back.", OwnerCodeStep.Words(OwnerCodeResult.NotSetUp));
}
