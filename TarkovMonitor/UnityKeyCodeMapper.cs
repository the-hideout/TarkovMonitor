using System.Collections.Generic;

namespace TarkovMonitor
{
    /// <summary>
    /// Maps Unity KeyCode names to Windows Virtual Key codes.
    /// Based on: https://gist.github.com/jhincapie/8a4c95d5cbe81d79b329ffc37e9f6c97
    /// </summary>
    public static class UnityKeyCodeMapper
    {
        private static readonly Dictionary<string, int> UnityToVirtualKey = new()
        {
            // Function keys
            ["F1"] = 0x70,
            ["F2"] = 0x71,
            ["F3"] = 0x72,
            ["F4"] = 0x73,
            ["F5"] = 0x74,
            ["F6"] = 0x75,
            ["F7"] = 0x76,
            ["F8"] = 0x77,
            ["F9"] = 0x78,
            ["F10"] = 0x79,
            ["F11"] = 0x7A,
            ["F12"] = 0x7B,
            ["F13"] = 0x7C,
            ["F14"] = 0x7D,
            ["F15"] = 0x7E,

            // Alphanumeric - letters
            ["A"] = 0x41,
            ["B"] = 0x42,
            ["C"] = 0x43,
            ["D"] = 0x44,
            ["E"] = 0x45,
            ["F"] = 0x46,
            ["G"] = 0x47,
            ["H"] = 0x48,
            ["I"] = 0x49,
            ["J"] = 0x4A,
            ["K"] = 0x4B,
            ["L"] = 0x4C,
            ["M"] = 0x4D,
            ["N"] = 0x4E,
            ["O"] = 0x4F,
            ["P"] = 0x50,
            ["Q"] = 0x51,
            ["R"] = 0x52,
            ["S"] = 0x53,
            ["T"] = 0x54,
            ["U"] = 0x55,
            ["V"] = 0x56,
            ["W"] = 0x57,
            ["X"] = 0x58,
            ["Y"] = 0x59,
            ["Z"] = 0x5A,

            // Alphanumeric - numbers (top row)
            ["Alpha0"] = 0x30,
            ["Alpha1"] = 0x31,
            ["Alpha2"] = 0x32,
            ["Alpha3"] = 0x33,
            ["Alpha4"] = 0x34,
            ["Alpha5"] = 0x35,
            ["Alpha6"] = 0x36,
            ["Alpha7"] = 0x37,
            ["Alpha8"] = 0x38,
            ["Alpha9"] = 0x39,

            // Numpad
            ["Keypad0"] = 0x60,
            ["Keypad1"] = 0x61,
            ["Keypad2"] = 0x62,
            ["Keypad3"] = 0x63,
            ["Keypad4"] = 0x64,
            ["Keypad5"] = 0x65,
            ["Keypad6"] = 0x66,
            ["Keypad7"] = 0x67,
            ["Keypad8"] = 0x68,
            ["Keypad9"] = 0x69,
            ["KeypadMultiply"] = 0x6A,
            ["KeypadPlus"] = 0x6B,
            ["KeypadMinus"] = 0x6D,
            ["KeypadPeriod"] = 0x6E,
            ["KeypadDivide"] = 0x6F,
            ["KeypadEnter"] = 0x0D,
            ["KeypadEquals"] = 0xBB,

            // Navigation
            ["UpArrow"] = 0x26,
            ["DownArrow"] = 0x28,
            ["LeftArrow"] = 0x25,
            ["RightArrow"] = 0x27,
            ["Home"] = 0x24,
            ["End"] = 0x23,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["Insert"] = 0x2D,
            ["Delete"] = 0x2E,

            // Modifiers
            ["LeftShift"] = 0xA0,
            ["RightShift"] = 0xA1,
            ["LeftControl"] = 0xA2,
            ["RightControl"] = 0xA3,
            ["LeftAlt"] = 0xA4,
            ["RightAlt"] = 0xA5,
            ["LeftWindows"] = 0x5B,
            ["RightWindows"] = 0x5C,
            ["LeftCommand"] = 0x5B,
            ["RightCommand"] = 0x5C,

            // Special keys
            ["Backspace"] = 0x08,
            ["Tab"] = 0x09,
            ["Return"] = 0x0D,
            ["Escape"] = 0x1B,
            ["Space"] = 0x20,
            ["Pause"] = 0x13,
            ["Clear"] = 0x0C,
            ["Help"] = 0x2F,
            ["Print"] = 0x2A,
            ["SysReq"] = 0x2C,
            ["Break"] = 0x03,
            ["Menu"] = 0x5D,

            // Lock keys
            ["Numlock"] = 0x90,
            ["CapsLock"] = 0x14,
            ["ScrollLock"] = 0x91,

            // OEM keys (punctuation)
            ["BackQuote"] = 0xC0,
            ["Minus"] = 0xBD,
            ["Equals"] = 0xBB,
            ["Plus"] = 0xBB,
            ["LeftBracket"] = 0xDB,
            ["RightBracket"] = 0xDD,
            ["Backslash"] = 0xDC,
            ["Semicolon"] = 0xBA,
            ["Colon"] = 0xBA,
            ["Quote"] = 0xDE,
            ["DoubleQuote"] = 0xDE,
            ["Comma"] = 0xBC,
            ["Period"] = 0xBE,
            ["Slash"] = 0xBF,
            ["Question"] = 0xBF,
            ["Less"] = 0xBC,
            ["Greater"] = 0xBE,
            ["At"] = 0x32,
            ["Exclaim"] = 0x31,
            ["Hash"] = 0x33,
            ["Dollar"] = 0x34,
            ["Percent"] = 0x35,
            ["Caret"] = 0x36,
            ["Ampersand"] = 0x37,
            ["Asterisk"] = 0x38,
            ["LeftParen"] = 0x39,
            ["RightParen"] = 0x30,
            ["Underscore"] = 0xBD,
            ["Tilde"] = 0xC0,
        };

        /// <summary>
        /// Converts a Unity KeyCode name to a Windows Virtual Key code.
        /// </summary>
        /// <param name="unityKeyName">The Unity KeyCode name (e.g., "F12", "Equals", "Space")</param>
        /// <param name="defaultKey">The default VK code to return if not found (default: F12 = 0x7B)</param>
        /// <returns>The Windows Virtual Key code</returns>
        public static int ToVirtualKey(string unityKeyName, int defaultKey = 0x7B)
        {
            if (string.IsNullOrEmpty(unityKeyName))
                return defaultKey;

            return UnityToVirtualKey.TryGetValue(unityKeyName, out var vk) ? vk : defaultKey;
        }
    }
}
