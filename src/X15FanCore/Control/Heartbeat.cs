using System;
using System.IO;
using System.Text;

namespace X15FanCore.Control
{
    public sealed class Heartbeat
    {
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
            string directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            if (File.Exists(FilePath))
            {
                File.Replace(temporary, FilePath, null);
            }
            else
            {
                File.Move(temporary, FilePath);
            }
        }
    }
}
