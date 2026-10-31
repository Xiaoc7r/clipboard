using System;
using System.Collections.Generic;
using System.IO;

namespace PicoPaste
{
    internal static class LocalStoreTests
    {
        private static void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
        }

        private static void Main()
        {
            string directory = Path.Combine(Path.GetTempPath(), "PicoPaste-tests-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("PICOPASTE_DATA_DIR", directory);
            try
            {
                var first = new List<ClipEntry> { new ClipEntry { Text = "synthetic old text", CreatedAt = DateTime.UtcNow } };
                var next = new List<ClipEntry> { new ClipEntry { Text = "synthetic new text", CreatedAt = DateTime.UtcNow } };
                Check(LocalStore.SaveHistory(first), "First save failed");
                Check(LocalStore.LoadHistory()[0].Text == first[0].Text, "First round trip failed");
                string path = Path.Combine(directory, "history.json");
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Check(!LocalStore.SaveHistory(next), "A failed replacement must be reported");
                    Check(LocalStore.LoadHistory()[0].Text == first[0].Text, "Failed save destroyed previous history");
                }
                Check(LocalStore.SaveHistory(next), "Retry after unlocking failed");
                Check(LocalStore.LoadHistory()[0].Text == next[0].Text, "Replacement did not persist new history");
                Check(!File.Exists(path + ".tmp"), "Successful replacement left a temporary file");
                Console.WriteLine("PASS: 7 isolated persistence checks; no clipboard access.");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }
}
