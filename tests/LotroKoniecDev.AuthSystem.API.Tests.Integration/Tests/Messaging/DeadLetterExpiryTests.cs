using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using RabbitMQ.Client;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Messaging;

/// <summary>
/// Proves on a real broker that the expiry policy <c>scripts/hetzner/deploy.sh</c> sets (ADR-0036 §6,
/// #1085) removes a parked message from <see cref="RabbitMqTopology.EmailDeadLetterQueue"/> by itself
/// once it is older than the limit. Nothing consumes the queue, and its declared arguments do not
/// change. The name, the pattern and the definition are read from the script, so a typo there fails
/// here under RabbitMQ's own rules. Only the message-ttl is cut from 7 days to seconds, so the test can
/// watch it work. This class has its own broker, so its policy never expires a message that
/// <see cref="DeadLetterTopologyTests"/> is waiting to read.
/// </summary>
public sealed class DeadLetterExpiryTests : IClassFixture<RabbitMqBrokerFixture>, IAsyncLifetime
{
    /// <summary>
    /// Five seconds: long enough to see the message park first even on a busy runner, short enough to
    /// watch it expire.
    /// </summary>
    private const long ShortMessageTtlMilliseconds = 5000;

    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExpiryTimeout = TimeSpan.FromSeconds(30);

    private readonly RabbitMqBrokerFixture _broker;
    private IConnection? _connection;
    private IChannel? _channel;

    private IChannel Channel => _channel ?? throw new InvalidOperationException("Fixture not initialized.");

    public async Task InitializeAsync()
    {
        _connection = await _broker.ConnectAsync(CancellationToken.None);
        _channel = await _connection.CreateChannelAsync();
        await RabbitMqTopologyDeclaration.DeclareAsync(_channel, CancellationToken.None);
        await _channel.QueuePurgeAsync(RabbitMqTopology.EmailQueue);
        await _channel.QueuePurgeAsync(RabbitMqTopology.EmailDeadLetterQueue);
    }

    public async Task DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Broker_ShouldDropParkedMessage_WhenItOutlivesTheDeployedExpiryPolicy()
    {
        // Arrange: the deploy's own policy, only shorter, on a queue that already exists, as on a live box
        string deployScript = await File.ReadAllTextAsync(
            Path.Combine(FindSolutionDirectory(), "scripts", "hetzner", "deploy.sh"));
        JsonObject definition = JsonNode.Parse(ReadShellValue(deployScript, "EMAIL_DLQ_EXPIRY_DEFINITION"))!.AsObject();
        definition.ContainsKey("message-ttl").ShouldBeTrue("the policy in deploy.sh does not set message-ttl");
        definition["message-ttl"] = ShortMessageTtlMilliseconds;
        await _broker.SetOperatorPolicyAsync(
            ReadShellValue(deployScript, "EMAIL_DLQ_EXPIRY_POLICY"),
            ReadShellValue(deployScript, "EMAIL_DLQ_EXPIRY_PATTERN"),
            definition.ToJsonString());
        await PublishAsync(Encoding.UTF8.GetBytes("""{"poison":true}"""));
        BasicGetResult delivery = (await GetWithinTimeoutAsync(RabbitMqTopology.EmailQueue, DeliveryTimeout)).ShouldNotBeNull();

        // Act: the consumer's poison path parks the message
        await Channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);

        // Assert: it parked first, so the empty queue afterwards is the expiry and not a lost message
        uint parked = await WaitForMessageCountAsync(RabbitMqTopology.EmailDeadLetterQueue, expected: 1, DeliveryTimeout);
        parked.ShouldBe(1u, "the rejected message never reached the parking lot");
        uint afterExpiry = await WaitForMessageCountAsync(RabbitMqTopology.EmailDeadLetterQueue, expected: 0, ExpiryTimeout);
        afterExpiry.ShouldBe(0u, "the parked message outlived the policy's message-ttl");
    }

    public DeadLetterExpiryTests(RabbitMqBrokerFixture broker)
    {
        _broker = broker;
    }

    private async Task PublishAsync(byte[] body)
    {
        BasicProperties properties = new()
        {
            MessageId = Guid.NewGuid().ToString(),
            DeliveryMode = DeliveryModes.Persistent
        };

        await Channel.BasicPublishAsync(
            exchange: RabbitMqTopology.EmailsExchange,
            routingKey: RabbitMqTopology.EmailConfirmationRoutingKey,
            mandatory: true,
            basicProperties: properties,
            body: body);
    }

    private async Task<BasicGetResult?> GetWithinTimeoutAsync(string queue, TimeSpan timeout)
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        while (true)
        {
            BasicGetResult? delivery = await Channel.BasicGetAsync(queue, autoAck: false);
            if (delivery is not null)
            {
                return delivery;
            }

            if (elapsed.Elapsed >= timeout)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>
    /// Polls the queue until it holds <paramref name="expected"/> messages or the timeout passes, and
    /// returns the last count it saw. The broker dead-letters and expires messages on its own schedule,
    /// so one immediate read would race it.
    /// </summary>
    private async Task<uint> WaitForMessageCountAsync(string queue, uint expected, TimeSpan timeout)
    {
        Stopwatch elapsed = Stopwatch.StartNew();

        while (true)
        {
            QueueDeclareOk state = await Channel.QueueDeclarePassiveAsync(queue);
            if (state.MessageCount == expected || elapsed.Elapsed >= timeout)
            {
                return state.MessageCount;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>
    /// Reads a plain <c>NAME='value'</c> line of a shell script.
    /// </summary>
    private static string ReadShellValue(string script, string name)
    {
        Match match = Regex.Match(script, $"^{name}='(?<value>[^']*)'\r?$", RegexOptions.Multiline);

        return match.Success
            ? match.Groups["value"].Value
            : throw new InvalidOperationException($"deploy.sh has no {name}='…' line.");
    }

    private static string FindSolutionDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LotroKoniecDev.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find the solution directory (LotroKoniecDev.slnx) above {AppContext.BaseDirectory}.");
    }
}
