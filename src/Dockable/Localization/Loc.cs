using System.ComponentModel;
using System.Globalization;

namespace Dockable.Localization;

/// <summary>
/// Runtime localization service. Holds the active language and resolves string keys against the
/// in-code tables in <see cref="LocData"/>. It backs both XAML (via <see cref="LocExtension"/>,
/// which binds to the indexer and refreshes on <see cref="INotifyPropertyChanged"/>) and code-behind
/// (via <see cref="T"/>). Switching language is live: it raises <c>PropertyChanged("Item[]")</c>
/// (refreshing every bound element) and fires <see cref="LanguageChanged"/> for menus built in code.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>The shared instance XAML binds to.</summary>
    public static Loc Instance { get; } = new();

    private IReadOnlyDictionary<string, string> _table = LocData.Tables[LocData.DefaultCode];
    private static readonly IReadOnlyDictionary<string, string> Fallback = LocData.Tables[LocData.DefaultCode];

    /// <summary>The active language code (e.g. "en", "pt-BR", "zh-Hans").</summary>
    public string CurrentCode { get; private set; } = LocData.DefaultCode;

    private Loc() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changes, for UI built in code (menus) that can't data-bind.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>Localized string for <paramref name="key"/> (the XAML binding target). Falls back to
    /// English, then to the key itself, so a missing translation never renders blank.</summary>
    public string this[string key]
    {
        get
        {
            if (_table.TryGetValue(key, out var v)) return v;
            if (Fallback.TryGetValue(key, out var f)) return f;
            return key;
        }
    }

    /// <summary>Localized string for <paramref name="key"/> (code-behind convenience).</summary>
    public static string T(string key) => Instance[key];

    /// <summary>The languages offered in the picker, in display order: (code, native name).</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages => LocData.Languages;

    /// <summary>
    /// Resolves the initial language from a saved code or, failing that, the OS UI culture, applies
    /// it, and returns the concrete resolved code so the caller can persist it.
    /// </summary>
    public static string Initialize(string? saved)
    {
        string code = Resolve(saved);
        Instance.Apply(code);
        return code;
    }

    /// <summary>Switches the active language and notifies all bound UI + code-built menus.</summary>
    public void SetLanguage(string code)
    {
        Apply(Resolve(code));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(string code)
    {
        CurrentCode = code;
        _table = LocData.Tables.TryGetValue(code, out var t) ? t : Fallback;
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // The string table is what actually drives the UI; a missing CLR culture is non-fatal.
        }
    }

    /// <summary>Maps a saved/OS code to a supported code: exact match, else two-letter prefix, else English.</summary>
    private static string Resolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            // No saved choice → follow the OS UI language when it's one we support.
            string osTwo = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return MatchTwoLetter(osTwo) ?? LocData.DefaultCode;
        }
        if (LocData.Tables.ContainsKey(code))
            return code;
        return MatchTwoLetter(code.Split('-')[0]) ?? LocData.DefaultCode;
    }

    private static string? MatchTwoLetter(string two)
    {
        foreach (var supported in LocData.Tables.Keys)
            if (supported.Split('-')[0].Equals(two, StringComparison.OrdinalIgnoreCase))
                return supported;
        return null;
    }
}
