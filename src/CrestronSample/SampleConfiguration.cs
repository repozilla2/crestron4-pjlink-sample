using System;
using System.IO;

namespace KeithGariepy.CrestronSample;

internal static class SampleConfiguration
{
    internal const string HostFileName = "pjlink-host.txt";
    internal const string PasswordFileName = "pjlink-password.txt";
    internal const int DisplayPort = 4352;
    internal const int ResponseTimeoutSeconds = 30;
    private const int MaximumConfigurationCharacters = 64;

    /// <summary>
    /// Reads the display address on every explicit connect, so commissioning can
    /// change the target without rebuilding the CPZ.
    /// </summary>
    internal static string ReadHost()
    {
        string host;
        try
        {
            host = ReadText(HostFileName);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException(
                "Place the display's IPv4 address in " + HostFileName + " in the program directory.");
        }

        string[] parts = host.Split('.');
        if (parts.Length != 4)
            throw new InvalidOperationException(HostFileName + " must contain one dotted IPv4 address.");

        int first = -1;
        foreach (string part in parts)
        {
            if (part.Length == 0 || part.Length > 3 || (part.Length > 1 && part[0] == '0'))
                throw new InvalidOperationException(HostFileName + " has an invalid IPv4 component.");
            int value = 0;
            foreach (char character in part)
            {
                if (character < '0' || character > '9')
                    throw new InvalidOperationException(HostFileName + " has an invalid IPv4 component.");
                value = value * 10 + character - '0';
            }
            if (value > 255)
                throw new InvalidOperationException(HostFileName + " has an invalid IPv4 component.");
            if (first < 0) first = value;
        }

        // Reject unspecified, loopback, multicast and reserved first-octet ranges.
        if (first == 0 || first == 127 || first >= 224)
            throw new InvalidOperationException(HostFileName + " must contain a unicast display address.");
        return host;
    }

    internal static string ReadPassword()
    {
        try
        {
            return ReadText(PasswordFileName);
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
    }

    private static string ReadText(string fileName)
    {
        string directory = Crestron.SimplSharp.CrestronIO.Directory.GetApplicationDirectory();
        using var reader = new StreamReader(Path.Combine(directory, fileName));
        var buffer = new char[MaximumConfigurationCharacters + 1];
        int count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count > MaximumConfigurationCharacters)
            throw new InvalidOperationException(fileName + " is too long.");
        return new string(buffer, 0, count).TrimEnd('\r', '\n');
    }
}
