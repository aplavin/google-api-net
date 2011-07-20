namespace GoogleAPI.GoogleReader
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Reflection;
    using System.Text;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public static class GoogleReader
    {
        private static readonly TimeSpan TokenExpire = TimeSpan.FromMinutes(10);

        private static bool _isLoggedIn;
        private static string _username;
        private static string _password;
        private static string _sid;
        private static string _auth;

        private static string _token;
        private static DateTime _tokenTaken = DateTime.MinValue;

        /// <summary>
        /// Gets authorization token, need for some operations in Google Reader. 
        /// If token is requested the first time or it has been expired, token is automatically downloaded.
        /// </summary>
        private static string Token
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

        /// <summary>
        /// Sets username and password for Google Reader. Call this before calling any other method.
        /// </summary>
        public static void SetCredentials(string googleUsername, string googlePassword)
        {
            _username = googleUsername;
            _password = googlePassword;
        }

        /// <summary>
        /// Resets state of this class. SetCredentials is needed after it.
        /// </summary>
        public static void Reset()
        {
            _isLoggedIn = false;
            _sid = null;
            _auth = null;
            _username = null;
            _password = null;
        }

        /// <summary>
        /// Get list of subscribed feeds from google account.
        /// </summary>
        public static IEnumerable<Feed> GetFeeds()
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
                    unreadCount: unread.ContainsKey(p.Key) ? unread[p.Key] : 0,
                    icon: GetFavicon(p.Key.Substring(5))));
        }

        /// <summary>
        /// Gets unread entries from the specified feed.
        /// </summary>
        public static IEnumerable<FeedEntry> GetEntries(this Feed feed)
        {
            var json = GetJson(string.Format(
                   "api/0/stream/contents/feed/{0}?xt=user/-/state/com.google/read&n=1000&ck={1}",
                   feed.Url,
                   DateTime.UtcNow.ToUnixTime(true)));

            return json["items"].Select(
                v => new FeedEntry(
                    id: v["id"].Value<string>(),
                    published: DateTimeUtils.FromUnixTime(v["published"].Value<long>()),
                    feed: feed, 
                    link: CommonUtils.TryNullReference(() => v["alternate"].First["href"].Value<string>()),
                    title: CommonUtils.TryNullReference(() => v["title"].Value<string>()),
                    content: CommonUtils.TryNullReference(() => v["summary"]["content"].Value<string>())));
        }

        /// <summary>
        /// Gets unread entries for all specified feeds.
        /// </summary>
        public static IEnumerable<IGrouping<Feed, FeedEntry>> GetEntries(this IEnumerable<Feed> feeds)
        {
            if (feeds == null) throw new ArgumentNullException("feeds");
            if (feeds.Empty()) return Enumerable.Empty<IGrouping<Feed, FeedEntry>>();

            int degree = CompareUtils.Clamp(feeds.Count(), 1, 63);

            return feeds.
                AsParallel().WithDegreeOfParallelism(degree).
                SelectMany(GetEntries).
                GroupBy(e => e.Feed);
        }

        /// <summary>
        /// Marks all entries of the specified feed as read.
        /// </summary>
        public static void MarkAsRead(Feed feed)
        {
            string res = GetString("api/0/mark-all-as-read", new { s = feed.Id, t = feed.Title, T = Token });

            if (res != "OK")
            {
                throw new GoogleReaderException(
                    string.Format("Mark feed '{0}' as read probably failed: Google didn't return OK.", feed.Id));
            }
        }

        /// <summary>
        /// Marks all entries of the specified feeds as read.
        /// </summary>
        public static void MarkAsRead(IEnumerable<Feed> feeds)
        {
            if (feeds == null) throw new ArgumentNullException("feeds");
            if (feeds.Empty()) return;

            int degree = Math.Min(63, feeds.Count());
            feeds.AsParallel().WithDegreeOfParallelism(degree).ForAll(MarkAsRead);
        }

        /// <summary>
        /// Marks the specified entry as read.
        /// </summary>
        public static void MarkAsRead(FeedEntry entry)
        {
            string res = GetString(
                "api/0/edit-tag",
                new { i = entry.Id, a = "user/-/state/com.google/read", ac = "edit", T = Token, });

            if (res != "OK")
            {
                throw new GoogleReaderException(
                    string.Format("Mark entry '{0}' as read probably failed: Google didn't return OK.", entry.Id));
            }
        }

        /// <summary>
        /// Marks all specified entries as read.
        /// </summary>
        public static void MarkAsRead(IEnumerable<FeedEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException("entries");
            if (entries.Empty()) return;

            int degree = Math.Min(63, entries.Count());
            entries.AsParallel().WithDegreeOfParallelism(degree).ForAll(MarkAsRead);
        }

        /// <summary>
        /// Logs into google account only in not logged in still (or Reset was called). Otherwise just returns.
        /// Username and password from settings are used.
        /// </summary>
        private static void Login()
        {
            if (_isLoggedIn) return;

            if (string.IsNullOrEmpty(_username) || string.IsNullOrEmpty(_password))
                throw new GoogleReaderException("Google username and/or password isn't specified");

            try
            {
                var loginRequest = (HttpWebRequest)WebRequest.Create(@"https://www.google.com/accounts/ClientLogin");

                byte[] requestContent = Encoding.UTF8.GetBytes(
                    "service={service}&Email={user}&Passwd={pass}&continue=http://www.google.com/".
                    FormatNamed(new { service = "reader", user = _username, pass = _password }));

                loginRequest.Method = "POST";
                loginRequest.ContentType = "application/x-www-form-urlencoded";
                loginRequest.ContentLength = requestContent.Length;

                using (Stream requestStream = loginRequest.GetRequestStream())
                {
                    // add form data to request stream
                    requestStream.Write(requestContent, 0, requestContent.Length);
                }

                string data;
                using (var response = loginRequest.GetResponse())
                using (var responseStream = response.GetResponseStream())
                using (var sr = new StreamReader(responseStream))
                {
                    data = sr.ReadToEnd();
                }

                try
                {
                    _sid = data.Substring((data.IndexOf("SID=") + 4), (data.IndexOf("\n") - 4)).Trim();
                    _auth = data.Substring(data.IndexOf("Auth=") + 5).Trim();

                    _isLoggedIn = true;
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    throw new GoogleReaderException("Wrong response format from Google ClientLogin, can't parse SID and Auth.", ex);
                }
            }
            catch (WebException webEx)
            {
                if (((HttpWebResponse)webEx.Response).StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new GoogleReaderException("Login to Google Reader failed: incorrect username or password.", webEx);
                }
                else
                {
                    throw new GoogleReaderException("Login to Google Reader failed: there are problems with your Internet connection or Google has changed its API.", webEx);
                }
            }
        }

        /// <summary>
        /// Get favicon for the host of the url. Icon is fetched from google cache in png format.
        /// </summary>
        private static Bitmap GetFavicon(string url)
        {
            if (url == null) throw new ArgumentNullException("url");

            Uri srcUri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out srcUri))
            {
                throw new ArgumentException("URL specified to get favicon is malformed");
            }

            url = string.Format("http://s2.googleusercontent.com/s2/favicons?domain={0}", srcUri.Host);
            return ImageUtils.Download(url);
        }

        /// <summary>
        /// Gets response from address "http://www.google.com/reader/{url}" using SID and Auth from Login method.
        /// If not logged in - logs in.
        /// </summary>
        private static string GetString(string url, object data = null)
        {
            if (url == null) throw new ArgumentNullException("url");

            Login();

            bool hasData = data != null;
            byte[] bytes = null;
            if (hasData)
            {
                var values = data.GetType().GetMembers().
                    Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property).
                    ToDictionary(m => m.Name, m => ((PropertyInfo)m).GetValue(data, null).ToString());
                string post = values.Aggregate(kvp => "{0}={1}".FormatWith(kvp.Key, kvp.Value), "&");
                bytes = new ASCIIEncoding().GetBytes(post);
            }

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(string.Format(@"http://www.google.com/reader/{0}", url));

                request.Headers.Add("Authorization", string.Format("GoogleLogin auth={0}", _auth));
                request.CookieContainer = new CookieContainer();
                request.CookieContainer.Add(new Cookie("SID", _sid, "/", ".google.com"));

                if (hasData)
                {
                    request.Method = "POST";
                    request.ContentType = "application/x-www-form-urlencoded";
                    request.ContentLength = bytes.Length;

                    using (var stream = request.GetRequestStream())
                    {
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                else
                {
                    request.Method = "GET";
                }

                using (var response = request.GetResponse())
                using (var responseStream = response.GetResponseStream())
                using (var reader = new StreamReader(responseStream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException webEx)
            {
                throw new GoogleReaderException(
                    string.Format(
                    "Request to url '{0}' {1}at Google Reader failed: there are problems with your Internet connection or Google has changed its API.",
                    url,
                    hasData ? "(with additional POST data) " : string.Empty),
                    webEx);
            }
        }

        /// <summary>
        /// Gets api result from google reader as JSON object.
        /// </summary>
        private static JObject GetJson(string url, object data = null)
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
                    "Request from URL '{0}' {1}wasn't in JSON format. Probably Google Reader API has changed.",
                    url,
                    data != null ? "(with additional POST data) " : string.Empty),
                    jsonEx);
            }
            catch (Exception ex)
            {
                // Used JSON library can throw also just System.Exception
                if (ex.Message.Contains("json"))
                {
                    throw new GoogleReaderException(
                        string.Format(
                            "Request from URL '{0}' {1}wasn't in JSON format. Probably Google Reader API has changed.",
                            url,
                            data != null ? "(with additional POST data) " : string.Empty),
                        ex);
                }

                throw;
            }
        }

        private static string GetToken()
        {
            Login();
            return GetString("api/0/token");
        }
    }
}
