using System;
using System.Runtime.InteropServices;

namespace Emptor.GameData;

/// <summary>
/// Real Windows keystroke messages to the game window. Nothing done from managed
/// FFXIVClientStructs code (SetText, InsertText, input callbacks, RunSearch)
/// makes the market-board search actually run — only genuine typed input does.
/// SendMessage to the game's own window is processed synchronously by the game
/// thread (which is where we call it from), so there is no deadlock.
/// </summary>
public static class Keyboard
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_CHAR = 0x0102;
    private const int VK_BACK = 0x08;
    private const int VK_RETURN = 0x0D;

    private static nint cached;

    public static nint GameWindow
    {
        get
        {
            if (cached == 0)
                cached = FindWindowW("FFXIVGAME", null);
            return cached;
        }
    }

    public static bool Available => GameWindow != 0;

    public static void TypeChar(char c)
    {
        var hwnd = GameWindow;
        if (hwnd == 0)
            return;
        var vk = (nint)(VkKeyScanW(c) & 0xFF);
        SendMessageW(hwnd, WM_KEYDOWN, vk, 0);
        SendMessageW(hwnd, WM_CHAR, c, 0);
        SendMessageW(hwnd, WM_KEYUP, vk, 0);
    }

    public static void TypeString(string s)
    {
        foreach (var c in s)
            TypeChar(c);
    }

    public static void Backspace(int count)
    {
        var hwnd = GameWindow;
        if (hwnd == 0)
            return;
        for (var i = 0; i < count; i++)
        {
            SendMessageW(hwnd, WM_KEYDOWN, VK_BACK, 0);
            SendMessageW(hwnd, WM_CHAR, VK_BACK, 0);
            SendMessageW(hwnd, WM_KEYUP, VK_BACK, 0);
        }
    }

    /// <summary>Real Enter keystroke — submits a text field.</summary>
    public static void Enter()
    {
        var hwnd = GameWindow;
        if (hwnd == 0)
            return;
        SendMessageW(hwnd, WM_KEYDOWN, VK_RETURN, 0);
        SendMessageW(hwnd, WM_CHAR, VK_RETURN, 0);
        SendMessageW(hwnd, WM_KEYUP, VK_RETURN, 0);
    }
}
