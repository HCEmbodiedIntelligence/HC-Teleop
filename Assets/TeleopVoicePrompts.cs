using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class TeleopVoicePrompts : MonoBehaviour
{
    public enum Cue { TeleopStarted, TeleopExited, RecordingStarted, RecordingExited }

    private readonly LinkedList<Cue> pending = new LinkedList<Cue>();
    private readonly Dictionary<Cue, AudioClip> clips = new Dictionary<Cue, AudioClip>();
    private AudioSource player;
    private Cue current;

    private void Awake()
    {
        player = gameObject.AddComponent<AudioSource>();
        player.playOnAwake = false;
        player.spatialBlend = 0f;
        player.ignoreListenerPause = true;
        foreach (Cue cue in System.Enum.GetValues(typeof(Cue)))
        {
            AudioClip clip = Resources.Load<AudioClip>("TeleopVoice/" + cue);
            if (clip != null) clips.Add(cue, clip);
        }
    }

    public void Announce(Cue cue)
    {
        if (!isActiveAndEnabled || !clips.TryGetValue(cue, out var clip)) return;

        if (cue == Cue.TeleopExited)
        {
            // Drop obsolete start cues but keep recording confirmations in order.
            while (pending.Remove(Cue.TeleopStarted)) { }
            if (player.isPlaying && current == cue) return;
            if (player.isPlaying && (current == Cue.RecordingStarted || current == Cue.RecordingExited))
                pending.AddFirst(current);
            Play(cue, clip);
        }
        else if (player.isPlaying || pending.Count > 0)
        {
            pending.AddLast(cue);
        }
        else
        {
            Play(cue, clip);
        }
    }

    private void Update()
    {
        if (!player.isPlaying && pending.Count > 0)
        {
            Cue cue = pending.First.Value;
            pending.RemoveFirst();
            Play(cue, clips[cue]);
        }
    }

    private void Play(Cue cue, AudioClip clip)
    {
        current = cue;
        player.clip = clip;
        player.Play();
    }

    private void OnDisable()
    {
        pending.Clear();
        if (player != null) player.Stop();
    }
}
