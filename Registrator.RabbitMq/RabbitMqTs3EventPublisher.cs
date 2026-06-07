using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Serilog;

namespace Registrator.RabbitMq;

public class RabbitMqTs3EventPublisher : ITs3EventPublisher, IAsyncDisposable
{
	/// <summary>
	/// Логгер, используемый для записи диагностической информации и сообщений об ошибках
	/// в процессе работы RabbitMqTs3EventPublisher.
	/// </summary>
	private static readonly ILogger Log = Serilog.Log.ForContext<RabbitMqTs3EventPublisher>();

    /// <summary>
    /// Настройки подключения к брокеру сообщений RabbitMQ, используемые для публикации событий.
    /// </summary>
    private readonly RabbitMqSettings _settings;

    /// <summary>
    /// Соединение с сервером RabbitMQ, используемое для публикации событий.
    /// </summary>
    private IConnection? _connection;

    /// <summary>
    /// Объект канала для взаимодействия с RabbitMQ.
    /// Используется для выполнения операций, таких как публикация сообщений или объявление обменников.
    /// Канал создается при установке соединения с RabbitMQ и применяется для передачи данных.
    /// </summary>
    private IChannel? _channel;

    /// <summary>
    /// Семафор, используемый для обеспечения однократной инициализации подключения к RabbitMQ.
    /// </summary>
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>
    /// Указывает, был ли успешно инициализирован экземпляр RabbitMqTs3EventPublisher.
    /// </summary>
    private bool _initialized;

    /// <summary>
    /// Флаг, указывающий на то, что последняя попытка подключения к RabbitMQ завершилась неудачей.
    /// Используется для предотвращения частых повторных попыток подключения.
    /// </summary>
    private bool _connectionFailed;

    /// <summary>
    /// Отмечает время последней попытки подключения к RabbitMQ.
    /// Используется для ограничения частоты повторных попыток подключения,
    /// чтобы не происходило частое обращение при неудачных попытках.
    /// </summary>
    private DateTimeOffset _lastConnectionAttempt = DateTimeOffset.MinValue;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Класс-реализация для публикации событий TeamSpeak 3 в брокер сообщений RabbitMQ.
    /// </summary>
    public RabbitMqTs3EventPublisher(IOptions<RabbitMqSettings> options)
    {
        _settings = options.Value;
    }

    /// <summary>
    /// Обеспечивает инициализацию подключения к RabbitMQ перед выполнением операций.
    /// Метод гарантирует, что подключение установлено и готово для отправки сообщений.
    /// </summary>
    /// <param name="ct">Токен отмены, используемый для прерывания процесса инициализации при необходимости.</param>
    /// <returns>Возвращает асинхронную задачу, представляющую процесс завершения инициализации.</returns>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        // Rate limit: не пытаться подключаться чаще раз в 30 секунд после неудачи
        if (_connectionFailed && DateTimeOffset.UtcNow - _lastConnectionAttempt < ReconnectDelay)
            return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            if (_connectionFailed && DateTimeOffset.UtcNow - _lastConnectionAttempt < ReconnectDelay)
                return;

            _lastConnectionAttempt = DateTimeOffset.UtcNow;

            var factory = new ConnectionFactory
            {
                HostName = _settings.Host,
                Port = _settings.Port,
                UserName = _settings.UserName,
                Password = _settings.Password,
                VirtualHost = _settings.VirtualHost,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true
            };

            _connection = await factory.CreateConnectionAsync(ct);
            _channel = await _connection.CreateChannelAsync(cancellationToken: ct);

            await _channel.ExchangeDeclareAsync(
                exchange: _settings.ExchangeName,
                type: ExchangeType.Fanout,
                durable: true,
                autoDelete: false,
                cancellationToken: ct);

            _initialized = true;
            _connectionFailed = false;
            Log.Information("RabbitMQ publisher connected to {Host}:{Port}, exchange '{Exchange}'",
                _settings.Host, _settings.Port, _settings.ExchangeName);
        }
        catch (Exception ex)
        {
            _connectionFailed = true;
            Log.Error(ex, "Failed to initialize RabbitMQ connection to {Host}:{Port}. Will retry in {Delay}s.",
                _settings.Host, _settings.Port, ReconnectDelay.TotalSeconds);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task PublishAsync(Ts3EventEnvelope eventData, CancellationToken ct = default)
    {
        try
        {
            await EnsureInitializedAsync(ct);

            if (!_initialized || _channel == null)
            {
                Log.Debug("RabbitMQ not initialized, dropping event {EventType}", eventData.EventType);
                return;
            }

            var json = JsonSerializer.Serialize(eventData, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var body = Encoding.UTF8.GetBytes(json);

            var props = new BasicProperties
            {
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            };

            await _channel.BasicPublishAsync(
                exchange: _settings.ExchangeName,
                routingKey: eventData.EventType,
                mandatory: false,
                basicProperties: props,
                body: body,
                cancellationToken: ct);

            Log.Debug("Published RabbitMQ event {EventType}", eventData.EventType);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to publish RabbitMQ event {EventType}", eventData.EventType);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_channel != null)
                await _channel.CloseAsync();
            if (_connection != null)
                await _connection.CloseAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error disposing RabbitMQ publisher");
        }

        _channel?.Dispose();
        _connection?.Dispose();
        _initLock.Dispose();
    }
}
