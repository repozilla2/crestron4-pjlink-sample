using System;
using System.Security.Cryptography;
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

        public PjLinkCommand Command { get; }
        public bool Succeeded { get; }
        public string Response { get; }
        public DisplayPowerState? PowerState { get; }
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
        private bool _active;
        private bool _awaitingGreeting;
        private string _authenticationPrefix = string.Empty;
        private PjLinkCommand? _pendingCommand;
        private TimeSpan? _deadline;

        public PjLinkSession(string password = "", TimeSpan? responseTimeout = null)
        {
            if (password == null) throw new ArgumentNullException(nameof(password));
            if (password.Length > 32)
                throw new ArgumentException("PJLink passwords contain at most 32 ASCII alphanumeric characters.", nameof(password));
            foreach (char character in password)
            {
                if (!((character >= 'a' && character <= 'z') ||
                      (character >= 'A' && character <= 'Z') ||
                      (character >= '0' && character <= '9')))
                    throw new ArgumentException("PJLink passwords use ASCII alphanumeric characters.", nameof(password));
            }

            _password = password;
            _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(30);
            if (_responseTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        }

        public event Action<string>? SendRequested;
        public event Action? Ready;
        public event Action<DisplayPowerState>? PowerStateChanged;
        public event Action<PjLinkCommandResult>? CommandCompleted;
        public event Action<string>? Faulted;

        /// <summary>The greeting has been accepted and the session can send commands.</summary>
        public bool IsReady { get; private set; }
        public bool HasPendingCommand => _pendingCommand.HasValue;
        public DisplayPowerState PowerState { get; private set; } = DisplayPowerState.Unknown;

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
            _receiveBuffer.Clear();
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
            if (SendRequested == null)
                throw new InvalidOperationException("A transport must subscribe to SendRequested.");

            string wireText = _authenticationPrefix + "%1POWR " + parameter + "\r";
            _authenticationPrefix = string.Empty;
            _pendingCommand = command;
            _deadline = now + _responseTimeout;
            SendRequested.Invoke(wireText);
        }

        public void Receive(string chunk, TimeSpan now)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            CheckTimeout(now);
            string? completedLine = null;
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
                    _receiveBuffer.Clear();
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
            CommandCompleted?.Invoke(new PjLinkCommandResult(command, succeeded, response, powerState));
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
                    _authenticationPrefix = BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
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
            Ready?.Invoke();
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
            PowerStateChanged?.Invoke(state);
        }

        private void Fail(string message)
        {
            Disconnect();
            Faulted?.Invoke(message);
        }
    }
}
