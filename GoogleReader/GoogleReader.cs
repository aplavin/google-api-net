namespace GoogleAPI.GoogleReader
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public class GoogleReader
    {
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _refreshToken;

        #region AccessToken

        private string _accessToken;
        private static readonly TimeSpan AccessTokenExpire = TimeSpan.FromMinutes(10);
        private DateTime _accessTokenTaken = DateTime.MinValue;

        private string AccessToken
        {

            get
            {
                if (_accessToken == null || DateTime.Now - _accessTokenTaken > AccessTokenExpire)
                {
                    _accessToken = GetAccessToken();
                    _accessTokenTaken = DateTime.Now;
                }

                return _accessToken;
            }
        }

        private string GetAccessToken()
        {
            string response = InternetUtils.Fetch("https://accounts.google.com/o/oauth2/token", new
                {
                    client_id = _clientId,
                    client_secret = _clientSecret,
                    refresh_token = _refreshToken,
                    grant_type = "refresh_token",
                });
            var json = JObject.Parse(response);
            return json["access_token"].ToString();
        }
        #endregion

        #region Edit Token
        private string _token;

        private static readonly TimeSpan TokenExpire = TimeSpan.FromMinutes(10);
        private DateTime _tokenTaken = DateTime.MinValue;

        /// <summary>
        /// Gets authorization token, need for some operations in Google Reader. 
        /// If token is requested the first time or it has been expired, token is automatically downloaded.
        /// </summary>
        private string Token
        {
            get
            {
                if (_token == null || DateTime.Now - _tokenTaken > TokenExpire)
                {
                    _token = GetToken();
                    _tokenTaken = DateTime.Now;
                }

                return _token;
            }
        }

        private string GetToken()
        {
            return GetString("api/0/token");
        }
        #endregion

        /// <summary>
        /// Creates new instance of GoogleReader with specified OAuth parameters.
        /// </summary>
        /// <param name="clientId">Client ID - obtained by developer from Google</param>
        /// <param name="clientSecret">Client secret - obtained by developer from Google</param>
        /// <param name="refreshToken">Refresh token - obtained during authorization process</param>
        public GoogleReader(string clientId, string clientSecret, string refreshToken)
        {
            _clientId = clientId;
            _clientSecret = clientSecret;
            _refreshToken = refreshToken;
        }

        /// <summary>
        /// Get list of subscribed feeds from google account.
        /// </summary>
        public IEnumerable<Feed> GetFeeds()
        {
            var unread = GetJson("api/0/unread-count?output=json")["unreadcounts"].
                Where(f => ((string)f["id"]).StartsWith("feed/")).
                ToDictionary(f => (string)f["id"], f => (int)f["count"]);

            var titles = GetJson("api/0/subscription/list?output=json")["subscriptions"].
                Where(f => ((string)f["id"]).StartsWith("feed/")).
                ToDictionary(f => (string)f["id"], f => (string)f["title"]);

            return titles.Select(p =>
                new Feed(
                    id: p.Key,
                    title: p.Value,
                    unreadCount: unread.ContainsKey(p.Key) ? unread[p.Key] : 0));
        }

        /// <summary>
        /// Gets unread entries from the specified <paramref name="feed"/>.
        /// If <paramref name="number"/> is between 1 and 1000, then it specifies number of entries to load. Else all unread entries are loaded.
        /// </summary>
        public IEnumerable<FeedEntry> GetEntries(Feed feed, int number = 0)
        {
            return GetEntries(feed.Id, number);
        }

        /// <summary>
        /// Gets unread entries from the feed with specified <paramref name="feedId"/>.
        /// If <paramref name="number"/> is between 1 and 1000, then it specifies number of entries to load. Else all unread entries are loaded.
        /// </summary>
        public IEnumerable<FeedEntry> GetEntries(string feedId, int number = 0)
        {
            string request = number.InRange(1, 1001)
                                 ? "api/0/stream/contents/feed/{0}?n=" + number + "&ck={1}"
                                 : "api/0/stream/contents/feed/{0}?xt=user/-/state/com.google/read&n=1000&ck={1}";
            var json = GetJson(string.Format(
                   request,
                   feedId.Substring(5),
                   DateTime.UtcNow.ToUnixTime(true)));

            return json["items"].Select(
                v => new FeedEntry(
                    id: v["id"].Value<string>(),
                    published: DateTimeUtils.FromUnixTime(v["published"].Value<long>()),
                    feedId: feedId,
                    link: CommonUtils.NullSafe(() => v["alternate"].First["href"].Value<string>()),
                    title: CommonUtils.NullSafe(() => v["title"].Value<string>()),
                    content: CommonUtils.NullSafe(() => v["summary"]["content"].Value<string>())));
        }

        /// <summary>
        /// Marks all entries of the specified feed as read.
        /// </summary>
        public void MarkAsRead(Feed feed)
        {
            string res = GetString("api/0/mark-all-as-read", new { s = feed.Id, t = feed.Title, T = Token });

            if (res != "OK")
            {
                throw new GoogleReaderException(
                    string.Format("Mark feed '{0}' as read probably failed: Google didn't return OK.", feed.Id));
            }
        }

        /// <summary>
        /// Marks the specified entry as read.
        /// </summary>
        public void MarkAsRead(FeedEntry entry)
        {
            MarkAsRead(entry.Id);
        }

        /// <summary>
        /// Marks entry with the specified id as read.
        /// </summary>
        public void MarkAsRead(string id)
        {
            string res = GetString(
                "api/0/edit-tag",
                new
                {
                    i = id,
                    a = "user/-/state/com.google/read",
                    ac = "edit",
                    T = Token,
                });

            if (res != "OK")
            {
                throw new GoogleReaderException(
                    string.Format("Mark entry '{0}' as read probably failed: Google didn't return OK.", id));
            }
        }

        /// <summary>
        /// Gets response from address "https://www.google.com/reader/{url}" using SID and Auth from Login method.
        /// If not logged in - logs in.
        /// </summary>
        private string GetString(string url, object data = null)
        {
            if (url == null) throw new ArgumentNullException("url");

            try
            {
                return InternetUtils.Fetch("https://www.google.com/reader/" + url,
                     data,
                     new { Authorization = string.Format("Bearer {0}", AccessToken) });
            }
            catch (WebException webEx)
            {
                throw new GoogleReaderException(
                    string.Format(
                    "Request to url '{0}' at Google Reader failed: there are problems with access to Google API.",
                    url),
                    webEx);
            }
        }

        /// <summary>
        /// Gets api result from google reader as JSON object.
        /// </summary>
        private JObject GetJson(string url, object data = null)
        {
            if (url == null) throw new ArgumentNullException("url");

            try
            {
                return JObject.Parse(GetString(url, data));
            }
            catch (JsonReaderException jsonEx)
            {
                throw new GoogleReaderException(
                    string.Format(
                    "Request from URL '{0}' {1}wasn't in JSON format. Problem with access to Google API.",
                    url,
                    data != null ? "(with additional POST data) " : string.Empty),
                    jsonEx);
            }
        }
    }
}
