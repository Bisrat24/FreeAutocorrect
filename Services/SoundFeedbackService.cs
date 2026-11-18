using System;
using System.Media;

namespace FreeAutocorrect.Services;

/// <summary>
/// Provides subtle, non-intrusive offline acoustic cues for auto-corrections and suggestion acceptances.
/// Wrapped in try/catch to ensure zero crashes on headless, muted, or audio-disabled systems.
/// </summary>
public static class SoundFeedbackService
{
    /// <summary>
    /// Plays an acoustic cue when a real-time typo is auto-corrected while typing.
    /// </summary>
    public static void PlayCorrectionSound(bool enabled)
    {
        if (!enabled) return;
        try
        {
            // SystemSounds.Beep produces a standard, lightweight acoustic cue.
            SystemSounds.Beep.Play();
        }
        catch
        {
            // Fail silently on machines without sound hardware.
        }
    }

    /// <summary>
    /// Plays an acoustic cue when a grammar/spelling suggestion is accepted or replaced.
    /// </summary>
    public static void PlaySuggestionSound(bool enabled)
    {
        if (!enabled) return;
        try
        {
            // SystemSounds.Asterisk produces a distinct subtle chime.
            SystemSounds.Asterisk.Play();
        }
        catch
        {
            // Fail silently on machines without sound hardware.
        }
    }
}
