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
    /// take effect immediately - the menu holds no separate copy of the "real" values except the
    /// raw text currently being typed into a field (see _textBuffers). "Save to config file"
    /// copies the current live values back into their BepInEx ConfigEntry so they survive a
    /// restart too; without it, changes only last until the game closes.
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

        private bool _visible;
        private bool _loggedDrawError;
        private bool _loggedChromeError;
        private Vector2 _scroll;
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
        private GUIStyle _textFieldStyle;
        private GUIStyle _backgroundStyle;
        private GUIStyle _updateStyle;

        // Raw text currently being typed per field, keyed by a stable per-field id. Deliberately
        // NOT re-derived from the live value every frame (that fights the user mid-keystroke on
        // any partial/invalid state, e.g. an empty field or a bare "-") - only seeded once, the
        // first time a field is drawn after the menu opens, and cleared when the menu closes so
        // reopening picks up whatever the current real values are.
        private readonly Dictionary<string, string> _textBuffers = new Dictionary<string, string>();

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
            if (keyboard == null || !keyboard.f9Key.wasPressedThisFrame)
            {
                return;
            }

            _visible = !_visible;
            _textBuffers.Clear();

            // ChaosController.IsMenuOpen checks Cursor.lockState as a heuristic for "some menu is
            // open" - freeing the cursor here both lets the streamer actually click this menu
            // (it'd otherwise be invisible/unclickable while the cursor is locked during normal
            // play) and, as a side effect, makes chaos effects correctly hold while this menu is
            // open too, same as any other menu. Restored to whatever it was on close.
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

            _textFieldStyle = new GUIStyle(GUI.skin?.textField) { fontSize = 12 };
            _textFieldStyle.normal.textColor = Color.white;
            _textFieldStyle.normal.background = controlBackground;
        }

        private void OnGUI()
        {
            if (!_visible || _chaos == null)
            {
                return;
            }

            EnsureStyles();

            // Plain GUI.Box + GUILayout.BeginArea rather than GUILayout.Window - the latter needs
            // a GUI.WindowFunction callback, which this project's IL2CPP interop-generated
            // UnityEngine.IMGUIModule can't construct from a plain C# method group (fails to
            // convert even when explicitly wrapped in `new GUI.WindowFunction(...)`, apparently
            // expecting an IL2CPP-native constructor instead). This loses drag-to-move, but the
            // panel is otherwise fully usable at its fixed position.
            //
            // Background and title are each wrapped in their own try/catch: IL2CPP strips the
            // native implementation of some legacy IMGUI overloads in this build (GUI.DrawTexture
            // was one - confirmed via a live "Method unstripping failed" trampoline exception), and
            // an uncaught exception here happens BEFORE the try/finally below, so it would abort
            // the entire OnGUI call for the frame - including EndArea, unbalancing GUILayout's
            // internal state for every subsequent frame too.
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
                GUILayout.BeginArea(new Rect(_windowRect.x + 6, _windowRect.y + 26, _windowRect.width - 12, _windowRect.height - 32));
                DrawPanel();
            }
            catch (Exception e)
            {
                // Doesn't rely on the game's own GUISkin for anything above (explicit background +
                // colors), so if content still doesn't render this is almost certainly a real bug
                // in DrawPanel rather than a skin/contrast issue - logged once (OnGUI runs multiple
                // times per frame) rather than flooding the log every frame it stays broken.
                if (!_loggedDrawError)
                {
                    _loggedDrawError = true;
                    _log?.LogError($"ModMenu: DrawPanel threw - panel content will stay blank until this is fixed: {e}");
                }
            }
            finally
            {
                try
                {
                    GUILayout.EndArea();
                }
                catch (Exception e)
                {
                    if (!_loggedChromeError)
                    {
                        _loggedChromeError = true;
                        _log?.LogError($"ModMenu: EndArea threw: {e}");
                    }
                }
            }
        }

        private void DrawPanel()
        {
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Width(460), GUILayout.Height(560));

            DrawUpdateBanner();

            GUILayout.Label("Changes apply immediately. Twitch credentials aren't editable here - see the .cfg file for those.", _labelStyle);
            if (GUILayout.Button("Save current settings to config file (survive a restart)", _buttonStyle))
            {
                _saveToConfig?.Invoke();
            }

            GUILayout.Space(10);
            GUILayout.Label("Chaos commands", _headerStyle);
            foreach (var action in _actionOrder)
            {
                DrawActionRow(action);
            }

            GUILayout.Space(10);
            GUILayout.Label("Effect tuning", _headerStyle);
            LabeledFloat("Invert duration (s)", "invertDuration", () => _chaos.InvertDurationSeconds, v => _chaos.InvertDurationSeconds = v);
            LabeledFloat("No-jump duration (s)", "noJumpDuration", () => _chaos.NoJumpDurationSeconds, v => _chaos.NoJumpDurationSeconds = v);
            LabeledFloat("Poop lifetime (s)", "poopLifetime", () => _chaos.PoopLifetimeSeconds, v => _chaos.PoopLifetimeSeconds = v);
            LabeledFloat("Yeet up force", "yeetUp", () => _chaos.YeetUpForce, v => _chaos.YeetUpForce = v);
            LabeledFloat("Yeet sideways force", "yeetSide", () => _chaos.YeetSidewaysForce, v => _chaos.YeetSidewaysForce = v);
            LabeledFloat("Ragdoll up force", "ragdollUp", () => _chaos.RagdollUpForce, v => _chaos.RagdollUpForce = v);
            LabeledFloat("Ragdoll sideways force", "ragdollSide", () => _chaos.RagdollSidewaysForce, v => _chaos.RagdollSidewaysForce = v);
            LabeledFloat("Add money amount", "addMoneyAmt", () => _chaos.AddMoneyAmount, v => _chaos.AddMoneyAmount = v);
            LabeledFloat("Remove money amount", "removeMoneyAmt", () => _chaos.RemoveMoneyAmount, v => _chaos.RemoveMoneyAmount = v);
            LabeledFloat("Earthquake up force", "eqUp", () => _chaos.EarthquakeRagdollUpForce, v => _chaos.EarthquakeRagdollUpForce = v);
            LabeledFloat("Earthquake sideways force", "eqSide", () => _chaos.EarthquakeRagdollSidewaysForce, v => _chaos.EarthquakeRagdollSidewaysForce = v);
            LabeledFloat("Gravity duration (s)", "gravityDuration", () => _chaos.GravityDurationSeconds, v => _chaos.GravityDurationSeconds = v);
            LabeledFloat("Gravity low multiplier", "gravityLow", () => _chaos.GravityLowMultiplier, v => _chaos.GravityLowMultiplier = v);
            LabeledFloat("Gravity high multiplier", "gravityHigh", () => _chaos.GravityHighMultiplier, v => _chaos.GravityHighMultiplier = v);
            LabeledFloat("Fire sale duration (s)", "fireSaleDuration", () => _chaos.FireSaleDurationSeconds, v => _chaos.FireSaleDurationSeconds = v);

            GUILayout.Space(10);
            GUILayout.Label("Economy", _headerStyle);
            _points.PassiveIncomeEnabled = GUILayout.Toggle(_points.PassiveIncomeEnabled, "Passive income enabled", _toggleStyle);
            LabeledInt("Passive income amount", "passiveAmt", () => _points.PassiveIncomeAmount, v => _points.PassiveIncomeAmount = v);
            LabeledInt("Passive income interval (s)", "passiveInterval", () => _points.PassiveIncomeIntervalSeconds, v => _points.PassiveIncomeIntervalSeconds = v);
            LabeledInt("Starting balance - viewer", "startViewer", () => _router.StartingBalanceViewer, v => _router.StartingBalanceViewer = v);
            LabeledInt("Starting balance - follower", "startFollower", () => _router.StartingBalanceFollower, v => _router.StartingBalanceFollower = v);
            LabeledInt("Starting balance - VIP/mod", "startVipMod", () => _router.StartingBalanceVipMod, v => _router.StartingBalanceVipMod = v);
            LabeledInt("Sub points per tier", "subPoints", () => _router.SubscriberPointsPerTier, v => _router.SubscriberPointsPerTier = v);
            LabeledInt("Gift sub points per tier", "giftPoints", () => _router.GiftedSubPointsPerTier, v => _router.GiftedSubPointsPerTier = v);
            LabeledInt("Points per bit", "bitsRatio", () => _router.BitsToPointsRatio, v => _router.BitsToPointsRatio = v);
            LabeledInt("Extra Life points per cent donated", "extraLifeRatio", () => _extraLifeTracker.CentsToPointsRatio, v => _extraLifeTracker.CentsToPointsRatio = v);

            GUILayout.Space(10);
            GUILayout.Label("Chat vote polls", _headerStyle);
            LabeledFloat("Poll duration (s)", "pollDuration", () => _pollManager.PollDurationSeconds, v => _pollManager.PollDurationSeconds = v);
            LabeledFloat("Auto poll interval (min, 0 = off)", "pollAutoMinutes", () => _pollManager.AutoIntervalSeconds / 60f, v => _pollManager.AutoIntervalSeconds = v * 60f);
            LabeledInt("Poll option count", "pollOptions", () => _pollManager.OptionCount, v => _pollManager.OptionCount = v);

            GUILayout.Space(10);
            GUILayout.Label("Features", _headerStyle);
            _chaos.HoldEffectsWhileMenuOpen = GUILayout.Toggle(_chaos.HoldEffectsWhileMenuOpen, "Hold chaos effects while a menu is open", _toggleStyle);
            var overlayOn = GUILayout.Toggle(_overlay.IsRunning, "OBS overlay server enabled (port needs a restart to change)", _toggleStyle);
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

            GUILayout.EndScrollView();
        }

        private void DrawUpdateBanner()
        {
            if (_updateChecker == null || _updateChecker.CheckStatus != UpdateChecker.Status.UpdateAvailable)
            {
                return;
            }

            GUILayout.Label($"Update available: {_updateChecker.LatestVersionText}", _updateStyle);

            switch (_updateChecker.InstallStatus)
            {
                case UpdateChecker.Install.Idle:
                    if (_updateChecker.CanInstall)
                    {
                        if (GUILayout.Button("Install update (finishes next time you close the game)", _buttonStyle))
                        {
                            _updateChecker.BeginInstall();
                        }
                    }
                    else if (GUILayout.Button("Open releases page to download manually", _buttonStyle))
                    {
                        OpenUrl(_updateChecker.ReleaseUrl);
                    }
                    break;
                case UpdateChecker.Install.Downloading:
                    GUILayout.Label("Downloading update...", _labelStyle);
                    break;
                case UpdateChecker.Install.Staged:
                    GUILayout.Label("Staged - close the game normally to finish installing.", _labelStyle);
                    break;
                case UpdateChecker.Install.Failed:
                    GUILayout.Label($"Install failed: {_updateChecker.InstallError}", _labelStyle);
                    if (GUILayout.Button("Retry install", _buttonStyle))
                    {
                        _updateChecker.BeginInstall();
                    }
                    break;
            }

            GUILayout.Space(10);
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
            GUILayout.BeginHorizontal();

            var enabled = !_router.DisabledActions.Contains(action);
            var newEnabled = GUILayout.Toggle(enabled, "", _toggleStyle, GUILayout.Width(20));
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

            GUILayout.Label(action, _labelStyle, GUILayout.Width(120));
            GUILayout.Label("cost:", _labelStyle, GUILayout.Width(35));
            _router.Prices[action] = IntField($"price_{action}", _router.Prices[action]);

            GUILayout.EndHorizontal();
        }

        private void LabeledFloat(string label, string key, Func<float> getter, Action<float> setter)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _labelStyle, GUILayout.Width(230));
            setter(FloatField(key, getter()));
            GUILayout.EndHorizontal();
        }

        private void LabeledInt(string label, string key, Func<int> getter, Action<int> setter)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _labelStyle, GUILayout.Width(230));
            setter(IntField(key, getter()));
            GUILayout.EndHorizontal();
        }

        /// <summary>Text field bound to a per-field raw-text buffer rather than the live value
        /// directly - see _textBuffers' doc comment for why. Applies the parsed value back to the
        /// caller-supplied setter (via LabeledFloat) on every keystroke that parses cleanly;
        /// invalid/partial text (e.g. mid-edit) just doesn't push a new value yet.</summary>
        private float FloatField(string key, float currentValue)
        {
            if (!_textBuffers.TryGetValue(key, out var text))
            {
                text = currentValue.ToString(Invariant);
            }

            var newText = GUILayout.TextField(text, _textFieldStyle, GUILayout.Width(70));
            _textBuffers[key] = newText;

            return float.TryParse(newText, NumberStyles.Float, Invariant, out var parsed) ? parsed : currentValue;
        }

        private int IntField(string key, int currentValue)
        {
            if (!_textBuffers.TryGetValue(key, out var text))
            {
                text = currentValue.ToString(Invariant);
            }

            var newText = GUILayout.TextField(text, _textFieldStyle, GUILayout.Width(70));
            _textBuffers[key] = newText;

            return int.TryParse(newText, NumberStyles.Integer, Invariant, out var parsed) ? parsed : currentValue;
        }
    }
}
