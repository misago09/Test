using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace PptFigmaDrag
{
    // Chronological event log merging raw input, hook gate decisions, gesture
    // state, viewport samples and guard actions - one file that lets a failure
    // session be diagnosed remotely. Toggled together with the tray's log item.
    internal static class DiagLog
    {
        private static readonly object Sync = new object();
        private static StreamWriter _writer;
        private static volatile bool _enabled;

        public static string LogPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PptFigmaDrag", "event-log.txt");
            }
        }

        public static bool Enabled
        {
            get { return _enabled; }
            set
            {
                lock (Sync)
                {
                    if (value && _writer == null)
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                            // fresh file per session, so what the user shares is small
                            _writer = new StreamWriter(LogPath, false, Encoding.UTF8);
                            _writer.AutoFlush = true;
                            _writer.WriteLine("=== PPT Figma Drag event log " +
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ===");
                            // The exe timestamp instantly answers "is this the build
                            // we think it is?" - a recurring failure mode.
                            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                            _writer.WriteLine("exe: " + exe);
                            _writer.WriteLine("built: " + File.GetLastWriteTime(exe)
                                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                        }
                        catch
                        {
                            _writer = null;
                            _enabled = false;
                            return;
                        }
                    }
                    else if (!value && _writer != null)
                    {
                        try
                        {
                            _writer.Flush();
                            _writer.Dispose();
                        }
                        catch
                        {
                        }
                        _writer = null;
                    }
                    _enabled = value;
                }
            }
        }

        public static void Log(string source, string message)
        {
            if (!_enabled)
                return;
            lock (Sync)
            {
                if (_writer == null)
                    return;
                try
                {
                    _writer.WriteLine(
                        DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                        " [" + source + "] " + message);
                }
                catch
                {
                }
            }
        }
    }
}
