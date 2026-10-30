using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using ApfepCoop.Net;

// =====================================================================================
// ApfepCoop.Sync.SyncBrain — host-authoritative co-op state sync (slice 2).
//
// Message header is added by the transport (Wire.Build): [kind][seq][peerFrom]; the
// payloads below are headerless. All numbers little-endian; Vector3 = 3 float32;
// Quaternion = 4 float32; enum fields are int32 of the enum value.
//
// ---------------------------------------------------------------------------
// Snapshot payload (MsgKind.Snapshot, host -> client(s)):
//   [int32 version=1]
//   [int32 game_state_id]                      eGameState id of the host
//   [int32 gold][int32 diamonds]               CurrencyData values
//   [int32 lab_manifest_hash][int32 declined_manifest_hash]   PlayerProgress
//   [int32 potion_manifest_hash][int32 slot_manifest_hash]    PotionsPersistentData
//   [byte game_started][byte lab_completed][byte lab_summary_shown]
//   [int32 total_playtime_seconds][int32 experience_points][int32 onboarding_flags]
//   [int32 achievement_moments][int32 unclaimed_experience][int32 unannounced_skill_points]
//   [int32 potions_finished][int32 potions_shelved_home][int32 pours][int32 undos]
//   [int32 vat_pours_in][int32 vat_draws_out][int32 apron_stows][int32 cat_pets]
//   [int32 misfiles][int32 selected_hand_index]
//   [int32 owned_count] then [int32 x owned_count]        eSkill ids
//   [int32 switched_off_count] then [int32 ...]           eSkill ids
//   [int32 scored_shelves_count] then [int32 ...]         shelf uids
//   [int32 scored_sections_count] then [int32 ...]        section keys
//   [int32 bucket_count] then per bucket:
//     [int32 bucket_tag][int32 entry_count] then per entry:
//       [int32 uid][byte location]            ePotionLocation
//       [Vector3 position][Quaternion rotation]
//       [int32 slot_uid][int32 hand_index]
//       [byte unit_count] then [byte x unit_count]   eSectionFamily ids
//       [byte scored_shelve]
//   A full snapshot is authoritative: a client replaces the listed buckets' entries.
//
// ---------------------------------------------------------------------------
// Delta payload (MsgKind.Delta, host -> all peers):
//   [int32 version=1][uint32 mask]
//   Present fields follow in ascending bit order, each exactly as in the snapshot.
//   bit 0  game_state_id          bit 1  gold                bit 2  diamonds
//   bit 3  lab_manifest_hash      bit 4  declined_manifest_hash
//   bit 5  potion_manifest_hash   bit 6  slot_manifest_hash
//   bit 7  game_started           bit 8  lab_completed       bit 9  lab_summary_shown
//   bit 10 total_playtime_seconds bit 11 experience_points   bit 12 onboarding_flags
//   bit 13 achievement_moments    bit 14 unclaimed_experience bit 15 unannounced_skill_points
//   bit 16 potions_finished       bit 17 potions_shelved_home bit 18 pours
//   bit 19 undos                  bit 20 vat_pours_in        bit 21 vat_draws_out
//   bit 22 apron_stows            bit 23 cat_pets            bit 24 misfiles
//   bit 25 selected_hand_index    bit 26 owned_skills        bit 27 switched_off_skills
//   bit 28 scored_shelves         bit 29 scored_sections
//   bit 30 potion entries (all buckets, whole-list replacement)
//   Empty delta (mask == 0) is never sent. Lists/entries are sent whole when changed.
//
// ---------------------------------------------------------------------------
// StateChange payload (MsgKind.StateChange, host -> all peers): [int32 eGameState id]
// Ready payload   (MsgKind.Ready, client -> host):              [uint32 peer][int32 snapshot_version]
//   Sent by a client right after it applies a snapshot; the host answers with a fresh
//   snapshot so a late joiner always lands on current state.
//
// SyncBrain owns the receive drain (transport.PumpReceived) and forwards every kind it
// does not own (PuppetMove, Hello, PeerList, Ping/Pong, Bye, Error) to
// SyncBrain.NonSyncMessageHandler, so PuppetBrain can subscribe without double-draining.
// =====================================================================================
namespace ApfepCoop.Sync
{
    public sealed class SyncBrain
    {
        public static bool IsInstalled;
        public static int HostGameStateId = -1;   // authoritative eGameState id
        public static int Gold;
        public static int Diamonds;

        // Kinds SyncBrain does not consume are handed to this callback (slice 3/4 hooks).
        public static Action<uint, MsgKind, byte[]> NonSyncMessageHandler;

        const int WireVersion = 1;
        const int DeltaIntervalSeconds = 250;   // ms between host delta broadcasts
        const int MaxBuckets = 16;              // potion bucket probe cap
        const int MaxEntriesPerBucket = 64;     // payload cap per bucket
        const float PositionEpsilon = 0.001f;

        // ---- delta bit assignments ----
        const uint BitGameState = 1u << 0;
        const uint BitGold = 1u << 1;
        const uint BitDiamonds = 1u << 2;
        const uint BitLabManifestHash = 1u << 3;
        const uint BitDeclinedManifestHash = 1u << 4;
        const uint BitPotionManifestHash = 1u << 5;
        const uint BitSlotManifestHash = 1u << 6;
        const uint BitGameStarted = 1u << 7;
        const uint BitLabCompleted = 1u << 8;
        const uint BitLabSummaryShown = 1u << 9;
        const uint BitPlaytime = 1u << 10;
        const uint BitExperience = 1u << 11;
        const uint BitOnboardingFlags = 1u << 12;
        const uint BitAchievementMoments = 1u << 13;
        const uint BitUnclaimedExp = 1u << 14;
        const uint BitUnannouncedSkillPts = 1u << 15;
        const uint BitPotionsFinished = 1u << 16;
        const uint BitPotionsShelvedHome = 1u << 17;
        const uint BitPours = 1u << 18;
        const uint BitUndos = 1u << 19;
        const uint BitVatPoursIn = 1u << 20;
        const uint BitVatDrawsOut = 1u << 21;
        const uint BitApronStows = 1u << 22;
        const uint BitCatPets = 1u << 23;
        const uint BitMisfiles = 1u << 24;
        const uint BitSelectedHand = 1u << 25;
        const uint BitOwnedSkills = 1u << 26;
        const uint BitSwitchedOffSkills = 1u << 27;
        const uint BitScoredShelves = 1u << 28;
        const uint BitScoredSections = 1u << 29;
        const uint BitEntries = 1u << 30;

        // ------------------------------------------------------------------
        // sampled authoritative state
        // ------------------------------------------------------------------
        sealed class State
        {
            public int GameStateId;
            public int Gold;
            public int Diamonds;
            public int LabManifestHash;
            public int DeclinedManifestHash;
            public int PotionManifestHash;
            public int SlotManifestHash;
            public bool GameStarted;
            public bool LabCompleted;
            public bool LabSummaryShown;
            public int Playtime;
            public int Experience;
            public int OnboardingFlags;
            public int AchievementMoments;
            public int UnclaimedExperience;
            public int UnannouncedSkillPoints;
            public int PotionsFinished;
            public int PotionsShelvedHome;
            public int Pours;
            public int Undos;
            public int VatPoursIn;
            public int VatDrawsOut;
            public int ApronStows;
            public int CatPets;
            public int Misfiles;
            public int SelectedHandIndex;
            public List<int> OwnedSkills = new List<int>();
            public List<int> SwitchedOffSkills = new List<int>();
            public List<int> ScoredShelves = new List<int>();
            public List<int> ScoredSections = new List<int>();
            public List<Bucket> Buckets = new List<Bucket>();
        }

        sealed class Bucket
        {
            public int Tag;
            public List<Entry> Entries = new List<Entry>();
        }

        sealed class Entry
        {
            public int Uid;
            public int Location;
            public Vector3 Position;
            public Quaternion Rotation;
            public int SlotUid;
            public int HandIndex;
            public List<int> Units = new List<int>();
            public bool ScoredShelve;
        }

        static readonly State current = new State();
        static readonly State baseline = new State();
        static bool haveBaseline;
        static bool snapshotDirty;
        static long lastDeltaMs;
        static readonly HashSet<uint> knownPeers = new HashSet<uint>();
        static bool servicesWarned;

        // lazily resolved game services
        static Il2CppCodeBase.Services.PersistentDataService.IPersistentDataService persistentData;
        static Il2CppCodeBase.Services.RuntimeDataService.IRuntimeDataService runtimeData;
        static Il2CppCodeBase.Services.SaveLoadService.ISaveLoadService saveLoad;
        static Il2CppCodeBase.Services.GameStateMachineService.IGameStateMachine stateMachine;
        static Il2CppCodeBase.Services.PotionsPersistenceService.IPotionsPersistence potionsPersistence;

        static Il2CppCodeBase.Services.PersistentDataService.PlayerProgress progress;
        static Il2CppCodeBase.Services.RuntimeDataService.PlayerRuntimeData runtimePlayer;
        static Il2CppCodeBase.Services.PotionsPersistenceService.PotionsPersistence potionsPersistenceImpl;
        static Il2CppCodeBase.Services.GameStateMachineService.GameStateMachine stateMachineImpl;

        static readonly Buf snapshotBuf = new Buf();
        static readonly Buf deltaBuf = new Buf();
        static readonly byte[] readyPayload = new byte[8];
        static readonly byte[] stateChangePayload = new byte[4];
        static HarmonyLib.Harmony harmonyInstance;

        // true once a puppet has completed its own boot (reached lobby or a level state)
        static bool puppetBootDone;
        static int lastReadySent = -1;
        static readonly Dictionary<uint, int> peerAcked = new Dictionary<uint, int>();

        public static bool LocalIsPuppet => CoopNet.Transport != null && !CoopNet.Transport.IsHost;

        public static bool Install(HarmonyLib.Harmony harmony)
        {
            if (harmony is null) return false;
            harmonyInstance = harmony;
            int patched = 0;

            // Enter(eGameState) is patchable under il2cpp interop and carries the puppet remap,
            // local state tracking and the host's transition broadcast. The payload overload
            // Enter(eGameState, TPayload) fails ("IL Compile Error"), so it is not attempted.
            patched += TryPatch(
                AccessTools.Method(typeof(Il2CppCodeBase.Services.GameStateMachineService.GameStateMachine),
                    "Enter", new Type[] { typeof(Il2Cpp.eGameState) }),
                new HarmonyMethod(typeof(SyncBrain), nameof(EnterPrefix)),
                new HarmonyMethod(typeof(SyncBrain), nameof(EnterPostfix)));

            // AssignNewState is the choke point every Enter overload funnels through; a postfix
            // here backstops state tracking for transitions that bypass Enter(eGameState).
            patched += TryPatch(
                AccessTools.Method(typeof(Il2CppCodeBase.Services.GameStateMachineService.GameStateMachine),
                    "AssignNewState"),
                null, new HarmonyMethod(typeof(SyncBrain), nameof(AssignPostfix)));

            // --- clients never write saves ---
            // SaveImpl / PersistentDataService.Save are generic il2cpp methods and cannot be
            // patched under il2cpp interop ("IL Compile Error"); every save reaches disk through
            // BackgroundSaveWriter.Write, which is patchable, so suppression happens there.
            patched += TryPatch(
                AccessTools.Method(typeof(Il2CppCodeBase.Services.SaveLoadService.BackgroundSaveWriter), "Write"),
                new HarmonyMethod(typeof(SyncBrain), nameof(ClientOnlySuppressPrefix)), null);

            // --- clients never push undo records (undo stack stays local) ---
            patched += TryPatch(
                AccessTools.Method(typeof(Il2CppCodeBase.Services.UndoService.UndoService), "Record"),
                new HarmonyMethod(typeof(SyncBrain), nameof(ClientOnlySuppressPrefix)), null);

            // --- puppets produce no move input ---
            patched += TryPatch(
                AccessTools.Method(typeof(Il2CppCodeBase.Components.Hero.HeroMover), "GetMoveInput"),
                new HarmonyMethod(typeof(SyncBrain), nameof(GetMoveInputPrefix)), null);

            // --- host: full snapshot after (re)populating the lab ---
            patched += TryPatch(
                AccessTools.Method(typeof(Il2CppCodeBase.Services.GameStateMachineService.LoadLevelState), "PopulateLevel"),
                null, new HarmonyMethod(typeof(SyncBrain), nameof(LabPopulatedPostfix)));
            patched += TryPatch(
                AccessTools.Method(typeof(Il2CppCodeBase.Services.GameStateMachineService.LoadLevelState), "LoadCurrentLevel"),
                null, new HarmonyMethod(typeof(SyncBrain), nameof(LabPopulatedPostfix)));

            // --- host: deal/manifest mutations force a full snapshot ---
            patched += TryPatch(
                AccessTools.Method(typeof(Il2CppCodeBase.Services.PotionsPersistenceService.PotionsPersistence), "ClaimDeal"),
                null, new HarmonyMethod(typeof(SyncBrain), nameof(ManifestChangedPostfix)));
            patched += TryPatch(
                AccessTools.Method(typeof(Il2CppCodeBase.Services.PotionsPersistenceService.PotionsPersistence), "AdoptLab"),
                null, new HarmonyMethod(typeof(SyncBrain), nameof(ManifestChangedPostfix)));
            patched += TryPatch(
                AccessTools.Method(typeof(Il2CppCodeBase.Services.PotionsPersistenceService.PotionsPersistence), "Pin"),
                null, new HarmonyMethod(typeof(SyncBrain), nameof(ManifestChangedPostfix)));
            patched += TryPatch(
                AccessTools.Method(typeof(Il2CppCodeBase.Services.PotionsPersistenceService.PotionsPersistence), "Flush"),
                null, new HarmonyMethod(typeof(SyncBrain), nameof(ManifestChangedPostfix)));

            IsInstalled = patched > 0;
            MelonLogger.Msg($"[apfep] sync installed: {patched} patch(es)");
            return IsInstalled;
        }

        public static void Tick()
        {
            try
            {
                ITransport transport = CoopNet.Transport;
                if (transport is null) return;

                EnsureServices();
                DiffPeers(transport);
                transport.PumpReceived(OnMessage);

                if (transport.IsHost) HostTick(transport);
                else FollowHostState();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[apfep] SyncBrain.Tick failed: " + ex.Message);
            }
        }

        public static void OnPeerJoined(uint peerId)
        {
            knownPeers.Add(peerId);
            ITransport transport = CoopNet.Transport;
            if (transport is null || !transport.IsHost) return;
            if (peerId == CoopNet.LocalPeerId) return;
            try
            {
                byte[] snapshot = BuildSnapshot(current, snapshotBuf);
                transport.Send(peerId, MsgKind.Snapshot, snapshot);
                haveBaseline = true;
                CaptureBaseline(current);
                MelonLogger.Msg($"[apfep] snapshot -> peer {peerId} ({snapshot.Length} bytes)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[apfep] snapshot to peer {peerId} failed: " + ex.Message);
            }
        }

        public static void OnPeerLeft(uint peerId)
        {
            knownPeers.Remove(peerId);
        }

        public static string ProgressSummary()
        {
            try
            {
                string stateName = HostGameStateId >= 0 ? StateName(HostGameStateId) : "booting";
                return $"lab: {stateName} | gold {Gold} | diamonds {Diamonds} | pours {current.Pours} | undos {current.Undos}"
                     + $" | finished {current.PotionsFinished} | shelves {current.ScoredShelves.Count} | time {current.Playtime}s";
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[apfep] ProgressSummary failed: " + ex.Message);
                return "lab: unknown";
            }
        }

        // ------------------------------------------------------------------
        // frame pump
        // ------------------------------------------------------------------
        static void HostTick(ITransport transport)
        {
            SampleState(current);
            HostGameStateId = current.GameStateId;
            Gold = current.Gold;
            Diamonds = current.Diamonds;

            int remote = 0;
            IReadOnlyList<PeerInfo> peers = transport.Peers;
            if (peers != null)
            {
                for (int i = 0; i < peers.Count; i++)
                {
                    if (peers[i].PeerId != CoopNet.LocalPeerId) remote++;
                }
            }
            if (remote == 0 && !snapshotDirty) return;

            if (snapshotDirty || !haveBaseline)
            {
                byte[] snapshot = BuildSnapshot(current, snapshotBuf);
                transport.Broadcast(MsgKind.Snapshot, snapshot);
                CaptureBaseline(current);
                snapshotDirty = false;
                return;
            }

            long now = Environment.TickCount64;
            if (now - lastDeltaMs < DeltaIntervalSeconds) return;
            lastDeltaMs = now;

            uint mask = DiffMask(current, baseline);
            if (mask == 0) return;
            byte[] delta = BuildDelta(current, mask, deltaBuf);
            transport.Broadcast(MsgKind.Delta, delta);
            CaptureBaseline(current);
        }

        static void DiffPeers(ITransport transport)
        {
            IReadOnlyList<PeerInfo> peers = transport.Peers;
            if (peers is null) return;

            foreach (PeerInfo p in peers)
            {
                if (!knownPeers.Contains(p.PeerId)) OnPeerJoined(p.PeerId);
            }

            List<uint> gone = null;
            foreach (uint id in knownPeers)
            {
                bool seen = false;
                foreach (PeerInfo p in peers) if (p.PeerId == id) { seen = true; break; }
                if (!seen) { if (gone is null) gone = new List<uint>(); gone.Add(id); }
            }
            if (gone != null)
            {
                foreach (uint id in gone) OnPeerLeft(id);
            }
        }

        static void OnMessage(uint from, MsgKind kind, byte[] payload)
        {
            ITransport transport = CoopNet.Transport;
            bool isHost = transport != null && transport.IsHost;

            switch (kind)
            {
                case MsgKind.Snapshot:
                    if (isHost) return;
                    ApplySnapshot(from, payload);
                    break;
                case MsgKind.Delta:
                    if (isHost) return;
                    ApplyDelta(from, payload);
                    break;
                case MsgKind.StateChange:
                    if (isHost) return;
                    if (payload != null && payload.Length >= 4)
                    {
                        HostGameStateId = Wire.GetInt(payload, 0);
                        current.GameStateId = HostGameStateId;
                    }
                    break;
                case MsgKind.Ready:
                    if (!isHost) return;
                    if (payload != null && payload.Length >= 8)
                    {
                        uint peer = Wire.GetUInt(payload, 0);
                        int version = Wire.GetInt(payload, 4);
                        peerAcked[peer] = version;
                        // a peer acking the current wire format needs no resend; only a version
                        // mismatch (or a pending dirty snapshot) re-sends, else acks ping-pong
                        if (version == WireVersion && !snapshotDirty) return;
                        MelonLogger.Msg($"[apfep] peer {peer} ready v{version} (wire v{WireVersion}) — resending snapshot");
                        OnPeerJoined(peer);
                    }
                    break;
                default:
                    NonSyncMessageHandler?.Invoke(from, kind, payload);
                    break;
            }
        }

        // ------------------------------------------------------------------
        // host sampling
        // ------------------------------------------------------------------
        static bool servicesReady;

        // VContainer binds contracts, not concrete types: a missing concrete registration must
        // not take the whole sync path down, so every resolve is guarded separately.
        static void EnsureServices()
        {
            if (servicesReady) return;
            try
            {
                if (persistentData is null)
                {
                    try
                    {
                        persistentData = Il2CppCodeBase.Services.Core.ServiceLocator
                            .Resolve<Il2CppCodeBase.Services.PersistentDataService.IPersistentDataService>();
                    }
                    catch (Exception) { }
                }
                if (runtimeData is null)
                {
                    try
                    {
                        runtimeData = Il2CppCodeBase.Services.Core.ServiceLocator
                            .Resolve<Il2CppCodeBase.Services.RuntimeDataService.IRuntimeDataService>();
                    }
                    catch (Exception) { }
                }
                if (saveLoad is null)
                {
                    try
                    {
                        saveLoad = Il2CppCodeBase.Services.Core.ServiceLocator
                            .Resolve<Il2CppCodeBase.Services.SaveLoadService.ISaveLoadService>();
                    }
                    catch (Exception) { }
                }
                if (stateMachine is null)
                {
                    try
                    {
                        stateMachine = Il2CppCodeBase.Services.Core.ServiceLocator
                            .Resolve<Il2CppCodeBase.Services.GameStateMachineService.IGameStateMachine>();
                    }
                    catch (Exception) { }
                }
                if (potionsPersistence is null)
                {
                    try
                    {
                        potionsPersistence = Il2CppCodeBase.Services.Core.ServiceLocator
                            .Resolve<Il2CppCodeBase.Services.PotionsPersistenceService.IPotionsPersistence>();
                    }
                    catch (Exception) { }
                }
                // concrete services are optional: GetBucket falls back to PersistentDataService.Get<T>(tag)
                if (potionsPersistenceImpl is null)
                {
                    try
                    {
                        potionsPersistenceImpl = Il2CppCodeBase.Services.Core.ServiceLocator
                            .Resolve<Il2CppCodeBase.Services.PotionsPersistenceService.PotionsPersistence>();
                    }
                    catch (Exception) { }
                }
                if (stateMachineImpl is null)
                {
                    try
                    {
                        stateMachineImpl = Il2CppCodeBase.Services.Core.ServiceLocator
                            .Resolve<Il2CppCodeBase.Services.GameStateMachineService.GameStateMachine>();
                    }
                    catch (Exception) { }
                }

                if (persistentData is null) return;

                if (progress is null)
                {
                    try { progress = persistentData.Get<Il2CppCodeBase.Services.PersistentDataService.PlayerProgress>(); }
                    catch (Exception) { }
                }
                if (runtimePlayer is null && runtimeData != null)
                {
                    try { runtimePlayer = runtimeData.Get<Il2CppCodeBase.Services.RuntimeDataService.PlayerRuntimeData>(); }
                    catch (Exception) { }
                }

                if (persistentData is null || progress is null) return;
                servicesReady = true;
                MelonLogger.Msg($"[apfep] sync services ready: buckets={(potionsPersistenceImpl is null ? "PersistentDataService.Get<T>(tag)" : "PotionsPersistence.GetBucket")}, state={(stateMachineImpl is null ? "interface only" : "concrete")}");
            }
            catch (Exception ex)
            {
                if (!servicesWarned)
                {
                    servicesWarned = true;
                    MelonLogger.Warning("[apfep] sync services unavailable: " + ex.Message);
                }
            }
        }

        static void SampleState(State s)
        {
            EnsureServices();

            // game state: prefer the id captured by the Enter patch; fall back to the machine field
            if (HostGameStateId < 0 && stateMachineImpl != null)
            {
                try { HostGameStateId = (int)stateMachineImpl._currentState; }
                catch (Exception) { /* field unavailable until the machine exists */ }
            }
            if (HostGameStateId < 0) HostGameStateId = (int)Il2Cpp.eGameState.None;
            s.GameStateId = HostGameStateId;

            var p = progress;
            if (p is null) return;

            s.Gold = ReadInt(p.Currency?.Get(Il2Cpp.eCurrency.Gold));
            s.Diamonds = ReadInt(p.Currency?.Get(Il2Cpp.eCurrency.Diamonds));
            s.LabManifestHash = p.LabManifestHash;
            s.DeclinedManifestHash = p.DeclinedManifestHash;
            s.GameStarted = p.GameStarted;
            s.LabCompleted = p.LabCompleted;
            s.LabSummaryShown = p.LabSummaryShown;
            s.Playtime = ReadInt(p.TotalPlaytimeSeconds);
            s.Experience = ReadInt(p.ExperiencePoints);
            s.OnboardingFlags = ReadInt(p.OnboardingFlags);
            s.AchievementMoments = ReadInt(p.AchievementMoments);
            s.PotionsFinished = ReadInt(p.PotionsFinished);
            s.PotionsShelvedHome = ReadInt(p.PotionsShelvedHome);
            s.Pours = ReadInt(p.Pours);
            s.Undos = ReadInt(p.Undos);
            s.VatPoursIn = ReadInt(p.VatPoursIn);
            s.VatDrawsOut = ReadInt(p.VatDrawsOut);
            s.ApronStows = ReadInt(p.ApronStows);
            s.CatPets = ReadInt(p.CatPets);
            s.Misfiles = p.Misfiles;
            s.SelectedHandIndex = p.SelectedHandIndex;

            ReadIntList(p.OwnedSkills, s.OwnedSkills);
            ReadIntList(p.SwitchedOffSkills, s.SwitchedOffSkills);
            ReadIntList(p.ScoredShelves, s.ScoredShelves);
            ReadIntList(p.ScoredSections, s.ScoredSections);

            var rt = runtimePlayer;
            if (rt != null)
            {
                s.UnclaimedExperience = ReadInt(rt.UnclaimedExperience);
                s.UnannouncedSkillPoints = ReadInt(rt.UnannouncedSkillPoints);
            }

            SampleBuckets(s);
        }

        // Bucket source: concrete PotionsPersistence.GetBucket when registered, otherwise the
        // persistence service's keyed generic getter (key = type name + tag).
        static Il2CppCodeBase.Services.PersistentDataService.PotionsPersistentData ProbeBucket(int tag)
        {
            var impl = potionsPersistenceImpl;
            if (impl != null)
            {
                try { return impl.GetBucket(tag); }
                catch (Exception) { return null; }
            }
            var pd = persistentData;
            if (pd is null) return null;
            try
            {
                return pd.Get<Il2CppCodeBase.Services.PersistentDataService.PotionsPersistentData>(tag);
            }
            catch (Exception) { return null; }
        }

        static void SampleBuckets(State s)
        {
            s.Buckets.Clear();
            if (potionsPersistenceImpl is null && persistentData is null) return;

            int emptyRun = 0;
            for (int tag = 0; tag < MaxBuckets; tag++)
            {
                Il2CppCodeBase.Services.PersistentDataService.PotionsPersistentData data;
                try { data = ProbeBucket(tag); }
                catch (Exception) { break; }   // bucket index past the packed uid range
                if (data is null)
                {
                    if (++emptyRun >= 3) break;
                    continue;
                }
                emptyRun = 0;

                Bucket b = new Bucket { Tag = tag };
                int potionHash = data._potionManifestHash;
                int slotHash = data._slotManifestHash;
                if (s.PotionManifestHash == 0) s.PotionManifestHash = potionHash;
                if (s.SlotManifestHash == 0) s.SlotManifestHash = slotHash;

                Il2CppSystem.Collections.Generic.List<Il2CppCodeBase.Services.PersistentDataService.PotionPersistentEntry> entries = data._entries;
                if (entries is null) { s.Buckets.Add(b); continue; }
                int n = entries.Count;
                for (int i = 0; i < n; i++)
                {
                    if (b.Entries.Count >= MaxEntriesPerBucket) break;
                    Il2CppCodeBase.Services.PersistentDataService.PotionPersistentEntry e = entries[i];
                    if (e is null) continue;
                    Entry en = new Entry
                    {
                        Uid = e.Uid,
                        Location = (int)e.Location,
                        Position = e.Position,
                        Rotation = e.Rotation,
                        SlotUid = e.SlotUid,
                        HandIndex = e.HandIndex,
                        ScoredShelve = e.ScoredShelve,
                    };
                    Il2CppStructArray<Il2Cpp.eSectionFamily> units = e.Units;
                    if (units != null)
                    {
                        for (int k = 0; k < units.Length; k++) en.Units.Add((int)units[k]);
                    }
                    b.Entries.Add(en);
                }
                s.Buckets.Add(b);
            }
        }

        static int ReadInt(Il2CppCodeBase.Common.Utilities.ReactiveInt r)
        {
            if (r is null) return 0;
            return r.Value;
        }

        static void WriteInt(Il2CppCodeBase.Common.Utilities.ReactiveInt r, int value)
        {
            if (r is null) return;
            if (r.Value == value) return;
            int previous = r.Value;
            r.Value = value;
            // publish the change to R3 subscribers so HUD/time readouts refresh on puppets
            try { r.EditorOnValueChanged(previous, value); }
            catch (Exception) { /* notify path optional; field is already applied */ }
        }

        static void ReadIntList(Il2CppCodeBase.Common.Utilities.ReactiveList<Il2Cpp.eSkill> list, List<int> into)
        {
            into.Clear();
            if (list is null) return;
            Il2CppSystem.Collections.Generic.List<Il2Cpp.eSkill> inner = list._innerList;
            if (inner is null) return;
            int n = inner.Count;
            for (int i = 0; i < n; i++) into.Add((int)inner[i]);
        }

        static void ReadIntList(Il2CppCodeBase.Common.Utilities.ReactiveList<int> list, List<int> into)
        {
            into.Clear();
            if (list is null) return;
            Il2CppSystem.Collections.Generic.List<int> inner = list._innerList;
            if (inner is null) return;
            int n = inner.Count;
            for (int i = 0; i < n; i++) into.Add(inner[i]);
        }

        // ------------------------------------------------------------------
        // encoding
        // ------------------------------------------------------------------
        sealed class Buf
        {
            public byte[] Data = new byte[1024];
            public int Len;

            public void Reset() { Len = 0; }

            void Need(int extra)
            {
                if (Len + extra <= Data.Length) return;
                int size = Data.Length;
                while (size < Len + extra) size *= 2;
                byte[] grown = new byte[size];
                Buffer.BlockCopy(Data, 0, grown, 0, Len);
                Data = grown;
            }

            public void Int(int v) { Need(4); Wire.PutInt(Data, Len, v); Len += 4; }
            public void UInt(uint v) { Need(4); Wire.PutUInt(Data, Len, v); Len += 4; }
            public void Byte(byte v) { Need(1); Data[Len] = v; Len++; }
            public void Bool(bool v) { Byte(v ? (byte)1 : (byte)0); }
            public void Float(float v) { Need(4); Wire.PutFloat(Data, Len, v); Len += 4; }
            public void V3(Vector3 v) { Need(12); Wire.PutFloat(Data, Len, v.x); Len += 4; Wire.PutFloat(Data, Len, v.y); Len += 4; Wire.PutFloat(Data, Len, v.z); Len += 4; }
            public void Quat(Quaternion q) { Need(16); Wire.PutFloat(Data, Len, q.x); Len += 4; Wire.PutFloat(Data, Len, q.y); Len += 4; Wire.PutFloat(Data, Len, q.z); Len += 4; Wire.PutFloat(Data, Len, q.w); Len += 4; }

            public void IntList(List<int> items)
            {
                Int(items.Count);
                for (int i = 0; i < items.Count; i++) Int(items[i]);
            }

            public void Buckets(List<Bucket> buckets)
            {
                Int(buckets.Count);
                for (int i = 0; i < buckets.Count; i++)
                {
                    Bucket b = buckets[i];
                    Int(b.Tag);
                    Int(b.Entries.Count);
                    for (int j = 0; j < b.Entries.Count; j++)
                    {
                        Entry e = b.Entries[j];
                        Int(e.Uid);
                        Byte((byte)e.Location);
                        V3(e.Position);
                        Quat(e.Rotation);
                        Int(e.SlotUid);
                        Int(e.HandIndex);
                        Byte((byte)e.Units.Count);
                        for (int k = 0; k < e.Units.Count; k++) Byte((byte)e.Units[k]);
                        Bool(e.ScoredShelve);
                    }
                }
            }

            public byte[] ToArray()
            {
                byte[] outBuf = new byte[Len];
                Buffer.BlockCopy(Data, 0, outBuf, 0, Len);
                return outBuf;
            }
        }

        static void PutScalars(Buf buf, State s)
        {
            buf.Int(WireVersion);
            buf.Int(s.GameStateId);
            buf.Int(s.Gold);
            buf.Int(s.Diamonds);
            buf.Int(s.LabManifestHash);
            buf.Int(s.DeclinedManifestHash);
            buf.Int(s.PotionManifestHash);
            buf.Int(s.SlotManifestHash);
        }

        static void PutFlags(Buf buf, State s)
        {
            buf.Bool(s.GameStarted);
            buf.Bool(s.LabCompleted);
            buf.Bool(s.LabSummaryShown);
        }

        static void PutCounters(Buf buf, State s)
        {
            buf.Int(s.Playtime);
            buf.Int(s.Experience);
            buf.Int(s.OnboardingFlags);
            buf.Int(s.AchievementMoments);
            buf.Int(s.UnclaimedExperience);
            buf.Int(s.UnannouncedSkillPoints);
            buf.Int(s.PotionsFinished);
            buf.Int(s.PotionsShelvedHome);
            buf.Int(s.Pours);
            buf.Int(s.Undos);
            buf.Int(s.VatPoursIn);
            buf.Int(s.VatDrawsOut);
            buf.Int(s.ApronStows);
            buf.Int(s.CatPets);
            buf.Int(s.Misfiles);
            buf.Int(s.SelectedHandIndex);
        }

        static void PutLists(Buf buf, State s)
        {
            buf.IntList(s.OwnedSkills);
            buf.IntList(s.SwitchedOffSkills);
            buf.IntList(s.ScoredShelves);
            buf.IntList(s.ScoredSections);
        }

        static byte[] BuildSnapshot(State s, Buf buf)
        {
            buf.Reset();
            PutScalars(buf, s);
            PutFlags(buf, s);
            PutCounters(buf, s);
            PutLists(buf, s);
            buf.Buckets(s.Buckets);
            return buf.ToArray();
        }

        static uint DiffMask(State s, State b)
        {
            uint mask = 0;
            if (s.GameStateId != b.GameStateId) mask |= BitGameState;
            if (s.Gold != b.Gold) mask |= BitGold;
            if (s.Diamonds != b.Diamonds) mask |= BitDiamonds;
            if (s.LabManifestHash != b.LabManifestHash) mask |= BitLabManifestHash;
            if (s.DeclinedManifestHash != b.DeclinedManifestHash) mask |= BitDeclinedManifestHash;
            if (s.PotionManifestHash != b.PotionManifestHash) mask |= BitPotionManifestHash;
            if (s.SlotManifestHash != b.SlotManifestHash) mask |= BitSlotManifestHash;
            if (s.GameStarted != b.GameStarted) mask |= BitGameStarted;
            if (s.LabCompleted != b.LabCompleted) mask |= BitLabCompleted;
            if (s.LabSummaryShown != b.LabSummaryShown) mask |= BitLabSummaryShown;
            if (s.Playtime != b.Playtime) mask |= BitPlaytime;
            if (s.Experience != b.Experience) mask |= BitExperience;
            if (s.OnboardingFlags != b.OnboardingFlags) mask |= BitOnboardingFlags;
            if (s.AchievementMoments != b.AchievementMoments) mask |= BitAchievementMoments;
            if (s.UnclaimedExperience != b.UnclaimedExperience) mask |= BitUnclaimedExp;
            if (s.UnannouncedSkillPoints != b.UnannouncedSkillPoints) mask |= BitUnannouncedSkillPts;
            if (s.PotionsFinished != b.PotionsFinished) mask |= BitPotionsFinished;
            if (s.PotionsShelvedHome != b.PotionsShelvedHome) mask |= BitPotionsShelvedHome;
            if (s.Pours != b.Pours) mask |= BitPours;
            if (s.Undos != b.Undos) mask |= BitUndos;
            if (s.VatPoursIn != b.VatPoursIn) mask |= BitVatPoursIn;
            if (s.VatDrawsOut != b.VatDrawsOut) mask |= BitVatDrawsOut;
            if (s.ApronStows != b.ApronStows) mask |= BitApronStows;
            if (s.CatPets != b.CatPets) mask |= BitCatPets;
            if (s.Misfiles != b.Misfiles) mask |= BitMisfiles;
            if (s.SelectedHandIndex != b.SelectedHandIndex) mask |= BitSelectedHand;
            if (!ListsEqual(s.OwnedSkills, b.OwnedSkills)) mask |= BitOwnedSkills;
            if (!ListsEqual(s.SwitchedOffSkills, b.SwitchedOffSkills)) mask |= BitSwitchedOffSkills;
            if (!ListsEqual(s.ScoredShelves, b.ScoredShelves)) mask |= BitScoredShelves;
            if (!ListsEqual(s.ScoredSections, b.ScoredSections)) mask |= BitScoredSections;
            if (BucketsChanged(s.Buckets, b.Buckets)) mask |= BitEntries;
            return mask;
        }

        static bool ListsEqual(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static bool BucketsChanged(List<Bucket> a, List<Bucket> b)
        {
            if (a.Count != b.Count) return true;
            for (int i = 0; i < a.Count; i++)
            {
                Bucket ba = a[i], bb = b[i];
                if (ba.Tag != bb.Tag || ba.Entries.Count != bb.Entries.Count) return true;
                for (int j = 0; j < ba.Entries.Count; j++)
                {
                    Entry ea = ba.Entries[j], eb = bb.Entries[j];
                    if (ea.Uid != eb.Uid || ea.Location != eb.Location || ea.SlotUid != eb.SlotUid
                        || ea.HandIndex != eb.HandIndex || ea.ScoredShelve != eb.ScoredShelve
                        || ea.Units.Count != eb.Units.Count) return true;
                    for (int k = 0; k < ea.Units.Count; k++) if (ea.Units[k] != eb.Units[k]) return true;
                    if (VecChanged(ea.Position, eb.Position)) return true;
                    if (QuatChanged(ea.Rotation, eb.Rotation)) return true;
                }
            }
            return false;
        }

        static bool VecChanged(Vector3 a, Vector3 b)
        {
            return (a - b).sqrMagnitude > PositionEpsilon * PositionEpsilon;
        }

        static bool QuatChanged(Quaternion a, Quaternion b)
        {
            float d = Quaternion.Dot(a, b);
            return 1f - Mathf.Abs(d) > 0.0005f;
        }

        static byte[] BuildDelta(State s, uint mask, Buf buf)
        {
            buf.Reset();
            buf.Int(WireVersion);
            buf.UInt(mask);
            if ((mask & BitGameState) != 0) buf.Int(s.GameStateId);
            if ((mask & BitGold) != 0) buf.Int(s.Gold);
            if ((mask & BitDiamonds) != 0) buf.Int(s.Diamonds);
            if ((mask & BitLabManifestHash) != 0) buf.Int(s.LabManifestHash);
            if ((mask & BitDeclinedManifestHash) != 0) buf.Int(s.DeclinedManifestHash);
            if ((mask & BitPotionManifestHash) != 0) buf.Int(s.PotionManifestHash);
            if ((mask & BitSlotManifestHash) != 0) buf.Int(s.SlotManifestHash);
            if ((mask & BitGameStarted) != 0) buf.Bool(s.GameStarted);
            if ((mask & BitLabCompleted) != 0) buf.Bool(s.LabCompleted);
            if ((mask & BitLabSummaryShown) != 0) buf.Bool(s.LabSummaryShown);
            if ((mask & BitPlaytime) != 0) buf.Int(s.Playtime);
            if ((mask & BitExperience) != 0) buf.Int(s.Experience);
            if ((mask & BitOnboardingFlags) != 0) buf.Int(s.OnboardingFlags);
            if ((mask & BitAchievementMoments) != 0) buf.Int(s.AchievementMoments);
            if ((mask & BitUnclaimedExp) != 0) buf.Int(s.UnclaimedExperience);
            if ((mask & BitUnannouncedSkillPts) != 0) buf.Int(s.UnannouncedSkillPoints);
            if ((mask & BitPotionsFinished) != 0) buf.Int(s.PotionsFinished);
            if ((mask & BitPotionsShelvedHome) != 0) buf.Int(s.PotionsShelvedHome);
            if ((mask & BitPours) != 0) buf.Int(s.Pours);
            if ((mask & BitUndos) != 0) buf.Int(s.Undos);
            if ((mask & BitVatPoursIn) != 0) buf.Int(s.VatPoursIn);
            if ((mask & BitVatDrawsOut) != 0) buf.Int(s.VatDrawsOut);
            if ((mask & BitApronStows) != 0) buf.Int(s.ApronStows);
            if ((mask & BitCatPets) != 0) buf.Int(s.CatPets);
            if ((mask & BitMisfiles) != 0) buf.Int(s.Misfiles);
            if ((mask & BitSelectedHand) != 0) buf.Int(s.SelectedHandIndex);
            if ((mask & BitOwnedSkills) != 0) buf.IntList(s.OwnedSkills);
            if ((mask & BitSwitchedOffSkills) != 0) buf.IntList(s.SwitchedOffSkills);
            if ((mask & BitScoredShelves) != 0) buf.IntList(s.ScoredShelves);
            if ((mask & BitScoredSections) != 0) buf.IntList(s.ScoredSections);
            if ((mask & BitEntries) != 0) buf.Buckets(s.Buckets);
            return buf.ToArray();
        }

        static void CaptureBaseline(State s)
        {
            baseline.GameStateId = s.GameStateId;
            baseline.Gold = s.Gold;
            baseline.Diamonds = s.Diamonds;
            baseline.LabManifestHash = s.LabManifestHash;
            baseline.DeclinedManifestHash = s.DeclinedManifestHash;
            baseline.PotionManifestHash = s.PotionManifestHash;
            baseline.SlotManifestHash = s.SlotManifestHash;
            baseline.GameStarted = s.GameStarted;
            baseline.LabCompleted = s.LabCompleted;
            baseline.LabSummaryShown = s.LabSummaryShown;
            baseline.Playtime = s.Playtime;
            baseline.Experience = s.Experience;
            baseline.OnboardingFlags = s.OnboardingFlags;
            baseline.AchievementMoments = s.AchievementMoments;
            baseline.UnclaimedExperience = s.UnclaimedExperience;
            baseline.UnannouncedSkillPoints = s.UnannouncedSkillPoints;
            baseline.PotionsFinished = s.PotionsFinished;
            baseline.PotionsShelvedHome = s.PotionsShelvedHome;
            baseline.Pours = s.Pours;
            baseline.Undos = s.Undos;
            baseline.VatPoursIn = s.VatPoursIn;
            baseline.VatDrawsOut = s.VatDrawsOut;
            baseline.ApronStows = s.ApronStows;
            baseline.CatPets = s.CatPets;
            baseline.Misfiles = s.Misfiles;
            baseline.SelectedHandIndex = s.SelectedHandIndex;
            baseline.OwnedSkills.Clear();
            baseline.OwnedSkills.AddRange(s.OwnedSkills);
            baseline.SwitchedOffSkills.Clear();
            baseline.SwitchedOffSkills.AddRange(s.SwitchedOffSkills);
            baseline.ScoredShelves.Clear();
            baseline.ScoredShelves.AddRange(s.ScoredShelves);
            baseline.ScoredSections.Clear();
            baseline.ScoredSections.AddRange(s.ScoredSections);
            baseline.Buckets.Clear();
            for (int i = 0; i < s.Buckets.Count; i++)
            {
                Bucket src = s.Buckets[i];
                Bucket dst = new Bucket { Tag = src.Tag };
                for (int j = 0; j < src.Entries.Count; j++)
                {
                    Entry e = src.Entries[j];
                    Entry copy = new Entry
                    {
                        Uid = e.Uid,
                        Location = e.Location,
                        Position = e.Position,
                        Rotation = e.Rotation,
                        SlotUid = e.SlotUid,
                        HandIndex = e.HandIndex,
                        ScoredShelve = e.ScoredShelve,
                    };
                    copy.Units.AddRange(e.Units);
                    dst.Entries.Add(copy);
                }
                baseline.Buckets.Add(dst);
            }
            haveBaseline = true;
        }

        // ------------------------------------------------------------------
        // client application
        // ------------------------------------------------------------------
        static void ApplySnapshot(uint from, byte[] payload)
        {
            if (payload is null || payload.Length < 8) return;
            int off = 0;
            int version = ReadIntAt(payload, ref off);
            if (version != WireVersion)
            {
                MelonLogger.Warning($"[apfep] snapshot v{version} from peer {from} not understood (expected {WireVersion}) — ignored");
                return;
            }
            State s = new State();
            s.GameStateId = ReadIntAt(payload, ref off);
            s.Gold = ReadIntAt(payload, ref off);
            s.Diamonds = ReadIntAt(payload, ref off);
            s.LabManifestHash = ReadIntAt(payload, ref off);
            s.DeclinedManifestHash = ReadIntAt(payload, ref off);
            s.PotionManifestHash = ReadIntAt(payload, ref off);
            s.SlotManifestHash = ReadIntAt(payload, ref off);
            s.GameStarted = ReadByteAt(payload, ref off) != 0;
            s.LabCompleted = ReadByteAt(payload, ref off) != 0;
            s.LabSummaryShown = ReadByteAt(payload, ref off) != 0;
            s.Playtime = ReadIntAt(payload, ref off);
            s.Experience = ReadIntAt(payload, ref off);
            s.OnboardingFlags = ReadIntAt(payload, ref off);
            s.AchievementMoments = ReadIntAt(payload, ref off);
            s.UnclaimedExperience = ReadIntAt(payload, ref off);
            s.UnannouncedSkillPoints = ReadIntAt(payload, ref off);
            s.PotionsFinished = ReadIntAt(payload, ref off);
            s.PotionsShelvedHome = ReadIntAt(payload, ref off);
            s.Pours = ReadIntAt(payload, ref off);
            s.Undos = ReadIntAt(payload, ref off);
            s.VatPoursIn = ReadIntAt(payload, ref off);
            s.VatDrawsOut = ReadIntAt(payload, ref off);
            s.ApronStows = ReadIntAt(payload, ref off);
            s.CatPets = ReadIntAt(payload, ref off);
            s.Misfiles = ReadIntAt(payload, ref off);
            s.SelectedHandIndex = ReadIntAt(payload, ref off);
            ReadIntListAt(payload, ref off, s.OwnedSkills);
            ReadIntListAt(payload, ref off, s.SwitchedOffSkills);
            ReadIntListAt(payload, ref off, s.ScoredShelves);
            ReadIntListAt(payload, ref off, s.ScoredSections);
            ReadBucketsAt(payload, ref off, s.Buckets);

            MergeIntoCurrent(s, true);
            ApplyToGameObjects(s, AllFieldsMask, true);

            ITransport transport = CoopNet.Transport;
            if (transport != null && lastReadySent != WireVersion)
            {
                Wire.PutUInt(readyPayload, 0, CoopNet.LocalPeerId);
                Wire.PutInt(readyPayload, 4, WireVersion);
                transport.Send(from, MsgKind.Ready, readyPayload);
                lastReadySent = WireVersion;
            }
            puppetBootDone = true;
        }

        static void ApplyDelta(uint from, byte[] payload)
        {
            if (payload is null || payload.Length < 8) return;
            int off = 0;
            int version = ReadIntAt(payload, ref off);
            if (version != WireVersion)
            {
                MelonLogger.Warning($"[apfep] delta v{version} from peer {from} not understood — ignored");
                return;
            }
            uint mask = Wire.GetUInt(payload, off);
            off += 4;

            State s = new State();
            // seed with what we already know so partial deltas don't zero unrelated fields
            s.GameStateId = current.GameStateId;
            s.Gold = current.Gold;
            s.Diamonds = current.Diamonds;
            s.LabManifestHash = current.LabManifestHash;
            s.DeclinedManifestHash = current.DeclinedManifestHash;
            s.PotionManifestHash = current.PotionManifestHash;
            s.SlotManifestHash = current.SlotManifestHash;
            s.GameStarted = current.GameStarted;
            s.LabCompleted = current.LabCompleted;
            s.LabSummaryShown = current.LabSummaryShown;
            s.Playtime = current.Playtime;
            s.Experience = current.Experience;
            s.OnboardingFlags = current.OnboardingFlags;
            s.AchievementMoments = current.AchievementMoments;
            s.UnclaimedExperience = current.UnclaimedExperience;
            s.UnannouncedSkillPoints = current.UnannouncedSkillPoints;
            s.PotionsFinished = current.PotionsFinished;
            s.PotionsShelvedHome = current.PotionsShelvedHome;
            s.Pours = current.Pours;
            s.Undos = current.Undos;
            s.VatPoursIn = current.VatPoursIn;
            s.VatDrawsOut = current.VatDrawsOut;
            s.ApronStows = current.ApronStows;
            s.CatPets = current.CatPets;
            s.Misfiles = current.Misfiles;
            s.SelectedHandIndex = current.SelectedHandIndex;
            s.OwnedSkills.AddRange(current.OwnedSkills);
            s.SwitchedOffSkills.AddRange(current.SwitchedOffSkills);
            s.ScoredShelves.AddRange(current.ScoredShelves);
            s.ScoredSections.AddRange(current.ScoredSections);

            if ((mask & BitGameState) != 0) s.GameStateId = ReadIntAt(payload, ref off);
            if ((mask & BitGold) != 0) s.Gold = ReadIntAt(payload, ref off);
            if ((mask & BitDiamonds) != 0) s.Diamonds = ReadIntAt(payload, ref off);
            if ((mask & BitLabManifestHash) != 0) s.LabManifestHash = ReadIntAt(payload, ref off);
            if ((mask & BitDeclinedManifestHash) != 0) s.DeclinedManifestHash = ReadIntAt(payload, ref off);
            if ((mask & BitPotionManifestHash) != 0) s.PotionManifestHash = ReadIntAt(payload, ref off);
            if ((mask & BitSlotManifestHash) != 0) s.SlotManifestHash = ReadIntAt(payload, ref off);
            if ((mask & BitGameStarted) != 0) s.GameStarted = ReadByteAt(payload, ref off) != 0;
            if ((mask & BitLabCompleted) != 0) s.LabCompleted = ReadByteAt(payload, ref off) != 0;
            if ((mask & BitLabSummaryShown) != 0) s.LabSummaryShown = ReadByteAt(payload, ref off) != 0;
            if ((mask & BitPlaytime) != 0) s.Playtime = ReadIntAt(payload, ref off);
            if ((mask & BitExperience) != 0) s.Experience = ReadIntAt(payload, ref off);
            if ((mask & BitOnboardingFlags) != 0) s.OnboardingFlags = ReadIntAt(payload, ref off);
            if ((mask & BitAchievementMoments) != 0) s.AchievementMoments = ReadIntAt(payload, ref off);
            if ((mask & BitUnclaimedExp) != 0) s.UnclaimedExperience = ReadIntAt(payload, ref off);
            if ((mask & BitUnannouncedSkillPts) != 0) s.UnannouncedSkillPoints = ReadIntAt(payload, ref off);
            if ((mask & BitPotionsFinished) != 0) s.PotionsFinished = ReadIntAt(payload, ref off);
            if ((mask & BitPotionsShelvedHome) != 0) s.PotionsShelvedHome = ReadIntAt(payload, ref off);
            if ((mask & BitPours) != 0) s.Pours = ReadIntAt(payload, ref off);
            if ((mask & BitUndos) != 0) s.Undos = ReadIntAt(payload, ref off);
            if ((mask & BitVatPoursIn) != 0) s.VatPoursIn = ReadIntAt(payload, ref off);
            if ((mask & BitVatDrawsOut) != 0) s.VatDrawsOut = ReadIntAt(payload, ref off);
            if ((mask & BitApronStows) != 0) s.ApronStows = ReadIntAt(payload, ref off);
            if ((mask & BitCatPets) != 0) s.CatPets = ReadIntAt(payload, ref off);
            if ((mask & BitMisfiles) != 0) s.Misfiles = ReadIntAt(payload, ref off);
            if ((mask & BitSelectedHand) != 0) s.SelectedHandIndex = ReadIntAt(payload, ref off);
            if ((mask & BitOwnedSkills) != 0) ReadIntListAt(payload, ref off, s.OwnedSkills);
            if ((mask & BitSwitchedOffSkills) != 0) ReadIntListAt(payload, ref off, s.SwitchedOffSkills);
            if ((mask & BitScoredShelves) != 0) ReadIntListAt(payload, ref off, s.ScoredShelves);
            if ((mask & BitScoredSections) != 0) ReadIntListAt(payload, ref off, s.ScoredSections);
            if ((mask & BitEntries) != 0) ReadBucketsAt(payload, ref off, s.Buckets);

            MergeIntoCurrent(s, (mask & BitEntries) != 0);
            ApplyToGameObjects(s, mask, false);
        }

        static void MergeIntoCurrent(State s, bool replaceEntries)
        {
            current.GameStateId = s.GameStateId;
            HostGameStateId = s.GameStateId;   // snapshot is authoritative
            current.Gold = s.Gold;
            current.Diamonds = s.Diamonds;
            Gold = s.Gold;
            Diamonds = s.Diamonds;
            current.LabManifestHash = s.LabManifestHash;
            current.DeclinedManifestHash = s.DeclinedManifestHash;
            current.PotionManifestHash = s.PotionManifestHash;
            current.SlotManifestHash = s.SlotManifestHash;
            current.GameStarted = s.GameStarted;
            current.LabCompleted = s.LabCompleted;
            current.LabSummaryShown = s.LabSummaryShown;
            current.Playtime = s.Playtime;
            current.Experience = s.Experience;
            current.OnboardingFlags = s.OnboardingFlags;
            current.AchievementMoments = s.AchievementMoments;
            current.UnclaimedExperience = s.UnclaimedExperience;
            current.UnannouncedSkillPoints = s.UnannouncedSkillPoints;
            current.PotionsFinished = s.PotionsFinished;
            current.PotionsShelvedHome = s.PotionsShelvedHome;
            current.Pours = s.Pours;
            current.Undos = s.Undos;
            current.VatPoursIn = s.VatPoursIn;
            current.VatDrawsOut = s.VatDrawsOut;
            current.ApronStows = s.ApronStows;
            current.CatPets = s.CatPets;
            current.Misfiles = s.Misfiles;
            current.SelectedHandIndex = s.SelectedHandIndex;
            current.OwnedSkills.Clear();
            current.OwnedSkills.AddRange(s.OwnedSkills);
            current.SwitchedOffSkills.Clear();
            current.SwitchedOffSkills.AddRange(s.SwitchedOffSkills);
            current.ScoredShelves.Clear();
            current.ScoredShelves.AddRange(s.ScoredShelves);
            current.ScoredSections.Clear();
            current.ScoredSections.AddRange(s.ScoredSections);
            if (replaceEntries)
            {
                current.Buckets.Clear();
                for (int i = 0; i < s.Buckets.Count; i++)
                {
                    Bucket src = s.Buckets[i];
                    Bucket dst = new Bucket { Tag = src.Tag };
                    for (int j = 0; j < src.Entries.Count; j++) dst.Entries.Add(src.Entries[j]);
                    current.Buckets.Add(dst);
                }
            }
            if (s.GameStateId >= 0) HostGameStateId = s.GameStateId;
            Gold = s.Gold;
            Diamonds = s.Diamonds;
        }

        const uint AllFieldsMask = 0xFFFFFFFFu;

        static void ApplyToGameObjects(State s, uint mask, bool full)
        {
            EnsureServices();
            var p = progress;
            if (p is null) return;

            try
            {
                if ((mask & BitGold) != 0) WriteInt(p.Currency?.Get(Il2Cpp.eCurrency.Gold), s.Gold);
                if ((mask & BitDiamonds) != 0) WriteInt(p.Currency?.Get(Il2Cpp.eCurrency.Diamonds), s.Diamonds);
                if ((mask & BitLabManifestHash) != 0) p.LabManifestHash = s.LabManifestHash;
                if ((mask & BitDeclinedManifestHash) != 0) p.DeclinedManifestHash = s.DeclinedManifestHash;
                if ((mask & BitGameStarted) != 0) p.GameStarted = s.GameStarted;
                if ((mask & BitLabCompleted) != 0) p.LabCompleted = s.LabCompleted;
                if ((mask & BitLabSummaryShown) != 0) p.LabSummaryShown = s.LabSummaryShown;
                if ((mask & BitSelectedHand) != 0) p.SelectedHandIndex = s.SelectedHandIndex;
                if ((mask & BitMisfiles) != 0) p.Misfiles = s.Misfiles;
                if ((mask & BitPlaytime) != 0) WriteInt(p.TotalPlaytimeSeconds, s.Playtime);
                if ((mask & BitExperience) != 0) WriteInt(p.ExperiencePoints, s.Experience);
                if ((mask & BitOnboardingFlags) != 0) WriteInt(p.OnboardingFlags, s.OnboardingFlags);
                if ((mask & BitAchievementMoments) != 0) WriteInt(p.AchievementMoments, s.AchievementMoments);
                if ((mask & BitPotionsFinished) != 0) WriteInt(p.PotionsFinished, s.PotionsFinished);
                if ((mask & BitPotionsShelvedHome) != 0) WriteInt(p.PotionsShelvedHome, s.PotionsShelvedHome);
                if ((mask & BitPours) != 0) WriteInt(p.Pours, s.Pours);
                if ((mask & BitUndos) != 0) WriteInt(p.Undos, s.Undos);
                if ((mask & BitVatPoursIn) != 0) WriteInt(p.VatPoursIn, s.VatPoursIn);
                if ((mask & BitVatDrawsOut) != 0) WriteInt(p.VatDrawsOut, s.VatDrawsOut);
                if ((mask & BitApronStows) != 0) WriteInt(p.ApronStows, s.ApronStows);
                if ((mask & BitCatPets) != 0) WriteInt(p.CatPets, s.CatPets);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[apfep] apply progress failed: " + ex.Message);
            }

            var rt = runtimePlayer;
            if (rt != null)
            {
                try
                {
                    if ((mask & BitUnclaimedExp) != 0) WriteInt(rt.UnclaimedExperience, s.UnclaimedExperience);
                    if ((mask & BitUnannouncedSkillPts) != 0) WriteInt(rt.UnannouncedSkillPoints, s.UnannouncedSkillPoints);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("[apfep] apply runtime data failed: " + ex.Message);
                }
            }

            if ((mask & BitOwnedSkills) != 0) ApplySkillList(p.OwnedSkills, s.OwnedSkills);
            if ((mask & BitSwitchedOffSkills) != 0) ApplySkillList(p.SwitchedOffSkills, s.SwitchedOffSkills);
            if ((mask & BitScoredShelves) != 0) ApplyIntList(p.ScoredShelves, s.ScoredShelves);
            if ((mask & BitScoredSections) != 0) ApplyIntList(p.ScoredSections, s.ScoredSections);

            if ((mask & BitEntries) != 0 || full) ApplyEntries(s);
        }

        static void ApplySkillList(Il2CppCodeBase.Common.Utilities.ReactiveList<Il2Cpp.eSkill> list, List<int> values)
        {
            if (list is null) return;
            try
            {
                list.Clear();
                for (int i = 0; i < values.Count; i++) list.Add((Il2Cpp.eSkill)values[i]);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[apfep] apply skill list failed: " + ex.Message);
            }
        }

        static void ApplyIntList(Il2CppCodeBase.Common.Utilities.ReactiveList<int> list, List<int> values)
        {
            if (list is null) return;
            try
            {
                list.Clear();
                for (int i = 0; i < values.Count; i++) list.Add(values[i]);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[apfep] apply int list failed: " + ex.Message);
            }
        }

        static void ApplyEntries(State s)
        {
            if (s.Buckets.Count == 0) return;
            if (potionsPersistenceImpl is null && persistentData is null) return;
            try
            {
                for (int i = 0; i < s.Buckets.Count; i++)
                {
                    Bucket b = s.Buckets[i];
                    Il2CppCodeBase.Services.PersistentDataService.PotionsPersistentData data;
                    try { data = ProbeBucket(b.Tag); }
                    catch (Exception) { continue; }
                    if (data is null) continue;

                    data.ClearEntries();
                    for (int j = 0; j < b.Entries.Count; j++)
                    {
                        Entry e = b.Entries[j];
                        Il2CppCodeBase.Services.PersistentDataService.PotionPersistentEntry entry = data.GetOrCreateEntry(e.Uid);
                        if (entry is null) continue;
                        entry.Location = (Il2Cpp.ePotionLocation)e.Location;
                        entry.Position = e.Position;
                        entry.Rotation = e.Rotation;
                        entry.SlotUid = e.SlotUid;
                        entry.HandIndex = e.HandIndex;
                        Il2CppStructArray<Il2Cpp.eSectionFamily> units = new Il2CppStructArray<Il2Cpp.eSectionFamily>(e.Units.Count);
                        for (int k = 0; k < e.Units.Count; k++) units[k] = (Il2Cpp.eSectionFamily)e.Units[k];
                        entry.Units = units;
                        entry.ScoredShelve = e.ScoredShelve;
                    }
                    data.StampHashes(s.PotionManifestHash, s.SlotManifestHash);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[apfep] apply potion entries failed: " + ex.Message);
            }
        }

        static int ReadIntAt(byte[] raw, ref int off)
        {
            if (raw == null || off + 4 > raw.Length) return 0;
            int v = Wire.GetInt(raw, off);
            off += 4;
            return v;
        }

        static byte ReadByteAt(byte[] raw, ref int off)
        {
            if (raw == null || off + 1 > raw.Length) return 0;
            byte v = raw[off];
            off += 1;
            return v;
        }

        static void ReadIntListAt(byte[] raw, ref int off, List<int> into)
        {
            into.Clear();
            int count = ReadIntAt(raw, ref off);
            if (count < 0) count = 0;
            for (int i = 0; i < count; i++)
            {
                if (off + 4 > raw.Length) break;
                into.Add(ReadIntAt(raw, ref off));
            }
        }

        static void ReadBucketsAt(byte[] raw, ref int off, List<Bucket> into)
        {
            into.Clear();
            int bucketCount = ReadIntAt(raw, ref off);
            for (int i = 0; i < bucketCount; i++)
            {
                Bucket b = new Bucket { Tag = ReadIntAt(raw, ref off) };
                int entryCount = ReadIntAt(raw, ref off);
                for (int j = 0; j < entryCount; j++)
                {
                    if (off + 43 > raw.Length) break;   // uid+location+pos+rot+slot+hand+unitCount+scored
                    Entry e = new Entry
                    {
                        Uid = ReadIntAt(raw, ref off),
                        Location = ReadByteAt(raw, ref off),
                        Position = Wire.DecV3(raw, ref off),
                        Rotation = Wire.DecQ(raw, ref off),
                        SlotUid = ReadIntAt(raw, ref off),
                        HandIndex = ReadIntAt(raw, ref off),
                    };
                    int unitCount = ReadByteAt(raw, ref off);
                    for (int k = 0; k < unitCount; k++) e.Units.Add(ReadByteAt(raw, ref off));
                    e.ScoredShelve = ReadByteAt(raw, ref off) != 0;
                    b.Entries.Add(e);
                }
                into.Add(b);
            }
        }

        static string StateName(int id)
        {
            try { return ((Il2Cpp.eGameState)id).ToString(); }
            catch (Exception) { return "state " + id; }
        }

        // ------------------------------------------------------------------
        // Harmony patches
        // ------------------------------------------------------------------
        static int TryPatch(MethodInfo target, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            if (target is null)
            {
                MelonLogger.Warning("[apfep] sync patch target not found");
                return 0;
            }
            try
            {
                harmonyInstance?.Patch(target, prefix, postfix);
                return harmonyInstance is null ? 0 : 1;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[apfep] sync patch {target.DeclaringType?.Name}.{target.Name} failed: " + ex.Message);
                return 0;
            }
        }

        // Puppets sit idle in their own lobby until the host drives a gameplay state; they
        // initiate the transition themselves so the host's choice becomes the local one.
        static long lastFollowMs;

        static bool followDebugLogged;

        static void FollowHostState()
        {
            if (!puppetBootDone || stateMachine is null) return;
            int hostId = HostGameStateId;
            if (hostId < 0) return;

            if (!followDebugLogged)
            {
                followDebugLogged = true;
                MelonLogger.Msg($"[apfep] follow check: local={StateName(localStateId)} host={StateName(hostId)} machineImpl={(stateMachineImpl is null ? "none" : "ok")}");
            }

            int local = localStateId;
            if (local < 0 && stateMachineImpl != null)
            {
                try { local = (int)stateMachineImpl._currentState; }
                catch (Exception) { local = -1; }
            }
            if (local < 0 || local == hostId) return;

            // save-start flows and terminal states are local-only; they need a payload/scene the
            // puppet cannot synthesize from a bare id
            if (hostId == (int)Il2Cpp.eGameState.NewGame || hostId == (int)Il2Cpp.eGameState.ContinueGame
                || hostId == (int)Il2Cpp.eGameState.DemoCompleted || hostId == (int)Il2Cpp.eGameState.None)
            {
                return;
            }

            // A puppet sitting in the lobby cannot enter Playing directly: the level scene has
            // to load first, so a host-side Playing becomes a local LoadLevel.
            Il2Cpp.eGameState target = (Il2Cpp.eGameState)hostId;
            if (target == Il2Cpp.eGameState.Playing
                && (local == (int)Il2Cpp.eGameState.Lobby || local == (int)Il2Cpp.eGameState.LoadLobby))
            {
                target = Il2Cpp.eGameState.LoadLevel;
            }
            if ((int)target == local) return;

            long now = Environment.TickCount64;
            // level loads are urgent (host may pass through LoadLevel in under a second);
            // other transitions are debounced so transient states don't thrash
            if (target != Il2Cpp.eGameState.LoadLevel && now - lastFollowMs < 1000) return;
            lastFollowMs = now;

            try
            {
                MelonLogger.Msg($"[apfep] puppet follows host state {StateName(hostId)} -> local {StateName((int)target)} (was {StateName(local)})");
                stateMachine.Enter(target);
            }
            catch (Exception ex) { MelonLogger.Warning("[apfep] follow host state failed: " + ex.Message); }
        }

        // Shared remap rule: puppets adopt the host's state. The host's Playing state cannot be
        // entered from a puppet's lobby directly - the level scene has to load first, so a lobby
        // transition becomes LoadLevel and LoadLevelState reaches Playing on its own. Host-side
        // save-start states stay local on the puppet.
        static void RemapPuppetState(ref Il2Cpp.eGameState id)
        {
            if (!LocalIsPuppet) return;
            int hostId = HostGameStateId;
            if (hostId < 0) return; // host state unknown: puppet runs its own boot
            if ((int)id == hostId) return;
            if ((int)id == (int)Il2Cpp.eGameState.LoadLevel && hostId == (int)Il2Cpp.eGameState.Playing)
                return; // an explicit level load is the puppet's step toward Playing
            if (hostId == (int)Il2Cpp.eGameState.Playing
                && ((int)id == (int)Il2Cpp.eGameState.Lobby || (int)id == (int)Il2Cpp.eGameState.LoadLobby
                    || (int)id == (int)Il2Cpp.eGameState.NewGame || (int)id == (int)Il2Cpp.eGameState.ContinueGame))
            {
                id = Il2Cpp.eGameState.LoadLevel;
                return;
            }
            if (hostId == (int)Il2Cpp.eGameState.NewGame || hostId == (int)Il2Cpp.eGameState.ContinueGame
                || hostId == (int)Il2Cpp.eGameState.DemoCompleted || hostId == (int)Il2Cpp.eGameState.None)
            {
                return;
            }
            id = (Il2Cpp.eGameState)hostId;
        }

        static bool EnterPrefix(ref Il2Cpp.eGameState id)
        {
            RemapPuppetState(ref id);
            return true;
        }

        static void EnterPostfix(Il2Cpp.eGameState id)
        {
            OnHostStateEntered((int)id, id);
        }

        static void AssignPostfix(Il2Cpp.eGameState id)
        {
            OnHostStateEntered((int)id, id);
        }

        // The state this instance actually executed; HostGameStateId is the authoritative
        // (host) value and must never be overwritten by a puppet's own transitions.
        static int localStateId = -1;
        static int lastBroadcastState = -1;

        static void OnHostStateEntered(int id, Il2Cpp.eGameState state)
        {
            ITransport transport = CoopNet.Transport;
            if (transport is null) return;
            if (localStateId < 0)
                MelonLogger.Msg($"[apfep] first Enter observed: {StateName(id)} (host={transport.IsHost})");
            localStateId = id;
            current.GameStateId = id;
            if (transport.IsHost) HostGameStateId = id;
            if (!transport.IsHost) return;
            // Enter payload + AssignNewState postfix fire for one transition; broadcast once
            if (id == lastBroadcastState) return;
            lastBroadcastState = id;

            try
            {
                Wire.PutInt(stateChangePayload, 0, id);
                transport.Broadcast(MsgKind.StateChange, stateChangePayload);
                if (id == (int)Il2Cpp.eGameState.LoadLevel || id == (int)Il2Cpp.eGameState.NewGame
                    || id == (int)Il2Cpp.eGameState.ContinueGame)
                {
                    snapshotDirty = true;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[apfep] StateChange broadcast failed: " + ex.Message);
            }
        }

        // Clients must not persist saves: return false to skip the original body.
        static bool ClientOnlySuppressPrefix() => !LocalIsPuppet;

        static bool GetMoveInputPrefix(ref UnityEngine.Vector2 __result)
        {
            if (LocalIsPuppet)
            {
                __result = Vector2.zero;
                return false;
            }
            return true;
        }

        static void LabPopulatedPostfix()
        {
            ITransport transport = CoopNet.Transport;
            if (transport is null || !transport.IsHost) return;
            snapshotDirty = true;
        }

        static void ManifestChangedPostfix()
        {
            ITransport transport = CoopNet.Transport;
            if (transport is null || !transport.IsHost) return;
            snapshotDirty = true;
        }
    }
}
