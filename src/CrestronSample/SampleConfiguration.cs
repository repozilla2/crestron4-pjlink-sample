using System.IO;

namespace KeithGariepy.CrestronSample;

internal static class SampleConfiguration
{
    // Reserved documentation address. Configure a private working copy before use.
    internal const string DisplayHost = "192.0.2.10";
    internal const int DisplayPort = 4352;

    internal const int ResponseTimeoutSeconds = 30;

    internal static string ReadPassword()
    {
        string directory = Crestron.SimplSharp.CrestronIO.Directory.GetApplicationDirectory();
        string path = Path.Combine(directory, "pjlink-password.txt");
        try
        {
            return File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
    }
}
