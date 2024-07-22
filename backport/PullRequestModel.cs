using System.Collections.Generic;

namespace Backport;

internal class PullRequestModel
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Url { get; init; }
    public List<string>? Labels { get; init; }
}