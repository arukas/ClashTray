using System.Text;

namespace ClashTray.Setup;

internal static class SetupLog
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ClashTray",
        "setup.log");

    public static void Write(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            File.AppendAllText(
                FilePath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Diagnostics must never make installation fail.
        }
    }
}
