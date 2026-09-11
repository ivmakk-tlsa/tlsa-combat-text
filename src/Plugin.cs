using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Game.Actors;
using Game.Data.Combat;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace CombatText;

// Zombie health bars and floating damage numbers. The ApplyDamage patch below measures the real
// health loss per hit and feeds CombatTextTracker; CombatTextDrawer reads that back each frame and
// draws the bars and numbers with IMGUI.
[BepInPlugin(PluginGuid, "CombatText", "1.1.0")]
public class Plugin : BasePlugin
{
    public const string PluginGuid = "com.ivmakk.tlsa.combattext";
    internal static new ManualLogSource Log;

    internal static ConfigEntry<bool> Verbose;
    internal static ConfigEntry<bool> ShowBars;
    internal static ConfigEntry<bool> ShowNumbers;
    internal static ConfigEntry<float> MergeTickWindow;
    internal static ConfigEntry<float> NumberLifetime;
    internal static ConfigEntry<float> BarOffset;
    internal static ConfigEntry<float> Scale;
    internal static ConfigEntry<float> StatusIconSize;
    internal static ConfigEntry<float> ArmorIconSize;
    internal static ConfigEntry<float> StatusShowDelay;

    public override void Load()
    {
        Log = base.Log;

        Verbose = Config.Bind(
            "General", "Verbose", false,
            "Log every zombie damage event: actor id, model, damage type and amount, health before and after. Keep off in normal play.");
        ShowBars = Config.Bind(
            "Display", "ShowBars", true,
            "When true, a health bar is drawn above each damaged zombie until it dies or returns to full health.");
        ShowNumbers = Config.Bind(
            "Display", "ShowNumbers", true,
            "When true, each damage event spawns a floating number showing the health actually lost.");
        MergeTickWindow = Config.Bind(
            "Display", "MergeTickWindow", 0.5f,
            "Seconds within which repeated damage of the same type on the same zombie sums into one number instead of spawning another.");
        NumberLifetime = Config.Bind(
            "Display", "NumberLifetime", 1.5f,
            "Seconds a floating number stays on screen while it drifts upward and fades out.");
        Scale = Config.Bind(
            "Display", "Scale", 1.0f,
            "Size multiplier for the bars and numbers, on top of the game's own HUD scale (resolution plus the UI Scale setting).");
        BarOffset = Config.Bind(
            "Display", "BarOffset", 0.6f,
            "World units above the zombie's chest position where the health bar is anchored.");
        StatusIconSize = Config.Bind(
            "Display", "StatusIconSize", 16.0f,
            new BepInEx.Configuration.ConfigDescription(
                "Base size in pixels (at 1080p) of the status icons (fire, bleed, stun) drawn after the bar. Scaled by the HUD scale and the Scale setting.",
                new AcceptableValueRange<float>(4f, 64f)));
        ArmorIconSize = Config.Bind(
            "Display", "ArmorIconSize", 16.0f,
            new BepInEx.Configuration.ConfigDescription(
                "Base size in pixels (at 1080p) of the armor-break icon that flashes when the last plate breaks. Scaled by the HUD scale and the Scale setting.",
                new AcceptableValueRange<float>(4f, 64f)));
        StatusShowDelay = Config.Bind(
            "Display", "StatusShowDelay", 0.1f,
            new BepInEx.Configuration.ConfigDescription(
                "Seconds a status effect must last before its icon appears, so a zombie that dies right after the first tick never flashes an icon.",
                new AcceptableValueRange<float>(0f, 5f)));

        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll();

        ClassInjector.RegisterTypeInIl2Cpp<CombatTextDrawer>();
        var host = new GameObject("CombatTextDrawer");
        host.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<CombatTextDrawer>();

        Log.LogInfo("CombatText loaded. Zombie damage seam and overlay active.");
    }
}

// The damage seam. SetHealth(float, bool) is the single health write for every source (melee,
// bullets, explosions, damage over time, heals), so one patch pair covers them all. The number
// that matters is the health delta measured across the call: the prefix records Health, the
// postfix reads it again. A heal gives a non-positive delta and draws nothing.
//
// ApplyDamage(Damage&) is the more obvious seam, but a Harmony patch on it stops damage being dealt
// at all: Damage is an IL2CPP value type that the interop generates as a class, and the detour
// marshals the byref struct as an object pointer, so the original reads a shifted struct. Never
// patch a game method whose signature carries Damage or another non-blittable struct.
//
// CharacterActor defines SetHealth and zombies do not override it, so the patch targets the base
// type and narrows to zombies inside with TryCast (is/as do not work across the interop boundary).
// A non-zombie leaves __state at -1 and the postfix returns. The overload is bound by argument
// types because SetHealth(float) also exists.
[HarmonyPatch(typeof(CharacterActor), nameof(CharacterActor.SetHealth), new Type[] { typeof(float), typeof(bool) })]
public static class SetHealthPatch
{
    [HarmonyPrefix]
    public static void Prefix(CharacterActor __instance, ref float __state)
    {
        __state = -1f;
        try
        {
            if (__instance != null && __instance.TryCast<ZombieActor>() != null)
            {
                __state = __instance.Health;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"CombatText SetHealth prefix failed: {e.Message}");
        }
    }

    [HarmonyPostfix]
    public static void Postfix(CharacterActor __instance, float value, bool forceUpdate, float __state)
    {
        try
        {
            if (__state < 0f)
            {
                return;
            }
            var zombie = __instance?.TryCast<ZombieActor>();
            if (zombie == null)
            {
                return;
            }
            float after = __instance.Health;

            // The damage type comes from the actor's last damage record. Reading it through the
            // property is safe: the interop boxes a copy. Whether ApplyDamage stores the record
            // before or after it writes health is unknown, so the verbose line prints the record's
            // time next to the frame time; equal means the type belongs to this hit.
            int type = 0;
            bool critical = false;
            float previousTime = -1f;
            var previous = __instance.PreviousDamage;
            if (previous != null)
            {
                type = (int)previous.Type;
                critical = (previous.Flags & DamageFlags.Critical) != 0;
                previousTime = __instance.PreviousDamageTime;
            }
            bool tracked = CombatTextTracker.OnDamage(zombie, __state, after, type, critical);

            if (!Plugin.Verbose.Value)
            {
                return;
            }
            string model = zombie.Model != null ? zombie.Model.name : "<null>";
            string previousText = previous != null ? $"{previous.Type} flags={previous.Flags} amount={previous.Amount} origin={previous.Origin}" : "<null>";
            Plugin.Log.LogDebug(
                $"CombatText: actor {__instance.Id} {model} SetHealth({value}, {forceUpdate}) " +
                $"health {__state} -> {after} (delta {__state - after}) max={__instance.HealthMaxEffective} dead={__instance.IsDead} tracked={tracked} " +
                $"previous=[{previousText}] previousTime={previousTime} now={Time.time} chest={__instance.ChestPosition} " +
                $"armor={CombatTextTracker.ArmorSummary(zombie)}");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"CombatText SetHealth postfix failed: {e.Message}");
        }
    }
}

// Armor. A broken part is the one armor event with a safe seam (Break() takes no arguments; the
// two methods that apply armor damage take Damage& and are off limits). A hit on armor never
// reaches SetHealth, so the tracker's armor poll is what catches the first hit on a plate; this
// postfix is the fallback that starts tracking at the latest when the first part goes.
[HarmonyPatch(typeof(ActorArmorPart), nameof(ActorArmorPart.Break))]
public static class ArmorPartBreakPatch
{
    [HarmonyPostfix]
    public static void Postfix(ActorArmorPart __instance)
    {
        try
        {
            var zombie = CombatTextTracker.OwnerOf(__instance);
            if (zombie != null && !zombie.IsDead)
            {
                CombatTextTracker.Track(zombie);
                CombatTextTracker.OnArmorBroken(zombie);
            }
            if (Plugin.Verbose.Value)
            {
                string owner = zombie != null ? $"actor {zombie.Id} armor={CombatTextTracker.ArmorSummary(zombie)}" : "no zombie owner";
                Plugin.Log.LogDebug($"CombatText: armor part broke ({__instance.HealthCurrent}/{__instance.HealthMax}) on {owner}.");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"CombatText armor break postfix failed: {e.Message}");
        }
    }
}

// Eviction. Zombies come from a pool and are reused, so a bar must go on death and again when the
// same actor is reset or respawned, or a recycled zombie carries the previous one's health bar.
// Forget swallows its own errors, so these postfixes stay one-liners.
[HarmonyPatch(typeof(ZombieActor), nameof(ZombieActor.OnDied))]
public static class ZombieOnDiedPatch
{
    [HarmonyPostfix]
    public static void Postfix(ZombieActor __instance) => CombatTextTracker.Forget(__instance);
}

[HarmonyPatch(typeof(ZombieActor), nameof(ZombieActor.ResetState))]
public static class ZombieResetStatePatch
{
    [HarmonyPostfix]
    public static void Postfix(ZombieActor __instance) => CombatTextTracker.Forget(__instance);
}

// Spawn(ZombieSpawnData) is the natural hook, but ZombieSpawnData is a non-blittable struct by
// value, the same marshaling hazard as Damage above. OnSpawning() takes no arguments and runs in
// the same spawn.
[HarmonyPatch(typeof(ZombieActor), nameof(ZombieActor.OnSpawning))]
public static class ZombieOnSpawningPatch
{
    [HarmonyPostfix]
    public static void Postfix(ZombieActor __instance) => CombatTextTracker.Forget(__instance);
}
