// Test hook for the drop path, which otherwise needs a real drag from Explorer (Debug builds only):
//   Windex.exe --test-drop "C:\a.txt;C:\b.png" [--test-drop "C:\c.bin" ...]
// Once the focused pane of the window on screen can take files, each drop in turn shows that
// card's drop overlay for two seconds and then drops its files there through the code a real drop
// runs (DragContents.ReadAsync, Offer, ShowDropOverlay, AcceptDropAsync on a DataPackage of the
// files). Several drops start transfers that overlap.
#if DEBUG
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Windex.Panes;

public sealed partial class PaneArea
{
    private static List<string[]>? _testDrops = TestDrops();

    private static List<string[]>? TestDrops()
    {
        var args = Environment.GetCommandLineArgs();
        var drops = new List<string[]>();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--test-drop") drops.Add(args[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return drops.Count > 0 ? drops : null;
    }

    partial void RunTestDrop()
    {
        if (_testDrops is not { } drops || _window is not { } window || _dropTarget is null) return;
        ulong pane = window.FocusedPane;
        if (!_shown.Contains(pane) || !_dropTarget.OfferFor(pane).Accepts) return;
        _testDrops = null;
        DispatcherQueue.TryEnqueue(async () =>
        {
            foreach (var files in drops)
            {
                var items = new List<IStorageItem>();
                foreach (var path in files)
                    items.Add(Directory.Exists(path) ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path));
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetStorageItems(items);
                var data = package.GetView();
                _drag = await DragContents.ReadAsync(data);
                var offer = Offer(pane);
                ShowDropOverlay(pane, offer);
                await Task.Delay(TimeSpan.FromSeconds(2));
                HideDropOverlay();
                if (offer.Accepts) await AcceptDropAsync(pane, data);
            }
        });
    }
}
#endif
