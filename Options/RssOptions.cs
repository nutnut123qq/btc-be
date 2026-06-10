namespace Backend.Options;

public class RssOptions
{
    public const string SectionName = "Rss";

    public int PollMinutes { get; set; } = 15;
    public List<RssFeedItem> Feeds { get; set; } = new();
}

public class RssFeedItem
{
    public string Url { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}
