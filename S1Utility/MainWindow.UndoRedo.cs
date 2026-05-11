using System;
using System.Collections.Generic;
using Avalonia.Interactivity;
using Avalonia.Threading;
using S1Utility.Core;

namespace S1Utility;

public partial class MainWindow
{
    // One undoable parameter edit. NewValue is updated in place while the user
    // drags a knob so a continuous gesture collapses into a single undo step.
    private sealed class UndoEntry
    {
        public int      Cc;
        public int      OldValue;
        public int      NewValue;
        public DateTime LastChange;
    }

    private readonly Stack<UndoEntry> _undoStack       = new();
    private readonly Stack<UndoEntry> _redoStack       = new();
    private readonly Dictionary<int, int> _lastSeenValues = new();
    private bool _suppressUndoTracking;

    // Coalesce window: same-CC edits within this window merge into one entry.
    // 500 ms easily covers a knob drag but lets two intentional taps separate.
    private const int CoalesceMs        = 500;
    private const int MaxUndoStackSize  = 200;

    private void InitializeUndoTracking()
    {
        foreach (var p in _patch.AllParameters)
        {
            _lastSeenValues[p.CcNumber] = p.Value;
            p.ValueChanged += OnParamValueChangedForUndo;
        }
        UpdateUndoRedoButtons();
    }

    private void OnParamValueChangedForUndo(object? sender, int newVal)
    {
        if (sender is not S1Parameter p) return;
        int cc = p.CcNumber;

        // Hardware-driven CCs arrive on the MIDI thread. Sync the cache on the
        // UI thread so a later UI edit computes the right oldVal, but do not
        // record an undo entry: the hardware knob has already moved physically.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => _lastSeenValues[cc] = newVal);
            return;
        }

        int oldVal = _lastSeenValues.TryGetValue(cc, out var v) ? v : newVal;
        _lastSeenValues[cc] = newVal;

        if (_suppressUndoTracking) return;
        if (oldVal == newVal) return;

        var now = DateTime.UtcNow;

        if (_undoStack.Count > 0)
        {
            var top = _undoStack.Peek();
            if (top.Cc == cc && (now - top.LastChange).TotalMilliseconds < CoalesceMs)
            {
                top.NewValue   = newVal;
                top.LastChange = now;
                _redoStack.Clear();
                UpdateUndoRedoButtons();
                return;
            }
        }

        _undoStack.Push(new UndoEntry
        {
            Cc         = cc,
            OldValue   = oldVal,
            NewValue   = newVal,
            LastChange = now,
        });

        TrimUndoStack();
        _redoStack.Clear();
        UpdateUndoRedoButtons();
    }

    private void TrimUndoStack()
    {
        if (_undoStack.Count <= MaxUndoStackSize) return;
        // Stack<T> has no trim-from-bottom; rebuild keeping the newest N.
        var arr = _undoStack.ToArray();          // newest first
        _undoStack.Clear();
        for (int i = MaxUndoStackSize - 1; i >= 0; i--)
            _undoStack.Push(arr[i]);
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        var entry = _undoStack.Pop();
        ApplyEntry(entry.Cc, entry.OldValue);
        // Reset coalescing window so a quick subsequent edit doesn't merge with this entry on redo.
        entry.LastChange = DateTime.MinValue;
        _redoStack.Push(entry);
        UpdateUndoRedoButtons();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        var entry = _redoStack.Pop();
        ApplyEntry(entry.Cc, entry.NewValue);
        entry.LastChange = DateTime.MinValue;
        _undoStack.Push(entry);
        UpdateUndoRedoButtons();
    }

    private void ApplyEntry(int cc, int value)
    {
        var param = _patch.GetByCC(cc);
        if (param is null) return;

        _suppressUndoTracking = true;
        try
        {
            // Setting Value sends the CC to hardware (if connected) and fires
            // ValueChanged so every subscribed UI control refreshes.
            param.Value = value;
            _lastSeenValues[cc] = value;
            // A user-driven edit goes through Value too, so the synth's value
            // matches the editor's after this — mark synced.
            param.MarkSynced();
        }
        finally
        {
            _suppressUndoTracking = false;
        }
    }

    private void ClearUndoHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        foreach (var p in _patch.AllParameters)
            _lastSeenValues[p.CcNumber] = p.Value;
        UpdateUndoRedoButtons();
    }

    private void UpdateUndoRedoButtons()
    {
        if (UndoButton != null) UndoButton.IsEnabled = _undoStack.Count > 0;
        if (RedoButton != null) RedoButton.IsEnabled = _redoStack.Count > 0;
    }

    private void OnUndoClicked(object? sender, RoutedEventArgs e) => Undo();
    private void OnRedoClicked(object? sender, RoutedEventArgs e) => Redo();
}
