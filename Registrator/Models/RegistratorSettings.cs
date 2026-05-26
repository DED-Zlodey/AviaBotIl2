namespace Registrator.Models;

public class RegistratorSettings
{
    /// <summary>
    /// Адрес сервера TeamSpeak с указанием порта (например, "localhost:9987").
    /// </summary>
    public string Address { get; set; } = "localhost:9987";

    /// <summary>
    /// Отображаемое имя бота на сервере.
    /// </summary>
    public string Name { get; set; } = "Registrator";

    /// <summary>
    /// Канал, в который бот войдёт после подключения.
    /// </summary>
    public string Channel { get; set; } = "";

    /// <summary>
    /// Пароль сервера (если требуется).
    /// </summary>
    public string ServerPassword { get; set; } = "";

    /// <summary>
    /// Пароль канала (если требуется).
    /// </summary>
    public string ChannelPassword { get; set; } = "";

    /// <summary>
    /// Уровень безопасности идентификатора. Если меньше 0, улучшение не выполняется.
    /// </summary>
    public int SecurityLevel { get; set; } = -1;
}
