using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;

namespace S1Utility;

public partial class MainWindow
{
    private void OnPatchClicked(int program, Button btn)
    {
        _patch.SendProgramChange(program, PcChannel);
        HighlightPatchButton(program);
        btn.Focus();
    }

    private void OnPatchGridKeyDown(object? sender, KeyEventArgs e)
    {
        int dx = 0, dy = 0;
        switch (e.Key)
        {
            case Key.Left:  dx = -1; break;
            case Key.Right: dx =  1; break;
            case Key.Up:    dy = -1; break;
            case Key.Down:  dy =  1; break;
            default: return;
        }

        e.Handled = true;

        int program;
        if (_currentSlotIndex < 0)
        {
            program = 0;
        }
        else
        {
            int bank = _currentSlotIndex / 16;
            int slot = _currentSlotIndex % 16;
            int newBank = bank + dy;
            int newSlot = slot + dx;
            if ((uint)newBank >= 4u || (uint)newSlot >= 16u) return;
            program = newBank * 16 + newSlot;
        }

        _patch.SendProgramChange(program, PcChannel);
        HighlightPatchButton(program);
        if ((uint)program < (uint)_patchButtons.Count)
            _patchButtons[program].Focus();
    }

    private void HighlightPatchButton(int program)
    {
        if (program == _currentSlotIndex) return;

        // Capture the outgoing slot's current state before leaving it.
        if (_currentSlotIndex >= 0 && _dirtySlots.Contains(_currentSlotIndex))
            _dirtyStateSnapshots[_currentSlotIndex] = CaptureSnapshot();

        foreach (var b in _patchButtons)
            b.Classes.Remove("patch-btn-active");
        if ((uint)program < (uint)_patchButtons.Count)
            _patchButtons[program].Classes.Add("patch-btn-active");
        _currentSlotIndex = program;

        if (_prm.PatternSync && !string.IsNullOrEmpty(_prm.PrmFolder))
        {
            if (_dirtySlots.Contains(program) && _dirtyStateSnapshots.TryGetValue(program, out var dirtySnap))
            {
                // Restore the user's modified values to the live patch, but
                // refresh Tab 2 from the on-disk PRM so the inspector keeps
                // showing the untouched file state.
                RestoreSnapshotValues(dirtySnap);
                _patch.MarkAllSynced();
                _prm.TryLoadInspectorOnly(program);
            }
            else
            {
                _suppressDirtyTracking = true;
                _suppressUndoTracking  = true;
                bool loaded = _prm.TryLoadPatternPrm(program);
                _suppressUndoTracking  = false;
                _suppressDirtyTracking = false;
                if (loaded) _slotSnapshots[program] = CaptureSnapshot();
            }
            UpdateRestorePatchButton();
        }
        else
        {
            ClearDirtyTracking();
            _patch.ResetAllSync();
            if (!string.IsNullOrEmpty(_prm.PrmFolder))
                _prm.TryLoadInspectorOnly(program);
        }

        // Undo/redo is scoped to edits made within a single slot. Switching slots
        // is a major navigation action; whatever was on the stack belongs to the
        // previous editing context and would behave confusingly here.
        ClearUndoHistory();
    }

    private int[] CaptureSnapshot() =>
        _patch.AllParameters.Select(p => p.Value).ToArray();

    private void RestoreSnapshotValues(int[] snapshot)
    {
        _suppressDirtyTracking = true;
        _suppressUndoTracking  = true;
        for (int i = 0; i < _patch.AllParameters.Count; i++)
            _patch.HandleIncomingCC(_patch.AllParameters[i].CcNumber, snapshot[i]);
        _suppressUndoTracking  = false;
        _suppressDirtyTracking = false;
    }

    private void MarkCurrentSlotDirty()
    {
        if (_suppressDirtyTracking || !_prm.PatternSync || _currentSlotIndex < 0) return;
        if (_dirtySlots.Add(_currentSlotIndex))
        {
            RefreshPatchButtonStyle(_currentSlotIndex);
            UpdateRestorePatchButton();
        }
    }

    private void RefreshPatchButtonStyle(int slot)
    {
        if ((uint)slot >= (uint)_patchButtons.Count) return;
        var btn = _patchButtons[slot];
        if (_prm.PatternSync && _dirtySlots.Contains(slot))
            btn.Classes.Add("patch-btn-dirty");
        else
            btn.Classes.Remove("patch-btn-dirty");
    }

    private void RefreshAllPatchButtonStyles()
    {
        for (int i = 0; i < _patchButtons.Count; i++)
            RefreshPatchButtonStyle(i);
    }

    private void UpdateRestorePatchButton()
    {
        if (_restorePatchButton == null) return;
        bool mirrorOn = _prm.PatternSync;
        _restorePatchButton.IsVisible = mirrorOn;
        _restorePatchButton.IsEnabled = mirrorOn
            && _currentSlotIndex >= 0
            && _dirtySlots.Contains(_currentSlotIndex);
    }

    private void ClearDirtyTracking()
    {
        _dirtySlots.Clear();
        _slotSnapshots.Clear();
        _dirtyStateSnapshots.Clear();
        RefreshAllPatchButtonStyles();
        UpdateRestorePatchButton();
    }

    private async void OnRestorePatchClicked()
    {
        if (_currentSlotIndex < 0 || !_dirtySlots.Contains(_currentSlotIndex)) return;

        _suppressDirtyTracking = true;
        _suppressUndoTracking  = true;
        bool loaded = _prm.TryLoadPatternPrm(_currentSlotIndex);
        _suppressUndoTracking  = false;
        _suppressDirtyTracking = false;
        if (!loaded) return;

        _dirtySlots.Remove(_currentSlotIndex);
        _dirtyStateSnapshots.Remove(_currentSlotIndex);
        _slotSnapshots[_currentSlotIndex] = CaptureSnapshot();
        RefreshPatchButtonStyle(_currentSlotIndex);
        UpdateRestorePatchButton();
        ClearUndoHistory();
        await _patch.SendAllAsync();
        _patch.MarkAllSynced();
    }
}
