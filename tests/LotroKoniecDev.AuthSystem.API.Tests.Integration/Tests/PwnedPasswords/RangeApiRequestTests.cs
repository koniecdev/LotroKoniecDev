using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.PwnedPasswords;

/// <summary>
/// Sends a check through the client exactly as the auth API registers it, real handler included, to a
/// listener on this machine, and reads what arrives. A stub handler cannot show this: the trace headers
/// are added inside the real one. It starts no host and no container.
/// </summary>
public sealed class RangeApiRequestTests
{
    private const string HashPrefix = "5BAA6";

    [Fact]
    public async Task CheckAsync_DuringATracedRequest_SendsOnlyThePrefixWithNoTraceOrBaggageHeader()
    {
        // Arrange: a traced request with baggage, as an ASP.NET Core request with telemetry has
        using ActivitySource requestSource = new("RangeApiRequestTests");
        using ActivityListener activityListener = new()
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(activityListener);

        using LoopbackRangeApi rangeApi = LoopbackRangeApi.Start();
        await using ServiceProvider provider = BuildProvider(rangeApi.BaseAddress);
        IPwnedPasswordChecker checker = provider.GetRequiredService<IPwnedPasswordChecker>();

        using Activity? request = requestSource.StartActivity("POST auth/register");
        request.ShouldNotBeNull();
        request.AddBaggage("user", "someone");

        // Act
        PwnedPasswordVerdict verdict = await checker.CheckAsync("password", CancellationToken.None);

        // Assert
        verdict.ShouldBe(PwnedPasswordVerdict.NotFound);
        string received = await rangeApi.ReceivedRequestAsync;
        received.ShouldStartWith($"GET /range/{HashPrefix} HTTP/1.1\r\n");
        received.ShouldNotContain("traceparent", Case.Insensitive);
        received.ShouldNotContain("tracestate", Case.Insensitive);
        received.ShouldNotContain("baggage", Case.Insensitive);
        received.ShouldNotContain("Correlation-Context", Case.Insensitive);
        received.ShouldNotContain("Request-Id", Case.Insensitive);
        received.ShouldNotContain(request.TraceId.ToHexString(), Case.Insensitive);
    }

    private static ServiceProvider BuildProvider(Uri baseAddress)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddPwnedPasswords();

        // Only the address changes. Configured last, so it wins over the documented one.
        services.AddHttpClient(nameof(IPwnedPasswordChecker), client => client.BaseAddress = baseAddress);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A plain TCP listener that takes one request and answers it with one range line. It needs no
    /// administrator rights on any operating system, unlike <c>HttpListener</c> on Windows.
    /// </summary>
    private sealed class LoopbackRangeApi : IDisposable
    {
        private const string RangeBody = "0018A45C4D1DEF81644B54AB7F969B88D65:1\r\n";

        private readonly TcpListener _listener;

        private LoopbackRangeApi(TcpListener listener)
        {
            _listener = listener;
            BaseAddress = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            ReceivedRequestAsync = AnswerOneRequestAsync();
        }

        public Uri BaseAddress { get; }

        public Task<string> ReceivedRequestAsync { get; }

        public static LoopbackRangeApi Start()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return new LoopbackRangeApi(listener);
        }

        public void Dispose() => _listener.Stop();

        private async Task<string> AnswerOneRequestAsync()
        {
            using TcpClient connection = await _listener.AcceptTcpClientAsync();
            NetworkStream stream = connection.GetStream();

            StringBuilder request = new();
            byte[] buffer = new byte[4096];
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                int read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            byte[] body = Encoding.ASCII.GetBytes(RangeBody);
            byte[] head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(body);

            return request.ToString();
        }
    }
}
