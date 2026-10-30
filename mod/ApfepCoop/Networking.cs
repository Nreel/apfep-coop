using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSteamworks;
using MelonLoader;

// Co-op transport layer (contract slice 1).
// Transport selection: APFEP_QA_PORT -> localhost UDP QA, APFEP_JOIN -> client,
// otherwise Steam lobby host. Never throws from Tick paths; logs with [apfep] prefix.
namespace ApfepCoop.Net
{
    public sealed class PeerInfo
    {
        public uint PeerId;
        public string SteamId;   // decimal SteamID64, "" for localhost QA peers
        public string Name;
        public bool IsHost;
        public float LastSeen;   // seconds, monotonic wall clock
    }

    public interface ITransport
    {
        bool IsHost { get; }
        string Mode { get; }                 // "steam" | "udp-local"
        string RoomCode { get; }             // steam lobby id decimal string, or "udp:<port>"
        IReadOnlyList<PeerInfo> Peers { get; }
        void OpenAsHost();
        void OpenAsClient(string roomCode);
        void Close();
        void Tick();                         // pump Steam callbacks / drain sockets, non-blocking
        void Send(uint peerId, MsgKind kind, byte[] payload);   // payload WITHOUT header
        void Broadcast(MsgKind kind, byte[] payload);           // to every peer except self
        // Drain received messages; callback runs on the caller thread (Unity main thread).
        void PumpReceived(Action<uint, MsgKind, byte[]> onMessage);
    }

    static class NetTime
    {
        public static double Now() => Environment.TickCount64 / 1000.0;
    }

    struct InboxMsg
    {
        public uint From;
        public MsgKind Kind;
        public byte[] Payload;
    }

    // ---------------------------------------------------------------------
    // localhost QA transport: host binds 127.0.0.1:APFEP_QA_PORT, clients
    // connect there. Non-blocking: sockets are polled via UdpClient.Available.
    // Host assigns uint32 peer ids (host itself = 1) and broadcasts PeerList
    // on every membership change; a client learns its own id from the host's
    // Hello reply. Client->client sends are routed through the host, which
    // relays them to all other peers (localhost QA approximation).
    // ---------------------------------------------------------------------
    public sealed class UdpTransport : ITransport
    {
        const double HelloRetry = 1.0;
        const double PingInterval = 1.5;
        const double PruneAfter = 25.0;

        readonly bool hostMode;
        UdpClient sock;
        int port;
        IPEndPoint hostEP;
        string roomCode = "";
        uint seq;
        double lastPing;
        double lastHello;
        bool warnedMismatch;

        readonly Dictionary<IPEndPoint, uint> epToPeer = new Dictionary<IPEndPoint, uint>();
        readonly Dictionary<uint, IPEndPoint> peerToEP = new Dictionary<uint, IPEndPoint>();
        readonly Dictionary<uint, double> seen = new Dictionary<uint, double>();
        readonly List<PeerInfo> peers = new List<PeerInfo>();
        readonly Queue<InboxMsg> inbox = new Queue<InboxMsg>();
        uint nextPeerId = 2;
        uint hostPeerId = 1;

        public uint LocalPeerId { get; private set; }
        public bool IsHost => hostMode;
        public string Mode => "udp-local";
        public string RoomCode => roomCode;
        public IReadOnlyList<PeerInfo> Peers => peers;

        public UdpTransport(bool asHost) { hostMode = asHost; }

        static int ParsePort(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            s = s.Trim();
            int colon = s.IndexOf(':');
            if (colon >= 0) s = s.Substring(colon + 1);
            return int.TryParse(s, out int p) && p > 0 && p < 65536 ? p : 0;
        }

        public void OpenAsHost()
        {
            port = ParsePort(Environment.GetEnvironmentVariable("APFEP_QA_PORT"));
            if (port <= 0) throw new InvalidOperationException("APFEP_QA_PORT not set or invalid");
            sock = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            roomCode = "udp:" + port;
            LocalPeerId = 1;
            hostPeerId = 1;
            nextPeerId = 2;
            lastPing = NetTime.Now();
            seen[1] = NetTime.Now(); // host itself is peer 1 but not listed in Peers
            MelonLogger.Msg("[apfep] udp host bound 127.0.0.1:" + port + " (room " + roomCode + ")");
        }

        public void OpenAsClient(string roomCode)
        {
            port = ParsePort(roomCode);
            if (port <= 0) port = ParsePort(Environment.GetEnvironmentVariable("APFEP_QA_PORT"));
            if (port <= 0) throw new InvalidOperationException("no QA port available for client join");
            sock = new UdpClient();
            hostEP = new IPEndPoint(IPAddress.Loopback, port);
            this.roomCode = "udp:" + port;
            hostPeerId = 1;
            LocalPeerId = 0;
            lastHello = 0.0;
            lastPing = 0.0;
            MelonLogger.Msg("[apfep] udp client connecting to 127.0.0.1:" + port + " (room " + this.roomCode + ")");
        }

        public void Close()
        {
            if (sock == null) return;
            try
            {
                byte[] bye = Wire.Build(MsgKind.Bye, seq++, LocalPeerId, Array.Empty<byte>());
                if (hostMode)
                    foreach (KeyValuePair<uint, IPEndPoint> kv in peerToEP)
                        if (kv.Key != LocalPeerId) SendRaw(kv.Value, bye);
                else if (hostEP != null) SendRaw(hostEP, bye);
            }
            catch (Exception ex) { MelonLogger.Warning("[apfep] udp close: " + ex.Message); }
            try { sock.Close(); } catch { }
            sock = null;
            peers.Clear();
            epToPeer.Clear();
            peerToEP.Clear();
            seen.Clear();
        }

        public void Tick()
        {
            if (sock == null) return;
            try
            {
                // Non-blocking drain: Receive() is only called when bytes are already buffered.
                while (sock.Available > 0)
                {
                    IPEndPoint remote = null;
                    byte[] raw;
                    try { raw = sock.Receive(ref remote); }
                    catch (SocketException) { break; }
                    if (raw == null || raw.Length < Wire.HeaderSize) continue;
                    if (!Wire.TryParse(raw, out MsgKind kind, out _, out uint from, out byte[] payload)) continue;
                    Handle(kind, from, payload, remote);
                }

                double now = NetTime.Now();
                if (hostMode)
                {
                    if (now - lastPing >= PingInterval)
                    {
                        lastPing = now;
                        byte[] ping = Wire.Build(MsgKind.Ping, seq++, LocalPeerId, Array.Empty<byte>());
                        foreach (KeyValuePair<uint, IPEndPoint> kv in peerToEP)
                            if (kv.Key != LocalPeerId) SendRaw(kv.Value, ping);
                        Prune(now);
                    }
                }
                else
                {
                    if (LocalPeerId == 0)
                    {
                        if (now - lastHello >= HelloRetry)
                        {
                            lastHello = now;
                            SendRaw(hostEP, Wire.Build(MsgKind.Hello, seq++, 0, Wire.Enc(roomCode)));
                        }
                    }
                    else if (now - lastPing >= PingInterval)
                    {
                        lastPing = now;
                        SendRaw(hostEP, Wire.Build(MsgKind.Ping, seq++, LocalPeerId, Array.Empty<byte>()));
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[apfep] udp tick failed: " + ex.Message); }
        }

        void Handle(MsgKind kind, uint from, byte[] payload, IPEndPoint remote)
        {
            double now = NetTime.Now();
            if (hostMode)
            {
                uint peer;
                if (!epToPeer.TryGetValue(remote, out peer))
                {
                    if (kind != MsgKind.Hello) return; // ignore traffic from unknown endpoints
                    peer = nextPeerId++;
                    epToPeer[remote] = peer;
                    peerToEP[peer] = remote;
                    seen[peer] = now;
                    AddPeer(peer, "", "udp-" + peer, false, now);
                    if (!warnedMismatch && payload.Length >= 4)
                    {
                        int off = 0;
                        string room = Wire.Dec(payload, ref off);
                        if (!string.IsNullOrEmpty(room) && room != roomCode)
                        {
                            warnedMismatch = true;
                            MelonLogger.Warning("[apfep] udp hello from " + remote + " claims room '" + room + "', host room is '" + roomCode + "'");
                        }
                    }
                    MelonLogger.Msg("[apfep] udp client joined: peer " + peer + " from " + remote);
                    // Hello reply carries the client's own assigned peer id.
                    byte[] ackRoom = Wire.Enc(roomCode);
                    byte[] ackPayload = new byte[4 + ackRoom.Length];
                    Wire.PutUInt(ackPayload, 0, peer);
                    Buffer.BlockCopy(ackRoom, 0, ackPayload, 4, ackRoom.Length);
                    SendRaw(remote, Wire.Build(MsgKind.Hello, seq++, LocalPeerId, ackPayload));
                    BroadcastPeerList();
                    return;
                }
                seen[peer] = now;
                switch (kind)
                {
                    case MsgKind.Hello:
                        // retry hello from an already-known endpoint: re-ack in case it was lost
                        byte[] ack = new byte[4];
                        Wire.PutUInt(ack, 0, peer);
                        SendRaw(remote, Wire.Build(MsgKind.Hello, seq++, LocalPeerId, ack));
                        break;
                    case MsgKind.Ping:
                        SendRaw(remote, Wire.Build(MsgKind.Pong, seq++, LocalPeerId, Array.Empty<byte>()));
                        break;
                    case MsgKind.Pong:
                        break;
                    case MsgKind.Bye:
                        MelonLogger.Msg("[apfep] udp client left: peer " + peer);
                        RemovePeer(peer);
                        break;
                    default:
                        inbox.Enqueue(new InboxMsg { From = peer, Kind = kind, Payload = payload });
                        // relay client traffic to every other peer (localhost QA mesh approximation)
                        byte[] relayed = Wire.Build(kind, seq++, peer, payload);
                        foreach (KeyValuePair<uint, IPEndPoint> kv in peerToEP)
                            if (kv.Key != peer && kv.Key != LocalPeerId) SendRaw(kv.Value, relayed);
                        break;
                }
            }
            else
            {
                switch (kind)
                {
                    case MsgKind.Hello:
                        if (LocalPeerId == 0 && payload.Length >= 4)
                        {
                            LocalPeerId = Wire.GetUInt(payload, 0);
                            MelonLogger.Msg("[apfep] udp client identified as peer " + LocalPeerId);
                        }
                        break;
                    case MsgKind.PeerList:
                        ApplyPeerList(payload, now);
                        break;
                    case MsgKind.Ping:
                        SendRaw(hostEP, Wire.Build(MsgKind.Pong, seq++, LocalPeerId, Array.Empty<byte>()));
                        seen[hostPeerId] = now;
                        break;
                    case MsgKind.Pong:
                        seen[hostPeerId] = now;
                        break;
                    case MsgKind.Bye:
                        MelonLogger.Warning("[apfep] udp host closed the room");
                        peers.Clear();
                        seen.Clear();
                        break;
                    default:
                        if (from != 0) inbox.Enqueue(new InboxMsg { From = from, Kind = kind, Payload = payload });
                        seen[from] = now;
                        break;
                }
            }
        }



        void ApplyPeerList(byte[] payload, double now)
        {
            peers.Clear();
            PeerListCodec.Decode(payload, (id, steam, isHost, name) =>
            {
                if (isHost) hostPeerId = id;
                if (id != LocalPeerId) peers.Add(new PeerInfo { PeerId = id, SteamId = steam == 0 ? "" : steam.ToString(), Name = name, IsHost = isHost, LastSeen = (float)now });
                seen[id] = now;
            });
        }

        void AddPeer(uint id, string steamId, string name, bool isHost, double now)
        {
            peers.Add(new PeerInfo { PeerId = id, SteamId = steamId, Name = name, IsHost = isHost, LastSeen = (float)now });
            seen[id] = now;
            peers.Sort((a, b) => a.PeerId.CompareTo(b.PeerId));
        }

        void RemovePeer(uint id)
        {
            peerToEP.Remove(id);
            seen.Remove(id);
            peers.RemoveAll(p => p.PeerId == id);
            BroadcastPeerList();
        }

        void Prune(double now)
        {
            List<uint> stale = null;
            foreach (KeyValuePair<uint, double> kv in seen)
                if (kv.Key != LocalPeerId && now - kv.Value > PruneAfter)
                    (stale ??= new List<uint>()).Add(kv.Key);
            if (stale == null) return;
            foreach (uint id in stale)
            {
                MelonLogger.Msg("[apfep] udp pruned stale peer " + id);
                IPEndPoint ep;
                peerToEP.TryGetValue(id, out ep);
                if (ep != null) epToPeer.Remove(ep);
                RemovePeer(id);
            }
        }

        void BroadcastPeerList()
        {
            // Clients need the host entry too (peer 1) to address it; Peers itself excludes self.
            List<PeerInfo> all = new List<PeerInfo>(peers.Count + 1);
            all.Add(new PeerInfo { PeerId = LocalPeerId, SteamId = "", Name = hostMode ? "host" : "client-" + LocalPeerId, IsHost = hostMode, LastSeen = (float)NetTime.Now() });
            all.AddRange(peers);
            byte[] payload = PeerListCodec.Encode(all);
            byte[] raw = Wire.Build(MsgKind.PeerList, seq++, LocalPeerId, payload);
            foreach (KeyValuePair<uint, IPEndPoint> kv in peerToEP)
                if (kv.Key != LocalPeerId) SendRaw(kv.Value, raw);
            MelonLogger.Msg("[apfep] udp peer list (" + peers.Count + "): " + DescribePeers());
        }

        string DescribePeers()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (PeerInfo p in peers)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(p.PeerId + ":" + (p.IsHost ? "host" : p.Name));
            }
            return sb.ToString();
        }

        void SendRaw(IPEndPoint ep, byte[] raw)
        {
            if (sock == null || ep == null) return;
            try { sock.Send(raw, raw.Length, ep); }
            catch (Exception ex) { MelonLogger.Warning("[apfep] udp send failed: " + ex.Message); }
        }

        public void Send(uint peerId, MsgKind kind, byte[] payload)
        {
            if (sock == null || peerId == LocalPeerId) return;
            byte[] raw = Wire.Build(kind, seq++, LocalPeerId, payload);
            if (hostMode)
            {
                IPEndPoint ep;
                if (!peerToEP.TryGetValue(peerId, out ep))
                {
                    MelonLogger.Warning("[apfep] udp: unknown peer " + peerId);
                    return;
                }
                SendRaw(ep, raw);
            }
            else
            {
                // client-only knowledge: everything routes through the host;
                // the host relays to all other peers.
                SendRaw(hostEP, raw);
            }
        }

        public void Broadcast(MsgKind kind, byte[] payload)
        {
            if (sock == null) return;
            byte[] raw = Wire.Build(kind, seq++, LocalPeerId, payload);
            if (hostMode)
            {
                foreach (KeyValuePair<uint, IPEndPoint> kv in peerToEP)
                    if (kv.Key != LocalPeerId) SendRaw(kv.Value, raw);
            }
            else
            {
                SendRaw(hostEP, raw); // host relays to the rest of the room
            }
        }

        public void PumpReceived(Action<uint, MsgKind, byte[]> onMessage)
        {
            if (onMessage == null) return;
            while (inbox.Count > 0)
            {
                InboxMsg m = inbox.Dequeue();
                try { onMessage(m.From, m.Kind, m.Payload); }
                catch (Exception ex) { MelonLogger.Warning("[apfep] udp receive handler failed: " + ex.Message); }
            }
        }
    }

    // ---------------------------------------------------------------------
    // Steam transport: host creates a matchmaking lobby and logs its decimal
    // id as the room code; clients join via SteamMatchmaking.JoinLobby with
    // that code. Lobby membership is enumerated every tick and mapped to
    // uint32 peer ids (host = 1, host-assigned, stable within the session).
    // Messaging uses SteamNetworkingMessages (SendSteamMessage is exposed as
    // SendMessageToUser in this Steamworks.NET build), identities built from
    // member SteamIds. No managed Callback<T> objects are used; lobby state
    // is polled every tick. SteamAPI.RunCallbacks() is pumped only when the
    // game's SteamService has not initialized Steam itself.
    // ---------------------------------------------------------------------
    public sealed class SteamTransport : ITransport
    {
        const int LobbyMemberLimit = 8;
        const double PingInterval = 1.0;
        const double SteamGateTimeout = 20.0;
        const double JoinTimeout = 30.0;
        const int MaxReceive = 16;

        readonly bool hostMode;
        CSteamID lobby;
        ulong lobbyId;      // decimal room code
        ulong mySteamId;
        ulong hostSteamId;
        string roomCode = "";
        bool ready;
        bool lobbyIssued;   // CreateLobby/JoinLobby issued once Steam context is live
        bool warnedSteamGate;
        bool warnedJoin;
        double startedAt;
        double lastPing;
        uint seq;
        uint localId;
        uint hostPeerId = 1;
        uint nextPeerId = 2;

        readonly Dictionary<ulong, uint> steamToPeer = new Dictionary<ulong, uint>();
        readonly Dictionary<uint, ulong> peerToSteam = new Dictionary<uint, ulong>();
        readonly Dictionary<uint, string> names = new Dictionary<uint, string>();
        readonly Dictionary<uint, double> seen = new Dictionary<uint, double>();
        readonly List<PeerInfo> peers = new List<PeerInfo>();
        readonly Queue<InboxMsg> inbox = new Queue<InboxMsg>();
        readonly HashSet<ulong> warnedUnknown = new HashSet<ulong>();

        readonly Il2CppStructArray<IntPtr> recvArray = new Il2CppStructArray<IntPtr>(MaxReceive);
        byte[] recvBuf = new byte[4096];
        IntPtr scratch = IntPtr.Zero;
        int scratchCap;

        public uint LocalPeerId => localId;
        public bool IsHost => hostMode;
        public string Mode => "steam";
        public string RoomCode => roomCode;
        public IReadOnlyList<PeerInfo> Peers => peers;

        public SteamTransport(bool asHost) { hostMode = asHost; }

        public void OpenAsHost()
        {
            startedAt = NetTime.Now();
            MelonLogger.Msg("[apfep] steam host: waiting for game SteamService, then creating lobby");
        }

        public void OpenAsClient(string roomCode)
        {
            ulong id;
            string s = (roomCode ?? "").Trim();
            int colon = s.IndexOf(':');
            if (colon >= 0) s = s.Substring(colon + 1);
            if (!ulong.TryParse(s, out id) || id == 0)
                throw new InvalidOperationException("APFEP_JOIN is not a decimal lobby id: '" + roomCode + "'");
            lobbyId = id;
            lobby = new CSteamID();
            lobby.m_SteamID = id;
            this.roomCode = id.ToString();
            startedAt = NetTime.Now();
            MelonLogger.Msg("[apfep] steam client: joining lobby " + this.roomCode + " once Steam is live");
        }

        public void Close()
        {
            try
            {
                if (ready) Broadcast(MsgKind.Bye, Array.Empty<byte>());
                if (lobbyId != 0) SteamMatchmaking.LeaveLobby(lobby);
            }
            catch (Exception ex) { MelonLogger.Warning("[apfep] steam close: " + ex.Message); }
            if (scratch != IntPtr.Zero) { Marshal.FreeHGlobal(scratch); scratch = IntPtr.Zero; scratchCap = 0; }
            peers.Clear();
            steamToPeer.Clear();
            peerToSteam.Clear();
            seen.Clear();
            ready = false;
        }

        public void Tick()
        {
            try
            {
                if (!SteamAPI.IsSteamRunning()) return;
                bool gameInit = GameSteamInitialized();
                // Game pumps its own callbacks once its SteamService is initialized;
                // until then we pump them ourselves so async lobby calls progress.
                if (!gameInit)
                {
                    SteamAPI.RunCallbacks();
                    double gateNow = NetTime.Now();
                    if (!warnedSteamGate && gateNow - startedAt > SteamGateTimeout)
                    {
                        warnedSteamGate = true;
                        MelonLogger.Warning("[apfep] steam transport idle: game SteamService not initialized after " + (int)SteamGateTimeout + "s");
                    }
                    return; // lobby/messaging calls need the game's Steam context pointers
                }

                mySteamId = SteamUser.GetSteamID().m_SteamID;
                if (mySteamId == 0) return;

                if (!ready) { PollReady(); return; }

                if (hostMode) RefreshMembersHost();

                Receive();

                double now = NetTime.Now();
                if (now - lastPing >= PingInterval)
                {
                    lastPing = now;
                    Broadcast(MsgKind.Ping, Array.Empty<byte>());
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[apfep] steam tick failed: " + ex.Message); }
        }

        static bool GameSteamInitialized()
        {
            try
            {
                Il2CppCodeBase.Services.SteamService.SteamService svc = UnityEngine.Object.FindFirstObjectByType<Il2CppCodeBase.Services.SteamService.SteamService>();
                if (svc == null) return false;
                return svc.IsAvailable; // SteamService._isAvailable
            }
            catch { return false; }
        }

        void PollReady()
        {
            double now = NetTime.Now();
            if (hostMode)
            {
                if (!lobbyIssued)
                {
                    lobbyIssued = true;
                    SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, LobbyMemberLimit);
                    MelonLogger.Msg("[apfep] steam host: CreateLobby issued (public, limit " + LobbyMemberLimit + ")");
                }
                // The lobby id arrives asynchronously (LobbyCreated_t); poll the
                // joined-lobby list instead of relying on managed callbacks.
                for (int i = 0; i < 16; i++)
                {
                    CSteamID cand = SteamMatchmaking.GetLobbyByIndex(i);
                    ulong candId = cand.m_SteamID;
                    if (candId == 0 || !cand.IsValid()) continue;
                    if (SteamMatchmaking.GetLobbyOwner(cand).m_SteamID != mySteamId) continue;
                    lobby = cand;
                    lobbyId = candId;
                    roomCode = candId.ToString();
                    ready = true;
                    localId = 1;
                    hostPeerId = 1;
                    nextPeerId = 2;
                    hostSteamId = mySteamId;
                    SteamMatchmaking.SetLobbyMemberLimit(cand, LobbyMemberLimit);
                    SteamMatchmaking.SetLobbyData(cand, "apfep-coop", "1");
                    steamToPeer[mySteamId] = 1;
                    peerToSteam[1] = mySteamId;
                    seen[1] = now;
                    MelonLogger.Msg("[apfep] steam lobby ready: room code " + roomCode);
                    BroadcastPeerList();
                    return;
                }
                if (!warnedJoin && now - startedAt > JoinTimeout)
                {
                    warnedJoin = true;
                    MelonLogger.Warning("[apfep] steam lobby creation did not complete within " + (int)JoinTimeout + "s");
                }
            }
            else
            {
                if (!lobbyIssued)
                {
                    lobbyIssued = true;
                    SteamMatchmaking.JoinLobby(lobby);
                }
                if (SteamMatchmaking.GetNumLobbyMembers(lobby) > 0)
                {
                    ready = true;
                    hostSteamId = SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID;
                    localId = 0; // learned from host PeerList (steam id match)
                    hostPeerId = 1; // host is always peer 1; map it so we can send before PeerList
                    steamToPeer[hostSteamId] = hostPeerId;
                    peerToSteam[hostPeerId] = hostSteamId;
                    seen[hostPeerId] = now;
                    MelonLogger.Msg("[apfep] steam client joined lobby " + roomCode + " (owner " + hostSteamId + ")");
                    Send(hostPeerId, MsgKind.Hello, Wire.Enc(roomCode));
                }
                else if (!warnedJoin && now - startedAt > JoinTimeout)
                {
                    warnedJoin = true;
                    MelonLogger.Warning("[apfep] steam join of lobby " + roomCode + " did not complete within " + (int)JoinTimeout + "s");
                }
            }
        }

        void RefreshMembersHost()
        {
            HashSet<ulong> members = new HashSet<ulong>();
            int n = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < n; i++)
            {
                CSteamID m = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
                if (m.IsValid() && m.m_SteamID != 0) members.Add(m.m_SteamID);
            }
            if (members.Count == 0) return;

            bool changed = false;
            foreach (ulong steam in members)
                if (!steamToPeer.ContainsKey(steam))
                {
                    uint id = nextPeerId++;
                    steamToPeer[steam] = id;
                    peerToSteam[id] = steam;
                    names[id] = PersonaName(steam);
                    seen[id] = NetTime.Now();
                    changed = true;
                    MelonLogger.Msg("[apfep] steam member joined: peer " + id + " (steamid " + steam + ")");
                }
            List<uint> gone = null;
            foreach (KeyValuePair<uint, ulong> kv in peerToSteam)
                if (!members.Contains(kv.Value)) (gone ??= new List<uint>()).Add(kv.Key);
            if (gone != null)
            {
                foreach (uint id in gone)
                {
                    ulong steam = peerToSteam[id];
                    peerToSteam.Remove(id);
                    steamToPeer.Remove(steam);
                    seen.Remove(id);
                    names.Remove(id);
                    changed = true;
                    MelonLogger.Msg("[apfep] steam member left: peer " + id + " (steamid " + steam + ")");
                }
            }
            if (changed) BroadcastPeerList();
        }

        void Receive()
        {
            int got = SteamNetworkingMessages.ReceiveMessagesOnChannel(0, recvArray, MaxReceive);
            if (got <= 0) return;
            if (got > MaxReceive) got = MaxReceive;
            double now = NetTime.Now();
            for (int i = 0; i < got; i++)
            {
                IntPtr p = recvArray[i];
                if (p == IntPtr.Zero) continue;
                try
                {
                    SteamNetworkingMessage_t msg = SteamNetworkingMessage_t.FromIntPtr(p);
                    int cb = msg.m_cbSize;
                    IntPtr data = msg.m_pData;
                    ulong sender = msg.m_identityPeer.GetSteamID64();
                    if (cb > 0 && data != IntPtr.Zero)
                    {
                        byte[] raw;
                        if (cb == recvBuf.Length) raw = recvBuf;
                        else
                        {
                            if (cb > recvBuf.Length) recvBuf = new byte[Math.Max(cb, 8192)];
                            Marshal.Copy(data, recvBuf, 0, cb);
                            raw = new byte[cb];
                            Buffer.BlockCopy(recvBuf, 0, raw, 0, cb);
                        }
                        Handle(sender, raw, now);
                    }
                }
                catch (Exception ex) { MelonLogger.Warning("[apfep] steam receive failed: " + ex.Message); }
                finally { SteamNetworkingMessage_t.Release(p); }
            }
        }

        void Handle(ulong senderSteam, byte[] raw, double now)
        {
            if (!Wire.TryParse(raw, out MsgKind kind, out _, out uint headerPeer, out byte[] payload)) return;

            uint from;
            if (!steamToPeer.TryGetValue(senderSteam, out from))
            {
                if (hostMode)
                {
                    // host is authoritative for id assignment; a message from a member
                    // whose lobby membership refresh hasn't run yet gets its id now.
                    from = nextPeerId++;
                    steamToPeer[senderSteam] = from;
                    peerToSteam[from] = senderSteam;
                    names[from] = PersonaName(senderSteam);
                    BroadcastPeerList();
                }
                else
                {
                    if (senderSteam == hostSteamId) { from = hostPeerId; steamToPeer[hostSteamId] = hostPeerId; peerToSteam[hostPeerId] = hostSteamId; }
                    else
                    {
                        if (warnedUnknown.Add(senderSteam))
                            MelonLogger.Warning("[apfep] steam message from unknown peer (steamid " + senderSteam + ") before PeerList; dropped");
                        return;
                    }
                }
            }
            seen[from] = now;

            switch (kind)
            {
                case MsgKind.PeerList:
                    if (!hostMode) ApplyPeerList(payload, now);
                    break;
                case MsgKind.Ping:
                    Send(from, MsgKind.Pong, Array.Empty<byte>());
                    break;
                case MsgKind.Pong:
                    break;
                case MsgKind.Hello:
                    if (hostMode)
                    {
                        int off = 0;
                        string room = Wire.Dec(payload, ref off);
                        MelonLogger.Msg("[apfep] steam hello from peer " + from + ": room '" + room + "'");
                    }
                    break;
                case MsgKind.Bye:
                    if (hostMode)
                    {
                        MelonLogger.Msg("[apfep] steam peer " + from + " said bye");
                        // membership refresh will drop it if they actually left the lobby
                    }
                    else if (from == hostPeerId)
                    {
                        MelonLogger.Warning("[apfep] steam host closed the room");
                        peers.Clear();
                    }
                    break;
                default:
                    inbox.Enqueue(new InboxMsg { From = headerPeer != 0 ? headerPeer : from, Kind = kind, Payload = payload });
                    break;
            }
        }

        void ApplyPeerList(byte[] payload, double now)
        {
            peers.Clear();
            PeerListCodec.Decode(payload, (id, steam, isHost, name) =>
            {
                ulong steam64 = (ulong)steam;
                if (isHost) { hostPeerId = id; if (steam64 != 0) hostSteamId = steam64; }
                if (steam64 == mySteamId) { localId = id; return; } // self entry
                steamToPeer[steam64] = id;
                peerToSteam[id] = steam64;
                names[id] = name;
                seen[id] = now;
                peers.Add(new PeerInfo { PeerId = id, SteamId = steam64.ToString(), Name = name, IsHost = isHost, LastSeen = (float)now });
            });
            peers.Sort((a, b) => a.PeerId.CompareTo(b.PeerId));
            if (localId != 0)
                MelonLogger.Msg("[apfep] steam peer list (" + (peers.Count + 1) + "), local peer " + localId + ": " + DescribePeers());
        }

        string PersonaName(ulong steam64)
        {
            try
            {
                CSteamID id = new CSteamID();
                id.m_SteamID = steam64;
                string name = SteamFriends.GetFriendPersonaName(id);
                return string.IsNullOrEmpty(name) ? steam64.ToString() : name;
            }
            catch { return steam64.ToString(); }
        }

        void BroadcastPeerList()
        {
            byte[] payload = PeerListCodec.Encode(peersAll());
            SendRawToAll(MsgKind.PeerList, payload);
            MelonLogger.Msg("[apfep] steam peer list (" + peers.Count + " + host), local peer " + localId + ": " + DescribePeers());
        }

        List<PeerInfo> peersAll()
        {
            List<PeerInfo> all = new List<PeerInfo>(peers.Count + 1);
            foreach (PeerInfo p in peers) all.Add(p);
            all.Add(new PeerInfo { PeerId = localId != 0 ? localId : hostPeerId, SteamId = mySteamId.ToString(), Name = PersonaName(mySteamId), IsHost = hostMode, LastSeen = (float)NetTime.Now() });
            return all;
        }

        string DescribePeers()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (PeerInfo p in peers)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(p.PeerId + ":" + (string.IsNullOrEmpty(p.Name) ? p.SteamId : p.Name));
            }
            return sb.ToString();
        }

        void EnsureScratch(int size)
        {
            if (scratch == IntPtr.Zero || scratchCap < size)
            {
                if (scratch != IntPtr.Zero) Marshal.FreeHGlobal(scratch);
                scratchCap = Math.Max(size, 1024);
                scratch = Marshal.AllocHGlobal(scratchCap);
            }
        }

        // Writes [kind][seq][peerFrom][payload] into native scratch and sends to
        // each target identity. One scratch fill serves a whole broadcast.
        void SendRawToAll(MsgKind kind, byte[] payload)
        {
            int len = Wire.HeaderSize + (payload?.Length ?? 0);
            EnsureScratch(len);
            IntPtr s = scratch;
            Marshal.WriteByte(s, 0, (byte)kind);
            WriteUInt(s, 1, seq++);
            WriteUInt(s, 5, localId);
            if (payload != null && payload.Length > 0) Marshal.Copy(payload, 0, s + Wire.HeaderSize, payload.Length);
            foreach (KeyValuePair<uint, ulong> kv in peerToSteam)
            {
                if (kv.Key == localId) continue; // never send to self
                SendToSteam(kv.Value, s, len);
            }
        }

        void WriteUInt(IntPtr buf, int off, uint v)
        {
            Marshal.WriteByte(buf, off, (byte)v);
            Marshal.WriteByte(buf, off + 1, (byte)(v >> 8));
            Marshal.WriteByte(buf, off + 2, (byte)(v >> 16));
            Marshal.WriteByte(buf, off + 3, (byte)(v >> 24));
        }

        void SendToSteam(ulong steam64, IntPtr buf, int len)
        {
            SteamNetworkingIdentity identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(steam64);
            EResult res = SteamNetworkingMessages.SendMessageToUser(ref identity, buf, (uint)len, 0, 0);
            if (res != EResult.k_EResultOK)
                MelonLogger.Warning("[apfep] SendMessageToUser to steamid " + steam64 + " failed: " + res);
        }

        public void Send(uint peerId, MsgKind kind, byte[] payload)
        {
            if (peerId == localId) return;
            ulong steam;
            if (!peerToSteam.TryGetValue(peerId, out steam))
            {
                MelonLogger.Warning("[apfep] steam: unknown peer " + peerId + " (need PeerList first)");
                return;
            }
            int len = Wire.HeaderSize + (payload?.Length ?? 0);
            EnsureScratch(len);
            IntPtr s = scratch;
            Marshal.WriteByte(s, 0, (byte)kind);
            WriteUInt(s, 1, seq++);
            WriteUInt(s, 5, localId);
            if (payload != null && payload.Length > 0) Marshal.Copy(payload, 0, s + Wire.HeaderSize, payload.Length);
            SendToSteam(steam, s, len);
        }

        public void Broadcast(MsgKind kind, byte[] payload)
        {
            SendRawToAll(kind, payload);
        }

        public void PumpReceived(Action<uint, MsgKind, byte[]> onMessage)
        {
            if (onMessage == null) return;
            while (inbox.Count > 0)
            {
                InboxMsg m = inbox.Dequeue();
                try { onMessage(m.From, m.Kind, m.Payload); }
                catch (Exception ex) { MelonLogger.Warning("[apfep] steam receive handler failed: " + ex.Message); }
            }
        }
    }

    // Facade used by the integration owner: CoopPlugin patches InputService.Update
    // and calls CoopNet.Tick() once per frame on the Unity main thread, then
    // SyncBrain/PuppetBrains consume CoopNet.Transport + CoopNet.LocalPeerId.
    public sealed class CoopNet
    {
        public static ITransport Transport;
        public static bool IsHost;
        public static string Mode = "none";
        public static uint LocalPeerId;

        static bool offlineWarned;

        // Selection order per contract: APFEP_QA_PORT, then APFEP_JOIN, then Steam host.
        public static void Open()
        {
            if (Transport != null || offlineWarned) return;
            string qaPort = Environment.GetEnvironmentVariable("APFEP_QA_PORT");
            string join = Environment.GetEnvironmentVariable("APFEP_JOIN");
            try
            {
                if (!string.IsNullOrEmpty(qaPort))
                {
                    UdpTransport t = new UdpTransport(string.IsNullOrEmpty(join));
                    if (string.IsNullOrEmpty(join)) t.OpenAsHost();
                    else t.OpenAsClient(join);
                    Transport = t;
                    Mode = t.Mode;
                    IsHost = t.IsHost;
                    return;
                }
                if (!string.IsNullOrEmpty(join))
                {
                    SteamTransport t = new SteamTransport(false);
                    t.OpenAsClient(join);
                    Transport = t;
                    Mode = t.Mode;
                    IsHost = false;
                    return;
                }
                if (SteamAPI.IsSteamRunning())
                {
                    SteamTransport t = new SteamTransport(true);
                    t.OpenAsHost();
                    Transport = t;
                    Mode = t.Mode;
                    IsHost = true;
                    return;
                }
                offlineWarned = true;
                MelonLogger.Warning("[apfep] Steam not running and no APFEP_QA_PORT/APFEP_JOIN set — co-op transport stays offline (single-player unchanged)");
            }
            catch (Exception ex)
            {
                offlineWarned = true;
                MelonLogger.Warning("[apfep] transport open failed: " + ex.Message + " — co-op stays offline");
            }
        }

        public static void Tick()
        {
            try
            {
                if (Transport == null) Open();
                ITransport t = Transport;
                if (t == null) return;
                t.Tick();
                IsHost = t.IsHost;
                Mode = t.Mode;
                if (t is UdpTransport u) LocalPeerId = u.LocalPeerId;
                else if (t is SteamTransport s) LocalPeerId = s.LocalPeerId;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[apfep] CoopNet.Tick failed: " + ex.Message);
            }
        }
    }
}
