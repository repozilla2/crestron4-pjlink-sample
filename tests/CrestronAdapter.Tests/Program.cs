using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;
using KeithGariepy.CrestronSample;
using System.Reflection;

// Compile the production adapter against SDK doubles to test its lifecycle decisions.
// Processor execution and native SDK callback behavior require separate hardware tests.
string directory = Path.Combine(Path.GetTempPath(), "crestron-adapter-tests-" + Guid.NewGuid().ToString("N"));
System.IO.Directory.CreateDirectory(directory);
Crestron.SimplSharp.CrestronIO.Directory.TestApplicationDirectory = directory;

var tests = new (string Name, Action Run)[]
{
    ("Pause closes resources and suppresses commands and callbacks", Pause),
    ("Resume requires an explicit new connection", Resume),
    ("Stop while paused releases registrations and stays stopped", StopWhilePaused),
    ("Late callbacks cannot replace the current connection", StaleCallbacks),
    ("Inline callbacks defer disposal until the outer lock exits", InlineCallbacks),
    ("Failed registration preserves another console command", RegistrationOwnership),
    ("Send failure and invalid ASCII close the connection", TransportFailures),
    ("Runtime password file supplies authentication without credential logs", RuntimePassword)
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

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, received {actual}.");
}
