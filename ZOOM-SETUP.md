# Soundboard + microphone in Zoom (macOS)

This routes your microphone and Amplitude clips into one Zoom microphone input,
while you hear clips and meeting participants through headphones.

```text
Microphone → OBS monitoring ───────→ BlackHole 2ch → Zoom microphone
Amplitude clips ──────────────────→ BlackHole 2ch
               └─────────────────→ Headphones
Zoom speaker ────────────────────→ Headphones
```

## 1. Install and allow access

Install [Amplitude](README.md#installation), [OBS Studio](https://obsproject.com/download),
[BlackHole 2ch](https://existential.audio/blackhole/), and the Zoom desktop app.
Use a build from this fork to retain its playback and reconnect fixes.
Restart if the BlackHole installer requests it, then connect your headset.

In **System Settings → Privacy & Security**, enable **Microphone** access for OBS
and Zoom, and **Accessibility** access for Amplitude's global hotkeys.
Reopen Amplitude after granting access. Set macOS sound output to your headphones.

## 2. Send your microphone through OBS

1. In **OBS → Settings → Audio**, disable unused global audio devices.
2. Add an **Audio Input Capture** source to a scene and select your actual
   microphone/headset explicitly. Alternatively, select it as the global
   **Mic/Aux** device; use only one capture of that microphone.
3. Set OBS's **Monitoring Device** to **BlackHole 2ch** in Settings (under
   Audio/Advanced; the location varies by OBS version).
4. In **Edit → Advanced Audio Properties**, enable monitoring for the microphone
   (**Monitor and Output** in versions with that dropdown). Speak and check its
   meter; keep peaks below clipping.

Keep that scene active and OBS running. Recording, streaming, and Virtual Camera
are not required. Do not capture BlackHole or Zoom's speaker output in this OBS
scene: monitoring either back into BlackHole creates a feedback loop.

## 3. Configure Amplitude

Create an output profile with two outputs: **BlackHole 2ch** for Zoom, and
**Default** for local listening through your macOS-selected headphones. Assign
the profile to your clips. Start with modest clip volumes and adjust by ear.

No macOS Aggregate or Multi-Output Device is needed for this arrangement;
Amplitude sends each clip to both outputs itself.

## 4. Configure and test Zoom

In **Zoom → Settings → Audio**, choose:

| Setting | Device / mode |
| --- | --- |
| Microphone | BlackHole 2ch |
| Speaker | Headphones, or Same as System with headphones selected |
| Microphone mode | Original sound for musicians |

Confirm Original Sound is active in the meeting so noise suppression does not
remove effects. Computer-audio sharing is unnecessary for this route.

Use **Test microphone**: say a sentence, play a clip, then use Amplitude's Stop
action during a longer clip. Playback should contain both voice and effects.
Keep OBS and Amplitude open throughout the meeting. Zoom's mute mutes both.

If only clips work, check OBS's mic selection, permission, meter, and monitoring.
If only voice works, check the clip's Amplitude output profile. After headset
reconnection, reselect its OBS input and restart Amplitude if audio remains stale.

References: [OBS monitoring](https://obsproject.com/kb/audio-mixer-guide),
[BlackHole virtual input](https://github.com/ExistentialAudio/BlackHole/wiki/Use-BlackHole-as-a-Virtual-Microphone),
[Zoom Original Sound](https://support.zoom.com/hc/en/article?id=zm_kb&sysparm_article=KB0059985).
