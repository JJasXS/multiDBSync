using SqlAccountSync.Models;

namespace SqlAccountSync.Helpers;

/// <summary>Shared flash-toast builders for Razor Pages.</summary>
public static class FlashToasts
{
    public static FlashToastModel Fail(string title, IEnumerable<string>? lines = null) =>
        new()
        {
            Title = title,
            Lines = lines?.Where(l => !string.IsNullOrWhiteSpace(l)).ToList() ?? [],
            Ok = false
        };

    public static FlashToastModel Ok(string title, IEnumerable<string>? lines = null) =>
        new()
        {
            Title = title,
            Lines = lines?.Where(l => !string.IsNullOrWhiteSpace(l)).ToList() ?? [],
            Ok = true
        };

    public static FlashToastModel Queue(int queued, int failed, List<string> messages, string noun)
    {
        if (queued == 0 && failed == 0)
            return Fail(messages.FirstOrDefault() ?? "Nothing queued.");

        var lines = messages.Count > 12
            ? messages.Take(12).Append($"+{messages.Count - 12} more").ToList()
            : messages;

        return new FlashToastModel
        {
            Title = $"Queued {queued} {noun}{(queued == 1 ? "" : "s")}."
                + (failed > 0 ? $" {failed} could not be queued." : ""),
            Lines = lines,
            Ok = queued > 0 && failed == 0
        };
    }

    public static void TallyJob(ref int queued, ref int failed, List<string> messages, int jobId, string okLine, string failMsg)
    {
        if (jobId > 0)
        {
            queued++;
            messages.Add(okLine);
            return;
        }

        failed++;
        messages.Add(failMsg);
    }
}
