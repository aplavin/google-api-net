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

    public class GoogleReader
    {
        private static readonly TimeSpan TokenExpire = TimeSpan.FromMinutes(10);

        private bool _isLoggedIn;
        private readonly string _username;
        private readonly string _password;
        private string _sid;
        private string _auth;

        public string Sid { get { return _sid; } }
        public string Auth { get { return _auth; } }

        public static bool CredentialsValid(string username, string password)
        {
            try
            {
                new GoogleReader(username, password, true);
                return true;
            }
            catch (GoogleReaderException)
            {
                return false;
            }
        }

        public static bool SessionValid(string sid, string auth)
        {
            try
            {
                new GoogleReader(sid, auth).GetJson("api/0/unread-count?output=json");
                return true;
            }
            catch (GoogleReaderException)
            {
                return false;
            }
        }

        public GoogleReader(string sid, string auth)
        {
            _sid = sid;
            _auth = auth;
            _isLoggedIn = true;
        }

        public GoogleReader(string username, string password, bool login)
        {
            _username = username;
            _password = password;
            if (login)
            {
                Login();
            }
        }

        #region Token
        private string _token;
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
            Login();
            return GetString("api/0/token");
        }
        #endregion

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
        /// Marks entry with the specified id as read.
        /// </summary>
        public void MarkAsRead(string id)
        {
            string res = GetString(
                "api/0/edit-tag",
                new { i = id, a = "user/-/state/com.google/read", ac = "edit", T = Token, });

            if (res != "OK")
            {
                throw new GoogleReaderException(
                    string.Format("Mark entry '{0}' as read probably failed: Google didn't return OK.", id));
            }
        }

        /// <summary>
        /// Logs into google account only in not logged in still (or Reset was called). Otherwise just returns.
        /// Username and password from settings are used.
        /// </summary>
        private void Login()
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
                    throw new GoogleReaderException("Login to Google Reader failed: there are problems with access to Google API.", webEx);
                }
            }
        }

        /// <summary>
        /// Gets response from address "http://www.google.com/reader/{url}" using SID and Auth from Login method.
        /// If not logged in - logs in.
        /// </summary>
        private string GetString(string url, object data = null)
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
                    "Request to url '{0}' {1}at Google Reader failed: there are problems with access to Google API.",
                    url,
                    hasData ? "(with additional POST data) " : string.Empty),
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
            catch (Exception ex)
            {
                // Used JSON library can throw also just System.Exception
                if (ex.Message.ContainsCi("json"))
                {
                    throw new GoogleReaderException(
                        string.Format(
                            "Request from URL '{0}' {1}wasn't in JSON format. Problem with access to Google API.",
                            url,
                            data != null ? "(with additional POST data) " : string.Empty),
                        ex);
                }

                throw;
            }
        }
    }
}
