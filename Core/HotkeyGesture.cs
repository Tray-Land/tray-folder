using System.Globalization;

namespace TrayFolder.Core;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}

/// <summary>
/// A global hotkey: modifier keys plus one virtual key (letters, digits, F1-F24 or Space).
/// Pure, so parsing, formatting and validation are covered by plain tests.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public static HotkeyGesture Default { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'F');

    private const uint VkSpace = 0x20;
    private const uint VkF1 = 0x70;
    private const uint VkF24 = 0x87;

    /// <summary>A hotkey needs a modifier (a bare letter would steal typing everywhere) and a supported key.</summary>
    public bool IsValid => Modifiers != HotkeyModifiers.None && IsSupportedKey(VirtualKey);

    public static bool IsSupportedKey(uint key) =>
        key is >= 'A' and <= 'Z' or >= '0' and <= '9' or VkSpace or (>= VkF1 and <= VkF24);

    public override string ToString()
    {
        List<string> parts = [];
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) { parts.Add("Ctrl"); }
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) { parts.Add("Alt"); }
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) { parts.Add("Shift"); }
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) { parts.Add("Win"); }
        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    /// <summary>Parses text like "Ctrl+Alt+F"; null for anything that isn't a valid gesture.</summary>
    public static HotkeyGesture? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        uint? key = null;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= HotkeyModifiers.Control; break;
                case "ALT": modifiers |= HotkeyModifiers.Alt; break;
                case "SHIFT": modifiers |= HotkeyModifiers.Shift; break;
                case "WIN" or "WINDOWS": modifiers |= HotkeyModifiers.Windows; break;
                default:
                    if (key is not null || ParseKey(raw) is not { } parsed)
                    {
                        return null;
                    }

                    key = parsed;
                    break;
            }
        }

        HotkeyGesture gesture = new(modifiers, key ?? 0);
        return gesture.IsValid ? gesture : null;
    }

    private static string KeyName(uint key) => key switch
    {
        VkSpace => "Space",
        >= VkF1 and <= VkF24 => $"F{key - VkF1 + 1}",
        _ => ((char)key).ToString(),
    };

    private static uint? ParseKey(string name)
    {
        string upper = name.ToUpperInvariant();
        if (upper == "SPACE")
        {
            return VkSpace;
        }

        if (upper.Length == 1 && IsSupportedKey(upper[0]))
        {
            return upper[0];
        }

        if (upper.Length is 2 or 3 && upper[0] == 'F' &&
            uint.TryParse(upper.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out uint number) &&
            number is >= 1 and <= 24)
        {
            return VkF1 + number - 1;
        }

        return null;
    }
}

public enum HotkeyResult
{
    /// <summary>The hotkey is registered.</summary>
    Registered,

    /// <summary>The hotkey is turned off.</summary>
    Disabled,

    /// <summary>Another app already owns this combination.</summary>
    InUse,
}

public interface IHotkeyService
{
    /// <summary>What the user asked for; null when the hotkey is off.</summary>
    HotkeyGesture? Gesture { get; }

    /// <summary>Whether <see cref="Gesture"/> is currently registered with Windows.</summary>
    HotkeyResult Status { get; }

    /// <summary>Switches to <paramref name="gesture"/> (null turns the hotkey off) and remembers it.</summary>
    HotkeyResult Set(HotkeyGesture? gesture);
}
