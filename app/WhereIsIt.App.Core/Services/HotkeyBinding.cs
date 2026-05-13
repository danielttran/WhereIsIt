using System;
using System.Collections.Generic;
using System.Text;

namespace WhereIsIt.App.Services;

/// <summary>
/// Parses Everything-style hotkey strings (e.g. "Ctrl+Alt+W") into the Win32
/// modifier flags + virtual-key code needed for <c>RegisterHotKey</c>.
/// </summary>
public sealed class HotkeyBinding
{
    public const uint ModAlt     = 0x1;
    public const uint ModControl = 0x2;
    public const uint ModShift   = 0x4;
    public const uint ModWin     = 0x8;

    public uint Modifiers { get; }
    public uint VirtualKey { get; }
    public string KeyName { get; }

    private HotkeyBinding(uint modifiers, uint vk, string keyName)
    {
        Modifiers = modifiers;
        VirtualKey = vk;
        KeyName = keyName;
    }

    public static HotkeyBinding? Parse(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;

        uint mods = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control": mods |= ModControl; break;
                case "alt":     mods |= ModAlt; break;
                case "shift":   mods |= ModShift; break;
                case "win":
                case "super":
                case "meta":    mods |= ModWin; break;
                default: return null;
            }
        }

        var keyToken = parts[^1];
        var vk = ParseVk(keyToken);
        if (vk is null) return null;

        return new HotkeyBinding(mods, vk.Value, FormatKeyName(keyToken));
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if ((Modifiers & ModControl) != 0) sb.Append("Ctrl+");
        if ((Modifiers & ModAlt)     != 0) sb.Append("Alt+");
        if ((Modifiers & ModShift)   != 0) sb.Append("Shift+");
        if ((Modifiers & ModWin)     != 0) sb.Append("Win+");
        sb.Append(KeyName);
        return sb.ToString();
    }

    private static uint? ParseVk(string token)
    {
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z') return (uint)c;
            if (c is >= '0' and <= '9') return (uint)c;
        }
        if (token.Length >= 2 && (token[0] == 'F' || token[0] == 'f')
            && int.TryParse(token[1..], out var n) && n is >= 1 and <= 24)
        {
            return (uint)(0x70 + (n - 1));
        }
        return token.ToLowerInvariant() switch
        {
            "space"    => 0x20u,
            "enter"    => 0x0Du,
            "tab"      => 0x09u,
            "escape"   => 0x1Bu,
            "esc"      => 0x1Bu,
            "back"     => 0x08u,
            "delete"   => 0x2Eu,
            "insert"   => 0x2Du,
            "home"     => 0x24u,
            "end"      => 0x23u,
            "pageup"   => 0x21u,
            "pagedown" => 0x22u,
            _ => null
        };
    }

    private static string FormatKeyName(string token)
    {
        if (token.Length == 1) return char.ToUpperInvariant(token[0]).ToString();
        if (token.Length >= 2 && (token[0] == 'F' || token[0] == 'f'))
            return "F" + token[1..];
        return char.ToUpperInvariant(token[0]) + token[1..].ToLowerInvariant();
    }
}
