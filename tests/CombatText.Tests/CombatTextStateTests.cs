using System.Linq;
using CombatText;
using Xunit;

namespace CombatText.Tests;

public class CombatTextStateTests
{
    private const float MergeWindow = 0.5f;
    private const float Lifetime = 1.0f;

    private static CombatTextState State(float mergeWindow = MergeWindow, float lifetime = Lifetime)
        => new CombatTextState(mergeWindow, lifetime);

    private static WorldPoint P(float x = 1f, float y = 2f, float z = 3f) => new WorldPoint(x, y, z);

    private static TrackedActor Only(CombatTextState state) => Assert.Single(state.Tracked);

    // ---- Tracking ----

    [Fact]
    public void Damage_tracks_the_actor_with_its_new_health()
    {
        var state = State();

        state.OnDamage(7, healthBefore: 100f, healthAfter: 80f, healthMax: 100f, damageType: 1, position: P(), now: 0.0);

        var actor = Only(state);
        Assert.Equal(7, actor.Id);
        Assert.Equal(80f, actor.Health);
        Assert.Equal(100f, actor.HealthMax);
    }

    [Fact]
    public void Zero_delta_tracks_nothing_and_spawns_nothing()
    {
        var state = State();

        state.OnDamage(7, 80f, 80f, 100f, 1, P(), 0.0);

        Assert.Empty(state.Tracked);
        Assert.Empty(state.Numbers);
    }

    [Fact]
    public void A_heal_on_an_untracked_actor_tracks_nothing_and_spawns_nothing()
    {
        var state = State();

        state.OnDamage(7, 60f, 70f, 100f, 1, P(), 0.0);

        Assert.Empty(state.Tracked);
        Assert.Empty(state.Numbers);
    }

    [Fact]
    public void A_second_damage_updates_health_and_max()
    {
        var state = State();
        state.OnDamage(7, 100f, 80f, 100f, 1, P(), 0.0);

        state.OnDamage(7, 80f, 55f, 90f, 1, P(), 1.0);

        var actor = Only(state);
        Assert.Equal(55f, actor.Health);
        Assert.Equal(90f, actor.HealthMax);
    }

    [Fact]
    public void Health_back_at_max_removes_a_tracked_actor()
    {
        var state = State();
        state.OnDamage(7, 100f, 80f, 100f, 1, P(), 0.0);

        state.OnDamage(7, 80f, 100f, 100f, 1, P(), 1.0);

        Assert.Empty(state.Tracked);
    }

    [Fact]
    public void Health_at_max_does_not_track_an_unknown_actor()
    {
        var state = State();

        state.OnDamage(7, 120f, 100f, 100f, 1, P(), 0.0);

        Assert.Empty(state.Tracked);
        Assert.Empty(state.Numbers);
    }

    [Fact]
    public void Remove_evicts_the_actor_and_keeps_its_numbers()
    {
        var state = State();
        state.OnDamage(7, 100f, 80f, 100f, 1, P(), 0.0);
        state.OnDamage(9, 100f, 80f, 100f, 1, P(), 0.0);

        state.Remove(7);

        Assert.Equal(9, Only(state).Id);
        Assert.Equal(2, state.Numbers.Count);
        Assert.Contains(state.Numbers, n => n.ActorId == 7);
    }

    [Fact]
    public void Remove_of_an_unknown_id_changes_nothing()
    {
        var state = State();
        state.OnDamage(7, 100f, 80f, 100f, 1, P(), 0.0);

        state.Remove(42);

        Assert.Equal(7, Only(state).Id);
        Assert.Single(state.Numbers);
    }

    [Fact]
    public void Track_adds_an_actor_without_a_number()
    {
        var state = State();

        state.Track(7, 100f, 100f);

        var actor = Only(state);
        Assert.Equal(7, actor.Id);
        Assert.Equal(1f, actor.Fraction);
        Assert.Empty(state.Numbers);
    }

    [Fact]
    public void Track_updates_a_tracked_actor()
    {
        var state = State();
        state.OnDamage(7, 100f, 80f, 100f, 1, P(), 0.0);

        state.Track(7, 60f, 120f);

        var actor = Only(state);
        Assert.Equal(60f, actor.Health);
        Assert.Equal(120f, actor.HealthMax);
        Assert.Single(state.Numbers);
    }

    [Fact]
    public void Fraction_is_health_over_max()
    {
        var state = State();

        state.OnDamage(7, 100f, 25f, 100f, 1, P(), 0.0);

        Assert.Equal(0.25f, Only(state).Fraction, 5);
    }

    [Fact]
    public void Fraction_clamps_negative_health_to_zero()
    {
        var state = State();

        state.OnDamage(7, 20f, -5f, 100f, 1, P(), 0.0);

        Assert.Equal(0f, Only(state).Fraction);
    }

    [Fact]
    public void Fraction_is_zero_when_max_is_not_positive()
    {
        var state = State();

        state.OnDamage(7, 20f, 10f, 0f, 1, P(), 0.0);

        Assert.Equal(0f, Only(state).Fraction);
    }

    // ---- Numbers ----

    [Fact]
    public void Damage_spawns_one_number_carrying_the_delta_type_and_position()
    {
        var state = State();

        state.OnDamage(7, 100f, 82.5f, 100f, damageType: 3, position: P(4f, 5f, 6f), now: 12.0);

        var number = Assert.Single(state.Numbers);
        Assert.Equal(7, number.ActorId);
        Assert.Equal(17.5f, number.Amount, 5);
        Assert.Equal(3, number.DamageType);
        Assert.Equal(4f, number.Position.X);
        Assert.Equal(5f, number.Position.Y);
        Assert.Equal(6f, number.Position.Z);
        Assert.Equal(12.0, number.SpawnedAt);
    }

    [Theory]
    [InlineData(0.0, 0f, 1f, 0f)]
    [InlineData(0.5, 0.5f, 1f, 20f)]
    [InlineData(0.6, 0.6f, 1f, 24f)]
    [InlineData(0.8, 0.8f, 0.5f, 32f)]
    [InlineData(1.0, 1f, 0f, 40f)]
    [InlineData(2.0, 1f, 0f, 40f)]
    public void Progress_alpha_and_drift_follow_the_age(double age, float progress, float alpha, float drift)
    {
        var state = State();
        state.OnDamage(7, 100f, 90f, 100f, 1, P(), 10.0);
        var number = Assert.Single(state.Numbers);

        double now = 10.0 + age;

        Assert.Equal(progress, number.Progress(now), 5);
        Assert.Equal(alpha, number.Alpha(now), 5);
        Assert.Equal(drift, number.Drift(now), 5);
    }

    [Fact]
    public void Tick_drops_expired_numbers_and_keeps_younger_ones()
    {
        var state = State();
        state.OnDamage(7, 100f, 90f, 100f, 1, P(), 0.0);
        state.OnDamage(9, 100f, 90f, 100f, 1, P(), 0.6);

        state.Tick(1.0);

        Assert.Equal(9, Assert.Single(state.Numbers).ActorId);
    }

    [Fact]
    public void Tick_does_not_touch_the_tracked_set()
    {
        var state = State();
        state.OnDamage(7, 100f, 90f, 100f, 1, P(), 0.0);

        state.Tick(99.0);

        Assert.Equal(7, Only(state).Id);
    }

    [Fact]
    public void Events_inside_the_merge_window_sum_into_one_number()
    {
        var state = State();
        state.OnDamage(7, 100f, 90f, 100f, 1, P(1f, 1f, 1f), 0.0);

        state.OnDamage(7, 90f, 85f, 100f, 1, P(2f, 2f, 2f), 0.3);

        var number = Assert.Single(state.Numbers);
        Assert.Equal(15f, number.Amount, 5);
        Assert.Equal(0.3, number.SpawnedAt);
        Assert.Equal(2f, number.Position.X);
    }

    [Fact]
    public void A_third_event_merges_against_the_reset_spawn_time()
    {
        var state = State();
        state.OnDamage(7, 100f, 90f, 100f, 1, P(), 0.0);
        state.OnDamage(7, 90f, 85f, 100f, 1, P(), 0.4);

        state.OnDamage(7, 85f, 80f, 100f, 1, P(), 0.8);

        var number = Assert.Single(state.Numbers);
        Assert.Equal(20f, number.Amount, 5);
        Assert.Equal(0.8, number.SpawnedAt);
    }

    [Fact]
    public void An_event_outside_the_window_spawns_a_second_number()
    {
        var state = State();
        state.OnDamage(7, 100f, 90f, 100f, 1, P(), 0.0);

        state.OnDamage(7, 90f, 85f, 100f, 1, P(), 0.9);

        Assert.Equal(2, state.Numbers.Count);
        Assert.Equal(new[] { 10f, 5f }, state.Numbers.Select(n => n.Amount).ToArray());
    }

    [Fact]
    public void A_different_damage_type_spawns_a_second_number()
    {
        var state = State();
        state.OnDamage(7, 100f, 90f, 100f, damageType: 1, position: P(), now: 0.0);

        state.OnDamage(7, 90f, 85f, 100f, damageType: 2, position: P(), now: 0.1);

        Assert.Equal(2, state.Numbers.Count);
    }

    [Fact]
    public void A_different_actor_spawns_a_second_number()
    {
        var state = State();
        state.OnDamage(7, 100f, 90f, 100f, 1, P(), 0.0);

        state.OnDamage(9, 100f, 95f, 100f, 1, P(), 0.1);

        Assert.Equal(2, state.Numbers.Count);
    }

    [Fact]
    public void A_number_is_not_critical_by_default()
    {
        var state = State();

        state.OnDamage(7, 100f, 90f, 100f, 1, P(), 0.0);

        Assert.False(Assert.Single(state.Numbers).Critical);
    }

    [Fact]
    public void A_critical_hit_spawns_a_critical_number()
    {
        var state = State();

        state.OnDamage(7, 100f, 90f, 100f, 1, P(), 0.0, critical: true);

        Assert.True(Assert.Single(state.Numbers).Critical);
    }

    [Fact]
    public void A_critical_hit_merged_into_a_normal_number_marks_it_critical()
    {
        var state = State();
        state.OnDamage(7, 100f, 90f, 100f, 1, P(), 0.0);

        state.OnDamage(7, 90f, 70f, 100f, 1, P(), 0.2, critical: true);

        var number = Assert.Single(state.Numbers);
        Assert.True(number.Critical);
        Assert.Equal(30f, number.Amount, 5);
    }

    [Fact]
    public void A_normal_hit_merged_into_a_critical_number_keeps_it_critical()
    {
        var state = State();
        state.OnDamage(7, 100f, 80f, 100f, 1, P(), 0.0, critical: true);

        state.OnDamage(7, 80f, 75f, 100f, 1, P(), 0.2);

        Assert.True(Assert.Single(state.Numbers).Critical);
    }

    [Fact]
    public void A_zero_merge_window_never_merges()
    {
        var state = State(mergeWindow: 0f);
        state.OnDamage(7, 100f, 90f, 100f, 1, P(), 0.0);

        state.OnDamage(7, 90f, 85f, 100f, 1, P(), 0.0);

        Assert.Equal(2, state.Numbers.Count);
    }
}
