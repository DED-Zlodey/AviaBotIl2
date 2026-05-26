using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using AviaBot.Models;

namespace AviaBot.Services;

public class RabbitMqConsumer : IHostedService, IDisposable
{
	/// <summary>
	/// Настройки для JSON-сериализации и десериализации.
	/// Используется для управления политиками именования свойств (например, использование camelCase)
	/// и игнорирования регистра символов при сопоставлении имен свойств.
	/// </summary>
	private static readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	/// <summary>
	/// Логгер, используемый для записи информации, предупреждений и ошибок, возникающих в процессе работы RabbitMQ consumer.
	/// Предоставляет возможность записывать сообщения для отладки и мониторинга работы сервиса.
	/// </summary>
	private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<RabbitMqConsumer>();

	/// <summary>
	/// Настройки подключения и конфигурации RabbitMQ.
	/// Содержат информацию о хосте, порте, учетных данных,
	/// имени очереди, имени обменника и других параметрах.
	/// Используются для настройки соединения и выполнения операций с очередью.
	/// </summary>
	private readonly RabbitMqSettings _settings;

	/// <summary>
	/// Сервис для обработки позиций игроков и событий, связанных с их активностью.
	/// Используется для управления состояниями игроков (лобби, игра) и обработки поступающих данных из очереди сообщений.
	/// </summary>
	private readonly PlayerPositionService _positionService;

	/// <summary>
	/// Соединение с сервером RabbitMQ.
	/// Используется для установки и управления постоянным подключением
	/// между приложением и брокером сообщений RabbitMQ.
	/// </summary>
	private IConnection? _connection;

	/// <summary>
	/// Канал для взаимодействия с RabbitMQ.
	/// Используется для объявления очередей, подписки на них и обработки сообщений сервиса.
	/// </summary>
	private IModel? _channel;

	/// <summary>
	/// Потребитель сообщений из очередей RabbitMQ.
	/// Используется для обработки входящих сообщений, полученных из определенной очереди.
	/// Управляет событиями, связанными с получением сообщений.
	/// </summary>
	private EventingBasicConsumer? _consumer;

	/// <summary>
	/// Источник токена отмены, используемый для управления жизненным циклом фонового
	/// задания RabbitMQ consumer. Позволяет инициировать отмену операций при остановке службы
	/// или возникновении других условий прекращения работы.
	/// </summary>
	private CancellationTokenSource? _cts;


	/// <summary>
	/// Реализует фоновый сервис для потребления сообщений из RabbitMQ.
	/// Обеспечивает управление подключением, потреблением сообщений и их обработкой с использованием службы передачи.
	/// </summary>
	public RabbitMqConsumer(IOptions<RabbitMqSettings> settings, PlayerPositionService positionService)
	{
		_settings = settings.Value;
		_positionService = positionService;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		if (!_settings.Enabled)
		{
			Log.Information("RabbitMQ consumer is disabled.");
			return Task.CompletedTask;
		}

		Log.Information("Starting RabbitMQ consumer...");
		_cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_ = Task.Run(() => RunConsumerAsync(_cts.Token), cancellationToken);
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		Log.Information("Stopping RabbitMQ consumer...");
		_cts?.Cancel();
		_channel?.Close();
		_connection?.Close();
		return Task.CompletedTask;
	}

	/// <summary>
	/// Выполняет потребление сообщений из RabbitMQ.
	/// Обеспечивает подключение к серверу RabbitMQ, обработку исключений и восстановление при сбоях.
	/// </summary>
	/// <param name="ct">Токен отмены, используемый для завершения работы метода при остановке службы.</param>
	/// <returns>Задача, представляющая процесс выполнения потребления сообщений.</returns>
	private async Task RunConsumerAsync(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			try
			{
				await ConnectAndConsumeAsync(ct);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				Log.Error(ex, "RabbitMQ consumer error, reconnecting in 5s...");
				await Task.Delay(TimeSpan.FromSeconds(5), ct);
			}
		}
	}

	/// <summary>
	/// Устанавливает соединение с RabbitMQ, создает потребителя и начинает обработку сообщений из очереди.
	/// Настраивает подключение, конфигурирует очередь и обрабатывает сообщения асинхронно.
	/// Работа продолжается до получения сигнала отмены.
	/// </summary>
	/// <param name="ct">Токен отмены, используемый для прекращения выполнения метода.</param>
	/// <returns>Возвращает задачу, представляющую асинхронное выполнение операции.</returns>
	private async Task ConnectAndConsumeAsync(CancellationToken ct)
	{
		var factory = new ConnectionFactory
		{
			HostName = _settings.HostName,
			Port = _settings.Port,
			UserName = _settings.UserName,
			Password = _settings.Password,
			AutomaticRecoveryEnabled = true,
			TopologyRecoveryEnabled = true,
			DispatchConsumersAsync = false
		};

		Log.Information("Connecting to RabbitMQ at {0}:{1}...", _settings.HostName, _settings.Port);
		_connection = factory.CreateConnection();
		_channel = _connection.CreateModel();

		// Очередь объявляется producer'ом (Commander-Il2), consumer только подключается к ней

		_consumer = new EventingBasicConsumer(_channel);
		_consumer.Received += (model, ea) =>
		{
			Task.Run(async () =>
			{
				if (model != null) await OnMessageReceivedAsync(ea);
			}, ct);
		};

		var queueInfo = _channel.QueueDeclarePassive(_settings.QueueName);
		Log.Information("Queue '{Queue}' has {Messages} messages, {Consumers} consumers",
			_settings.QueueName, queueInfo.MessageCount, queueInfo.ConsumerCount);

		var consumerTag = _channel.BasicConsume(
			queue: _settings.QueueName,
			autoAck: true,
			consumer: _consumer);

		Log.Information("RabbitMQ consumer started on queue '{0}' with tag '{1}'", _settings.QueueName, consumerTag);

		// Ждём отмены
		while (!ct.IsCancellationRequested)
		{
			await Task.Delay(1000, ct);
		}
	}

	/// <summary>
	/// Асинхронно обрабатывает полученное RabbitMQ сообщение, выполняя десериализацию и дальнейшую обработку данных.
	/// Производит логику обработки на основе типа и содержания сообщения.
	/// </summary>
	/// <param name="ea">Аргументы поставки сообщения, содержащие данные о полученном сообщении, такие как тело, Exchange и Routing Key.</param>
	/// <returns>Задача, представляющая асинхронную операцию обработки сообщения.</returns>
	private async Task OnMessageReceivedAsync(BasicDeliverEventArgs ea)
	{
		Log.Information("OnMessageReceivedAsync fired! DeliveryTag={DeliveryTag}, Exchange={Exchange}, RoutingKey={RoutingKey}",
			ea.DeliveryTag, ea.Exchange, ea.RoutingKey);
		try
		{
			var body = ea.Body.ToArray();
			var json = Encoding.UTF8.GetString(body);
			Log.Information("RabbitMQ received message from exchange '{Exchange}' routing key '{RoutingKey}' ({Length} bytes)",
				ea.Exchange, ea.RoutingKey, body.Length);
			Log.Debug("Payload: {Payload}", json);

			// Пробуем десериализовать как PlayerSession (позиция от Commander-Il2)
			var session = JsonSerializer.Deserialize<PlayerSession>(json, _jsonOptions);
			if (session != null && (!string.IsNullOrEmpty(session.Event) || session.Id > 0))
			{
				Log.Information("Deserialized as PlayerSession: Id={Id}, Name={Name}, Event={Event}",
					session.Id, session.GamerName, session.Event);
				_positionService.ProcessEvent(session);
				return;
			}

			Log.Warning("Failed to deserialize message as PlayerSession (RelayMessage is disabled)");
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error processing RabbitMQ message");
		}
		await Task.CompletedTask;
	}

	public void Dispose()
	{
		_channel?.Dispose();
		_connection?.Dispose();
		_cts?.Dispose();
	}
}
