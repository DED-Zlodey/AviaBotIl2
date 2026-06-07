namespace Registrator.RabbitMq;

public interface ITs3EventPublisher
{
	/// <summary>
	/// Асинхронно публикует событие в систему обмена сообщениями.
	/// </summary>
	/// <param name="eventData">
	/// Объект <see cref="Ts3EventEnvelope"/>, содержащий данные о событии для публикации.
	/// </param>
	/// <param name="ct">
	/// Токен отмены <see cref="System.Threading.CancellationToken"/>. Позволяет отменить операцию публикации. Не является обязательным.
	/// </param>
	/// <returns>
	/// Задача <see cref="System.Threading.Tasks.Task"/>, представляющая асинхронную операцию публикации.
	/// </returns>
	System.Threading.Tasks.Task PublishAsync(Ts3EventEnvelope eventData, System.Threading.CancellationToken ct = default);
}
