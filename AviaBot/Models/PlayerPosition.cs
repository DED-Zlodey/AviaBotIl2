using AviaBot.Enums;
using System;

namespace AviaBot.Models
{
    public class PlayerPosition
    {
        public string PlayerId { get; set; } = "";
        public string GamerName { get; set; } = "";
        public string? Ts3Uid { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public int Coalition { get; set; }
        public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
        public int Pid { get; set; } = -1;
        public string ObjectName { get; set; } = "";
        public string TypeObject { get; set; } = "";

        /// <summary>true — игрок в лобби, false — в катке (активная игра).</summary>
        public bool IsInLobby { get; set; } = true;

        /// <summary>Категория объекта (aircraft, vehicle, Spectator и т.д.).</summary>
        public CategoryObject Category { get; set; } = CategoryObject.unknown;
    }
}
