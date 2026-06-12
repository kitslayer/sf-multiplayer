#!/usr/bin/env python3
# Minimal kernel-level keyboard injector for unattended SF testing on Wayland.
# Writes directly to /dev/uinput (no ydotoold, no sudo — relies on the user
# being in the 'input' group + the device ACL). Creates a virtual keyboard,
# emits key events to whatever window is FOCUSED, then destroys the device.
#
# Games accept these because they look like a real keyboard to the kernel
# (unlike xdotool's XSendEvent, which Unity/Wine often ignore on Wayland).
#
# Usage:
#   uinput-inject.py hold <KEY> <seconds>      # press, wait, release
#   uinput-inject.py tap <KEY> [count]         # quick press/release
#   uinput-inject.py script <FILE>             # lines: "hold KEY SECS" / "tap KEY" / "sleep SECS"
#
# KEY is a Linux KEY_* name without the prefix, e.g. A D W SPACE LEFT RIGHT.
import os, sys, struct, time, fcntl

# --- uinput / input-event constants (linux/uinput.h, linux/input.h) ---
UINPUT_MAX_NAME_SIZE = 80
EV_SYN, EV_KEY = 0x00, 0x01
SYN_REPORT = 0
UI_SET_EVBIT  = 0x40045564
UI_SET_KEYBIT = 0x40045565
UI_DEV_CREATE = 0x5501
UI_DEV_DESTROY = 0x5502
BUS_USB = 0x03

# Minimal KEY_* code map (linux/input-event-codes.h) — extend as needed.
KEYS = {
    "A":30,"B":48,"C":46,"D":32,"E":18,"F":33,"G":34,"H":35,"I":23,"J":36,
    "K":37,"L":38,"M":50,"N":49,"O":24,"P":25,"Q":16,"R":19,"S":31,"T":20,
    "U":22,"V":47,"W":17,"X":45,"Y":21,"Z":44,
    "SPACE":57,"ENTER":28,"ESC":1,"LEFT":105,"RIGHT":106,"UP":103,"DOWN":108,
    "LSHIFT":42,"LCTRL":29,"TAB":15,"F2":60,"F4":62,
    "1":2,"2":3,"3":4,"4":5,
}

def emit(fd, etype, code, value):
    # struct input_event: timeval(2x long) + u16 type + u16 code + s32 value
    os.write(fd, struct.pack("llHHi", 0, 0, etype, code, value))

def syn(fd): emit(fd, EV_SYN, SYN_REPORT, 0)

def open_dev():
    fd = os.open("/dev/uinput", os.O_WRONLY | os.O_NONBLOCK)
    fcntl.ioctl(fd, UI_SET_EVBIT, EV_KEY)
    for code in set(KEYS.values()):
        fcntl.ioctl(fd, UI_SET_KEYBIT, code)
    # uinput_user_dev: name[80] + input_id(4xu16) + ff_effects_max(u32) + 4*64 abs arrays
    name = b"sf-test-kbd".ljust(UINPUT_MAX_NAME_SIZE, b"\0")
    dev = name + struct.pack("HHHH", BUS_USB, 0x1, 0x1, 0x1) + struct.pack("i", 0)
    dev += b"\0" * (4 * 64 * 4)
    os.write(fd, dev)
    fcntl.ioctl(fd, UI_DEV_CREATE)
    time.sleep(0.3)  # let the compositor enumerate the device
    return fd

def close_dev(fd):
    try: fcntl.ioctl(fd, UI_DEV_DESTROY)
    except Exception: pass
    os.close(fd)

def press(fd, code):   emit(fd, EV_KEY, code, 1); syn(fd)
def release(fd, code): emit(fd, EV_KEY, code, 0); syn(fd)

def do_hold(fd, key, secs):
    c = KEYS[key.upper()]; press(fd, c); time.sleep(float(secs)); release(fd, c)

def do_tap(fd, key, count=1):
    c = KEYS[key.upper()]
    for _ in range(int(count)):
        press(fd, c); time.sleep(0.05); release(fd, c); time.sleep(0.08)

def main():
    if len(sys.argv) < 2: print(__doc__); return 2
    fd = open_dev()
    try:
        cmd = sys.argv[1]
        if cmd == "hold": do_hold(fd, sys.argv[2], sys.argv[3])
        elif cmd == "tap": do_tap(fd, sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else 1)
        elif cmd == "script":
            for line in open(sys.argv[2]):
                p = line.split()
                if not p or p[0].startswith("#"): continue
                if p[0] == "hold": do_hold(fd, p[1], p[2])
                elif p[0] == "tap": do_tap(fd, p[1], p[2] if len(p) > 2 else 1)
                elif p[0] == "sleep": time.sleep(float(p[1]))
        else: print("unknown cmd", cmd); return 2
    finally:
        close_dev(fd)
    return 0

if __name__ == "__main__":
    sys.exit(main())
