using System;
using System.Text;
#if RMC3
using RuntimePath = Crestron.SimplSharp.CrestronIO.Path;
using RuntimeReader = Crestron.SimplSharp.CrestronIO.StreamReader;
using RuntimeFileNotFoundException = Crestron.SimplSharp.CrestronIO.FileNotFoundException;
#else
using RuntimePath = System.IO.Path;
using RuntimeReader = System.IO.StreamReader;
using RuntimeFileNotFoundException = System.IO.FileNotFoundException;
#endif

namespace KeithGariepy.CrestronSample
{
    internal static class SampleConfiguration
    {
        internal const int DisplayPort = 4352;
        internal const int ResponseTimeoutSeconds = 30;
        private const int MaximumConfigurationCharacters = 64;

        internal static string ReadHost()
        {
            string host = ReadText("pjlink-host.txt");
            string[] parts = host.Split('.');
            if (parts.Length != 4)
                throw new ArgumentException("PJLink host must be a private dotted IPv4 address.");

            int[] octets = new int[4];
            for (int index = 0; index < parts.Length; index++)
            {
                string part = parts[index];
                if (part.Length == 0 || part.Length > 3 || (part.Length > 1 && part[0] == '0'))
                    throw new ArgumentException("PJLink host has an invalid IPv4 component.");

                int value = 0;
                foreach (char character in part)
                {
                    if (character < '0' || character > '9')
                        throw new ArgumentException("PJLink host has an invalid IPv4 component.");
                    value = value * 10 + character - '0';
                }
                if (value > 255)
                    throw new ArgumentException("PJLink host has an invalid IPv4 component.");
                octets[index] = value;
            }

            bool isPrivate = octets[0] == 10 ||
                (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31) ||
                (octets[0] == 192 && octets[1] == 168);
            if (!isPrivate)
                throw new ArgumentException("PJLink host must be a private dotted IPv4 address.");

            return host;
        }

        internal static string ReadPassword()
        {
            try
            {
                return ReadText("pjlink-password.txt");
            }
            catch (RuntimeFileNotFoundException)
            {
                return string.Empty;
            }
        }

        private static string ReadText(string filename)
        {
            string directory = Crestron.SimplSharp.CrestronIO.Directory.GetApplicationDirectory();
            string path = RuntimePath.Combine(directory, filename);
            using (RuntimeReader reader = new RuntimeReader(path))
            {
                StringBuilder text = new StringBuilder();
                int value;
                while ((value = reader.Read()) != -1)
                {
                    if (text.Length == MaximumConfigurationCharacters)
                        throw new InvalidOperationException("PJLink configuration file is too long.");
                    text.Append((char)value);
                }
                return text.ToString().TrimEnd('\r', '\n');
            }
        }
    }
}
