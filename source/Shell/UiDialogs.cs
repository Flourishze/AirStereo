using Microsoft.UI.Xaml.Controls;

namespace AirStereo2;

internal static class UiDialogs
{
    static readonly SemaphoreSlim gate = new(1, 1);
    internal static async Task<ContentDialogResult> Show(ContentDialog dialog)
    {
        // WinUI permits one ContentDialog at a time. Queue service prompts
        // behind details/settings prompts without cancelling the original task.
        await gate.WaitAsync();
        try { return await dialog.ShowAsync(); }
        finally { gate.Release(); }
    }
}
