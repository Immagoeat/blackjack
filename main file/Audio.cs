using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Silk.NET.OpenAL;

namespace BlackjackSimulator
{
    public unsafe class AudioEngine : IDisposable
    {
        AL        _al  = null!;
        ALContext _alc = null!;
        Device*   _dev = null;
        Context*  _ctx = null;
        bool      _ok  = false;

        readonly Dictionary<string, uint> _bufs = new();
        uint  _musicSrc;
        bool  _musicPlaying = false;
        float _masterVol    = 1.0f;
        float _musicVol     = 0.30f;

        const int SR = 44100;

        public bool Ok => _ok;

        public void Init()
        {
            try
            {
                _al  = AL.GetApi();
                _alc = ALContext.GetApi();
                _dev = _alc.OpenDevice(null);
                if (_dev == null) return;
                _ctx = _alc.CreateContext(_dev, null);
                _alc.MakeContextCurrent(_ctx);
                _ok = true;
                BakeAll();
                StartMusic();
            }
            catch { _ok = false; }
        }

        // ── primitive generators ─────────────────────────────────────

        static short[] Silence(float dur)
        {
            return new short[(int)(SR * dur)];
        }

        static short[] Sine(float freq, float dur, float amp = 0.5f, float decay = 2f)
        {
            int n = (int)(SR * dur);
            var buf = new short[n];
            for (int i = 0; i < n; i++)
            {
                float t   = i / (float)SR;
                float env = decay > 0 ? MathF.Exp(-t * decay) : 1f;
                buf[i] = (short)(amp * env * MathF.Sin(2f * MathF.PI * freq * t) * short.MaxValue);
            }
            return buf;
        }

        // Piano-like tone: sine + harmonics with pluck-style envelope
        static short[] Piano(float freq, float dur, float amp = 0.35f)
        {
            int n = (int)(SR * dur);
            var buf = new short[n];
            float[] hA = { 1f, 0.5f, 0.25f, 0.12f, 0.06f };
            for (int i = 0; i < n; i++)
            {
                float t   = i / (float)SR;
                float att = Math.Min(t / 0.005f, 1f);
                float rel = MathF.Exp(-t * 3.5f);
                float env = att * rel;
                float s   = 0f;
                for (int h = 0; h < hA.Length; h++)
                    s += hA[h] * MathF.Sin(2f * MathF.PI * freq * (h + 1) * t);
                buf[i] = (short)(amp * env * s * short.MaxValue / hA.Length);
            }
            return buf;
        }

        // Warm organ/pad: multiple detuned sines, slow attack
        static short[] Pad(float freq, float dur, float amp = 0.22f)
        {
            int n = (int)(SR * dur);
            var buf = new short[n];
            float[] detune = { 1f, 1.003f, 0.997f, 2.001f, 0.5f };
            float[] dA     = { 1f, 0.6f,   0.6f,   0.3f,   0.4f };
            for (int i = 0; i < n; i++)
            {
                float t   = i / (float)SR;
                float att = Math.Min(t / 0.08f, 1f);
                float rel = Math.Max(0f, 1f - Math.Max(0f, t - (dur - 0.12f)) / 0.12f);
                float env = att * rel;
                float s   = 0f;
                for (int d = 0; d < detune.Length; d++)
                    s += dA[d] * MathF.Sin(2f * MathF.PI * freq * detune[d] * t);
                buf[i] = (short)(amp * env * s * short.MaxValue / detune.Length);
            }
            return buf;
        }

        // Bass note: fat sine with subtle square-ish shape
        static short[] Bass(float freq, float dur, float amp = 0.55f)
        {
            int n = (int)(SR * dur);
            var buf = new short[n];
            for (int i = 0; i < n; i++)
            {
                float t   = i / (float)SR;
                float att = Math.Min(t / 0.012f, 1f);
                float rel = MathF.Exp(-t * 4f);
                float env = att * rel;
                float s   = MathF.Sin(2f * MathF.PI * freq * t)
                          + 0.3f * MathF.Sin(2f * MathF.PI * freq * 2 * t)
                          + 0.15f * MathF.Sin(2f * MathF.PI * freq * 3 * t);
                s = MathF.Tanh(s);
                buf[i] = (short)(amp * env * s * short.MaxValue);
            }
            return buf;
        }

        // Kick drum: low sine sweep + noise click
        static short[] Kick(float amp = 0.7f)
        {
            int n = (int)(SR * 0.18f);
            var buf = new short[n];
            var rng = new Random(7);
            for (int i = 0; i < n; i++)
            {
                float t    = i / (float)SR;
                float freq = 160f * MathF.Exp(-t * 45f) + 40f;
                float body = MathF.Sin(2f * MathF.PI * freq * t) * MathF.Exp(-t * 18f);
                float click = (rng.NextSingle() * 2f - 1f) * MathF.Exp(-t * 120f) * 0.4f;
                buf[i] = (short)(amp * (body + click) * short.MaxValue);
            }
            return buf;
        }

        // Snare: short noise burst + tone
        static short[] Snare(float amp = 0.55f)
        {
            int n = (int)(SR * 0.12f);
            var buf = new short[n];
            var rng = new Random(13);
            for (int i = 0; i < n; i++)
            {
                float t    = i / (float)SR;
                float noise = (rng.NextSingle() * 2f - 1f) * MathF.Exp(-t * 22f);
                float tone  = MathF.Sin(2f * MathF.PI * 200f * t) * MathF.Exp(-t * 30f) * 0.4f;
                buf[i] = (short)(amp * (noise + tone) * short.MaxValue);
            }
            return buf;
        }

        // Hi-hat: filtered noise, very short
        static short[] HiHat(float amp = 0.25f, float decay = 80f)
        {
            int n = (int)(SR * 0.04f);
            var buf = new short[n];
            var rng = new Random(21);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                buf[i] = (short)(amp * (rng.NextSingle() * 2f - 1f) * MathF.Exp(-t * decay) * short.MaxValue);
            }
            return buf;
        }

        static short[] Mix(params short[][] layers)
        {
            int n = layers.Max(l => l.Length);
            var buf = new short[n];
            foreach (var layer in layers)
                for (int i = 0; i < layer.Length; i++)
                    buf[i] = (short)Math.Clamp(buf[i] + layer[i], short.MinValue, short.MaxValue);
            return buf;
        }

        static short[] Noise(float dur, float amp = 0.3f, float decay = 8f)
        {
            int n = (int)(SR * dur);
            var buf = new short[n];
            var rng = new Random(42);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float env = MathF.Exp(-t * decay);
                buf[i] = (short)(amp * env * (rng.NextSingle() * 2f - 1f) * short.MaxValue);
            }
            return buf;
        }

        static short[] Sweep(float f0, float f1, float dur, float amp = 0.5f, float decay = 2f)
        {
            int n = (int)(SR * dur);
            var buf = new short[n];
            for (int i = 0; i < n; i++)
            {
                float t    = i / (float)SR;
                float freq = f0 + (f1 - f0) * (t / dur);
                float env  = MathF.Exp(-t * decay);
                buf[i] = (short)(amp * env * MathF.Sin(2f * MathF.PI * freq * t) * short.MaxValue);
            }
            return buf;
        }

        // Trumpet: harmonic-rich bright brass tone
        static short[] Trumpet(float freq, float dur, float amp = 0.40f)
        {
            int n = (int)(SR * dur);
            var buf = new short[n];
            float[] hAmps = { 1.0f, 0.75f, 0.9f, 0.5f, 0.6f, 0.3f, 0.35f, 0.18f, 0.2f, 0.1f };
            for (int i = 0; i < n; i++)
            {
                float t   = i / (float)SR;
                float att = Math.Min(t / 0.01f, 1f);
                float rel = Math.Max(0f, 1f - Math.Max(0f, t - (dur - 0.06f)) / 0.06f);
                float env = att * rel;
                float s   = 0f;
                for (int h = 0; h < hAmps.Length; h++)
                    s += hAmps[h] * MathF.Sin(2f * MathF.PI * freq * (h + 1) * t);
                s = MathF.Tanh(s * 1.2f);
                buf[i] = (short)(amp * env * s * short.MaxValue / hAmps.Length);
            }
            return buf;
        }

        static short[] TrumpetFanfare(float[] freqs, float[] durs, float amp = 0.40f)
        {
            int total  = freqs.Select((_, i) => (int)(SR * durs[i])).Sum();
            var buf    = new short[total];
            int offset = 0;
            for (int ni = 0; ni < freqs.Length; ni++)
            {
                var note = Trumpet(freqs[ni], durs[ni], amp);
                note.CopyTo(buf, offset);
                offset += note.Length;
            }
            return buf;
        }

        // ── music composer ───────────────────────────────────────────
        //
        //  8-bar loop at 110 bpm (~4.4 s).  Structure:
        //    Drums:   kick on 1&3, snare on 2&4, hi-hats on 8th notes
        //    Bass:    walking bass line (Cm pentatonic)
        //    Pad:     sustained chord stabs (Cm → Fm → Gm → Cm)
        //    Piano:   syncopated melody over the top
        //
        static short[] MakeMusic()
        {
            const float BPM    = 110f;
            const float BEAT   = 60f / BPM;          // seconds per beat
            const float BAR    = BEAT * 4f;           // 4/4
            const int   BARS   = 8;
            int totalN = (int)(SR * BAR * BARS);
            var mix    = new int[totalN];              // accumulate as int to avoid clipping

            void Place(short[] src, float startSec)
            {
                int start = (int)(startSec * SR);
                for (int i = 0; i < src.Length && start + i < totalN; i++)
                    mix[start + i] += src[i];
            }

            // ── DRUMS ────────────────────────────────────────────────
            var kick  = Kick(0.70f);
            var snare = Snare(0.55f);
            var hhc   = HiHat(0.22f, 80f);  // closed
            var hho   = HiHat(0.18f, 25f);  // open (longer decay)

            for (int bar = 0; bar < BARS; bar++)
            {
                float b = bar * BAR;
                // Kick: beats 1 & 3
                Place(kick, b + BEAT * 0);
                Place(kick, b + BEAT * 2);
                // Snare: beats 2 & 4
                Place(snare, b + BEAT * 1);
                Place(snare, b + BEAT * 3);
                // Closed hi-hat: every 8th note
                for (int e = 0; e < 8; e++)
                    Place(hhc, b + e * BEAT * 0.5f);
                // Open hi-hat: beat 4 "and"
                Place(hho, b + BEAT * 3.5f);
                // Extra kick on the "and" of 2 for swing
                if (bar % 2 == 0) Place(kick, b + BEAT * 1.5f);
            }

            // ── BASS (walking line, Cm) ──────────────────────────────
            // Notes: C2=65.4, D2=73.4, Eb2=77.8, F2=87.3, G2=98.0, Ab2=103.8, Bb2=116.5
            float[] bassLine = {
                65.4f, 73.4f, 77.8f, 87.3f,   // bar 1: C D Eb F
                87.3f, 77.8f, 73.4f, 65.4f,   // bar 2: F Eb D C
                65.4f, 73.4f, 98.0f, 87.3f,   // bar 3: C D G  F
                103.8f,98.0f, 87.3f, 77.8f,   // bar 4: Ab G  F  Eb
                65.4f, 77.8f, 87.3f, 98.0f,   // bar 5
                116.5f,103.8f,98.0f, 87.3f,   // bar 6
                65.4f, 73.4f, 77.8f, 98.0f,   // bar 7
                65.4f, 65.4f, 98.0f, 65.4f,   // bar 8: resolving
            };
            for (int i = 0; i < bassLine.Length; i++)
            {
                float startT = i * BEAT;
                var note = Bass(bassLine[i], BEAT * 0.85f, 0.50f);
                Place(note, startT);
            }

            // ── PAD (chord stabs) ────────────────────────────────────
            // Chord roots per bar (2 bars each): Cm, Fm, Gm, Cm
            float[] padRoots   = { 130.8f, 174.6f, 196.0f, 130.8f };  // C3 F3 G3 C3
            float[] padFifths  = { 196.0f, 261.6f, 293.7f, 196.0f };  // G3 C4 D4 G3
            float[] padMinors  = { 155.6f, 207.7f, 233.1f, 155.6f };  // Eb3 Ab3 Bb3 Eb3

            for (int chord = 0; chord < 4; chord++)
            {
                float startT = chord * 2 * BAR;
                float dur    = 2 * BAR * 0.92f;
                Place(Pad(padRoots[chord],  dur, 0.18f), startT);
                Place(Pad(padFifths[chord], dur, 0.14f), startT);
                Place(Pad(padMinors[chord], dur, 0.12f), startT);
            }

            // ── PIANO MELODY ─────────────────────────────────────────
            // Syncopated melody in Cm, inspired by jazzy lounge vibes
            // Format: (freq, startBeat, durationBeats)
            (float f, float s, float d)[] melody = {
                // bar 1-2
                (523.3f, 0.0f, 0.4f),   // C5
                (523.3f, 0.5f, 0.35f),
                (587.3f, 1.0f, 0.4f),   // D5
                (622.3f, 1.5f, 0.3f),   // Eb5
                (523.3f, 2.0f, 0.5f),   // C5
                (493.9f, 2.75f, 0.3f),  // B4
                (523.3f, 3.0f, 0.75f),  // C5
                // bar 3-4
                (587.3f, 4.0f, 0.35f),  // D5
                (622.3f, 4.5f, 0.4f),   // Eb5
                (698.5f, 5.0f, 0.5f),   // F5
                (659.3f, 5.5f, 0.35f),  // E5 (blue note)
                (622.3f, 6.0f, 0.4f),   // Eb5
                (587.3f, 6.5f, 0.3f),   // D5
                (523.3f, 7.0f, 0.9f),   // C5 (held)
                // bar 5-6
                (392.0f, 8.0f, 0.35f),  // G4
                (440.0f, 8.5f, 0.35f),  // A4
                (466.2f, 9.0f, 0.35f),  // Bb4
                (523.3f, 9.5f, 0.5f),   // C5
                (587.3f, 10.0f, 0.4f),  // D5
                (622.3f, 10.5f, 0.4f),  // Eb5
                (523.3f, 11.0f, 0.4f),  // C5
                (493.9f, 11.5f, 0.3f),  // B4
                // bar 7-8: run back to root
                (523.3f, 12.0f, 0.3f),  // C5
                (466.2f, 12.5f, 0.3f),  // Bb4
                (440.0f, 13.0f, 0.3f),  // A4
                (392.0f, 13.5f, 0.3f),  // G4
                (349.2f, 14.0f, 0.4f),  // F4
                (329.6f, 14.5f, 0.3f),  // E4
                (261.6f, 15.0f, 0.9f),  // C4 (resolve)
            };

            foreach (var (f, s, d) in melody)
            {
                float startT = s * BEAT;
                var note = Piano(f, d * BEAT, 0.28f);
                Place(note, startT);
            }

            // Convert int accumulator → short with soft clip
            var result = new short[totalN];
            for (int i = 0; i < totalN; i++)
                result[i] = (short)Math.Clamp(mix[i], short.MinValue, short.MaxValue);
            return result;
        }

        // ── baking ───────────────────────────────────────────────────

        void BakeAll()
        {
            Bake("deal",      Mix(Noise(0.04f, 0.4f, 30f), Sine(180f, 0.07f, 0.25f, 20f)));
            Bake("chip",      Mix(Sine(880f, 0.12f, 0.35f, 18f), Sine(1320f, 0.08f, 0.18f, 25f)));
            Bake("click",     Mix(Noise(0.025f, 0.3f, 40f), Sine(400f, 0.04f, 0.2f, 30f)));
            Bake("win",       TrumpetFanfare(
                new[] { 392f, 523f, 659f, 523f },
                new[] { 0.12f, 0.12f, 0.14f, 0.38f }));
            Bake("blackjack", TrumpetFanfare(
                new[] { 392f, 523f, 659f, 784f, 1047f },
                new[] { 0.10f, 0.10f, 0.10f, 0.12f, 0.55f }));
            Bake("lose",      Mix(Sweep(440f, 220f, 0.4f, 0.35f, 1.8f), Sine(330f, 0.3f, 0.15f, 2.5f)));
            Bake("bust",      Mix(Sweep(300f, 100f, 0.45f, 0.4f, 1.5f), Noise(0.2f, 0.2f, 12f)));
            Bake("push",      Mix(Sine(440f, 0.15f, 0.3f, 8f), Sine(550f, 0.15f, 0.15f, 10f)));
            Bake("music",     MakeMusic());
        }

        uint UploadBuffer(short[] samples)
        {
            uint buf = _al.GenBuffer();
            fixed (short* p = samples)
                _al.BufferData(buf, BufferFormat.Mono16, p, samples.Length * 2, SR);
            return buf;
        }

        void Bake(string name, short[] samples) => _bufs[name] = UploadBuffer(samples);

        // ── playback ─────────────────────────────────────────────────

        public void Play(string name, float vol = 1f)
        {
            if (!_ok || !_bufs.TryGetValue(name, out uint buf)) return;
            uint src = _al.GenSource();
            _al.SetSourceProperty(src, SourceInteger.Buffer, (int)buf);
            _al.SetSourceProperty(src, SourceFloat.Gain, vol * _masterVol);
            _al.SourcePlay(src);
            _al.GetBufferProperty(buf, GetBufferInteger.Size, out int bufSize);
            int sleepMs = bufSize / 2 * 1000 / SR + 200;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(sleepMs);
                try { _al.DeleteSource(src); } catch { }
            });
        }

        void StartMusic()
        {
            if (!_ok || !_bufs.TryGetValue("music", out uint buf)) return;
            _musicSrc = _al.GenSource();
            _al.SetSourceProperty(_musicSrc, SourceInteger.Buffer, (int)buf);
            _al.SetSourceProperty(_musicSrc, SourceBoolean.Looping, true);
            _al.SetSourceProperty(_musicSrc, SourceFloat.Gain, _musicVol * _masterVol);
            _al.SourcePlay(_musicSrc);
            _musicPlaying = true;
        }

        public void SetMusicVol(float v)
        {
            _musicVol = Math.Clamp(v, 0f, 1f);
            if (_ok && _musicPlaying)
                _al.SetSourceProperty(_musicSrc, SourceFloat.Gain, _musicVol * _masterVol);
        }

        public void Dispose()
        {
            if (!_ok) return;
            if (_musicPlaying) try { _al.SourceStop(_musicSrc); _al.DeleteSource(_musicSrc); } catch { }
            foreach (var b in _bufs.Values) try { _al.DeleteBuffer(b); } catch { }
            _ok = false;
        }
    }
}
