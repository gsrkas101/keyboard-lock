// Unit tests for the key-blocking logic. Built and run by build.ps1 (no real hook involved).

using System;

namespace KeyboardLock
{
    static class Tests
    {
        const int DOWN = Native.WM_KEYDOWN, UP = Native.WM_KEYUP, SYSDOWN = Native.WM_SYSKEYDOWN, SYSUP = Native.WM_SYSKEYUP;
        const uint NONE = 0, INJECTED = Native.LLKHF_INJECTED, EXTENDED = Native.LLKHF_EXTENDED;
        const int F24 = 0x87, A = 0x41, B = 0x42, K = 0x4B, ESC = 0x1B, LCTRL = 0xA2, LALT = 0xA4, LSHIFT = 0xA0, OEM = 0xFF;

        static int failures;

        static void Check(bool condition, string name)
        {
            Console.WriteLine((condition ? "PASS  " : "FAIL  ") + name);
            if (!condition) failures++;
        }

        static KeyFilter NewFilter(HotKey toggle, int[] toggleCount)
        {
            var f = new KeyFilter { Toggle = toggle };
            f.Toggled = () => toggleCount[0]++;
            return f;
        }

        static bool Press(KeyFilter f, int vk) { return f.Process(DOWN, vk, 0, NONE); }
        static bool Release(KeyFilter f, int vk) { return f.Process(UP, vk, 0, NONE); }

        static int Main()
        {
            int[] count = { 0 };
            var f24 = new HotKey(F24, 0x76, false, 0);

            var f = NewFilter(f24, count);
            Check(!Press(f, A) && !Release(f, A), "unlocked: normal keys pass");

            Check(Press(f, F24) && f.Locked && count[0] == 1, "lock key locks and is swallowed");
            Check(Press(f, F24) && Press(f, F24) && f.Locked && count[0] == 1, "auto-repeat of lock key doesn't flip again");
            Check(Release(f, F24), "lock key release is swallowed");
            Check(Press(f, A) && Release(f, A), "locked: key down and up blocked");
            Check(f.Process(SYSDOWN, LALT, 0, NONE) && f.Process(SYSUP, LALT, 0, NONE), "locked: Alt blocked");
            Check(Press(f, 0x5B) && Release(f, 0x5B), "locked: Windows key blocked");
            Check(f.Process(DOWN, A, 0, INJECTED) && f.Process(UP, A, 0, INJECTED), "locked: software-typed keys blocked");
            Check(Press(f, 0xAF) && Release(f, 0xAF) && Press(f, 0xAD) && Press(f, 0x70) && Press(f, 0x7B),
                "locked: volume, mute and F1-F12 blocked");
            Release(f, 0xAD); Release(f, 0x70); Release(f, 0x7B);
            Check(Press(f, F24) && !f.Locked && count[0] == 2 && Release(f, F24), "lock key unlocks");
            Check(!Press(f, A) && !Release(f, A), "unlocked again: keys pass");

            // No stuck keys: a key held while locking must get its key-up.
            f = NewFilter(f24, count);
            Press(f, LSHIFT);
            Press(f, F24);
            Check(f.Locked && !Release(f, LSHIFT), "key held before locking: its release passes");
            Release(f, F24);

            // A key pressed while locked and released after unlocking: up stays blocked.
            Check(Press(f, B), "locked: B down blocked");
            Press(f, F24);
            Release(f, F24);
            Check(!f.Locked && Release(f, B), "key pressed while locked: release blocked after unlock");

            // Lock key still works while Shift is held (e.g. kid holding a key).
            f = NewFilter(f24, count);
            Press(f, LSHIFT);
            Check(Press(f, F24) && f.Locked, "single lock key works with extra modifiers held");

            // Combination lock key: Ctrl+Alt+K.
            var combo = new HotKey(K, 0x25, false, HotKey.Ctrl | HotKey.Alt);
            f = NewFilter(combo, count);
            Check(!Press(f, K) && !Release(f, K) && !f.Locked, "combo: K alone types normally");
            Press(f, LCTRL);
            f.Process(SYSDOWN, LALT, 0, NONE);
            Check(f.Process(SYSDOWN, K, 0, NONE) && f.Locked, "combo: Ctrl+Alt+K locks");
            f.Process(SYSUP, K, 0, NONE);
            Check(!Release(f, LCTRL) && !f.Process(SYSUP, LALT, 0, NONE), "combo: Ctrl/Alt released after locking pass");
            Check(Press(f, K) && f.Locked, "combo: K alone while locked is blocked, stays locked");
            Release(f, K);
            Check(Press(f, LCTRL) && f.Process(SYSDOWN, LALT, 0, NONE), "combo: modifiers blocked while locked");
            Check(f.Process(SYSDOWN, K, 0, NONE) && !f.Locked, "combo: Ctrl+Alt+K unlocks");
            Check(f.Process(SYSUP, K, 0, NONE) && Release(f, LCTRL) && f.Process(SYSUP, LALT, 0, NONE),
                "combo: releases of keys blocked while locked stay blocked");

            // OEM key without a virtual-key code: matched by scan code.
            var oem = new HotKey(OEM, 0x6E, true, 0);
            f = NewFilter(oem, count);
            Check(!f.Process(DOWN, OEM, 0x6F, EXTENDED) && !f.Locked, "OEM: other scan code doesn't match");
            f.Process(UP, OEM, 0x6F, EXTENDED);
            Check(f.Process(DOWN, OEM, 0x6E, EXTENDED) && f.Locked, "OEM: matching scan code locks");

            // Learning a new key.
            f = NewFilter(null, count);
            HotKey learned = null;
            bool called = false;
            f.BeginLearn(k => { learned = k; called = true; });
            Check(!Press(f, LCTRL), "learn: modifiers pass");
            Check(Press(f, K) && called && learned != null && learned.Vk == K && learned.Mods == HotKey.Ctrl,
                "learn: records Ctrl+K");
            Check(Release(f, K) && !Release(f, LCTRL), "learn: key release swallowed, modifier release passes");
            called = false;
            f.BeginLearn(k => { learned = k; called = true; });
            Check(Press(f, ESC) && called && learned == null && !f.IsLearning, "learn: Esc cancels");
            Release(f, ESC);

            // Reset after Win+L (key-ups lost).
            f = NewFilter(f24, count);
            Press(f, F24);
            f.ResetKeyState();
            f.Locked = false;
            Check(Press(f, F24) && f.Locked, "after reset: lock key works on first press");

            // Settings round trip.
            var s = new Settings { Toggle = new HotKey(OEM, 0x6E, true, HotKey.Win), ShowOverlay = false };
            Settings back = Settings.Parse(s.ToLines());
            Check(back.Toggle != null && back.Toggle.Vk == OEM && back.Toggle.Scan == 0x6E && back.Toggle.Extended
                  && back.Toggle.Mods == HotKey.Win && !back.ShowOverlay, "settings round trip");
            Check(Settings.Parse(new[] { "ToggleVk=999", "junk" }).Toggle == HotKey.Default, "settings: invalid key falls back to Ctrl+Alt+K");
            Check(Settings.Parse(new string[0]).Toggle.ToString() == "Ctrl+Alt+K", "settings: default key is Ctrl+Alt+K");
            Check(new HotKey(A, 0x1E, false, 0).IsTypingKey && !new HotKey(F24, 0x76, false, 0).IsTypingKey,
                "typing-key warning detection");

            Console.WriteLine(failures == 0 ? "\nAll tests passed." : "\n" + failures + " test(s) FAILED.");
            return failures == 0 ? 0 : 1;
        }
    }
}
