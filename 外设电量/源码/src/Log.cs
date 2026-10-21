using System;
using System.IO;
using System.Text;

namespace BatteryPeek
{
    /// <summary>
    /// Tiny append-only log next to the user's AppData. A tray app has nowhere to print,
    /// so when something goes wrong on someone else's machine this file is the only evidence.
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();

        /// <summary>
        /// Preferred location, with a %TEMP% fallback: locked-down environments (and sandboxes)
        /// sometimes deny writes under AppData, and losing the log there would hide real bugs.
        /// </summary>
        private static readonly string[] CandidateDirectories =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryPeek"),
            Path.Combine(Path.GetTempPath(), "BatteryPeek")
        };

        private static string resolvedPath;

        internal static string Path_
        {
            get { return resolvedPath ?? Resolve(); }
        }

        private static string Resolve()
        {
            foreach (string directory in CandidateDirectories)
            {
                try
                {
                    if (!System.IO.Directory.Exists(directory))
                    {
                        System.IO.Directory.CreateDirectory(directory);
                    }

                    string candidate = System.IO.Path.Combine(directory, "app.log");
                    using (var probe = new FileStream(candidate, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        // opening it proves the directory is writable
                    }

                    resolvedPath = candidate;
                    return candidate;
                }
                catch (Exception)
                {
                    // try the next candidate
                }
            }

            return null;
        }

        internal static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    string target = Path_;
                    if (target == null)
                    {
                        return;
                    }

                    var info = new FileInfo(target);
                    if (info.Exists && info.Length > 256 * 1024)
                    {
                        File.Delete(target);
                    }

                    File.AppendAllText(target,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                // Logging must never take the app down, but surface the reason when a console exists.
                try { Console.Error.WriteLine("Log.Write failed: " + ex); } catch { }
            }
        }
    }
}
