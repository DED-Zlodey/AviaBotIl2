using Registrator.Interfaces;
using System;
using TSLib.Full;

namespace Registrator.Services;

/// <summary>
/// Класс предоставляет доступ к экземпляру <c>TsFullClient</c>,
/// а также управление состоянием подключения клиента и событиями, связанными с его состоянием.
/// </summary>
public class Ts3ClientAccessor : ITs3ClientAccessor
{
	/// Приватное поле, хранящее ссылку на текущий экземпляр клиента TeamSpeak.
	/// Если клиент не установлен, поле имеет значение null.
	/// Используется для управления состоянием клиента и взаимодействия с сервером.
	private TsFullClient? _client;

	/// <summary>
	/// Свойство, представляющее текущий экземпляр клиента TeamSpeak.
	/// </summary>
	/// <remarks>
	/// Данное свойство возвращает объект типа <see cref="TsFullClient"/>, который
	/// используется для взаимодействия с сервером TeamSpeak. Если объект клиента
	/// не был установлен или был очищен, свойство возвращает null.
	/// </remarks>
	public TsFullClient? Client => _client;

    public event EventHandler<TsFullClient>? ClientReady;
    public event EventHandler? ClientLost;

    public void SetClient(TsFullClient client)
    {
        _client = client;
        ClientReady?.Invoke(this, client);
    }

    public void ClearClient()
    {
        _client = null;
        ClientLost?.Invoke(this, EventArgs.Empty);
    }
}
