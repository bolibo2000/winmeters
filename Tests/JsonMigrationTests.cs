using System.Text.Json;
using Xunit;

namespace WinMeters.Tests;

/// <summary>
/// Additional coverage over <c>AppSettings.MigrateSettings</c> that exists specifically to pin
/// the new <see cref="System.Text.Json.JsonDocument"/>-based value extraction. The original
/// hand-rolled <c>TryReadString</c>/<c>TryReadBool</c> scanned raw JSON with <c>IndexOf</c> +
/// quote hopping and were brittle when a value contained an escaped quote, when a number was
/// present where a string was expected, or when a later-shape sibling happened to share a
/// token with the migration key.
///
/// The tests in this file pin the new <see cref="System.Text.Json"/> path against those edge
/// cases so a future "speed up the migration" refactor that goes back to substring scanning
/// fails loudly instead of silently shipping broken user upgrades.
/// </summary>
public class JsonMigrationTests
{
    /// <summary>
    /// Legacy <c>Background</c> default "#FF202020" embedded inside a long Colors object that
    /// also contains a key whose value happens to literally include the substring "#FF202020".
    /// The old IndexOf-based detection would fire on that sibling and overwrite the real value;
    /// JsonDocument scopes the lookup to the actual Colors.Background element so this passes.
    /// </summary>
    [Fact]
    public void Background_LegacyRebase_IgnoresSubstringPoisoning()
    {
        const string raw = "{\"Colors\":{\"Background\":\"#FF202020\",\"Label\":\"#FF202020-poison\"}}";
        var settings = JsonSerializer.Deserialize<AppSettings>(raw)!;

        InvokeMigrate(settings, raw);

        Assert.Equal("#CC202020", settings.Colors.Background);
        // The explicitly-winning sister Label stays untouched (it has a suffix that is not the legacy default).
        Assert.Equal("#FF202020-poison", settings.Colors.Label);
    }

    /// <summary>
    /// Backwards compat: legacy docking flag present at the *top level of* the JSON (not under Window),
    /// expressed as a JSON boolean (true/false). The old TryReadBool parsed whatever character
    /// followed the key so "true" / "false" / "True" all worked; JsonDocument reads the actual
    /// JSON token type so a JSON bool stays correct and a string-form bool still converts as
    /// the old code accepted.
    /// </summary>
    [Theory]
    [InlineData("{\"DockOnTaskbar\":true}", true)]
    [InlineData("{\"DockOnTaskbar\":false}", false)]
    [InlineData("{\"DockOnTaskbar\":\"true\"}", true)]
    [InlineData("{\"DockOnTaskbar\":\"false\"}", false)]
    public void DockOnTaskbar_AcceptsBoolAndStringForms(string raw, bool expected)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(raw)!;
        InvokeMigrate(settings, raw);

        Assert.Equal(expected, settings.Window.StickToTaskbar);
    }

    /// <summary>
    /// <c>WindowMode</c> swallowing: legacy Floating mode flips StickToTaskbar only when
    /// <c>DockOnTaskbar</c> is absent; with both present the explicit DockOnTaskbar value wins.
    /// </summary>
    [Theory]
    [InlineData("{\"WindowMode\":\"Floating\"}", false)]
    [InlineData("{\"WindowMode\":\"AppBar\"}", true)]
    [InlineData("{\"WindowMode\":\"Floating\",\"DockOnTaskbar\":true}", true)]
    public void WindowMode_FloatingTranslatesToStickyFlag(string raw, bool expectedSticky)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(raw)!;
        InvokeMigrate(settings, raw);

        Assert.Equal(expectedSticky, settings.Window.StickToTaskbar);
    }

    /// <summary>
    /// Explicit JSON null in a property slot now returns <see cref="string.Empty"/>-ish default
    /// for <c>JsonReadString</c> instead of an arbitrary substring cut. The legacy
    /// <c>TryReadString</c> couldn't distinguish "Field present as null" from "Field absent" and
    /// both landed here as null. The new path preserves null so the test contract pinned in
    /// <c>AppSettingsTests.MigrateSettings_BackgroundIsNull</c> still holds.
    /// </summary>
    [Fact]
    public void Background_Null_PreservedAsNull()
    {
        const string raw = "{\"Colors\":{\"Background\":null}}";
        var settings = JsonSerializer.Deserialize<AppSettings>(raw)!;

        InvokeMigrate(settings, raw);

        // null preserved — the legacy default-rebase guard skips null because GetRawText would
        // emit the literal "null" token which is not "#FF202020".
        Assert.Null(settings.Colors.Background);
    }

    /// <summary>
    /// Malformed JSON: the migration paths' Has(rawJson, …) substring checks still produce some
    /// results but InitializeJsonDocument swallows JsonException cleanly so the migration never
    /// throws out of Load(). This pins that the legacy background rebase no-ops cleanly when the
    /// JSON cannot be parsed at all (corrupt partial save on a forced shutdown).
    /// </summary>
    [Fact]
    public void MigrateSettings_MalformedJson_DoesNotThrow()
    {
        // Truncated JSON that the model can't deserialize cleanly into AppSettings — note we
        // still mark Has(rawJson,"Background") = true so the rebase path is actually exercised.
        const string raw = "{\"Colors\":{\"Background\":\"#FF";

        // We can't Deserialize a corrupt string, but MigrateSettings is exposed defensively
        // for already-deserialized settings. Pass an empty AppSettings + valid-looking-but-
        // malformed rawJson so InitializeJsonDocument fails and Background is left untouched.
        var settings = new AppSettings { Colors = { Background = "#12345678" } };
        InvokeMigrate(settings, raw);

        // JsonDocument parse failed → InitializeJsonDocument returned null → Background stays at
        // the explicit "non-legacy-default" sentinel we set, NOT the rebase value.
        Assert.Equal("#12345678", settings.Colors.Background);
    }

    private static void InvokeMigrate(AppSettings settings, string rawJson)
    {
        var method = typeof(AppSettings).GetMethod(
            "MigrateSettings",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("MigrateSettings not found");
        method.Invoke(null, new object[] { settings, rawJson });
    }
}
