using System;
using System.Collections.Generic;

namespace CombatText;

// The overlay state, with no game types. It holds the damaged actors to draw a bar for and the live
// floating numbers, on a clock the caller supplies, so a unit test scripts time. The game adapter
// (Plugin.cs) reads CharacterActor health and the Damage struct into these plain values and the
// IMGUI drawer reads the two collections back. Keep this file free of BepInEx, Il2Cpp and
// UnityEngine references; the test project links it directly.

// A world-space position, so the state needs no UnityEngine.Vector3. The adapter converts.
public readonly struct WorldPoint
{
    public readonly float X;
    public readonly float Y;
    public readonly float Z;

    public WorldPoint(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

// One damaged actor: the health last seen at a damage event, and the bar fill it implies.
public sealed class TrackedActor
{
    public int Id { get; }
    public float Health { get; private set; }
    public float HealthMax { get; private set; }

    internal TrackedActor(int id, float health, float healthMax)
    {
        Id = id;
        Health = health;
        HealthMax = healthMax;
    }

    // The bar fill. A non-positive max means the adapter could not read one, so draw an empty bar
    // rather than divide.
    public float Fraction
    {
        get
        {
            if (HealthMax <= 0f)
            {
                return 0f;
            }
            float fraction = Health / HealthMax;
            if (fraction < 0f)
            {
                return 0f;
            }
            return fraction > 1f ? 1f : fraction;
        }
    }

    internal void Update(float health, float healthMax)
    {
        Health = health;
        HealthMax = healthMax;
    }
}

// One floating number. Amount is the summed health loss (positive). DamageType is the game's
// DamageType enum as an int; the state never interprets it, it only keeps hits of the same type
// mergeable and lets the drawer pick a colour. Critical marks a number that includes at least one
// critical hit, so the drawer can weight it.
public sealed class FloatingNumber
{
    private readonly float _lifetime;

    public int ActorId { get; }
    public float Amount { get; private set; }
    public int DamageType { get; }
    public bool Critical { get; private set; }
    public WorldPoint Position { get; private set; }
    public double SpawnedAt { get; private set; }

    internal FloatingNumber(int actorId, float amount, int damageType, bool critical, WorldPoint position, double spawnedAt, float lifetime)
    {
        ActorId = actorId;
        Amount = amount;
        DamageType = damageType;
        Critical = critical;
        Position = position;
        SpawnedAt = spawnedAt;
        _lifetime = lifetime;
    }

    // Age against the lifetime, 0 at spawn and 1 at or past the end.
    public float Progress(double now)
    {
        if (_lifetime <= 0f)
        {
            return 1f;
        }
        double progress = (now - SpawnedAt) / _lifetime;
        if (progress <= 0.0)
        {
            return 0f;
        }
        return progress >= 1.0 ? 1f : (float)progress;
    }

    // Fully opaque for the first part of the life, then a linear fade, so the number is readable
    // for most of its time on screen instead of half-transparent at half life.
    public const float FadeStart = 0.6f;

    public float Alpha(double now)
    {
        float progress = Progress(now);
        if (progress <= FadeStart)
        {
            return 1f;
        }
        return 1f - (progress - FadeStart) / (1f - FadeStart);
    }

    // Vertical drift in screen pixels; the drawer subtracts it from the projected y.
    public float Drift(double now) => Progress(now) * CombatTextState.DriftPixels;

    // A later hit of the same type on the same actor folds into this number: the amounts sum, the
    // number restarts its life, it moves to the newer hit position, and a crit anywhere in the run
    // keeps the number marked critical.
    internal void Merge(float amount, bool critical, WorldPoint position, double now)
    {
        Amount += amount;
        Critical |= critical;
        Position = position;
        SpawnedAt = now;
    }
}

// A one-shot "armor destroyed" marker. It spawns when the last armor plate of a zombie breaks,
// rises and fades on the same clock as a number, and the drawer renders it as an icon. It carries
// no amount or type, and the state never merges markers: one per break.
public sealed class ArmorBreakMarker
{
    private readonly float _lifetime;

    public int ActorId { get; }
    public WorldPoint Position { get; }
    public double SpawnedAt { get; }

    internal ArmorBreakMarker(int actorId, WorldPoint position, double spawnedAt, float lifetime)
    {
        ActorId = actorId;
        Position = position;
        SpawnedAt = spawnedAt;
        _lifetime = lifetime;
    }

    public float Progress(double now)
    {
        if (_lifetime <= 0f)
        {
            return 1f;
        }
        double progress = (now - SpawnedAt) / _lifetime;
        if (progress <= 0.0)
        {
            return 0f;
        }
        return progress >= 1.0 ? 1f : (float)progress;
    }

    public float Alpha(double now)
    {
        float progress = Progress(now);
        if (progress <= FloatingNumber.FadeStart)
        {
            return 1f;
        }
        return 1f - (progress - FloatingNumber.FadeStart) / (1f - FloatingNumber.FadeStart);
    }

    public float Drift(double now) => Progress(now) * CombatTextState.DriftPixels;
}

public sealed class CombatTextState
{
    // How far a number rises over its full lifetime, in screen pixels.
    public const float DriftPixels = 40f;

    private readonly float _mergeTickWindow;
    private readonly float _numberLifetime;
    private readonly Dictionary<int, TrackedActor> _tracked = new Dictionary<int, TrackedActor>();
    private readonly List<FloatingNumber> _numbers = new List<FloatingNumber>();
    private readonly List<ArmorBreakMarker> _markers = new List<ArmorBreakMarker>();

    public CombatTextState(float mergeTickWindow, float numberLifetime)
    {
        _mergeTickWindow = mergeTickWindow;
        _numberLifetime = numberLifetime;
    }

    public IReadOnlyCollection<TrackedActor> Tracked => _tracked.Values;

    public IReadOnlyList<FloatingNumber> Numbers => _numbers;

    public IReadOnlyList<ArmorBreakMarker> Markers => _markers;

    // One damage event, already resolved to a health loss by the adapter. A non-positive loss (a
    // fully resisted hit, or a heal) draws nothing. An actor back at full health leaves the tracked
    // set, so its bar disappears.
    public void OnDamage(int actorId, float healthBefore, float healthAfter, float healthMax, int damageType, WorldPoint position, double now, bool critical = false)
    {
        if (healthMax > 0f && healthAfter >= healthMax)
        {
            _tracked.Remove(actorId);
            return;
        }

        float amount = healthBefore - healthAfter;
        if (amount <= 0f)
        {
            // A partial heal still moves the bar of an actor already being drawn.
            if (_tracked.TryGetValue(actorId, out var healed))
            {
                healed.Update(healthAfter, healthMax);
            }
            return;
        }

        if (_tracked.TryGetValue(actorId, out var actor))
        {
            actor.Update(healthAfter, healthMax);
        }
        else
        {
            _tracked.Add(actorId, new TrackedActor(actorId, healthAfter, healthMax));
        }

        var mergeInto = FindMergeTarget(actorId, damageType, now);
        if (mergeInto != null)
        {
            mergeInto.Merge(amount, critical, position, now);
            return;
        }
        _numbers.Add(new FloatingNumber(actorId, amount, damageType, critical, position, now, _numberLifetime));
    }

    // Track an actor with no number. An armor hit that costs no health still puts the bar up, so an
    // armored zombie shows its state from the first hit.
    public void Track(int actorId, float health, float healthMax)
    {
        if (_tracked.TryGetValue(actorId, out var actor))
        {
            actor.Update(health, healthMax);
        }
        else
        {
            _tracked.Add(actorId, new TrackedActor(actorId, health, healthMax));
        }
    }

    // Evict an actor's bar. Its numbers stay and expire on their own clock: the adapter calls this
    // from the death hook, which fires inside the killing blow's ApplyDamage, and that last number
    // is the one the player most wants to see.
    public void Remove(int actorId)
    {
        _tracked.Remove(actorId);
    }

    // The last armor plate of an actor broke. Spawn one marker, unless one for this actor is still
    // alive, so a re-scan or a repeated break event does not stack two.
    public void OnArmorBroken(int actorId, WorldPoint position, double now)
    {
        for (int i = 0; i < _markers.Count; i++)
        {
            if (_markers[i].ActorId == actorId)
            {
                return;
            }
        }
        _markers.Add(new ArmorBreakMarker(actorId, position, now, _numberLifetime));
    }

    // Drop the numbers and markers that have finished their life. The tracked set is untouched: an
    // actor leaves it only through OnDamage at full health or through Remove.
    public void Tick(double now)
    {
        _numbers.RemoveAll(n => now - n.SpawnedAt >= _numberLifetime);
        _markers.RemoveAll(m => now - m.SpawnedAt >= _numberLifetime);
    }

    // The newest number for the same actor and damage type that is still inside the merge window,
    // so a burn tick lands on the running total instead of stacking a new number every tick.
    private FloatingNumber FindMergeTarget(int actorId, int damageType, double now)
    {
        for (int i = _numbers.Count - 1; i >= 0; i--)
        {
            var number = _numbers[i];
            if (number.ActorId == actorId && number.DamageType == damageType && now - number.SpawnedAt < _mergeTickWindow)
            {
                return number;
            }
        }
        return null;
    }
}
