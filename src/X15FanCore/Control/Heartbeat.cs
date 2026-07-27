using System;
using System.IO;
using System.Text;
using System.Threading;

namespace X15FanCore.Control
{
    public sealed class Heartbeat
    {
        private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
        private readonly object _writeLock = new object();

        public Heartbeat(string filePath)
        {
            FilePath = filePath;
        }

        public string FilePath { get; private set; }

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

                IOException lastError = null;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    string temporary = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.WriteAllText(temporary, text, Utf8WithoutBom);
                        Publish(temporary);
                        return;
                    }
                    catch (IOException exception)
                    {
                        lastError = exception;
                        if (attempt == 3)
                        {
                            throw;
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

                throw lastError ?? new IOException("Unable to publish heartbeat.");
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
