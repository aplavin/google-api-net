namespace GoogleAPI.GoogleReader
{
    using System;
    using System.Collections.Generic;

    public class FeedEntry : IEquatable<FeedEntry>
    {
        public FeedEntry()
        {
        }

        public FeedEntry(string id, DateTime published, Feed feed, string link, string title, string content)
            : this(id, published, feed.Id, link, title, content)
        {
        }

        public FeedEntry(string id, DateTime published, string feedId, string link, string title, string content)
            : this()
        {
            if (id == null) throw new ArgumentNullException("id");

            Id = id;
            Published = published;
            FeedId = feedId;
            Link = link;
            Title = title;
            Content = content;
        }

        public string Id { get; private set; }
        public DateTime Published { get; private set; }
        public string FeedId { get; private set; }
        public string Link { get; private set; }
        public string Title { get; private set; }
        public string Content { get; private set; }

        public bool Equals(FeedEntry other)
        {
            return this.Id == other.Id;
        }

        public override string ToString()
        {
            return string.Format("{0} ({1})", this.Title, this.Link);
        }
    }
}