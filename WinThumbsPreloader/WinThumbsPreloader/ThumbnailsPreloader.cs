using System;
using System.Windows.Forms;
using System.IO;
using WinThumbsPreloader.Properties;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;

namespace WinThumbsPreloader
{
    public enum ThumbnailsPreloaderState
    {
        New,
        GettingNumberOfItems,
        Processing,
        Canceled,
        Done
    }

    //Preload all thumbnails, show progress dialog
    class ThumbnailsPreloader
    {
        private DirectoryScanner directoryScanner;
        private ProgressDialog progressDialog;
        private System.Windows.Forms.Timer progressDialogUpdateTimer;

        protected bool _multiThreaded;

        public ThumbnailsPreloaderState state = ThumbnailsPreloaderState.GettingNumberOfItems;
        public ThumbnailsPreloaderState prevState = ThumbnailsPreloaderState.New;
        public int totalItemsCount = 0;
        public int processedItemsCount = 0;
        public string currentFile = "";

        string executablePath = "";

        // Definitions needed for instantiation
        Guid CLSID_LocalThumbnailCache = new Guid("50ef4544-ac9f-4a8e-b21b-8a26180db13f");
        Guid IID_IShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        public ThumbnailsPreloader(string path, bool includeNestedDirectories, bool silentMode, bool multiThreaded)
        {
            //Set the process priority to Below Normal to prevent system unresponsiveness
            using (Process p = Process.GetCurrentProcess())
                p.PriorityClass = ProcessPriorityClass.BelowNormal;

            // Single File Mode for when passing a file through the command line or preloading a .svg file
            executablePath = Process.GetCurrentProcess().MainModule.FileName;
            FileAttributes fAt = File.GetAttributes(path);
            if (!fAt.HasFlag(FileAttributes.Directory)) // path is file and not a directory
            {
                // Instantiate cache just for this single file
                var TBCacheType = Type.GetTypeFromCLSID(CLSID_LocalThumbnailCache);
                var tbCache = (ThumbnailPreloader.IThumbnailCache)Activator.CreateInstance(TBCacheType);
                try 
                {
                    ThumbnailPreloader.PreloadThumbnail(path, tbCache, IID_IShellItem); 
                }
                finally
                {
                    if (tbCache != null) Marshal.ReleaseComObject(tbCache);
                }
                Environment.Exit(0);
            }

            // Normal mode
            directoryScanner = new DirectoryScanner(path, includeNestedDirectories);
            if (!silentMode)
            {
                InitProgressDialog();
                InitProgressDialogUpdateTimer();
            }
            _multiThreaded = multiThreaded;
            Run();
        }

        private void InitProgressDialog()
        {
            progressDialog = new ProgressDialog();
            progressDialog.AutoClose = false;
            progressDialog.ShowTimeRemaining = false;
            progressDialog.Title = "WinThumbsPreloader";
            progressDialog.CancelMessage = Resources.ThumbnailsPreloader_CancelMessage;
            progressDialog.Maximum = 100;
            progressDialog.Value = 0;
            progressDialog.Show();
            UpdateProgressDialog(null, null);
        }

        private void InitProgressDialogUpdateTimer()
        {
            progressDialogUpdateTimer = new System.Windows.Forms.Timer();
            progressDialogUpdateTimer.Interval = 250;
            progressDialogUpdateTimer.Tick += new EventHandler(UpdateProgressDialog);
            progressDialogUpdateTimer.Start();
        }

        private void UpdateProgressDialog(object sender, EventArgs e)
        {
            if (progressDialog.HasUserCancelled)
            {
                state = ThumbnailsPreloaderState.Canceled;
            }
            else if (state == ThumbnailsPreloaderState.GettingNumberOfItems)
            {
                if (prevState != state)
                {
                    prevState = state;
                    progressDialog.Line1 = Resources.ThumbnailsPreloader_PreloadingThumbnails;
                    progressDialog.Line3 = Resources.ThumbnailsPreloader_CalculatingNumberOfItems;
                    progressDialog.Marquee = true;
                }
                progressDialog.Line2 = String.Format(Resources.ThumbnailsPreloader_Discovered0Items, totalItemsCount);
            }
            else if (state == ThumbnailsPreloaderState.Processing)
            {
                if (prevState != state)
                {
                    prevState = state;
                    progressDialog.Line1 = String.Format(Resources.ThumbnailsPreloader_PreloadingThumbnailsFor0Items, totalItemsCount);
                    progressDialog.Maximum = totalItemsCount;
                    progressDialog.Marquee = false;
                }
                // Calculate percentage safely
                int currentCount = processedItemsCount; 
                int percent = totalItemsCount > 0 ? (currentCount * 100) / totalItemsCount : 0;
                
                progressDialog.Title = String.Format(Resources.ThumbnailsPreloader_Processing, percent);
                progressDialog.Line2 = Resources.ThumbnailsPreloader_Name + ": " + Path.GetFileName(currentFile);
                progressDialog.Line3 = String.Format(Resources.ThumbnailsPreloader_ItemsRemaining, totalItemsCount - currentCount);
                progressDialog.Value = currentCount;
            }
        }

        private async void Run()
        {
            await Task.Run(() =>
            {
                using (Process p = Process.GetCurrentProcess())
                    p.PriorityClass = ProcessPriorityClass.BelowNormal;

                state = ThumbnailsPreloaderState.GettingNumberOfItems;

                List<string> items = new List<string>();
                foreach (Tuple<int, List<string>> itemsCount in directoryScanner.GetItemsCount())
                {
                    totalItemsCount = itemsCount.Item1;
                    items = itemsCount.Item2;
                    if (state == ThumbnailsPreloaderState.Canceled) return;
                }
                if (totalItemsCount == 0)
                {
                    state = ThumbnailsPreloaderState.Done;
                    return;
                }

                state = ThumbnailsPreloaderState.Processing;

                if (!_multiThreaded)
                {
                    // Single-threaded optimization: Create Cache ONCE
                    var TBCacheType = Type.GetTypeFromCLSID(CLSID_LocalThumbnailCache);
                    var tbCache = (ThumbnailPreloader.IThumbnailCache)Activator.CreateInstance(TBCacheType);
                    
                    try
                    {
                        foreach (string item in items)
                        {
                            try
                            {
                                currentFile = item;
                                ThumbnailPreloader.PreloadThumbnail(item, tbCache, IID_IShellItem);
                                
                                Interlocked.Increment(ref processedItemsCount);
                                
                                if (processedItemsCount == totalItemsCount) state = ThumbnailsPreloaderState.Done;
                                if (state == ThumbnailsPreloaderState.Canceled) 
                                {
                                    Application.Exit();
                                    return; 
                                }
                            }
                            catch (Exception) { } 
                        }
                    }
                    finally
                    {
                        if (tbCache != null) Marshal.ReleaseComObject(tbCache);
                    }
                }
                else
                {
                    // Multi-threaded optimization: Create Cache ONCE PER THREAD
                    Parallel.ForEach(
                        items,
                        new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                        // 1. Init: Create the COM instance for this thread
                        () => 
                        {
                            var TBCacheType = Type.GetTypeFromCLSID(CLSID_LocalThumbnailCache);
                            return (ThumbnailPreloader.IThumbnailCache)Activator.CreateInstance(TBCacheType);
                        },
                        // 2. Body: Process file using the thread-local cache
                        (item, loopState, index, tbCache) =>
                        {
                            try
                            {
                                currentFile = item; // Still racey for UI, but acceptable for display only
                                ThumbnailPreloader.PreloadThumbnail(item, tbCache, IID_IShellItem);
                                
                                Interlocked.Increment(ref processedItemsCount);
                                
                                if (processedItemsCount == totalItemsCount) state = ThumbnailsPreloaderState.Done;
                                if (state == ThumbnailsPreloaderState.Canceled) 
                                {
                                    loopState.Stop();
                                    Application.Exit();
                                }
                            }
                            catch (Exception) { } 
                            return tbCache; // Pass cache to next iteration
                        },
                        // 3. Finally: Cleanup COM instance for this thread
                        (tbCache) => 
                        {
                             if (tbCache != null) Marshal.ReleaseComObject(tbCache);
                        }
                    );
                }
            });
            Application.Exit();
        }
    }
}
