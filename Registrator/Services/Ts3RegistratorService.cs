using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Serilog;
using TSLib;
using TSLib.Full;
using TSLib.Helper;
using TSLib.Scheduler;
using Registrator.Data;
using Registrator.DataModels;
using Registrator.Models;

namespace Registrator.Services;

public class Ts3RegistratorService : IHostedService, IDisposable
{
	/// <summary>
	/// Логгер для записи информации, предупреждений и ошибок в процессе работы сервиса Ts3RegistratorService.
	/// Используется для мониторинга выполнения задач, обработки событий и отображения сообщений о состоянии приложения.
	/// </summary>
	private static readonly ILogger Log = Serilog.Log.ForContext<Ts3RegistratorService>();

	/// <summary>
	/// Настройки подключения и конфигурации для сервиса Ts3RegistratorService.
	/// Используются для хранения адреса сервера, имени пользователя, уровня безопасности,
	/// данных паролей и настроек каналов для взаимодействия с сервером TeamSpeak.
	/// </summary>
	private readonly RegistratorSettings _settings;

	/// <summary>
	/// Фабрика для создания экземпляров контекста базы данных <see cref="AppDbContext"/>.
	/// Используется для управления подключениями к базе данных в рамках различных операций сервиса
	/// <see cref="Ts3RegistratorService"/>, включая обработку пользовательских запросов и сохранение данных.
	/// </summary>
	private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    /// <summary>
    /// Экземпляр класса <see cref="DedicatedTaskScheduler"/>, предназначенный для управления выполнением задач в выделенном потоке.
    /// Используется для обеспечения последовательного выполнения логики и управления асинхронным выполнением.
    /// </summary>
    private DedicatedTaskScheduler? _scheduler;

    /// <summary>
    /// Поле, представляющее экземпляр клиента TeamSpeak 3 с полной функциональностью.
    /// Используется для управления подключением к серверу TeamSpeak 3, обработки событий и выполнения различных операций.
    /// </summary>
    private TsFullClient? _tsFullClient;

    /// <summary>
    /// Данные идентификации, используемые для подключения к серверу и обеспечения безопасности соединения.
    /// Генерируется автоматически, если отсутствует сохранённый файл идентификации.
    /// Хранит закрытый и открытый ключи, а также служебную информацию для работы с библиотекой TeamSpeak.
    /// </summary>
    private IdentityData? _identity;

    /// <summary>
    /// Источник токена отмены, используемый для управления циклом работы сервиса Ts3RegistratorService.
    /// Позволяет координировать завершение операций и остановку связанных задач при завершении службы.
    /// </summary>
    private CancellationTokenSource? _cts;

    /// Реализует службу Ts3RegistratorService, предназначенную для работы с клиентом TeamSpeak.
    /// Выполняет задачи по настройке соединений, управлению задачами и регистрацией событий.
    public Ts3RegistratorService(IOptions<RegistratorSettings> settings, IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _settings = settings.Value;
        _dbContextFactory = dbContextFactory;
    }

    /// Запускает службу Ts3RegistratorService.
    /// Выполняет инициализацию необходимых ресурсов, запускает планировщик задач и начинает выполнение асинхронной логики бота.
    /// <param name="cancellationToken">
    /// Токен, используемый для отмены операции.
    /// </param>
    /// <return>
    /// Задача, представляющая асинхронную операцию запуска службы.
    /// </return>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Log.Information("Starting Ts3RegistratorService...");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _scheduler = new DedicatedTaskScheduler(new Id(0));
        _scheduler.Invoke(() => RunBotAsync(_cts.Token));

        return Task.CompletedTask;
    }

    /// Останавливает службу Ts3RegistratorService.
    /// Завершает выполнение всех задач, отключает клиента TeamSpeak и освобождает ресурсы.
    /// <param name="cancellationToken">
    /// Токен, используемый для отмены операции.
    /// </param>
    /// <return>
    /// Задача, представляющая асинхронную операцию завершения службы.
    /// </return>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("Stopping Ts3RegistratorService...");
        await _cts?.CancelAsync()!;

        if (_scheduler != null)
        {
            await _scheduler.InvokeAsync(async () =>
            {
                if (_tsFullClient != null)
                {
                    await _tsFullClient.Disconnect();
                    _tsFullClient.Dispose();
                }
            });
            _scheduler.Dispose();
        }
    }

    /// Выполняет основную рабочую логику бота в асинхронном режиме.
    /// Устанавливает соединение с сервером TeamSpeak, обрабатывает входящие сообщения
    /// и поддерживает цикл работы до завершения или отмены операции.
    /// <param name="ct">
    /// Токен, используемый для уведомления о необходимости завершения работы бота.
    /// </param>
    /// <return>
    /// Задача, представляющая асинхронную операцию запуска бота.
    /// </return>
    private async Task RunBotAsync(CancellationToken ct)
    {
        try
        {
            _tsFullClient = new TsFullClient(_scheduler);
            _tsFullClient.OnDisconnected += (_, e) =>
            {
                Log.Warning("Disconnected: {0}", e.Error?.ErrorFormat() ?? e.ExitReason.ToString());
            };
            _tsFullClient.OnEachTextMessage += async (_, msg) =>
            {
                try
                {
                    if (msg.Target != TextMessageTargetMode.Private)
                        return;

                    var request = JsonConvert.DeserializeObject<UserRequest>(msg.Message.Trim());
                    if (request == null || string.IsNullOrWhiteSpace(request.Event))
                    {
                        await SendErrorAsync(msg.InvokerId,
                            "Invalid request. Please send a JSON with a valid 'event' field.",
                            "Неверный запрос. Пожалуйста, отправьте JSON с валидным полем 'event'.");
                        return;
                    }

                    await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

                    if (string.Equals(request.Event, "link", StringComparison.OrdinalIgnoreCase))
                    {
                        if (request.GamerId <= 0 || string.IsNullOrWhiteSpace(request.RegistrationToken))
                        {
                            await SendErrorAsync(msg.InvokerId,
                                "Invalid request. Please send a JSON in the format:\n" +
                                "{\"event\":\"link\",\"gamerId\":<number>,\"registrationToken\":\"<token>\"}",
                                "Неверный запрос. Пожалуйста, отправьте JSON в формате:\n" +
                                "{\"event\":\"link\",\"gamerId\":<число>,\"registrationToken\":\"<токен>\"}");
                            return;
                        }

                        var gamer = await db.Gamers
	                        .FirstOrDefaultAsync(x => x.Id == request.GamerId, ct);
                        if (gamer == null)
                        {
                            await SendErrorAsync(msg.InvokerId,
                                "Gamer not found.",
                                "Геймер не найден.");
                            return;
                        }

                        var expectedToken = GenerateTokenTeamSpeak(gamer.InGameId, gamer.Id);
                        if (!string.Equals(request.RegistrationToken.Trim(), expectedToken, StringComparison.Ordinal))
                        {
                            await SendErrorAsync(msg.InvokerId,
                                "Invalid registration token.",
                                "Неверный регистрационный токен.");
                            return;
                        }

                        gamer.TeamSpeakId = msg.InvokerUid?.Value ?? string.Empty;
                        await db.SaveChangesAsync(ct);

                        await _tsFullClient.SendPrivateMessage(
                            "Your account has been successfully linked!\n" +
                            "Ваш аккаунт успешно привязан!",
                            msg.InvokerId);

                        Log.Information("Linked gamer {GamerId} to TS3 UID {InvokerUid}", gamer.Id, msg.InvokerUid);
                    }
                    else if (string.Equals(request.Event, "status", StringComparison.OrdinalIgnoreCase))
                    {
                        var uid = msg.InvokerUid?.Value ?? string.Empty;
                        var gamer = await db.Gamers
	                        .FirstOrDefaultAsync(x => x.TeamSpeakId == uid, ct);
                        if (gamer == null)
                        {
                            await _tsFullClient.SendPrivateMessage(
                                "Your account is not linked.\n" +
                                "Ваш аккаунт не привязан.",
                                msg.InvokerId);
                        }
                        else
                        {
                            await _tsFullClient.SendPrivateMessage(
                                "Your account is linked.\n" +
                                "Ваш аккаунт привязан.",
                                msg.InvokerId);
                        }
                    }
                    else
                    {
                        await SendErrorAsync(msg.InvokerId,
                            "Unknown event. Supported events: 'link', 'status'.",
                            "Неизвестное событие. Поддерживаемые события: 'link', 'status'.");
                    }
                }
                catch (JsonException)
                {
                    await SendErrorAsync(msg.InvokerId,
                        "Invalid request. Please send a JSON in the format:\n" +
                        "{\"event\":\"link\",\"gamerId\":<number>,\"registrationToken\":\"<token>\"}",
                        "Неверный запрос. Пожалуйста, отправьте JSON в формате:\n" +
                        "{\"event\":\"link\",\"gamerId\":<число>,\"registrationToken\":\"<токен>\"}");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error processing text message from {InvokerName} ({InvokerUid})", msg.InvokerName, msg.InvokerUid);
                }
            };

            // Identity
            var identityPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, "identity_registrator.json");
            if (File.Exists(identityPath))
            {
                var json = await File.ReadAllTextAsync(identityPath, ct);
                var saved = JsonConvert.DeserializeObject<SavedIdentity>(json);
                if (saved?.PrivateKey != null)
                {
                    var result = TsCrypt.LoadIdentityDynamic(saved.PrivateKey, saved.Offset);
                    if (result.Ok)
                    {
                        _identity = result.Value;
                        Log.Information("Loaded existing identity");
                    }
                    else
                    {
                        Log.Warning("Failed to load identity, generating new one");
                    }
                }
            }

            if (_identity == null)
            {
                _identity = TsCrypt.GenerateNewIdentity();
                var saved = new SavedIdentity
                {
                    PrivateKey = _identity.PrivateKeyString,
                    Offset = _identity.ValidKeyOffset
                };
                await File.WriteAllTextAsync(identityPath, JsonConvert.SerializeObject(saved, Formatting.Indented), ct);
                Log.Information("Generated and saved new identity");
            }

            // Security level
            if (_settings.SecurityLevel >= 0 && _settings.SecurityLevel <= 160)
            {
                if (TsCrypt.GetSecurityLevel(_identity) < _settings.SecurityLevel)
                {
                    Log.Information("Improving security level to {0}...", _settings.SecurityLevel);
                    TsCrypt.ImproveSecurity(_identity, _settings.SecurityLevel);
                    var saved = new SavedIdentity
                    {
                        PrivateKey = _identity.PrivateKeyString,
                        Offset = _identity.ValidKeyOffset
                    };
                    await File.WriteAllTextAsync(identityPath, JsonConvert.SerializeObject(saved, Formatting.Indented), ct);
                }
            }

            var versionSign = Tools.IsLinux ? TsVersionSigned.VER_LIN_3_X_X : TsVersionSigned.VER_WIN_3_X_X;

            var connectionData = new ConnectionDataFull(
                _settings.Address,
                _identity,
                versionSign: versionSign,
                username: _settings.Name,
                serverPassword: _settings.ServerPassword,
                defaultChannel: _settings.Channel,
                defaultChannelPassword: _settings.ChannelPassword,
                logId: new Id(0)
            );

            Log.Information("Connecting to {0} as '{1}'...", _settings.Address, _settings.Name);
            var connectResult = await _scheduler!.InvokeAsync(() => _tsFullClient.Connect(connectionData));
            if (!connectResult.GetOk(out var error))
            {
                Log.Error("Could not connect: {0}", error.ErrorFormat());
                return;
            }

            Log.Information("Connected successfully!");

            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("Bot loop cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Bot loop error");
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _tsFullClient?.Dispose();
        _scheduler?.Dispose();
    }

    /// Асинхронно отправляет сообщение об ошибке указанному клиенту.
    /// <param name="clientId">Идентификатор клиента, которому будет отправлено сообщение.</param>
    /// <param name="enMessage">Сообщение об ошибке на английском языке.</param>
    /// <param name="ruMessage">Сообщение об ошибке на русском языке.</param>
    /// <return>Задача, представляющая завершение операции отправки сообщения.</return>
    private async Task SendErrorAsync(ClientId clientId, string enMessage, string ruMessage)
    {
        if (_tsFullClient == null) return;
        await _tsFullClient.SendPrivateMessage($"{enMessage}\n\n{ruMessage}", clientId);
    }

    /// Генерирует токен для идентификации пользователя в TeamSpeak.
    /// Токен создается на основе уникального идентификатора игрока (playerId) и идентификатора геймера (gamerId).
    /// <param name="playerId">Уникальный идентификатор игрока.</param>
    /// <param name="gamerId">Идентификатор геймера.</param>
    /// <returns>Сгенерированный строковый токен в формате Base64.</returns>
    private static string GenerateTokenTeamSpeak(Guid playerId, int gamerId)
    {
        Span<byte> bytes = stackalloc byte[20];
        playerId.TryWriteBytes(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes[16..], gamerId);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Класс представляет сохранённую идентичность (ключ и смещение),
    /// используемую для взаимодействия с сервером TeamSpeak 3.
    /// </summary>
    private class SavedIdentity
    {
	    /// <summary>
	    /// Приватный ключ, используемый для идентификации и аутентификации приложения.
	    /// Сохраняется в формате строки и применяется для загрузки или генерации новой идентификации,
	    /// обеспечивая необходимую безопасность и соответствие требованиям к защищённым соединениям.
	    /// </summary>
	    public string PrivateKey { get; set; } = "";

	    /// <summary>
	    /// Смещение, используемое для управления динамической загрузкой идентификатора в процессе работы Ts3RegistratorService.
	    /// Представляет собой значение, которое определяет позицию ключа или секции при операциях с идентификационными данными.
	    /// Обеспечивает корректность и безопасность операций, связанных с аутентификацией.
	    /// </summary>
	    public ulong Offset { get; set; }
    }

    /// <summary>
    /// Класс представляет запрос пользователя, содержащий информацию о событии,
    /// идентификаторе игрока и токене регистрации для взаимодействия с сервером.
    /// Используется в рамках обработки входящих сообщений от пользователей TeamSpeak 3.
    /// </summary>
    private class UserRequest
    {
	    /// <summary>
	    /// Свойство, отвечающее за название события, передаваемого в запросе.
	    /// Используется для идентификации и выполнения соответствующего действия
	    /// (например, привязка аккаунта или проверка статуса).
	    /// Значение должно быть передано в формате JSON и соответствовать одному из поддерживаемых событий.
	    /// </summary>
	    [JsonProperty("event")]
        public string Event { get; set; } = "";

	    /// <summary>
	    /// Уникальный идентификатор геймера, используемый для связи данных пользователя
	    /// внутри приложения и связывания учетных записей с другими системами.
	    /// </summary>
	    [JsonProperty("gamerId")]
        public int GamerId { get; set; }

        /// <summary>
        /// Регистрационный токен, используемый для связывания аккаунта пользователя с игровым профилем.
        /// Токен передается в запросах для подтверждения подлинности и обеспечения безопасности операций.
        /// </summary>
        [JsonProperty("registrationToken")]
        public string RegistrationToken { get; set; } = "";
    }
}
