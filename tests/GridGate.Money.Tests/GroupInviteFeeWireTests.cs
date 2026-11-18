using System.Net;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Groups;
using Xunit;

namespace GridGate.Money.Tests;

/// <summary>The join fee an invitation showed, on the wire between a region and Robust's groups service.</summary>
public class GroupInviteFeeWireTests
{
    private static GroupInviteInfo Invite(int? fee) => new()
    {
        GroupID = UUID.Random(), RoleID = UUID.Zero, AgentID = UUID.Random().ToString(), InviteID = UUID.Random(), Fee = fee,
    };

    [Fact]
    public void An_invitation_with_a_fee_round_trips_with_it()
    {
        var back = GroupsDataUtils.GroupInviteInfo(GroupsDataUtils.GroupInviteInfo(Invite(25)));
        Assert.Equal(25, back.Fee);
    }

    [Fact]
    public void An_invitation_without_a_fee_sends_no_fee_field_and_reads_back_unknown()
    {
        var dict = GroupsDataUtils.GroupInviteInfo(Invite(null));
        Assert.False(dict.ContainsKey("Fee"));
        Assert.Null(GroupsDataUtils.GroupInviteInfo(dict).Fee);
    }

    [Fact]
    public void A_fee_that_is_not_a_number_reads_as_unknown()
    {
        var dict = GroupsDataUtils.GroupInviteInfo(Invite(25));
        dict["Fee"] = "abc";
        Assert.Null(GroupsDataUtils.GroupInviteInfo(dict).Fee);
        dict["Fee"] = "";
        Assert.Null(GroupsDataUtils.GroupInviteInfo(dict).Fee);
    }

    /// <summary>Runs the remote connector against a local listener and returns the form body the service would receive.</summary>
    private static string BodySentFor(int? fee)
    {
        if (WebUtil.SharedSocketsHttpHandler is null) WebUtil.SetupHTTPClients(false, false, null, 32);
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var body = Task.Run(() =>
        {
            var ctx = listener.GetContext();
            string received;
            using (var reader = new StreamReader(ctx.Request.InputStream)) received = reader.ReadToEnd();
            var reply = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><ServerResponse><RESULT>True</RESULT></ServerResponse>");
            ctx.Response.ContentLength64 = reply.Length;
            ctx.Response.OutputStream.Write(reply, 0, reply.Length);
            ctx.Response.Close();
            return received;
        });

        var config = new IniConfigSource();
        config.AddConfig("Groups").Set("GroupsServerURI", $"http://127.0.0.1:{port}");
        var connector = new GroupsServiceRemoteConnector(config);
        Assert.True(connector.AddAgentToGroupInvite(UUID.Random().ToString(), UUID.Random(), UUID.Random(), UUID.Zero, UUID.Random().ToString(), fee));
        return body.GetAwaiter().GetResult();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public void The_invite_request_carries_the_fee_when_it_is_known()
    {
        var body = BodySentFor(7);
        Assert.Contains("Fee=7", body);
    }

    [Fact]
    public void The_invite_request_carries_no_fee_when_it_is_not_known()
    {
        var body = BodySentFor(null);
        Assert.DoesNotContain("Fee=", body);
        Assert.Contains("OP=ADD", body);
    }
}
