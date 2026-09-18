"""Hardware-like typing for the e2e test: physical keys by scan code via SendInput, with a pause between keys.

    python typer.py "<text>" [delay_ms]

<text> is written in US-layout letters ("ghbdtn" = the keys that give "привет" in RU); the receiving app
interprets the keys in whatever layout it currently has, exactly like a real keyboard. Tokens: {BS} {ENTER}
{TAB} {F9} {PAUSE} {ALTSHIFT} {SLEEP:ms}.
"""
import ctypes
import ctypes.wintypes as w
import sys
import time

user32 = ctypes.windll.user32

INPUT_KEYBOARD = 1
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_SCANCODE = 0x0008
KEYEVENTF_EXTENDEDKEY = 0x0001


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", w.WORD), ("wScan", w.WORD), ("dwFlags", w.DWORD), ("time", w.DWORD), ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]


class _U(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT), ("pad", ctypes.c_byte * 32)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", w.DWORD), ("u", _U)]


def send(scan, up=False, extended=False):
    inp = INPUT()
    inp.type = INPUT_KEYBOARD
    inp.u.ki.wScan = scan
    inp.u.ki.dwFlags = KEYEVENTF_SCANCODE | (KEYEVENTF_KEYUP if up else 0) | (KEYEVENTF_EXTENDEDKEY if extended else 0)
    n = user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))
    if n != 1:
        log(f"SendInput failed scan={scan:X} err={ctypes.windll.kernel32.GetLastError()}")
        raise SystemExit("SendInput failed")


def tap(scan, extended=False):
    send(scan, False, extended)
    send(scan, True, extended)


LOG = __import__("os").path.join(__import__("os").path.dirname(__file__), "typer.log")


def log(msg):
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(msg + chr(10))


def main():
    text = sys.argv[1]
    delay = int(sys.argv[2]) / 1000 if len(sys.argv) > 2 else 0.04
    expect = int(sys.argv[3], 16) if len(sys.argv) > 3 else 0
    # never type into somebody else's window: wait briefly for the expected window to be in the foreground
    fg = 0
    for _ in range(100):
        fg = user32.GetForegroundWindow()
        if not expect or fg == expect:
            break
        time.sleep(0.02)
    log(f"start text={text!r} fg={fg:X} expect={expect:X}")
    if expect and fg != expect:
        log("ABORT: expected window is not in the foreground")
        raise SystemExit(2)
    # map characters through the US layout (handle only, not activated)
    hkl = user32.LoadKeyboardLayoutW("00000409", 0)
    SHIFT_SCAN = 0x2A
    tokens = {"BS": 0x0E, "ENTER": 0x1C, "TAB": 0x0F, "F9": 0x43, "PAUSE": 0x45}
    t0 = time.perf_counter(); sent = 0
    i = 0
    while i < len(text):
        ch = text[i]
        if ch == "{":
            j = text.index("}", i)
            tok = text[i + 1:j]
            i = j + 1
            if tok.startswith("SLEEP:"):
                time.sleep(int(tok[6:]) / 1000)
                continue
            if tok == "ALTSHIFT":
                send(0x38); send(SHIFT_SCAN); send(SHIFT_SCAN, True); send(0x38, True)
            elif tok == "PAUSE":
                # Pause/Break: E1 1D 45 — SendInput accepts the plain 0x45 scan code with the extended flag off
                tap(0x45)
            else:
                tap(tokens[tok], extended=tok == "ENTER" and False)
            time.sleep(delay)
            continue
        i += 1
        r = user32.VkKeyScanExW(ord(ch), hkl)
        if r == -1:
            raise SystemExit(f"cannot type {ch!r} on the US layout")
        vk, mods = r & 0xFF, (r >> 8) & 0xFF
        scan = user32.MapVirtualKeyExW(vk, 0, hkl)
        if mods & 1:
            send(SHIFT_SCAN)
        tap(scan)
        if mods & 1:
            send(SHIFT_SCAN, True)
        sent += 2
        time.sleep(delay)
    log(f"done: {sent} events in {(time.perf_counter() - t0) * 1000:.1f} ms (delay {delay * 1000:.0f} ms)")


if __name__ == "__main__":
    try:
        main()
    except BaseException as e:  # pythonw has no console — leave a trace next to the script
        import os, traceback
        with open(os.path.join(os.path.dirname(__file__), "typer.log"), "a", encoding="utf-8") as f:
            f.write(traceback.format_exc())
        raise
