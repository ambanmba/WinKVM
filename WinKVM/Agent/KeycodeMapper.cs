namespace WinKVM.Agent;

/// Maps AI action key names to Raritan keycodes for key_combo actions.
/// Also provides character → keycode+shift mapping for text injection.
public static class KeycodeMapper
{
    public const ushort LeftShift   = 41;
    public const ushort LeftControl = 54;
    public const ushort LeftAlt     = 55;
    public const ushort LeftWin     = 105;

    private static readonly Dictionary<string, ushort> NameToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"]   = LeftControl, ["control"] = LeftControl,
        ["shift"]  = LeftShift,
        ["alt"]    = LeftAlt,
        ["win"]    = LeftWin, ["super"] = LeftWin,
        ["enter"]  = 27,  ["return"] = 27,
        ["esc"]    = 59,  ["escape"] = 59,
        ["tab"]    = 14,
        ["space"]  = 56,
        ["backspace"] = 13,
        ["delete"] = 78,
        ["insert"] = 75,
        ["home"]   = 76, ["end"]     = 79,
        ["pageup"] = 77, ["pagedown"] = 80,
        ["up"]     = 81, ["down"]    = 83, ["left"] = 82, ["right"] = 84,
        ["f1"]  = 60, ["f2"]  = 61, ["f3"]  = 62, ["f4"]  = 63,
        ["f5"]  = 64, ["f6"]  = 65, ["f7"]  = 66, ["f8"]  = 67,
        ["f9"]  = 68, ["f10"] = 69, ["f11"] = 70, ["f12"] = 71,
        ["a"] = 29, ["b"] = 47, ["c"] = 45, ["d"] = 31, ["e"] = 17,
        ["f"] = 32, ["g"] = 33, ["h"] = 34, ["i"] = 22, ["j"] = 35,
        ["k"] = 36, ["l"] = 37, ["m"] = 49, ["n"] = 48, ["o"] = 23,
        ["p"] = 24, ["q"] = 15, ["r"] = 18, ["s"] = 30, ["t"] = 19,
        ["u"] = 21, ["v"] = 46, ["w"] = 16, ["x"] = 44, ["y"] = 20,
        ["z"] = 43,
        ["0"] = 10, ["1"] = 1, ["2"] = 2, ["3"] = 3, ["4"] = 4,
        ["5"] = 5,  ["6"] = 6, ["7"] = 7, ["8"] = 8, ["9"] = 9,
    };

    public static ushort? KeyCode(string name)
        => NameToCode.TryGetValue(name, out var c) ? c : null;

    // Char → (keycode, needsShift) pairs for text injection — populated by static constructor
    private static readonly Dictionary<char, (ushort code, bool shift)> CharMap = new();

    static KeycodeMapper()
    {
        // Lowercase letters
        foreach (var kv in NameToCode)
            if (kv.Key.Length == 1 && char.IsLetter(kv.Key[0]))
                CharMap[kv.Key[0]] = (kv.Value, false);

        // Uppercase → same code + shift
        foreach (var kv in CharMap.ToList())
            if (char.IsLower(kv.Key))
                CharMap[char.ToUpper(kv.Key)] = (kv.Value.code, true);

        // Digits
        for (char c = '0'; c <= '9'; c++)
            CharMap[c] = (NameToCode[c.ToString()], false);

        // Common punctuation
        CharMap[' '] = (56, false);
        CharMap['\n'] = (27, false);
        CharMap['\t'] = (14, false);
        CharMap['-']  = (11, false); CharMap['_']  = (11, true);
        CharMap['=']  = (12, false); CharMap['+']  = (12, true);
        CharMap['[']  = (25, false); CharMap['{']  = (25, true);
        CharMap[']']  = (26, false); CharMap['}']  = (26, true);
        CharMap['\\'] = (40, false); CharMap['|']  = (40, true);
        CharMap[';']  = (38, false); CharMap[':']  = (38, true);
        CharMap['\''] = (39, false); CharMap['"']  = (39, true);
        CharMap['`']  = (0,  false); CharMap['~']  = (0,  true);
        CharMap[',']  = (50, false); CharMap['<']  = (50, true);
        CharMap['.']  = (51, false); CharMap['>']  = (51, true);
        CharMap['/']  = (52, false); CharMap['?']  = (52, true);
        CharMap['!']  = (1,  true);  CharMap['@']  = (2,  true);
        CharMap['#']  = (3,  true);  CharMap['$']  = (4,  true);
        CharMap['%']  = (5,  true);  CharMap['^']  = (6,  true);
        CharMap['&']  = (7,  true);  CharMap['*']  = (8,  true);
        CharMap['(']  = (9,  true);  CharMap[')']  = (10, true);
    }

    public static (ushort code, bool shift)[]? KeyCodesForChar(char ch)
        => CharMap.TryGetValue(ch, out var v) ? [v] : null;
}
