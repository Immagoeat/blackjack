using System;
using System.IO;
using System.Text.Json;

namespace BlackjackSimulator
{
    public class SaveData
    {
        public int    Chips       { get; set; } = 1000;
        public int    HandsPlayed { get; set; } = 0;
        public int    HandsWon    { get; set; } = 0;
        public int    NetWinnings { get; set; } = 0;
        public int    BetAmount   { get; set; } = 100;
        public bool   AutoPlay    { get; set; } = false;
        public string SlotName    { get; set; } = "New Run";
        public string LastPlayed  { get; set; } = "";

        static readonly string _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "blackjack");

        static string SlotPath(int slot) => Path.Combine(_dir, $"save{slot}.json");

        static readonly JsonSerializerOptions _jsonOpts =
            new JsonSerializerOptions { WriteIndented = true };

        public static SaveData Load(int slot)
        {
            try
            {
                string path = SlotPath(slot);
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<SaveData>(File.ReadAllText(path)) ?? DefaultFor(slot);
            }
            catch { }
            return DefaultFor(slot);
        }

        static SaveData DefaultFor(int slot) => new SaveData
        {
            SlotName = $"Run {slot + 1}",
            Chips    = 1000,
        };

        public void Save(int slot)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                LastPlayed = DateTime.Now.ToString("MMM d, h:mm tt");
                File.WriteAllText(SlotPath(slot), JsonSerializer.Serialize(this, _jsonOpts));
            }
            catch { }
        }

        public static void Delete(int slot)
        {
            try
            {
                string path = SlotPath(slot);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        public static bool Exists(int slot) => File.Exists(SlotPath(slot));

        public void ApplyTo(GS g)
        {
            g.Chips       = Math.Max(Chips, 0);
            g.HandsPlayed = HandsPlayed;
            g.HandsWon    = HandsWon;
            g.NetWinnings = NetWinnings;
            g.BetAmount   = Math.Clamp(BetAmount, 10, Math.Max(g.Chips, 10));
            g.AutoPlay    = AutoPlay;
        }

        public static SaveData From(GS g, string slotName) => new SaveData
        {
            Chips       = g.Chips,
            HandsPlayed = g.HandsPlayed,
            HandsWon    = g.HandsWon,
            NetWinnings = g.NetWinnings,
            BetAmount   = g.BetAmount,
            AutoPlay    = g.AutoPlay,
            SlotName    = slotName,
        };
    }
}
