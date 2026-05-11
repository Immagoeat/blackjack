using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace BlackjackSimulator
{
    // Message types sent between host and clients
    public enum NetMsg { State, Action, PlayerInfo, Ping, Pong, Chat }

    public class NetPacket
    {
        public NetMsg    Type    { get; set; }
        public string    Payload { get; set; } = "";
        public int       Seat    { get; set; }  // which player seat this is from
    }

    // Minimal serialisable snapshot of the poker game sent to all clients each turn
    public class PokerStateSnapshot
    {
        public PokerPhase          Phase       { get; set; }
        public int                 Pot         { get; set; }
        public int                 CurrentBet  { get; set; }
        public int                 ActiveIdx   { get; set; }
        public int                 DealerIdx   { get; set; }
        public int                 RaiseAmount { get; set; }
        public string              ShowdownMsg { get; set; } = "";
        public List<string>        Log         { get; set; } = new();
        public List<NetPlayerSnap> Players     { get; set; } = new();
        public List<NetCardSnap>   Community   { get; set; } = new();
        // Hole cards: index = player seat. Clients only receive their own hole cards
        // (host sends a targeted packet per client). All face-down for others.
        public List<List<NetCardSnap>> HoleCards { get; set; } = new();
    }

    public class NetPlayerSnap
    {
        public string     Name       { get; set; } = "";
        public int        Chips      { get; set; }
        public bool       IsHuman    { get; set; }
        public bool       IsFolded   { get; set; }
        public bool       IsAllIn    { get; set; }
        public bool       IsOut      { get; set; }
        public int        Bet        { get; set; }
        public PokerAction LastAction { get; set; }
        public bool       IsWinner   { get; set; }
    }

    public class NetCardSnap
    {
        public Suit   Suit     { get; set; }
        public Rank   Rank     { get; set; }
        public double DealTime { get; set; }
    }

    public class NetActionPayload
    {
        public PokerAction Action    { get; set; }
        public int         RaiseAmt  { get; set; }
        public int         Seat      { get; set; }
    }

    public class LanHost : IDisposable
    {
        public const int PORT = 27015;

        private TcpListener?            _listener;
        private readonly List<TcpClient> _clients = new();
        private readonly List<Thread>    _threads = new();
        private bool                     _running;

        public int                SeatCount  { get; private set; } = 1; // host is seat 0
        public List<string>       ClientNames { get; } = new();
        public event Action<NetActionPayload>? ActionReceived;
        public event Action<int>?              ClientConnected;   // seat index
        public event Action<int>?              ClientDisconnected;

        public void Start(int totalSeats)
        {
            SeatCount = totalSeats;
            _running  = true;
            _listener = new TcpListener(IPAddress.Any, PORT);
            _listener.Start();
            var t = new Thread(AcceptLoop) { IsBackground = true };
            t.Start();
            _threads.Add(t);
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    int seat;
                    lock (_clients) { seat = _clients.Count + 1; _clients.Add(client); }
                    ClientNames.Add($"Player {seat + 1}");
                    ClientConnected?.Invoke(seat);
                    var rt = new Thread(() => ReadLoop(client, seat)) { IsBackground = true };
                    rt.Start();
                    _threads.Add(rt);
                }
                catch { break; }
            }
        }

        private void ReadLoop(TcpClient client, int seat)
        {
            try
            {
                var stream = client.GetStream();
                var buf    = new byte[65536];
                while (_running)
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n == 0) break;
                    var pkt = JsonSerializer.Deserialize<NetPacket>(buf.AsSpan(0, n));
                    if (pkt?.Type == NetMsg.Action)
                    {
                        var act = JsonSerializer.Deserialize<NetActionPayload>(pkt.Payload) ?? new();
                        act.Seat = seat;
                        ActionReceived?.Invoke(act);
                    }
                    else if (pkt?.Type == NetMsg.PlayerInfo)
                    {
                        if (seat < ClientNames.Count + 1)
                            ClientNames[seat - 1] = pkt.Payload;
                    }
                }
            }
            catch { }
            finally { ClientDisconnected?.Invoke(seat); }
        }

        public void BroadcastState(PokerGame game, double now)
        {
            var snap = BuildSnapshot(game, now);
            // Send full state to all clients (hole cards masked)
            for (int i = 0; i < _clients.Count; i++)
            {
                int clientSeat = i + 1;
                var s = MaskSnapshot(snap, clientSeat);
                Send(_clients[i], new NetPacket {
                    Type    = NetMsg.State,
                    Seat    = 0,
                    Payload = JsonSerializer.Serialize(s)
                });
            }
        }

        private void Send(TcpClient client, NetPacket pkt)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(pkt);
                client.GetStream().Write(bytes, 0, bytes.Length);
            }
            catch { }
        }

        private static PokerStateSnapshot BuildSnapshot(PokerGame game, double now)
        {
            var snap = new PokerStateSnapshot {
                Phase       = game.Phase,
                Pot         = game.Pot,
                CurrentBet  = game.CurrentBet,
                ActiveIdx   = game.ActiveIdx,
                DealerIdx   = game.DealerIdx,
                RaiseAmount = game.RaiseAmount,
                ShowdownMsg = game.ShowdownMsg,
                Log         = new List<string>(game.Log),
            };
            foreach (var p in game.Players)
            {
                snap.Players.Add(new NetPlayerSnap {
                    Name       = p.Name,
                    Chips      = p.Chips,
                    IsHuman    = p.IsHuman,
                    IsFolded   = p.IsFolded,
                    IsAllIn    = p.IsAllIn,
                    IsOut      = p.IsOut,
                    Bet        = p.Bet,
                    LastAction = p.LastAction,
                    IsWinner   = game.Winners.Contains(p),
                });
                snap.HoleCards.Add(MapCards(p.HoleCards));
            }
            snap.Community = MapCards(game.Community);
            return snap;
        }

        // Replace other players' hole cards with face-down placeholders
        private static PokerStateSnapshot MaskSnapshot(PokerStateSnapshot snap, int clientSeat)
        {
            var masked = JsonSerializer.Deserialize<PokerStateSnapshot>(
                JsonSerializer.Serialize(snap))!;
            for (int i = 0; i < masked.HoleCards.Count; i++)
            {
                bool showdown = snap.Phase == PokerPhase.Showdown;
                if (i != clientSeat && !showdown)
                    foreach (var c in masked.HoleCards[i])
                        c.Rank = Rank.Two; // sentinel for face-down (renderer checks DealTime < 0)
            }
            return masked;
        }

        private static List<NetCardSnap> MapCards(List<Card> cards)
        {
            var list = new List<NetCardSnap>();
            foreach (var c in cards)
                list.Add(new NetCardSnap { Suit = c.Suit, Rank = c.Rank, DealTime = c.DealTime });
            return list;
        }

        public void Dispose()
        {
            _running = false;
            _listener?.Stop();
            foreach (var c in _clients) c.Close();
        }
    }

    public class LanClient : IDisposable
    {
        private TcpClient?  _client;
        private bool        _running;

        public int    MySeat      { get; private set; } = 1;
        public bool   IsConnected => _client?.Connected == true;

        public event Action<PokerStateSnapshot>? StateReceived;
        public event Action?                     Disconnected;

        public bool Connect(string host, string playerName)
        {
            try
            {
                _client  = new TcpClient();
                _client.Connect(host, LanHost.PORT);
                _running = true;

                // Send our name
                Send(new NetPacket { Type = NetMsg.PlayerInfo, Payload = playerName, Seat = 0 });

                var t = new Thread(ReadLoop) { IsBackground = true };
                t.Start();
                return true;
            }
            catch { return false; }
        }

        private void ReadLoop()
        {
            var buf = new byte[65536];
            try
            {
                var stream = _client!.GetStream();
                while (_running)
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n == 0) break;
                    var pkt = JsonSerializer.Deserialize<NetPacket>(buf.AsSpan(0, n));
                    if (pkt?.Type == NetMsg.State)
                    {
                        var snap = JsonSerializer.Deserialize<PokerStateSnapshot>(pkt.Payload);
                        if (snap != null) StateReceived?.Invoke(snap);
                    }
                }
            }
            catch { }
            finally { Disconnected?.Invoke(); }
        }

        public void SendAction(PokerAction action, int raiseAmt = 0)
        {
            Send(new NetPacket {
                Type    = NetMsg.Action,
                Seat    = MySeat,
                Payload = JsonSerializer.Serialize(new NetActionPayload {
                    Action = action, RaiseAmt = raiseAmt, Seat = MySeat
                })
            });
        }

        private void Send(NetPacket pkt)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(pkt);
                _client?.GetStream().Write(bytes, 0, bytes.Length);
            }
            catch { }
        }

        public void Dispose()
        {
            _running = false;
            _client?.Close();
        }
    }
}
