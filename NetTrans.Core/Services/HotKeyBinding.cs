namespace NetTrans.Services;

/// <summary>
/// Modifier flags for a global hot key. The values are Win32's own MOD_ALT,
/// MOD_CONTROL, MOD_SHIFT and MOD_WIN, so the shell can hand them straight to
/// RegisterHotKey -- but they are spelled out here rather than imported, to
/// keep this half of the app free of Win32.
/// </summary>
[Flags]
public enum HotKeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
}

/// <summary>
/// The 老板键 setting, which is stored the way it is displayed ("Ctrl + Alt +
/// H") because that is what has to survive a restart and reappear in the sheet.
/// </summary>
public readonly record struct HotKeyBinding(HotKeyModifiers Modifiers, int VirtualKey)
{
    /// <summary>The default the settings sheet ships with.</summary>
    public static HotKeyBinding Default { get; } = new(HotKeyModifiers.Control | HotKeyModifiers.Alt, 'H');

    /// <summary>
    /// Parses a displayed combination. Returns null for anything that would not
    /// register: no modifier (which would swallow a bare key system-wide), no
    /// key, or a key this does not know how to name.
    /// </summary>
    public static HotKeyBinding? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;

        var modifiers = HotKeyModifiers.None;
        int? key = null;

        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= HotKeyModifiers.Control; continue;
                case "ALT": modifiers |= HotKeyModifiers.Alt; continue;
                case "SHIFT": modifiers |= HotKeyModifiers.Shift; continue;
                case "WIN" or "WINDOWS": modifiers |= HotKeyModifiers.Windows; continue;
            }

            // Two keys is not a combination anyone meant to type.
            if (key is not null) return null;

            key = VirtualKeyFor(part);
            if (key is null) return null;
        }

        return modifiers != HotKeyModifiers.None && key is { } code ? new HotKeyBinding(modifiers, code) : null;
    }

    /// <summary>Back to the sheet's spelling, in the order Windows shows them.</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);

        if (Modifiers.HasFlag(HotKeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotKeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotKeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotKeyModifiers.Windows)) parts.Add("Win");

        parts.Add(NameFor(VirtualKey));

        return string.Join(" + ", parts);
    }

    private static int? VirtualKeyFor(string key)
    {
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);

            // VK_A..VK_Z and VK_0..VK_9 are the ASCII codes themselves.
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
            return null;
        }

        if (key.Length is 2 or 3 &&
            (key[0] is 'F' or 'f') &&
            int.TryParse(key[1..], out int n) &&
            n is >= 1 and <= 24)
        {
            return 0x70 + n - 1; // VK_F1
        }

        return key.ToUpperInvariant() switch
        {
            "SPACE" => 0x20,
            "HOME" => 0x24,
            "END" => 0x23,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            _ => null,
        };
    }

    private static string NameFor(int virtualKey) => virtualKey switch
    {
        >= 'A' and <= 'Z' or >= '0' and <= '9' => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x87 => "F" + (virtualKey - 0x70 + 1),
        0x20 => "Space",
        0x24 => "Home",
        0x23 => "End",
        0x2D => "Insert",
        0x2E => "Delete",
        _ => "?",
    };
}
