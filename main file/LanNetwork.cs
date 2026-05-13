using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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

    internal static class NetLog
    {
        internal static void Print(string msg) =>
            Console.WriteLine($"[NET {DateTime.Now:HH:mm:ss.fff}] {msg}");
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

            // Print local IPs so the host can share them easily
            var localIPs = NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                         && !IPAddress.IsLoopback(a.Address))
                .Select(a => a.Address.ToString())
                .ToList();
            NetLog.Print($"Table created — listening on port {PORT}  ({totalSeats} seats)");
            foreach (var ip in localIPs)
                NetLog.Print($"  Local IP: {ip}");
            if (localIPs.Count == 0)
                NetLog.Print("  (no non-loopback IPv4 addresses found)");

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
                    string remoteIP = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
                    int seat;
                    lock (_clients) { seat = _clients.Count + 1; _clients.Add(client); }
                    ClientNames.Add($"Player {seat + 1}");
                    NetLog.Print($"Player connected from {remoteIP}  (seat {seat})  [{_clients.Count}/{SeatCount - 1} clients]");
                    ClientConnected?.Invoke(seat);
                    var rt = new Thread(() => ReadLoop(client, seat, remoteIP)) { IsBackground = true };
                    rt.Start();
                    _threads.Add(rt);
                }
                catch { break; }
            }
            NetLog.Print("Accept loop stopped.");
        }

        private void ReadLoop(TcpClient client, int seat, string remoteIP)
        {
            try
            {
                var stream = client.GetStream();
                while (_running)
                {
                    var pkt = ReadPacket(stream);
                    if (pkt == null) break;
                    if (pkt.Type == NetMsg.Action)
                    {
                        var act = JsonSerializer.Deserialize<NetActionPayload>(pkt.Payload) ?? new();
                        act.Seat = seat;
                        NetLog.Print($"Action from seat {seat} ({remoteIP}): {act.Action}" +
                            (act.Action == PokerAction.Raise ? $" +${act.RaiseAmt}" : ""));
                        ActionReceived?.Invoke(act);
                    }
                    else if (pkt.Type == NetMsg.PlayerInfo)
                    {
                        int nameIdx = seat - 1;
                        if (nameIdx >= 0 && nameIdx < ClientNames.Count)
                        {
                            string oldName = ClientNames[nameIdx];
                            ClientNames[nameIdx] = pkt.Payload;
                            NetLog.Print($"Seat {seat} ({remoteIP}) set name: \"{pkt.Payload}\"" +
                                (oldName != pkt.Payload ? $"  (was \"{oldName}\")" : ""));
                        }
                    }
                }
            }
            catch (Exception ex) { NetLog.Print($"Read error on seat {seat} ({remoteIP}): {ex.Message}"); }
            finally
            {
                NetLog.Print($"Seat {seat} ({remoteIP}) disconnected.");
                ClientDisconnected?.Invoke(seat);
            }
        }

        public void BroadcastState(PokerGame game, double now)
        {
            var snap = BuildSnapshot(game, now);
            lock (_clients)
            {
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
            NetLog.Print($"State broadcast → {_clients.Count} client(s)  phase={game.Phase}  pot=${game.Pot}  active={game.ActiveIdx}");
        }

        private static void Send(TcpClient client, NetPacket pkt)
        {
            try { WritePacket(client.GetStream(), pkt); }
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

        // Length-prefix framing shared by host and client
        internal static void WritePacket(NetworkStream stream, NetPacket pkt)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(pkt);
            var len  = BitConverter.GetBytes(body.Length);
            stream.Write(len,  0, 4);
            stream.Write(body, 0, body.Length);
        }

        internal static NetPacket? ReadPacket(NetworkStream stream)
        {
            var lenBuf = new byte[4];
            if (!ReadExact(stream, lenBuf, 4)) return null;
            int size = BitConverter.ToInt32(lenBuf, 0);
            if (size <= 0 || size > 1 << 20) return null; // sanity cap 1 MB
            var body = new byte[size];
            if (!ReadExact(stream, body, size)) return null;
            return JsonSerializer.Deserialize<NetPacket>(body);
        }

        private static bool ReadExact(NetworkStream stream, byte[] buf, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buf, read, count - read);
                if (n == 0) return false;
                read += n;
            }
            return true;
        }

        public void Dispose()
        {
            _running = false;
            _listener?.Stop();
            lock (_clients) { foreach (var c in _clients) c.Close(); }
            NetLog.Print("Host shut down.");
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
            NetLog.Print($"Connecting to {host}:{LanHost.PORT} as \"{playerName}\"...");
            try
            {
                _client  = new TcpClient();
                _client.Connect(host, LanHost.PORT);
                _running = true;
                NetLog.Print($"Connected to {host}:{LanHost.PORT}  (local {_client.Client.LocalEndPoint})");

                Send(new NetPacket { Type = NetMsg.PlayerInfo, Payload = playerName, Seat = 0 });
                NetLog.Print($"Sent player name: \"{playerName}\"");

                var t = new Thread(ReadLoop) { IsBackground = true };
                t.Start();
                return true;
            }
            catch (Exception ex)
            {
                NetLog.Print($"Connection failed: {ex.Message}");
                return false;
            }
        }

        private void ReadLoop()
        {
            int stateCount = 0;
            try
            {
                var stream = _client!.GetStream();
                NetLog.Print("Read loop started — waiting for state packets.");
                while (_running)
                {
                    var pkt = LanHost.ReadPacket(stream);
                    if (pkt == null) break;
                    if (pkt.Type == NetMsg.State)
                    {
                        var snap = JsonSerializer.Deserialize<PokerStateSnapshot>(pkt.Payload);
                        if (snap != null)
                        {
                            stateCount++;
                            NetLog.Print($"State received #{stateCount}  phase={snap.Phase}  pot=${snap.Pot}  active={snap.ActiveIdx}");
                            StateReceived?.Invoke(snap);
                        }
                    }
                }
            }
            catch (Exception ex) { NetLog.Print($"Read error: {ex.Message}"); }
            finally
            {
                NetLog.Print($"Disconnected from host after {stateCount} state packet(s).");
                Disconnected?.Invoke();
            }
        }

        public void SendAction(PokerAction action, int raiseAmt = 0)
        {
            NetLog.Print($"Sending action: {action}" + (action == PokerAction.Raise ? $" +${raiseAmt}" : ""));
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
                if (_client?.Connected == true)
                    LanHost.WritePacket(_client.GetStream(), pkt);
            }
            catch { }
        }

        public void Dispose()
        {
            _running = false;
            _client?.Close();
            NetLog.Print("Client shut down.");
        }
    }
}
