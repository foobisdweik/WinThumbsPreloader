using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WinThumbsPreloader
{
    class DirectoryScanner
    {
        private string path;
        private bool includeNestedDirectories;
        // Removed the class-level 'filesList' to prevent memory bloat
        
        string[] thumbnailExtensions = ThumbnailExtensions();

        public DirectoryScanner(string path, bool includeNestedDirectories)
        {
            this.path = path;
            this.includeNestedDirectories = includeNestedDirectories;
        }

        public static string[] ThumbnailExtensions()
        {
            string[] defaultExtensions = { "avif", "bmp", "gif", "heic", "jpg", "jpeg", "mkv", "mov", "mp4", "png", "svg", "tif", "tiff", "webp" };
            string[] extensions;
            try
            {
                extensions = File.ReadAllLines("ThumbnailExtensions.txt")
                                          .SelectMany(line => line.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                                          .Where(ext => !string.IsNullOrWhiteSpace(ext))
                                          .Select(ext => ext.Trim(' ')).ToArray();
                if (extensions == null || extensions.Length == 0)
                {
                    extensions = defaultExtensions;
                }
            }
            catch (Exception)
            {
                extensions = defaultExtensions;
            }
            return extensions;
        }

        // Changed return type to simply yield strings one by one
        public IEnumerable<string> GetFiles()
        {
            var options = includeNestedDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            
            // Use EnumerateFiles for lazy evaluation (starts yielding immediately)
            // Note: EnumerateFiles can fail on permission errors if not handled carefully. 
            // Since we need robustness, we often have to do a custom recursive enumerate to catch access denied exceptions.
            return SafeEnumerateFiles(path, includeNestedDirectories);
        }

        private IEnumerable<string> SafeEnumerateFiles(string rootPath, bool recursive)
        {
            Queue<string> dirs = new Queue<string>();
            dirs.Enqueue(rootPath);

            while (dirs.Count > 0)
            {
                string currentDir = dirs.Dequeue();
                
                // 1. Process Files in current directory
                IEnumerable<string> files = null;
                try
                {
                    files = Directory.EnumerateFiles(currentDir);
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                catch (Exception) { continue; }

                if (files != null)
                {
                    foreach (string file in files)
                    {
                        string ext = "";
                        try { ext = Path.GetExtension(file).TrimStart('.'); } catch { }
                        
                        if (thumbnailExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) || thumbnailExtensions.Length == 0)
                        {
                            yield return file;
                        }
                    }
                }

                // 2. Queue Subdirectories if recursive
                if (recursive)
                {
                    IEnumerable<string> subDirs = null;
                    try
                    {
                        subDirs = Directory.EnumerateDirectories(currentDir);
                    }
                    catch (Exception) { }

                    if (subDirs != null)
                    {
                        foreach (var dir in subDirs)
                        {
                            dirs.Enqueue(dir);
                        }
                    }
                }
            }
        }
    }
}
