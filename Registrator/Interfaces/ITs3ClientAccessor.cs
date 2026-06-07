using System;
using TSLib.Full;

namespace Registrator.Interfaces;

/// <summary>
/// Предоставляет доступ к клиенту TeamSpeak и событиям, связанным с его состоянием.
/// </summary>
/// <remarks>
/// Интерфейс предназначен для управления объектом <see cref="TSLib.Full.TsFullClient"/>,
/// а также уведомлениями о его готовности или отключении.
/// Используется для взаимодействия с клиентом TeamSpeak в рамках систем, требующих
/// управления состоянием подключения.
/// </remarks>
public interface ITs3ClientAccessor
{
	/// <summary>
	/// Свойство, представляющее текущий экземпляр клиента TeamSpeak.
	/// </summary>
	/// <remarks>
	/// Предназначено для получения доступа к объекту <see cref="TSLib.Full.TsFullClient"/>,
	/// который управляет функциональностью клиента TeamSpeak, такой как списки каналов,
	/// управление пользователями и события, связанные с их состояниями.
	/// Может быть использовано для проверки состояния подключения клиента или для
	/// взаимодействия с ним.
	/// </remarks>
	TsFullClient? Client { get; }

    /// <summary>
    /// Событие, возникающее при готовности клиента TeamSpeak.
    /// </summary>
    /// <remarks>
    /// Используется для уведомления о том, что клиент TeamSpeak
    /// успешно инициализирован, подключен и готов к работе.
    /// </remarks>
    event EventHandler<TsFullClient>? ClientReady;

    /// <summary>
    /// Событие, уведомляющее об утрате клиента.
    /// </summary>
    event EventHandler? ClientLost;

    /// <summary>
    /// Устанавливает экземпляр клиента TeamSpeak.
    /// </summary>
    /// <param name="client">Экземпляр <see cref="TsFullClient"/>, который будет установлен как текущий клиент.</param>
    void SetClient(TsFullClient client);

    /// <summary>
    /// Очищает текущий экземпляр клиента TeamSpeak.
    /// </summary>
    /// <remarks>
    /// Метод устанавливает внутреннее значение клиента в null и уведомляет
    /// подписчиков события <see cref="ITs3ClientAccessor.ClientLost"/> о потерянном клиенте.
    /// </remarks>
    void ClearClient();
}
