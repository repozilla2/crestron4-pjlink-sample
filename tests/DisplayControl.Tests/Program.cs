using KeithGariepy.CrestronSample;

var tests = new (string Name, Action Run)[]
{
    ("Fragmented greeting waits for CR", FragmentedGreeting),
    ("Official MD5 fixture and first-command prefix", AuthenticationFixture),
    ("Power queries decode all four states", PowerQueryStates),
    ("Set acknowledgments preserve confirmed feedback", SetPowerAcknowledgment),
    ("Fragmented responses and CRLF are framed", ResponseFraming),
    ("Trailing data cannot satisfy a callback-created command", TrailingData),
    ("Idle partial replies cannot complete a future query", IdlePartialQuery),
    ("Idle partial acknowledgments cannot complete a future write", IdlePartialAcknowledgment),
    ("One command can be outstanding", SingleOutstandingCommand),
    ("Device errors complete without invented feedback", DeviceErrors),
    ("Missing greeting and response time out", Timeouts),
    ("Late replies cannot complete timed-out commands", LateReply),
    ("Disconnect clears state and partial data", DisconnectAndRestart),
    ("Authentication failure invalidates session", AuthenticationFailure),
    ("Invalid greetings are rejected", InvalidGreetings),
    ("Malformed or unexpected responses are rejected", InvalidResponses),
    ("Oversized and non-ASCII frames are rejected", InvalidFrames),
    ("Configuration rejects invalid passwords and timeout", ConfigurationValidation)
};

int failures = 0;
foreach (var test in tests)
{
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
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} test groups passed.");
return failures == 0 ? 0 : 1;

static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

static void FragmentedGreeting()
{
    var rig = new TestRig();
    rig.Session.Begin(At(0));
    rig.Session.Receive("PJL", At(1));
    rig.Session.Receive("INK 0", At(2));
    Equal(false, rig.Session.IsReady);
    Equal(0, rig.ReadyCount);
    rig.Session.Receive("\r", At(3));
    Equal(true, rig.Session.IsReady);
    Equal(1, rig.ReadyCount);
    rig.Session.QueryPower(At(3));
    Equal("%1POWR ?\r", rig.Sent.Single());
}

static void AuthenticationFixture()
{
    var rig = new TestRig("JBMIAProjectorLink");
    rig.Session.Begin(At(0));
    rig.Session.Receive("PJLINK 1 498e", At(1));
    rig.Session.Receive("4a67\r", At(2));
    rig.Session.SetPower(true, At(2));
    Equal("5d8409bc1c3fa39749434aa3a5c38682%1POWR 1\r", rig.Sent[0]);
    rig.Session.Receive("%1POWR=OK\r", At(3));
    rig.Session.QueryPower(At(3));
    Equal("%1POWR ?\r", rig.Sent[1]);
}

static void PowerQueryStates()
{
    var rig = TestRig.Ready();
    for (int value = 0; value <= 3; value++)
    {
        rig.Session.QueryPower(At(value + 1));
        rig.Session.Receive("%1POWR=" + value + "\r", At(value + 1));
        Equal((DisplayPowerState)value, rig.Session.PowerState);
        Equal((DisplayPowerState)value, rig.Results.Last().PowerState!.Value);
        Equal(true, rig.Results.Last().Succeeded);
        Equal(PjLinkCommand.QueryPower, rig.Results.Last().Command);
    }
    Equal(4, rig.States.Count);
    rig.Session.QueryPower(At(5));
    rig.Session.Receive("%1powr=3\r", At(5));
    Equal(4, rig.States.Count);
}

static void SetPowerAcknowledgment()
{
    var rig = TestRig.Ready();
    rig.Session.QueryPower(At(1));
    rig.Session.Receive("%1POWR=0\r", At(1));
    rig.Session.SetPower(true, At(2));
    Equal("%1POWR 1\r", rig.Sent.Last());
    rig.Session.Receive("%1POWR=OK\r", At(2));
    Equal(DisplayPowerState.Off, rig.Session.PowerState);
    Equal(PjLinkCommand.PowerOn, rig.Results.Last().Command);
    Equal(true, rig.Results.Last().Succeeded);
    Equal<DisplayPowerState?>(null, rig.Results.Last().PowerState);
    rig.Session.SetPower(false, At(3));
    Equal("%1POWR 0\r", rig.Sent.Last());
    rig.Session.Receive("%1POWR=OK\r", At(3));
    Equal(PjLinkCommand.PowerOff, rig.Results.Last().Command);
    Equal(1, rig.States.Count);
}

static void ResponseFraming()
{
    var rig = TestRig.Ready();
    rig.Session.QueryPower(At(1));
    rig.Session.Receive("%1PO", At(1));
    rig.Session.Receive("WR=", At(1));
    Equal(0, rig.Results.Count);
    rig.Session.Receive("1\r\n", At(1));
    Equal(1, rig.Results.Count);
    rig.Session.QueryPower(At(2));
    rig.Session.Receive("%1powr=2\r", At(2));
    rig.Session.Receive("\n", At(2));
    Equal(2, rig.Results.Count);
    Equal(DisplayPowerState.Cooling, rig.Session.PowerState);
    Equal(0, rig.Faults.Count);
}

static void TrailingData()
{
    foreach (string trailing in new[] { "%1POWR=2\r", "%1PO" })
    {
        var rig = TestRig.Ready();
        rig.Session.QueryPower(At(1));
        rig.Session.CommandCompleted += _ => rig.Session.QueryPower(At(2));
        rig.Session.Receive("%1POWR=1\r" + trailing, At(2));
        Equal(1, rig.Sent.Count);
        Equal(0, rig.Results.Count);
        Equal(1, rig.Faults.Count);
        Equal(false, rig.Session.IsReady);
    }

    var greeting = new TestRig();
    greeting.Session.Ready += () => greeting.Session.QueryPower(At(1));
    greeting.Session.Begin(At(0));
    greeting.Session.Receive("PJLINK 0\r%1POWR=1\r", At(1));
    Equal(0, greeting.ReadyCount);
    Equal(0, greeting.Sent.Count);
    Equal(1, greeting.Faults.Count);
}

static void IdlePartialQuery()
{
    RejectIdleFragments("%1POWR=2\r", session => session.QueryPower(At(4)));
}

static void IdlePartialAcknowledgment()
{
    RejectIdleFragments("%1POWR=OK\r", session => session.SetPower(true, At(4)));
}

static void RejectIdleFragments(string response, Action<PjLinkSession> sendCommand)
{
    foreach (bool completeEarlierQuery in new[] { false, true })
    {
        for (int split = 1; split < response.Length; split++)
        {
            var rig = TestRig.Ready();
            if (completeEarlierQuery)
            {
                rig.Session.QueryPower(At(1));
                rig.Session.Receive("%1POWR=1\r", At(2));
            }

            int previousResults = rig.Results.Count;
            int previousSends = rig.Sent.Count;
            rig.Session.Receive(response.Substring(0, split), At(3));
            Equal(1, rig.Faults.Count);
            Equal(false, rig.Session.IsReady);
            Equal(false, rig.Session.HasPendingCommand);
            Equal(DisplayPowerState.Unknown, rig.Session.PowerState);

            Throws<InvalidOperationException>(() => sendCommand(rig.Session));
            rig.Session.Receive(response.Substring(split), At(5));
            Equal(previousResults, rig.Results.Count);
            Equal(previousSends, rig.Sent.Count);
            Equal(1, rig.Faults.Count);
        }
    }
}

static void SingleOutstandingCommand()
{
    var rig = new TestRig();
    Throws<InvalidOperationException>(() => rig.Session.QueryPower(At(0)));
    rig.Session.Begin(At(0));
    Throws<InvalidOperationException>(() => rig.Session.SetPower(true, At(0)));
    rig.Session.Receive("PJLINK 0\r", At(0));
    rig.Session.QueryPower(At(1));
    Throws<InvalidOperationException>(() => rig.Session.SetPower(true, At(1)));
    Equal(1, rig.Sent.Count);
    Equal(true, rig.Session.HasPendingCommand);
    rig.Session.Receive("%1POWR=1\r", At(1));
    Equal(false, rig.Session.HasPendingCommand);
    rig.Session.SetPower(false, At(2));
    Equal(2, rig.Sent.Count);
}

static void DeviceErrors()
{
    foreach (string response in new[] { "ERR1", "ERR2", "ERR3", "ERR4" })
    {
        var rig = TestRig.Ready();
        rig.Session.SetPower(true, At(1));
        rig.Session.Receive("%1POWR=" + response + "\r", At(1));
        Equal(false, rig.Results.Single().Succeeded);
        Equal(response, rig.Results.Single().Response);
        Equal(true, rig.Session.IsReady);
        Equal(false, rig.Session.HasPendingCommand);
        Equal(DisplayPowerState.Unknown, rig.Session.PowerState);
        Equal(0, rig.Faults.Count);
        rig.Session.QueryPower(At(2));
        rig.Session.Receive("%1POWR=" + response + "\r", At(2));
        Equal(false, rig.Results.Last().Succeeded);
    }
}

static void Timeouts()
{
    var greeting = new TestRig(responseTimeout: At(5));
    greeting.Session.Begin(At(10));
    greeting.Session.CheckTimeout(At(14.999));
    Equal(0, greeting.Faults.Count);
    greeting.Session.CheckTimeout(At(15));
    Equal(1, greeting.Faults.Count);
    Equal(false, greeting.Session.IsReady);

    var command = TestRig.Ready();
    command.Session.QueryPower(At(1));
    command.Session.CheckTimeout(At(30.999));
    Equal(0, command.Faults.Count);
    command.Session.CheckTimeout(At(31));
    Equal(1, command.Faults.Count);
    Equal(false, command.Session.HasPendingCommand);
    command.Session.CheckTimeout(At(99));
    Equal(1, command.Faults.Count);
}

static void LateReply()
{
    var rig = TestRig.Ready();
    rig.Session.QueryPower(At(1));
    rig.Session.Receive("%1POWR=1\r", At(31));
    Equal(1, rig.Faults.Count);
    Equal(0, rig.Results.Count);
    Equal(DisplayPowerState.Unknown, rig.Session.PowerState);
    rig.Session.Receive("%1POWR=1\r", At(32));
    Throws<InvalidOperationException>(() => rig.Session.QueryPower(At(32)));
    Equal(1, rig.Faults.Count);
}

static void DisconnectAndRestart()
{
    var rig = TestRig.Ready();
    rig.Session.QueryPower(At(1));
    rig.Session.Receive("%1POWR=1\r", At(1));
    rig.Session.QueryPower(At(2));
    rig.Session.Receive("%1PO", At(2));
    rig.Session.Disconnect();
    Equal(DisplayPowerState.Unknown, rig.Session.PowerState);
    Equal(false, rig.Session.HasPendingCommand);
    rig.Session.Receive("WR=0\r", At(3));
    Equal(1, rig.Results.Count);
    rig.Session.Begin(At(4));
    rig.Session.Receive("PJLINK 0\r", At(4));
    rig.Session.QueryPower(At(4));
    rig.Session.Receive("%1POWR=0\r", At(4));
    Equal(DisplayPowerState.Off, rig.Session.PowerState);
    Equal(0, rig.Faults.Count);
}

static void AuthenticationFailure()
{
    var rig = new TestRig("WrongPassword");
    rig.Session.Begin(At(0));
    rig.Session.Receive("PJLINK 1 498e4a67\r", At(0));
    rig.Session.QueryPower(At(1));
    rig.Session.Receive("PJLINK ERRA\r", At(2));
    Equal(false, rig.Session.IsReady);
    Equal(false, rig.Session.HasPendingCommand);
    Equal(1, rig.Faults.Count);
    Equal(0, rig.Results.Count);
    Equal(false, rig.Faults[0].Contains("WrongPassword", StringComparison.Ordinal));
}

static void InvalidGreetings()
{
    foreach (string greeting in new[]
    {
        "PJLINK 2", "PJLINK 1 1234567", "PJLINK 1 123456789", "PJLINK 1 1234567z",
        "PJLINK 1 12345678 extra", "PJLINK 0 extra", "WELCOME", ""
    })
    {
        var rig = new TestRig("Password");
        rig.Session.Begin(At(0));
        rig.Session.Receive(greeting + "\r", At(1));
        Equal(1, rig.Faults.Count);
        Equal(false, rig.Session.IsReady);
        Equal(0, rig.ReadyCount);
    }
    var missingPassword = new TestRig();
    missingPassword.Session.Begin(At(0));
    missingPassword.Session.Receive("PJLINK 1 12345678\r", At(1));
    Equal(1, missingPassword.Faults.Count);
}

static void InvalidResponses()
{
    foreach (string response in new[] { "%1INPT=11", "%2POWR=1", "%1POWR=OK", "%1POWR=4", "%1POWR=ERR9", "" })
    {
        var rig = TestRig.Ready();
        rig.Session.QueryPower(At(1));
        rig.Session.Receive(response + "\r", At(2));
        Equal(1, rig.Faults.Count);
        Equal(0, rig.Results.Count);
        Equal(false, rig.Session.IsReady);
    }
    var set = TestRig.Ready();
    set.Session.SetPower(true, At(1));
    set.Session.Receive("%1POWR=1\r", At(2));
    Equal(1, set.Faults.Count);
    var unsolicited = TestRig.Ready();
    unsolicited.Session.Receive("%1POWR=1\r", At(1));
    Equal(1, unsolicited.Faults.Count);
}

static void InvalidFrames()
{
    foreach (string data in new[] { new string('x', 136), "PJL\u0000INK", "PJLéINK" })
    {
        var rig = new TestRig();
        rig.Session.Begin(At(0));
        rig.Session.Receive(data, At(1));
        Equal(1, rig.Faults.Count);
        Equal(false, rig.Session.IsReady);
    }
    var boundary = new TestRig();
    boundary.Session.Begin(At(0));
    boundary.Session.Receive(new string('x', 135), At(1));
    Equal(0, boundary.Faults.Count);
    boundary.Session.Receive("x", At(1));
    Equal(1, boundary.Faults.Count);
}

static void ConfigurationValidation()
{
    Throws<ArgumentException>(() => new PjLinkSession(new string('a', 33)));
    Throws<ArgumentException>(() => new PjLinkSession("space password"));
    Throws<ArgumentException>(() => new PjLinkSession("nonASCIIé"));
    Throws<ArgumentOutOfRangeException>(() => new PjLinkSession(responseTimeout: TimeSpan.Zero));
    Throws<ArgumentOutOfRangeException>(() => new PjLinkSession(responseTimeout: At(-1)));
    var session = new PjLinkSession();
    session.Begin(At(0));
    session.Receive("PJLINK 0\r", At(0));
    Throws<InvalidOperationException>(() => session.QueryPower(At(1)));
    Equal(false, session.HasPendingCommand);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, received {actual}.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
}

internal sealed class TestRig
{
    public TestRig(string password = "", TimeSpan? responseTimeout = null)
    {
        Session = new PjLinkSession(password, responseTimeout);
        Session.SendRequested += Sent.Add;
        Session.CommandCompleted += Results.Add;
        Session.PowerStateChanged += States.Add;
        Session.Faulted += Faults.Add;
        Session.Ready += () => ReadyCount++;
    }

    public PjLinkSession Session { get; }
    public List<string> Sent { get; } = new List<string>();
    public List<PjLinkCommandResult> Results { get; } = new List<PjLinkCommandResult>();
    public List<DisplayPowerState> States { get; } = new List<DisplayPowerState>();
    public List<string> Faults { get; } = new List<string>();
    public int ReadyCount { get; private set; }

    public static TestRig Ready()
    {
        var rig = new TestRig();
        rig.Session.Begin(TimeSpan.Zero);
        rig.Session.Receive("PJLINK 0\r", TimeSpan.Zero);
        return rig;
    }
}
