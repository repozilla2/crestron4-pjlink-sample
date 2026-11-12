using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;
using KeithGariepy.CrestronSample;
using System.Reflection;

// Compile the production adapter against SDK doubles to test its lifecycle decisions.
// Processor execution and native SDK callback behavior require separate hardware tests.
string directory = Path.Combine(Path.GetTempPath(), "crestron-adapter-tests-" + Guid.NewGuid().ToString("N"));
System.IO.Directory.CreateDirectory(directory);
Crestron.SimplSharp.CrestronIO.Directory.TestApplicationDirectory = directory;
File.WriteAllText(Path.Combine(directory, "pjlink-host.txt"), "192.168.10.20\n");

var tests = new (string Name, Action Run)[]
{
    ("Pause closes resources and suppresses commands and callbacks", Pause),
    ("Resume requires an explicit new connection", Resume),
    ("Stop while paused releases registrations and stays stopped", StopWhilePaused),
    ("Late callbacks cannot replace the current connection", StaleCallbacks),
    ("Inline callbacks defer disposal until the outer lock exits", InlineCallbacks),
    ("Failed registration preserves another console command", RegistrationOwnership),
    ("Send failure and invalid ASCII close the connection", TransportFailures),
    ("Timer-driven timeouts close the socket and permit reconnection", TimerTimeouts),
    ("Production construction uses the running monotonic Stopwatch", ProductionClock),
    ("Runtime password file supplies authentication without credential logs", RuntimePassword),
    ("Runtime host file selects each explicit connection", RuntimeHost),
    ("Host configuration rejects malformed and non-private IPv4", HostValidation),
    ("ASCII conversion rejects every high-bit byte", HighBitAscii),
    ("Configuration reads are bounded and errors exclude credentials", ConfigurationFiles),
    ("Console commands use invariant casing", InvariantCommands),
    ("Concurrent receive precedes stop with one cleanup", AdapterConcurrencyScenarios.ReceiveBeforeStop),
    ("Concurrent stop rejects queued receive", AdapterConcurrencyScenarios.StopBeforeReceive),
    ("Concurrent timeout rejects queued response", AdapterConcurrencyScenarios.TimeoutBeforeResponse),
    ("Concurrent response completes before timeout", AdapterConcurrencyScenarios.ResponseBeforeTimeout),
    ("Concurrent disconnect rejects queued send completion", AdapterConcurrencyScenarios.DisconnectBeforeSendCompletion),
    ("Concurrent failed send completion precedes disconnect", AdapterConcurrencyScenarios.SendCompletionBeforeDisconnect)
};

int failures = 0;
try
{
    foreach (var test in tests)
    {
        Reset();
        try
        {
            test.Run();
            Console.WriteLine("PASS " + test.Name);
        }
        catch (Exception exception)
        {
            failures++;
            Console.Error.WriteLine("FAIL " + test.Name + " - " + exception.Message);
        }
        finally
        {
            CrestronEnvironment.Raise(eProgramStatusEventType.Stopping);
        }
    }
}
finally
{
    System.IO.Directory.Delete(directory, true);
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} adapter test groups passed using SDK doubles.");
return failures == 0 ? 0 : 1;

static void Reset()
{
    CrestronEnvironment.Reset();
    CrestronConsole.Reset();
    TCPClient.Created.Clear();
    TCPClient.IsStateLockHeld = null;
    CTimer.Created.Clear();
}

static ControlSystem Start()
{
    var program = new ControlSystem();
    program.InitializeSystem();
    return program;
}

static TCPClient Connect()
{
    CrestronConsole.Command!("connect");
    TCPClient connection = TCPClient.Created.Last();
    connection.CompleteConnection();
    connection.Deliver("PJLINK 0\r");
    return connection;
}

static void Pause()
{
    Start();
    TCPClient connection = Connect();
    CTimer timer = CTimer.Created.Single();
    CrestronConsole.Command!("query");
    connection.Deliver("%1POWR=1\r");
    CrestronEnvironment.Raise(eProgramStatusEventType.Paused);
    Equal(true, connection.IsDisposed);
    Equal(true, timer.IsDisposed);
    Equal(true, CrestronConsole.Messages.Any(message => message.Contains("feedback = Unknown", StringComparison.Ordinal)));
    int messages = CrestronConsole.Messages.Count;
    foreach (string command in new[] { "connect", "disconnect", "status", "query", "on", "off" })
        CrestronConsole.Command!(command);
    connection.CompleteConnection();
    connection.Deliver("%1POWR=0\r");
    connection.CompleteSend(0);
    timer.DeliverCallback();
    Equal(1, TCPClient.Created.Count);
    Equal(1, connection.Sent.Count);
    Equal(messages, CrestronConsole.Messages.Count);
}

static void Resume()
{
    Start();
    TCPClient oldConnection = Connect();
    CrestronEnvironment.Raise(eProgramStatusEventType.Paused);
    CrestronEnvironment.Raise(eProgramStatusEventType.Resumed);
    Equal(2, CTimer.Created.Count);
    Equal(false, CTimer.Created.Last().IsDisposed);
    Equal(1, TCPClient.Created.Count);
    oldConnection.CompleteConnection();
    oldConnection.Deliver("PJLINK 0\r");
    TCPClient newConnection = Connect();
    CrestronConsole.Command!("query");
    newConnection.Deliver("%1POWR=0\r");
    Equal(2, TCPClient.Created.Count);
    Equal("%1POWR ?\r", newConnection.Sent.Single());
    Equal(false, newConnection.IsDisposed);
}

static void StopWhilePaused()
{
    Start();
    Connect();
    Action<string> savedConsoleCallback = CrestronConsole.Command!;
    CrestronEnvironment.Raise(eProgramStatusEventType.Paused);
    CrestronEnvironment.Raise(eProgramStatusEventType.Stopping);
    CrestronEnvironment.Raise(eProgramStatusEventType.Resumed);
    savedConsoleCallback("connect");
    Equal<Action<string>?>(null, CrestronConsole.Command);
    Equal(1, TCPClient.Created.Count);
    Equal(1, CTimer.Created.Count);
    Equal(true, TCPClient.Created.Single().IsDisposed);
    Equal(true, CTimer.Created.Single().IsDisposed);
}

static void StaleCallbacks()
{
    Start();
    TCPClient oldConnection = Connect();
    CrestronConsole.Command!("disconnect");
    TCPClient newConnection = Connect();
    oldConnection.CompleteConnection();
    oldConnection.Deliver("PJLINK ERRA\r");
    CrestronConsole.Command!("query");
    newConnection.Deliver("%1POWR=1\r");
    Equal(false, newConnection.IsDisposed);
    Equal(1, newConnection.Sent.Count);
}

static void TimerTimeouts()
{
    TimeSpan now = TimeSpan.Zero;
    var program = new ControlSystem(() => now);
    program.InitializeSystem();
    CTimer timer = CTimer.Created.Single();

    // Greeting timeout: the connection completes but the display never greets.
    CrestronConsole.Command!("connect");
    TCPClient silent = TCPClient.Created.Last();
    silent.CompleteConnection();
    now = TimeSpan.FromSeconds(29.9);
    timer.DeliverCallback();
    Equal(false, silent.IsDisposed);
    now = TimeSpan.FromSeconds(30);
    timer.DeliverCallback();
    Equal(true, silent.IsDisposed);
    Equal(true, CrestronConsole.Messages.Any(message => message.Contains("Timed out waiting for the PJLink greeting", StringComparison.Ordinal)));

    // Command timeout: a query with no reply closes the socket and clears feedback.
    TCPClient slow = Connect();
    CrestronConsole.Command!("query");
    slow.Deliver("%1POWR=1\r");
    CrestronConsole.Command!("query");
    now += TimeSpan.FromSeconds(30);
    timer.DeliverCallback();
    Equal(true, slow.IsDisposed);
    Equal(true, CrestronConsole.Messages.Any(message => message.Contains("Timed out waiting for the PJLink command response", StringComparison.Ordinal)));
    Equal("PJLink power feedback = Unknown.", CrestronConsole.Messages.Last(message => message.StartsWith("PJLink power feedback", StringComparison.Ordinal)));

    // A late reply on the timed-out socket is ignored, and an explicit reconnect works.
    slow.Deliver("%1POWR=0\r");
    TCPClient fresh = Connect();
    CrestronConsole.Command!("query");
    Equal("%1POWR ?\r", fresh.Sent.Single());
    Equal(false, fresh.IsDisposed);
}

static void ProductionClock()
{
    // The public constructor is the one the processor invokes.
    ControlSystem program = Start();
    BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
    var stopwatch = (System.Diagnostics.Stopwatch)typeof(ControlSystem).GetField("clock", flags)!.GetValue(program)!;
    var now = (Func<TimeSpan>)typeof(ControlSystem).GetField("now", flags)!.GetValue(program)!;
    Equal(true, stopwatch.IsRunning);
    TimeSpan first = now();
    Thread.Sleep(20);
    TimeSpan second = now();
    Equal(true, second - first >= TimeSpan.FromMilliseconds(15));
    Equal(true, second <= stopwatch.Elapsed);
}

static void TransportFailures()
{
    Start();
    TCPClient connection = Connect();
    connection.ThrowOnSend = true;
    CrestronConsole.Command!("query");
    Equal(true, connection.IsDisposed);
    TCPClient replacement = Connect();
    replacement.Deliver(new byte[] { 0x80 });
    Equal(true, replacement.IsDisposed);
}

static void InlineCallbacks()
{
    ControlSystem program = Start();
    object gate = typeof(ControlSystem).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(program)!;
    TCPClient.IsStateLockHeld = () => Monitor.IsEntered(gate);
    TCPClient sending = Connect();
    sending.FailSendInline = true;
    CrestronConsole.Command!("query");
    Equal(true, sending.IsDisposed);
    Equal(false, sending.DisposedWithStateLock);

    TCPClient receiving = Connect();
    CrestronConsole.Command!("query");
    receiving.EndReceiveInline = true;
    receiving.Deliver("%1POWR=1\r");
    Equal(true, receiving.IsDisposed);
    Equal(false, receiving.DisposedWithStateLock);
}

static void RegistrationOwnership()
{
    Action<string> existingCommand = _ => { };
    CrestronConsole.AddNewConsoleCommand(existingCommand, "pjlink", "Existing command", ConsoleAccessLevelEnum.AccessOperator);
    CrestronConsole.RejectRegistration = true;
    Start();
    CrestronEnvironment.Raise(eProgramStatusEventType.Stopping);
    Equal(0, CrestronConsole.RemoveCalls);
    Equal(existingCommand, CrestronConsole.Command);
}

static void RuntimePassword()
{
    const string password = "JBMIAProjectorLink";
    const string digest = "5d8409bc1c3fa39749434aa3a5c38682";
    string path = Path.Combine(Crestron.SimplSharp.CrestronIO.Directory.TestApplicationDirectory, "pjlink-password.txt");
    File.WriteAllText(path, password + "\r\n");
    try
    {
        Start();
        CrestronConsole.Command!("connect");
        TCPClient connection = TCPClient.Created.Single();
        connection.CompleteConnection();
        connection.Deliver("PJLINK 1 498e4a67\r");
        CrestronConsole.Command!("query");
        Equal(digest + "%1POWR ?\r", connection.Sent.Single());
        Equal(false, CrestronConsole.Messages.Any(message => message.Contains(password, StringComparison.Ordinal)));
        Equal(false, CrestronConsole.Messages.Any(message => message.Contains(digest, StringComparison.Ordinal)));
    }
    finally
    {
        File.Delete(path);
    }
}

static string ConfigPath(string filename)
{
    return Path.Combine(Crestron.SimplSharp.CrestronIO.Directory.TestApplicationDirectory, filename);
}

static void RuntimeHost()
{
    Start();
    File.WriteAllText(ConfigPath("pjlink-host.txt"), "192.168.10.20\r\n");
    TCPClient first = Connect();
    Equal("192.168.10.20", first.Host);
    Equal(4352, first.Port);
    CrestronConsole.Command!("disconnect");
    File.WriteAllText(ConfigPath("pjlink-host.txt"), "192.168.10.21\n");
    TCPClient second = Connect();
    Equal("192.168.10.21", second.Host);
    Equal(true, first.IsDisposed);
    CrestronConsole.Command!("disconnect");
    File.Delete(ConfigPath("pjlink-host.txt"));
    CrestronConsole.Command!("connect");
    Equal(2, TCPClient.Created.Count);
    File.WriteAllText(ConfigPath("pjlink-host.txt"), "192.168.10.20\n");
    TCPClient third = Connect();
    Equal("192.168.10.20", third.Host);
    Equal(false, third.IsDisposed);
}

static void HostValidation()
{
    string path = ConfigPath("pjlink-host.txt");
    try
    {
        foreach (string valid in new[] { "10.0.0.1", "172.16.0.1", "172.31.255.254", "192.168.10.20" })
        {
            File.WriteAllText(path, valid + "\r\n");
            Equal(valid, SampleConfiguration.ReadHost());
        }
        foreach (string invalid in new[] { "", "localhost", "127.0.0.1", "8.8.8.8", "172.15.0.1",
                     "172.32.0.1", "192.169.0.1", "192.168.1", "192.168.1.1.2", "192.168.1.256",
                     "192.168.001.2", " 192.168.1.2", "192.168.1.2 ", "192.168.1.+2",
                     "192.168.1.-2", "192.168.1.2\n192.168.1.3", "192.168.1.é", "192.168..1" })
        {
            File.WriteAllText(path, invalid);
            Throws<ArgumentException>(() => SampleConfiguration.ReadHost());
        }
    }
    finally
    {
        File.WriteAllText(path, "192.168.10.20\n");
    }
}

static void HighBitAscii()
{
    Start();
    for (int value = 128; value <= 255; value++)
    {
        CrestronConsole.Command!("connect");
        TCPClient connection = TCPClient.Created.Last();
        connection.CompleteConnection();
        connection.Deliver(new[] { (byte)value });
        Equal(true, connection.IsDisposed);
        Equal(0, connection.Sent.Count);
    }
    TCPClient valid = Connect();
    CrestronConsole.Command!("query");
    valid.Deliver("%1POWR=1\r");
    Equal(false, valid.IsDisposed);
    Equal(true, CrestronConsole.Messages.Contains("PJLink power feedback = On."));
}

static void ConfigurationFiles()
{
    string host = ConfigPath("pjlink-host.txt");
    string password = ConfigPath("pjlink-password.txt");
    const string marker = "PrivateCredentialMarker";
    try
    {
        File.WriteAllText(host, new string('1', 65));
        Throws<InvalidOperationException>(() => SampleConfiguration.ReadHost());
        File.WriteAllText(host, "192.168.10.20\n");
        File.WriteAllText(password, marker + new string('x', 65));
        Throws<InvalidOperationException>(() => new ControlSystem());
        Equal(false, CrestronConsole.Messages.Any(message => message.Contains(marker, StringComparison.Ordinal)));
        Equal<Action<string>?>(null, CrestronConsole.Command);
        File.WriteAllText(password, "bad password");
        Throws<InvalidOperationException>(() => new ControlSystem());
        Equal(false, CrestronConsole.Messages.Any(message => message.Contains("bad password", StringComparison.Ordinal)));
        File.WriteAllText(password, new string('a', 32) + "\r\n");
        Start();
        Equal(new string('a', 32), SampleConfiguration.ReadPassword());
    }
    finally
    {
        File.Delete(password);
        File.WriteAllText(host, "192.168.10.20\n");
    }
}

static void InvariantCommands()
{
    var previous = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
        Start();
        TCPClient connection = Connect();
        CrestronConsole.Command!("DISCONNECT");
        Equal(true, connection.IsDisposed);
        CrestronConsole.Command!("CONNECT");
        Equal(2, TCPClient.Created.Count);
    }
    finally
    {
        System.Globalization.CultureInfo.CurrentCulture = previous;
    }
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, received {actual}.");
}
