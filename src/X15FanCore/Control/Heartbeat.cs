using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace X15FanCore.Control
{
    public sealed class Heartbeat
    {
        private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
        private readonly object _writeLock = new object();
        private readonly string _eventName;
        private EventWaitHandle _pulseEvent;

        public Heartbeat(string filePath)
        {
            FilePath = filePath;
            _eventName = GetEventName(filePath);
        }

        public string FilePath { get; private set; }

        public static string GetEventName(string filePath)
        {
            string fullPath = Path.GetFullPath(filePath).ToUpperInvariant();
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(fullPath));
                StringBuilder hex = new StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes) hex.Append(value.ToString("x2"));
                return "Local\\X15FanControl-Heartbeat-" + hex;
            }
        }

        public static EventWaitHandle OpenExistingPulseEvent(string filePath)
        {
            try
            {
                return EventWaitHandle.OpenExisting(GetEventName(filePath));
            }
            catch
            {
                return null;
            }
        }

        public void WriteActive(int parentProcessId)
        {
            Write("ACTIVE|" + parentProcessId + "|" + DateTime.UtcNow.ToString("O"));
        }

        public void WriteStop()
        {
            Write("STOP|0|" + DateTime.UtcNow.ToString("O"));
        }

        private void Write(string text)
        {
            lock (_writeLock)
            {
                string directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                for (int attempt = 0; attempt < 4; attempt++)
                {
                    string temporary = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.WriteAllText(temporary, text, Utf8WithoutBom);
                        Publish(temporary);
                        // File.Replace normally carries the temporary file's
                        // timestamp across. Explicitly refresh it as well so
                        // watchdogs on file systems with unusual replace
                        // semantics do not mistake a live heartbeat for a
                        // stale one.
                        File.SetLastWriteTimeUtc(FilePath, DateTime.UtcNow);
                        SignalPulse();
                        return;
                    }
                    catch (IOException)
                    {
                        if (attempt == 3)
                        {
                            // Heartbeat publication is diagnostic/supervisory
                            // state. A transient sharing violation must not
                            // crash the control loop or the UI safety path;
                            // the independent watchdog will fail safe if the
                            // heartbeat remains unavailable.
                            return;
                        }
                        Thread.Sleep(25 * (attempt + 1));
                    }
                    finally
                    {
                        try
                        {
                            if (File.Exists(temporary))
                            {
                                File.Delete(temporary);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private void SignalPulse()
        {
            try
            {
                if (_pulseEvent == null)
                {
                    lock (_writeLock)
                    {
                        if (_pulseEvent == null)
                        {
                            _pulseEvent = new EventWaitHandle(
                                false,
                                EventResetMode.AutoReset,
                                _eventName);
                        }
                    }
                }

                _pulseEvent.Set();
            }
            catch
            {
                // The file heartbeat remains the diagnostic fallback.
            }
        }

        private void Publish(string temporary)
        {
            if (File.Exists(FilePath))
            {
                File.Replace(temporary, FilePath, null);
                return;
            }

            try
            {
                File.Move(temporary, FilePath);
            }
            catch (IOException)
            {
                // Another writer may have created the destination after the
                // existence check. Replacing it keeps publication atomic.
                File.Replace(temporary, FilePath, null);
            }
        }
    }
}
