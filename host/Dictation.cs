using System;
using System.Globalization;
using System.Linq;
using System.Speech.Recognition;
using System.Threading;

namespace Laica
{
    /// <summary>
    /// Offline dictation with Windows' own speech recognizer: no account, no network. Words appear as they are heard
    /// (hypotheses) and are confirmed when the recognizer is sure (final).
    /// </summary>
    public sealed class HostDictation : IDisposable
    {
        readonly Action<string, bool> emit; readonly object gate = new object(); SpeechRecognitionEngine engine;
        public HostDictation(Action<string, bool> emit) { this.emit = emit; }

        /// <summary>Starts listening. Returns null on success, or a message that explains what is missing.</summary>
        public string Start()
        {
            lock (gate)
            {
                if (engine != null) return null;
                try
                {
                    var all = SpeechRecognitionEngine.InstalledRecognizers();
                    var info = all.FirstOrDefault(r => r.Culture.Name == CultureInfo.CurrentUICulture.Name) ?? all.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ?? all.FirstOrDefault();
                    if (info == null) return "Windows has no speech recognizer installed. Add a language with speech support under Settings, Time & language, Speech.";
                    var e = new SpeechRecognitionEngine(info);
                    e.LoadGrammar(new DictationGrammar());
                    e.SetInputToDefaultAudioDevice();
                    e.SpeechHypothesized += (s, a) => { try { emit(a.Result.Text, false); } catch (Exception) { } };
                    e.SpeechRecognized += (s, a) => { try { if (a.Result.Confidence > 0.2) emit(a.Result.Text, true); } catch (Exception) { } };
                    e.RecognizeCompleted += (s, a) => Release(e);
                    e.RecognizeAsync(RecognizeMode.Multiple);
                    engine = e; return null;
                }
                catch (InvalidOperationException) { engine = null; return "No microphone was found. Plug one in or choose a default input device in Windows sound settings."; }
                catch (Exception ex) { engine = null; return "Dictation couldn't start: " + ex.Message; }
            }
        }

        void Release(SpeechRecognitionEngine e)
        {
            lock (gate) { if (engine == e) engine = null; }
            try { e.Dispose(); } catch (Exception) { }
        }

        public void Stop()
        {
            SpeechRecognitionEngine e; lock (gate) e = engine;
            if (e == null) return;
            try { e.RecognizeAsyncStop(); } catch (Exception) { Release(e); return; }
            var t = new Timer(_ => Release(e), null, 4000, Timeout.Infinite); GC.KeepAlive(t);
        }

        public void Dispose() { SpeechRecognitionEngine e; lock (gate) { e = engine; engine = null; } if (e != null) { try { e.RecognizeAsyncCancel(); e.Dispose(); } catch (Exception) { } } }
    }
}
