using System;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using ApfepCoop.Net;

// Co-op plugin for "A Place for Every Potion" (Unity 6000.3.23f1 IL2CPP, Steam appid 5189500).
// Host-authoritative state sync over Steam lobbies + SteamNetworkingMessages, with a
// localhost UDP transport for automated QA on one PC.
[assembly: MelonInfo(typeof(ApfepCoop.CoopPlugin), "ApfepCoop", "0.1.0", "coop-anything agent")]
[assembly: MelonGame("CubeQuad", "A Place for Every Potion")]

namespace ApfepCoop
{
    public class CoopPlugin : MelonPlugin
    {
        static float lastTickLog = -1f;
        static int ticks;
        static bool auto;

        bool inited;

        public override void OnApplicationStarted()
        {
            if (inited) return;
            inited = true;

            auto = Environment.GetEnvironmentVariable("APFEP_SMOKE_AUTO") == "1";

            var harmony = new HarmonyLib.Harmony("apfep.coop");

            // InputService.Update runs every frame in lobby and lab scenes: the mod's tick hook.
            var inputUpdate = AccessTools.Method(typeof(Il2CppCodeBase.Services.InputService.InputService), "Update");
            if (inputUpdate is null)
            {
                MelonLogger.Error("[apfep] InputService.Update not found — no frame pump");
                return;
            }
            harmony.Patch(inputUpdate, postfix: new HarmonyMethod(typeof(CoopPlugin), nameof(InputTickPostfix)));
            MelonLogger.Msg("[apfep] frame pump installed (InputService.Update)");

            if (ApfepCoop.Sync.SyncBrain.Install(harmony)) MelonLogger.Msg("[apfep] sync subsystem installed");
            if (ApfepCoop.Puppet.PuppetBrain.Install(harmony)) MelonLogger.Msg("[apfep] puppet subsystem installed");

            MelonLogger.Msg($"[apfep] SteamAPI.IsSteamRunning={Il2CppSteamworks.SteamAPI.IsSteamRunning()}");
        }

        static void InputTickPostfix()
        {
            ticks++;
            MaybeAutoContinue();

            CoopNet.Tick();
            if (ApfepCoop.Sync.SyncBrain.IsInstalled) ApfepCoop.Sync.SyncBrain.Tick();
            if (ApfepCoop.Puppet.PuppetBrain.IsInstalled) ApfepCoop.Puppet.PuppetBrain.Tick();

            if (Time.realtimeSinceStartup - lastTickLog < 2f) return;
            lastTickLog = Time.realtimeSinceStartup;
            int peers = CoopNet.Transport?.Peers?.Count ?? 0;
            MelonLogger.Msg($"[apfep] tick={ticks} mode={CoopNet.Mode} host={CoopNet.IsHost} peer={CoopNet.LocalPeerId} peers={peers} avatars={ApfepCoop.Puppet.PuppetBrain.AvatarCount} recv={ApfepCoop.Puppet.PuppetBrain.ReceivedMoves} sent={ApfepCoop.Puppet.PuppetBrain.SentMoves} gs={ApfepCoop.Sync.SyncBrain.HostGameStateId} {ApfepCoop.Sync.SyncBrain.ProgressSummary()}");
        }

        // QA harness hook: APFEP_SMOKE_AUTO=1 drives ContinueGame from the frame pump so the
        // lab loads headlessly and sync/puppet paths get exercised without a human at the menu.
        static void MaybeAutoContinue()
        {
            if (!auto || ticks < 300) return;
            auto = false;
            try
            {
                var machine = Il2CppCodeBase.Services.Core.ServiceLocator
                    .Resolve<Il2CppCodeBase.Services.GameStateMachineService.IGameStateMachine>();
                if (machine is null) { MelonLogger.Warning("[apfep] IGameStateMachine not resolvable"); return; }
                MelonLogger.Msg("[apfep] auto Enter(ContinueGame)");
                machine.Enter(Il2Cpp.eGameState.ContinueGame);
            }
            catch (Exception e) { MelonLogger.Warning("[apfep] auto continue failed: " + e.Message); }
        }
    }
}
