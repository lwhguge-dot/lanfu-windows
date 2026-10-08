using System.Collections.Concurrent;
using Microsoft.Windows.ApplicationModel.Resources;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Helpers;

public static class LocalizationHelper
{
    /// <summary>
    /// Language every UI lookup uses when nothing overrides it. LanFu is a
    /// Simplified-Chinese product, so this is a product constant rather than the
    /// Windows display language: the shell must render Chinese on any OS locale.
    /// <c>OPENCLAW_LANGUAGE</c> still wins (see <see cref="App"/>), which keeps the
    /// localization tests able to pin another locale.
    /// </summary>
    public const string DefaultUiLanguage = "zh-CN";

    private static ResourceManager? _resourceManager;
    private static ResourceContext? _overrideContext;
    private static string? _languageOverride;
    private static readonly ConcurrentDictionary<string, byte> s_loggedLookupFailures = new();
    private const int MaxLoggedLookupFailures = 1024;
    private static int s_lookupFailureLimitLogged;

    /// <summary>
    /// Force a specific language for testing (e.g. "zh-CN").
    /// Must be called before any GetString calls.
    /// </summary>
    public static void SetLanguageOverride(string language)
    {
        _languageOverride = language;
        _resourceManager = null;
        _overrideContext = null;
    }

    /// <summary>
    /// Applies the process-wide UI language. Must run before the first page is loaded.
    /// <para>
    /// XAML <c>x:Uid</c> lookups never call into this class: the XAML framework resolves
    /// them with its own resource context, whose language comes from the Windows App SDK
    /// <c>ApplicationLanguages</c> list. On a zh-CN machine that list still resolved to the
    /// PRI's default language, so the shell rendered the English resources. Setting
    /// <c>PrimaryLanguageOverride</c> is what makes the XAML path agree with the code path
    /// <em>and</em> with <c>OPENCLAW_LANGUAGE</c>.
    /// </para>
    /// </summary>
    public static void ApplyUiLanguage(string languageTag)
    {
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = languageTag;
        }
        catch (Exception ex)
        {
            Logger.Warn($"LocalizationHelper: PrimaryLanguageOverride('{languageTag}') failed: {ex.Message}");
        }

        SetLanguageOverride(languageTag);
    }

    private static ResourceManager Manager => _resourceManager ??= new ResourceManager();

    /// <summary>
    /// The process-wide resource manager, shared with the XAML framework through
    /// <c>Application.ResourceManagerRequested</c> (see <see cref="App"/>): the framework
    /// resolves <c>x:Uid</c> strings through the manager it is handed there, not through
    /// this class.
    /// </summary>
    internal static ResourceManager SharedResourceManager => Manager;

    private static ResourceContext GetContext()
    {
        if (_overrideContext != null) return _overrideContext;

        // Always pin the language qualifier: an unpinned context follows the process
        // language list, which is what let the shell fall back to English.
        _overrideContext = Manager.CreateResourceContext();
        _overrideContext.QualifierValues["Language"] = _languageOverride ?? DefaultUiLanguage;
        return _overrideContext;
    }

    public static string GetString(string resourceKey)
    {
        var found = TryGetValueAsString(resourceKey, out var value, out var lookupFailure);
        if (found && !string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (TryGetXamlPropertyResourcePath(resourceKey, out var propertyResourcePath))
        {
            var propertyFound = TryGetValueAsString(propertyResourcePath, out value, out var propertyLookupFailure);
            if (propertyFound && !string.IsNullOrEmpty(value))
            {
                return value;
            }

            if (!found)
            {
                lookupFailure ??= propertyLookupFailure;
            }
        }

        if (lookupFailure is not null)
        {
            var logKey = $"{_languageOverride ?? "<default>"}:{resourceKey}:{lookupFailure.GetType().FullName}";
            if (s_loggedLookupFailures.ContainsKey(logKey))
                return resourceKey;

            if (s_loggedLookupFailures.Count < MaxLoggedLookupFailures && s_loggedLookupFailures.TryAdd(logKey, 0))
            {
                Logger.Warn($"LocalizationHelper: Resource lookup failed for '{resourceKey}' (language='{_languageOverride ?? "<default>"}'): {lookupFailure.Message}");
            }
            else if (System.Threading.Interlocked.Exchange(ref s_lookupFailureLimitLogged, 1) == 0)
            {
                Logger.Warn("LocalizationHelper: Resource lookup failure log limit reached; suppressing additional unique resource lookup failures");
            }
        }

        return resourceKey;
    }

    private static bool TryGetValueAsString(string resourceKey, out string? value, out Exception? lookupFailure)
    {
        try
        {
            var candidate = Manager.MainResourceMap.GetValue($"Resources/{resourceKey}", GetContext());
            value = candidate?.ValueAsString;
            lookupFailure = null;
            return true;
        }
        catch (Exception ex)
        {
            value = null;
            lookupFailure = ex;
            return false;
        }
    }

    private static bool TryGetXamlPropertyResourcePath(string resourceKey, out string resourcePath)
    {
        var propertySeparator = resourceKey.LastIndexOf('.');
        if (propertySeparator > 0 && propertySeparator < resourceKey.Length - 1)
        {
            resourcePath = $"{resourceKey[..propertySeparator]}/{resourceKey[(propertySeparator + 1)..]}";
            return true;
        }

        resourcePath = string.Empty;
        return false;
    }

    /// <summary>
    /// Localized <see cref="string.Format(string, object[])"/>. Use for resw values that
    /// contain placeholders like "{0}". Catches <see cref="FormatException"/> caused by
    /// malformed translations (e.g., a translator writing "{2}" with one arg, or "{a}")
    /// so the UI thread can't crash on a translator typo.
    /// </summary>
    public static string Format(string resourceKey, params object?[] args)
    {
        var template = GetString(resourceKey);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            // Surface the unformatted template instead of crashing. The raw "{0}"
            // is still useful debugging signal but doesn't kill the page.
            Logger.Warn($"LocalizationHelper: Resource format failed for '{resourceKey}': template='{template}'");
            return template;
        }
    }

    public static string GetConnectionStatusText(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => GetString("StatusDisplay_Connected"),
        ConnectionStatus.Connecting => GetString("StatusDisplay_Connecting"),
        ConnectionStatus.Disconnected => GetString("StatusDisplay_Disconnected"),
        ConnectionStatus.Error => GetString("StatusDisplay_Error"),
        _ => GetString("StatusDisplay_Unknown")
    };
}
