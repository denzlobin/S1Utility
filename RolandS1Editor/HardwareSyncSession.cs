using System;
using System.Collections.Generic;
using Avalonia.Threading;

namespace RolandS1Editor;

// Manages a single "Sync from Hardware" session.
//
// All public methods must be called on the UI thread — use
// Dispatcher.UIThread.Post when feeding values from a MIDI callback.
//
// How it works:
//   1. RecordCC(cc, value) is called for every incoming CC.
//   2. Each CC gets a 500ms DispatcherTimer.  Every new value resets it.
//   3. When the timer fires the CC hasn't changed — take the MEDIAN of
//      every value seen for that CC and raise ParameterSettled.
//   4. GetFinalValues() returns medians for ALL buffered CCs so that
//      clicking Done before the last timer fires still commits everything.
public sealed class HardwareSyncSession : IDisposable
{
    // All values received per CC number.
    private readonly Dictionary<int, List<int>> _buffer = new();

    // Per-CC debounce timer — fires 500 ms after the last incoming value.
    private readonly Dictionary<int, DispatcherTimer> _timers = new();

    // CCs that have settled at least once (used for the unique count).
    private readonly HashSet<int> _settled = new();

    // Raised on the UI thread when a CC has been quiet for 500 ms.
    // Arguments: (ccNumber, medianValue)
    public event Action<int, int>? ParameterSettled;

    // Number of unique CCs that have settled since the session started.
    public int CapturedCount => _settled.Count;

    // Feed an incoming CC value into the session.  Must be on the UI thread.
    public void RecordCC(int ccNumber, int value)
    {
        // Accumulate raw values for this CC.
        if (!_buffer.TryGetValue(ccNumber, out var list))
        {
            list = new List<int>();
            _buffer[ccNumber] = list;
        }
        list.Add(value);

        // Reset (or create) the debounce timer.
        if (_timers.TryGetValue(ccNumber, out var timer))
        {
            // Each new value postpones the settlement by another 500 ms.
            timer.Stop();
            timer.Start();
        }
        else
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t.Tick += (_, _) => OnCCSettled(ccNumber, t);
            _timers[ccNumber] = t;
            t.Start();
        }
    }

    // Called 500 ms after the last value arrived for this CC.
    private void OnCCSettled(int ccNumber, DispatcherTimer timer)
    {
        timer.Stop();

        if (!_buffer.TryGetValue(ccNumber, out var values) || values.Count == 0)
            return;

        _settled.Add(ccNumber);                        // idempotent for the count
        ParameterSettled?.Invoke(ccNumber, Median(values));
    }

    // Returns the current median for EVERY CC that has received at least one
    // value — whether or not its debounce timer has fired yet.
    // Used when Done is clicked to commit everything still in-flight.
    public Dictionary<int, int> GetFinalValues()
    {
        var result = new Dictionary<int, int>();
        foreach (var (cc, values) in _buffer)
            if (values.Count > 0)
                result[cc] = Median(values);
        return result;
    }

    private static int Median(List<int> values)
    {
        var sorted = new List<int>(values);
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    public void Dispose()
    {
        foreach (var t in _timers.Values) t.Stop();
        _timers.Clear();
    }
}
