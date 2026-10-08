namespace HushMusic.Core.Services;

/// <summary>What a system-wide keyboard shortcut does. The names are the keys of <c>AppSettings.GlobalHotkeys</c>.</summary>
public enum HotkeyAction
{
    PlayPause,
    Next,
    Previous,
    VolumeUp,
    VolumeDown,
    Mute,
    Like,
    ShowHide,
}

/// <summary>Modifier keys, with the values RegisterHotKey takes (MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN).</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}

/// <summary>
/// A key with modifiers, e.g. Ctrl+Alt+Right. <see cref="Key"/> is a Windows virtual-key code. Text form: modifiers in
/// the order Win, Ctrl, Alt, Shift, then the key, joined with "+".
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int Key)
{
    /// <summary>
    /// A gesture that can be registered without getting in the way of typing: a nameable key with Ctrl, Alt or Win, or
    /// with Shift alone only for keys that don't type anything (F-keys, arrows, Home…). Shift+A would take capital A
    /// away from every other app.
    /// </summary>
    public bool IsValid => HotkeyKeys.NameOf(Key) is not null && ValidModifiers(Modifiers, Key);

    /// <summary>Reads "Ctrl+Alt+Right" (any order and case, spaces allowed). False for anything <see cref="IsValid"/> rejects.</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (var part in text.Split('+'))
        {
            var token = part.Trim();
            if (token.Length == 0)
            {
                return false;
            }

            if (ModifierOf(token) is { } modifier)
            {
                modifiers |= modifier;
            }
            else if (key is null && HotkeyKeys.TryGetKey(token, out var code))
            {
                key = code;
            }
            else
            {
                return false;
            }
        }

        if (key is not { } found)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, found);
        return gesture.IsValid;
    }

    public static HotkeyGesture? Parse(string? text) => TryParse(text, out var gesture) ? gesture : null;

    /// <summary>True when <paramref name="modifiers"/> are enough for <paramref name="key"/> (see <see cref="IsValid"/>).</summary>
    public static bool ValidModifiers(HotkeyModifiers modifiers, int key) =>
        (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0
        || (modifiers == HotkeyModifiers.Shift && !HotkeyKeys.TypesText(key));

    /// <summary>"Win+Ctrl+Alt+Shift", or the part of it that is held; empty for none.</summary>
    public static string FormatModifiers(HotkeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        return string.Join('+', parts);
    }

    public override string ToString()
    {
        var key = HotkeyKeys.NameOf(Key) ?? $"0x{Key:X2}";
        return Modifiers == HotkeyModifiers.None ? key : FormatModifiers(Modifiers) + "+" + key;
    }

    private static HotkeyModifiers? ModifierOf(string token) => token.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => HotkeyModifiers.Control,
        "ALT" => HotkeyModifiers.Alt,
        "SHIFT" => HotkeyModifiers.Shift,
        "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
        _ => null,
    };
}

/// <summary>Names of the keys a global shortcut can use (US layout names for the punctuation keys).</summary>
public static class HotkeyKeys
{
    private static readonly Dictionary<int, string> Names = BuildNames();

    private static readonly Dictionary<string, int> Codes = BuildCodes();

    /// <summary>The key's name, or null for keys a shortcut can't use (modifiers, Esc, Backspace, lock keys, media keys).</summary>
    public static string? NameOf(int key) => Names.GetValueOrDefault(key);

    public static bool TryGetKey(string name, out int key) => Codes.TryGetValue(name.Trim(), out key);

    /// <summary>Shift, Ctrl, Alt and the Windows keys, left, right or either.</summary>
    public static bool IsModifier(int key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    /// <summary>Keys that type something (or move focus) when pressed with Shift alone.</summary>
    public static bool TypesText(int key) =>
        key is 0x09 or 0x0D or 0x20
        || key is >= 0x30 and <= 0x39
        || key is >= 0x41 and <= 0x5A
        || key is >= 0x60 and <= 0x6F
        || key is >= 0xBA and <= 0xC0
        || key is >= 0xDB and <= 0xDE;

    private static Dictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>
        {
            [0x09] = "Tab",
            [0x0D] = "Enter",
            [0x13] = "Pause",
            [0x20] = "Space",
            [0x21] = "PageUp",
            [0x22] = "PageDown",
            [0x23] = "End",
            [0x24] = "Home",
            [0x25] = "Left",
            [0x26] = "Up",
            [0x27] = "Right",
            [0x28] = "Down",
            [0x2C] = "PrintScreen",
            [0x2D] = "Insert",
            [0x2E] = "Delete",
            [0x6A] = "Num*",
            [0x6B] = "NumPlus",
            [0x6D] = "Num-",
            [0x6E] = "Num.",
            [0x6F] = "Num/",
            [0x91] = "ScrollLock",
            [0xBA] = ";",
            [0xBB] = "=",
            [0xBC] = ",",
            [0xBD] = "-",
            [0xBE] = ".",
            [0xBF] = "/",
            [0xC0] = "`",
            [0xDB] = "[",
            [0xDC] = "\\",
            [0xDD] = "]",
            [0xDE] = "'",
        };

        for (var c = '0'; c <= '9'; c++)
        {
            names[c] = c.ToString();
            names[0x60 + (c - '0')] = "Num" + c;
        }

        for (var c = 'A'; c <= 'Z'; c++)
        {
            names[c] = c.ToString();
        }

        for (var f = 1; f <= 24; f++)
        {
            names[0x6F + f] = "F" + f;
        }

        return names;
    }

    private static Dictionary<string, int> BuildCodes()
    {
        var codes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, name) in Names)
        {
            codes[name] = code;
        }

        codes["Return"] = 0x0D;
        codes["PgUp"] = 0x21;
        codes["PgDn"] = 0x22;
        codes["Ins"] = 0x2D;
        codes["Del"] = 0x2E;
        codes["Plus"] = 0xBB;
        codes["Minus"] = 0xBD;
        return codes;
    }
}

/// <summary>
/// Which gesture each action uses: the saved one (<c>AppSettings.GlobalHotkeys</c>, action name → gesture text), or the
/// built-in default when the action isn't saved. An empty saved value means the user removed the shortcut.
/// </summary>
public static class GlobalHotkeyMap
{
    public static IReadOnlyList<HotkeyAction> Actions { get; } = Enum.GetValues<HotkeyAction>();

    public static HotkeyGesture DefaultFor(HotkeyAction action)
    {
        const HotkeyModifiers CtrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;
        return action switch
        {
            HotkeyAction.PlayPause => new(CtrlAlt, 0x20),
            HotkeyAction.Next => new(CtrlAlt, 0x27),
            HotkeyAction.Previous => new(CtrlAlt, 0x25),
            HotkeyAction.VolumeUp => new(CtrlAlt, 0x26),
            HotkeyAction.VolumeDown => new(CtrlAlt, 0x28),
            HotkeyAction.Mute => new(CtrlAlt, 'M'),
            HotkeyAction.Like => new(CtrlAlt, 'L'),
            HotkeyAction.ShowHide => new(CtrlAlt, 'H'),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }

    /// <summary>
    /// The gesture for <paramref name="action"/>: the saved one; none when saved empty; the default when it isn't saved
    /// or can't be read. <paramref name="useDefaults"/> false leaves unsaved actions without a gesture.
    /// </summary>
    public static HotkeyGesture? Resolve(IReadOnlyDictionary<string, string>? saved, HotkeyAction action, bool useDefaults = true)
    {
        var fallback = useDefaults ? DefaultFor(action) : (HotkeyGesture?)null;
        if (Saved(saved, action) is not { } text)
        {
            return fallback;
        }

        return string.IsNullOrWhiteSpace(text) ? null : HotkeyGesture.Parse(text) ?? fallback;
    }

    /// <summary>Every action that has a gesture.</summary>
    public static IReadOnlyDictionary<HotkeyAction, HotkeyGesture> ResolveAll(IReadOnlyDictionary<string, string>? saved, bool useDefaults = true)
    {
        var map = new Dictionary<HotkeyAction, HotkeyGesture>();
        foreach (var action in Actions)
        {
            if (Resolve(saved, action, useDefaults) is { } gesture)
            {
                map[action] = gesture;
            }
        }

        return map;
    }

    /// <summary>
    /// Actions whose gesture an earlier action (in <see cref="Actions"/> order) already uses, mapped to that action.
    /// Only the earlier one can be registered.
    /// </summary>
    public static IReadOnlyDictionary<HotkeyAction, HotkeyAction> Conflicts(IReadOnlyDictionary<HotkeyAction, HotkeyGesture> gestures)
    {
        var owners = new Dictionary<HotkeyGesture, HotkeyAction>();
        var conflicts = new Dictionary<HotkeyAction, HotkeyAction>();
        foreach (var action in Actions)
        {
            if (!gestures.TryGetValue(action, out var gesture))
            {
                continue;
            }

            if (owners.TryGetValue(gesture, out var owner))
            {
                conflicts[action] = owner;
            }
            else
            {
                owners[gesture] = action;
            }
        }

        return conflicts;
    }

    /// <summary>The settings value for <paramref name="gesture"/>: its text, or empty for "no shortcut".</summary>
    public static string ToSetting(HotkeyGesture? gesture) => gesture?.ToString() ?? string.Empty;

    // Settings keys are matched case-insensitively (the file may have been edited by hand).
    private static string? Saved(IReadOnlyDictionary<string, string>? saved, HotkeyAction action)
    {
        if (saved is null)
        {
            return null;
        }

        var name = action.ToString();
        if (saved.TryGetValue(name, out var exact))
        {
            return exact;
        }

        foreach (var (key, value) in saved)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}
