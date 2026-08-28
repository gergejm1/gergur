namespace Gergur.Tabs;

/// <summary>Everything a discarded tab is: enough to draw it in the strip and bring it back.</summary>
public sealed record TabSnapshot(string Url, string Title);
