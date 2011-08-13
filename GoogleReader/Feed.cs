﻿namespace GoogleAPI.GoogleReader
{
    using System;
    using System.Drawing;
    using System.Windows.Media.Imaging;

    public class Feed : IEquatable<Feed>
    {
        public Feed()
        {
        }

        public Feed(string id, string title, int unreadCount, BitmapSource icon)
            : this(id, title, unreadCount, icon.ToBitmap())
        {
        }

        public Feed(string id, string title, int unreadCount, Bitmap icon)
            : this()
        {
            if (id == null) throw new ArgumentNullException("id");
            if (!id.StartsWith("feed/") || !Uri.IsWellFormedUriString(id.Substring(5), UriKind.Absolute))
                throw new ArgumentException("Wrong id format: it must have format 'feed/{url}'");

            this.Id = id;
            this.Title = title ?? "[No title]";
            this.UnreadCount = unreadCount;
            this.Icon = icon;
        }

        public string Id { get; private set; }
        public string Url { get { return this.Id.Substring(5); } }
        public string Title { get; private set; }
        public int UnreadCount { get; private set; }
        public Bitmap Icon { get; private set; }
        public BitmapSource IconSource { get { return this.Icon.ToBitmapSource(); } }

        public override string ToString()
        {
            return string.Format("{0} ({1})", this.Title, this.Url);
        }

        public bool Equals(Feed other)
        {
            return Equals(other.Id, this.Id);
        }

        public override int GetHashCode()
        {
            return this.Id.GetHashCode();
        }
    }
}