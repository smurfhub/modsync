using System;
using System.Collections.Generic;

namespace ModSync
{
    internal sealed class ChallengeRegistry<TConnection> where TConnection : class
    {
        private readonly Dictionary<TConnection, State> _connections =
            new Dictionary<TConnection, State>();

        internal bool Register(TConnection connection, double deadline)
        {
            if (connection == null || _connections.ContainsKey(connection))
            {
                return false;
            }

            _connections.Add(connection, new State { Deadline = deadline });
            return true;
        }

        internal bool TryIssue(
            TConnection connection,
            string nonce,
            double deadline)
        {
            if (!InventoryPacketCodec.IsNonce(nonce) ||
                !_connections.TryGetValue(connection, out State state) ||
                state.ChallengeIssued || state.Accepted || state.Rejected)
            {
                return false;
            }

            state.Nonce = nonce;
            state.Deadline = deadline;
            state.ChallengeIssued = true;
            return true;
        }

        internal bool TryConsume(TConnection connection, string nonce, double now)
        {
            if (!_connections.TryGetValue(connection, out State state) ||
                !state.ChallengeIssued || state.Consumed || state.Accepted || state.Rejected)
            {
                return false;
            }

            state.Consumed = true;
            bool valid = now < state.Deadline &&
                         string.Equals(state.Nonce, nonce, StringComparison.Ordinal);
            state.Nonce = null;
            if (!valid)
            {
                state.Rejected = true;
            }

            return valid;
        }

        internal bool Accept(TConnection connection)
        {
            if (!_connections.TryGetValue(connection, out State state) ||
                !state.Consumed || state.Rejected)
            {
                return false;
            }

            state.Accepted = true;
            return true;
        }

        internal bool ExtendDeadline(TConnection connection, double deadline)
        {
            if (!_connections.TryGetValue(connection, out State state) ||
                state.Accepted || state.Rejected ||
                double.IsNaN(deadline) ||
                deadline <= state.Deadline)
            {
                return false;
            }

            state.Deadline = deadline;
            return true;
        }

        internal bool IsAccepted(TConnection connection)
        {
            return connection != null &&
                   _connections.TryGetValue(connection, out State state) &&
                   state.Accepted;
        }

        internal void Reject(TConnection connection)
        {
            if (_connections.TryGetValue(connection, out State state) && !state.Accepted)
            {
                state.Rejected = true;
                state.Nonce = null;
            }
        }

        internal void CollectExpired(double now, List<TConnection> expired)
        {
            foreach (KeyValuePair<TConnection, State> item in _connections)
            {
                State state = item.Value;
                if (!state.Accepted && !state.Rejected && now >= state.Deadline)
                {
                    state.Rejected = true;
                    state.Nonce = null;
                    expired.Add(item.Key);
                }
            }
        }

        internal void Remove(TConnection connection)
        {
            if (connection != null)
            {
                _connections.Remove(connection);
            }
        }

        internal void Clear()
        {
            _connections.Clear();
        }

        private sealed class State
        {
            internal string Nonce;
            internal double Deadline;
            internal bool ChallengeIssued;
            internal bool Consumed;
            internal bool Accepted;
            internal bool Rejected;
        }
    }
}
