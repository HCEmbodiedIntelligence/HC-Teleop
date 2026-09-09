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

## Middleware emergency-stop feedback (UDP 5007)

The headset listens on `UdpPoseSender.inboundEventPort` (default 5007) for UTF-8
JSON events already emitted by the middleware:

```json
{"kind":"safety_stop","source":"middleware","timestamp":1750000000.1,"payload":{"reason":"no VR pose data for 201 ms"}}
```

`safety_stop` latches a red emergency-stop banner above the headset control panel
and changes the UDP indicator to red with the text `急停`. Timeout, stale pose
sample, and invalid head tracking reasons are displayed in Chinese. Other reasons
are displayed as provided. `payload.message` is a fallback for a missing reason.

```json
{"kind":"safety_resume","source":"middleware","timestamp":1750000001.2,"payload":{"reason":"VR controller A button pressed"}}
```

Only `safety_resume` clears the indicator. Tracking recovery, discovery replies,
recording/replay events, and local UDP toggles do not clear it. Pose/input traffic
continues while enabled so the middleware can receive the A-button resume request.
The display waits for the middleware confirmation; pressing A does not locally
acknowledge the stop. The existing replay-reset acknowledgement is preserved.

`type` is accepted as an alias for `kind`. Positive middleware timestamps order
safety events; an older event cannot overwrite a newer stop/resume state. Events
without timestamps remain supported. This is UI feedback, not an additional
robot-side safety controller.

### Verification

1. Install a newly built headset app, connect to the middleware, and enable UDP.
2. Interrupt pose traffic while keeping PC-to-headset UDP 5007 reachable. Confirm
   the middleware stop and the headset's red banner show the timeout reason.
3. Restore pose traffic. The banner must remain until a middleware resume event.
4. Press A after resolving the fault, or resume from the middleware dashboard.
   Confirm the banner clears only when the resume event arrives.
5. Repeat with stale samples and head-tracking loss. Check that recording and
   replay status changes cannot hide the emergency-stop banner.

`Tools/SafetyStateRegression.cs` tests the managed safety state without XR hardware.
Compile it as a console executable, then run using Unity's bundled `mono.exe`,
passing the freshly compiled project DLL and the Editor's
`Data/Managed/UnityEngine` directory. This does not test actual UDP delivery or UI
rendering.

The existing middleware sends stop/resume as individual UDP events. Delivery is
not guaranteed during packet loss or a disconnected return path; reliable
reconciliation after such a loss requires periodic middleware safety snapshots or
an acknowledgement/retry protocol. This headset change consumes the existing
protocol and does not add that middleware mechanism.
