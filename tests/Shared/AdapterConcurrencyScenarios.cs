using System.Reflection;
using System.Threading;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;
using KeithGariepy.CrestronSample;

// The same schedules exercise both adapters against desktop SDK doubles.
// These are bounded lock-contention tests, not a firmware scheduler model.
internal static class AdapterConcurrencyScenarios
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    internal static void ReceiveBeforeStop()
    {
        ControlSystem program = Start();
        TCPClient connection = Connect();
        Action<string> console = CrestronConsole.Command!;
        console("query");
        Overlap(program, "PJLink power feedback = On.",
            () => connection.Deliver("%1POWR=1\r"),
            () => CrestronEnvironment.Raise(eProgramStatusEventType.Stopping));

        Require(CompletionCount() == 1, "The winning response must complete exactly once.");
        Require(Session(program).PowerState == DisplayPowerState.Unknown, "Stop must clear observed state.");
        Require(CrestronConsole.Command == null, "Stop must remove the owned console command.");
        Require(CTimer.Created.Single().IsDisposed, "Stop must dispose the timer.");
        RequireClosedOnce(connection);

        int messages = CrestronConsole.Messages.Count;
        console("connect");
        connection.Deliver("%1POWR=0\r");
        connection.CompleteSend(0);
        CTimer.Created.Single().DeliverCallback();
        Require(TCPClient.Created.Count == 1 && connection.Sent.Count == 1,
            "No work may start after stop.");
        Require(CrestronConsole.Messages.Count == messages, "Late callbacks after stop must be ignored.");
        RequireClosedOnce(connection);
    }

    internal static void StopBeforeReceive()
    {
        ControlSystem program = Start();
        TCPClient connection = Connect();
        SeedOff(connection);
        CrestronConsole.Command!("query");
        Overlap(program, "PJLink power feedback = Unknown.",
            () => CrestronEnvironment.Raise(eProgramStatusEventType.Stopping),
            () => connection.Deliver("%1POWR=1\r"));

        Require(CompletionCount() == 1, "The receive queued behind stop must not complete.");
        Require(!CrestronConsole.Messages.Contains("PJLink power feedback = On."),
            "A queued receive must not publish state after stop.");
        Require(!Session(program).IsReady && !Session(program).HasPendingCommand,
            "Stop must clear readiness and the pending transaction.");
        RequireClosedOnce(connection);
    }

    internal static void TimeoutBeforeResponse()
    {
        long milliseconds = 0;
        ControlSystem program = Start(() => TimeSpan.FromMilliseconds(Volatile.Read(ref milliseconds)));
        TCPClient connection = Connect();
        CrestronConsole.Command!("query");
        Interlocked.Exchange(ref milliseconds, 30000);
        Overlap(program, "PJLink connection closed. Timed out waiting for the PJLink command response.",
            () => CTimer.Created.Single().DeliverCallback(),
            () => connection.Deliver("%1POWR=1\r"));

        Require(CompletionCount() == 0, "A response queued behind timeout must not complete.");
        Require(Session(program).PowerState == DisplayPowerState.Unknown, "Timeout must clear state.");
        RequireClosedOnce(connection);
        TCPClient fresh = Connect();
        SeedOff(fresh);
        connection.CompleteSend(0);
        Require(!fresh.IsDisposed && Session(program).PowerState == DisplayPowerState.Off,
            "An obsolete send callback must not fault a fresh connection.");
    }

    internal static void ResponseBeforeTimeout()
    {
        long milliseconds = 0;
        ControlSystem program = Start(() => TimeSpan.FromMilliseconds(Volatile.Read(ref milliseconds)));
        TCPClient connection = Connect();
        CrestronConsole.Command!("query");
        Interlocked.Exchange(ref milliseconds, 29999);
        Overlap(program, "PJLink power feedback = On.",
            () => connection.Deliver("%1POWR=1\r"),
            () =>
            {
                Interlocked.Exchange(ref milliseconds, 30000);
                CTimer.Created.Single().DeliverCallback();
            });

        Require(CompletionCount() == 1, "The pre-deadline response must complete exactly once.");
        Require(Session(program).IsReady && !Session(program).HasPendingCommand,
            "The subsequent timer must see a completed transaction.");
        Require(!connection.IsDisposed && connection.DisposeCalls == 0,
            "A completed transaction must not be timed out by a queued timer.");
        Require(!CrestronConsole.Messages.Any(message => message.Contains("Timed out", StringComparison.Ordinal)),
            "The queued timer must not report a false timeout.");
    }

    internal static void DisconnectBeforeSendCompletion()
    {
        ControlSystem program = Start();
        TCPClient connection = Connect();
        SeedOff(connection);
        CrestronConsole.Command!("on");
        Overlap(program, "PJLink power feedback = Unknown.",
            () => CrestronConsole.Command!("disconnect"),
            () => connection.CompleteSend(0));

        Require(CompletionCount() == 1, "Disconnect must not complete the pending write.");
        RequireClosedOnce(connection);
        Require(!CrestronConsole.Messages.Any(message => message.Contains("not sent completely", StringComparison.Ordinal)),
            "An obsolete send callback must not fault the disconnected session.");
        TCPClient fresh = Connect();
        connection.CompleteSend(0);
        SeedOff(fresh);
        Require(!fresh.IsDisposed, "The old send callback must not close a replacement connection.");
    }

    internal static void SendCompletionBeforeDisconnect()
    {
        ControlSystem program = Start();
        TCPClient connection = Connect();
        CrestronConsole.Command!("on");
        Overlap(program, "PJLink connection closed. The command was not sent completely. Its outcome is unknown.",
            () => connection.CompleteSend(0),
            () => CrestronConsole.Command!("disconnect"));

        Require(CompletionCount() == 0, "A failed send must not complete the write successfully.");
        Require(!Session(program).IsReady && !Session(program).HasPendingCommand,
            "The failed send must clear transaction state.");
        RequireClosedOnce(connection);
        Require(connection.Sent.Count == 1, "An uncertain write must not be replayed.");
        TCPClient fresh = Connect();
        SeedOff(fresh);
        Require(fresh.Sent.Count == 1, "Recovery must only send the explicitly requested query.");
    }

    private static ControlSystem Start(Func<TimeSpan>? clock = null)
    {
        File.WriteAllText(Path.Combine(
            Crestron.SimplSharp.CrestronIO.Directory.TestApplicationDirectory, "pjlink-host.txt"), "192.168.10.20\n");
        var program = new ControlSystem(clock);
        program.InitializeSystem();
        object gate = typeof(ControlSystem).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(program)!;
        TCPClient.IsStateLockHeld = () => Monitor.IsEntered(gate);
        return program;
    }

    private static PjLinkSession Session(ControlSystem program) =>
        (PjLinkSession)typeof(ControlSystem).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(program)!;

    private static TCPClient Connect()
    {
        CrestronConsole.Command!("connect");
        TCPClient connection = TCPClient.Created.Last();
        connection.CompleteConnection();
        connection.Deliver("PJLINK 0\r");
        return connection;
    }

    private static void SeedOff(TCPClient connection)
    {
        CrestronConsole.Command!("query");
        connection.Deliver("%1POWR=0\r");
    }

    private static int CompletionCount() => CrestronConsole.Messages.Count(message =>
        message.StartsWith("PJLink QueryPower returned", StringComparison.Ordinal) ||
        message.StartsWith("PJLink PowerOn returned", StringComparison.Ordinal) ||
        message.StartsWith("PJLink PowerOff returned", StringComparison.Ordinal));

    private static void RequireClosedOnce(TCPClient connection)
    {
        Require(connection.IsDisposed && connection.DisposeCalls == 1, "A detached socket must be disposed exactly once.");
        Require(!connection.DisposedWithStateLock, "Socket disposal must run outside the state lock.");
    }

    private static void Overlap(ControlSystem program, string holdMessage, Action owner, Action contender)
    {
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var contenderStarted = new ManualResetEventSlim();
        Exception? ownerError = null;
        Exception? contenderError = null;
        int intercepted = 0;
        CrestronConsole.BeforePrintLine = message =>
        {
            if (message != holdMessage || Interlocked.Exchange(ref intercepted, 1) != 0) return;
            Require(TCPClient.IsStateLockHeld!(), "The owner must hold the production state lock.");
            held.Set();
            Require(release.Wait(Budget), "The owner was not released within the test budget.");
        };

        var first = new Thread(() =>
        {
            try { owner(); }
            catch (Exception exception) { ownerError = exception; }
        }) { IsBackground = true };
        var second = new Thread(() =>
        {
            contenderStarted.Set();
            try { contender(); }
            catch (Exception exception) { contenderError = exception; }
        }) { IsBackground = true };
        bool secondStarted = false;
        bool ownerJoined;
        bool contenderJoined;
        try
        {
            first.Start();
            Require(held.Wait(Budget), "The owner did not reach the selected callback boundary.");
            second.Start();
            secondStarted = true;
            Require(contenderStarted.Wait(Budget), "The contender did not start.");
            Require(SpinWait.SpinUntil(() =>
                (second.ThreadState & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) != 0, Budget),
                "The contender did not reach lock contention within the test budget.");
            Require((second.ThreadState & ThreadState.WaitSleepJoin) != 0,
                "The contender completed while the owner still held the state lock.");
        }
        finally
        {
            release.Set();
            ownerJoined = first.Join(Budget);
            contenderJoined = !secondStarted || second.Join(Budget);
            CrestronConsole.BeforePrintLine = null;
        }
        Require(ownerJoined && contenderJoined, "Concurrent callbacks did not finish within the test budget.");
        Require(ownerError == null && contenderError == null,
            "A concurrent callback escaped with an exception: " + (ownerError ?? contenderError)?.GetType().Name);
        // Keep the inspected program alive until both callbacks have returned.
        GC.KeepAlive(program);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
