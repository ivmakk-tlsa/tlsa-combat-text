using System;
using System.Collections.Generic;
using Game.Actors;
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
            Plugin.Log.LogWarning($"CombatText tracker failed: {e.Message}");
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
            Plugin.Log.LogWarning($"CombatText track failed: {e.Message}");
        }
    }

    internal static void Forget(int actorId)
    {
        State.Remove(actorId);
        Actors.Remove(actorId);
        Armor.Remove(actorId);
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
            Plugin.Log.LogWarning($"CombatText armor poll failed: {e.Message}");
        }
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
