using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using ApfepCoop.Net;
using ApfepCoop.Sync;

// =====================================================================================
// Slice 3 — puppet avatars for remote peers.
//
// Outbound: Harmony postfix on HeroMover.Update (every peer, host included) broadcasts
// MsgKind.PuppetMove at ~10 Hz via CoopNet.Transport.Broadcast. Payload (contract wire):
//   [peer:uint32][pos:Vector3 3*float32][quat:Quaternion 4*float32][int32 state]
//   36 bytes total; always sent, even when the hero stands still, so peers keep the
//   freshness stamp alive and can prune stale avatars.
//
// Inbound: SyncBrain.Tick() is the only PumpReceived drainer; kinds it does not consume
// arrive on SyncBrain.NonSyncMessageHandler, which Install wires to OnNonSyncMessage.
// This slice never calls PumpReceived.
//
// Avatars: mod-built GameObject.CreatePrimitive(PrimitiveType.Capsule) under one root,
// NOT clones of the game hero. One per remote peer, named "apfep-puppet-<peerId>", hue
// palette derived from the peer id, collider forced to a trigger (isTrigger=true) so it
// never collides with gameplay. Position/rotation smoothed toward the latest received
// pose each frame. Avatars are destroyed when the peer leaves the roster, when its last
// PuppetMove is older than STALE_SECONDS, when local game state is not Playing/Paused,
// and when the transport shuts down.
// =====================================================================================
namespace ApfepCoop.Puppet
{
    public sealed class PuppetBrain
    {
        public static bool IsInstalled;

        const float SendInterval = 0.1f;      // ~10 Hz broadcast
        const float StaleSeconds = 3.0f;      // prune avatars with no fresh PuppetMove
        const float SmoothRate = 8f;          // pose convergence rate (per second)
        const float AvatarHeight = 2f;        // capsule height; center sits +1 above reported feet pos

        sealed class PoseRecord
        {
            public Vector3 Pos;
            public Quaternion Quat;
            public int State;
            public float ReceivedAt;
        }

        sealed class AvatarState
        {
            public GameObject Go;
            public Quaternion Rot;
        }

        static readonly Dictionary<uint, PoseRecord> poses = new Dictionary<uint, PoseRecord>();
        static readonly Dictionary<uint, AvatarState> avatars = new Dictionary<uint, AvatarState>();
        static GameObject root;
        static float lastSendAt = -1f;

        // Reused outbound payload (36 bytes); Wire.Build copies it, never aliases it.
        static readonly byte[] movePayload = new byte[36];

        public static int AvatarCount => avatars.Count;
        public static int ReceivedMoves; // PuppetMove messages stored since install (QA/UI telemetry)
        public static int SentMoves;     // PuppetMove messages broadcast since install (QA/UI telemetry)

        public static bool Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                var heroUpdate = AccessTools.Method(typeof(Il2CppCodeBase.Components.Hero.HeroMover), "Update");
                if (heroUpdate is null)
                {
                    MelonLogger.Warning("[apfep] puppet: HeroMover.Update not found — no puppet broadcast");
                    return false;
                }
                harmony.Patch(heroUpdate, postfix: new HarmonyMethod(typeof(PuppetBrain), nameof(HeroMoverPostfix)));
                // SyncBrain forwards every non-sync kind here; += lets other slices hook too.
                SyncBrain.NonSyncMessageHandler += OnNonSyncMessage;
                IsInstalled = true;
                MelonLogger.Msg("[apfep] puppet subsystem installed (HeroMover.Update postfix)");
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[apfep] puppet install failed: " + e.Message);
                IsInstalled = false;
                return false;
            }
        }

        public static void Tick()
        {
            try
            {
                ManageAvatars();
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[apfep] puppet tick failed: " + e.Message);
            }
        }

        // ---- outbound -------------------------------------------------------------

        // Postfix on HeroMover.Update: runs on the Unity main thread whenever the hero
        // updates (lab gameplay). Send at ~10 Hz from every peer, idle included.
        static void HeroMoverPostfix(Il2CppCodeBase.Components.Hero.HeroMover __instance)
        {
            try
            {
                ITransport transport = CoopNet.Transport;
                if (transport is null || transport.Peers is null || transport.Peers.Count == 0) return;

                float now = Time.realtimeSinceStartup;
                if (lastSendAt >= 0f && now - lastSendAt < SendInterval) return;
                lastSendAt = now;

                // Verified interop pattern: Component.gameObject/Transform are exposed on
                // Il2Cpp wrappers (NativeMethodInfoPtr_get_gameObject/get_transform present).
                Transform heroT = __instance.transform;
                Vector3 pos = __instance.gameObject.transform.position;
                Quaternion rot = heroT.rotation;

                int off = 0;
                Wire.PutUInt(movePayload, 0, CoopNet.LocalPeerId);
                off = 4;
                Wire.PutFloat(movePayload, off, pos.x); off += 4;
                Wire.PutFloat(movePayload, off, pos.y); off += 4;
                Wire.PutFloat(movePayload, off, pos.z); off += 4;
                Wire.PutFloat(movePayload, off, rot.x); off += 4;
                Wire.PutFloat(movePayload, off, rot.y); off += 4;
                Wire.PutFloat(movePayload, off, rot.z); off += 4;
                Wire.PutFloat(movePayload, off, rot.w); off += 4;
                Wire.PutInt(movePayload, off, SyncBrain.HostGameStateId);

                transport.Broadcast(MsgKind.PuppetMove, movePayload);
                SentMoves++;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[apfep] puppet send failed: " + e.Message);
            }
        }

        // ---- inbound --------------------------------------------------------------

        // Fed by SyncBrain.Tick()'s single PumpReceived drain for kinds sync does not own.
        static void OnNonSyncMessage(uint from, MsgKind kind, byte[] payload)
        {
            try
            {
                if (kind != MsgKind.PuppetMove) return; // nothing else to consume
                if (payload is null || payload.Length < 36) return;
                uint peer = Wire.GetUInt(payload, 0);
                if (peer == CoopNet.LocalPeerId || peer == 0) return; // ignore self

                int off = 4;
                PoseRecord rec = new PoseRecord();
                rec.Pos = Wire.DecV3(payload, ref off);
                rec.Quat = Wire.DecQ(payload, ref off);
                rec.State = Wire.GetInt(payload, off);
                rec.ReceivedAt = Time.realtimeSinceStartup;
                poses[peer] = rec;
                ReceivedMoves++;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[apfep] puppet receive failed: " + e.Message);
            }
        }

        // ---- avatar lifecycle -----------------------------------------------------

        static void ManageAvatars()
        {
            ITransport transport = CoopNet.Transport;
            if (transport is null)
            {
                DestroyAll();
                return;
            }

            int gs = SyncBrain.HostGameStateId;
            bool active = gs == (int)Il2Cpp.eGameState.Playing || gs == (int)Il2Cpp.eGameState.Paused;

            float now = Time.realtimeSinceStartup;
            float dt = Time.deltaTime;
            float smoothT = Mathf.Clamp01(dt * SmoothRate);

            // Current remote roster (everything that isn't us).
            var remote = new HashSet<uint>();
            var peers = transport.Peers;
            for (int i = 0; i < peers.Count; i++)
            {
                uint id = peers[i].PeerId;
                if (id != CoopNet.LocalPeerId && id != 0) remote.Add(id);
            }

            // Drop pose records for peers who left or went stale (bounds memory).
            if (poses.Count > 0)
            {
                var dead = new List<uint>();
                foreach (var kv in poses)
                {
                    if (!remote.Contains(kv.Key) || now - kv.Value.ReceivedAt > StaleSeconds * 2f) dead.Add(kv.Key);
                }
                for (int i = 0; i < dead.Count; i++) poses.Remove(dead[i]);
            }

            if (!active)
            {
                DestroyAll();
                return;
            }

            // Create/refresh avatars for fresh remote poses.
            foreach (var kv in poses)
            {
                uint peer = kv.Key;
                PoseRecord rec = kv.Value;
                if (!remote.Contains(peer)) continue;
                if (now - rec.ReceivedAt > StaleSeconds) continue;

                AvatarState av;
                if (!avatars.TryGetValue(peer, out av))
                {
                    av = new AvatarState();
                    av.Go = CreateAvatar(peer);
                    if (av.Go is null) continue; // primitive creation failed
                    av.Rot = rec.Quat;
                    avatars[peer] = av;
                }

                Transform t = av.Go.transform;
                Vector3 target = new Vector3(rec.Pos.x, rec.Pos.y + AvatarHeight * 0.5f, rec.Pos.z);
                t.position = Vector3.Lerp(t.position, target, smoothT);
                av.Rot = Quaternion.Slerp(av.Rot, rec.Quat, smoothT);
                t.rotation = av.Rot;
            }

            // Prune avatars whose peer left or whose pose went stale.
            if (avatars.Count > 0)
            {
                var gone = new List<uint>();
                foreach (var kv in avatars)
                {
                    PoseRecord rec;
                    bool fresh = poses.TryGetValue(kv.Key, out rec) && now - rec.ReceivedAt <= StaleSeconds;
                    if (!remote.Contains(kv.Key) || !fresh) gone.Add(kv.Key);
                }
                for (int i = 0; i < gone.Count; i++)
                {
                    DestroyAvatar(avatars[gone[i]]);
                    avatars.Remove(gone[i]);
                }
            }

            EnsureRoot();
        }

        static GameObject CreateAvatar(uint peer)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "apfep-puppet-" + peer;
            go.transform.localScale = new Vector3(0.7f, AvatarHeight * 0.5f, 0.7f); // height ~2, narrower body
            ForceTrigger(go);
            Paint(go, peer);
            go.transform.position = new Vector3(0f, AvatarHeight * 0.5f, 0f);
            return go;
        }

        // The collider on a primitive is physics-active by default; make it a trigger so
        // avatars never collide with gameplay. PhysicsModule types are not referenced by
        // the csproj, so set isTrigger through the runtime wrapper type via reflection
        // (generated CapsuleCollider/Collider expose the isTrigger property).
        static void ForceTrigger(GameObject go)
        {
            try
            {
                Component[] comps = go.GetComponents<Component>();
                if (comps is null) return;
                for (int i = 0; i < comps.Length; i++)
                {
                    Component c = comps[i];
                    if (c is null) continue;
                    Type ty = c.GetType();
                    if (!ty.Name.EndsWith("Collider")) continue;
                    PropertyInfo prop = ty.GetProperty("isTrigger", BindingFlags.Public | BindingFlags.Instance);
                    if (prop is not null && prop.CanWrite) prop.SetValue(c, true);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[apfep] puppet trigger setup failed: " + e.Message);
            }
        }

        // Theme-independent palette: golden-angle hue per peer id, so peers are distinct.
        static void Paint(GameObject go, uint peer)
        {
            try
            {
                Renderer rend = go.GetComponent<Renderer>();
                if (rend is null) return;
                // Generated interop Color lacks HsvToColor; convert hue -> RGB manually.
                float hue = (peer * 0.618034f) % 1f;
                float h = hue * 6f;
                int sector = (int)h;
                float f = h - sector;
                const float s = 0.65f, v = 0.95f;
                float r, g, b;
                switch (sector % 6)
                {
                    case 0: r = v; g = v * (1f - s * f); b = v * (1f - s); break;
                    case 1: r = v * (1f - s * (1f - f)); g = v; b = v * (1f - s); break;
                    case 2: r = v * (1f - s); g = v; b = v * (1f - s * f); break;
                    case 3: r = v * (1f - s); g = v * (1f - s * f); b = v; break;
                    case 4: r = v * (1f - s * f); g = v * (1f - s); b = v; break;
                    default: r = v; g = v * (1f - s); b = v * (1f - s * f); break;
                }
                Color c = new Color(r, g, b, 1f);
                // .material (not sharedMaterial) gives this avatar its own material copy.
                rend.material.color = c;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[apfep] puppet paint failed: " + e.Message);
            }
        }

        static void EnsureRoot()
        {
            if (avatars.Count == 0) return;
            if (root is null || !root)
            {
                root = new GameObject("apfep-puppets");
                UnityEngine.Object.DontDestroyOnLoad(root);
            }
            foreach (var av in avatars.Values)
            {
                if (av.Go is not null && av.Go.transform.parent is null)
                    av.Go.transform.SetParent(root.transform, true);
            }
        }

        static void DestroyAvatar(AvatarState av)
        {
            if (av?.Go is not null) UnityEngine.Object.Destroy(av.Go);
        }

        static void DestroyAll()
        {
            foreach (var av in avatars.Values) DestroyAvatar(av);
            avatars.Clear();
            if (root is not null && root) UnityEngine.Object.Destroy(root);
            root = null;
        }
    }
}
