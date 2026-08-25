using Windows.Storage.Pickers;

namespace NetTrans.Services;

/// <summary>
/// The folder picker behind 默认位置.
///
/// An unpackaged WinUI 3 app has no window of its own as far as the picker is
/// concerned, so it has to be told which HWND to parent to; without that it
/// throws rather than showing anything.
/// </summary>
public static class FolderPrompt
{
    /// <summary>
    /// Returns the chosen folder, or null when the user cancelled or the
    /// picker could not be shown. There is no way to open it at a specific
    /// path -- the API only takes one of a handful of known locations -- so it
    /// starts at 下载 and the user navigates from there.
    /// </summary>
    public static async Task<string?> PickAsync()
    {
        if (App.MainAppWindow is not { } window) return null;

        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List,
            };

            // The filter list may not be empty, and "*" means any folder.
            picker.FileTypeFilter.Add("*");

            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(window));

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception)
        {
            // A picker that will not open is not worth taking the app down for;
            // the caller simply keeps the folder it had.
            return null;
        }
    }
}

/// <summary>The open dialog behind 种子文件, with the same HWND caveat.</summary>
public static class FilePrompt
{
    /// <summary>Returns the chosen .torrent, or null when the user cancelled.</summary>
    public static async Task<string?> PickTorrentAsync()
    {
        if (App.MainAppWindow is not { } window) return null;

        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            };

            picker.FileTypeFilter.Add(".torrent");

            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(window));

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
