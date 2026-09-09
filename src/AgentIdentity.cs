using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// Read only a top-level identity, never agent_id text nested in a tool response.
internal static class AgentIdentity
{
    public static string Token(string json)
    {
        int depth = 0;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '{' || c == '[') { depth++; continue; }
            if (c == '}' || c == ']') { depth--; continue; }
            if (c != '"') continue;
            int begin = ++i;
            for (; i < json.Length; i++)
            {
                if (json[i] == '\\') { i++; continue; }
                if (json[i] == '"') break;
            }
            if (depth != 1 || i - begin != 8 || string.CompareOrdinal(json, begin, "agent_id", 0, 8) != 0) continue;
            int p = i + 1;
            while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
            if (p >= json.Length || json[p++] != ':') continue;
            while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
            if (p >= json.Length || json[p++] != '"') return "";
            int end = json.IndexOf('"', p);
            if (end <= p || end - p > 256) return "";
            string id = json.Substring(p, end - p);
            if (!Regex.IsMatch(id, @"\A[A-Za-z0-9_.:-]+\z")) return "";
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(id))).Replace("-", "").ToLowerInvariant();
        }
        return "";
    }
}
