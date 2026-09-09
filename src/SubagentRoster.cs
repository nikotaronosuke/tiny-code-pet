using System;
using System.Collections.Generic;

namespace ClaudePet
{
    // Observed identities only. Tombstones absorb duplicate/reordered async hooks.
    internal sealed class SubagentRoster
    {
        private const int Limit = 256;
        private readonly HashSet<string> active = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> stopped = new HashSet<string>(StringComparer.Ordinal);
        private bool saturated;
        public int Count { get { return active.Count; } }
        public void Reset() { active.Clear(); stopped.Clear(); saturated = false; }
        public void Apply(bool start, string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 128) return;
            if (start)
            {
                if (!saturated && !stopped.Contains(token) && active.Count < Limit) active.Add(token);
            }
            else
            {
                active.Remove(token);
                if (stopped.Count < Limit) stopped.Add(token);
                else saturated = true; // Never resurrect unknown late starts after overflow.
            }
        }
    }
}
