using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GalaxyWearable.Launcher;

internal static class Program
{
    private const string PayloadResourceName = "GalaxyWearable.Payload.zip";
    private const string AppExecutableName = "GalaxyWearable.Windows.exe";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    private static int Main()
    {
        string? extractionDirectory = null;

        try
        {
            using Stream payload = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(PayloadResourceName)
                ?? throw new InvalidOperationException(
                    "The application files are missing from this launcher. Please download a fresh build.");

            extractionDirectory = Path.Combine(
                Path.GetTempPath(),
                "GalaxyWearable",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(extractionDirectory);

            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                string root = Path.GetFullPath(extractionDirectory) + Path.DirectorySeparatorChar;

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string destination = Path.GetFullPath(
                        Path.Combine(extractionDirectory, entry.FullName));

                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The application package contains an invalid file path.");
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }

            string executablePath = Path.Combine(extractionDirectory, AppExecutableName);
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException(
                    $"The packaged application executable '{AppExecutableName}' was not found.");
            }

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = extractionDirectory,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Windows could not start Galaxy Wearable.");

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero,
                "Galaxy Wearable couldn't start.\n\n" + ex.Message,
                "Galaxy Wearable",
                0x10);
            return 1;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(extractionDirectory))
            {
                try
                {
                    if (Directory.Exists(extractionDirectory))
                    {
                        Directory.Delete(extractionDirectory, recursive: true);
                    }
                }
                catch
                {
                    // Windows may briefly hold files after the app exits. A later
                    // launch or Windows temporary-file cleanup can remove leftovers.
                }
            }
        }
    }
}
