namespace GoogleAPI.GoogleReader
{
    using System;
    using System.Collections.Generic;

    public struct FeedEntry : IEquatable<FeedEntry>
    {
        public FeedEntry(string id, DateTime published, Feed feed, string link, string title, string content)
            : this()
        {
            if (id == null) throw new ArgumentNullException("id");

            this.Id = id;
            this.Published = published;
            this.Feed = feed;
            this.Link = link;
            this.Title = title;
            this.Content = content;
        }

        public string Id { get; private set; }
        public DateTime Published { get; private set; }
        public Feed Feed { get; private set; }
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