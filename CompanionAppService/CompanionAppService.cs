﻿using EddiSpeechService;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Utilities;

namespace EddiCompanionAppService
{
    public partial class CompanionAppService : IDisposable
    {
        // Implementation instructions from Frontier: https://hosting.zaonce.net/docs/oauth2/instructions.html
        private static readonly string LIVE_SERVER = "https://companion.orerve.net";
        private static readonly string BETA_SERVER = "https://pts-companion.orerve.net";
        private static readonly string AUTH_SERVER = "https://auth.frontierstore.net";
        private static readonly string CALLBACK_URL = $"{Constants.EDDI_URL_PROTOCOL}://auth/";
        private static readonly string AUTH_URL = "/auth";
        private static readonly string DECODE_URL = "/decode";
        private static readonly string TOKEN_URL = "/token";
        private static readonly string AUDIENCE = "audience=steam,frontier,epic";
        private static readonly string SCOPE = "scope=capi";
        private static readonly string PROFILE_URL = "/profile";
        private static readonly string MARKET_URL = "/market";
        private static readonly string SHIPYARD_URL = "/shipyard";

        // We cache the profile to avoid spamming the service
        private Profile cachedProfile;
        private DateTime cachedProfileExpires;

        private readonly CustomURLResponder URLResponder;
        private string verifier;
        private string authSessionID;

        public enum State
        {
            LoggedOut,
            AwaitingCallback,
            Authorized,
            NoClientIDConfigured,
            TokenRefresh,
        };
        private State _currentState;
        public State CurrentState
        {
            get => _currentState;
            private set
            {
                if (_currentState == value) { return; }
                State oldState = _currentState;
                _currentState = value;
                StateChanged?.Invoke(oldState, _currentState);
            }
        }
        public delegate void StateChangeHandler(State oldState, State newState);

        // This is not a UI event handler so I consider that CA1009 is just unnecessary ceremony for no benefit.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1009:DeclareEventHandlersCorrectly")]
        public event StateChangeHandler StateChanged;

        public CompanionAppCredentials Credentials;
        public bool gameIsBeta { get; set; } = false;
        public bool active => CurrentState == State.Authorized;

        private static CompanionAppService instance;
        private readonly string clientID; // we are not allowed to check the client ID into version control or publish it to 3rd parties

        private static readonly object instanceLock = new object();
        public static CompanionAppService Instance
        {
            get
            {
                if (instance == null)
                {
                    lock (instanceLock)
                    {
                        if (instance == null)
                        {
                            Logging.Debug("No companion API instance: creating one");
                            instance = new CompanionAppService();
                        }
                    }
                }
                return instance;
            }
        }

        private CompanionAppService()
        {
            Credentials = CompanionAppCredentials.Load();
            string appPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            void logger(string message) => Logging.Error(message);
            URLResponder = new CustomURLResponder(Constants.EDDI_URL_PROTOCOL, handleCallbackUrl, logger, appPath);
            clientID = ClientId.ID;
            if (clientID == null)
            {
                CurrentState = State.NoClientIDConfigured;
                return;
            }

            try
            {
                RefreshToken();
            }
            catch (Exception)
            {
                CurrentState = State.LoggedOut;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // dispose managed resources
                URLResponder.Dispose();
            }
            // dispose unmanaged resources
        }

        private string ServerURL()
        {
            return gameIsBeta ? BETA_SERVER : LIVE_SERVER;
        }

        ///<summary>Log in. Throws an exception if it fails</summary>
        public void Login()
        {
            if (CurrentState != State.LoggedOut)
            {
                // Shouldn't be here
                throw new EliteDangerousCompanionAppIllegalStateException("Service in incorrect state to login (" + CurrentState + ")");
            }

            if (clientID == null)
            {
                throw new EliteDangerousCompanionAppAuthenticationException("Client ID is not configured");
            }

            CurrentState = State.AwaitingCallback;
            string codeChallenge = createAndRememberChallenge();
            string webURL = $"{AUTH_SERVER}{AUTH_URL}" + $"?response_type=code&{AUDIENCE}&{SCOPE}&client_id={clientID}&code_challenge={codeChallenge}&code_challenge_method=S256&state={authSessionID}&redirect_uri={Uri.EscapeDataString(CALLBACK_URL)}";
            Process.Start(webURL);
        }

        private string createAndRememberChallenge()
        {
            RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
            byte[] rawVerifier = new byte[32];
            rng.GetBytes(rawVerifier);
            verifier = base64UrlEncode(rawVerifier);

            byte[] rawAuthSessionID = new byte[8];
            rng.GetBytes(rawAuthSessionID);
            authSessionID = base64UrlEncode(rawAuthSessionID);

            byte[] byteVerifier = Encoding.ASCII.GetBytes(verifier);
            byte[] hash = SHA256.Create().ComputeHash(byteVerifier);
            string codeChallenge = base64UrlEncode(hash);
            return codeChallenge;
        }

        private string base64UrlEncode(byte[] blob)
        {
            string base64 = Convert.ToBase64String(blob, Base64FormattingOptions.None);
            return base64.Replace('+', '-').Replace('/', '_').Replace("=", "");
        }

        private void handleCallbackUrl(string url)
        {
            // NB any user can send an arbitrary URL from the Windows Run dialog, so it must be treated as untrusted
            try
            {
                string code = codeFromCallback(url);

                HttpWebRequest request = GetRequest(AUTH_SERVER + TOKEN_URL);
                request.ContentType = "application/x-www-form-urlencoded";
                request.Method = "POST";
                request.KeepAlive = false;
                request.AllowAutoRedirect = true;
                byte[] data = Encoding.UTF8.GetBytes($"grant_type=authorization_code&client_id={clientID}&code_verifier={verifier}&code={code}&redirect_uri={Uri.EscapeDataString(CALLBACK_URL)}");
                request.ContentLength = data.Length;
                using (Stream dataStream = request.GetRequestStream())
                {
                    dataStream.Write(data, 0, data.Length);
                }

                using (HttpWebResponse response = GetResponse(request))
                {
                    if (response?.StatusCode == null)
                    {
                        throw new EliteDangerousCompanionAppAuthenticationException("Failed to contact authorization server");
                    }
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        string responseData = getResponseData(response);
                        JObject json = JObject.Parse(responseData);
                        Credentials.refreshToken = (string)json["refresh_token"];
                        Credentials.accessToken = (string)json["access_token"];
                        Credentials.tokenExpiry = DateTime.UtcNow.AddSeconds((double)json["expires_in"]);
                        Credentials.Save();
                        if (Credentials.accessToken == null)
                        {
                            throw new EliteDangerousCompanionAppAuthenticationException("Access token not found");
                        }
                        CurrentState = State.Authorized;
                    }
                    else
                    {
                        throw new EliteDangerousCompanionAppAuthenticationException("Invalid refresh token from authorization server");
                    }
                }

            }
            catch (Exception)
            {
                CurrentState = State.LoggedOut;
            }
        }

        private string codeFromCallback(string url)
        {
            if (!(url.StartsWith(CALLBACK_URL) && url.Contains("?")))
            {
                throw new EliteDangerousCompanionAppAuthenticationException("Malformed callback URL from Frontier");
            }

            Dictionary<string, string> paramsDict = ParseQueryString(url);
            if (authSessionID == null || !paramsDict.ContainsKey("state") || paramsDict["state"] != authSessionID)
            {
                throw new EliteDangerousCompanionAppAuthenticationException("Unexpected callback URL from Frontier");
            }

            if (!paramsDict.ContainsKey("code"))
            {
                if (!paramsDict.TryGetValue("error_description", out string desc))
                {
                    paramsDict.TryGetValue("error", out desc);
                }
                desc = desc ?? "no error description";
                throw new EliteDangerousCompanionAppAuthenticationException($"Negative response from Frontier: {desc}");
            }
            return paramsDict["code"];
        }

        private Dictionary<string, string> ParseQueryString(string url)
        {
            // Sadly System.Web.HttpUtility.ParseQueryString() is not available to us
            // https://stackoverflow.com/questions/659887/get-url-parameters-from-a-string-in-net
            Uri myUri = new Uri(url);
            string query = myUri.Query.TrimStart('?');
            string[] queryParams = query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
            var paramValuePairs = queryParams.Select(parameter => parameter.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries));
            var sanitizedValuePairs = paramValuePairs.GroupBy(
                parts => parts[0],
                parts => parts.Length > 2 ? string.Join("=", parts, 1, parts.Length - 1) : (parts.Length > 1 ? parts[1] : ""));
            Dictionary<string, string> paramsDict = sanitizedValuePairs.ToDictionary(
                grouping => grouping.Key,
                grouping => string.Join(",", grouping));
            return paramsDict;
        }

        private JObject DecodeToken()
        {
            if (Credentials.accessToken == null) { return null; }

            HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(AUTH_SERVER + DECODE_URL);
            request.AllowAutoRedirect = true;
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.Headers["Authorization"] = $"Bearer {Credentials.accessToken}";

            using (HttpWebResponse response = GetResponse(request))
            {
                if (response == null)
                {
                    Logging.Debug("Failed to contact API server");
                    throw new EliteDangerousCompanionAppException("Failed to contact API server");
                }

                if (response.StatusCode == HttpStatusCode.Found)
                {
                    return null;
                }
                return JObject.Parse(getResponseData(response));
            }
        }

        private void RefreshToken()
        {
            if (clientID == null)
            {
                throw new EliteDangerousCompanionAppAuthenticationException("Client ID is not configured");
            }
            if (Credentials.refreshToken == null)
            {
                throw new EliteDangerousCompanionAppAuthenticationException("Refresh token not found, need full login");
            }

            CurrentState = State.TokenRefresh;
            HttpWebRequest request = GetRequest(AUTH_SERVER + TOKEN_URL);
            request.ContentType = "application/x-www-form-urlencoded";
            request.Method = "POST";
            byte[] data = Encoding.UTF8.GetBytes($"grant_type=refresh_token&client_id={clientID}&refresh_token={Credentials.refreshToken}");
            request.ContentLength = data.Length;
            using (Stream dataStream = request.GetRequestStream())
            {
                dataStream.Write(data, 0, data.Length);
            }

            using (HttpWebResponse response = GetResponse(request))
            {
                if (response == null)
                {
                    throw new EliteDangerousCompanionAppException("Failed to contact API server");
                }
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    string responseData = getResponseData(response);
                    JObject json = JObject.Parse(responseData);
                    Credentials.refreshToken = (string)json["refresh_token"];
                    Credentials.accessToken = (string)json["access_token"];
                    Credentials.tokenExpiry = DateTime.UtcNow.AddSeconds((double)json["expires_in"]);
                    Credentials.Save();
                    if (Credentials.accessToken == null)
                    {
                        CurrentState = State.LoggedOut;
                        throw new EliteDangerousCompanionAppAuthenticationException("Access token not found");
                    }
                    CurrentState = State.Authorized;
                }
                else
                {
                    CurrentState = State.LoggedOut;
                    throw new EliteDangerousCompanionAppAuthenticationException("Invalid refresh token");
                }
            }
        }

        /// <summary>Log out of the companion API and remove local credentials</summary>
        public void Logout()
        {
            // Remove everything other than the local email address
            Credentials = CompanionAppCredentials.Load();
            Credentials.Clear();
            Credentials.Save();
            CurrentState = State.LoggedOut;
        }

        public Profile Profile(bool forceRefresh = false)
        {
            if ((!forceRefresh) && cachedProfileExpires > DateTime.UtcNow)
            {
                // return the cached version
                Logging.Debug("Returning cached profile");
                return cachedProfile;
            }

            try
            {
                string data = obtainProfile(ServerURL() + PROFILE_URL, out DateTime timestamp);

                if (data == null || data == "Profile unavailable")
                {
                    // Happens if there is a problem with the API.  Logging in again might clear this...
                    relogin();
                    if (CurrentState != State.Authorized)
                    {
                        // No luck; give up
                        SpeechService.Instance.Say(null, Properties.CapiResources.frontier_api_lost, 0);
                        Logout();
                    }
                    else
                    {
                        // Looks like login worked; try again
                        data = obtainProfile(ServerURL() + PROFILE_URL, out timestamp);

                        if (data == null || data == "Profile unavailable")

                        {
                            // No luck with a relogin; give up
                            SpeechService.Instance.Say(null, Properties.CapiResources.frontier_api_lost, 0);
                            Logout();
                            throw new EliteDangerousCompanionAppException("Failed to obtain data from Frontier server (" + CurrentState + ")");
                        }
                    }
                }

                try
                {
                    cachedProfile = ProfileFromJson(data, timestamp);
                }
                catch (JsonException ex)
                {
                    Logging.Error("Failed to parse companion profile", ex);
                    cachedProfile = null;
                }
            }
            catch (EliteDangerousCompanionAppException ex)
            {
                // not Logging.Error as Rollbar is getting spammed when the server is down
                Logging.Info(ex.Message);
            }

            if (cachedProfile != null)
            {
                cachedProfileExpires = DateTime.UtcNow.AddSeconds(30);
                Logging.Debug("Profile is " + JsonConvert.SerializeObject(cachedProfile));
            }

            return cachedProfile;
        }

        private string obtainProfile(string url, out DateTime timestamp)
        {
            DateTime expiry = Credentials?.tokenExpiry.AddSeconds(-60) ?? DateTime.MinValue;
            if (DateTime.UtcNow > expiry)
            {
                RefreshToken();
            }

            if (CurrentState == State.Authorized)
            {
                HttpWebRequest request = GetRequest(url);
                using (HttpWebResponse response = GetResponse(request))
                {
                    if (response == null)
                    {
                        Logging.Debug("Failed to contact API server");
                        throw new EliteDangerousCompanionAppException("Failed to contact API server");
                    }

                    if (response.StatusCode == HttpStatusCode.Found)
                    {
                        timestamp = DateTime.MinValue;
                        return null;
                    }

                    timestamp = DateTime.Parse(response.Headers.Get("date")).ToUniversalTime();
                    return getResponseData(response);
                }
            }
            else
            {
                Logging.Debug("Service in incorrect state to provide profile (" + CurrentState + ")");
                timestamp = DateTime.MinValue;
                return null;
            }
        }

        /**
         * Try to relogin if there is some issue that requires it.
         * Throws an exception if it failed to log in.
         */
        private void relogin()
        {
            // Need to log in again.
            if (clientID == null) { return; }
            Logout();
            Login();
            if (CurrentState != State.Authorized)
            {
                Logging.Debug("Service in incorrect state to provide profile (" + CurrentState + ")");
                throw new EliteDangerousCompanionAppIllegalStateException("Service in incorrect state to provide profile (" + CurrentState + ")");
            }
        }

        /**
         * Obtain the response data from an HTTP web response
         */
        private string getResponseData(HttpWebResponse response)
        {
            if (response is null) { return null; }
            // Obtain and parse our response
            var encoding = response.CharacterSet == ""
                    ? Encoding.UTF8
                    : Encoding.GetEncoding(response.CharacterSet ?? string.Empty);

            Logging.Debug("Reading response");
            using (var stream = response.GetResponseStream())
            {
                if (stream == null)
                {
                    Logging.Warn("No response stream");
                    return null;
                }
                var reader = new StreamReader(stream, encoding);
                string data = reader.ReadToEnd();
                if (string.IsNullOrEmpty(data) || data.Trim() == "")
                {
                    Logging.Warn("No data returned");
                    return null;
                }
                Logging.Debug("Data is " + data);
                return data;
            }
        }

        // Set up a request with the correct parameters for talking to the companion app
        private HttpWebRequest GetRequest(string url)
        {
            HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(url);
            request.AllowAutoRedirect = true;
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.UserAgent = $"EDCD-{Constants.EDDI_NAME}-{Constants.EDDI_VERSION.ShortString}";
            if (CurrentState == State.Authorized)
            {
                request.Headers["Authorization"] = $"Bearer {Credentials.accessToken}";
            }

            return request;
        }

        // Obtain a response, ensuring that we obtain the response's cookies
        private HttpWebResponse GetResponse(HttpWebRequest request)
        {
            HttpWebResponse response;
            try
            {
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException wex)
            {
                Logging.Warn("Failed to obtain response, error code " + wex.Status);
                return null;
            }
            Logging.Debug("Response is " + JsonConvert.SerializeObject(response));
            return response;
        }
    }
}
