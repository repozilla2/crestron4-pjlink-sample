using System;
using System.Globalization;
#if RMC3
using MD5 = Crestron.SimplSharp.Cryptography.MD5;
#else
using MD5 = System.Security.Cryptography.MD5;
#endif
using System.Text;

namespace KeithGariepy.CrestronSample
{
    public enum DisplayPowerState
    {
        Unknown = -1,
        Off = 0,
        On = 1,
        Cooling = 2,
        Warming = 3
    }

    public enum PjLinkCommand
    {
        QueryPower,
        PowerOn,
        PowerOff
    }

    public sealed class PjLinkCommandResult
    {
        internal PjLinkCommandResult(PjLinkCommand command, bool succeeded,
            string response, DisplayPowerState? powerState)
        {
            Command = command;
            Succeeded = succeeded;
            Response = response;
            PowerState = powerState;
        }

        public PjLinkCommand Command { get; private set; }
        public bool Succeeded { get; private set; }
        public string Response { get; private set; }
        public DisplayPowerState? PowerState { get; private set; }
    }

    /// <summary>
    /// PJLink Class 1 power commands with legacy no-auth/MD5 authentication.
    /// The host serializes calls, supplies a monotonic clock, and owns the socket.
    /// Events run synchronously. State/fault handlers must observe, without restarting
    /// the session. A fault requires the host to close the connection.
    /// </summary>
    public sealed class PjLinkSession
    {
        // Class 1 responses contain a seven-character prefix and up to 128 data bytes.
        private const int MaximumLineLength = 135;
        private readonly string _password;
        private readonly TimeSpan _responseTimeout;
        private readonly StringBuilder _receiveBuffer = new StringBuilder();
        private readonly object _eventSync = new object();
        private Action<string> _sendRequested;
        private Action _ready;
        private Action<DisplayPowerState> _powerStateChanged;
        private Action<PjLinkCommandResult> _commandCompleted;
        private Action<string> _faulted;
        private bool _active;
        private bool _awaitingGreeting;
        private string _authenticationPrefix = string.Empty;
        private PjLinkCommand? _pendingCommand;
        private TimeSpan? _deadline;

        public PjLinkSession() : this(string.Empty, null)
        {
        }

        public PjLinkSession(string password) : this(password, null)
        {
        }

        public PjLinkSession(TimeSpan responseTimeout) : this(string.Empty, responseTimeout)
        {
        }

        public PjLinkSession(string password, TimeSpan? responseTimeout)
        {
            if (password == null) throw new ArgumentNullException("password");
            if (password.Length > 32)
                throw new ArgumentException("PJLink passwords contain at most 32 ASCII alphanumeric characters.", "password");
            foreach (char character in password)
            {
                if (!((character >= 'a' && character <= 'z') ||
                      (character >= 'A' && character <= 'Z') ||
                      (character >= '0' && character <= '9')))
                    throw new ArgumentException("PJLink passwords use ASCII alphanumeric characters.", "password");
            }

            PowerState = DisplayPowerState.Unknown;
            _password = password;
            _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(30);
            if (_responseTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException("responseTimeout");
        }

        // Explicit accessors use the 3-Series-supported Monitor synchronization.
        // Every callback uses a snapshot taken under this lock and runs after release.
        public event Action<string> SendRequested
        {
            add { lock (_eventSync) { _sendRequested += value; } }
            remove { lock (_eventSync) { _sendRequested -= value; } }
        }

        public event Action Ready
        {
            add { lock (_eventSync) { _ready += value; } }
            remove { lock (_eventSync) { _ready -= value; } }
        }

        public event Action<DisplayPowerState> PowerStateChanged
        {
            add { lock (_eventSync) { _powerStateChanged += value; } }
            remove { lock (_eventSync) { _powerStateChanged -= value; } }
        }

        public event Action<PjLinkCommandResult> CommandCompleted
        {
            add { lock (_eventSync) { _commandCompleted += value; } }
            remove { lock (_eventSync) { _commandCompleted -= value; } }
        }

        public event Action<string> Faulted
        {
            add { lock (_eventSync) { _faulted += value; } }
            remove { lock (_eventSync) { _faulted -= value; } }
        }

        /// <summary>The greeting has been accepted and the session can send commands.</summary>
        public bool IsReady { get; private set; }
        public bool HasPendingCommand { get { return _pendingCommand.HasValue; } }
        public DisplayPowerState PowerState { get; private set; }

        public void Begin(TimeSpan now)
        {
            Disconnect();
            _active = true;
            _awaitingGreeting = true;
            _deadline = now + _responseTimeout;
        }

        public void Disconnect()
        {
            _active = false;
            _awaitingGreeting = false;
            IsReady = false;
            _pendingCommand = null;
            _deadline = null;
            _authenticationPrefix = string.Empty;
            _receiveBuffer.Length = 0;
            UpdatePowerState(DisplayPowerState.Unknown);
        }

        public void QueryPower(TimeSpan now)
        {
            SendCommand(PjLinkCommand.QueryPower, "?", now);
        }

        public void SetPower(bool on, TimeSpan now)
        {
            SendCommand(on ? PjLinkCommand.PowerOn : PjLinkCommand.PowerOff,
                on ? "1" : "0", now);
        }

        private void SendCommand(PjLinkCommand command, string parameter, TimeSpan now)
        {
            CheckTimeout(now);
            if (!IsReady)
                throw new InvalidOperationException("A valid PJLink greeting is required before sending a command.");
            if (HasPendingCommand)
                throw new InvalidOperationException("Wait for the outstanding PJLink command to complete.");
            Action<string> sendRequested;
            lock (_eventSync) { sendRequested = _sendRequested; }
            if (sendRequested == null)
                throw new InvalidOperationException("A transport must subscribe to SendRequested.");

            string wireText = _authenticationPrefix + "%1POWR " + parameter + "\r";
            _authenticationPrefix = string.Empty;
            _pendingCommand = command;
            _deadline = now + _responseTimeout;
            sendRequested(wireText);
        }

        public void Receive(string chunk, TimeSpan now)
        {
            if (chunk == null) throw new ArgumentNullException("chunk");
            CheckTimeout(now);
            string completedLine = null;
            foreach (char character in chunk)
            {
                if (!_active) return;
                if (!_awaitingGreeting && !HasPendingCommand && character != '\n')
                {
                    Fail("Unexpected data without an outstanding PJLink command.");
                    return;
                }
                if (completedLine != null && character != '\n')
                {
                    Fail("Unexpected data after a complete PJLink response.");
                    return;
                }
                if (character == '\r')
                {
                    completedLine = _receiveBuffer.ToString();
                    _receiveBuffer.Length = 0;
                }
                else if (character == '\n' && _receiveBuffer.Length == 0)
                {
                    // Accept CRLF devices while transmitting the specified CR terminator.
                    continue;
                }
                else if (character < 0x20 || character > 0x7e ||
                         _receiveBuffer.Length == MaximumLineLength)
                {
                    Fail("Invalid or oversized PJLink response frame.");
                    return;
                }
                else
                {
                    _receiveBuffer.Append(character);
                }
            }
            // Validate the entire chunk before callbacks can request another command.
            if (completedLine != null) HandleLine(completedLine);
        }

        public void CheckTimeout(TimeSpan now)
        {
            if (!_active || !_deadline.HasValue || now < _deadline.Value) return;
            Fail(_awaitingGreeting ? "Timed out waiting for the PJLink greeting."
                : "Timed out waiting for the PJLink command response.");
        }

        private void HandleLine(string line)
        {
            if (line == "PJLINK ERRA")
            {
                Fail("PJLink authentication was rejected.");
                return;
            }
            if (_awaitingGreeting)
            {
                HandleGreeting(line);
                return;
            }
            if (!_pendingCommand.HasValue ||
                !line.StartsWith("%1POWR=", StringComparison.OrdinalIgnoreCase))
            {
                Fail("Unexpected PJLink response.");
                return;
            }

            PjLinkCommand command = _pendingCommand.Value;
            string response = line.Substring(7);
            bool succeeded = false;
            DisplayPowerState? powerState = null;
            if (response == "ERR1" || response == "ERR2" || response == "ERR3" || response == "ERR4")
            {
                // A device error completes the transaction without asserting a state change.
            }
            else if (command == PjLinkCommand.QueryPower && response.Length == 1 &&
                     response[0] >= '0' && response[0] <= '3')
            {
                powerState = (DisplayPowerState)(response[0] - '0');
                succeeded = true;
            }
            else if (command != PjLinkCommand.QueryPower && response == "OK")
            {
                succeeded = true;
            }
            else
            {
                Fail("Invalid response for the outstanding PJLink command.");
                return;
            }

            _pendingCommand = null;
            _deadline = null;
            if (powerState.HasValue) UpdatePowerState(powerState.Value);
            Action<PjLinkCommandResult> completed;
            lock (_eventSync) { completed = _commandCompleted; }
            if (completed != null)
                completed(new PjLinkCommandResult(command, succeeded, response, powerState));
        }

        private void HandleGreeting(string line)
        {
            if (line == "PJLINK 0")
            {
                _authenticationPrefix = string.Empty;
            }
            else if (line.StartsWith("PJLINK 1 ", StringComparison.Ordinal) &&
                     line.Length == 17 && IsHexadecimal(line.Substring(9)))
            {
                if (_password.Length == 0)
                {
                    Fail("The display requires a PJLink password.");
                    return;
                }
                // PJLink v1.04 requires MD5 of the ASCII challenge followed by the password.
                using (MD5 md5 = MD5.Create())
                {
                    byte[] digest = md5.ComputeHash(Encoding.ASCII.GetBytes(line.Substring(9) + _password));
                    _authenticationPrefix = BitConverter.ToString(digest).Replace("-", "").ToLower(CultureInfo.InvariantCulture);
                }
            }
            else
            {
                Fail("Unsupported or malformed PJLink greeting.");
                return;
            }
            _awaitingGreeting = false;
            _deadline = null;
            IsReady = true;
            Action ready;
            lock (_eventSync) { ready = _ready; }
            if (ready != null) ready();
        }

        private static bool IsHexadecimal(string text)
        {
            foreach (char character in text)
            {
                if (!Uri.IsHexDigit(character)) return false;
            }
            return true;
        }

        private void UpdatePowerState(DisplayPowerState state)
        {
            if (PowerState == state) return;
            PowerState = state;
            Action<DisplayPowerState> changed;
            lock (_eventSync) { changed = _powerStateChanged; }
            if (changed != null) changed(state);
        }

        private void Fail(string message)
        {
            Disconnect();
            Action<string> faulted;
            lock (_eventSync) { faulted = _faulted; }
            if (faulted != null) faulted(message);
        }
    }
}
