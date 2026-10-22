using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using RabbitMQ.Client;

public record TaskCompletedMessage(Guid TaskId, string Title, DateTime CompletedAt, Priority Priority);

public interface IEventPublisher
{
    void PublishTaskCompleted(TaskCompletedMessage message);
}

/// <summary>
/// Публикация не блокирует запрос: сообщения попадают в очередь в памяти,
/// а фоновый цикл отправляет их в RabbitMQ. Если RabbitMQ недоступен, только пишем в лог.
/// </summary>
public class RabbitMqService : IEventPublisher, IHostedService
{
    private const string Exchange = "task.events";
    private const string RoutingKey = "task.completed";
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Channel<TaskCompletedMessage> _queue = Channel.CreateUnbounded<TaskCompletedMessage>();
    private readonly ConnectionFactory _factory;
    private readonly ILogger<RabbitMqService> _logger;

    private IConnection? _connection;
    private IChannel? _channel;
    private Task _loop = Task.CompletedTask;

    public RabbitMqService(IConfiguration config, ILogger<RabbitMqService> logger)
    {
        _logger = logger;
        _factory = new ConnectionFactory
        {
            Uri = new Uri(config["RabbitMq:Uri"]!),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(3)
        };
    }

    // Вызывается из эндпоинта после успешного сохранения в БД
    public void PublishTaskCompleted(TaskCompletedMessage message)
    {
        if (!_queue.Writer.TryWrite(message))
            _logger.LogError("Queue is closed (shutting down), message lost: {Payload}", Serialize(message));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Соединение создаётся лениво, поэтому приложение стартует даже без RabbitMQ
        _loop = Task.Run(ProcessQueueAsync);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.Complete();

        try
        {
            // даём циклу отправить то, что уже в очереди
            await _loop.WaitAsync(PublishTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // HACK: не успел, доделал бы так: сохранял бы неотправленные сообщения (outbox-таблица) вместо потери
            _logger.LogWarning("Shutdown timeout, some task.completed messages may not have been sent");
        }

        await CloseAsync();
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var message in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await PublishAsync(message);
            }
            catch (Exception ex)
            {
                // Задача уже завершена в БД, поэтому просто пишем payload в лог
                _logger.LogError(ex, "Could not publish to RabbitMQ: {Payload}", Serialize(message));
            }
        }
    }

    private async Task PublishAsync(TaskCompletedMessage message)
    {
        using var cts = new CancellationTokenSource(PublishTimeout);

        if (_channel is not { IsOpen: true })
            await ConnectAsync(cts.Token);

        var props = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent
        };

        // HACK: не успел, доделал бы так: включил бы publisher confirms и повторял отправку при nack.
        // Сейчас "отправлено" значит только "передано клиенту RabbitMQ".
        await _channel!.BasicPublishAsync(Exchange, RoutingKey, mandatory: false, props,
            JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions), cts.Token);
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        await CloseAsync();

        _connection = await _factory.CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(cancellationToken: ct);
        await _channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
    }

    private async Task CloseAsync()
    {
        try
        {
            if (_channel is not null) await _channel.CloseAsync();
            if (_connection is not null) await _connection.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing RabbitMQ connection");
        }
        finally
        {
            _channel?.Dispose();
            _connection?.Dispose();
            _channel = null;
            _connection = null;
        }
    }

    private static string Serialize(TaskCompletedMessage m) => JsonSerializer.Serialize(m, JsonOptions);
}
