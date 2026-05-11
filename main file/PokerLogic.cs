using System;
using System.Collections.Generic;
using System.Linq;

namespace BlackjackSimulator
{
    public enum PokerPhase { Waiting, Preflop, Flop, Turn, River, Showdown }
    public enum PokerAction { None, Fold, Check, Call, Raise, AllIn }

    public class PokerPlayer
    {
        public string     Name      { get; set; }
        public int        Chips     { get; set; }
        public bool       IsHuman   { get; set; }
        public bool       IsFolded  { get; set; }
        public bool       IsAllIn   { get; set; }
        public bool       IsOut     { get; set; }   // busted out of tournament
        public int        Bet       { get; set; }   // amount bet this street
        public int        TotalBet  { get; set; }   // total in pot from this player
        public List<Card> HoleCards { get; set; } = new();
        public PokerAction LastAction { get; set; } = PokerAction.None;
        public string     LastActionLabel => LastAction switch {
            PokerAction.Fold  => "FOLD",
            PokerAction.Check => "CHECK",
            PokerAction.Call  => "CALL",
            PokerAction.Raise => "RAISE",
            PokerAction.AllIn => "ALL IN",
            _ => ""
        };

        public PokerPlayer(string name, int chips, bool isHuman)
        {
            Name    = name;
            Chips   = chips;
            IsHuman = isHuman;
        }
    }

    public class PokerGame
    {
        public List<PokerPlayer> Players     { get; } = new();
        public List<Card>        Community   { get; } = new();
        public Deck              Deck        { get; } = new(1);
        public PokerPhase        Phase       { get; set; } = PokerPhase.Waiting;
        public int               Pot         { get; set; }
        public int               CurrentBet  { get; set; }  // highest bet this street
        public int               DealerIdx   { get; set; }
        public int               ActiveIdx   { get; set; }
        public int               SmallBlind  { get; set; } = 25;
        public int               BigBlind    { get; set; } = 50;
        public int               RaiseAmount { get; set; } = 50;
        public List<string>      Log         { get; } = new();
        public List<PokerPlayer> Winners     { get; set; } = new();
        public string            ShowdownMsg { get; set; } = "";

        // Deal animation: host stamps these when cards are dealt
        public double DealClock   { get; set; } = 0;   // set by App each frame

        // UI state
        public int  RaiseSel      = 50;   // chosen raise size
        public bool ActionPending = false; // human needs to act
        public bool WaitingForDeal = false; // community card anim in progress

        private readonly Random _rng = new();

        public PokerPlayer? ActivePlayer => ActiveIdx >= 0 && ActiveIdx < Players.Count
            ? Players[ActiveIdx] : null;

        public bool IsHumanTurn => ActivePlayer?.IsHuman == true && !ActivePlayer.IsFolded && !ActivePlayer.IsAllIn;

        public void StartNewHand()
        {
            Deck.Shuffle();
            Community.Clear();
            Pot = 0;
            CurrentBet = 0;
            Log.Clear();
            Winners.Clear();
            ShowdownMsg = "";

            foreach (var p in Players)
            {
                p.HoleCards.Clear();
                p.IsFolded    = false;
                p.IsAllIn     = false;
                p.Bet         = 0;
                p.TotalBet    = 0;
                p.LastAction  = PokerAction.None;
            }

            // Advance dealer
            DealerIdx = NextActive(DealerIdx);

            // Deal hole cards with staggered deal times
            double t = DealClock;
            double step = 0.12;
            int   seat  = 0;
            for (int round = 0; round < 2; round++)
            {
                for (int i = 0; i < Players.Count; i++)
                {
                    var p = Players[(DealerIdx + 1 + i) % Players.Count];
                    if (!p.IsOut)
                    {
                        var c = Deck.Deal();
                        c.DealTime = t + seat * step + round * Players.Count * step;
                        p.HoleCards.Add(c);
                        seat++;
                    }
                }
                seat = 0;
            }

            // Post blinds
            int sbIdx = NextActive(DealerIdx);
            int bbIdx = NextActive(sbIdx);
            PostBlind(sbIdx, SmallBlind);
            PostBlind(bbIdx, BigBlind);
            CurrentBet = BigBlind;
            RaiseAmount = BigBlind;

            // First to act preflop is after BB
            ActiveIdx = NextActive(bbIdx);
            Phase     = PokerPhase.Preflop;
            ActionPending = Players[ActiveIdx].IsHuman;
            Log.Add($"New hand — dealer: {Players[DealerIdx].Name}");
        }

        private void PostBlind(int idx, int amount)
        {
            var p   = Players[idx];
            int amt = Math.Min(amount, p.Chips);
            p.Chips   -= amt;
            p.Bet     += amt;
            p.TotalBet += amt;
            Pot        += amt;
            if (p.Chips == 0) p.IsAllIn = true;
        }

        public int NextActive(int from)
        {
            int idx = (from + 1) % Players.Count;
            int tries = 0;
            while ((Players[idx].IsFolded || Players[idx].IsOut || Players[idx].IsAllIn) && tries < Players.Count)
            { idx = (idx + 1) % Players.Count; tries++; }
            return idx;
        }

        public int ActiveCount => Players.Count(p => !p.IsFolded && !p.IsOut);
        public bool AllActed(int startIdx)
        {
            // Everyone who can act has called or raised to CurrentBet
            foreach (var p in Players)
            {
                if (p.IsFolded || p.IsOut || p.IsAllIn) continue;
                if (p.Bet < CurrentBet) return false;
            }
            return true;
        }

        public void DoAction(int playerIdx, PokerAction action, int raiseAmt = 0)
        {
            var p = Players[playerIdx];
            p.LastAction = action;
            int toCall = CurrentBet - p.Bet;

            switch (action)
            {
                case PokerAction.Fold:
                    p.IsFolded = true;
                    Log.Add($"{p.Name} folds");
                    break;

                case PokerAction.Check:
                    Log.Add($"{p.Name} checks");
                    break;

                case PokerAction.Call:
                    int callAmt = Math.Min(toCall, p.Chips);
                    p.Chips    -= callAmt;
                    p.Bet      += callAmt;
                    p.TotalBet += callAmt;
                    Pot        += callAmt;
                    if (p.Chips == 0) { p.IsAllIn = true; p.LastAction = PokerAction.AllIn; }
                    Log.Add($"{p.Name} calls ${callAmt}");
                    break;

                case PokerAction.Raise:
                    int total = CurrentBet + raiseAmt;
                    int needed = total - p.Bet;
                    int actual = Math.Min(needed, p.Chips);
                    p.Chips    -= actual;
                    p.Bet      += actual;
                    p.TotalBet += actual;
                    Pot        += actual;
                    CurrentBet  = p.Bet;
                    RaiseAmount = raiseAmt;
                    if (p.Chips == 0) { p.IsAllIn = true; p.LastAction = PokerAction.AllIn; }
                    Log.Add($"{p.Name} raises to ${CurrentBet}");
                    break;

                case PokerAction.AllIn:
                    int allAmt = p.Chips;
                    p.Chips    = 0;
                    p.Bet      += allAmt;
                    p.TotalBet += allAmt;
                    Pot        += allAmt;
                    if (p.Bet > CurrentBet) CurrentBet = p.Bet;
                    p.IsAllIn  = true;
                    Log.Add($"{p.Name} goes all-in (${allAmt})");
                    break;
            }

            AdvanceTurn();
        }

        private void AdvanceTurn()
        {
            // Check if only one player remains
            if (ActiveCount == 1)
            { DoShowdown(); return; }

            // Find next active player
            int next = NextActive(ActiveIdx);
            bool streetDone = false;

            // Street is done when we've gone around and everyone has matched CurrentBet
            // Check if next player has already acted and is up to CurrentBet
            int count = 0;
            int check = next;
            streetDone = true;
            do
            {
                var p = Players[check];
                if (!p.IsFolded && !p.IsOut && !p.IsAllIn && p.Bet < CurrentBet)
                { streetDone = false; break; }
                check = NextActive(check);
                count++;
            } while (count < Players.Count);

            if (streetDone)
            { AdvanceStreet(); return; }

            ActiveIdx = next;
            if (!Players[ActiveIdx].IsHuman)
                DoAiAction(ActiveIdx);
            else
                ActionPending = true;
        }

        private void AdvanceStreet()
        {
            // Reset bets for new street
            foreach (var p in Players) p.Bet = 0;
            CurrentBet = 0;

            // First to act post-flop is first active after dealer
            int firstAct = NextActive(DealerIdx);

            switch (Phase)
            {
                case PokerPhase.Preflop:
                    Phase = PokerPhase.Flop;
                    for (int i = 0; i < 3; i++)
                    {
                        var c = Deck.Deal(); c.DealTime = DealClock + i * 0.15; Community.Add(c);
                    }
                    Log.Add("--- Flop ---");
                    WaitingForDeal = true;
                    break;
                case PokerPhase.Flop:
                    Phase = PokerPhase.Turn;
                    { var c = Deck.Deal(); c.DealTime = DealClock; Community.Add(c); }
                    Log.Add("--- Turn ---");
                    WaitingForDeal = true;
                    break;
                case PokerPhase.Turn:
                    Phase = PokerPhase.River;
                    { var c = Deck.Deal(); c.DealTime = DealClock; Community.Add(c); }
                    Log.Add("--- River ---");
                    WaitingForDeal = true;
                    break;
                case PokerPhase.River:
                    DoShowdown();
                    return;
            }

            // Check if all remaining players are all-in
            bool allAllIn = Players.All(p => p.IsFolded || p.IsOut || p.IsAllIn);
            if (allAllIn)
            {
                // Run remaining streets automatically
                while (Phase != PokerPhase.Showdown && Community.Count < 5)
                    AdvanceStreet();
                return;
            }

            ActiveIdx = firstAct;
            if (!WaitingForDeal)
            {
                if (!Players[ActiveIdx].IsHuman)
                    DoAiAction(ActiveIdx);
                else
                    ActionPending = true;
            }
        }

        // Called by App after deal animation completes to resume AI/human action
        public void ResumAfterDeal()
        {
            WaitingForDeal = false;
            if (Phase == PokerPhase.Showdown) return;
            if (!Players[ActiveIdx].IsHuman)
                DoAiAction(ActiveIdx);
            else
                ActionPending = true;
        }

        private void DoShowdown()
        {
            Phase = PokerPhase.Showdown;
            // Evaluate hands and determine winner(s)
            var activePlayers = Players.Where(p => !p.IsFolded && !p.IsOut).ToList();
            if (activePlayers.Count == 1)
            {
                var winner = activePlayers[0];
                winner.Chips += Pot;
                Winners = activePlayers;
                ShowdownMsg = $"{winner.Name} wins ${Pot}";
                Log.Add(ShowdownMsg);
            }
            else
            {
                // Evaluate each hand
                var evals = activePlayers
                    .Select(p => (player: p, rank: EvaluateBest(p.HoleCards, Community)))
                    .OrderByDescending(x => x.rank)
                    .ToList();

                int bestRank = evals[0].rank;
                var tied = evals.Where(x => x.rank == bestRank).Select(x => x.player).ToList();

                int share = Pot / tied.Count;
                int rem   = Pot % tied.Count;
                foreach (var w in tied) w.Chips += share;
                tied[0].Chips += rem;
                Winners = tied;

                string handName = HandRankName(bestRank);
                if (tied.Count == 1)
                    ShowdownMsg = $"{tied[0].Name} wins ${Pot} with {handName}";
                else
                    ShowdownMsg = $"Split pot (${share} each) — {handName}";
                Log.Add(ShowdownMsg);

                // Log all hands
                foreach (var (player, rank) in evals)
                    Log.Add($"  {player.Name}: {HandRankName(rank)}");
            }

            // Mark broke players as out
            foreach (var p in Players)
                if (p.Chips <= 0 && !p.IsOut) p.IsOut = true;
        }

        // ── AI action ─────────────────────────────────────────
        public void DoAiAction(int idx)
        {
            var p       = Players[idx];
            int toCall  = CurrentBet - p.Bet;
            int strength = EvaluateAiStrength(p.HoleCards, Community);

            // strength 0..100
            if (toCall == 0)
            {
                // Check or raise
                if (strength > 65 && p.Chips > BigBlind)
                    DoAction(idx, PokerAction.Raise, BigBlind * 2);
                else
                    DoAction(idx, PokerAction.Check);
            }
            else
            {
                double callRatio = (double)toCall / Math.Max(p.Chips, 1);
                if (strength > 75)
                    DoAction(idx, PokerAction.Raise, BigBlind * 2);
                else if (strength > 40 || callRatio < 0.15)
                    DoAction(idx, PokerAction.Call);
                else
                    DoAction(idx, PokerAction.Fold);
            }
        }

        private int EvaluateAiStrength(List<Card> hole, List<Card> board)
        {
            if (hole.Count < 2) return 30;
            int phase = board.Count; // 0=preflop, 3=flop, 4=turn, 5=river

            int baseStrength = HoleStrength(hole);

            if (phase == 0) return baseStrength;

            // Post-flop: use actual hand evaluation
            int rank = EvaluateBest(hole, board);
            // Map rank (hand category) to 0-100
            int cat = rank >> 20;
            return Math.Clamp(cat * 12 + 10, 0, 95);
        }

        private int HoleStrength(List<Card> hole)
        {
            // Preflop hand strength estimate (0-100)
            var h0 = hole[0]; var h1 = hole[1];
            int r0 = (int)h0.Rank, r1 = (int)h1.Rank;
            if (r0 < r1) { (r0, r1) = (r1, r0); }
            bool suited  = h0.Suit == h1.Suit;
            bool pair    = r0 == r1;
            bool connected = Math.Abs(r0 - r1) <= 1;

            if (pair)
            {
                if (r0 >= (int)Rank.Ten)  return 90;
                if (r0 >= (int)Rank.Seven) return 70;
                return 55;
            }
            if (r0 == (int)Rank.Ace)
            {
                if (r1 >= (int)Rank.King)  return 85;
                if (r1 >= (int)Rank.Ten)   return 72;
                return suited ? 62 : 50;
            }
            if (r0 >= (int)Rank.King && r1 >= (int)Rank.Ten) return suited ? 68 : 60;
            if (connected && suited)  return 55;
            if (connected)            return 45;
            if (suited)               return 40;
            return 25 + r0;
        }

        // ── hand evaluation ───────────────────────────────────
        // Returns an integer where higher = better hand.
        // Top 4 bits = hand category (0=high card .. 8=straight flush)
        public static int EvaluateBest(List<Card> hole, List<Card> board)
        {
            var all = hole.Concat(board).ToList();
            int best = 0;
            // Try all C(n,5) combos
            for (int i = 0; i < all.Count - 1; i++)
            for (int j = i + 1; j < all.Count; j++)
            {
                var five = all.Where((_, idx) => idx != i && idx != j).ToList();
                if (five.Count == 5)
                {
                    int val = Evaluate5(five);
                    if (val > best) best = val;
                }
            }
            if (all.Count <= 5) best = Evaluate5(all);
            return best;
        }

        private static int Evaluate5(List<Card> cards)
        {
            var ranks  = cards.Select(c => (int)c.Rank).OrderByDescending(r => r).ToList();
            var suits  = cards.Select(c => c.Suit).ToList();
            bool flush  = suits.Distinct().Count() == 1;
            bool straight = IsStraight(ranks, out int topRank);

            var groups = ranks.GroupBy(r => r).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).ToList();
            int[] counts = groups.Select(g => g.Count()).ToArray();
            int[] keys   = groups.Select(g => g.Key).ToArray();

            int cat, tiebreak;

            if (flush && straight)      { cat = 8; tiebreak = topRank; }
            else if (counts[0] == 4)    { cat = 7; tiebreak = keys[0] * 15 + keys[1]; }
            else if (counts[0] == 3 && counts[1] == 2) { cat = 6; tiebreak = keys[0] * 15 + keys[1]; }
            else if (flush)             { cat = 5; tiebreak = TiebreakerList(ranks); }
            else if (straight)          { cat = 4; tiebreak = topRank; }
            else if (counts[0] == 3)    { cat = 3; tiebreak = keys[0] * 225 + TiebreakerList(ranks.Where(r => r != keys[0]).ToList()); }
            else if (counts[0] == 2 && counts[1] == 2) { cat = 2; tiebreak = Math.Max(keys[0],keys[1])*225 + Math.Min(keys[0],keys[1])*15 + keys[2]; }
            else if (counts[0] == 2)    { cat = 1; tiebreak = keys[0] * 3375 + TiebreakerList(ranks.Where(r => r != keys[0]).ToList()); }
            else                        { cat = 0; tiebreak = TiebreakerList(ranks); }

            return (cat << 20) | tiebreak;
        }

        private static bool IsStraight(List<int> sorted, out int topRank)
        {
            topRank = sorted[0];
            // Check normal straight
            bool ok = true;
            for (int i = 0; i < sorted.Count - 1; i++)
                if (sorted[i] - sorted[i+1] != 1) { ok = false; break; }
            if (ok) return true;
            // Wheel (A-2-3-4-5): ace = 14, treat as 1
            if (sorted[0] == (int)Rank.Ace)
            {
                var wheel = new List<int> { 5, 4, 3, 2, 1 };
                var low   = sorted.Skip(1).ToList();
                low.Add(1);
                low.Sort((a, b) => b - a);
                ok = true;
                for (int i = 0; i < low.Count - 1; i++)
                    if (low[i] - low[i+1] != 1) { ok = false; break; }
                if (ok) { topRank = 5; return true; }
            }
            return false;
        }

        private static int TiebreakerList(List<int> ranks)
        {
            int v = 0;
            foreach (var r in ranks.Take(5)) v = v * 15 + r;
            return v;
        }

        public static string HandRankName(int rank)
        {
            int cat = rank >> 20;
            return cat switch {
                8 => "Straight Flush",
                7 => "Four of a Kind",
                6 => "Full House",
                5 => "Flush",
                4 => "Straight",
                3 => "Three of a Kind",
                2 => "Two Pair",
                1 => "One Pair",
                _ => "High Card"
            };
        }
    }
}
