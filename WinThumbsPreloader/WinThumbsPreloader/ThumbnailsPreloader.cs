private async void Run()
{
    await Task.Run(() =>
    {
        using (Process p = Process.GetCurrentProcess())
            p.PriorityClass = ProcessPriorityClass.BelowNormal;

        state = ThumbnailsPreloaderState.Processing; // Skip "GettingNumberOfItems" phase entirely
        
        // Use the streaming scanner
        var fileStream = directoryScanner.GetFiles();

        if (!_multiThreaded)
        {
            var TBCacheType = Type.GetTypeFromCLSID(CLSID_LocalThumbnailCache);
            var tbCache = (ThumbnailPreloader.IThumbnailCache)Activator.CreateInstance(TBCacheType);
            try
            {
                foreach (string item in fileStream)
                {
                    if (state == ThumbnailsPreloaderState.Canceled) break;
                    
                    // Update total count dynamically or just show "Processed: X"
                    // Since we are streaming, we don't know the Total ahead of time.
                    // If you strictly need a Progress Bar (0-100%), you MUST do the two-pass scan (scan all, then process).
                    // If you prefer speed, you switch the UI to "Processed: 500 items..." (Indeterminate progress).
                    
                    try
                    {
                        currentFile = item;
                        ThumbnailPreloader.PreloadThumbnail(item, tbCache, IID_IShellItem);
                        Interlocked.Increment(ref processedItemsCount);
                    }
                    catch { }
                }
            }
            finally 
            {
                if (tbCache != null) Marshal.ReleaseComObject(tbCache);
            }
        }
        else
        {
             // For Parallel.ForEach to work with a stream, it simply consumes the IEnumerable
             Parallel.ForEach(fileStream, 
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                () => {
                    var TBCacheType = Type.GetTypeFromCLSID(CLSID_LocalThumbnailCache);
                    return (ThumbnailPreloader.IThumbnailCache)Activator.CreateInstance(TBCacheType);
                },
                (item, loopState, index, tbCache) => {
                    if (state == ThumbnailsPreloaderState.Canceled) loopState.Stop();
                    ThumbnailPreloader.PreloadThumbnail(item, tbCache, IID_IShellItem);
                    Interlocked.Increment(ref processedItemsCount);
                    return tbCache;
                },
                (tbCache) => { if (tbCache != null) Marshal.ReleaseComObject(tbCache); }
             );
        }
        
        state = ThumbnailsPreloaderState.Done;
    });
    Application.Exit();
}
