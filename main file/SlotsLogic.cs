using System;
using System.Collections.Generic;

namespace BlackjackSimulator
{
    public enum SlotSymbol { Cherry, Lemon, Orange, Plum, Bell, Bar, Seven, Diamond }

    public class SlotsGame
    {
        public const int REELS = 3, ROWS = 3;

        // visible symbols on each reel (top, centre, bottom)
        public SlotSymbol[,] Display { get; } = new SlotSymbol[REELS, ROWS];

        // Spin animation: each reel has an offset that animates down
        public double[] SpinOffset    { get; } = new double[REELS];
        public double[] SpinTarget    { get; } = new double[REELS];
        public double   SpinStartTime { get; set; } = -999;
        public bool     IsSpinning    { get; private set; }
        public bool[]   ReelStopped   { get; } = new bool[REELS];

        public int  Bet         { get; set; } = 10;
        public int  LastWin     { get; set; }
        public string WinLabel  { get; set; } = "";
        public bool ShowResult  { get; set; }

        private readonly Random        _rng  = new();
        private readonly SlotSymbol[]  _strip;

        // Probability-weighted reel strip (longer strips = more variety)
        private static readonly (SlotSymbol sym, int weight)[] _weights = {
            (SlotSymbol.Cherry,  12),
            (SlotSymbol.Lemon,   10),
            (SlotSymbol.Orange,   9),
            (SlotSymbol.Plum,     8),
            (SlotSymbol.Bell,     6),
            (SlotSymbol.Bar,      4),
            (SlotSymbol.Seven,    2),
            (SlotSymbol.Diamond,  1),
        };

        public SlotsGame()
        {
            var list = new List<SlotSymbol>();
            foreach (var (sym, w) in _weights)
                for (int i = 0; i < w; i++) list.Add(sym);
            _strip = list.ToArray();
            // initialise display
            for (int r = 0; r < REELS; r++)
            for (int row = 0; row < ROWS; row++)
                Display[r, row] = _strip[_rng.Next(_strip.Length)];
        }

        public void Spin(int playerChips)
        {
            if (IsSpinning || Bet > playerChips || Bet <= 0) return;
            IsSpinning   = true;
            ShowResult   = false;
            WinLabel     = "";
            LastWin      = 0;
            for (int r = 0; r < REELS; r++)
            {
                ReelStopped[r] = false;
                SpinOffset[r]  = 0;
                // Random number of extra symbols before landing
                SpinTarget[r] = 80 + r * 20 + _rng.Next(25);
            }
        }

        // Returns chips won (0 if nothing), call each frame with dt
        public int Tick(double now, double dt, int bet)
        {
            if (!IsSpinning) return 0;

            double speed = 32.0; // symbols per second
            bool allStopped = true;

            for (int r = 0; r < REELS; r++)
            {
                if (ReelStopped[r]) continue;
                // Stagger stop: reel r stops after r * 0.55s extra
                double staggerEnd = SpinTarget[r] / speed;
                SpinOffset[r] += speed * dt;
                if (SpinOffset[r] >= SpinTarget[r])
                {
                    SpinOffset[r]  = SpinTarget[r];
                    ReelStopped[r] = true;
                    // Pick final symbols
                    for (int row = 0; row < ROWS; row++)
                        Display[r, row] = _strip[_rng.Next(_strip.Length)];
                }
                else allStopped = false;
            }

            if (allStopped)
            {
                IsSpinning = false;
                int win = CalcWin(bet);
                LastWin  = win;
                WinLabel = win > 0 ? BuildWinLabel() : "";
                ShowResult = true;
                return win;
            }
            return 0;
        }

        private int CalcWin(int bet)
        {
            // Centre row (row 1) is the payline
            var s0 = Display[0, 1];
            var s1 = Display[1, 1];
            var s2 = Display[2, 1];

            // Three of a kind
            if (s0 == s1 && s1 == s2)
            {
                return bet * s0 switch {
                    SlotSymbol.Diamond => 500,
                    SlotSymbol.Seven   => 100,
                    SlotSymbol.Bar     =>  50,
                    SlotSymbol.Bell    =>  20,
                    SlotSymbol.Plum    =>  10,
                    SlotSymbol.Orange  =>   8,
                    SlotSymbol.Lemon   =>   5,
                    SlotSymbol.Cherry  =>   3,
                    _ => 0
                };
            }
            // Two cherries on payline
            int cherries = (s0 == SlotSymbol.Cherry ? 1 : 0) + (s1 == SlotSymbol.Cherry ? 1 : 0) + (s2 == SlotSymbol.Cherry ? 1 : 0);
            if (cherries == 2) return bet * 2;
            if (cherries == 1 && s0 == SlotSymbol.Cherry) return bet;
            return 0;
        }

        private string BuildWinLabel()
        {
            var s0 = Display[0, 1];
            var s1 = Display[1, 1];
            var s2 = Display[2, 1];
            if (s0 == s1 && s1 == s2)
                return $"THREE {SymName(s0).ToUpper()}S!";
            return "CHERRY BONUS!";
        }

        public static string SymName(SlotSymbol s) => s switch {
            SlotSymbol.Cherry  => "Cherry",
            SlotSymbol.Lemon   => "Lemon",
            SlotSymbol.Orange  => "Orange",
            SlotSymbol.Plum    => "Plum",
            SlotSymbol.Bell    => "Bell",
            SlotSymbol.Bar     => "BAR",
            SlotSymbol.Seven   => "7",
            SlotSymbol.Diamond => "Diamond",
            _ => "?"
        };

        // Multiplier table for display
        public static (string label, int mult)[] PayTable => new[] {
            ("Diamond Diamond Diamond", 500),
            ("7 7 7",                  100),
            ("BAR BAR BAR",             50),
            ("Bell Bell Bell",          20),
            ("Plum Plum Plum",          10),
            ("Orange Orange Orange",     8),
            ("Lemon Lemon Lemon",        5),
            ("Cherry Cherry Cherry",     3),
            ("Cherry Cherry",            2),
            ("Cherry",                   1),
        };
    }
}
