using System;
using System.Text;
using System.Web.Script.Serialization;

// Only structured status and the active plan label are decoded. Never inspect
// prompts, responses, shell commands or inactive task descriptions.
internal static class WorkDetails
{
    public static string Snapshot(string json, bool codex)
    {
        try
        {
            string input = BackgroundWork.Field(json, "tool_input");
            string list = BackgroundWork.Field(input, codex ? "plan" : "todos");
            int done = 0, active = 0, total = 0;
            string label = "";
            foreach (string item in BackgroundWork.Items(list))
            {
                if (++total > 256) return null;
                string status = BackgroundWork.Field(item, "status");
                if (status == "\"completed\"") done++;
                else if (status == "\"in_progress\"")
                {
                    active++;
                    if (active == 1)
                    {
                        string raw = BackgroundWork.Field(item, codex ? "step" : "activeForm");
                        if (raw == null && !codex) raw = BackgroundWork.Field(item, "content");
                        label = DecodeLabel(raw);
                    }
                }
                else if (status != "\"pending\"") return null;
            }
            if (active != 1) label = ""; // Multiple concurrent tasks: no invented selection.
            return done + "/" + active + "/" + total + "|" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(label));
        }
        catch { return null; }
    }

    private static string DecodeLabel(string raw)
    {
        try
        {
            if (raw == null || raw.Length > 4096 || raw[0] != '"') return "";
            return Clean(new JavaScriptSerializer().Deserialize<string>(raw));
        }
        catch { return ""; }
    }

    public static string Clean(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var result = new StringBuilder();
        foreach (char c in value)
        {
            if (char.IsControl(c) || char.GetUnicodeCategory(c) ==
                System.Globalization.UnicodeCategory.Format) result.Append(' ');
            else result.Append(c);
            if (result.Length >= 120) break;
        }
        if (result.Length > 0 && char.IsHighSurrogate(result[result.Length - 1])) result.Length--;
        return result.ToString().Trim();
    }

    internal static bool TryCounts(string extra, out int done, out int active, out int total)
    {
        done = active = total = 0;
        if (extra == null) return false;
        string[] nums = extra.Split('|')[0].Split('/');
        return nums.Length == 3 && int.TryParse(nums[0], out done) &&
            int.TryParse(nums[1], out active) && int.TryParse(nums[2], out total) &&
            total >= 0 && done >= 0 && active >= 0 && active <= total && done <= total - active;
    }

    public static string Label(string extra)
    {
        try
        {
            int split = extra.IndexOf('|');
            if (split < 0 || extra.Length - split > 1024) return "";
            return Clean(Encoding.UTF8.GetString(Convert.FromBase64String(extra.Substring(split + 1))));
        }
        catch { return ""; }
    }

    public static string Elapsed(long milliseconds, bool observedStart)
    {
        long seconds = Math.Max(0, milliseconds / 1000);
        string duration = seconds >= 3600
            ? (seconds / 3600) + ":" + ((seconds / 60) % 60).ToString("00") + ":" + (seconds % 60).ToString("00")
            : (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
        return (observedStart ? "経過 " : "観測から ") + duration;
    }
}
