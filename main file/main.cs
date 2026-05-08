using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BlackjackSimulator
{
    // ─────────────────────────────────────────────
    //  Card & Deck
    // ─────────────────────────────────────────────
    public enum Suit { Spades, Hearts, Diamonds, Clubs }
    public enum Rank { Two=2,Three,Four,Five,Six,Seven,Eight,Nine,Ten,Jack,Queen,King,Ace }

    public class Card
    {
        public Suit Suit { get; }
        public Rank Rank { get; }
        public bool FaceDown { get; set; }

        public Card(Suit suit, Rank rank) { Suit = suit; Rank = rank; }

        public int Value()
        {
            if (Rank >= Rank.Ten && Rank <= Rank.King) return 10;
            if (Rank == Rank.Ace) return 11;
            return (int)Rank;
        }

        private string SuitSymbol() => Suit switch
        {
            Suit.Spades   => "♠", Suit.Hearts   => "♥",
            Suit.Diamonds => "♦", Suit.Clubs     => "♣", _ => "?"
        };

        private string RankSymbol() => Rank switch
        {
            Rank.Jack  => "J", Rank.Queen => "Q",
            Rank.King  => "K", Rank.Ace   => "A",
            _ => ((int)Rank).ToString()
        };

        public string Display() => FaceDown ? "[??]" : $"[{RankSymbol()}{SuitSymbol()}]";
    }

    public class Deck
    {
        private List<Card> _cards = new();
        private Random _rng = new();
        private int _deckCount;

        public Deck(int deckCount = 6)
        {
            _deckCount = deckCount;
            Shuffle();
        }

        public void Shuffle()
        {
            _cards.Clear();
            for (int d = 0; d < _deckCount; d++)
                foreach (Suit s in Enum.GetValues(typeof(Suit)))
                    foreach (Rank r in Enum.GetValues(typeof(Rank)))
                        _cards.Add(new Card(s, r));

            // Fisher-Yates
            for (int i = _cards.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (_cards[i], _cards[j]) = (_cards[j], _cards[i]);
            }
        }

        public Card Deal()
        {
            if (_cards.Count < 52) Shuffle(); // reshuffle when low
            var card = _cards[0];
            _cards.RemoveAt(0);
            return card;
        }

        public int CardsRemaining => _cards.Count;
    }

    // ─────────────────────────────────────────────
    //  Hand
    // ─────────────────────────────────────────────
    public class Hand
    {
        public List<Card> Cards { get; } = new();
        public int Bet { get; set; }
        public bool IsDoubledDown { get; set; }
        public bool IsSplit { get; set; }

        public void AddCard(Card c) => Cards.Add(c);

        public int Score()
        {
            int total = 0, aces = 0;
            foreach (var c in Cards.Where(c => !c.FaceDown))
            {
                total += c.Value();
                if (c.Rank == Rank.Ace) aces++;
            }
            while (total > 21 && aces > 0) { total -= 10; aces--; }
            return total;
        }

        public bool IsBust()        => Score() > 21;
        public bool IsBlackjack()   => Cards.Count == 2 && Score() == 21 && !IsSplit;
        public bool IsSoftHand()
        {
            int total = Cards.Where(c => !c.FaceDown).Sum(c => c.Value());
            int aces  = Cards.Count(c => c.Rank == Rank.Ace && !c.FaceDown);
            return aces > 0 && total != Score();
        }
        public bool CanSplit()      => Cards.Count == 2 && Cards[0].Rank == Cards[1].Rank;

        public string DisplayCards() =>
            string.Join(" ", Cards.Select(c => c.Display()));

        public string ScoreDisplay()
        {
            if (Cards.Any(c => c.FaceDown)) return "??";
            string soft = IsSoftHand() ? $" / {Score() + 10}" : "";
            return $"{Score()}{soft}";
        }
    }

    // ─────────────────────────────────────────────
    //  UI Helpers
    // ─────────────────────────────────────────────
    static class UI
    {
        public static ConsoleColor ChipColor  = ConsoleColor.Yellow;
        public static ConsoleColor WinColor   = ConsoleColor.Green;
        public static ConsoleColor LoseColor  = ConsoleColor.Red;
        public static ConsoleColor CardColor  = ConsoleColor.Cyan;
        public static ConsoleColor TitleColor = ConsoleColor.Magenta;

        public static void Print(string msg, ConsoleColor color = ConsoleColor.White)
        {
            Console.ForegroundColor = color;
            Console.Write(msg);
            Console.ResetColor();
        }

        public static void PrintLine(string msg = "", ConsoleColor color = ConsoleColor.White)
        {
            Print(msg + "\n", color);
        }

        public static void Header()
        {
            Console.Clear();
            PrintLine("╔══════════════════════════════════════════════════╗", TitleColor);
            PrintLine("║          ♠  BLACKJACK SIMULATOR  ♠               ║", TitleColor);
            PrintLine("║          ♥  6-Deck Shoe · Vegas Rules ♥           ║", TitleColor);
            PrintLine("╚══════════════════════════════════════════════════╝", TitleColor);
            PrintLine();
        }

        public static void Separator() =>
            PrintLine("──────────────────────────────────────────────────");

        public static void DrawChips(int amount)
        {
            Print("  ◈ Chips: ", ConsoleColor.White);
            PrintLine($"${amount:N0}", ChipColor);
        }

        public static void DrawHand(string label, Hand hand, bool hideScore = false)
        {
            Print($"  {label}: ", ConsoleColor.White);
            Print(hand.DisplayCards() + " ", CardColor);
            if (!hideScore)
                PrintLine($"({hand.ScoreDisplay()})", ConsoleColor.DarkCyan);
            else
                PrintLine();
        }

        public static string Prompt(string msg)
        {
            Print(msg, ConsoleColor.DarkYellow);
            return Console.ReadLine()?.Trim().ToUpper() ?? "";
        }

        public static void Pause(string msg = "Press ENTER to continue...")
        {
            Print(msg, ConsoleColor.DarkGray);
            Console.ReadLine();
        }

        public static void AnimateDeal()
        {
            Print("  Dealing", ConsoleColor.DarkGray);
            for (int i = 0; i < 3; i++) { Thread.Sleep(250); Print(".", ConsoleColor.DarkGray); }
            PrintLine();
        }

        public static int GetBet(int chips, int min = 10)
        {
            while (true)
            {
                string input = Prompt($"  Place your bet (min ${min}, max ${chips}): $");
                if (int.TryParse(input, out int bet) && bet >= min && bet <= chips)
                    return bet;
                PrintLine($"  ✗ Invalid bet. Enter a value between ${min} and ${chips}.", LoseColor);
            }
        }
    }

    // ─────────────────────────────────────────────
    //  Game Engine
    // ─────────────────────────────────────────────
    public class BlackjackGame
    {
        private Deck    _deck = new(6);
        private int     _chips;
        private int     _handsPlayed;
        private int     _handsWon;
        private int     _totalWinnings;

        public BlackjackGame(int startingChips = 1000)
        {
            _chips = startingChips;
        }

        // ── Entry Point ────────────────────────────
        public void Run()
        {
            UI.Header();
            UI.PrintLine("  Welcome to the Blackjack Table!", ConsoleColor.White);
            UI.PrintLine("  Rules: Blackjack pays 3:2 · Dealer stands on soft 17.", ConsoleColor.DarkGray);
            UI.PrintLine("  Commands during play: H)it  S)tand  D)ouble  P)split  Q)uit", ConsoleColor.DarkGray);
            UI.PrintLine();
            UI.DrawChips(_chips);
            UI.PrintLine();
            UI.Pause();

            while (true)
            {
                UI.Header();
                UI.DrawChips(_chips);

                if (_chips < 10)
                {
                    BuyChips();
                    if (_chips < 10) { UI.PrintLine("  Not enough chips. Thanks for playing!", UI.LoseColor); break; }
                }

                string action = UI.Prompt("  [B]et  [Y]Buy chips  [T]Statistics  [Q]Quit → ").ToUpper();
                switch (action)
                {
                    case "B": PlayRound(); break;
                    case "Y": BuyChips();  break;
                    case "T": ShowStats(); break;
                    case "Q": ShowStats(); return;
                }
            }

            ShowStats();
        }

        // ── Buy Chips ──────────────────────────────
        private void BuyChips()
        {
            UI.PrintLine();
            UI.PrintLine("  ╔═══════════════════════╗", UI.ChipColor);
            UI.PrintLine("  ║     CHIP PURCHASE      ║", UI.ChipColor);
            UI.PrintLine("  ╠═══════════════════════╣", UI.ChipColor);
            UI.PrintLine("  ║  [1]  $500             ║", UI.ChipColor);
            UI.PrintLine("  ║  [2]  $1,000           ║", UI.ChipColor);
            UI.PrintLine("  ║  [3]  $5,000           ║", UI.ChipColor);
            UI.PrintLine("  ║  [4]  Custom amount    ║", UI.ChipColor);
            UI.PrintLine("  ╚═══════════════════════╝", UI.ChipColor);
            UI.PrintLine();

            string choice = UI.Prompt("  Your choice: ");
            int add = choice switch
            {
                "1" => 500,
                "2" => 1000,
                "3" => 5000,
                "4" => CustomAmount(),
                _   => 0
            };

            if (add > 0)
            {
                _chips += add;
                UI.PrintLine($"  ✓ Added ${add:N0}! New balance: ${_chips:N0}", UI.WinColor);
            }
            else UI.PrintLine("  No chips purchased.", ConsoleColor.DarkGray);

            UI.PrintLine();
            UI.Pause();
        }

        private int CustomAmount()
        {
            string input = UI.Prompt("  Enter amount to buy: $");
            if (int.TryParse(input, out int amt) && amt > 0) return amt;
            UI.PrintLine("  Invalid amount.", UI.LoseColor);
            return 0;
        }

        // ── Stats ──────────────────────────────────
        private void ShowStats()
        {
            UI.PrintLine();
            UI.PrintLine("  ╔══════════════════════════════╗", ConsoleColor.Cyan);
            UI.PrintLine("  ║         SESSION STATS        ║", ConsoleColor.Cyan);
            UI.PrintLine("  ╠══════════════════════════════╣", ConsoleColor.Cyan);
            UI.PrintLine($"  ║  Hands played : {_handsPlayed,-13}║", ConsoleColor.Cyan);
            UI.PrintLine($"  ║  Hands won    : {_handsWon,-13}║", ConsoleColor.Cyan);
            double pct = _handsPlayed > 0 ? (_handsWon * 100.0 / _handsPlayed) : 0;
            UI.PrintLine($"  ║  Win rate     : {pct:F1}%{new string(' ', 9)}║", ConsoleColor.Cyan);
            string net = (_totalWinnings >= 0 ? "+" : "") + $"${_totalWinnings:N0}";
            UI.PrintLine($"  ║  Net winnings : {net,-13}║", _totalWinnings >= 0 ? UI.WinColor : UI.LoseColor);
            UI.PrintLine($"  ║  Chips left   : ${_chips,-12:N0}║", UI.ChipColor);
            UI.PrintLine("  ╚══════════════════════════════╝", ConsoleColor.Cyan);
            UI.PrintLine();
            UI.Pause();
        }

        // ── Main Round ─────────────────────────────
        private void PlayRound()
        {
            UI.PrintLine();
            int bet = UI.GetBet(_chips);
            _chips -= bet;

            // Deal initial hands
            var dealer = new Hand();
            var playerHands = new List<Hand> { new Hand { Bet = bet } };

            playerHands[0].AddCard(_deck.Deal());
            dealer.AddCard(_deck.Deal());
            playerHands[0].AddCard(_deck.Deal());
            var hole = _deck.Deal();
            hole.FaceDown = true;
            dealer.AddCard(hole);

            UI.AnimateDeal();

            // Check dealer blackjack (peek)
            bool dealerBJ = DealerHasBlackjack(dealer);

            // Insurance offer
            if (dealer.Cards[0].Rank == Rank.Ace)
                OfferInsurance(ref _chips, bet, dealerBJ);

            if (dealerBJ)
            {
                RevealDealer(dealer);
                DrawTable(dealer, playerHands, 0);
                if (playerHands[0].IsBlackjack())
                    UI.PrintLine("  Push — both have Blackjack!", ConsoleColor.DarkYellow);
                else
                {
                    UI.PrintLine("  Dealer has Blackjack! You lose.", UI.LoseColor);
                    _totalWinnings -= bet;
                }
                _handsPlayed++;
                UI.PrintLine();
                UI.Pause();
                return;
            }

            // Player turns
            int handIndex = 0;
            while (handIndex < playerHands.Count)
            {
                var hand = playerHands[handIndex];

                // Auto-resolve blackjack on first hand
                if (hand.IsBlackjack())
                {
                    handIndex++;
                    continue;
                }

                bool standing = false;
                while (!standing && !hand.IsBust())
                {
                    DrawTable(dealer, playerHands, handIndex);

                    // Build available options
                    var opts = new List<string> { "[H]it", "[S]tand" };
                    if (hand.Cards.Count == 2 && _chips >= hand.Bet)
                        opts.Add("[D]ouble");
                    if (hand.CanSplit() && playerHands.Count < 4 && _chips >= hand.Bet)
                        opts.Add("[P]split");
                    opts.Add("[Q]uit round");

                    string prompt = $"  Hand {handIndex + 1}: {string.Join("  ", opts)} → ";
                    string choice = UI.Prompt(prompt);

                    switch (choice)
                    {
                        case "H":
                            hand.AddCard(_deck.Deal());
                            break;

                        case "S":
                            standing = true;
                            break;

                        case "D" when hand.Cards.Count == 2 && _chips >= hand.Bet:
                            _chips -= hand.Bet;
                            hand.Bet *= 2;
                            hand.IsDoubledDown = true;
                            hand.AddCard(_deck.Deal());
                            standing = true;
                            DrawTable(dealer, playerHands, handIndex);
                            UI.PrintLine($"  ↳ Doubled down! Card: {hand.Cards.Last().Display()}", UI.CardColor);
                            break;

                        case "P" when hand.CanSplit() && playerHands.Count < 4 && _chips >= hand.Bet:
                            _chips -= hand.Bet;
                            var newHand = new Hand { Bet = hand.Bet, IsSplit = true };
                            hand.IsSplit = true;
                            newHand.AddCard(hand.Cards[1]);
                            hand.Cards.RemoveAt(1);
                            hand.AddCard(_deck.Deal());
                            newHand.AddCard(_deck.Deal());
                            playerHands.Insert(handIndex + 1, newHand);
                            UI.PrintLine("  ↳ Split! Playing each hand separately.", ConsoleColor.DarkYellow);
                            break;

                        case "Q":
                            UI.PrintLine("  Folding this hand.", ConsoleColor.DarkGray);
                            standing = true;
                            break;

                        default:
                            UI.PrintLine("  ✗ Invalid choice.", UI.LoseColor);
                            break;
                    }
                }

                if (hand.IsBust())
                {
                    DrawTable(dealer, playerHands, handIndex);
                    UI.PrintLine($"  ✗ Hand {handIndex + 1} BUSTS!", UI.LoseColor);
                    Thread.Sleep(600);
                }

                handIndex++;
            }

            // Dealer's turn
            RevealDealer(dealer);
            bool allBust = playerHands.All(h => h.IsBust());

            if (!allBust)
            {
                DrawTable(dealer, playerHands, -1);
                UI.PrintLine("  Dealer's turn...", ConsoleColor.DarkGray);
                Thread.Sleep(500);
                while (DealerShouldHit(dealer))
                {
                    dealer.AddCard(_deck.Deal());
                    DrawTable(dealer, playerHands, -1);
                    UI.PrintLine($"  Dealer hits → {dealer.Cards.Last().Display()}", UI.CardColor);
                    Thread.Sleep(600);
                }
                if (dealer.IsBust())
                    UI.PrintLine("  Dealer BUSTS!", UI.WinColor);
                else
                    UI.PrintLine($"  Dealer stands on {dealer.Score()}.", ConsoleColor.DarkGray);
            }

            // Settle bets
            UI.PrintLine();
            UI.Separator();
            int roundChipsBefore = _chips;
            for (int i = 0; i < playerHands.Count; i++)
                SettleHand(playerHands[i], dealer, i + 1);

            _totalWinnings += _chips - roundChipsBefore - (-playerHands.Sum(h => h.Bet) + (allBust ? 0 : 0));
            // Recalculate net properly
            _handsPlayed += playerHands.Count;
            UI.DrawChips(_chips);
            UI.PrintLine();
            UI.Pause();
        }

        // ── Dealer Logic ───────────────────────────
        private bool DealerHasBlackjack(Hand dealer)
        {
            var hole = dealer.Cards.FirstOrDefault(c => c.FaceDown);
            if (hole == null) return false;
            hole.FaceDown = false;
            bool bj = dealer.IsBlackjack();
            hole.FaceDown = !bj; // keep face-up only if BJ
            return bj;
        }

        private bool DealerShouldHit(Hand dealer)
        {
            int score = dealer.Score();
            if (score < 17) return true;
            if (score == 17 && dealer.IsSoftHand()) return true; // soft 17
            return false;
        }

        private void RevealDealer(Hand dealer)
        {
            foreach (var c in dealer.Cards) c.FaceDown = false;
        }

        // ── Insurance ──────────────────────────────
        private void OfferInsurance(ref int chips, int bet, bool dealerBJ)
        {
            int maxInsure = bet / 2;
            UI.PrintLine($"  Dealer shows Ace. Insurance? (up to ${maxInsure}) [Y/N]", ConsoleColor.DarkYellow);
            string ans = UI.Prompt("  → ");
            if (ans != "Y") return;

            string inp = UI.Prompt($"  Insurance bet (max ${maxInsure}): $");
            if (!int.TryParse(inp, out int ins) || ins < 1 || ins > maxInsure || ins > chips)
            { UI.PrintLine("  No insurance taken.", ConsoleColor.DarkGray); return; }

            chips -= ins;
            if (dealerBJ)
            {
                UI.PrintLine($"  ✓ Insurance pays 2:1! You win ${ins * 2}", UI.WinColor);
                chips += ins * 3;
            }
            else UI.PrintLine($"  ✗ Insurance lost (${ins}).", UI.LoseColor);
        }

        // ── Settlement ─────────────────────────────
        private void SettleHand(Hand hand, Hand dealer, int num)
        {
            int bet = hand.Bet;
            int playerScore = hand.Score();
            int dealerScore = dealer.Score();
            string label = $"Hand {num}";

            if (hand.IsBust())
            {
                UI.PrintLine($"  {label}: BUST → Lost ${bet:N0}", UI.LoseColor);
                _totalWinnings -= bet;
                return;
            }

            if (hand.IsBlackjack() && !dealer.IsBlackjack())
            {
                int payout = (int)(bet * 1.5);
                _chips += bet + payout;
                UI.PrintLine($"  {label}: BLACKJACK! Won ${payout:N0} 🎉", UI.WinColor);
                _handsWon++;
                _totalWinnings += payout;
                return;
            }

            if (dealer.IsBust() || playerScore > dealerScore)
            {
                _chips += bet * 2;
                UI.PrintLine($"  {label}: WIN! +${bet:N0} (You {playerScore} vs Dealer {dealerScore})", UI.WinColor);
                _handsWon++;
                _totalWinnings += bet;
                return;
            }

            if (playerScore == dealerScore)
            {
                _chips += bet;
                UI.PrintLine($"  {label}: PUSH — bet returned (Both {playerScore})", ConsoleColor.DarkYellow);
                return;
            }

            UI.PrintLine($"  {label}: LOSE — Lost ${bet:N0} (You {playerScore} vs Dealer {dealerScore})", UI.LoseColor);
            _totalWinnings -= bet;
        }

        // ── Table Display ──────────────────────────
        private void DrawTable(Hand dealer, List<Hand> playerHands, int activeIndex)
        {
            UI.Header();
            UI.DrawChips(_chips);
            UI.PrintLine();
            UI.Separator();

            // Dealer
            bool hideScore = dealer.Cards.Any(c => c.FaceDown);
            UI.DrawHand("Dealer", dealer, hideScore);
            UI.Separator();

            // Player hands
            for (int i = 0; i < playerHands.Count; i++)
            {
                var h = playerHands[i];
                bool isActive = (i == activeIndex);
                string tag = isActive ? " ◄ YOUR TURN" : "";
                string extra = h.IsDoubledDown ? " [2x]" : h.IsSplit ? " [split]" : "";
                string betStr = $"  Bet: ${h.Bet:N0}{extra}";

                UI.PrintLine($"  Player Hand {i + 1}{tag}", isActive ? ConsoleColor.Yellow : ConsoleColor.White);
                UI.PrintLine(betStr, UI.ChipColor);
                UI.DrawHand("Cards", h);

                if (h.IsBust())         UI.PrintLine("  ✗ BUST", UI.LoseColor);
                else if (h.IsBlackjack()) UI.PrintLine("  ★ BLACKJACK!", UI.WinColor);
                UI.Separator();
            }
            UI.PrintLine();
        }
    }

    // ─────────────────────────────────────────────
    //  Program Entry
    // ─────────────────────────────────────────────
    class Program
    {
        static void Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "♠ Blackjack Simulator";

            var game = new BlackjackGame(startingChips: 1000);
            game.Run();

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("\n  Thanks for playing! Goodbye.\n");
            Console.ResetColor();
        }
    }
}