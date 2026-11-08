using KeithGariepy.CrestronSample;

// Each callback waits for another thread to edit the same event subscription.
// Holding the subscription lock during invocation makes this check fail.
var tests = new (string Name, Action Run)[]
{
    ("SendRequested", () => Verify<Action<string>>(
        callback => _ => callback(),
        (session, handler) => session.SendRequested += handler,
        (session, handler) => session.SendRequested -= handler,
        session => { MakeReady(session); session.QueryPower(TimeSpan.Zero); })),
    ("Ready", () => Verify<Action>(
        callback => callback,
        (session, handler) => session.Ready += handler,
        (session, handler) => session.Ready -= handler,
        MakeReady)),
    ("PowerStateChanged", () => Verify<Action<DisplayPowerState>>(
        callback => _ => callback(),
        (session, handler) => session.PowerStateChanged += handler,
        (session, handler) => session.PowerStateChanged -= handler,
        CompleteQuery)),
    ("CommandCompleted", () => Verify<Action<PjLinkCommandResult>>(
        callback => _ => callback(),
        (session, handler) => session.CommandCompleted += handler,
        (session, handler) => session.CommandCompleted -= handler,
        CompleteQuery)),
    ("Faulted", () => Verify<Action<string>>(
        callback => _ => callback(),
        (session, handler) => session.Faulted += handler,
        (session, handler) => session.Faulted -= handler,
        session => { session.Begin(TimeSpan.Zero); session.Receive("invalid\r", TimeSpan.Zero); }))
};

int failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine("PASS " + test.Name + " subscription semantics and unlocked callback");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine("FAIL " + test.Name + " - " + error.Message);
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} event test groups passed.");
return failures == 0 ? 0 : 1;

static void Verify<THandler>(Func<Action, THandler> wrap,
    Action<PjLinkSession, THandler> add, Action<PjLinkSession, THandler> remove,
    Action<PjLinkSession> trigger) where THandler : Delegate
{
    var session = new PjLinkSession();
    session.SendRequested += _ => { };
    int callbackCount = 0;
    int removedCount = 0;
    THandler removed = wrap(() => removedCount++);
    THandler callback = wrap(() =>
    {
        callbackCount++;
        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            try
            {
                add(session, removed);
                remove(session, removed);
            }
            catch (Exception error)
            {
                workerError = error;
            }
        }) { IsBackground = true };
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("Callback held the subscription lock.");
        if (workerError != null) throw workerError;
    });

    add(session, callback);
    add(session, callback);
    remove(session, callback);
    add(session, removed);
    remove(session, removed);
    trigger(session);
    if (callbackCount != 1 || removedCount != 0)
        throw new InvalidOperationException("Duplicate subscription or removal changed event semantics.");

    remove(session, callback);
    trigger(session);
    if (callbackCount != 1 || removedCount != 0)
        throw new InvalidOperationException("An unsubscribed callback was invoked.");
}

static void MakeReady(PjLinkSession session)
{
    session.Begin(TimeSpan.Zero);
    session.Receive("PJLINK 0\r", TimeSpan.Zero);
}

static void CompleteQuery(PjLinkSession session)
{
    MakeReady(session);
    session.QueryPower(TimeSpan.Zero);
    session.Receive("%1POWR=0\r", TimeSpan.Zero);
}
