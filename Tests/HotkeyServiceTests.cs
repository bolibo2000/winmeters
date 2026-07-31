using Xunit;
using WinMeters.Services;

namespace WinMeters.Tests;

/// <summary>
/// Covers <see cref="HotkeyService.ParseHotkeyString"/>.
/// HotkeyService is compiled into this project via &lt;Compile Include&gt; in the test
/// csproj so the internal members (including the ctor's default parameter) are visible
/// to the test code. Each (fsModifiers, vk) component is compared against the canonical
/// <see cref="NativeMethods.MOD_*"/> / <see cref="NativeMethods.VK_M"/> constants so any
/// future shift in those values automatically invalidates the test rather than silently
/// test a hard-coded copy.
///
/// <para>
/// We assert each tuple element individually rather than via
/// <c>Assert.Equal((expectedMods, expectedVk), ParseHotkeyString(input))</c> because
/// xUnit 2.9 has multiple tuple-style <c>Assert.Equal</c> overloads whose binding path
/// can prefer the <c>(T1, T2) : ITuple</c> generic path over the value-typed tuple
/// expectation here — easier and clearer to just compare element-wise.
/// </para>
/// </summary>
public class HotkeyServiceTests
{
    private const uint Ctrl  = NativeMethods.MOD_CONTROL;
    private const uint Alt   = NativeMethods.MOD_ALT;
    private const uint Shift = NativeMethods.MOD_SHIFT;
    private const uint Win   = NativeMethods.MOD_WIN;
    private const uint FallbackMods = Ctrl | Alt | Shift;
    private const uint FallbackVk   = NativeMethods.VK_M;

    private static void AssertChord(string hotkey, uint expectedMods, uint expectedVk)
    {
        var parsed = HotkeyService.ParseHotkeyString(hotkey);
        Assert.Equal(expectedMods, parsed.fsModifiers);
        Assert.Equal(expectedVk, parsed.vk);
    }

    [Fact]
    public void Parse_NullOrEmpty_ReturnsCanonicalFallback()
    {
        AssertChord(null!, FallbackMods, FallbackVk);
        AssertChord("",    FallbackMods, FallbackVk);
        AssertChord("   ", FallbackMods, FallbackVk);
    }

    [Fact]
    public void Parse_DefaultHotkeyString_MatchesHistoricalHardcodedChord()
    {
        // "Ctrl+Alt+Shift+M" was the long-standing hard-coded RegisterHotKey chord before
        // ParseHotkeyString existed. Re-resolving the same string must round-trip to the
        // exact (fsModifiers, vk) pair the old hardcode produced.
        AssertChord(HotkeyService.DefaultHotkeyString, Ctrl | Alt | Shift, NativeMethods.VK_M);
    }

    [Theory]
    [InlineData("Ctrl+M",       Ctrl,         (uint)'M')]
    [InlineData("ctrl+m",       Ctrl,         (uint)'M')] // case-insensitive
    [InlineData("Ctrl+Shift+M", Ctrl | Shift, (uint)'M')]
    [InlineData("Alt+Shift+H",  Alt | Shift,  (uint)'H')]
    [InlineData("Ctrl+Alt+Z",   Ctrl | Alt,   (uint)'Z')]
    public void Parse_StandardChords_ResolveToExpectedTokenPair(string input, uint expectedMods, uint expectedVk)
    {
        AssertChord(input, expectedMods, expectedVk);
    }

    [Theory]
    [InlineData("  Ctrl+M  ",                  Ctrl,         (uint)'M')]   // outer whitespace
    [InlineData(" Ctrl + Shift + F12 ",        Ctrl | Shift, 0x7B)]        // whitespace around operators + named key
    [InlineData("  Tab  ",                     FallbackMods, 0x09)]        // bare named key (no mods → fallback mods)
    [InlineData("\tAlt+F1\t",                  Alt,          0x70)]        // tabs instead of spaces
    public void Parse_WhitespacePaddedTokens_TrimsAroundOperators(string input, uint expectedMods, uint expectedVk)
    {
        AssertChord(input, expectedMods, expectedVk);
    }

    [Theory]
    [InlineData("Ctrl+Ctrl+M",                    Ctrl,           (uint)'M')]   // dup Ctrl folds (bit OR is idempotent)
    [InlineData("Alt+Shift+Alt+M",                Alt | Shift,    (uint)'M')]   // dup Alt folds
    [InlineData("Ctrl+Alt+Shift+Ctrl+Win+Shift+M", Ctrl | Alt | Shift | Win, (uint)'M')] // 4 mods × 2 dupes all fold
    public void Parse_DuplicateModifiers_FoldToSingleBit(string input, uint expectedMods, uint expectedVk)
    {
        AssertChord(input, expectedMods, expectedVk);
    }

    [Fact]
    public void FormatChord_ZeroModifiers_ReturnsKeyOnly()
    {
        // Zero-modifier chord (rare in the wild — RegisterHotKey no-ops it on
        // most desktops — but our formatter still produces a readable string)
        // should render just the key, no leading separator or stray modifier.
        Assert.Equal("M", HotkeyService.FormatChord(0, (uint)'M'));
    }

    [Fact]
    public void FormatChord_AllFourModifiers_RendersCanonicalOrder()
    {
        // Canonical order is always Ctrl → Alt → Shift → Win regardless of
        // which bit was set first in the input mask. FormatChord's internal
        // List<string> appends in that order — assert here so a future
        // reordering (e.g. alphabetical, native-Win32 conventional) stays
        // deliberate rather than accidental.
        Assert.Equal("Ctrl+Alt+Shift+Win+M",
            HotkeyService.FormatChord(Ctrl | Alt | Shift | Win, (uint)'M'));
    }

    [Fact]
    public void Parse_IncludesWinModifier_AddsWinFlag()
    {
        AssertChord("Ctrl+Win+M", Ctrl | Win, NativeMethods.VK_M);
    }

    [Fact]
    public void Parse_UnrecognizedMultiCharToken_FallsBackToVkM()
    {
        // Tokens NOT in the named-keys map (e.g. "Junk", "FooBar") still fall through
        // to VK_M and surface in the rolling log, so an unexpected config value doesn't
        // quietly trap the user on M. This test deliberately uses tokens that aren't
        // ordinary typos — "Junk" and "FooBar" are obviously not VK names so the
        // resolver has nothing to do with them.
        AssertChord("Ctrl+Junk",   Ctrl,         FallbackVk);
        AssertChord("Foo+Bar",     FallbackMods, FallbackVk);
    }

    [Fact]
    public void Parse_ModifiersOnly_KeepsFallbackVk()
    {
        // Only-modifier tokens (e.g. "Ctrl+Shift") leaves vkConsumed=false so vk falls
        // through to VK_M. This is intentional: RegisterHotKey rejects zero-modifier
        // chords on most desktop SKUs and we'd rather default to VK_M than require the
        // user specify an extra trailing key for what was clearly intended as a chord.
        AssertChord("Ctrl+Shift", Ctrl | Shift, FallbackVk);
        AssertChord("Ctrl",       Ctrl,         FallbackVk);
    }

    [Fact]
    public void Parse_SingleKeyNoModifiers_UsesCanonicalModifierFallback()
    {
        // Bare key tokens (no modifiers) get the canonical Ctrl+Alt+Shift modifier
        // fallback rather than zero modifiers — zero mods is rejected by RegisterHotKey
        // on Win10+ desktops. The fallback is the same one used by the no-input path so
        // both look uniform to the operator reading the log.
        AssertChord("M", FallbackMods, NativeMethods.VK_M);
    }

    [Fact]
    public void Parse_FirstKeyWinsSecondKeyLoggedAsUnrecognized()
    {
        // Two single-char keys: the parser must record the second as "unrecognized"
        // (its slot is treated as a duplicate since vkConsumed is already true) and
        // keep the first VK. The flag is in the parser side-effect, not the return
        // value — the test asserts the return values stay coherent and the first key wins.
        //
        // "M+Z" has no modifiers in its token list at all, so modsConsumed stays false
        // and the canonical Ctrl+Alt|Shift fallback mods kick in (the first VK, 'M', is
        // single-char so it always wins). This is consistent with the single-key-no-mods
        // case above.
        AssertChord("Ctrl+F+G", Ctrl,         (uint)'F');
        AssertChord("M+Z",      FallbackMods, NativeMethods.VK_M);
    }

    [Fact]
    public void Parse_ToleratesWhitespaceAroundTokens()
    {
        AssertChord("  Ctrl + Shift + M  ", Ctrl | Shift, (uint)'M');
    }

    [Fact]
    public void Parse_ControlAlias_BehavesLikeCtrl()
    {
        AssertChord("Control+M", Ctrl, (uint)'M');
    }

    [Theory]
    [InlineData("Ctrl+M")]
    [InlineData("Ctrl+Junk")]                 // unrecognized multi-char → VK_M fallback (changed from Ctrl+Shift+F12 once that resolved cleanly via the named-keys map)
    [InlineData("")]
    [InlineData("Ctrl")]                      // modifier-only → VK_M fallback
    [InlineData("M")]                         // bare-key → fallback mods
    public void Parse_SilentFlag_ReturnsSameChordAsLogging(string input)
    {
        // The only difference between the logWarnings:true and logWarnings:false paths is
        // that the silent variant skips WinMeters.Log.D calls. The chord tuple itself is
        // identical, so callers using the flag to avoid log spam during per-keystroke UI
        // validation don't sacrifice any resolution fidelity.
        var withLogging    = HotkeyService.ParseHotkeyString(input, logWarnings: true);
        var withoutLogging = HotkeyService.ParseHotkeyString(input, logWarnings: false);

        Assert.Equal(withLogging, withoutLogging);
    }

    [Fact]
    public void ReRegister_UpdatesCurrentSpecFromBoundSettings()
    {
        // HotkeyService with IntPtr.Zero can't actually register a system hotkey, but
        // ReRegister still walks through the spec update unconditionally so the
        // internal CurrentSpec field reflects the latest settings. We assert on
        // CurrentSpec (a test-only internal accessor) to verify the swap propagated.
        var svc = new HotkeyService(IntPtr.Zero, () => { }, "Ctrl+M");
        Assert.Equal("Ctrl+M", svc.CurrentSpec);

        var settings = new AppSettings { General = { Hotkey = "Ctrl+Shift+B" } };
        svc.BindSettings(settings);
        svc.ReRegister();

        Assert.Equal("Ctrl+Shift+B", svc.CurrentSpec);
    }

    [Fact]
    public void ReRegister_WithoutBind_FallsBackToInitialSpec()
    {
        var svc = new HotkeyService(IntPtr.Zero, () => { }, "Ctrl+Alt+Shift+M");
        // No BindSettings call — ReRegister should re-resolve to the ctor-injected spec.
        svc.ReRegister();
        Assert.Equal("Ctrl+Alt+Shift+M", svc.CurrentSpec);
    }

    [Fact]
    public void ReRegister_WithBlankHotkeyInSettings_FallsBackToDefault()
    {
        var svc = new HotkeyService(IntPtr.Zero, () => { }, "Ctrl+M");
        var settings = new AppSettings { General = { Hotkey = "" } };
        svc.BindSettings(settings);
        svc.ReRegister();

        Assert.Equal(HotkeyService.DefaultHotkeyString, svc.CurrentSpec);
    }

    [Fact]
    public void BindSettings_Null_Throws()
    {
        var svc = new HotkeyService(IntPtr.Zero, () => { });
        Assert.Throws<ArgumentNullException>(() => svc.BindSettings(null!));
    }

    [Theory]
    [InlineData(Ctrl,                (uint)'M',  "Ctrl+M")]
    [InlineData(Ctrl | Alt,          (uint)'Z',  "Ctrl+Alt+Z")]
    [InlineData(Ctrl | Alt | Shift,  (uint)'M',  "Ctrl+Alt+Shift+M")]   // canonical default
    [InlineData(Alt | Shift,         (uint)'H',  "Alt+Shift+H")]
    [InlineData(Ctrl | Win,          (uint)'A',  "Ctrl+Win+A")]
    [InlineData(0,                   (uint)'M',  "M")]                   // no mods
    [InlineData(Ctrl,                0x30,       "Ctrl+0")]              // digit
    [InlineData(Ctrl,                0x20,       "Ctrl+Space")]          // named special
    [InlineData(Ctrl,                0x09,       "Ctrl+Tab")]
    [InlineData(Ctrl,                0x0D,       "Ctrl+Enter")]
    [InlineData(Ctrl,                0x1B,       "Ctrl+Esc")]
    [InlineData(Ctrl,                0x70,       "Ctrl+F1")]             // F-keys (0x70–0x7B)
    [InlineData(Ctrl | Alt,          0x74,       "Ctrl+Alt+F5")]
    [InlineData(Shift,               0x7B,       "Shift+F12")]
    [InlineData(0,                   0x71,       "F2")]
    [InlineData(Ctrl,                0x26,       "Ctrl+Up")]             // navigation (0x21–0x28, 0x2D, 0x2E)
    [InlineData(Ctrl | Win,          0x28,       "Ctrl+Win+Down")]
    [InlineData(0,                   0x25,       "Left")]
    [InlineData(Shift,               0x27,       "Shift+Right")]
    [InlineData(Ctrl,                0x21,       "Ctrl+PageUp")]
    [InlineData(Ctrl,                0x22,       "Ctrl+PageDown")]
    [InlineData(Alt,                 0x24,       "Alt+Home")]
    [InlineData(Ctrl | Shift,        0x23,       "Ctrl+Shift+End")]
    [InlineData(Shift,               0x2D,       "Shift+Insert")]
    [InlineData(Ctrl | Alt,          0x2E,       "Ctrl+Alt+Delete")]
    [InlineData(Ctrl,                0x91,       "Ctrl+ScrollLock")]     // now resolves via FriendlyKeyNames since (ScrollLock, 0x91) was added to NamedVkPairs
    public void FormatChord_RendersModsPlusKeyInline(uint mods, uint vk, string expected)
    {
        Assert.Equal(expected, HotkeyService.FormatChord(mods, vk));
    }

    [Fact]
    public void RegisterFailed_Event_FiresWhenRegisterHotKeyReturnsFalse()
    {
        // IntPtr.Zero is an invalid HWND for RegisterHotKey, so the call always fails.
        // The event handler is then synchronously invoked with a one-line payload that
        // includes the configured chord spec — verified here via a captured string.
        var svc = new HotkeyService(IntPtr.Zero, () => { }, "Ctrl+Shift+B");
        string? captured = null;
        svc.RegisterFailed += msg => captured = msg;

        svc.Register();

        Assert.NotNull(captured);
        Assert.Contains("Ctrl+Shift+B", captured);
        Assert.Contains("could not be registered", captured);
    }

    [Theory]
    [InlineData("F1",        0x70)]
    [InlineData("F5",        0x74)]
    [InlineData("F12",       0x7B)]
    [InlineData("Space",     0x20)]
    [InlineData("Tab",       0x09)]
    [InlineData("Enter",     0x0D)]
    [InlineData("Esc",       0x1B)]
    [InlineData("Backspace", 0x08)]
    [InlineData("Up",        0x26)]
    [InlineData("Down",      0x28)]
    [InlineData("Left",      0x25)]
    [InlineData("Right",     0x27)]
    [InlineData("PageUp",    0x21)]
    [InlineData("PageDown",  0x22)]
    [InlineData("Home",      0x24)]
    [InlineData("End",       0x23)]
    [InlineData("Insert",    0x2D)]
    [InlineData("Delete",    0x2E)]
    public void Parse_NamedKey_ResolvesToCanonicalVk(string token, uint expectedVk)
    {
        // Multi-char tokens in NamedVkPairs now resolve cleanly via the lookup table
        // instead of falling through to VK_M. Each row asserts both the modifier-set
        // and the VK code so any drift in the named-keys map surfaces as a failing
        // test name rather than silently routing through the fallback path.
        AssertChord($"Ctrl+Shift+{token}", Ctrl | Shift, expectedVk);
        AssertChord($"Alt+{token}",        Alt,          expectedVk);
        AssertChord($"{token}",            FallbackMods, expectedVk);
    }

    [Theory]
    [InlineData("f12",       0x7B)]
    [InlineData("SPACE",     0x20)]
    [InlineData("shift+f1",  0x70)]
    public void Parse_NamedKey_CaseInsensitive(string input, uint expectedVk)
    {
        // The friendly-name lookup uses OrdinalIgnoreCase so users can type
        // "ctrl+shift+f12" or "CTRL+SHIFT+F12" interchangeably. The chord we
        // actually emit still carries the canonical VK code (case folding
        // happens on the friendly-name side, not the resolved-code side).
        var parsed = HotkeyService.ParseHotkeyString(input);
        Assert.Equal(expectedVk, parsed.vk);
    }

    [Theory]
    [InlineData("Ctrl+M")]
    [InlineData("Ctrl+Alt+Shift+M")]
    [InlineData("Ctrl+Shift+F12")]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Tab")]
    public void Parse_ThenFormat_RoundTripsBackToSameChord(string input)
    {
        // Symmetry check: feeding ParseHotkeyString's output back through
        // FormatChord should produce a string that re-parses to the same
        // (mods, vk) pair. Catches drift if anyone ever re-introduces an
        // asymmetric mapping (parser maps F1→VK_F1, formatter renders
        // VK_F1 as "Vk0x70") so the two surfaces can never disagree.
        var first   = HotkeyService.ParseHotkeyString(input);
        var rendered = HotkeyService.FormatChord(first.fsModifiers, first.vk);
        var second  = HotkeyService.ParseHotkeyString(rendered);

        Assert.Equal(first.fsModifiers, second.fsModifiers);
        Assert.Equal(first.vk, second.vk);
    }

    [Theory]
    [InlineData(null,                  false, FallbackMods, FallbackVk)]   // null input → EmptyInput
    [InlineData("",                    false, FallbackMods, FallbackVk)]   // empty string → EmptyInput
    [InlineData("   ",                 false, FallbackMods, FallbackVk)]   // whitespace → EmptyInput
    [InlineData("Ctrl+M",              true,  Ctrl,         (uint)'M')]    // canonical clean parse
    [InlineData("Ctrl+Alt+Shift+M",    true,  Ctrl | Alt | Shift, NativeMethods.VK_M)]  // historical default chord
    [InlineData("Ctrl+Shift+F12",      true,  Ctrl | Shift, 0x7B)]         // named-key resolves cleanly via map
    [InlineData("Alt+Up",              true,  Alt,          0x26)]         // navigation key resolves
    [InlineData("Ctrl+PageDown",       true,  Ctrl,         0x22)]         // another navigation chord
    [InlineData("Ctrl+Junk",           false, Ctrl,         FallbackVk)]   // unknown VK → Invalid
    [InlineData("Foo+Bar",             false, FallbackMods, FallbackVk)]   // two unknown VKs → Invalid
    [InlineData("Ctrl+Shift",          false, Ctrl | Shift, FallbackVk)]   // bare modifiers → Invalid
    [InlineData("Ctrl+F+G",            false, Ctrl,         (uint)'F')]    // first VK wins, second flagged → Invalid (chord still useful for label)
    public void TryParse_ThreeStateMatchesParseInternal(string? input, bool expectedClean, uint expectedMods, uint expectedVk)
    {
        // Comfort-style assertion: for every input the returned chord tuple equals
        // ParseHotkeyString's tuple (both methods share ParseInternal). The bool
        // distinguishes empty / clean / invalid so we can verify the three-state
        // distinction is properly propagated to callers — the SettingsWindow status
        // label depends on this to render distinct states. Each tuple element is
        // compared individually rather than via tuple equality because xUnit's
        // (T1, T2) : ITuple binding can prefer the generic tuple path over the
        // value-tuple expectation (see Parse_StandardChords_ResolveToExpectedTokenPair
        // for the same rationale on the parsing side).
        var ok = HotkeyService.TryParseHotkeyString(input, out var chord, logWarnings: false);

        Assert.Equal(expectedClean, ok);
        Assert.Equal(expectedMods,  chord.fsModifiers);
        Assert.Equal(expectedVk,    chord.vk);

        // Cross-check against ParseHotkeyString — both share ParseInternal so the
        // chord tuple must be identical regardless of which API the caller prefers.
        // This catches drift if anyone ever re-implements one of the two methods
        // independently without pulling the same value through the shared helper.
        var parsed = HotkeyService.ParseHotkeyString(input, logWarnings: false);
        Assert.Equal(parsed.fsModifiers, chord.fsModifiers);
        Assert.Equal(parsed.vk,           chord.vk);
    }

    [Fact]
    public void RegisterFailed_DoesNotFire_AfterDispose()
    {
        // We can't easily synthesize a successful RegisterHotKey in a hermetic test
        // (would require a real window), so we verify the symptom of failure (never
        // fired) by using a service that's been pre-disposed — Register() short-circuits
        // before the call without invoking handlers. This pins the contract that the
        // event never fires spuriously on no-op paths.
        var svc = new HotkeyService(IntPtr.Zero, () => { }, "Ctrl+M");
        bool fired = false;
        svc.RegisterFailed += _ => fired = true;
        svc.Dispose();
        svc.Register();
        Assert.False(fired);
    }

    // ── Media / browser keys (discrete 0xA6/0xA7/0xAD–0xAF/0xB3 VK codes) ───────────────────────────────────
    // BrowserBack / BrowserForward / VolumeMute / VolumeDown / VolumeUp /
    // MediaPlayPause were added to NamedVkPairs in HotkeyService.cs as part of
    // extending the friendly-name vocabulary beyond the F-keys / editor specials /
    // navigation keys. Both parser and formatter pick them up automatically via
    // the single-source-of-truth tuple array, so these theories pin both
    // directions of the chord vocabulary (in via Parse…, out via FormatChord).

    [Theory]
    [InlineData("Ctrl+VolumeUp",       Ctrl,         0xAF)]
    [InlineData("Alt+VolumeDown",      Alt,          0xAE)]
    [InlineData("Shift+VolumeMute",    Shift,        0xAD)]
    [InlineData("BrowserBack",         FallbackMods, 0xA6)]
    [InlineData("Ctrl+BrowserForward", Ctrl,         0xA7)]
    [InlineData("MediaPlayPause",      FallbackMods, 0xB3)]
    public void Parse_MediaAndBrowserKeys_ResolveToExpectedVk(string input, uint expectedMods, uint expectedVk)
    {
        AssertChord(input, expectedMods, expectedVk);
    }

    [Theory]
    [InlineData(Ctrl,  0xAF, "Ctrl+VolumeUp")]
    [InlineData(Alt,   0xAE, "Alt+VolumeDown")]
    [InlineData(Shift, 0xAD, "Shift+VolumeMute")]
    [InlineData(0,     0xA6, "BrowserBack")]
    [InlineData(Ctrl,  0xA7, "Ctrl+BrowserForward")]
    [InlineData(0,     0xB3, "MediaPlayPause")]
    public void FormatChord_MediaAndBrowserKeys_RenderFriendlyName(uint fsModifiers, uint vk, string expected)
    {
        Assert.Equal(expected, HotkeyService.FormatChord(fsModifiers, vk));
    }

    // ── Locks, snapshot & numpad cluster ──
    // CapsLock (0x14), PrintScreen (0x2C), NumLock (0x90), ScrollLock (0x91),
    // Numpad0–9 (0x60–0x69), and Numpad operator row (Multiply/Add/Subtract/
    // Decimal/Divide = 0x6A–0x6F). These follow the same single-source-of-truth
    // pattern as F-keys / navigation / media: added to NamedVkPairs once, both
    // parser and formatter pick them up without parallel structures.

    [Theory]
    [InlineData("Ctrl+CapsLock",     Ctrl,  0x14)]
    [InlineData("Alt+PrintScreen",   Alt,   0x2C)]
    [InlineData("Shift+NumLock",     Shift, 0x90)]
    [InlineData("Ctrl+ScrollLock",   Ctrl,  0x91)]
    [InlineData("Numpad5",           FallbackMods, 0x65)]   // bare numpad digit
    [InlineData("Ctrl+Numpad9",      Ctrl,  0x69)]
    [InlineData("Alt+Multiply",      Alt,   0x6A)]         // numpad operator row
    [InlineData("Ctrl+Add",          Ctrl,  0x6B)]
    [InlineData("Shift+Subtract",    Shift, 0x6D)]
    [InlineData("Alt+Decimal",       Alt,   0x6E)]
    [InlineData("Ctrl+Divide",       Ctrl,  0x6F)]
    // NumpadEnter (VK_NUMPAD_ENTER = 0x0E) is distinct from regular Enter
    // (VK_RETURN = 0x0D) — both map to entries in NamedVkPairs so a chord
    // like Ctrl+NumpadEnter isn't silently aliased to Ctrl+Enter.
    [InlineData("Ctrl+NumpadEnter",  Ctrl,  0x0E)]
    [InlineData("Alt+NumpadEnter",   Alt,   0x0E)]
    [InlineData("NumpadEnter",       FallbackMods, 0x0E)]   // bare numpad enter \u2192 fallback mods
    public void Parse_LocksSnapshotAndNumpadKeys_ResolveToExpectedVk(string input, uint expectedMods, uint expectedVk)
    {
        AssertChord(input, expectedMods, expectedVk);
    }

    [Theory]
    [InlineData(Ctrl,  0x14, "Ctrl+CapsLock")]
    [InlineData(Alt,   0x2C, "Alt+PrintScreen")]
    [InlineData(Shift, 0x90, "Shift+NumLock")]
    [InlineData(Ctrl,  0x91, "Ctrl+ScrollLock")]
    [InlineData(0,     0x65, "Numpad5")]
    [InlineData(Ctrl,  0x69, "Ctrl+Numpad9")]
    [InlineData(Alt,   0x6A, "Alt+Multiply")]
    [InlineData(Ctrl,  0x6B, "Ctrl+Add")]
    [InlineData(Shift, 0x6D, "Shift+Subtract")]
    [InlineData(Ctrl,  0x6F, "Ctrl+Divide")]
    [InlineData(Ctrl,  0x0E, "Ctrl+NumpadEnter")]
    [InlineData(Alt,   0x0E, "Alt+NumpadEnter")]
    // Mirror the bare-key parse case (FallbackMods) for symmetric coverage
    // — a future contributor hunting for parser/formatter contradictions has
    // the obvious zero-mod mirror row.
    [InlineData(0,     0x0E, "NumpadEnter")]
    public void FormatChord_LocksSnapshotAndNumpadKeys_RenderFriendlyName(uint fsModifiers, uint vk, string expected)
    {
        Assert.Equal(expected, HotkeyService.FormatChord(fsModifiers, vk));
    }
}
