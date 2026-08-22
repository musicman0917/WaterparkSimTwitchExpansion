using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.InputSystem;
using WaterparkSimTwitchExpansion.Chaos;
using WaterparkSimTwitchExpansion.Economy;

namespace WaterparkSimTwitchExpansion.Core
{
    /// <summary>
    /// In-game settings panel, toggled with F9 - lets the streamer enable/disable individual
    /// chaos commands and live-tune prices, effect strengths, economy amounts, poll settings, and
    /// a couple of feature toggles, all without editing the .cfg file and restarting the game.
    ///
    /// Built with Unity's legacy IMGUI (OnGUI), the same approach OnScreenNotifier already uses -
    /// there's no clean way to build a real uGUI/Canvas-based menu from a BepInEx IL2CPP mod
    /// without the game's own UI prefabs, and IMGUI needs nothing from the game at all.
    ///
    /// Every field here edits a live property on the actual runtime object (ChaosController,
    /// ChaosCommandRouter, PointsManager, ChaosPollManager, OverlayServer) directly, so changes
    /// take effect immediately - the menu holds no separate copy of the "real" values at all.
    /// Numeric values use -/+ stepper buttons rather than a typed text field (see StepInt/
    /// StepFloat's doc comment for why). "Save to config file" copies the current live values back
    /// into their BepInEx ConfigEntry so they survive a restart too; without it, changes only last
    /// until the game closes.
    ///
    /// Deliberately does NOT expose Twitch credentials (channel/bot/OAuth/Client ID, follower
    /// check credentials) - those need a restart to reconnect anyway, and putting an OAuth token
    /// on screen during a live stream is a real risk of it ending up in a clip/screenshot.
    /// Overlay port and autosave interval are similarly left config-file-only (minor, restart-only
    /// plumbing, not worth the extra menu surface).
    /// </summary>
    public sealed class ModMenu : MonoBehaviour
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private ManualLogSource _log;
        private ChaosController _chaos;
        private ChaosCommandRouter _router;
        private PointsManager _points;
        private ChaosPollManager _pollManager;
        private OverlayServer _overlay;
        private UpdateChecker _updateChecker;
        private ExtraLifeDonationTracker _extraLifeTracker;
        private Action _saveToConfig;
        private string[] _actionOrder;

        private const float RowSpacing = 4f;
        private const float ScrollWheelSpeed = 0.4f; // Raw Input System scroll units are ~120/notch on Windows - unverified/tunable once live.

        private bool _visible;
        private bool _loggedDrawError;
        private bool _loggedChromeError;
        private readonly HashSet<string> _loggedControlErrors = new HashSet<string>();

        // Manual layout state for the current OnGUI call - see the big comment on OnGUI/DrawPanel
        // for why this exists instead of GUILayout's automatic layout.
        private Rect _contentRect;
        private float _cursorY;
        private float _scrollY;

        private Rect _windowRect = new Rect(40, 40, 480, 620);

        // Explicit solid background + white text, same reasoning as OnScreenNotifier's _style -
        // this game doesn't otherwise use legacy IMGUI at all, so its built-in GUISkin (box/button/
        // toggle backgrounds, default text color) can't be assumed to look like anything sane. A
        // bare GUI.Box/GUILayout.Label with no explicit style rendered as an outline with no
        // visible content in an early live test - this panel doesn't rely on the default skin for
        // anything, it draws its own background and colors everything explicitly instead.
        private Texture2D _panelBackground;
        private GUIStyle _titleStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _toggleStyle;
        private GUIStyle _valueStyle;
        private GUIStyle _backgroundStyle;
        private GUIStyle _updateStyle;

        // IL2CPP constructs every injected MonoBehaviour from a native pointer, never the
        // parameterless .ctor - same requirement as OnScreenNotifier/UpdatePump.
        public ModMenu(IntPtr ptr) : base(ptr)
        {
        }

        /// <summary>Called once by Plugin.Load() right after AddComponent&lt;ModMenu&gt;() - IL2CPP
        /// injected types can't take extra constructor arguments, so wiring happens here instead.</summary>
        public void Init(
            ManualLogSource log,
            ChaosController chaos,
            ChaosCommandRouter router,
            PointsManager points,
            ChaosPollManager pollManager,
            OverlayServer overlay,
            UpdateChecker updateChecker,
            ExtraLifeDonationTracker extraLifeTracker,
            Action saveToConfig)
        {
            _log = log;
            _chaos = chaos;
            _router = router;
            _points = points;
            _pollManager = pollManager;
            _overlay = overlay;
            _updateChecker = updateChecker;
            _extraLifeTracker = extraLifeTracker;
            _saveToConfig = saveToConfig;

            var actions = new string[router.Prices.Count];
            router.Prices.Keys.CopyTo(actions, 0);
            _actionOrder = actions;
        }

        private void Update()
        {
            // Keyboard.current (new Input System), NOT UnityEngine.Input - see the csproj's
            // Unity.InputSystem reference comment for why: this game reads input exclusively
            // through the new Input System, which usually means Player Settings has the legacy
            // Input Manager disabled outright (reading UnityEngine.Input would throw, not just
            // silently do nothing).
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f9Key.wasPressedThisFrame)
            {
                _visible = !_visible;
                _scrollY = 0f;

                // ChaosController.IsMenuOpen checks Cursor.lockState as a heuristic for "some menu
                // is open" - freeing the cursor here both lets the streamer actually click this
                // menu (it'd otherwise be invisible/unclickable while the cursor is locked during
                // normal play) and, as a side effect, makes chaos effects correctly hold while
                // this menu is open too, same as any other menu. Restored to whatever it was on
                // close.
                if (_visible)
                {
                    _previousCursorLockState = Cursor.lockState;
                    _previousCursorVisible = Cursor.visible;
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                else
                {
                    Cursor.lockState = _previousCursorLockState;
                    Cursor.visible = _previousCursorVisible;
                }
            }

            // No GUI.BeginScrollView/GUILayout.BeginScrollView here (see OnGUI's doc comment) -
            // scrolling is done manually instead, so it needs its own input read every frame the
            // menu is open, not just on the F9 toggle frame.
            if (_visible)
            {
                var mouse = Mouse.current;
                var delta = mouse?.scroll.ReadValue().y ?? 0f;
                if (delta != 0f)
                {
                    _scrollY -= delta * ScrollWheelSpeed;
                    if (_scrollY < 0f)
                    {
                        _scrollY = 0f;
                    }
                    // Upper bound is clamped in DrawPanel once the actual content height for this
                    // frame is known.
                }
            }
        }

        private CursorLockMode _previousCursorLockState;
        private bool _previousCursorVisible;

        private void EnsureStyles()
        {
            if (_panelBackground != null)
            {
                return;
            }

            _panelBackground = new Texture2D(1, 1);
            _panelBackground.SetPixel(0, 0, new Color(0.05f, 0.05f, 0.08f, 0.92f));
            _panelBackground.Apply();

            var controlBackground = new Texture2D(1, 1);
            controlBackground.SetPixel(0, 0, new Color(0.2f, 0.2f, 0.25f, 1f));
            controlBackground.Apply();

            _titleStyle = new GUIStyle { fontStyle = FontStyle.Bold, fontSize = 15, alignment = TextAnchor.MiddleCenter };
            _titleStyle.normal.textColor = Color.white;

            // IL2CPP strips GUI.DrawTexture(Rect, Texture) in this build (the game itself never
            // calls it), so the panel background is drawn via GUI.Label(Rect, string, GUIStyle)
            // instead - the same overload shape already proven to survive stripping elsewhere.
            _backgroundStyle = new GUIStyle { normal = { background = _panelBackground } };

            _labelStyle = new GUIStyle { fontSize = 12 };
            _labelStyle.normal.textColor = Color.white;
            _labelStyle.wordWrap = true;

            _headerStyle = new GUIStyle(_labelStyle) { fontStyle = FontStyle.Bold, fontSize = 14 };

            _updateStyle = new GUIStyle(_labelStyle) { fontStyle = FontStyle.Bold };
            _updateStyle.normal.textColor = new Color(1f, 0.8f, 0.2f);

            _buttonStyle = new GUIStyle(GUI.skin?.button) { fontSize = 12 };
            _buttonStyle.normal.textColor = Color.white;
            _buttonStyle.normal.background = controlBackground;
            _buttonStyle.hover.textColor = Color.white;
            _buttonStyle.active.textColor = Color.white;

            _toggleStyle = new GUIStyle(GUI.skin?.toggle);
            _toggleStyle.normal.textColor = Color.white;
            _toggleStyle.onNormal.textColor = Color.white;

            // Values are shown as a plain centered label between -/+ buttons rather than an
            // editable text field - see StepInt/StepFloat's doc comment for why (GUI.TextField
            // needs GUIStateObjects.GetStateObject internally, which is ALSO stripped in this
            // build - confirmed live, the third distinct stripped IMGUI method found this way).
            _valueStyle = new GUIStyle(_labelStyle) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
        }

        private void OnGUI()
        {
            if (!_visible || _chaos == null)
            {
                return;
            }

            EnsureStyles();

            // Plain GUI.Box-style background rather than GUILayout.Window - the latter needs a
            // GUI.WindowFunction callback, which this project's IL2CPP interop-generated
            // UnityEngine.IMGUIModule can't construct from a plain C# method group. This loses
            // drag-to-move, but the panel is otherwise fully usable at its fixed position.
            //
            // NO GUILayout ANYWHERE in this file, and no GUI.TextField either, on purpose - two
            // live tests found two more IL2CPP-stripped methods on top of GUI.DrawTexture:
            // GUILayout.BeginArea (which took the ENTIRE panel blank, since its failure meant the
            // matching GUILayout.EndArea then threw "Stack empty" too) and GUI.TextField (needs
            // GUIStateObjects.GetStateObject internally for per-control cursor/selection state,
            // also stripped). Stateless controls - GUI.Label/Button/Toggle - all confirmed working
            // live. Given the game itself uses zero legacy IMGUI, there's no way to know in advance
            // which exact overloads survived stripping - so every control is drawn with an
            // explicit Rect via plain GUI.* calls (see NextRect/Row helpers), manually laid out and
            // manually scrolled (see Update()'s mouse-wheel handling), numeric values use -/+
            // stepper buttons instead of a text field (see StepInt/StepFloat), and EACH control is
            // wrapped in its own try/catch (SafeLabel/SafeButton/SafeToggle below) so one more
            // stripped method only blanks that ONE row instead of the whole panel again.
            try
            {
                GUI.Label(_windowRect, string.Empty, _backgroundStyle);
            }
            catch (Exception e)
            {
                if (!_loggedChromeError)
                {
                    _loggedChromeError = true;
                    _log?.LogError($"ModMenu: panel background draw threw - continuing without it: {e}");
                }
            }

            try
            {
                GUI.Label(new Rect(_windowRect.x, _windowRect.y + 4, _windowRect.width, 20), "Waterpark Twitch Expansion - Settings (F9 to close)", _titleStyle);
            }
            catch (Exception e)
            {
                if (!_loggedChromeError)
                {
                    _loggedChromeError = true;
                    _log?.LogError($"ModMenu: title draw threw - continuing without it: {e}");
                }
            }

            try
            {
                DrawPanel(new Rect(_windowRect.x + 6, _windowRect.y + 26, _windowRect.width - 12, _windowRect.height - 32));
            }
            catch (Exception e)
            {
                // Only reached for something NOT covered by an individual control's own try/catch
                // below (e.g. _actionOrder itself throwing) - logged once rather than flooding the
                // log every frame it stays broken.
                if (!_loggedDrawError)
                {
                    _loggedDrawError = true;
                    _log?.LogError($"ModMenu: DrawPanel threw - panel content will stay blank until this is fixed: {e}");
                }
            }
        }

        /// <summary>Lays out every control top-to-bottom with manually-tracked Rects (see OnGUI's
        /// doc comment for why) into <paramref name="contentRect"/>, offset by _scrollY. Rows
        /// entirely outside contentRect are skipped rather than drawn and left to bleed past the
        /// window's edges, since there's no GUI.BeginGroup/clip available to rely on either - a row
        /// right at the boundary can still slightly overflow, accepted as a minor cosmetic
        /// trade-off rather than another untested API dependency.</summary>
        private void DrawPanel(Rect contentRect)
        {
            _contentRect = contentRect;
            _cursorY = 0f;

            DrawUpdateBanner();

            var introText = "Changes apply immediately. Twitch credentials aren't editable here - see the .cfg file for those.";
            SafeLabel("intro", NextRect(34), introText, _labelStyle);
            if (SafeButton("saveConfig", NextRect(24), "Save current settings to config file (survive a restart)", _buttonStyle))
            {
                _saveToConfig?.Invoke();
            }

            Space(10);
            SafeLabel("hdrChaos", NextRect(20), "Chaos commands", _headerStyle);
            foreach (var action in _actionOrder)
            {
                DrawActionRow(action);
            }

            Space(10);
            SafeLabel("hdrEffects", NextRect(20), "Effect tuning", _headerStyle);
            LabeledFloat("Invert duration (s)", "invertDuration", () => _chaos.InvertDurationSeconds, v => _chaos.InvertDurationSeconds = v, 1f);
            LabeledFloat("No-jump duration (s)", "noJumpDuration", () => _chaos.NoJumpDurationSeconds, v => _chaos.NoJumpDurationSeconds = v, 1f);
            LabeledFloat("Poop lifetime (s)", "poopLifetime", () => _chaos.PoopLifetimeSeconds, v => _chaos.PoopLifetimeSeconds = v, 5f);
            LabeledFloat("Yeet up force", "yeetUp", () => _chaos.YeetUpForce, v => _chaos.YeetUpForce = v, 10f);
            LabeledFloat("Yeet sideways force", "yeetSide", () => _chaos.YeetSidewaysForce, v => _chaos.YeetSidewaysForce = v, 10f);
            LabeledFloat("Ragdoll up force", "ragdollUp", () => _chaos.RagdollUpForce, v => _chaos.RagdollUpForce = v, 10f);
            LabeledFloat("Ragdoll sideways force", "ragdollSide", () => _chaos.RagdollSidewaysForce, v => _chaos.RagdollSidewaysForce = v, 10f);
            LabeledFloat("Add money amount", "addMoneyAmt", () => _chaos.AddMoneyAmount, v => _chaos.AddMoneyAmount = v, 25f);
            LabeledFloat("Remove money amount", "removeMoneyAmt", () => _chaos.RemoveMoneyAmount, v => _chaos.RemoveMoneyAmount = v, 25f);
            LabeledFloat("Earthquake up force", "eqUp", () => _chaos.EarthquakeRagdollUpForce, v => _chaos.EarthquakeRagdollUpForce = v, 10f);
            LabeledFloat("Earthquake sideways force", "eqSide", () => _chaos.EarthquakeRagdollSidewaysForce, v => _chaos.EarthquakeRagdollSidewaysForce = v, 10f);
            LabeledFloat("Gravity duration (s)", "gravityDuration", () => _chaos.GravityDurationSeconds, v => _chaos.GravityDurationSeconds = v, 1f);
            LabeledFloat("Gravity low multiplier", "gravityLow", () => _chaos.GravityLowMultiplier, v => _chaos.GravityLowMultiplier = v, 0.1f, 0.05f);
            LabeledFloat("Gravity high multiplier", "gravityHigh", () => _chaos.GravityHighMultiplier, v => _chaos.GravityHighMultiplier = v, 0.1f, 0.05f);
            LabeledFloat("Fire sale duration (s)", "fireSaleDuration", () => _chaos.FireSaleDurationSeconds, v => _chaos.FireSaleDurationSeconds = v, 5f);

            Space(10);
            SafeLabel("hdrEconomy", NextRect(20), "Economy", _headerStyle);
            _points.PassiveIncomeEnabled = SafeToggle("passiveEnabled", NextRect(20), _points.PassiveIncomeEnabled, "Passive income enabled", _toggleStyle);
            LabeledInt("Passive income amount", "passiveAmt", () => _points.PassiveIncomeAmount, v => _points.PassiveIncomeAmount = v, 1);
            LabeledInt("Passive income interval (s)", "passiveInterval", () => _points.PassiveIncomeIntervalSeconds, v => _points.PassiveIncomeIntervalSeconds = v, 5);
            LabeledInt("Starting balance - viewer", "startViewer", () => _router.StartingBalanceViewer, v => _router.StartingBalanceViewer = v, 25);
            LabeledInt("Starting balance - follower", "startFollower", () => _router.StartingBalanceFollower, v => _router.StartingBalanceFollower = v, 25);
            LabeledInt("Starting balance - VIP/mod", "startVipMod", () => _router.StartingBalanceVipMod, v => _router.StartingBalanceVipMod = v, 25);
            LabeledInt("Sub points per tier", "subPoints", () => _router.SubscriberPointsPerTier, v => _router.SubscriberPointsPerTier = v, 25);
            LabeledInt("Gift sub points per tier", "giftPoints", () => _router.GiftedSubPointsPerTier, v => _router.GiftedSubPointsPerTier = v, 25);
            LabeledInt("Points per bit", "bitsRatio", () => _router.BitsToPointsRatio, v => _router.BitsToPointsRatio = v, 1);
            LabeledInt("Extra Life points per cent donated", "extraLifeRatio", () => _extraLifeTracker.CentsToPointsRatio, v => _extraLifeTracker.CentsToPointsRatio = v, 1);

            Space(10);
            SafeLabel("hdrPolls", NextRect(20), "Chat vote polls", _headerStyle);
            LabeledFloat("Poll duration (s)", "pollDuration", () => _pollManager.PollDurationSeconds, v => _pollManager.PollDurationSeconds = v, 5f);
            LabeledFloat("Auto poll interval (min, 0 = off)", "pollAutoMinutes", () => _pollManager.AutoIntervalSeconds / 60f, v => _pollManager.AutoIntervalSeconds = v * 60f, 1f);
            LabeledInt("Poll option count", "pollOptions", () => _pollManager.OptionCount, v => _pollManager.OptionCount = v, 1, 2);

            Space(10);
            SafeLabel("hdrFeatures", NextRect(20), "Features", _headerStyle);
            _chaos.HoldEffectsWhileMenuOpen = SafeToggle("holdEffects", NextRect(20), _chaos.HoldEffectsWhileMenuOpen, "Hold chaos effects while a menu is open", _toggleStyle);
            var overlayOn = SafeToggle("overlayOn", NextRect(20), _overlay.IsRunning, "OBS overlay server enabled (port needs a restart to change)", _toggleStyle);
            if (overlayOn != _overlay.IsRunning)
            {
                if (overlayOn)
                {
                    _overlay.Start();
                }
                else
                {
                    _overlay.Stop();
                }
            }

            // The full content height is only known now that every row above has advanced
            // _cursorY - clamp the scroll offset Update() applied earlier this frame against it.
            var maxScroll = Mathf.Max(0f, _cursorY - contentRect.height);
            if (_scrollY > maxScroll)
            {
                _scrollY = maxScroll;
            }
        }

        private void DrawUpdateBanner()
        {
            if (_updateChecker == null || _updateChecker.CheckStatus != UpdateChecker.Status.UpdateAvailable)
            {
                return;
            }

            SafeLabel("updateBanner", NextRect(20), $"Update available: {_updateChecker.LatestVersionText}", _updateStyle);

            switch (_updateChecker.InstallStatus)
            {
                case UpdateChecker.Install.Idle:
                    if (_updateChecker.CanInstall)
                    {
                        if (SafeButton("installUpdate", NextRect(24), "Install update (finishes next time you close the game)", _buttonStyle))
                        {
                            _updateChecker.BeginInstall();
                        }
                    }
                    else if (SafeButton("openReleasesPage", NextRect(24), "Open releases page to download manually", _buttonStyle))
                    {
                        OpenUrl(_updateChecker.ReleaseUrl);
                    }
                    break;
                case UpdateChecker.Install.Downloading:
                    SafeLabel("updateDownloading", NextRect(20), "Downloading update...", _labelStyle);
                    break;
                case UpdateChecker.Install.Staged:
                    SafeLabel("updateStaged", NextRect(20), "Staged - close the game normally to finish installing.", _labelStyle);
                    break;
                case UpdateChecker.Install.Failed:
                    SafeLabel("updateFailed", NextRect(20), $"Install failed: {_updateChecker.InstallError}", _labelStyle);
                    if (SafeButton("retryInstall", NextRect(24), "Retry install", _buttonStyle))
                    {
                        _updateChecker.BeginInstall();
                    }
                    break;
            }

            Space(10);
        }

        // Best-effort only - worst case the streamer just reads the URL from the log instead.
        private static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
            }
        }

        private void DrawActionRow(string action)
        {
            var row = NextRect(22);
            var toggleRect = new Rect(row.x, row.y, 20, row.height);
            var nameRect = new Rect(row.x + 24, row.y, 120, row.height);
            var costLabelRect = new Rect(row.x + 148, row.y, 35, row.height);
            var priceRect = new Rect(row.x + 186, row.y, 110, row.height);

            var enabled = !_router.DisabledActions.Contains(action);
            var newEnabled = SafeToggle($"actionEnabled_{action}", toggleRect, enabled, string.Empty, _toggleStyle);
            if (newEnabled != enabled)
            {
                if (newEnabled)
                {
                    _router.DisabledActions.Remove(action);
                }
                else
                {
                    _router.DisabledActions.Add(action);
                }
            }

            SafeLabel($"actionName_{action}", nameRect, action, _labelStyle);
            SafeLabel($"actionCostLabel_{action}", costLabelRect, "cost:", _labelStyle);
            _router.Prices[action] = StepInt($"price_{action}", priceRect, _router.Prices[action], 10, 0);
        }

        private void LabeledFloat(string label, string key, Func<float> getter, Action<float> setter, float step, float min = 0f)
        {
            var row = NextRect(22);
            var labelRect = new Rect(row.x, row.y, 190, row.height);
            var fieldRect = new Rect(row.x + 196, row.y, 120, row.height);

            SafeLabel($"label_{key}", labelRect, label, _labelStyle);
            setter(StepFloat(key, fieldRect, getter(), step, min));
        }

        private void LabeledInt(string label, string key, Func<int> getter, Action<int> setter, int step, int min = 0)
        {
            var row = NextRect(22);
            var labelRect = new Rect(row.x, row.y, 190, row.height);
            var fieldRect = new Rect(row.x + 196, row.y, 120, row.height);

            SafeLabel($"label_{key}", labelRect, label, _labelStyle);
            setter(StepInt(key, fieldRect, getter(), step, min));
        }

        /// <summary>Renders as "[-] value [+]" rather than an editable text field - a live test
        /// found GUI.TextField needs GUIStateObjects.GetStateObject internally to cache per-control
        /// cursor/selection state, and that's stripped in this build too (the third distinct
        /// stripped IMGUI method found this way, after GUI.DrawTexture and GUILayout.BeginArea) -
        /// while stateless controls (GUI.Label/Button/Toggle) all confirmed working live. Buttons
        /// step by a fixed amount tuned per field rather than allowing arbitrary typed input.</summary>
        private int StepInt(string id, Rect rect, int value, int step, int min)
        {
            if (SafeButton($"{id}_minus", new Rect(rect.x, rect.y, 24, rect.height), "-", _buttonStyle))
            {
                value = Math.Max(min, value - step);
            }

            SafeLabel($"{id}_value", new Rect(rect.x + 26, rect.y, rect.width - 52, rect.height), value.ToString(Invariant), _valueStyle);

            if (SafeButton($"{id}_plus", new Rect(rect.xMax - 24, rect.y, 24, rect.height), "+", _buttonStyle))
            {
                value += step;
            }

            return value;
        }

        private float StepFloat(string id, Rect rect, float value, float step, float min)
        {
            if (SafeButton($"{id}_minus", new Rect(rect.x, rect.y, 24, rect.height), "-", _buttonStyle))
            {
                value = Mathf.Max(min, value - step);
            }

            SafeLabel($"{id}_value", new Rect(rect.x + 26, rect.y, rect.width - 52, rect.height), value.ToString("0.##", Invariant), _valueStyle);

            if (SafeButton($"{id}_plus", new Rect(rect.xMax - 24, rect.y, 24, rect.height), "+", _buttonStyle))
            {
                value += step;
            }

            return value;
        }

        // --- Manual layout + per-control safety net (see OnGUI's doc comment) -------------------

        /// <summary>Advances the layout cursor by <paramref name="height"/> (plus RowSpacing) and
        /// returns the absolute screen Rect for a row of that height, positioned within
        /// _contentRect and offset by the current scroll amount.</summary>
        private Rect NextRect(float height)
        {
            var localY = _cursorY;
            _cursorY += height + RowSpacing;
            return new Rect(_contentRect.x, _contentRect.y + localY - _scrollY, _contentRect.width, height);
        }

        private void Space(float amount)
        {
            _cursorY += amount;
        }

        /// <summary>True if any part of <paramref name="rect"/> falls within _contentRect - used
        /// to skip drawing (and thus calling into a possibly-stripped IMGUI method for no reason)
        /// rows that have been scrolled fully out of view.</summary>
        private bool IsVisible(Rect rect) => rect.yMax >= _contentRect.y && rect.y <= _contentRect.yMax;

        private void LogControlError(string id, Exception e)
        {
            if (_loggedControlErrors.Add(id))
            {
                _log?.LogError($"ModMenu: control '{id}' draw threw - it will stay blank/inert rather than break the rest of the panel: {e}");
            }
        }

        private void SafeLabel(string id, Rect rect, string text, GUIStyle style)
        {
            if (!IsVisible(rect))
            {
                return;
            }

            try
            {
                GUI.Label(rect, text, style);
            }
            catch (Exception e)
            {
                LogControlError(id, e);
            }
        }

        private bool SafeButton(string id, Rect rect, string text, GUIStyle style)
        {
            if (!IsVisible(rect))
            {
                return false;
            }

            try
            {
                return GUI.Button(rect, text, style);
            }
            catch (Exception e)
            {
                LogControlError(id, e);
                return false;
            }
        }

        private bool SafeToggle(string id, Rect rect, bool value, string text, GUIStyle style)
        {
            if (!IsVisible(rect))
            {
                return value;
            }

            try
            {
                return GUI.Toggle(rect, value, text, style);
            }
            catch (Exception e)
            {
                LogControlError(id, e);
                return value;
            }
        }
    }
}
