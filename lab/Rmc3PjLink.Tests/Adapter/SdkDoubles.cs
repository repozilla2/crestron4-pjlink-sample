// These doubles exercise the adapter's own control flow. They do not emulate firmware.
using System.Text;

namespace Crestron.SimplSharp
{
    public enum eProgramStatusEventType { Stopping, Paused, Resumed }
    public enum ConsoleAccessLevelEnum { AccessOperator }

    public static class ErrorLog
    {
        public static void Error(string message, params object[] args) =>
            CrestronConsole.Messages.Add(string.Format(message, args));
    }

    public static class CrestronEnvironment
    {
        public static event Action<eProgramStatusEventType>? ProgramStatusEventHandler;
        public static void Raise(eProgramStatusEventType status) => ProgramStatusEventHandler?.Invoke(status);
        public static void Reset() => ProgramStatusEventHandler = null;
    }

    public static class CrestronConsole
    {
        public static Action<string>? Command { get; private set; }
        public static List<string> Messages { get; } = new List<string>();
        public static bool RejectRegistration { get; set; }
        public static int RemoveCalls { get; private set; }

        public static bool AddNewConsoleCommand(Action<string> callback, string name,
            string help, ConsoleAccessLevelEnum access)
        {
            if (RejectRegistration) return false;
            Command = callback;
            return true;
        }

        public static void PrintLine(string message, params object[] args) => Messages.Add(string.Format(message, args));
        public static void ConsoleCommandResponse(string message) => Messages.Add(message);
        public static bool RemoveConsoleCommand(string name)
        {
            RemoveCalls++;
            Command = null;
            return true;
        }
        public static void Reset()
        {
            Command = null;
            Messages.Clear();
            RejectRegistration = false;
            RemoveCalls = 0;
        }
    }

    public sealed class CTimer : IDisposable
    {
        private readonly Action<object?> callback;
        public static List<CTimer> Created { get; } = new List<CTimer>();
        public bool IsDisposed { get; private set; }

        public CTimer(Action<object?> callback, object? state, long due, long period)
        {
            this.callback = callback;
            Created.Add(this);
        }

        // A callback already dispatched by the SDK may arrive after disposal.
        public void DeliverCallback() => callback(null);
        public void Dispose() => IsDisposed = true;
    }
}

namespace Crestron.SimplSharp.CrestronIO
{
    public static class Directory
    {
        public static string TestApplicationDirectory { get; set; } = string.Empty;
        public static string GetApplicationDirectory() => TestApplicationDirectory;
    }
}

namespace Crestron.SimplSharpPro
{
    public abstract class CrestronControlSystem
    {
        public abstract void InitializeSystem();
    }
}

namespace Crestron.SimplSharpPro.CrestronThread
{
    public static class Thread
    {
        public static int MaxNumberOfUserThreads { get; set; }
    }
}

namespace Crestron.SimplSharp.CrestronSockets
{
    public enum SocketErrorCodes { SOCKET_OK, SOCKET_OPERATION_PENDING, SOCKET_ERROR }
    public enum SocketStatus { SOCKET_STATUS_CONNECTED, SOCKET_STATUS_DISCONNECTED }

    public sealed class TCPClient : IDisposable
    {
        private Action<TCPClient>? connected;
        private Action<TCPClient, int>? received;
        private Action<TCPClient, int>? sent;
        public static List<TCPClient> Created { get; } = new List<TCPClient>();
        public static Func<bool>? IsStateLockHeld { get; set; }
        public List<string> Sent { get; } = new List<string>();
        public bool IsDisposed { get; private set; }
        public bool DisposedWithStateLock { get; private set; }
        public bool ThrowOnSend { get; set; }
        public bool FailSendInline { get; set; }
        public bool EndReceiveInline { get; set; }
        public SocketStatus ClientStatus { get; private set; }
        public byte[] IncomingDataBuffer { get; } = new byte[4096];

        public string Host { get; }
        public int Port { get; }
        public TCPClient(string host, int port, int size)
        {
            Host = host;
            Port = port;
            Created.Add(this);
        }
        public SocketErrorCodes ConnectToServerAsync(Action<TCPClient> callback)
        {
            connected = callback;
            return SocketErrorCodes.SOCKET_OPERATION_PENDING;
        }
        public SocketErrorCodes ReceiveDataAsync(Action<TCPClient, int> callback)
        {
            received = callback;
            if (EndReceiveInline)
            {
                EndReceiveInline = false;
                callback(this, 0);
            }
            return SocketErrorCodes.SOCKET_OPERATION_PENDING;
        }
        public SocketErrorCodes SendDataAsync(byte[] bytes, int count, Action<TCPClient, int> callback)
        {
            if (ThrowOnSend) throw new IOException("Test send failure.");
            Sent.Add(Encoding.ASCII.GetString(bytes, 0, count));
            sent = callback;
            if (FailSendInline) callback(this, 0);
            return SocketErrorCodes.SOCKET_OPERATION_PENDING;
        }

        public void CompleteConnection()
        {
            ClientStatus = SocketStatus.SOCKET_STATUS_CONNECTED;
            connected!(this);
        }
        public void Deliver(string text) => Deliver(Encoding.ASCII.GetBytes(text));
        public void CompleteSend(int count) => sent!(this, count);
        public void Deliver(byte[] bytes)
        {
            Array.Copy(bytes, IncomingDataBuffer, bytes.Length);
            received!(this, bytes.Length);
        }
        public void Dispose()
        {
            IsDisposed = true;
            DisposedWithStateLock = IsStateLockHeld?.Invoke() ?? false;
        }
    }
}
