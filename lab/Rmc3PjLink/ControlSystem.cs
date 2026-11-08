using System;
using System.Globalization;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronSockets;
using Crestron.SimplSharpPro;
#if RMC3
using Stopwatch = Crestron.SimplSharp.Stopwatch;
#else
using Stopwatch = System.Diagnostics.Stopwatch;
#endif

namespace KeithGariepy.CrestronSample
{
    public sealed class ControlSystem : CrestronControlSystem
    {
        private const string ConsoleCommand = "pjlink";
        private readonly object gate = new object();
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Func<TimeSpan> now;
        private readonly PjLinkSession session;
        private readonly bool consoleCommandRegistered;
        private TCPClient client;
        private CTimer timeoutTimer;
        private int serializationDepth;
        private bool closeRequested;
        private bool paused;
        private bool stopped;

        public ControlSystem() : this(null)
        {
        }

        // The adapter tests supply a manual clock to drive timeouts without waiting.
        internal ControlSystem(Func<TimeSpan> monotonicClock)
        {
            now = monotonicClock ?? (() => clock.Elapsed);
            Crestron.SimplSharpPro.CrestronThread.Thread.MaxNumberOfUserThreads = 20;
            try
            {
                session = new PjLinkSession(
                    SampleConfiguration.ReadPassword(),
                    TimeSpan.FromSeconds(SampleConfiguration.ResponseTimeoutSeconds));
            }
            catch (Exception exception)
            {
                ErrorLog.Error("PJLink configuration failed with {0}.", exception.GetType().Name);
                throw new InvalidOperationException("PJLink configuration could not be loaded.");
            }

            session.SendRequested += Send;
            session.Faulted += FailConnection;
            session.Ready += () => CrestronConsole.PrintLine("PJLink ready. Use pjlink query to read power feedback.");
            session.PowerStateChanged += state => CrestronConsole.PrintLine("PJLink power feedback = {0}.", state);
            session.CommandCompleted += result => CrestronConsole.PrintLine(
                "PJLink {0} returned {1}.", result.Command, result.Response);

            CrestronEnvironment.ProgramStatusEventHandler += OnProgramStatus;
            consoleCommandRegistered = CrestronConsole.AddNewConsoleCommand(
                OnConsoleCommand,
                ConsoleCommand,
                "PJLink sample. connect | disconnect | status | query | on | off",
                ConsoleAccessLevelEnum.AccessOperator);
            if (!consoleCommandRegistered)
            {
                ErrorLog.Error("Could not register the PJLink sample console command.");
            }
        }

        public override void InitializeSystem()
        {
            lock (gate)
            {
                if (stopped || paused)
                    return;

                clock.Start();
                if (timeoutTimer == null)
                    timeoutTimer = new CTimer(OnTimer, null, 250, 250);
            }

            CrestronConsole.PrintLine("RMC3 PJLink sample 0.1 loaded.");
        }

        private void OnConsoleCommand(string arguments)
        {
            RunSerialized(() =>
            {
                string operation = (arguments ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture);
                switch (operation)
                {
                    case "connect":
                        Connect();
                        break;
                    case "disconnect":
                        closeRequested = true;
                        CrestronConsole.ConsoleCommandResponse("Disconnect requested.");
                        break;
                    case "status":
                        CrestronConsole.ConsoleCommandResponse(
                            string.Format("Socket {0}. Ready {1}. Pending {2}. Power {3}.",
                                client == null ? "disconnected" : client.ClientStatus.ToString(),
                                session.IsReady, session.HasPendingCommand, session.PowerState));
                        break;
                    case "query":
                    case "on":
                    case "off":
                        if (!session.IsReady || session.HasPendingCommand)
                        {
                            CrestronConsole.ConsoleCommandResponse(
                                "The session must be ready with no command pending. Use pjlink status.");
                            break;
                        }

                        if (operation == "query")
                            session.QueryPower(now());
                        else
                            session.SetPower(operation == "on", now());
                        break;
                    default:
                        CrestronConsole.ConsoleCommandResponse(
                            "Use pjlink connect, disconnect, status, query, on, or off.");
                        break;
                }
            });
        }

        private void Connect()
        {
            if (client != null)
            {
                CrestronConsole.ConsoleCommandResponse("A connection already exists. Disconnect before reconnecting.");
                return;
            }

            client = new TCPClient(SampleConfiguration.ReadHost(), SampleConfiguration.DisplayPort, 4096);
            session.Begin(now());
            SocketErrorCodes result = client.ConnectToServerAsync(OnConnected);
            if (!OperationStarted(result))
                FailConnection("Connection could not start. Socket result " + result + ".");
        }

        private void OnConnected(TCPClient connection)
        {
            RunSerialized(() =>
            {
                if (!ReferenceEquals(client, connection))
                    return;

                if (connection.ClientStatus != SocketStatus.SOCKET_STATUS_CONNECTED)
                {
                    FailConnection("Connection failed. Socket status " + connection.ClientStatus + ".");
                    return;
                }

                ReceiveNext(connection);
            });
        }

        private void ReceiveNext(TCPClient connection)
        {
            SocketErrorCodes result = connection.ReceiveDataAsync(OnReceived);
            if (!OperationStarted(result))
                FailConnection("Receive could not start. Socket result " + result + ".");
        }

        private void OnReceived(TCPClient connection, int byteCount)
        {
            RunSerialized(() =>
            {
                if (!ReferenceEquals(client, connection))
                    return;

                if (byteCount <= 0)
                {
                    FailConnection("The display closed the connection.");
                    return;
                }

                // Convert the SDK buffer before scheduling another receive that can reuse it.
                string chunk = DecodeAscii(connection.IncomingDataBuffer, byteCount);
                session.Receive(chunk, now());
                if (!closeRequested)
                    ReceiveNext(connection);
            });
        }

        private void Send(string command)
        {
            TCPClient connection = client;
            if (connection == null)
                throw new InvalidOperationException("No display connection is available.");

            byte[] bytes = EncodeAscii(command);
            SocketErrorCodes result = connection.SendDataAsync(bytes, bytes.Length, (sender, sentCount) =>
            {
                RunSerialized(() =>
                {
                    if (ReferenceEquals(client, sender) && sentCount != bytes.Length)
                        FailConnection("The command was not sent completely. Its outcome is unknown.");
                });
            });

            // Commands can contain authentication material. Never log the outgoing bytes.
            if (!OperationStarted(result))
                FailConnection("Send could not start. Socket result " + result + ".");
        }

        private static string DecodeAscii(byte[] bytes, int count)
        {
            char[] characters = new char[count];
            for (int index = 0; index < count; index++)
            {
                if (bytes[index] > 0x7f)
                    throw new ArgumentException("PJLink data must contain only ASCII bytes.");
                characters[index] = (char)bytes[index];
            }
            return new string(characters);
        }

        private static byte[] EncodeAscii(string text)
        {
            byte[] bytes = new byte[text.Length];
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] > 0x7f)
                    throw new ArgumentException("PJLink commands must contain only ASCII characters.");
                bytes[index] = (byte)text[index];
            }
            return bytes;
        }

        private void OnTimer(object state)
        {
            RunSerialized(() => session.CheckTimeout(now()));
        }

        private void FailConnection(string reason)
        {
            closeRequested = true;
            CrestronConsole.PrintLine("PJLink connection closed. {0}", reason);
        }

        private void RunSerialized(Action action)
        {
            TCPClient connectionToDispose = null;
            lock (gate)
            {
                if (stopped || paused || closeRequested)
                    return;

                serializationDepth++;
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    // Avoid logging exception data that could include protocol or credential text.
                    FailConnection("Transport callback failed with " + exception.GetType().Name + ".");
                }
                finally
                {
                    serializationDepth--;
                    if (closeRequested && serializationDepth == 0)
                    {
                        closeRequested = false;
                        connectionToDispose = client;
                        client = null;
                        session.Disconnect();
                    }
                }
            }

            // Inline SDK callbacks can re-enter this method. Only the outermost call disposes.
            DisposeClient(connectionToDispose);
        }

        private static bool OperationStarted(SocketErrorCodes result)
        {
            return result == SocketErrorCodes.SOCKET_OK || result == SocketErrorCodes.SOCKET_OPERATION_PENDING;
        }

        private static void DisposeClient(TCPClient connection)
        {
            if (connection == null)
                return;

            try
            {
                connection.Dispose();
            }
            catch (Exception exception)
            {
                ErrorLog.Error("PJLink socket cleanup failed with {0}.", exception.GetType().Name);
            }
        }

        private void OnProgramStatus(eProgramStatusEventType status)
        {
            if (status == eProgramStatusEventType.Resumed)
            {
                lock (gate)
                {
                    if (stopped || !paused)
                        return;

                    paused = false;
                    clock.Start();
                    if (timeoutTimer == null)
                        timeoutTimer = new CTimer(OnTimer, null, 250, 250);
                }

                CrestronConsole.PrintLine("PJLink sample resumed. Use pjlink connect to start a fresh session.");
                return;
            }

            if (status != eProgramStatusEventType.Stopping && status != eProgramStatusEventType.Paused)
                return;

            bool isStopping = status == eProgramStatusEventType.Stopping;
            TCPClient connectionToDispose;
            CTimer timerToDispose;
            lock (gate)
            {
                if (stopped)
                    return;

                stopped = isStopping;
                paused = true;
                closeRequested = false;
                connectionToDispose = client;
                client = null;
                timerToDispose = timeoutTimer;
                timeoutTimer = null;
                session.Disconnect();
            }

            try
            {
                if (timerToDispose != null) timerToDispose.Dispose();
            }
            catch (Exception exception)
            {
                ErrorLog.Error("PJLink timer cleanup failed with {0}.", exception.GetType().Name);
            }

            DisposeClient(connectionToDispose);
            if (isStopping)
            {
                CrestronEnvironment.ProgramStatusEventHandler -= OnProgramStatus;
                if (consoleCommandRegistered)
                    CrestronConsole.RemoveConsoleCommand(ConsoleCommand);
            }
            else
            {
                CrestronConsole.PrintLine("PJLink sample paused. The display connection is closed.");
            }
        }
    }
}
