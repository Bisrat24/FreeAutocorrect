using System;
using System.IO;
using System.Media;
using System.Text;
using System.Threading.Tasks;

namespace FreeAutocorrect.Services;

/// <summary>
/// Provides customizable, subtle, non-intrusive offline acoustic cues for auto-corrections and suggestion acceptances.
/// Includes procedural audio synthesis for zero-asset offline sound schemes: Subtle Pop, Typewriter Click, Soft Chime, and Windows Default.
/// Wrapped in try/catch to ensure zero crashes on headless, muted, or audio-disabled systems.
/// </summary>
public static class SoundFeedbackService
{
    private static readonly object InitLock = new();
    private static bool _initialized = false;

    private static SoundPlayer? _popCorrectionPlayer;
    private static SoundPlayer? _popSuggestionPlayer;
    private static SoundPlayer? _clickCorrectionPlayer;
    private static SoundPlayer? _clickSuggestionPlayer;
    private static SoundPlayer? _chimeCorrectionPlayer;
    private static SoundPlayer? _chimeSuggestionPlayer;

    static SoundFeedbackService()
    {
        InitializeSynthesizedSounds();
    }

    public static void InitializeSynthesizedSounds()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            try
            {
                // 1. Subtle Pop (correction: 520Hz->260Hz sweep, suggestion: 650Hz->320Hz sweep)
                _popCorrectionPlayer = CreatePlayer(GeneratePopWav(startFreq: 520, endFreq: 260, durationMs: 40, volume: 0.35));
                _popSuggestionPlayer = CreatePlayer(GeneratePopWav(startFreq: 680, endFreq: 340, durationMs: 45, volume: 0.35));

                // 2. Typewriter Click (correction: 2200Hz mechanical transient, suggestion: 1800Hz double-click)
                _clickCorrectionPlayer = CreatePlayer(GenerateClickWav(primaryFreq: 2200, durationMs: 16, volume: 0.30));
                _clickSuggestionPlayer = CreatePlayer(GenerateClickWav(primaryFreq: 2600, durationMs: 22, volume: 0.30));

                // 3. Soft Chime (correction: 880Hz bell, suggestion: 1046Hz C6 chord)
                _chimeCorrectionPlayer = CreatePlayer(GenerateChimeWav(baseFreq: 880, durationMs: 140, volume: 0.35));
                _chimeSuggestionPlayer = CreatePlayer(GenerateChimeWav(baseFreq: 1046, durationMs: 180, volume: 0.35));

                _initialized = true;
            }
            catch
            {
                // Fallback to system sounds if audio synthesis fails
            }
        }
    }

    private static SoundPlayer? CreatePlayer(byte[] wavBytes)
    {
        try
        {
            var ms = new MemoryStream(wavBytes);
            var player = new SoundPlayer(ms);
            player.Load();
            return player;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Plays an acoustic cue when a real-time typo is auto-corrected while typing.
    /// </summary>
    public static void PlayCorrectionSound(bool enabled, string scheme = "SubtlePop")
    {
        if (!enabled) return;
        PlaySoundInternal(isCorrection: true, scheme);
    }

    /// <summary>
    /// Plays an acoustic cue when a grammar/spelling suggestion is accepted or replaced.
    /// </summary>
    public static void PlaySuggestionSound(bool enabled, string scheme = "SubtlePop")
    {
        if (!enabled) return;
        PlaySoundInternal(isCorrection: false, scheme);
    }

    private static void PlaySoundInternal(bool isCorrection, string scheme)
    {
        try
        {
            switch (scheme?.ToLowerInvariant())
            {
                case "typewriter":
                case "click":
                    var clickPlayer = isCorrection ? _clickCorrectionPlayer : _clickSuggestionPlayer;
                    if (clickPlayer != null) { clickPlayer.Play(); return; }
                    break;

                case "softchime":
                case "chime":
                    var chimePlayer = isCorrection ? _chimeCorrectionPlayer : _chimeSuggestionPlayer;
                    if (chimePlayer != null) { chimePlayer.Play(); return; }
                    break;

                case "subtlepop":
                case "pop":
                    var popPlayer = isCorrection ? _popCorrectionPlayer : _popSuggestionPlayer;
                    if (popPlayer != null) { popPlayer.Play(); return; }
                    break;

                case "systemdefault":
                case "system":
                    if (isCorrection) SystemSounds.Beep.Play();
                    else SystemSounds.Asterisk.Play();
                    return;
            }

            // Default fallback
            var defPlayer = isCorrection ? _popCorrectionPlayer : _popSuggestionPlayer;
            if (defPlayer != null)
            {
                defPlayer.Play();
            }
            else
            {
                if (isCorrection) SystemSounds.Beep.Play();
                else SystemSounds.Asterisk.Play();
            }
        }
        catch
        {
            // Fail silently on machines without sound hardware.
        }
    }

    #region Procedural Audio Synthesis
    private static byte[] GeneratePopWav(double startFreq, double endFreq, int durationMs, double volume)
    {
        int sampleRate = 44100;
        int totalSamples = (sampleRate * durationMs) / 1000;
        var samples = new short[totalSamples];

        for (int i = 0; i < totalSamples; i++)
        {
            double t = (double)i / sampleRate;
            double progress = (double)i / totalSamples;

            // Downward pitch sweep
            double freq = startFreq + (endFreq - startFreq) * Math.Pow(progress, 0.7);

            // Fast attack, exponential decay envelope
            double envelope = progress < 0.1
                ? progress / 0.1
                : Math.Exp(-progress * 5.0);

            double sampleVal = Math.Sin(2.0 * Math.PI * freq * t) * envelope * volume;
            samples[i] = (short)(Math.Clamp(sampleVal, -1.0, 1.0) * short.MaxValue);
        }

        return BuildRiffWav(samples, sampleRate);
    }

    private static byte[] GenerateClickWav(double primaryFreq, int durationMs, double volume)
    {
        int sampleRate = 44100;
        int totalSamples = (sampleRate * durationMs) / 1000;
        var samples = new short[totalSamples];
        var rng = new Random(42);

        for (int i = 0; i < totalSamples; i++)
        {
            double t = (double)i / sampleRate;
            double progress = (double)i / totalSamples;

            // Exponential decay envelope (mechanical transient)
            double envelope = Math.Exp(-progress * 9.0);

            // Blend high frequency tone with tiny noise burst for tactile texture
            double tone = Math.Sin(2.0 * Math.PI * primaryFreq * t);
            double noise = (rng.NextDouble() * 2.0 - 1.0) * 0.35;

            double sampleVal = (tone * 0.7 + noise) * envelope * volume;
            samples[i] = (short)(Math.Clamp(sampleVal, -1.0, 1.0) * short.MaxValue);
        }

        return BuildRiffWav(samples, sampleRate);
    }

    private static byte[] GenerateChimeWav(double baseFreq, int durationMs, double volume)
    {
        int sampleRate = 44100;
        int totalSamples = (sampleRate * durationMs) / 1000;
        var samples = new short[totalSamples];

        for (int i = 0; i < totalSamples; i++)
        {
            double t = (double)i / sampleRate;
            double progress = (double)i / totalSamples;

            // Smooth bell envelope (attack then soft decay)
            double envelope = progress < 0.05
                ? progress / 0.05
                : Math.Exp(-progress * 3.8);

            // Harmonic bell chime: fundamental + harmonic shimmer
            double fund = Math.Sin(2.0 * Math.PI * baseFreq * t);
            double harm = Math.Sin(2.0 * Math.PI * (baseFreq * 2.0) * t) * 0.35;
            double third = Math.Sin(2.0 * Math.PI * (baseFreq * 1.5) * t) * 0.20;

            double sampleVal = (fund + harm + third) * envelope * volume * 0.6;
            samples[i] = (short)(Math.Clamp(sampleVal, -1.0, 1.0) * short.MaxValue);
        }

        return BuildRiffWav(samples, sampleRate);
    }

    private static byte[] BuildRiffWav(short[] samples, int sampleRate)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        int dataSize = samples.Length * sizeof(short);

        // RIFF chunk
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // Subchunk1Size (16 for PCM)
        bw.Write((short)1); // AudioFormat (1 = PCM)
        bw.Write((short)1); // NumChannels (1 = Mono)
        bw.Write(sampleRate);
        bw.Write(sampleRate * sizeof(short)); // ByteRate
        bw.Write((short)sizeof(short)); // BlockAlign
        bw.Write((short)16); // BitsPerSample

        // data chunk
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);

        for (int i = 0; i < samples.Length; i++)
        {
            bw.Write(samples[i]);
        }

        return ms.ToArray();
    }
    #endregion
}
