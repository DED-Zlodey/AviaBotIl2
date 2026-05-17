namespace AviaBot.Models;

/// <summary>
/// Дискретный уровень качества радиосвязи.
/// Каждый уровень задаёт порог дистанции (maxFactor) и параметры аудио-эффектов,
/// которые применяются к голосовому пакету при попадании в этот диапазон.
/// </summary>
public class RadioQualityLevel
{
    /// <summary>Верхняя граница диапазона как доля от MaxDistance (0..1).</summary>
    public double MaxFactor { get; set; }

    /// <summary>Множитель затухания громкости (0..1). 0 = без затухания, 1 = полное заглушение.</summary>
    public double Attenuation { get; set; }

    /// <summary>Амплитуда белого шума (0..1).</summary>
    public double Noise { get; set; }

    /// <summary>На сколько бит сдвигать при bit-crush (0..6).</summary>
    public int CrushBits { get; set; }

    /// <summary>Вероятность замены пакета тишиной (0..1).</summary>
    public double Dropout { get; set; }
}
