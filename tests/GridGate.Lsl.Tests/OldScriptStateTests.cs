using System.Xml;
using OpenMetaverse;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using Xunit;

namespace GridGate.Lsl.Tests;

/// <summary>
/// A saved script state keeps its PERMISSION_DEBIT only when this version of the program wrote it: the state carries a
/// mark of this program. A state from before that (no mark) loses the grant once, at load, and the script asks again.
/// </summary>
public sealed class OldScriptStateTests
{
    private static readonly UUID Owner = new("00000000-0000-0000-0000-000000000001");
    private const int Anim = ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION;
    private const int Debit = ScriptBaseClass.PERMISSION_DEBIT;

    private static XmlElement NewState()
    {
        var doc = new XmlDocument();
        var state = doc.CreateElement("", "ScriptState", "");
        state.SetAttribute("Engine", "YEngine");
        doc.AppendChild(state);
        return state;
    }

    private static int Restored(XmlElement state, StateSource source, bool fromOutside) =>
        DebitPermission.Restored(Debit | Anim, Owner, Owner,
            DebitPermission.FromRunningScript(source, fromOutside, DebitPermission.IsMarked(state)));

    [Fact]
    public void A_state_the_program_marks_is_read_back_as_marked()
    {
        var state = NewState();

        DebitPermission.Mark(state);

        Assert.True(DebitPermission.IsMarked(state));
    }

    [Fact]
    public void A_state_without_the_mark_is_not_marked() => Assert.False(DebitPermission.IsMarked(NewState()));

    [Fact]
    public void A_state_with_another_value_in_the_mark_is_not_marked()
    {
        var state = NewState();
        state.SetAttribute(DebitPermission.MarkAttribute, "0");

        Assert.False(DebitPermission.IsMarked(state));
    }

    [Theory]
    [InlineData(StateSource.RegionStart)]
    [InlineData(StateSource.PrimCrossing)]
    [InlineData(StateSource.Teleporting)]
    public void A_marked_state_keeps_the_owners_debit(StateSource source)
    {
        var state = NewState();
        DebitPermission.Mark(state);

        Assert.Equal(Debit | Anim, Restored(state, source, fromOutside: false));
    }

    [Theory]
    [InlineData(StateSource.RegionStart)]
    [InlineData(StateSource.PrimCrossing)]
    [InlineData(StateSource.Teleporting)]
    public void A_state_from_before_the_mark_loses_the_debit_and_keeps_the_rest(StateSource source) =>
        Assert.Equal(Anim, Restored(NewState(), source, fromOutside: false));

    [Fact]
    public void A_marked_state_from_outside_still_loses_the_debit()
    {
        // Anyone can copy the mark into a forged state; it only matters for state this program reads itself.
        var state = NewState();
        DebitPermission.Mark(state);

        Assert.Equal(Anim, Restored(state, StateSource.PrimCrossing, fromOutside: true));
        Assert.Equal(Anim, Restored(state, StateSource.RegionStart, fromOutside: true));
    }

    [Fact]
    public void A_marked_state_from_a_rez_still_loses_the_debit()
    {
        var state = NewState();
        DebitPermission.Mark(state);

        Assert.Equal(Anim, Restored(state, StateSource.NewRez, fromOutside: false));
    }
}
