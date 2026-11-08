using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using KeithGariepy.CrestronSample;

// This desktop transport drives the same ported protocol source over real TCP.
// The processor's Crestron socket/timer adapter is validated separately.
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2 || args.Length > 3)
                throw new ArgumentException("Expected port, scenario, and optional password-file path.");

            int port = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
            string scenario = args[1];
            string password = args.Length == 3 ? File.ReadAllText(args[2]).TrimEnd('\r', '\n') : "";
            Run(port, scenario, password);
            Console.WriteLine("PASS " + scenario);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error.GetType().Name + ". " + error.Message);
            return 1;
        }
    }

    private static void Run(int port, string scenario, string password)
    {
        using (var client = new ProtocolClient(port, scenario == "wrong-password" ? "WrongTestCredential" : password))
        {
            if (scenario == "no-greeting")
            {
                client.AwaitFailure("greeting");
                Require(client.Results.Count == 0, "Unexpected command completion during greeting timeout.");
            }
            else
            {
                client.AwaitReady();
                switch (scenario)
                {
                    case "normal":
                    case "fragmented":
                    case "authenticated":
                        PowerCycle(client);
                        return;
                    case "device-error":
                        client.Query();
                        client.AwaitResult(1);
                        Require(!client.Results[0].Succeeded, "Device error was treated as success.");
                        Require(client.Session.PowerState == DisplayPowerState.Unknown, "Device error invented feedback.");
                        client.Query();
                        client.AwaitResult(2);
                        Require(client.Results[1].Succeeded && client.Session.PowerState == DisplayPowerState.Off,
                            "The next command did not recover on the same session.");
                        return;
                    case "no-response":
                    case "late-response":
                        client.Query();
                        client.AwaitFailure("command response");
                        Require(client.Results.Count == 0, "A timed-out command completed unexpectedly.");
                        break;
                    case "disconnect":
                        client.Query();
                        client.AwaitFailure("disconnected");
                        break;
                    case "non-ascii":
                        client.Query();
                        client.AwaitFailure("ASCII");
                        break;
                    case "reject-auth":
                    case "wrong-password":
                        client.Query();
                        client.AwaitFailure("authentication");
                        Require(client.Results.Count == 0, "Authentication failure completed a command.");
                        break;
                    default:
                        throw new ArgumentException("Unknown integration scenario.");
                }
            }
            Require(!client.Session.IsReady && !client.Session.HasPendingCommand,
                "Fault left protocol ready or a command pending.");
            Require(client.Session.PowerState == DisplayPowerState.Unknown, "Fault retained confirmed state.");
            client.RequireClosedTransport();
        }

        // Fault scenarios consume their single fault before this fresh connection.
        using (var recovered = new ProtocolClient(port, password))
        {
            recovered.AwaitReady();
            recovered.Query();
            recovered.AwaitResult(1);
            Require(recovered.Results[0].Succeeded && recovered.Session.PowerState == DisplayPowerState.Off,
                "Explicit reconnection did not recover confirmed feedback.");
            if (scenario == "late-response")
            {
                // Keep observing the fresh connection beyond the old response's delay.
                recovered.ObserveIdle(TimeSpan.FromMilliseconds(650));
                Require(recovered.Results.Count == 1 && recovered.Session.PowerState == DisplayPowerState.Off,
                    "Late feedback altered the recovered session.");
                recovered.Query();
                recovered.AwaitResult(2);
                Require(recovered.Results[1].Succeeded && recovered.Session.PowerState == DisplayPowerState.Off,
                    "The recovered session failed after the late-response window.");
            }
        }
    }

    private static void PowerCycle(ProtocolClient client)
    {
        client.Query();
        client.AwaitResult(1);
        Require(client.Session.PowerState == DisplayPowerState.Off, "Initial query must report Off.");

        client.SetPower(true);
        client.AwaitResult(2);
        Require(client.Results[1].Succeeded, "Power-on acknowledgement failed.");
        Require(client.Session.PowerState == DisplayPowerState.Off, "Acknowledgement invented confirmed On feedback.");
        client.Query();
        client.AwaitResult(3);
        Require(client.Session.PowerState == DisplayPowerState.Warming, "Warming feedback was not observed.");
        client.WaitForPower(DisplayPowerState.On);

        int count = client.Results.Count;
        client.SetPower(false);
        client.AwaitResult(count + 1);
        Require(client.Results[count].Succeeded, "Power-off acknowledgement failed.");
        Require(client.Session.PowerState == DisplayPowerState.On, "Acknowledgement invented confirmed Off feedback.");
        client.Query();
        client.AwaitResult(count + 2);
        Require(client.Session.PowerState == DisplayPowerState.Cooling, "Cooling feedback was not observed.");
        client.WaitForPower(DisplayPowerState.Off);
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

internal sealed class ProtocolClient : IDisposable
{
    private readonly TcpClient client = new TcpClient();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly byte[] receiveBuffer = new byte[4096];
    private string failure;

    internal ProtocolClient(int port, string password)
    {
        Session = new PjLinkSession(password, TimeSpan.FromMilliseconds(350));
        Session.SendRequested += Send;
        Session.CommandCompleted += Results.Add;
        Session.Faulted += reason =>
        {
            failure = reason;
            client.Close();
        };
        client.Connect("127.0.0.1", port);
        client.NoDelay = true;
        Session.Begin(clock.Elapsed);
    }

    internal PjLinkSession Session { get; }
    internal List<PjLinkCommandResult> Results { get; } = new List<PjLinkCommandResult>();

    internal void AwaitReady()
    {
        PumpUntil(() => Session.IsReady, TimeSpan.FromSeconds(3));
    }

    internal void Query()
    {
        Session.QueryPower(clock.Elapsed);
    }

    internal void SetPower(bool on)
    {
        Session.SetPower(on, clock.Elapsed);
    }

    internal void AwaitResult(int count)
    {
        PumpUntil(() => Results.Count >= count, TimeSpan.FromSeconds(3));
        Program.Require(Results.Count == count, "Multiple completions for one command.");
    }

    internal void AwaitFailure(string expected)
    {
        PumpUntil(() => failure != null, TimeSpan.FromSeconds(3));
        Program.Require(failure.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "Unexpected fault category.");
    }

    internal void WaitForPower(DisplayPowerState expected)
    {
        TimeSpan deadline = clock.Elapsed + TimeSpan.FromSeconds(2);
        while (Session.PowerState != expected && clock.Elapsed < deadline)
        {
            Thread.Sleep(25);
            int count = Results.Count;
            Query();
            AwaitResult(count + 1);
        }
        Program.Require(Session.PowerState == expected, "Power transition did not complete within test budget.");
    }

    internal void ObserveIdle(TimeSpan duration)
    {
        TimeSpan until = clock.Elapsed + duration;
        PumpUntil(() => clock.Elapsed >= until, duration + TimeSpan.FromSeconds(1));
        Program.Require(failure == null, "Recovered session failed during idle observation.");
    }

    internal void RequireClosedTransport()
    {
        try
        {
            client.GetStream();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        throw new InvalidOperationException("Fault left the original TCP client open.");
    }

    private void Send(string command)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(command);
        client.GetStream().Write(bytes, 0, bytes.Length);
    }

    private void PumpUntil(Func<bool> condition, TimeSpan budget)
    {
        TimeSpan deadline = clock.Elapsed + budget;
        while (!condition())
        {
            Program.Require(clock.Elapsed < deadline, "Observation exceeded integration-test budget.");
            Session.CheckTimeout(clock.Elapsed);
            if (condition())
                return;
            Program.Require(failure == null, "Protocol failed before expected observation.");
            if (!client.Client.Poll(10000, SelectMode.SelectRead))
                continue;
            int count = client.Client.Receive(receiveBuffer);
            if (count == 0)
            {
                TransportFailure("Transport peer disconnected.");
                continue;
            }
            for (int index = 0; index < count; index++)
            {
                if (receiveBuffer[index] > 127)
                {
                    TransportFailure("Transport rejected non-ASCII data.");
                    break;
                }
            }
            if (failure == null)
                Session.Receive(Encoding.ASCII.GetString(receiveBuffer, 0, count), clock.Elapsed);
        }
    }

    private void TransportFailure(string reason)
    {
        failure = reason;
        Session.Disconnect();
        client.Close();
    }

    public void Dispose()
    {
        Session.Disconnect();
        client.Dispose();
    }
}
