using Windows.System;

namespace WinKVM.Input;

/// Maps Windows virtual keys to Raritan e-RIC keycodes.
/// Raritan uses AT scan codes − 1 (not USB HID or X11 keysyms).
public static class KeyboardHandler
{
    public static ushort? RaritanKeyCode(VirtualKey vk)
        => WinToRaritan.TryGetValue(vk, out ushort code) ? code : null;

    private static readonly Dictionary<VirtualKey, ushort> WinToRaritan = new()
    {
        // Letters
        { VirtualKey.A, 29 }, { VirtualKey.B, 47 }, { VirtualKey.C, 45 },
        { VirtualKey.D, 31 }, { VirtualKey.E, 17 }, { VirtualKey.F, 32 },
        { VirtualKey.G, 33 }, { VirtualKey.H, 34 }, { VirtualKey.I, 22 },
        { VirtualKey.J, 35 }, { VirtualKey.K, 36 }, { VirtualKey.L, 37 },
        { VirtualKey.M, 49 }, { VirtualKey.N, 48 }, { VirtualKey.O, 23 },
        { VirtualKey.P, 24 }, { VirtualKey.Q, 15 }, { VirtualKey.R, 18 },
        { VirtualKey.S, 30 }, { VirtualKey.T, 19 }, { VirtualKey.U, 21 },
        { VirtualKey.V, 46 }, { VirtualKey.W, 16 }, { VirtualKey.X, 44 },
        { VirtualKey.Y, 20 }, { VirtualKey.Z, 43 },

        // Numbers
        { VirtualKey.Number1, 1 }, { VirtualKey.Number2, 2 }, { VirtualKey.Number3, 3 },
        { VirtualKey.Number4, 4 }, { VirtualKey.Number5, 5 }, { VirtualKey.Number6, 6 },
        { VirtualKey.Number7, 7 }, { VirtualKey.Number8, 8 }, { VirtualKey.Number9, 9 },
        { VirtualKey.Number0, 10 },

        // Special keys
        { VirtualKey.Enter,      27 }, { VirtualKey.Escape,    59 },
        { VirtualKey.Back,       13 }, { VirtualKey.Tab,       14 },
        { VirtualKey.Space,      56 },
        { (VirtualKey)189, 11 },  // Minus (OEM_MINUS)
        { (VirtualKey)187, 12 },  // Equal (OEM_PLUS)
        { (VirtualKey)219, 25 },  // Left Bracket
        { (VirtualKey)221, 26 },  // Right Bracket
        { (VirtualKey)220, 40 },  // Backslash
        { (VirtualKey)186, 38 },  // Semicolon
        { (VirtualKey)222, 39 },  // Quote
        { (VirtualKey)192, 0  },  // Grave/Tilde
        { (VirtualKey)188, 50 },  // Comma
        { (VirtualKey)190, 51 },  // Period
        { (VirtualKey)191, 52 },  // Slash
        { VirtualKey.CapitalLock, 28 },

        // Function keys
        { VirtualKey.F1,  60 }, { VirtualKey.F2,  61 }, { VirtualKey.F3,  62 },
        { VirtualKey.F4,  63 }, { VirtualKey.F5,  64 }, { VirtualKey.F6,  65 },
        { VirtualKey.F7,  66 }, { VirtualKey.F8,  67 }, { VirtualKey.F9,  68 },
        { VirtualKey.F10, 69 }, { VirtualKey.F11, 70 }, { VirtualKey.F12, 71 },

        // Navigation
        { VirtualKey.Home,     76 }, { VirtualKey.PageUp,   77 },
        { VirtualKey.Delete,   78 }, { VirtualKey.End,      79 },
        { VirtualKey.PageDown, 80 }, { VirtualKey.Right,    84 },
        { VirtualKey.Left,     82 }, { VirtualKey.Down,     83 },
        { VirtualKey.Up,       81 }, { VirtualKey.Insert,   75 },

        // Modifiers
        { VirtualKey.LeftControl,  54 }, { VirtualKey.LeftShift,  41 },
        { VirtualKey.LeftMenu,     55 }, { VirtualKey.LeftWindows, 105 },
        { VirtualKey.RightControl, 58 }, { VirtualKey.RightShift,  53 },
        { VirtualKey.RightMenu,    57 }, { VirtualKey.RightWindows, 106 },

        // Numpad
        { VirtualKey.NumberPad0, 100 }, { VirtualKey.NumberPad1, 95 },
        { VirtualKey.NumberPad2, 96  }, { VirtualKey.NumberPad3, 97 },
        { VirtualKey.NumberPad4, 91  }, { VirtualKey.NumberPad5, 92 },
        { VirtualKey.NumberPad6, 93  }, { VirtualKey.NumberPad7, 86 },
        { VirtualKey.NumberPad8, 87  }, { VirtualKey.NumberPad9, 88 },
        { VirtualKey.Decimal,   101 }, { VirtualKey.Multiply, 94 },
        { VirtualKey.Add,       89  }, { VirtualKey.Divide,   90 },
        { VirtualKey.Subtract,  99  },

        // Print Screen / Scroll Lock / Pause
        { VirtualKey.Snapshot, 72 }, { VirtualKey.Scroll, 73 }, { VirtualKey.Pause, 74 },
    };
}
