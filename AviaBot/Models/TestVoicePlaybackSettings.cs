namespace AviaBot.Models;

public class TestVoicePlaybackSettings
{
	/// <summary>
	/// Включить автоматический тест воспроизведения голоса при подключении к TS3.
	/// </summary>
	public bool Enabled { get; set; } = false;

	/// <summary>
	/// Никнейм целевого пользователя, которому будет отправлен тестовый whisper-голос.
	/// </summary>
	public string TargetNickname { get; set; } = "-DED-Zlodey";

	/// <summary>
	/// TS3 UID целевого пользователя для тестового whisper-голоса.
	/// </summary>
	public string TargetTs3Uid { get; set; } = "";

	/// <summary>
	/// Путь к первому MP3-файлу (начинает воспроизводиться сразу).
	/// </summary>
	public string File1 { get; set; } = "recordings/1.mp3";

	/// <summary>
	/// Дистанция первого виртуального пользователя (в метрах).
	/// 0 = лобби (эффекты не применяются).
	/// Может быть больше MaxDistance из Relay — тогда сигнал полностью заглушается.
	/// </summary>
	public double Distance1 { get; set; } = 0;

	/// <summary>
	/// Путь ко второму MP3-файлу (начинает воспроизводиться с задержкой).
	/// </summary>
	public string File2 { get; set; } = "recordings/2.mp3";

	/// <summary>
	/// Дистанция второго виртуального пользователя (в метрах).
	/// 0 = лобби (эффекты не применяются).
	/// Может быть больше MaxDistance из Relay — тогда сигнал полностью заглушается.
	/// </summary>
	public double Distance2 { get; set; } = 0;

	/// <summary>
	/// Задержка перед стартом второго трека в миллисекундах.
	/// </summary>
	public int Track2DelayMs { get; set; } = 500;

	/// <summary>
	/// Дистанция для тестовых спикеров в режиме ретрансляции (без RabbitMQ).
	/// 0 = лобби (эффекты не применяются).
	/// </summary>
	public double TestDistance { get; set; } = 0;
}
