using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GridGate.Region.Money;
using Xunit;

namespace GridGate.Money.Tests;

/// <summary>What the client reports when a call fails, for the region log (found in the live checks).</summary>
public sealed class MoneyServiceClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static Task<MoneyAnswer> Transfer(MoneyServiceClient client) =>
        client.TransferAsync(Guid.NewGuid(), "pay_avatar", new MoneyWho(Guid.NewGuid(), "", Guid.NewGuid(), Guid.NewGuid()),
            new MoneyWho(Guid.NewGuid(), ""), 10, Guid.NewGuid(), null, null, Ct);

    [Fact]
    public async Task A_failed_call_is_reported_without_secrets()
    {
        var failures = new List<string>();
        var session = Guid.NewGuid();
        using var client = new MoneyServiceClient(new Uri($"https://127.0.0.1:{FreePort()}/money/v1/"), "the-secret-key", new string('A', 64), failures.Add);

        var answer = await client.BalanceAsync(new MoneyWho(Guid.NewGuid(), "", session, Guid.NewGuid()), Ct);

        Assert.Equal("unavailable", answer.Code);
        var line = Assert.Single(failures);
        Assert.StartsWith("balance: ", line);
        Assert.DoesNotContain("the-secret-key", line);
        Assert.DoesNotContain(session.ToString(), line);
    }

    [Fact]
    public async Task A_call_whose_connection_is_refused_is_unavailable_but_not_lost()
    {
        // Nothing listens (the service is restarting): no request was sent, so no coins can have moved. Still worth
        // sending again, unlike a call the service turned away.
        using var client = new MoneyServiceClient(new Uri($"https://127.0.0.1:{FreePort()}/money/v1/"), "key", new string('A', 64));

        var answer = await Transfer(client);

        Assert.Equal("unavailable", answer.Code);
        Assert.False(answer.Lost);
        Assert.False(answer.Final);
    }

    [Fact]
    public async Task A_call_whose_connection_closes_without_an_answer_is_lost()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var closing = Task.Run(async () =>
            {
                using var tcp = await listener.AcceptTcpClientAsync(Ct);
                tcp.Client.Close();
            }, Ct);
            using var client = new MoneyServiceClient(new Uri($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/money/v1/"), "key", new string('A', 64));

            var answer = await Transfer(client);
            await closing;

            Assert.Equal("unavailable", answer.Code);
            Assert.True(answer.Lost);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void An_outcome_unknown_answer_is_lost()
    {
        var answer = new MoneyAnswer("outcome_unknown");

        Assert.True(answer.Lost);
        Assert.False(answer.Ok);
        Assert.False(answer.Final);
        Assert.False(new MoneyAnswer("unavailable").Lost);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(413)]
    public async Task A_call_turned_away_with_a_4xx_is_not_lost(int status)
    {
        await using var server = StatusServer.Start(status);
        using var client = new MoneyServiceClient(server.Uri, "key", server.Fingerprint);

        var answer = await Transfer(client);

        Assert.Equal("unavailable", answer.Code);
        Assert.False(answer.Lost);
        Assert.True(answer.Final);
        Assert.Equal(1, server.Requests);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public async Task A_call_answered_with_a_5xx_is_lost(int status)
    {
        await using var server = StatusServer.Start(status);
        using var client = new MoneyServiceClient(server.Uri, "key", server.Fingerprint);

        var answer = await Transfer(client);

        Assert.Equal("unavailable", answer.Code);
        Assert.True(answer.Lost);
        Assert.False(answer.Final);
        Assert.Equal(1, server.Requests);
    }

    /// <summary>An HTTPS listener on loopback that answers every request with <c>status</c> and an empty body.</summary>
    private sealed class StatusServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _cert;
        private readonly int _status;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;

        private StatusServer(int status)
        {
            _status = status;
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            // Reloaded from PFX: Windows' TLS needs a key it can find again.
            _cert = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
            Fingerprint = Convert.ToHexString(SHA256.HashData(_cert.RawData));
            _listener.Start();
            Uri = new Uri($"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/money/v1/");
            _loop = Task.Run(LoopAsync);
        }

        public static StatusServer Start(int status) => new(status);

        public Uri Uri { get; }

        public string Fingerprint { get; }

        public int Requests => Volatile.Read(ref _requests);

        private async Task LoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var tls = new SslStream(tcp.GetStream());
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _cert }, _stop.Token);
                    await ReadRequestAsync(tls);
                    Interlocked.Increment(ref _requests);
                    var reply = $"HTTP/1.1 {_status} Status\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await tls.WriteAsync(Encoding.ASCII.GetBytes(reply), _stop.Token);
                    await tls.FlushAsync(_stop.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException or IOException
                or System.Security.Authentication.AuthenticationException)
            {
                // Stopped.
            }
        }

        /// <summary>Reads the headers and as many body bytes as Content-Length says.</summary>
        private async Task ReadRequestAsync(Stream s)
        {
            var seen = new List<byte>();
            var one = new byte[1];
            while (!(seen.Count >= 4 && seen[^4] == '\r' && seen[^3] == '\n' && seen[^2] == '\r' && seen[^1] == '\n'))
            {
                if (await s.ReadAsync(one, _stop.Token) == 0) return;
                seen.Add(one[0]);
            }
            var headers = Encoding.ASCII.GetString([.. seen]);
            var line = headers.Split("\r\n").FirstOrDefault(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var left = line is null ? 0 : int.Parse(line["Content-Length:".Length..].Trim());
            var buffer = new byte[4096];
            while (left > 0)
            {
                var n = await s.ReadAsync(buffer.AsMemory(0, Math.Min(left, buffer.Length)), _stop.Token);
                if (n == 0) return;
                left -= n;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _cert.Dispose();
            _stop.Dispose();
        }
    }
}
