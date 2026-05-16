namespace S1Utility;

// Semantic status categories for transient UI messages.
// Lives outside Core but stays Avalonia-free so PrmFileManager (and any future
// non-UI emitter) can raise status events without taking a brush dependency.
// MainWindow.SetStatus maps each kind to a Palette brush.
public enum StatusKind
{
    Info,   // neutral / in-progress (grey)
    Ok,     // success (green)
    Warn,   // recoverable problem, advisory (orange)
    Error,  // failure (red)
}
