using System;
using System.Collections.Generic;
using Game.Actors;
using Game.Data.Combat;
using Game.Engine;
using Game.Missions;
using UnityEngine;

namespace CombatText;

// The overlay. An injected MonoBehaviour on a DontDestroyOnLoad host, drawing with IMGUI so the mod
// needs no prefab, canvas or font asset. It owns no state: every frame it reads the tracked actors
// and the live numbers back out of CombatTextTracker.
public class CombatTextDrawer : MonoBehaviour
{
    public CombatTextDrawer(IntPtr ptr) : base(ptr) { }

    // Sizes at 1080p. Every draw multiplies them by the current scale, so the overlay keeps the same
    // apparent size on a 4K screen.
    private const float BaseBarWidth = 48f;
    private const float BaseBarHeight = 6f;
    private const float BaseArmorHeight = 4f;
    private const float BaseArmorGap = 1f;
    private const float BaseLabelWidth = 60f;
    private const float BaseLabelHeight = 20f;
    private const int BaseFontSize = 14;
    private const float ReferenceHeight = 1080f;

    private static readonly Color Orange = new Color(1f, 0.55f, 0.1f);
    private static readonly Color BarBackground = new Color(0f, 0f, 0f, 0.7f);
    private static readonly Color ArmorBlue = new Color(0.3f, 0.6f, 1f);

    // Reused across frames so the per-frame draw allocates nothing.
    private static readonly List<int> Stale = new List<int>();
    private static readonly List<ArmorPartHealth> ArmorParts = new List<ArmorPartHealth>();

    private static GUIStyle _numberStyle;
    private static GUIStyle _criticalStyle;
    private static int _numberStyleFontSize;
    private static float _gameScale = 1f;
    private static float _gameScaleNextRead = -1f;
    private static bool _gameScaleLogged;
    private static bool _gateWarned;
    private static bool _drawWarned;
    private static float _lastCountLog;

    public void OnGUI()
    {
        if (!Plugin.ShowBars.Value && !Plugin.ShowNumbers.Value)
        {
            return;
        }
        // IMGUI calls OnGUI several times per frame (layout, input, repaint). Drawing belongs to the
        // repaint pass only.
        var current = Event.current;
        if (current == null || current.type != EventType.Repaint)
        {
            return;
        }
        if (!TryGates(out var cam))
        {
            return;
        }

        try
        {
            Draw(cam);
        }
        catch (Exception e)
        {
            if (!_drawWarned)
            {
                _drawWarned = true;
                Plugin.Log.LogWarning($"CombatText draw failed (logged once): {e}");
            }
        }
        GUI.color = Color.white;
    }

    // Pause, mission state and camera. A read that throws skips the frame and is reported once, not
    // every frame.
    private static bool TryGates(out Camera cam)
    {
        cam = null;
        try
        {
            if (TimeManager.IsPaused)
            {
                return false;
            }
            var mission = MissionController.Active;
            if (mission == null || mission.State != MissionController.ControllerState.Running)
            {
                return false;
            }
            cam = Camera.main;
            return cam != null;
        }
        catch (Exception e)
        {
            if (!_gateWarned)
            {
                _gateWarned = true;
                Plugin.Log.LogWarning($"CombatText gate read failed (logged once): {e}");
            }
            return false;
        }
    }

    private static void Draw(Camera cam)
    {
        var state = CombatTextTracker.State;
        float now = Time.time;
        state.Tick(now);
        float scale = GameScale(now) * Plugin.Scale.Value;
        if (scale <= 0f)
        {
            scale = 1f;
        }

        if (Plugin.ShowBars.Value)
        {
            CombatTextTracker.PollArmor(now);
            DrawBars(cam, state, scale);
        }
        if (Plugin.ShowNumbers.Value)
        {
            DrawNumbers(cam, state, now, scale);
        }

        if (Plugin.Verbose.Value && now - _lastCountLog >= 1f)
        {
            int tracked = state.Tracked.Count;
            int numbers = state.Numbers.Count;
            if (tracked > 0 || numbers > 0)
            {
                _lastCountLog = now;
                Plugin.Log.LogDebug($"CombatText: {tracked} tracked, {numbers} numbers.");
            }
        }
    }

    private static void DrawBars(Camera cam, CombatTextState state, float scale)
    {
        float barWidth = BaseBarWidth * scale;
        float barHeight = BaseBarHeight * scale;
        Stale.Clear();
        foreach (var tracked in state.Tracked)
        {
            var actor = CombatTextTracker.Resolve(tracked.Id);
            // WasCollected is checked before the Unity null operator, which would touch a collected
            // pointer. A dead or gone actor leaves the state after the loop, not during it.
            if (actor is null || actor.WasCollected || actor == null || actor.IsDead)
            {
                Stale.Add(tracked.Id);
                continue;
            }

            // An actor the game is not rendering (culled, or hidden) must not leak its position.
            var view = actor.View;
            if (view != null && !view.RenderersEnabled)
            {
                continue;
            }

            Vector3 screen = cam.WorldToScreenPoint(actor.ChestPosition + Vector3.up * Plugin.BarOffset.Value);
            if (screen.z <= 0f)
            {
                continue;
            }

            float x = screen.x - barWidth * 0.5f;
            float y = Screen.height - screen.y;
            float fraction = tracked.Fraction;

            GUI.color = BarBackground;
            GUI.DrawTexture(new Rect(x, y, barWidth, barHeight), Texture2D.whiteTexture);
            GUI.color = FillColor(fraction);
            GUI.DrawTexture(new Rect(x, y, barWidth * fraction, barHeight), Texture2D.whiteTexture);

            DrawArmor(actor, x, y + barHeight + BaseArmorGap * scale, barWidth, scale);
        }
        GUI.color = Color.white;

        for (int i = 0; i < Stale.Count; i++)
        {
            CombatTextTracker.Forget(Stale[i]);
        }
        Stale.Clear();
    }

    // One segment per armor part under the health bar. The parts break in array order, and the
    // player reads the armor like the health bar (loss from the right), so the first part in the
    // array sits at the right edge. Every part keeps its dark background, so the player still sees
    // how many pieces the zombie had; an intact part fills blue by its own health, a broken one
    // stays empty.
    private static void DrawArmor(ZombieActor actor, float x, float y, float barWidth, float scale)
    {
        ArmorParts.Clear();
        CombatTextTracker.ReadArmor(actor, ArmorParts);
        int count = ArmorParts.Count;
        if (count == 0)
        {
            return;
        }

        float gap = BaseArmorGap * scale;
        float height = BaseArmorHeight * scale;
        float width = (barWidth - gap * (count - 1)) / count;
        for (int i = 0; i < count; i++)
        {
            float segmentX = x + (count - 1 - i) * (width + gap);
            GUI.color = BarBackground;
            GUI.DrawTexture(new Rect(segmentX, y, width, height), Texture2D.whiteTexture);

            var part = ArmorParts[i];
            if (part.Max <= 0f || part.Current <= 0f)
            {
                continue;
            }
            float fraction = Mathf.Clamp01(part.Current / part.Max);
            GUI.color = ArmorBlue;
            GUI.DrawTexture(new Rect(segmentX, y, width * fraction, height), Texture2D.whiteTexture);
        }
    }

    private static void DrawNumbers(Camera cam, CombatTextState state, float now, float scale)
    {
        var numbers = state.Numbers;
        EnsureNumberStyles(Mathf.Max(1, Mathf.RoundToInt(BaseFontSize * scale)));
        float labelWidth = BaseLabelWidth * scale;
        float labelHeight = BaseLabelHeight * scale;
        float shadow = Mathf.Max(1f, scale);
        for (int i = 0; i < numbers.Count; i++)
        {
            var number = numbers[i];
            Vector3 screen = cam.WorldToScreenPoint(new Vector3(number.Position.X, number.Position.Y, number.Position.Z));
            if (screen.z <= 0f)
            {
                continue;
            }

            float x = screen.x - labelWidth * 0.5f;
            float y = Screen.height - screen.y - number.Drift(now) * scale;
            var rect = new Rect(x, y, labelWidth, labelHeight);
            string text = Mathf.RoundToInt(number.Amount).ToString("F0");
            float alpha = number.Alpha(now);
            var style = number.Critical ? _criticalStyle : _numberStyle;

            // A black copy offset down and right keeps the number readable over bright ground.
            GUI.color = new Color(0f, 0f, 0f, alpha);
            GUI.Label(new Rect(x + shadow, y + shadow, labelWidth, labelHeight), text, style);

            Color colour = TypeColour(number.DamageType);
            GUI.color = new Color(colour.r, colour.g, colour.b, alpha);
            GUI.Label(rect, text, style);
        }
        GUI.color = Color.white;
    }

    // The game's own HUD scale: the Canvas.scaleFactor of a canvas driven by UICanvasSetup, which
    // folds the resolution and the user's UI Scale setting together. Re-read every two seconds, so
    // a settings change or a scene load is picked up without a scene search per frame. When no
    // such canvas is loaded, fall back to plain screen-height scaling.
    private static float GameScale(float now)
    {
        if (now < _gameScaleNextRead)
        {
            return _gameScale;
        }
        _gameScaleNextRead = now + 2f;

        float fallback = Screen.height / ReferenceHeight;
        float found = 0f;
        try
        {
            var setups = UnityEngine.Object.FindObjectsOfType<global::UI.UICanvasSetup>();
            for (int i = 0; i < setups.Length; i++)
            {
                var setup = setups[i];
                if (setup == null || setup.m_IgnoreUserScaleSetting)
                {
                    continue;
                }
                var canvas = setup.GetComponent<Canvas>();
                if (canvas != null && canvas.scaleFactor > 0f)
                {
                    found = canvas.scaleFactor;
                    break;
                }
            }
        }
        catch (Exception e)
        {
            if (!_gameScaleLogged)
            {
                _gameScaleLogged = true;
                Plugin.Log.LogWarning($"CombatText could not read the HUD scale (logged once): {e.Message}");
            }
        }

        _gameScale = found > 0f ? found : fallback;
        if (Plugin.Verbose.Value && !_gameScaleLogged)
        {
            _gameScaleLogged = true;
            Plugin.Log.LogDebug($"CombatText: HUD scale {(found > 0f ? "from canvas" : "fallback")} {_gameScale} (screen {Screen.width}x{Screen.height}, UIScale setting {Game.Data.Settings.GameSettingsCache.UIScale}).");
        }
        return _gameScale;
    }

    private static Color FillColor(float fraction)
    {
        if (fraction > 0.5f)
        {
            return Color.green;
        }
        return fraction > 0.25f ? Color.yellow : Color.red;
    }

    // Fire is the one type worth telling apart at a glance, because its numbers accumulate over
    // ticks rather than per hit. Every other type shares one colour.
    private static Color TypeColour(int damageType) =>
        (DamageType)damageType == DamageType.Fire ? Orange : Color.white;

    // GUI.skin is only valid inside OnGUI, so the styles are built on the first draw, not at load,
    // and rebuilt only when the scaled font size changes (a resolution or config change). Regular
    // hits use the normal weight; a critical hit is the bold one.
    private static void EnsureNumberStyles(int fontSize)
    {
        if (_numberStyle != null && _numberStyleFontSize == fontSize)
        {
            return;
        }
        _numberStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.MiddleCenter,
        };
        _criticalStyle = new GUIStyle(_numberStyle)
        {
            fontStyle = FontStyle.Bold,
        };
        _numberStyleFontSize = fontSize;
    }
}
