using Avalonia.Controls;

namespace S1Utility;

internal interface IWindowSizePolicy
{
    void Attach(Window window);
    void Detach();
}
