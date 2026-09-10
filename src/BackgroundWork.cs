using System;
using System.Collections.Generic;

// Structural scanning only: skip free text without decoding or retaining it.
internal static class BackgroundWork
{
    public const string Missing = "background-unknown";
    public const string Clear = "background-clear";
    public const string Pending = "background-pending";

    public static string Read(string json)
    {
        try
        {
            string tasks = Field(json, "background_tasks");
            if (tasks == null) return Missing; // Older providers remain compatible.
            if (tasks.Length < 2 || tasks[0] != '[') return Pending;
            int p = 1;
            White(tasks, ref p);
            while (p < tasks.Length && tasks[p] != ']')
            {
                int start = p;
                Skip(tasks, ref p);
                string task = tasks.Substring(start, p - start);
                // The official array contains in-flight tasks. A persistent monitor
                // is not a finite task; scheduled crons likewise do not gate this turn.
                if (Field(task, "type") != "\"monitor\"") return Pending;
                White(tasks, ref p);
                if (tasks[p] == ']') break;
                if (tasks[p++] != ',') return Pending;
                White(tasks, ref p);
                if (tasks[p] == ']') return Pending;
            }
            return p == tasks.Length - 1 && tasks[p] == ']' ? Clear : Pending;
        }
        catch { return Pending; } // Malformed metadata must not authorize completion.
    }

    internal static string Field(string json, string key)
    {
        int p = 0;
        White(json, ref p);
        if (json[p++] != '{') throw new FormatException();
        string found = null;
        while (true)
        {
            White(json, ref p);
            if (json[p] == '}') return found;
            int start = p;
            StringEnd(json, ref p);
            bool match = p - start == key.Length + 2 &&
                string.CompareOrdinal(json, start + 1, key, 0, key.Length) == 0;
            White(json, ref p);
            if (json[p++] != ':') throw new FormatException();
            White(json, ref p);
            start = p;
            Skip(json, ref p);
            if (match)
            {
                if (found != null) throw new FormatException();
                found = json.Substring(start, p - start);
            }
            White(json, ref p);
            if (json[p] == '}') return found;
            if (json[p++] != ',') throw new FormatException();
            White(json, ref p);
            if (json[p] == '}') throw new FormatException();
        }
    }

    private static void White(string s, ref int p)
    { while (p < s.Length && char.IsWhiteSpace(s[p])) p++; }

    internal static IEnumerable<string> Items(string json)
    {
        int p = 0;
        White(json, ref p);
        if (json[p++] != '[') throw new FormatException();
        White(json, ref p);
        while (json[p] != ']')
        {
            int start = p;
            Skip(json, ref p);
            yield return json.Substring(start, p - start);
            White(json, ref p);
            if (json[p] == ']') break;
            if (json[p++] != ',') throw new FormatException();
            White(json, ref p);
            if (json[p] == ']') throw new FormatException();
        }
        p++;
        White(json, ref p);
        if (p != json.Length) throw new FormatException();
    }

    private static void StringEnd(string s, ref int p)
    {
        if (s[p++] != '"') throw new FormatException();
        while (p < s.Length)
        {
            char c = s[p++];
            if (c == '"') return;
            if (c == '\\') p++;
            else if (c < 32) throw new FormatException();
        }
        throw new FormatException();
    }

    private static void Skip(string s, ref int p)
    {
        if (s[p] == '"') { StringEnd(s, ref p); return; }
        if (s[p] == '{' || s[p] == '[')
        {
            var closes = new Stack<char>();
            do
            {
                char c = s[p];
                if (c == '"') { StringEnd(s, ref p); continue; }
                p++;
                if (c == '{') closes.Push('}');
                else if (c == '[') closes.Push(']');
                else if (c == '}' || c == ']')
                { if (closes.Pop() != c) throw new FormatException(); }
            } while (closes.Count > 0);
            return;
        }
        int start = p;
        while (p < s.Length && !char.IsWhiteSpace(s[p]) && s[p] != ',' && s[p] != '}' && s[p] != ']') p++;
        if (start == p) throw new FormatException();
    }
}
