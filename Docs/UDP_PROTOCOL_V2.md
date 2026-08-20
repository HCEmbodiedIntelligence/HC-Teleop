# HC Teleop UDP protocol v2

All numeric fields use little-endian byte order. The complete packet is 162 bytes.

Python format string:

```python
"<4sBIdB21f3H6f3H6f"
```

## Packet layout

| Field | Type | Description |
|---|---:|---|
| Magic | `4s` | ASCII `PICO` |
| Version | `uint8` | `2` |
| Sequence | `uint32` | Wraparound packet sequence |
| Timestamp | `float64` | PICO realtime since application start |
| Tracking flags | `uint8` | bit 0 head, bit 1 left, bit 2 right |
| Head pose | `7 x float32` | position xyz, quaternion xyzw |
| Left pose | `7 x float32` | position xyz, quaternion xyzw |
| Right pose | `7 x float32` | position xyz, quaternion xyzw |
| Left input | `3 x uint16 + 6 x float32` | button masks and analog values |
| Right input | `3 x uint16 + 6 x float32` | button masks and analog values |

Each controller input contains, in order:

1. `held`: buttons currently held.
2. `pressed`: rising edges accumulated since the last successful packet.
3. `released`: falling edges accumulated since the last successful packet.
4. `trigger`: analog index trigger value, normally 0 to 1.
5. `grip`: analog middle-finger/grip value, normally 0 to 1.
6. `primary_axis_x`, `primary_axis_y`.
7. `secondary_axis_x`, `secondary_axis_y`.

## Button bit mapping

| Bit | Mask | Input |
|---:|---:|---|
| 0 | `0x0001` | Primary button: X on left, A on right |
| 1 | `0x0002` | Secondary button: Y on left, B on right |
| 2 | `0x0004` | Grip button |
| 3 | `0x0008` | Trigger button |
| 4 | `0x0010` | Menu/system button when exposed by PICO |
| 5 | `0x0020` | Primary joystick click |
| 6 | `0x0040` | Primary joystick touch |
| 7 | `0x0080` | Secondary axis click |
| 8 | `0x0100` | Secondary axis touch |
| 9 | `0x0200` | Primary button capacitive touch |
| 10 | `0x0400` | Secondary button capacitive touch |

PICO may reserve a short press of the system circle button. Android can pause the
application before that event reaches Unity, so this particular button is not
guaranteed. All other supported inputs are sent as reported by Unity XR.

When transmission stops, the app pauses, or tracking becomes invalid, tracking
flags and controller inputs are zeroed. The receiver must stop the corresponding
robot arm whenever its tracking flag is invalid and must stop the robot if no new
packet arrives for more than 200 ms.
