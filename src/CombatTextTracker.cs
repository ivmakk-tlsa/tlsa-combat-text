using System;
using System.Collections.Generic;
using Game.Actors;
using Game.Data.Models.StatusEffects;
using Game.Logic.StatusEffects;
using UnityEngine;

namespace CombatText;

// The adapter between the game and the game-free CombatTextState. It reads the damage seam's game
// types into plain values for the state, and keeps a live ZombieActor per tracked id so the drawer
// can read ChestPosition, View and IsDead every frame without a scene search.
internal readonly struct ArmorPartHealth
{
    public readonly float Current;
    public readonly float Max;

    public ArmorPartHealth(float current, float max)
    {
        Current = current;
        Max = max;
    }
}

internal static class CombatTextTracker
{
    private static CombatTextState _state;
    private static readonly Dictionary<int, ZombieActor> Actors = new Dictionary<int, ZombieActor>();

    // The armor set per actor id, looked up once per actor. A null value means the lookup ran and
    // found none, so an unarmored zombie costs one scene search, not one per frame.
    private static readonly Dictionary<int, ActorArmorSet> Armor = new Dictionary<int, ActorArmorSet>();

    // Every armor set in the scene, refreshed on a timer. A hit that armor absorbs never reaches
    // SetHealth (the log shows no health write until the last part is gone), and the methods that
    // apply armor damage take Damage&, so a poll over the parts is the only safe way to notice the
    // first hit on a plate.
    private static ActorArmorSet[] _armorSets = System.Array.Empty<ActorArmorSet>();
    private static float _armorScanNext = -1f;
    private const float ArmorScanInterval = 1f;

    // Built on first damage, not in Load(), so the config entries are already bound.
    internal static CombatTextState State
    {
        get
        {
            if (_state == null)
            {
                _state = new CombatTextState(Plugin.MergeTickWindow.Value, Plugin.NumberLifetime.Value);
            }
            return _state;
        }
    }

    internal static ZombieActor Resolve(int actorId) =>
        Actors.TryGetValue(actorId, out var actor) ? actor : null;

    // One health write, measured across SetHealth. damageType is the game's DamageType as an int, 0
    // when unknown; critical is the Damage.Flags Critical bit. Returns whether the state now tracks
    // the actor, for the verbose seam line.
    internal static bool OnDamage(ZombieActor zombie, float before, float after, int damageType, bool critical)
    {
        try
        {
            int id = zombie.Id;
            float max = zombie.HealthMaxEffective;
            int type = damageType;

            // The number spawns at the actor's chest. Damage.Origin is not used: the game's knockback
            // and stagger take an origin as the attacker's side, so it is most likely the source
            // position, not the impact point.
            Vector3 position = zombie.ChestPosition;

            State.OnDamage(id, before, after, max, type, new WorldPoint(position.x, position.y, position.z), Time.time, critical);

            // A killing blow spawns its number but leaves no bar behind. The death hook usually fires
            // inside ApplyDamage, before this postfix, so the check here is what keeps a dead actor
            // out of the tracked set.
            if (zombie.IsDead)
            {
                State.Remove(id);
            }

            bool tracked = IsTracked(id);
            if (tracked)
            {
                Actors[id] = zombie;
            }
            else
            {
                Actors.Remove(id);
            }
            return tracked;
        }
        catch (Exception e)
        {
            if (!_damageWarned)
            {
                _damageWarned = true;
                Plugin.Log.LogWarning($"CombatText tracker failed (logged once): {e}");
            }
            return false;
        }
    }

    internal static void Forget(ZombieActor zombie)
    {
        try
        {
            if (zombie != null)
            {
                Forget(zombie.Id);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"CombatText forget failed: {e.Message}");
        }
    }

    // Track with no number: the armor hooks call this so an armored zombie shows its bars from the
    // first hit, whether or not that hit cost health.
    internal static void Track(ZombieActor zombie)
    {
        try
        {
            int id = zombie.Id;
            State.Track(id, zombie.Health, zombie.HealthMaxEffective);
            Actors[id] = zombie;
        }
        catch (Exception e)
        {
            if (!_trackWarned)
            {
                _trackWarned = true;
                Plugin.Log.LogWarning($"CombatText track failed (logged once): {e}");
            }
        }
    }

    internal static void Forget(int actorId)
    {
        State.Remove(actorId);
        Actors.Remove(actorId);
        Armor.Remove(actorId);
    }

    // Recycle a pooled actor id: forget it and also drop its armor-break markers, so a reused id does
    // not inherit the previous zombie's marker. Called from the spawn and reset hooks, not from death,
    // where a marker should finish its flash.
    internal static void Recycle(ZombieActor zombie)
    {
        try
        {
            if (zombie == null)
            {
                return;
            }
            int id = zombie.Id;
            Forget(id);
            State.ClearMarkers(id);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"CombatText recycle failed: {e.Message}");
        }
    }

    // Called once per drawn frame. Rescans the scene for armor sets on the interval, then starts
    // tracking any live, untracked zombie whose armor shows damage.
    internal static void PollArmor(float now)
    {
        try
        {
            if (now >= _armorScanNext)
            {
                _armorScanNext = now + ArmorScanInterval;
                _armorSets = UnityEngine.Object.FindObjectsOfType<ActorArmorSet>();
            }
            for (int i = 0; i < _armorSets.Length; i++)
            {
                var set = _armorSets[i];
                if (set is null || set.WasCollected || set == null || !IsDamaged(set.m_ArmorParts))
                {
                    continue;
                }
                var zombie = OwnerOf(set);
                if (zombie == null || zombie.IsDead || IsTracked(zombie.Id))
                {
                    continue;
                }
                Armor[zombie.Id] = set;
                Track(zombie);
                if (Plugin.Verbose.Value)
                {
                    Plugin.Log.LogDebug($"CombatText: actor {zombie.Id} tracked by the armor poll, armor={ArmorSummary(zombie)}.");
                }
            }
        }
        catch (Exception e)
        {
            if (!_armorPollWarned)
            {
                _armorPollWarned = true;
                Plugin.Log.LogWarning($"CombatText armor poll failed (logged once): {e}");
            }
        }
    }

    // The status effects the overlay shows, resolved once from the game's static provider. Retried
    // until all resolve, because the provider may not be ready before a mission loads.
    private static StatusEffectModel _burning;
    private static StatusEffectModel _burningSmall;
    private static StatusEffectModel _bleeding;
    private static StatusEffectModel _stunned;
    private static float _statusLogNext;

    // Reused per-frame buffers for one actor's readings, handed to CombatTextState.ResolveStatuses,
    // which owns the show-delay bookkeeping.
    private static readonly float[] _fractionBuf = new float[CombatTextState.StatusKindCount];
    private static readonly bool[] _presentBuf = new bool[CombatTextState.StatusKindCount];

    // One-time warning guards for the frequently called reads, so a persistent failure never spams the
    // log frame after frame.
    private static bool _statusReadWarned;
    private static bool _armorPollWarned;
    private static bool _damageWarned;
    private static bool _trackWarned;
    private static bool _armorBrokenWarned;

    private static void EnsureModels()
    {
        if (_burning != null && _burningSmall != null && _bleeding != null && _stunned != null)
        {
            return;
        }
        try
        {
            _burning = StatusEffects.Burning;
            _burningSmall = StatusEffects.BurningSmall;
            _bleeding = StatusEffects.Bleeding;
            _stunned = StatusEffects.Stunned;
        }
        catch
        {
            // Not ready yet; a later frame retries.
        }
    }

    // Fills `into` with the zombie's active shown statuses, each with its remaining-time fraction
    // (1 at start, 0 at end). Reads the actor's live status effects, classifies each, keeps the max
    // fraction per kind, and holds each kind back until it lasts StatusShowDelay. Ordered by kind, so
    // the row layout is stable.
    internal static void ReadStatuses(ZombieActor zombie, List<ActiveStatus> into)
    {
        into.Clear();
        try
        {
            var receiver = zombie.StatusEffectReceiver;
            if (receiver == null)
            {
                return;
            }
            var effects = receiver.StatusEffects;
            if (effects == null)
            {
                return;
            }
            EnsureModels();
            bool haveAll = _burning != null && _burningSmall != null && _bleeding != null && _stunned != null;
            bool log = Plugin.Verbose.Value && Time.time >= _statusLogNext;
            bool logged = false;

            for (int i = 0; i < _fractionBuf.Length; i++)
            {
                _fractionBuf[i] = 0f;
                _presentBuf[i] = false;
            }

            var node = effects.First;
            while (node != null)
            {
                var effect = node.Value;
                if (effect != null && effect.IsActive)
                {
                    int kind = ClassifyKind(effect.Model, haveAll);
                    if (kind >= 0)
                    {
                        float duration = effect.Duration;

                        // Read the percentage only when duration is unknown, to save an interop call;
                        // CombatTextState.StatusFraction encodes the rule over these plain values.
                        float remaining = duration > 0f ? effect.TimeRemaining : 0f;
                        float percentage = duration > 0f ? 0f : effect.TimeRemainingPercentage;
                        float fraction = CombatTextState.StatusFraction(duration, remaining, percentage);

                        if (fraction > _fractionBuf[kind])
                        {
                            _fractionBuf[kind] = fraction;
                        }
                        _presentBuf[kind] = true;
                    }

                    if (log)
                    {
                        string modelName = effect.Model != null ? effect.Model.name : "<null>";
                        Plugin.Log.LogDebug($"CombatText status: actor {zombie.Id} {modelName} kind={kind} remaining={effect.TimeRemaining} duration={effect.Duration}");
                        logged = true;
                    }
                }
                node = node.Next;
            }
            if (logged)
            {
                _statusLogNext = Time.time + 0.5f;
            }

            State.ResolveStatuses(zombie.Id, _presentBuf, _fractionBuf, Time.time, Plugin.StatusShowDelay.Value, into);
        }
        catch (Exception e)
        {
            if (!_statusReadWarned)
            {
                _statusReadWarned = true;
                Plugin.Log.LogWarning($"CombatText status read failed (logged once): {e}");
            }
        }
    }

    // The status kind for an effect model, or -1 if not one the overlay shows. Reference match first;
    // a name fallback covers the window before the model references resolve.
    private static int ClassifyKind(StatusEffectModel model, bool haveAll)
    {
        if (model == null)
        {
            return -1;
        }
        if (model == _burning || model == _burningSmall)
        {
            return (int)StatusKind.Fire;
        }
        if (model == _bleeding)
        {
            return (int)StatusKind.Bleed;
        }
        if (model == _stunned)
        {
            return (int)StatusKind.Stun;
        }
        if (!haveAll)
        {
            return CombatTextState.ClassifyStatusByName(model.name);
        }
        return -1;
    }

    // A plate just broke. If it was the last one, spawn the armor-break marker once. The set is read
    // after Break() ran, so no part with health left means no armor remains.
    internal static void OnArmorBroken(ZombieActor zombie)
    {
        try
        {
            if (zombie == null || zombie.IsDead)
            {
                return;
            }
            var set = ResolveArmor(zombie);
            if (set == null || !AllBroken(set.m_ArmorParts))
            {
                return;
            }
            Vector3 position = zombie.ChestPosition;
            State.OnArmorBroken(zombie.Id, new WorldPoint(position.x, position.y, position.z), Time.time);
            Actors[zombie.Id] = zombie;
            if (Plugin.Verbose.Value)
            {
                Plugin.Log.LogDebug($"CombatText: actor {zombie.Id} lost its last armor plate, marker spawned.");
            }
        }
        catch (Exception e)
        {
            if (!_armorBrokenWarned)
            {
                _armorBrokenWarned = true;
                Plugin.Log.LogWarning($"CombatText armor-broken check failed (logged once): {e}");
            }
        }
    }

    private static bool AllBroken(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ActorArmorPart> parts)
    {
        if (parts == null || parts.Length == 0)
        {
            return false;
        }
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part != null && part.HealthCurrent > 0f)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsDamaged(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ActorArmorPart> parts)
    {
        if (parts == null)
        {
            return false;
        }
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part != null && part.HealthCurrent < part.HealthMax)
            {
                return true;
            }
        }
        return false;
    }

    private static ZombieActor OwnerOf(ActorArmorSet set)
    {
        var zombie = set.GetComponentInParent<ZombieActor>();
        if (zombie != null)
        {
            return zombie;
        }
        var view = set.m_ActorView;
        var actor = view != null ? view.Actor : null;
        return actor != null ? actor.TryCast<ZombieActor>() : null;
    }

    // The zombie an armor part belongs to: up the hierarchy first, then through the set's view.
    internal static ZombieActor OwnerOf(ActorArmorPart part)
    {
        var zombie = part.GetComponentInParent<ZombieActor>();
        if (zombie != null)
        {
            return zombie;
        }
        var set = part.GetComponentInParent<ActorArmorSet>();
        return set != null ? OwnerOf(set) : null;
    }

    // The parts' health as plain floats for the drawer, in the set's own order. Leaves the list
    // empty for an unarmored zombie.
    internal static void ReadArmor(ZombieActor zombie, List<ArmorPartHealth> into)
    {
        var parts = ResolveArmor(zombie)?.m_ArmorParts;
        if (parts == null)
        {
            return;
        }
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part != null)
            {
                into.Add(new ArmorPartHealth(part.HealthCurrent, part.HealthMax));
            }
        }
    }

    // One line for the verbose log: where the set was found, the part count, the active index, and
    // each part's current/max.
    internal static string ArmorSummary(ZombieActor zombie)
    {
        var set = ResolveArmor(zombie);
        if (set == null)
        {
            return "none";
        }
        var parts = set.m_ArmorParts;
        int count = parts != null ? parts.Length : 0;
        var text = new System.Text.StringBuilder();
        text.Append(count).Append(" parts active=").Append(set.m_ActivePartIndex).Append(" isActive=").Append(set.IsActive).Append(" [");
        for (int i = 0; i < count; i++)
        {
            var part = parts[i];
            if (i > 0)
            {
                text.Append(' ');
            }
            text.Append(part != null ? $"{part.HealthCurrent}/{part.HealthMax}" : "null");
        }
        return text.Append(']').ToString();
    }

    private static ActorArmorSet ResolveArmor(ZombieActor zombie)
    {
        int id = zombie.Id;
        if (Armor.TryGetValue(id, out var cached))
        {
            return cached;
        }
        ActorArmorSet set = null;
        string where = "nowhere";
        try
        {
            set = zombie.GetComponentInChildren<ActorArmorSet>(true);
            if (set != null)
            {
                where = "actor";
            }
            else
            {
                var view = zombie.View;
                set = view != null ? view.GetComponentInChildren<ActorArmorSet>(true) : null;
                if (set != null)
                {
                    where = "view";
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"CombatText armor lookup failed: {e.Message}");
        }
        Armor[id] = set;
        if (Plugin.Verbose.Value)
        {
            Plugin.Log.LogDebug($"CombatText: actor {id} armor set found on {where}.");
        }
        return set;
    }

    // The state exposes its tracked actors as a collection, not by id, so this is a short scan over
    // the handful of zombies currently damaged.
    private static bool IsTracked(int actorId)
    {
        foreach (var actor in State.Tracked)
        {
            if (actor.Id == actorId)
            {
                return true;
            }
        }
        return false;
    }
}
