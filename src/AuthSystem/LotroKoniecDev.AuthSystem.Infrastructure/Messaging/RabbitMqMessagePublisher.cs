using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LotroKoniecDev.SharedKernel.Guards;
using RabbitMQ.Client;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

/// <summary>
/// Publishes over one long-lived connection and one long-lived channel, both opened on the first
/// publish instead of in the constructor: a web application must boot even while the broker is
/// still starting, and nothing in the request pipeline needs the broker to be reachable.
/// </summary>
internal sealed partial class RabbitMqMessagePublisher : IMessagePublisher, IAsyncDisposable
{
    private const string JsonContentType = "application/json";

    /// <summary>
    /// Confirmations turn <see cref="IChannel.BasicPublishAsync{TProperties}(string,string,bool,TProperties,ReadOnlyMemory{byte},CancellationToken)"/>
    /// from "written to the socket" into "the broker took responsibility for the message": without
    /// them the call returns before the broker has seen anything, the relay marks the outbox row
    /// processed, and a message lost in flight is lost for good. Tracking correlates the broker's
    /// nack or return back to the exact publish, so both arrive as an exception on this very call.
    /// </summary>
    private static readonly CreateChannelOptions ChannelOptions = new(
        publisherConfirmationsEnabled: true,
        publisherConfirmationTrackingEnabled: true);

    private readonly ConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RabbitMqMessagePublisher> _logger;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);

    private IConnection? _connection;
    private IChannel? _channel;
    private bool _disposed;

    public RabbitMqMessagePublisher(
        IOptions<RabbitMqOptions> options,
        TimeProvider timeProvider,
        ILogger<RabbitMqMessagePublisher> logger)
    {
        _connectionFactory = RabbitMqConnectionFactoryBuilder.Build(options.Value, "lotro-auth-api-publisher");
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task PublishAsync(
        string routingKey,
        string payload,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        Ensure.NotEmpty(messageId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        IChannel channel = await GetChannelAsync(cancellationToken);

        BasicProperties properties = new()
        {
            MessageId = messageId.ToString(),
            ContentType = JsonContentType,
            ContentEncoding = Encoding.UTF8.WebName,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(_timeProvider.GetUtcNow().ToUnixTimeSeconds())
        };

        byte[] body = Encoding.UTF8.GetBytes(payload);

        await channel.BasicPublishAsync(
            exchange: RabbitMqTopology.EmailsExchange,
            routingKey: routingKey,
            mandatory: true,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);

        LogMessagePublished(_logger, messageId, routingKey);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await CloseChannelAsync();
        await CloseConnectionAsync();
        _connectionGate.Dispose();
    }

    /// <summary>
    /// Returns the shared channel, opening the connection, the channel and the topology on first
    /// use and rebuilding whichever of them the broker has since torn down.
    /// </summary>
    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        IChannel? current = _channel;
        if (current is { IsOpen: true })
        {
            return current;
        }

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            await CloseChannelAsync();

            if (_connection is not { IsOpen: true })
            {
                await CloseConnectionAsync();
                _connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
                LogBrokerConnected(_logger, _connectionFactory.HostName, _connectionFactory.VirtualHost);
            }

            IChannel channel = await _connection.CreateChannelAsync(ChannelOptions, cancellationToken);
            _channel = channel;

            try
            {
                await RabbitMqTopologyDeclaration.DeclareAsync(channel, cancellationToken);
            }
            catch
            {
                // A failed declaration (typically PRECONDITION_FAILED on drifted queue arguments)
                // leaves a channel the broker already closed; dispose the client half instead of
                // handing it back for the next publish to trip over.
                await CloseChannelAsync();
                throw;
            }

            return channel;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task CloseChannelAsync()
    {
        IChannel? channel = _channel;
        _channel = null;

        if (channel is null)
        {
            return;
        }

        try
        {
            await channel.DisposeAsync();
        }
        catch (Exception ex)
        {
            LogBrokerTeardownWarning(_logger, ex);
        }
    }

    private async Task CloseConnectionAsync()
    {
        IConnection? connection = _connection;
        _connection = null;

        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            LogBrokerTeardownWarning(_logger, ex);
        }
    }

    [LoggerMessage(
        EventId = EventIds.BrokerConnected,
        Level = LogLevel.Information,
        Message = "Opened a broker connection to {Host} on virtual host {VirtualHost}")]
    private static partial void LogBrokerConnected(ILogger logger, string host, string virtualHost);

    [LoggerMessage(
        EventId = EventIds.BrokerMessagePublished,
        Level = LogLevel.Debug,
        Message = "Published message {MessageId} with routing key {RoutingKey}")]
    private static partial void LogMessagePublished(ILogger logger, Guid messageId, string routingKey);

    [LoggerMessage(
        EventId = EventIds.BrokerTeardownWarning,
        Level = LogLevel.Debug,
        Message = "Failed to cleanly close a broker connection or channel")]
    private static partial void LogBrokerTeardownWarning(ILogger logger, Exception exception);
}
