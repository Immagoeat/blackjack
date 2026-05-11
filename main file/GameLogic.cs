using System;
using System.Collections.Generic;
using System.Linq;

namespace BlackjackSimulator
{
    public enum Suit { Spades, Hearts, Diamonds, Clubs }
    public enum Rank { Two=2,Three,Four,Five,Six,Seven,Eight,Nine,Ten,Jack,Queen,King,Ace }

    public class Card
    {
        public Suit   Suit     { get; }
        public Rank   Rank     { get; }
        public bool   FaceDown { get; set; }
        public double DealTime { get; set; } = -999;
        public bool   IsDealer { get; set; }
        public Card(Suit s, Rank r) { Suit = s; Rank = r; }

        public int Value()
        {
            if (Rank >= Rank.Ten && Rank <= Rank.King) return 10;
            if (Rank == Rank.Ace) return 11;
            return (int)Rank;
        }

        public string RankGlyph() => Rank switch
        {
            Rank.Jack => "J", Rank.Queen => "Q", Rank.King => "K", Rank.Ace => "A",
            _ => ((int)Rank).ToString()
        };

        public bool IsRed() => Suit == Suit.Hearts || Suit == Suit.Diamonds;
    }

    public class Deck
    {
        private readonly List<Card> _cards = new();
        private readonly Random     _rng   = new();
        private readonly int        _deckCount;
        public Deck(int n = 6) { _deckCount = n; Shuffle(); }

        public void Shuffle()
        {
            _cards.Clear();
            for (int d = 0; d < _deckCount; d++)
                foreach (Suit s in Enum.GetValues(typeof(Suit)))
                    foreach (Rank r in Enum.GetValues(typeof(Rank)))
                        _cards.Add(new Card(s, r));
            for (int i = _cards.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (_cards[i], _cards[j]) = (_cards[j], _cards[i]);
            }
        }

        public Card Deal()
        {
            if (_cards.Count < 52) Shuffle();
            var c = _cards[0]; _cards.RemoveAt(0); return c;
        }
    }

    public class Hand
    {
        public List<Card> Cards         { get; } = new();
        public int        Bet           { get; set; }
        public bool       IsDoubledDown { get; set; }
        public bool       IsSplit       { get; set; }
        public void AddCard(Card c) => Cards.Add(c);

        public int Score()
        {
            int total = 0, aces = 0;
            foreach (var c in Cards.Where(c => !c.FaceDown))
            { total += c.Value(); if (c.Rank == Rank.Ace) aces++; }
            while (total > 21 && aces > 0) { total -= 10; aces--; }
            return total;
        }

        public bool IsBust()      => Score() > 21;
        public bool IsBlackjack() => Cards.Count == 2 && Score() == 21 && !IsSplit;
        public bool IsSoftHand()
        {
            int total = Cards.Where(c => !c.FaceDown).Sum(c => c.Value());
            int aces  = Cards.Count(c => c.Rank == Rank.Ace && !c.FaceDown);
            return aces > 0 && total != Score();
        }
        public bool CanSplit() => Cards.Count == 2 && Cards[0].Rank == Cards[1].Rank;

        public string ScoreText()
        {
            if (Cards.Any(c => c.FaceDown)) return "?";
            string soft = IsSoftHand() ? $"/{Score()+10}" : "";
            return $"{Score()}{soft}";
        }
    }

    public class Player
    {
        public string     Name        { get; set; }
        public int        Chips       { get; set; }
        public int        HandsPlayed { get; set; }
        public int        HandsWon    { get; set; }
        public int        NetWinnings { get; set; }
        public int        BetAmount   { get; set; } = 100;
        public List<Hand> Hands       { get; set; } = new();
        public List<string> Results   { get; set; } = new();

        public Player(string name, int chips = 1000)
        {
            Name  = name;
            Chips = chips;
        }
    }

    public enum GameMode { Blackjack, Poker, Slots }
    public enum Phase { SlotSelect, ModeSelect, PlayerSetup, Menu, Betting, PlayerTurn, Results, BuyChips, Stats,
                        PokerSetup, PokerPlay, PokerShowdown,
                        SlotsPlay }

    public class GS
    {
        public Deck         Deck        = new(6);
        public Hand         Dealer      = new();
        public bool         DealerShown = false;

        // Multi-player
        public List<Player> Players     = new() { new Player("Player 1") };
        public int          ActivePlayer = 0;   // whose turn in betting / play
        public int          ActiveHand   = 0;   // which hand of ActivePlayer

        // legacy single-player accessors used by rendering helpers
        public int          Chips       { get => Players[0].Chips;       set => Players[0].Chips       = value; }
        public List<Hand>   Hands       { get => Players[ActivePlayer].Hands; set => Players[ActivePlayer].Hands = value; }
        public List<string> Results     { get => Players[ActivePlayer].Results; }
        public int          HandsPlayed { get => Players[0].HandsPlayed; set => Players[0].HandsPlayed = value; }
        public int          HandsWon    { get => Players[0].HandsWon;    set => Players[0].HandsWon    = value; }
        public int          NetWinnings { get => Players[0].NetWinnings; set => Players[0].NetWinnings = value; }
        public int          BetAmount   { get => Players[ActivePlayer].BetAmount; set => Players[ActivePlayer].BetAmount = value; }

        public Phase        Phase       = Phase.SlotSelect;
        public GameMode     Mode        = GameMode.Blackjack;

        // UI state
        public int  MenuSel        = -1;
        public int  BuySel         = 0;
        public bool AutoPlay       = false;
        public int  PlayerSetupSel = 1;   // number of players chosen (1-4)

        // Active save slot (0-2)
        public int  SlotIndex = 0;
        public int  SlotSel   = 0;
    }
}
