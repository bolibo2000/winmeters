using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace WinMeters.Services;

internal sealed class HotkeyService : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Action _onHotkeyPressed;
    private string _hotkeySpec;
    private AppSettings? _currentSettings;
    private bool _registered;
    private bool _disposed;

    /// <summary>
    /// Canonical fallback chord. Used as the safe-default when <see cref="ParseHotkeyString"/>
    /// can't make sense of the configured string (empty input, only-modifier tokens,
    /// unrecognized multi-char keys, etc.). Matches the historical hard-coded path so the
    /// existing registration call still goes through with the same combo (WinMeters'
    /// published hotkey ever since the Ctrl+Alt+Shift+M default was hard-coded).
    /// </summary>
    public const string DefaultHotkeyString = "Ctrl+Alt+Shift+M";

    /// <summary>
    /// Default modifier set used by <see cref="ParseHotkeyString"/> when a token list has
    /// at least one key but no modifier tokens. NOT zero — Windows' RegisterHotKey silently
    /// no-ops on a zero modifier + VK combo on most desktop SKUs, so a non-modifier hotkey
    /// must default to at least one of Ctrl/Alt/Shift/Win. We pick the full Ctrl+Alt+Shift
    /// mask (NOT just Ctrl) so a bare-key input like "F12" transparently maps to the
    /// documented default chord Ctrl+Alt+Shift+F12 rather than the much narrower Ctrl+F12,
    /// preserving the historical "always require the full canonical modifier mask" behavior
    /// that the JSON-side <see cref="DefaultHotkeyString"/> exposes.
    /// </summary>
    internal const uint DefaultFallbackModifiers =
        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT;

    /// <summary>
    /// Default virtual-key code used by <see cref="ParseHotkeyString"/> when the token
    /// list contains no usable VK token (parse failure). Matches the historical
    /// hard-coded VK_M so a regression to the safe-default re-registers the same chord.
    /// </summary>
    internal const uint DefaultFallbackVk = NativeMethods.VK_M;

    /// <summary>
    /// Single source of truth for friendly-name VK resolution. The parser looks up
    /// <see cref="NamedKeyCodes"/> for multi-char tokens (e.g. "F12", "Space"), and
    /// <see cref="FriendlyKeyNames"/> is the inverse used by <see cref="FormatChord"/>'s
    /// <c>FormatVirtualKey</c> helper. Adding a row here automatically extends both the
    /// parseable token set and the formatter's friendly-name rendering — there is no
    /// way for the two sides to drift apart because they're built from the same array
    /// at type-init time.
    ///
    /// <para>
    /// The set is curated to the common PC hotkey chord vocabulary: function row
    /// (F1–F12) plus a handful of editor-style specials. We deliberately do NOT
    /// include the full ~250-entry Win32 VK table here — anything outside this set
    /// falls through to the hex-literal path so the operator at least sees a concrete
    /// value rather than an empty label.
    /// </para>
    /// </summary>
    private static readonly (string Name, uint Vk)[] NamedVkPairs =
    {
        ("F1",        0x70),
        ("F2",        0x71),
        ("F3",        0x72),
        ("F4",        0x73),
        ("F5",        0x74),
        ("F6",        0x75),
        ("F7",        0x76),
        ("F8",        0x77),
        ("F9",        0x78),
        ("F10",       0x79),
        ("F11",       0x7A),
        ("F12",       0x7B),
        ("Space",     0x20),
        ("Tab",       0x09),
        ("Enter",     0x0D),  // VK_RETURN (regular Enter on the main keyboard)
        ("NumpadEnter", 0x0E), // VK_NUMPAD_ENTER (distinct chord from regular Enter — both map to NamedVkPairs so a chord like Ctrl+NumpadEnter is unambiguously distinct from Ctrl+Enter)
        ("Esc",       0x1B),
        ("Backspace", 0x08),
        ("Up",        0x26),
        ("Down",      0x28),
        ("Left",      0x25),
        ("Right",     0x27),
        ("PageUp",    0x21),
        ("PageDown",  0x22),
        ("Home",      0x24),
        ("End",       0x23),
        ("Insert",         0x2D),
        ("Delete",         0x2E),
        // Media / browser controls (VK_BROWSER_BACK / FORWARD, VK_VOLUME_MUTE /
        // DOWN / UP, VK_MEDIA_PLAY_PAUSE — discrete codes in the 0xA6–0xB3 range
        // of the Win32 VK table; gaps at 0xA8–0xAC aren't assigned).
        ("BrowserBack",    0xA6),
        ("BrowserForward", 0xA7),
        ("VolumeMute",     0xAD),
        ("VolumeDown",     0xAE),
        ("VolumeUp",       0xAF),
        ("MediaPlayPause", 0xB3),
        // Locks, snapshot & numpad cluster. VK_CAPITAL = 0x14 (CapsLock),
        // VK_SNAPSHOT = 0x2C (PrintScreen), VK_NUMLOCK = 0x90, VK_SCROLL = 0x91,
        // VK_NUMPAD0–NUMPAD9 = 0x60–0x69, VK_MULTIPLY / ADD / SEPARATOR /
        // SUBTRACT / DECIMAL / DIVIDE = 0x6A–0x6F (the numpad operator row).
        ("CapsLock",       0x14),
        ("PrintScreen",    0x2C),
        ("NumLock",        0x90),
        ("ScrollLock",     0x91),
        ("Numpad0",        0x60),
        ("Numpad1",        0x61),
        ("Numpad2",        0x62),
        ("Numpad3",        0x63),
        ("Numpad4",        0x64),
        ("Numpad5",        0x65),
        ("Numpad6",        0x66),
        ("Numpad7",        0x67),
        ("Numpad8",        0x68),
        ("Numpad9",        0x69),
        ("Multiply",       0x6A),
        ("Add",            0x6B),
        ("Separator",      0x6C),
        ("Subtract",       0x6D),
        ("Decimal",        0x6E),
        ("Divide",         0x6F),
    };

    private static readonly Dictionary<string, uint> NamedKeyCodes =
        NamedVkPairs.ToDictionary(p => p.Name, p => p.Vk, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<uint, string> FriendlyKeyNames =
        NamedVkPairs.ToDictionary(p => p.Vk, p => p.Name);

    /// <summary>
    /// Raised after a <see cref="NativeMethods.RegisterHotKey"/> call returned false,
    /// typically because another OS / app process already owns the same chord. The
    /// payload is a one-line human-readable description of the chord the caller just
    /// attempted to register; <see cref="MainWindow"/> subscribes once during initial
    /// tray-icon setup and surfaces a one-shot balloon-tip warning so the user can see
    /// that the JSON-saved Hotkey setting isn't actually active. Handlers run on the
    /// thread that invoked <see cref="Register"/> / <see cref="ReRegister"/> —
    /// currently the WPF dispatcher thread (via <c>OnSourceInitialized</c>) — so a
    /// handler can touch UI state without a thread-marshal.
    /// </summary>
    public event Action<string>? RegisterFailed;

    public HotkeyService(IntPtr hwnd, Action onHotkeyPressed, string hotkey = DefaultHotkeyString)
    {
        _hwnd = hwnd;
        _onHotkeyPressed = onHotkeyPressed ?? throw new ArgumentNullException(nameof(onHotkeyPressed));
        _hotkeySpec = string.IsNullOrWhiteSpace(hotkey) ? DefaultHotkeyString : hotkey;
    }

    public IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == Constants.Hotkey.HotkeyId)
        {
            _onHotkeyPressed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Register()
    {
        if (_registered || _disposed) return;
        (uint fsModifiers, uint vk) chord = ParseHotkeyString(_hotkeySpec);
        try
        {
            _registered = NativeMethods.RegisterHotKey(
                _hwnd, Constants.Hotkey.HotkeyId,
                chord.fsModifiers, chord.vk);
            if (!_registered)
            {
                int err = Marshal.GetLastWin32Error();
                WinMeters.Log.D($"HotkeyService: RegisterHotKey failed for '{_hotkeySpec}' (resolved to {FormatChord(chord.fsModifiers, chord.vk)} / mods=0x{chord.fsModifiers:X4}, vk=0x{chord.vk:X2}, error {err}).");
                // Notify UI so the user sees their saved Hotkey setting isn't active.
                // Re-entrancy note: a handler that itself triggers a settings reload could
                // re-enter this Register path. We invoke outside the try/catch so the
                // handler's exception bubbles to the caller; Register itself stayed
                // contract-clean (no re-entered Register).
                try { RegisterFailed?.Invoke($"Hotkey '{_hotkeySpec}' could not be registered (another app owns the chord). Last Win32 error: {err}."); }
                catch (Exception ex) { WinMeters.Log.D($"HotkeyService.Register: RegisterFailed handler threw: {ex}"); }
            }
        }
        catch (Exception ex) { WinMeters.Log.D($"HotkeyService.Register: {ex}"); }
    }

    public void Unregister()
    {
        if (!_registered || _disposed) return;
        try { NativeMethods.UnregisterHotKey(_hwnd, Constants.Hotkey.HotkeyId); _registered = false; }
        catch (Exception ex) { WinMeters.Log.D($"HotkeyService.Unregister: {ex}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
    }

    /// <summary>
    /// Refreshes the captured settings reference used by <see cref="ReRegister"/> so
    /// the hotkey chord tracks <paramref name="settings"/> without requiring the
    /// caller to dispose/recreate the service. Mirrors the
    /// <see cref="AppBarService.BindSettings"/> / <see cref="WindowPlacementService.BindSettings"/>
    /// pattern in <see cref="MainWindow.ApplySettings"/>.
    /// </summary>
    public void BindSettings(AppSettings settings)
    {
        _currentSettings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Unregisters the current chord and re-registers with the latest
    /// <c>_currentSettings.General.Hotkey</c> value. Used after the settings dialog
    /// closes (or after a JSON edit while paused) so chord changes take effect
    /// without restarting the whole app.
    /// </summary>
    public void ReRegister()
    {
        Unregister();
        var spec = _currentSettings?.General?.Hotkey;
        _hotkeySpec = string.IsNullOrWhiteSpace(spec) ? DefaultHotkeyString : spec!;
        Register();
    }

    /// <summary>
    /// Internal accessor exposed so the test project (which compiles this source via
    /// &lt;Compile Include&gt;) can assert that a chord swap propagated through the
    /// service. NOT part of the public surface.
    /// </summary>
    internal string CurrentSpec => _hotkeySpec;

    /// <summary>
    /// Distinct outcomes from <see cref="ParseInternal"/>. The three values mirror the
    /// three label states that <c>SettingsWindow.UpdateHotkeyStatus</c> can render —
    /// UI and parser agree by construction because both consume <see cref="ParseInternal"/>.
    /// </summary>
    private enum ParseResult
    {
        /// <summary>
        /// Input was null, empty, or only symbols that the splitter yielded zero
        /// tokens for. Both chord fields hold the canonical fallback so the UI can
        /// still format a label; the label itself uses the "(empty → using default)"
        /// variant.
        /// </summary>
        EmptyInput,
        /// <summary>
        /// Input parsed cleanly: at least one VK token was consumed and no trailing
        /// token was flagged unrecognized. UI renders the resolved chord.
        /// </summary>
        CleanParse,
        /// <summary>
        /// Input had content but didn't yield a usable VK chord (bare modifiers,
        /// unknown key token, extra VK by "first wins"). UI renders the warning
        /// label. Note: in this case the chord itself is still a valid (mods, vk)
        /// pair — the fallback — so a caller can render it without a second parse.
        /// </summary>
        Invalid,
    }

    /// <summary>
    /// Internal parser used by both <see cref="ParseHotkeyString"/> and
    /// <see cref="TryParseHotkeyString"/>. Returns the canonical (mods, vk) tuple
    /// (always non-garbage thanks to the fallback values) plus a
    /// <see cref="ParseResult"/> that distinguishes empty input from a clean
    /// parse from an invalid parse. Single source of truth so the two public
    /// methods can't drift apart on what "successfully parsed" means.
    /// </summary>
    private static (uint mods, uint vk, ParseResult result) ParseInternal(string? hotkey, bool logWarnings)
    {
        uint mods = DefaultFallbackModifiers;
        uint vk = DefaultFallbackVk;

        if (string.IsNullOrWhiteSpace(hotkey))
        {
            if (logWarnings)
                WinMeters.Log.D($"HotkeyService.ParseHotkeyString: empty input, using fallback '{DefaultHotkeyString}'.");
            return (mods, vk, ParseResult.EmptyInput);
        }

        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            if (logWarnings)
                WinMeters.Log.D($"HotkeyService.ParseHotkeyString: no tokens in '{hotkey}', using fallback.");
            return (mods, vk, ParseResult.EmptyInput);
        }

        uint parsedMods = 0;
        bool modsConsumed = false;
        uint parsedVk = 0;
        bool vkConsumed = false;
        int? unrecognizedIndex = null;

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    parsedMods |= NativeMethods.MOD_CONTROL;
                    modsConsumed = true;
                    break;
                case "alt":
                    parsedMods |= NativeMethods.MOD_ALT;
                    modsConsumed = true;
                    break;
                case "shift":
                    parsedMods |= NativeMethods.MOD_SHIFT;
                    modsConsumed = true;
                    break;
                case "win":
                    parsedMods |= NativeMethods.MOD_WIN;
                    modsConsumed = true;
                    break;
                default:
                    if (vkConsumed)
                    {
                        // Multiple VK tokens (e.g. "Ctrl+M+F12") — only honor the first,
                        // but record this so we don't silently pick the first.
                        unrecognizedIndex = i;
                    }
                    else if (part.Length == 1)
                    {
                        // Single char → the literal VK code (Ctrl+M = 'M' = 0x4D).
                        parsedVk = (uint)char.ToUpperInvariant(part[0]);
                        vkConsumed = true;
                    }
                    else
                    {
                        // Multi-character token. Consult NamedKeyCodes first so common
                        // chord notations like "Ctrl+Shift+F12" or "Alt+Space" resolve
                        // cleanly instead of silently falling through to VK_M. Tokens
                        // NOT in the map (e.g. "Junk", "F13", "FooBar") still surface
                        // in the rolling log so unexpected config values are visible
                        // without scanning every code path on the next debug session.
                        if (NamedKeyCodes.TryGetValue(part, out var namedCode))
                        {
                            parsedVk = namedCode;
                            vkConsumed = true;
                        }
                        else
                        {
                            unrecognizedIndex = i;
                        }
                    }
                    break;
            }
        }

        if (unrecognizedIndex is int idx)
        {
            if (logWarnings)
            WinMeters.Log.D(
                $"HotkeyService.ParseHotkeyString: unrecognized key token '{parts[idx]}' " +
                $"in '{hotkey}', using fallback VK_M. Recognized tokens: Ctrl|Alt|Shift|Win, " +
                "named keys (F1–F12, Space, Tab, Enter, NumpadEnter, Esc, Backspace, CapsLock, " +
                "Up/Down/Left/Right, PageUp/PageDown, Home/End, Insert/Delete, PrintScreen, " +
                "NumLock/ScrollLock, Numpad0–Numpad9, Multiply/Add/Subtract/Decimal/Divide, " +
                "BrowserBack/BrowserForward, VolumeMute/VolumeDown/VolumeUp, MediaPlayPause), " +
                "and any single character (resolved by its VK code).");
        }

        // Modifiers-only path (e.g. "Ctrl+Shift", "Alt+Win") would silently turn into
        // Ctrl+Shift+M via the fallback VK — that's a UX trap because the user sees the
        // bar toggle on Alt+M (or whichever fallback VK collides next). Warn loudly so a
        // bare-modifier config surfaces in the rolling error log the next time someone
        // debugs "why is my hotkey triggering M?" without scanning every code path.
        if (modsConsumed && !vkConsumed)
        {
            if (logWarnings)
                WinMeters.Log.D(
                    $"HotkeyService.ParseHotkeyString: '{hotkey}' resolved to modifier list with no virtual-key token; " +
                    $"falling back to fallback VK (0x{DefaultFallbackVk:X2}). Add a single-character key token " +
                    "(e.g. \"Ctrl+Shift+M\") to register a non-collision-prone chord.");
        }

        if (modsConsumed) mods = parsedMods;
        if (vkConsumed)   vk = parsedVk;

        // "Did this parse cleanly?" — flag any path that left us with fallback VK or
        // flagged any token so the UI can render a warning. Both !vkConsumed (e.g.
        // "Ctrl+Shift") and unrecognizedIndex.HasValue (e.g. "Ctrl+Junk", "Ctrl+F+G")
        // collapse into the same Invalid bucket — the UI doesn't need to distinguish
        // between "no key token" and "extra key tokens" to render a warning.
        var parseResult = (!vkConsumed || unrecognizedIndex.HasValue)
            ? ParseResult.Invalid
            : ParseResult.CleanParse;

        return (mods, vk, parseResult);
    }

    /// <summary>
    /// Maps the parsed <c>[Mods+]Key</c> token list (e.g. <c>"Ctrl+Shift+F12"</c>) to a
    /// <c>(fsModifiers, vk)</c> pair suitable for <see cref="NativeMethods.RegisterHotKey"/>.
    /// On parse failure (empty input, modifier-only token list, multiple VK tokens,
    /// unrecognized multi-char key), logs a warning via <see cref="WinMeters.Log"/> and
    /// returns the canonical <c>(Ctrl+Alt+Shift, VK_M)</c> fallback.
    ///
    /// <para>
    /// <see cref="NativeMethods"/> exposes only individual <c>VK_*</c> int constants
    /// (e.g. <c>VK_M = 0x4D</c>), not a full VirtualKey enum, so the parser resolves
    /// single-character keys by their literal UTF-16 code unit (<c>'M' → 0x4D</c>),
    /// mirrors how every documented WinMeters / kil0bit-style hotkey chord is written
    /// (<c>"Ctrl+Shift+M"</c>, <c>"Alt+Shift+H"</c>). Multi-character tokens are
    /// consulted against <see cref="NamedKeyCodes"/> first so common chord notations
    /// like <c>"Ctrl+Shift+F12"</c>, <c>"Alt+Up"</c>, or <c>"Ctrl+PageDown"</c>
    /// resolve cleanly via the same single-source-of-truth FriendlyKeyNames map.
    /// </para>
    /// </summary>
    /// <param name="hotkey">Chord string. Modifier names: <c>Ctrl|Control</c>,
    /// <c>Alt</c>, <c>Shift</c>, <c>Win</c> (case-insensitive). Trailing key token
    /// is either a single-character key (resolved to its VK code) or a named
    /// <c>VK_*</c> identifier (resolved via <see cref="NamedKeyCodes"/>).
    /// Whitespace around tokens is tolerated.</param>
    /// <returns>Resolved chord. Always returns a valid (non-zero, well-formed) pair;
    /// the safe-default is returned on failure so <see cref="NativeMethods.RegisterHotKey"/>
    /// still receives a (mods=0x0007, vk=VK_M) chord rather than nothing. Use
    /// <see cref="TryParseHotkeyString"/> instead if you need to distinguish empty
    /// input from a real parse failure.</returns>
    public static (uint fsModifiers, uint vk) ParseHotkeyString(string? hotkey, bool logWarnings = true)
    {
        var (mods, vk, _) = ParseInternal(hotkey, logWarnings);
        return (mods, vk);
    }

    /// <summary>
    /// Try-pattern variant of <see cref="ParseHotkeyString"/>. Returns true iff
    /// <see cref="ParseInternal"/> reports <see cref="ParseResult.CleanParse"/>.
    /// The <paramref name="chord"/> out always receives a usable (mods, vk) value
    /// (the canonical fallback on failure) so the caller can render it in a
    /// warning UI without doing a second parse — especially useful for the
    /// SettingsWindow status label which needs both the resolved chord AND the
    /// parse-outcome bool to render its three label states (empty / clean / invalid).
    /// </summary>
    public static bool TryParseHotkeyString(string? hotkey, out (uint fsModifiers, uint vk) chord, bool logWarnings = true)
    {
        var (mods, vk, result) = ParseInternal(hotkey, logWarnings);
        chord = (mods, vk);
        return result == ParseResult.CleanParse;
    }

    /// <summary>
    /// Renders a <c>(fsModifiers, vk)</c> chord as the canonical WinMeters chord string,
    /// e.g. <c>(0x07, 0x4D) → "Ctrl+Alt+Shift+M"</c>. Used by the SettingsWindow status
    /// label and by the <see cref="RegisterFailed"/> payload so the operator sees a
    /// string they can read AND paste back into the JSON / config UI.
    ///
    /// <para>
    /// Printable VK range covers digits 0–9 (<c>0x30–0x39</c>), uppercase letters A–Z
    /// (<c>0x41–0x5A</c>), and a couple specials (Space, Tab, Enter, Esc) which the
    /// routine names explicitly. Anything else falls back to the literal hex code so
    /// the operator at least sees a concrete value rather than an undefined character.
    /// Mods are appended in the standard Ctrl+Alt+Shift+Win order so the rendered chord
    /// matches what the user typed (modifiers may appear in any order in the input but
    /// display order is canonical).
    /// </para>
    /// </summary>
    public static string FormatChord(uint fsModifiers, uint vk)
    {
        var parts = new List<string>(5);
        if ((fsModifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((fsModifiers & NativeMethods.MOD_ALT) != 0)     parts.Add("Alt");
        if ((fsModifiers & NativeMethods.MOD_SHIFT) != 0)   parts.Add("Shift");
        if ((fsModifiers & NativeMethods.MOD_WIN) != 0)     parts.Add("Win");
        parts.Add(FormatVirtualKey(vk));
        return string.Join("+", parts);
    }

    private static string FormatVirtualKey(uint vk)
    {
        // Named VK friendly names (covers F1-F12 + Space/Tab/Enter/Esc/Backspace).
        // Lookup uses the inverse of the parser's NamedKeyCodes map so the renderer
        // is always consistent with what the parser can produce — any future row
        // added to NamedVkPairs shows up here automatically, no parallel switch
        // statement to keep in sync.
        if (FriendlyKeyNames.TryGetValue(vk, out var friendly))
            return friendly;
        // Printable ASCII letter / digit. ascii 0x30–0x39 = "0"–"9", 0x41–0x5A = "A"–"Z".
        // We surface these as their literal uppercase character because that's how the
        // user writes the chord in JSON. Lowercase letters round-trip via char.ToUpperInvariant
        // inside ParseHotkeyString so the resolvable set is uppercase here.
        if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A))
            return ((char)vk).ToString();
        return $"Vk0x{vk:X2}";
    }
}
