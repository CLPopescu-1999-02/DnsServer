﻿/*
Technitium DNS Server
Copyright (C) 2018  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using TechnitiumLibrary.IO;
using TechnitiumLibrary.Net;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Proxy;

namespace DnsServerCore
{
    public class DnsWebService
    {
        #region enum

        enum ServiceState
        {
            Stopped = 0,
            Running = 1,
            Stopping = 2
        }

        #endregion

        #region variables

        readonly string _currentVersion;
        readonly string _appFolder;
        readonly string _configFolder;
        readonly Uri _updateCheckUri;

        readonly LogManager _log;

        string _serverDomain;
        int _webServicePort;

        DnsServer _dnsServer;

        HttpListener _webService;
        Thread _webServiceThread;

        readonly ConcurrentDictionary<string, string> _credentials = new ConcurrentDictionary<string, string>();
        readonly ConcurrentDictionary<string, UserSession> _sessions = new ConcurrentDictionary<string, UserSession>();

        volatile ServiceState _state = ServiceState.Stopped;

        #endregion

        #region constructor

        public DnsWebService(string configFolder = null, Uri updateCheckUri = null)
        {
            Assembly assembly = Assembly.GetEntryAssembly();
            AssemblyName assemblyName = assembly.GetName();

            _currentVersion = assemblyName.Version.ToString();
            _appFolder = Path.GetDirectoryName(assembly.Location);

            if (configFolder == null)
                _configFolder = Path.Combine(_appFolder, "config");
            else
                _configFolder = configFolder;

            _updateCheckUri = updateCheckUri;

            if (!Directory.Exists(_configFolder))
                Directory.CreateDirectory(_configFolder);

            string logFolder = Path.Combine(_configFolder, "logs");

            if (!Directory.Exists(logFolder))
                Directory.CreateDirectory(logFolder);

            _log = new LogManager(logFolder);
        }

        #endregion

        #region private

        private void AcceptWebRequestAsync(object state)
        {
            try
            {
                while (true)
                {
                    HttpListenerContext context = _webService.GetContext();
                    ThreadPool.QueueUserWorkItem(ProcessRequestAsync, new object[] { context.Request, context.Response });
                }
            }
            catch
            {
                if (_state == ServiceState.Running)
                    throw;
            }
        }

        private void ProcessRequestAsync(object state)
        {
            object[] parameters = state as object[];
            HttpListenerRequest request = parameters[0] as HttpListenerRequest;
            HttpListenerResponse response = parameters[1] as HttpListenerResponse;

            response.AddHeader("Server", "");

            try
            {
                Uri url = request.Url;
                string path = url.AbsolutePath;

                if (!path.StartsWith("/"))
                {
                    Send404(response);
                    return;
                }

                if (path.StartsWith("/api/"))
                {
                    using (MemoryStream mS = new MemoryStream())
                    {
                        using (JsonTextWriter jsonWriter = new JsonTextWriter(new StreamWriter(mS)))
                        {
                            jsonWriter.WriteStartObject();

                            try
                            {
                                switch (path)
                                {
                                    case "/api/login":
                                        Login(request, jsonWriter);
                                        break;

                                    case "/api/logout":
                                        Logout(request);
                                        break;

                                    default:
                                        if (!IsSessionValid(request))
                                            throw new InvalidTokenDnsWebServiceException("Invalid token or session expired.");

                                        jsonWriter.WritePropertyName("response");
                                        jsonWriter.WriteStartObject();

                                        try
                                        {
                                            switch (path)
                                            {
                                                case "/api/changePassword":
                                                    ChangePassword(request);
                                                    break;

                                                case "/api/checkForUpdate":
                                                    CheckForUpdate(request, jsonWriter);
                                                    break;

                                                case "/api/getDnsSettings":
                                                    GetDnsSettings(jsonWriter);
                                                    break;

                                                case "/api/setDnsSettings":
                                                    SetDnsSettings(request, jsonWriter);
                                                    break;

                                                case "/api/flushDnsCache":
                                                    _dnsServer.CacheZoneRoot.Flush();
                                                    break;

                                                case "/api/listCachedZones":
                                                    ListCachedZones(request, jsonWriter);
                                                    break;

                                                case "/api/deleteCachedZone":
                                                    DeleteCachedZone(request);
                                                    break;

                                                case "/api/listZones":
                                                    ListZones(jsonWriter);
                                                    break;

                                                case "/api/createZone":
                                                    CreateZone(request);
                                                    break;

                                                case "/api/deleteZone":
                                                    DeleteZone(request);
                                                    break;

                                                case "/api/enableZone":
                                                    EnableZone(request);
                                                    break;

                                                case "/api/disableZone":
                                                    DisableZone(request);
                                                    break;

                                                case "/api/addRecord":
                                                    AddRecord(request);
                                                    break;

                                                case "/api/getRecords":
                                                    GetRecords(request, jsonWriter);
                                                    break;

                                                case "/api/deleteRecord":
                                                    DeleteRecord(request);
                                                    break;

                                                case "/api/updateRecord":
                                                    UpdateRecord(request);
                                                    break;

                                                case "/api/resolveQuery":
                                                    ResolveQuery(request, jsonWriter);
                                                    break;

                                                case "/api/listLogs":
                                                    ListLogs(jsonWriter);
                                                    break;

                                                case "/api/deleteLog":
                                                    DeleteLog(request);
                                                    break;

                                                default:
                                                    throw new DnsWebServiceException("Invalid command: " + path);
                                            }
                                        }
                                        finally
                                        {
                                            jsonWriter.WriteEndObject();
                                        }
                                        break;
                                }

                                jsonWriter.WritePropertyName("status");
                                jsonWriter.WriteValue("ok");
                            }
                            catch (InvalidTokenDnsWebServiceException ex)
                            {
                                jsonWriter.WritePropertyName("status");
                                jsonWriter.WriteValue("invalid-token");

                                jsonWriter.WritePropertyName("errorMessage");
                                jsonWriter.WriteValue(ex.Message);
                            }
                            catch (Exception ex)
                            {
                                _log.Write(GetRequestRemoteEndPoint(request), ex);

                                jsonWriter.WritePropertyName("status");
                                jsonWriter.WriteValue("error");

                                jsonWriter.WritePropertyName("errorMessage");
                                jsonWriter.WriteValue(ex.Message);

                                jsonWriter.WritePropertyName("stackTrace");
                                jsonWriter.WriteValue(ex.StackTrace);
                            }

                            jsonWriter.WriteEndObject();

                            jsonWriter.Flush();

                            response.ContentType = "application/json; charset=utf-8";
                            response.ContentEncoding = Encoding.UTF8;

                            using (Stream stream = response.OutputStream)
                            {
                                mS.WriteTo(response.OutputStream);
                            }
                        }
                    }
                }
                else if (path.StartsWith("/log/"))
                {
                    if (!IsSessionValid(request))
                    {
                        Send403(response, "Invalid token or session expired.");
                        return;
                    }

                    string[] pathParts = path.Split('/');

                    string logFileName = pathParts[2];
                    string logFile = Path.Combine(_log.LogFolder, logFileName + ".log");

                    LogManager.DownloadLog(response, logFile, 2 * 1024 * 1024);
                }
                else
                {
                    if (path.Contains("/../"))
                    {
                        Send404(response);
                        return;
                    }

                    if (path == "/")
                        path = "/index.html";

                    path = Path.Combine(_appFolder, "www" + path.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(path))
                    {
                        Send404(response);
                        return;
                    }

                    SendFile(response, path);
                }
            }
            catch (Exception ex)
            {
                _log.Write(GetRequestRemoteEndPoint(request), ex);

                try
                {
                    Send500(response, ex);
                }
                catch
                { }
            }
        }

        private IPEndPoint GetRequestRemoteEndPoint(HttpListenerRequest request)
        {
            //this is due to mono NullReferenceException issue
            try
            {
                return request.RemoteEndPoint;
            }
            catch
            {
                return new IPEndPoint(IPAddress.Any, 0);
            }
        }

        private void Send500(HttpListenerResponse response, Exception ex)
        {
            Send500(response, ex.ToString());
        }

        private void Send500(HttpListenerResponse response, string message)
        {
            byte[] buffer = Encoding.UTF8.GetBytes("<h1>500 Internal Server Error</h1><p>" + message + "</p>");

            response.StatusCode = 500;
            response.ContentType = "text/html";
            response.ContentLength64 = buffer.Length;

            using (Stream stream = response.OutputStream)
            {
                stream.Write(buffer, 0, buffer.Length);
            }
        }

        private void Send404(HttpListenerResponse response)
        {
            byte[] buffer = Encoding.UTF8.GetBytes("<h1>404 Not Found</h1>");

            response.StatusCode = 404;
            response.ContentType = "text/html";
            response.ContentLength64 = buffer.Length;

            using (Stream stream = response.OutputStream)
            {
                stream.Write(buffer, 0, buffer.Length);
            }
        }

        private void Send403(HttpListenerResponse response, string message)
        {
            byte[] buffer = Encoding.UTF8.GetBytes("<h1>403 Forbidden</h1><p>" + message + "</p>");

            response.StatusCode = 403;
            response.ContentType = "text/html";
            response.ContentLength64 = buffer.Length;

            using (Stream stream = response.OutputStream)
            {
                stream.Write(buffer, 0, buffer.Length);
            }
        }

        private void SendFile(HttpListenerResponse response, string path)
        {
            using (FileStream fS = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                response.ContentType = WebUtilities.GetContentType(path).MediaType;
                response.ContentLength64 = fS.Length;
                response.AddHeader("Cache-Control", "private, max-age=300");

                using (Stream stream = response.OutputStream)
                {
                    fS.CopyTo(stream);
                }
            }
        }

        private string CreateSession(string username)
        {
            string token = BinaryNumber.GenerateRandomNumber256().ToString();

            if (!_sessions.TryAdd(token, new UserSession(username)))
                throw new DnsWebServiceException("Error while creating session. Please try again.");

            return token;
        }

        private UserSession GetSession(string token)
        {
            if (_sessions.TryGetValue(token, out UserSession session))
                return session;

            return null;
        }

        private UserSession GetSession(HttpListenerRequest request)
        {
            string strToken = request.QueryString["token"];
            if (string.IsNullOrEmpty(strToken))
                throw new DnsWebServiceException("Parameter 'token' missing.");

            return GetSession(strToken);
        }

        private UserSession DeleteSession(string token)
        {
            if (_sessions.TryRemove(token, out UserSession session))
                return session;

            return null;
        }

        private UserSession DeleteSession(HttpListenerRequest request)
        {
            string strToken = request.QueryString["token"];
            if (string.IsNullOrEmpty(strToken))
                throw new DnsWebServiceException("Parameter 'token' missing.");

            return DeleteSession(strToken);
        }

        private void Login(HttpListenerRequest request, JsonTextWriter jsonWriter)
        {
            string strUsername = request.QueryString["user"];
            if (string.IsNullOrEmpty(strUsername))
                throw new DnsWebServiceException("Parameter 'user' missing.");

            string strPassword = request.QueryString["pass"];
            if (string.IsNullOrEmpty(strPassword))
                throw new DnsWebServiceException("Parameter 'pass' missing.");

            strUsername = strUsername.ToLower();
            string strPasswordHash = GetPasswordHash(strUsername, strPassword);

            if (!_credentials.TryGetValue(strUsername, out string passwordHash) || (passwordHash != strPasswordHash))
                throw new DnsWebServiceException("Invalid username or password: " + strUsername);

            _log.Write(GetRequestRemoteEndPoint(request), "[" + strUsername + "] User logged in.");

            string token = CreateSession(strUsername);

            jsonWriter.WritePropertyName("token");
            jsonWriter.WriteValue(token);
        }

        private bool IsSessionValid(HttpListenerRequest request)
        {
            UserSession session = GetSession(request);
            if (session == null)
                return false;

            if (session.HasExpired())
            {
                DeleteSession(request);
                return false;
            }

            session.UpdateLastSeen();
            return true;
        }

        private void ChangePassword(HttpListenerRequest request)
        {
            string strToken = request.QueryString["token"];
            if (string.IsNullOrEmpty(strToken))
                throw new DnsWebServiceException("Parameter 'token' missing.");

            string strPassword = request.QueryString["pass"];
            if (string.IsNullOrEmpty(strPassword))
                throw new DnsWebServiceException("Parameter 'pass' missing.");

            UserSession session = GetSession(strToken);
            if (session == null)
                throw new DnsWebServiceException("User session does not exists.");

            SetCredentials(session.Username, strPassword);
            SaveConfigFile();

            _log.Write(GetRequestRemoteEndPoint(request), "[" + session.Username + "] Password was changed for user.");
        }

        private void Logout(HttpListenerRequest request)
        {
            string strToken = request.QueryString["token"];
            if (string.IsNullOrEmpty(strToken))
                throw new DnsWebServiceException("Parameter 'token' missing.");

            UserSession session = DeleteSession(strToken);

            if (session != null)
                _log.Write(GetRequestRemoteEndPoint(request), "[" + session.Username + "] User logged out.");
        }

        public static void CreateUpdateInfo(Stream s, string version, string displayText, string downloadLink)
        {
            BinaryWriter bW = new BinaryWriter(s);

            bW.Write(Encoding.ASCII.GetBytes("DU")); //format
            bW.Write((byte)2); //version

            bW.WriteShortString(version);
            bW.WriteShortString(displayText);
            bW.WriteShortString(downloadLink);
        }

        private void CheckForUpdate(HttpListenerRequest request, JsonTextWriter jsonWriter)
        {
            string updateVersion = null;
            string displayText = null;
            string downloadLink = null;

            bool updateAvailable = false;

            if (_updateCheckUri != null)
            {
                try
                {
                    using (WebClientEx wc = new WebClientEx())
                    {
                        wc.Proxy = _dnsServer.Proxy;

                        byte[] response = wc.DownloadData(_updateCheckUri);

                        using (MemoryStream mS = new MemoryStream(response, false))
                        {
                            BinaryReader bR = new BinaryReader(mS);

                            if (Encoding.ASCII.GetString(bR.ReadBytes(2)) != "DU") //format
                                throw new InvalidDataException("DNS Server update info format is invalid.");

                            switch (bR.ReadByte()) //version
                            {
                                case 2:
                                    updateVersion = bR.ReadShortString();
                                    displayText = bR.ReadShortString();
                                    downloadLink = bR.ReadShortString();
                                    break;

                                default:
                                    throw new InvalidDataException("DNS Server update info version not supported.");
                            }
                        }
                    }

                    _log.Write(GetRequestRemoteEndPoint(request), "Check for update was done {updateAvailable: " + updateAvailable + "; updateVersion: " + updateVersion + "; displayText: " + displayText + "; downloadLink: " + downloadLink + ";}");
                }
                catch
                {
                    _log.Write(GetRequestRemoteEndPoint(request), "Check for update was done {updateAvailable: False;}");
                }
            }

            jsonWriter.WritePropertyName("updateAvailable");
            jsonWriter.WriteValue(updateAvailable);

            if (updateAvailable)
            {
                if (!string.IsNullOrEmpty(displayText))
                {
                    jsonWriter.WritePropertyName("displayText");
                    jsonWriter.WriteValue(displayText);
                }

                jsonWriter.WritePropertyName("downloadLink");
                jsonWriter.WriteValue(downloadLink);
            }
        }

        private static bool IsUpdateAvailable(string currentVersion, string updateVersion)
        {
            string[] uVer = updateVersion.Split(new char[] { '.' });
            string[] cVer = currentVersion.Split(new char[] { '.' });

            int x = uVer.Length;
            if (x > cVer.Length)
                x = cVer.Length;

            for (int i = 0; i < x; i++)
            {
                if (Convert.ToInt32(uVer[i]) > Convert.ToInt32(cVer[i]))
                    return true;
                else if (Convert.ToInt32(uVer[i]) < Convert.ToInt32(cVer[i]))
                    return false;
            }

            if (uVer.Length > cVer.Length)
            {
                for (int i = x; i < uVer.Length; i++)
                {
                    if (Convert.ToInt32(uVer[i]) > 0)
                        return true;
                }
            }

            return false;
        }

        private void GetDnsSettings(JsonTextWriter jsonWriter)
        {
            jsonWriter.WritePropertyName("version");
            jsonWriter.WriteValue(_currentVersion);

            jsonWriter.WritePropertyName("serverDomain");
            jsonWriter.WriteValue(_serverDomain);

            jsonWriter.WritePropertyName("webServicePort");
            jsonWriter.WriteValue(_webServicePort);

            jsonWriter.WritePropertyName("preferIPv6");
            jsonWriter.WriteValue(_dnsServer.PreferIPv6);

            jsonWriter.WritePropertyName("logQueries");
            jsonWriter.WriteValue(_dnsServer.QueryLogManager != null);

            jsonWriter.WritePropertyName("allowRecursion");
            jsonWriter.WriteValue(_dnsServer.AllowRecursion);

            jsonWriter.WritePropertyName("allowRecursionOnlyForPrivateNetworks");
            jsonWriter.WriteValue(_dnsServer.AllowRecursionOnlyForPrivateNetworks);

            jsonWriter.WritePropertyName("proxy");
            if (_dnsServer.Proxy == null)
            {
                jsonWriter.WriteNull();
            }
            else
            {
                jsonWriter.WriteStartObject();

                NetProxy proxy = _dnsServer.Proxy;

                jsonWriter.WritePropertyName("type");
                jsonWriter.WriteValue(proxy.Type.ToString());

                jsonWriter.WritePropertyName("address");
                jsonWriter.WriteValue(proxy.Address);

                jsonWriter.WritePropertyName("port");
                jsonWriter.WriteValue(proxy.Port);

                NetworkCredential credential = proxy.Credential;

                if (credential != null)
                {
                    jsonWriter.WritePropertyName("username");
                    jsonWriter.WriteValue(credential.UserName);

                    jsonWriter.WritePropertyName("password");
                    jsonWriter.WriteValue(credential.Password);
                }

                jsonWriter.WriteEndObject();
            }

            jsonWriter.WritePropertyName("forwarders");

            if (_dnsServer.Forwarders == null)
            {
                jsonWriter.WriteNull();
            }
            else
            {
                jsonWriter.WriteStartArray();

                foreach (NameServerAddress forwarder in _dnsServer.Forwarders)
                    jsonWriter.WriteValue(forwarder.OriginalString);

                jsonWriter.WriteEndArray();
            }

            jsonWriter.WritePropertyName("forwarderProtocol");
            jsonWriter.WriteValue(_dnsServer.ForwarderProtocol.ToString());
        }

        private void SetDnsSettings(HttpListenerRequest request, JsonTextWriter jsonWriter)
        {
            string strServerDomain = request.QueryString["serverDomain"];
            if (!string.IsNullOrEmpty(strServerDomain))
            {
                if (_serverDomain != strServerDomain)
                {
                    Zone.ZoneInfo[] zones = _dnsServer.AuthoritativeZoneRoot.ListAuthoritativeZones();

                    foreach (Zone.ZoneInfo zone in zones)
                    {
                        DnsResourceRecord[] dnsResourceRecords = _dnsServer.AuthoritativeZoneRoot.GetAllRecords(zone.ZoneName, DnsResourceRecordType.SOA, false, true);
                        if (dnsResourceRecords.Length > 0)
                        {
                            DnsResourceRecord soaRecord = dnsResourceRecords[0];
                            DnsSOARecord soaRecordData = soaRecord.RDATA as DnsSOARecord;

                            _dnsServer.AuthoritativeZoneRoot.SetRecords(soaRecord.Name, soaRecord.Type, soaRecord.TTLValue, new DnsResourceRecordData[] { new DnsSOARecord(strServerDomain, soaRecordData.ResponsiblePerson, soaRecordData.Serial, soaRecordData.Refresh, soaRecordData.Retry, soaRecordData.Expire, soaRecordData.Minimum) });
                            SaveZoneFile(soaRecord.Name);
                        }
                    }

                    _serverDomain = strServerDomain;
                }
            }

            string strWebServicePort = request.QueryString["webServicePort"];
            if (!string.IsNullOrEmpty(strWebServicePort))
                _webServicePort = int.Parse(strWebServicePort);

            string strPreferIPv6 = request.QueryString["preferIPv6"];
            if (!string.IsNullOrEmpty(strPreferIPv6))
                _dnsServer.PreferIPv6 = bool.Parse(strPreferIPv6);

            string strLogQueries = request.QueryString["logQueries"];
            if (!string.IsNullOrEmpty(strLogQueries))
            {
                if (bool.Parse(strLogQueries))
                    _dnsServer.QueryLogManager = _log;
                else
                    _dnsServer.QueryLogManager = null;
            }

            string strAllowRecursion = request.QueryString["allowRecursion"];
            if (!string.IsNullOrEmpty(strAllowRecursion))
                _dnsServer.AllowRecursion = bool.Parse(strAllowRecursion);

            string strAllowRecursionOnlyForPrivateNetworks = request.QueryString["allowRecursionOnlyForPrivateNetworks"];
            if (!string.IsNullOrEmpty(strAllowRecursionOnlyForPrivateNetworks))
                _dnsServer.AllowRecursionOnlyForPrivateNetworks = bool.Parse(strAllowRecursionOnlyForPrivateNetworks);

            string strProxyType = request.QueryString["proxyType"];
            if (!string.IsNullOrEmpty(strProxyType))
            {
                NetProxyType proxyType = (NetProxyType)Enum.Parse(typeof(NetProxyType), strProxyType, true);
                if (proxyType == NetProxyType.None)
                {
                    _dnsServer.Proxy = null;
                }
                else
                {
                    NetworkCredential credential = null;

                    string strUsername = request.QueryString["proxyUsername"];
                    if (!string.IsNullOrEmpty(strUsername))
                        credential = new NetworkCredential(strUsername, request.QueryString["proxyPassword"]);

                    _dnsServer.Proxy = new NetProxy(proxyType, request.QueryString["proxyAddress"], int.Parse(request.QueryString["proxyPort"]), credential);
                }
            }

            string strForwarders = request.QueryString["forwarders"];
            if (!string.IsNullOrEmpty(strForwarders))
            {
                if (strForwarders == "false")
                {
                    _dnsServer.Forwarders = null;
                }
                else
                {
                    string[] strForwardersList = strForwarders.Split(',');
                    NameServerAddress[] forwarders = new NameServerAddress[strForwardersList.Length];

                    for (int i = 0; i < strForwardersList.Length; i++)
                        forwarders[i] = new NameServerAddress(strForwardersList[i]);

                    _dnsServer.Forwarders = forwarders;
                }
            }

            string strForwarderProtocol = request.QueryString["forwarderProtocol"];
            if (!string.IsNullOrEmpty(strForwarderProtocol))
                _dnsServer.ForwarderProtocol = (DnsClientProtocol)Enum.Parse(typeof(DnsClientProtocol), strForwarderProtocol, true);

            _log.Write(GetRequestRemoteEndPoint(request), "[" + GetSession(request).Username + "] Dns Settings were updated {serverDomain: " + _serverDomain + "; webServicePort: " + _webServicePort + "; preferIPv6: " + _dnsServer.PreferIPv6 + "; logQueries: " + (_dnsServer.QueryLogManager != null) + "; allowRecursion: " + _dnsServer.AllowRecursion + "; allowRecursionOnlyForPrivateNetworks: " + _dnsServer.AllowRecursionOnlyForPrivateNetworks + "; proxyType: " + strProxyType + "; forwarders: " + strForwarders + "; forwarderProtocol: " + strForwarderProtocol + ";}");

            SaveConfigFile();

            GetDnsSettings(jsonWriter);
        }

        private void ListCachedZones(HttpListenerRequest request, JsonTextWriter jsonWriter)
        {
            string domain = request.QueryString["domain"];
            if (domain == null)
                domain = "";

            string direction = request.QueryString["direction"];

            string[] subZones;
            DnsResourceRecord[] records;

            while (true)
            {
                subZones = _dnsServer.CacheZoneRoot.ListSubZones(domain);
                records = _dnsServer.CacheZoneRoot.GetAllRecords(domain, DnsResourceRecordType.ANY, false);

                if (records.Length > 0)
                    break;

                if (subZones.Length != 1)
                    break;

                if (direction == "up")
                {
                    if (domain == "")
                        break;

                    int i = domain.IndexOf('.');
                    if (i < 0)
                        domain = "";
                    else
                        domain = domain.Substring(i + 1);
                }
                else
                {
                    domain = subZones[0] + "." + domain;
                }
            }

            Array.Sort(subZones);

            jsonWriter.WritePropertyName("domain");
            jsonWriter.WriteValue(domain);

            jsonWriter.WritePropertyName("zones");
            jsonWriter.WriteStartArray();

            if (domain != "")
                domain = "." + domain;

            foreach (string subZone in subZones)
                jsonWriter.WriteValue(subZone + domain);

            jsonWriter.WriteEndArray();

            WriteRecordsAsJson(records, jsonWriter);
        }

        private void DeleteCachedZone(HttpListenerRequest request)
        {
            string domain = request.QueryString["domain"];
            if (string.IsNullOrEmpty(domain))
                throw new DnsWebServiceException("Parameter 'domain' missing.");

            _dnsServer.CacheZoneRoot.DeleteZone(domain);

            _log.Write(GetRequestRemoteEndPoint(request), "[" + GetSession(request).Username + "] Cached zone was deleted: " + domain);
        }

        private void ListZones(JsonTextWriter jsonWriter)
        {
            Zone.ZoneInfo[] zones = _dnsServer.AuthoritativeZoneRoot.ListAuthoritativeZones();

            Array.Sort(zones);

            jsonWriter.WritePropertyName("zones");
            jsonWriter.WriteStartArray();

            foreach (Zone.ZoneInfo zone in zones)
            {
                jsonWriter.WriteStartObject();

                jsonWriter.WritePropertyName("zoneName");
                jsonWriter.WriteValue(zone.ZoneName);

                jsonWriter.WritePropertyName("disabled");
                jsonWriter.WriteValue(zone.Disabled);

                jsonWriter.WriteEndObject();
            }

            jsonWriter.WriteEndArray();
        }

        private void CreateZone(HttpListenerRequest request)
        {
            string domain = request.QueryString["domain"];
            if (string.IsNullOrEmpty(domain))
                throw new DnsWebServiceException("Parameter 'domain' missing.");

            if (IPAddress.TryParse(domain, out IPAddress ipAddress))
                domain = (new DnsQuestionRecord(ipAddress, DnsClass.IN)).Name;

            _dnsServer.AuthoritativeZoneRoot.SetRecords(domain, DnsResourceRecordType.SOA, 14400, new DnsResourceRecordData[] { new DnsSOARecord(_serverDomain, "hostmaster." + _serverDomain, uint.Parse(DateTime.UtcNow.ToString("yyyyMMddHH")), 28800, 7200, 604800, 600) });

            _log.Write(GetRequestRemoteEndPoint(request), "[" + GetSession(request).Username + "] Authoritative zone was created: " + domain);

            SaveZoneFile(domain);
        }

        private void DeleteZone(HttpListenerRequest request)
        {
            string domain = request.QueryString["domain"];
            if (string.IsNullOrEmpty(domain))
                throw new DnsWebServiceException("Parameter 'domain' missing.");

            _dnsServer.AuthoritativeZoneRoot.DeleteZone(domain);

            _log.Write(GetRequestRemoteEndPoint(request), "[" + GetSession(request).Username + "] Authoritative zone was deleted: " + domain);

            DeleteZoneFile(domain);
        }

        private void EnableZone(HttpListenerRequest request)
        {
            string domain = request.QueryString["domain"];
            if (string.IsNullOrEmpty(domain))
                throw new DnsWebServiceException("Parameter 'domain' missing.");

            _dnsServer.AuthoritativeZoneRoot.EnableZone(domain);

            _log.Write(GetRequestRemoteEndPoint(request), "[" + GetSession(request).Username + "] Authoritative zone was enabled: " + domain);

            SaveConfigFile();
        }

        private void DisableZone(HttpListenerRequest request)
        {
            string domain = request.QueryString["domain"];
            if (string.IsNullOrEmpty(domain))
                throw new DnsWebServiceException("Parameter 'domain' missing.");

            _dnsServer.AuthoritativeZoneRoot.DisableZone(domain);

            _log.Write(GetRequestRemoteEndPoint(request), "[" + GetSession(request).Username + "] Authoritative zone was disabled: " + domain);

            SaveConfigFile();
        }

        private void AddRecord(HttpListenerRequest request)
        {
            string domain = request.QueryString["domain"];
            if (string.IsNullOrEmpty(domain))
                throw new DnsWebServiceException("Parameter 'domain' missing.");

            string strType = request.QueryString["type"];
            if (string.IsNullOrEmpty(strType))
                throw new DnsWebServiceException("Parameter 'type' missing.");

            DnsResourceRecordType type = (DnsResourceRecordType)Enum.Parse(typeof(DnsResourceRecordType), strType);

            string value = request.QueryString["value"];
            if (string.IsNullOrEmpty(value))
                throw new DnsWebServiceException("Parameter 'value' missing.");

            uint ttl;
            string strTtl = request.QueryString["ttl"];
            if (string.IsNullOrEmpty(strTtl))
                ttl = 3600;
            else
                ttl = uint.Parse(strTtl);

            switch (type)
            {
                case DnsResourceRecordType.A:
                    _dnsServer.AuthoritativeZoneRoot.AddRecord(domain, type, ttl, new DnsARecord(IPAddress.Parse(value)));
                    break;

                case DnsResourceRecordType.AAAA:
                    _dnsServer.AuthoritativeZoneRoot.AddRecord(domain, type, ttl, new DnsAAAARecord(IPAddress.Parse(value)));
                    break;

                case DnsResourceRecordType.MX:
                    {
                        string preference = request.QueryString["preference"];
                        if (string.IsNullOrEmpty(preference))
                            throw new DnsWebServiceException("Parameter 'preference' missing.");

                        _dnsServer.AuthoritativeZoneRoot.AddRecord(domain, type, ttl, new DnsMXRecord(ushort.Parse(preference), value));
                    }
                    break;

                case DnsResourceRecordType.TXT:
                    _dnsServer.AuthoritativeZoneRoot.AddRecord(domain, type, ttl, new DnsTXTRecord(value));
                    break;

                case DnsResourceRecordType.NS:
                    _dnsServer.AuthoritativeZoneRoot.AddRecord(domain, type, ttl, new DnsNSRecord(value));
                    break;

                case DnsResourceRecordType.PTR:
                    _dnsServer.AuthoritativeZoneRoot.SetRecords(domain, type, ttl, new DnsResourceRecordData[] { new DnsPTRRecord(value) });
                    break;

                case DnsResourceRecordType.CNAME:
                    _dnsServer.AuthoritativeZoneRoot.SetRecords(domain, type, ttl, new DnsResourceRecordData[] { new DnsCNAMERecord(value) });
                    break;

                case DnsResourceRecordType.SRV:
                    {
                        string priority = request.QueryString["priority"];
                        if (string.IsNullOrEmpty(priority))
                            throw new DnsWebServiceException("Parameter 'priority' missing.");

                        string weight = request.QueryString["weight"];
                        if (string.IsNullOrEmpty(weight))
                            throw new DnsWebServiceException("Parameter 'weight' missing.");

                        string port = request.QueryString["port"];
                        if (string.IsNullOrEmpty(port))
                            throw new DnsWebServiceException("Parameter 'port' missing.");

                        _dnsServer.AuthoritativeZoneRoot.AddRecord(domain, type, ttl, new DnsSRVRecord(ushort.Parse(priority), ushort.Parse(weight), ushort.Parse(port), value));
                    }
                    break;

                default:
                    throw new DnsWebServiceException("Type not supported for AddRecords().");
            }

            _log.Write(GetRequestRemoteEndPoint(request), "[" + GetSession(request).Username + "] New record was added to authoritative zone {domain: " + domain + "; type: " + type + "; value: " + value + "; ttl: " + ttl + ";}");

            SaveZoneFile(domain);
        }

        private void GetRecords(HttpListenerRequest request, JsonTextWriter jsonWriter)
        {
            string domain = request.QueryString["domain"];
            if (string.IsNullOrEmpty(domain))
                throw new DnsWebServiceException("Parameter 'domain' missing.");

            DnsResourceRecord[] records = _dnsServer.AuthoritativeZoneRoot.GetAllRecords(domain);

            WriteRecordsAsJson(records, jsonWriter);
        }

        private void WriteRecordsAsJson(DnsResourceRecord[] records, JsonTextWriter jsonWriter)
        {
            if (records == null)
            {
                jsonWriter.WritePropertyName("records");
                jsonWriter.WriteStartArray();
                jsonWriter.WriteEndArray();

                return;
            }

            Dictionary<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> groupedByDomainRecords = Zone.GroupRecords(records);

            jsonWriter.WritePropertyName("records");
            jsonWriter.WriteStartArray();

            foreach (KeyValuePair<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> groupedByTypeRecords in groupedByDomainRecords)
            {
                foreach (KeyValuePair<DnsResourceRecordType, List<DnsResourceRecord>> groupedRecords in groupedByTypeRecords.Value)
                {
                    foreach (DnsResourceRecord resourceRecord in groupedRecords.Value)
                    {
                        jsonWriter.WriteStartObject();

                        jsonWriter.WritePropertyName("name");
                        jsonWriter.WriteValue(resourceRecord.Name);

                        jsonWriter.WritePropertyName("type");
                        jsonWriter.WriteValue(resourceRecord.Type.ToString());

                        jsonWriter.WritePropertyName("ttl");
                        jsonWriter.WriteValue(resourceRecord.TTLValue);

                        jsonWriter.WritePropertyName("rData");
                        jsonWriter.WriteStartObject();

                        switch (resourceRecord.Type)
                        {
                            case DnsResourceRecordType.A:
                                {
                                    DnsARecord rdata = (resourceRecord.RDATA as DnsARecord);
                                    if (rdata != null)
                                    {
                                        jsonWriter.WritePropertyName("value");
                                        jsonWriter.WriteValue(rdata.IPAddress);
                                    }
                                }
                                break;

                            case DnsResourceRecordType.AAAA:
                                {
                                    DnsAAAARecord rdata = (resourceRecord.RDATA as DnsAAAARecord);
                                    if (rdata != null)
                                    {
                                        jsonWriter.WritePropertyName("value");
                                        jsonWriter.WriteValue(rdata.IPAddress);
                                    }
                                }
                                break;

                            case DnsResourceRecordType.SOA:
                                {
                                    DnsSOARecord rdata = resourceRecord.RDATA as DnsSOARecord;
                                    if (rdata != null)
                                    {
                                        jsonWriter.WritePropertyName("masterNameServer");
                                        jsonWriter.WriteValue(rdata.MasterNameServer);

                                        jsonWriter.WritePropertyName("responsiblePerson");
                                        jsonWriter.WriteValue(rdata.ResponsiblePerson);

                                        jsonWriter.WritePropertyName("serial");
                                        jsonWriter.WriteValue(rdata.Serial);

                                        jsonWriter.WritePropertyName("refresh");
                                        jsonWriter.WriteValue(rdata.Refresh);

                                        jsonWriter.WritePropertyName("retry");
                                        jsonWriter.WriteValue(rdata.Retry);

                                        jsonWriter.WritePropertyName("expire");
                                        jsonWriter.WriteValue(rdata.Expire);

                                        jsonWriter.WritePropertyName("minimum");
                                        jsonWriter.WriteValue(rdata.Minimum);
                                    }
                                }
                                break;

                            case DnsResourceRecordType.PTR:
                                {
                                    DnsPTRRecord rdata = resourceRecord.RDATA as DnsPTRRecord;
                                    if (rdata != null)
                                    {
                                        jsonWriter.WritePropertyName("value");
                                        jsonWriter.WriteValue(rdata.PTRDomainName);
                                    }
                                }
                                break;

                            case DnsResourceRecordType.MX:
                                {
                                    DnsMXRecord rdata = resourceRecord.RDATA as DnsMXRecord;
                                    if (rdata != null)
                                    {
                                        jsonWriter.WritePropertyName("preference");
                                        jsonWriter.WriteValue(rdata.Preference);

                                        jsonWriter.WritePropertyName("value");
                                        jsonWriter.WriteValue(rdata.Exchange);
                                    }
                                }
                                break;

                            case DnsResourceRecordType.TXT:
                                {
                                    DnsTXTRecord rdata = resourceRecord.RDATA as DnsTXTRecord;
                                    if (rdata != null)
                                    {
                                        jsonWriter.WritePropertyName("value");
                                        jsonWriter.WriteValue(rdata.TXTData);
                                    }
                                }
                                break;

                            case DnsResourceRecordType.NS:
                                {
                                    DnsNSRecord rdata = resourceRecord.RDATA as DnsNSRecord;
                                    if (rdata != null)
                                    {
                                        jsonWriter.WritePropertyName("value");
                                        jsonWriter.WriteValue(rdata.NSDomainName);
                                    }
                                }
                                break;

                            case DnsResourceRecordType.CNAME:
                                {
                                    DnsCNAMERecord rdata = resourceRecord.RDATA as DnsCNAMERecord;
                                    if (rdata != null)
                                    {
                                        jsonWriter.WritePropertyName("value");
                                        jsonWriter.WriteValue(rdata.CNAMEDomainName);
                                    }
                                }
                                break;

                            case DnsResourceRecordType.SRV:
                                {
                                    DnsSRVRecord rdata = resourceRecord.RDATA as DnsSRVRecord;
                                    if (rdata != null)
                                    {
                                        jsonWriter.WritePropertyName("priority");
                                        jsonWriter.WriteValue(rdata.Priority);

                                        jsonWriter.WritePropertyName("weight");
                                        jsonWriter.WriteValue(rdata.Weight);

                                        jsonWriter.WritePropertyName("port");
                                        jsonWriter.WriteValue(rdata.Port);

                                        jsonWriter.WritePropertyName("value");
                                        jsonWriter.WriteValue(rdata.Target);
                                    }
                                }
                                break;

                            default:
                                {
                                    jsonWriter.WritePropertyName("value");

                                    using (MemoryStream mS = new MemoryStream())
                                    {
                                        resourceRecord.RDATA.WriteTo(mS, new List<DnsDomainOffset>());

                                        jsonWriter.WriteValue(Convert.ToBase64String(mS.ToArray()));
                                    }
                                }
                                break;
                        }

                        jsonWriter.WriteEndObject();

                        jsonWriter.WriteEndObject();
                    }
                }
            }

            jsonWriter.WriteEndArray();
        }

        private void DeleteRecord(HttpListenerRequest request)
        {
            string domain = request.QueryString["domain"];
            if (string.IsNullOrEmpty(domain))
                throw new DnsWebServiceException("Parameter 'domain' missing.");

            string strType = request.QueryString["type"];
            if (string.IsNullOrEmpty(strType))
                throw new DnsWebServiceException("Parameter 'type' missing.");

            DnsResourceRecordType type = (DnsResourceRecordType)Enum.Parse(typeof(DnsResourceRecordType), strType);

            string value = request.QueryString["value"];
            if (string.IsNullOrEmpty(value))
                throw new DnsWebServiceException("Parameter 'value' missing.");

            switch (type)
            {
                case DnsResourceRecordType.A:
                    _dnsServer.AuthoritativeZoneRoot.DeleteRecord(domain, type, new DnsARecord(IPAddress.Parse(value)));
                    break;

                case DnsResourceRecordType.AAAA:
                    _dnsServer.AuthoritativeZoneRoot.DeleteRecord(domain, type, new DnsAAAARecord(IPAddress.Parse(value)));
                    break;

                case DnsResourceRecordType.MX:
                    _dnsServer.AuthoritativeZoneRoot.DeleteRecord(domain, type, new DnsMXRecord(0, value));
                    break;

                case DnsResourceRecordType.TXT:
                    _dnsServer.AuthoritativeZoneRoot.DeleteRecord(domain, type, new DnsTXTRecord(value));
                    break;

                case DnsResourceRecordType.NS:
                    _dnsServer.AuthoritativeZoneRoot.DeleteRecord(domain, type, new DnsNSRecord(value));
                    break;

                case DnsResourceRecordType.CNAME:
                case DnsResourceRecordType.PTR:
                    _dnsServer.AuthoritativeZoneRoot.DeleteRecords(domain, type);
                    break;

                case DnsResourceRecordType.SRV:
                    {
                        string port = request.QueryString["port"];
                        if (string.IsNullOrEmpty(port))
                            throw new DnsWebServiceException("Parameter 'port' missing.");

                        _dnsServer.AuthoritativeZoneRoot.DeleteRecord(domain, type, new DnsSRVRecord(0, 0, ushort.Parse(port), value));
                    }
                    break;

                default:
                    throw new DnsWebServiceException("Type not supported for DeleteRecord().");
            }

            _log.Write(GetRequestRemoteEndPoint(request), "[" + GetSession(request).Username + "] Record was deleted from authoritative zone {domain: " + domain + "; type: " + type + "; value: " + value + ";}");

            SaveZoneFile(domain);
        }

        private void UpdateRecord(HttpListenerRequest request)
        {
            string strType = request.QueryString["type"];
            if (string.IsNullOrEmpty(strType))
                throw new DnsWebServiceException("Parameter 'type' missing.");

            DnsResourceRecordType type = (DnsResourceRecordType)Enum.Parse(typeof(DnsResourceRecordType), strType);

            string domain = request.QueryString["domain"];
            if (string.IsNullOrEmpty(domain))
                throw new DnsWebServiceException("Parameter 'domain' missing.");

            string oldDomain = request.QueryString["oldDomain"];
            if (string.IsNullOrEmpty(oldDomain))
                oldDomain = domain;

            string value = request.QueryString["value"];
            string oldValue = request.QueryString["oldValue"];

            uint ttl;
            string strTtl = request.QueryString["ttl"];
            if (string.IsNullOrEmpty(strTtl))
                ttl = 3600;
            else
                ttl = uint.Parse(strTtl);

            switch (type)
            {
                case DnsResourceRecordType.A:
                    _dnsServer.AuthoritativeZoneRoot.UpdateRecord(new DnsResourceRecord(oldDomain, type, DnsClass.IN, 0, new DnsARecord(IPAddress.Parse(oldValue))), new DnsResourceRecord(domain, type, DnsClass.IN, ttl, new DnsARecord(IPAddress.Parse(value))));
                    break;

                case DnsResourceRecordType.AAAA:
                    _dnsServer.AuthoritativeZoneRoot.UpdateRecord(new DnsResourceRecord(oldDomain, type, DnsClass.IN, 0, new DnsAAAARecord(IPAddress.Parse(oldValue))), new DnsResourceRecord(domain, type, DnsClass.IN, ttl, new DnsAAAARecord(IPAddress.Parse(value))));
                    break;

                case DnsResourceRecordType.MX:
                    string preference = request.QueryString["preference"];
                    if (string.IsNullOrEmpty(preference))
                        throw new DnsWebServiceException("Parameter 'preference' missing.");

                    _dnsServer.AuthoritativeZoneRoot.UpdateRecord(new DnsResourceRecord(oldDomain, type, DnsClass.IN, 0, new DnsMXRecord(0, oldValue)), new DnsResourceRecord(domain, type, DnsClass.IN, ttl, new DnsMXRecord(ushort.Parse(preference), value)));
                    break;

                case DnsResourceRecordType.TXT:
                    _dnsServer.AuthoritativeZoneRoot.UpdateRecord(new DnsResourceRecord(oldDomain, type, DnsClass.IN, 0, new DnsTXTRecord(oldValue)), new DnsResourceRecord(domain, type, DnsClass.IN, ttl, new DnsTXTRecord(value)));
                    break;

                case DnsResourceRecordType.NS:
                    _dnsServer.AuthoritativeZoneRoot.UpdateRecord(new DnsResourceRecord(oldDomain, type, DnsClass.IN, 0, new DnsNSRecord(oldValue)), new DnsResourceRecord(domain, type, DnsClass.IN, ttl, new DnsNSRecord(value)));
                    break;

                case DnsResourceRecordType.SOA:
                    {
                        string masterNameServer = request.QueryString["masterNameServer"];
                        if (string.IsNullOrEmpty(masterNameServer))
                            throw new DnsWebServiceException("Parameter 'masterNameServer' missing.");

                        string responsiblePerson = request.QueryString["responsiblePerson"];
                        if (string.IsNullOrEmpty(responsiblePerson))
                            throw new DnsWebServiceException("Parameter 'responsiblePerson' missing.");

                        string serial = request.QueryString["serial"];
                        if (string.IsNullOrEmpty(serial))
                            throw new DnsWebServiceException("Parameter 'serial' missing.");

                        string refresh = request.QueryString["refresh"];
                        if (string.IsNullOrEmpty(refresh))
                            throw new DnsWebServiceException("Parameter 'refresh' missing.");

                        string retry = request.QueryString["retry"];
                        if (string.IsNullOrEmpty(retry))
                            throw new DnsWebServiceException("Parameter 'retry' missing.");

                        string expire = request.QueryString["expire"];
                        if (string.IsNullOrEmpty(expire))
                            throw new DnsWebServiceException("Parameter 'expire' missing.");

                        string minimum = request.QueryString["minimum"];
                        if (string.IsNullOrEmpty(minimum))
                            throw new DnsWebServiceException("Parameter 'minimum' missing.");

                        _dnsServer.AuthoritativeZoneRoot.SetRecords(domain, type, ttl, new DnsResourceRecordData[] { new DnsSOARecord(masterNameServer, responsiblePerson, uint.Parse(serial), uint.Parse(refresh), uint.Parse(retry), uint.Parse(expire), uint.Parse(minimum)) });
                    }
                    break;

                case DnsResourceRecordType.PTR:
                    _dnsServer.AuthoritativeZoneRoot.UpdateRecord(new DnsResourceRecord(oldDomain, type, DnsClass.IN, 0, new DnsPTRRecord(oldValue)), new DnsResourceRecord(domain, type, DnsClass.IN, ttl, new DnsPTRRecord(value)));
                    break;

                case DnsResourceRecordType.CNAME:
                    _dnsServer.AuthoritativeZoneRoot.UpdateRecord(new DnsResourceRecord(oldDomain, type, DnsClass.IN, 0, new DnsCNAMERecord(oldValue)), new DnsResourceRecord(domain, type, DnsClass.IN, ttl, new DnsCNAMERecord(value)));
                    break;

                case DnsResourceRecordType.SRV:
                    {
                        string oldPort = request.QueryString["oldPort"];
                        if (string.IsNullOrEmpty(oldPort))
                            throw new DnsWebServiceException("Parameter 'oldPort' missing.");

                        string priority = request.QueryString["priority"];
                        if (string.IsNullOrEmpty(priority))
                            throw new DnsWebServiceException("Parameter 'priority' missing.");

                        string weight = request.QueryString["weight"];
                        if (string.IsNullOrEmpty(weight))
                            throw new DnsWebServiceException("Parameter 'weight' missing.");

                        string port = request.QueryString["port"];
                        if (string.IsNullOrEmpty(port))
                            throw new DnsWebServiceException("Parameter 'port' missing.");

                        DnsResourceRecord oldRecord = new DnsResourceRecord(oldDomain, type, DnsClass.IN, 0, new DnsSRVRecord(0, 0, ushort.Parse(oldPort), oldValue));
                        DnsResourceRecord newRecord = new DnsResourceRecord(domain, type, DnsClass.IN, ttl, new DnsSRVRecord(ushort.Parse(priority), ushort.Parse(weight), ushort.Parse(port), value));

                        _dnsServer.AuthoritativeZoneRoot.UpdateRecord(oldRecord, newRecord);
                    }
                    break;

                default:
                    throw new DnsWebServiceException("Type not supported for UpdateRecords().");
            }

            _log.Write(GetRequestRemoteEndPoint(request), "[" + GetSession(request).Username + "] Record was updated for authoritative zone {oldDomain: " + oldDomain + "; domain: " + domain + "; type: " + type + "; oldValue: " + oldValue + "; value: " + value + "; ttl: " + ttl + ";}");

            SaveZoneFile(domain);
        }

        private void ResolveQuery(HttpListenerRequest request, JsonTextWriter jsonWriter)
        {
            string server = request.QueryString["server"];
            if (string.IsNullOrEmpty(server))
                throw new DnsWebServiceException("Parameter 'server' missing.");

            if (server.Contains(" "))
                throw new DnsWebServiceException("Invalid parameter 'server' value.");

            string domain = request.QueryString["domain"];
            if (string.IsNullOrEmpty(domain))
                throw new DnsWebServiceException("Parameter 'domain' missing.");

            string strType = request.QueryString["type"];
            if (string.IsNullOrEmpty(strType))
                throw new DnsWebServiceException("Parameter 'type' missing.");

            DnsResourceRecordType type = (DnsResourceRecordType)Enum.Parse(typeof(DnsResourceRecordType), strType);

            string strProtocol = request.QueryString["protocol"];
            if (string.IsNullOrEmpty(strProtocol))
                strProtocol = "Udp";

            bool importRecords = false;
            string strImport = request.QueryString["import"];
            if (!string.IsNullOrEmpty(strImport))
                importRecords = bool.Parse(strImport);

            NetProxy proxy = _dnsServer.Proxy;
            bool preferIPv6 = _dnsServer.PreferIPv6;
            DnsClientProtocol protocol = (DnsClientProtocol)Enum.Parse(typeof(DnsClientProtocol), strProtocol, true);
            const int RETRIES = 2;

            DnsDatagram dnsResponse;

            if (server == "root-servers")
            {
                dnsResponse = DnsClient.ResolveViaRootNameServers(domain, type, new SimpleDnsCache(), proxy, preferIPv6, protocol, RETRIES);
            }
            else
            {
                NameServerAddress nameServer;

                if (server == "this-server")
                {
                    nameServer = new NameServerAddress(_serverDomain, IPAddress.Parse("127.0.0.1"));
                    proxy = null; //no proxy required for this server
                }
                else
                {
                    nameServer = new NameServerAddress(server);

                    if (nameServer.IPEndPoint == null)
                    {
                        if (proxy == null)
                        {
                            if (_dnsServer.AllowRecursion)
                                nameServer.ResolveIPAddress(new NameServerAddress[] { new NameServerAddress(IPAddress.Loopback) }, _dnsServer.Proxy, preferIPv6, DnsClientProtocol.Udp, RETRIES);
                            else
                                nameServer.RecursiveResolveIPAddress(_dnsServer.Cache, _dnsServer.Proxy, preferIPv6, DnsClient.RecursiveResolveDefaultProtocol, RETRIES);
                        }
                    }
                    else
                    {
                        try
                        {
                            if (_dnsServer.AllowRecursion)
                                nameServer.ResolveDomainName(new NameServerAddress[] { new NameServerAddress(IPAddress.Loopback) }, _dnsServer.Proxy, _dnsServer.PreferIPv6, DnsClientProtocol.Udp, RETRIES);
                            else
                                nameServer.RecursiveResolveDomainName(_dnsServer.Cache, _dnsServer.Proxy, _dnsServer.PreferIPv6, DnsClient.RecursiveResolveDefaultProtocol, RETRIES);
                        }
                        catch
                        { }
                    }
                }

                dnsResponse = (new DnsClient(nameServer) { Proxy = proxy, PreferIPv6 = preferIPv6, Protocol = protocol, Retries = RETRIES, ReceiveTimeout = 5000 }).Resolve(domain, type);
            }

            if (importRecords)
            {
                List<DnsResourceRecord> recordsToSet = new List<DnsResourceRecord>();
                bool containsSOARecord = false;

                foreach (DnsResourceRecord record in dnsResponse.Answer)
                {
                    if (record.Name.Equals(domain, StringComparison.CurrentCultureIgnoreCase))
                    {
                        recordsToSet.Add(record);

                        if (record.Type == DnsResourceRecordType.SOA)
                            containsSOARecord = true;
                    }
                }

                if (!containsSOARecord)
                {
                    bool SOARecordExists = false;

                    foreach (Zone.ZoneInfo zone in _dnsServer.AuthoritativeZoneRoot.ListAuthoritativeZones())
                    {
                        if (domain.EndsWith(zone.ZoneName, StringComparison.CurrentCultureIgnoreCase))
                        {
                            SOARecordExists = true;
                            break;
                        }
                    }

                    if (!SOARecordExists)
                        _dnsServer.AuthoritativeZoneRoot.SetRecords(domain, DnsResourceRecordType.SOA, 14400, new DnsResourceRecordData[] { new DnsSOARecord(_serverDomain, "hostmaster." + _serverDomain, uint.Parse(DateTime.UtcNow.ToString("yyyyMMddHH")), 28800, 7200, 604800, 600) });
                }

                _dnsServer.AuthoritativeZoneRoot.SetRecords(recordsToSet);

                _log.Write(GetRequestRemoteEndPoint(request), "[" + GetSession(request).Username + "] DNS Client imported record(s) for authoritative zone {server: " + server + "; domain: " + domain + "; type: " + type + ";}");

                SaveZoneFile(domain);
            }

            jsonWriter.WritePropertyName("result");
            jsonWriter.WriteRawValue(JsonConvert.SerializeObject(dnsResponse, new StringEnumConverter()));
        }

        private void ListLogs(JsonTextWriter jsonWriter)
        {
            string[] logFiles = Directory.GetFiles(_log.LogFolder, "*.log");

            Array.Sort(logFiles);
            Array.Reverse(logFiles);

            jsonWriter.WritePropertyName("logFiles");
            jsonWriter.WriteStartArray();

            foreach (string logFile in logFiles)
            {
                jsonWriter.WriteStartObject();

                jsonWriter.WritePropertyName("fileName");
                jsonWriter.WriteValue(Path.GetFileNameWithoutExtension(logFile));

                jsonWriter.WritePropertyName("size");
                jsonWriter.WriteValue(WebUtilities.GetFormattedSize(new FileInfo(logFile).Length));

                jsonWriter.WriteEndObject();
            }

            jsonWriter.WriteEndArray();
        }

        private void DeleteLog(HttpListenerRequest request)
        {
            string log = request.QueryString["log"];
            if (string.IsNullOrEmpty(log))
                throw new DnsWebServiceException("Parameter 'log' missing.");

            string logFile = Path.Combine(_log.LogFolder, log + ".log");

            if (_log.CurrentLogFile.Equals(logFile, StringComparison.CurrentCultureIgnoreCase))
                _log.DeleteCurrentLogFile();
            else
                File.Delete(logFile);

            _log.Write(GetRequestRemoteEndPoint(request), "[" + GetSession(request).Username + "] Log file was deleted: " + log);
        }

        private void SetCredentials(string username, string password)
        {
            username = username.ToLower();
            string passwordHash = GetPasswordHash(username, password);

            _credentials.AddOrUpdate(username, passwordHash, delegate (string key, string oldValue)
            {
                return passwordHash;
            });
        }

        private void LoadCredentials(string username, string passwordHash)
        {
            username = username.ToLower();

            _credentials.AddOrUpdate(username, passwordHash, delegate (string key, string oldValue)
            {
                return passwordHash;
            });
        }

        private static string GetPasswordHash(string username, string password)
        {
            using (HMAC hmac = new HMACSHA256(Encoding.UTF8.GetBytes(password)))
            {
                return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(username))).Replace("-", "").ToLower();
            }
        }

        private void LoadZoneFiles()
        {
            string[] zoneFiles = Directory.GetFiles(_configFolder, "*.zone");

            if (zoneFiles.Length == 0)
            {
                {
                    _dnsServer.AuthoritativeZoneRoot.SetRecords("localhost", DnsResourceRecordType.SOA, 14400, new DnsResourceRecordData[] { new DnsSOARecord("localhost", "hostmaster.localhost", uint.Parse(DateTime.UtcNow.ToString("yyyyMMddHH")), 28800, 7200, 604800, 600) });
                    _dnsServer.AuthoritativeZoneRoot.SetRecords("localhost", DnsResourceRecordType.A, 3600, new DnsResourceRecordData[] { new DnsARecord(IPAddress.Loopback) });
                    _dnsServer.AuthoritativeZoneRoot.SetRecords("localhost", DnsResourceRecordType.AAAA, 3600, new DnsResourceRecordData[] { new DnsAAAARecord(IPAddress.IPv6Loopback) });

                    SaveZoneFile("localhost");
                }

                {
                    string prtDomain = new DnsQuestionRecord(IPAddress.Loopback, DnsClass.IN).Name;

                    _dnsServer.AuthoritativeZoneRoot.SetRecords(prtDomain, DnsResourceRecordType.SOA, 14400, new DnsResourceRecordData[] { new DnsSOARecord("localhost", "hostmaster.localhost", uint.Parse(DateTime.UtcNow.ToString("yyyyMMddHH")), 28800, 7200, 604800, 600) });
                    _dnsServer.AuthoritativeZoneRoot.SetRecords(prtDomain, DnsResourceRecordType.PTR, 3600, new DnsResourceRecordData[] { new DnsPTRRecord("localhost") });

                    SaveZoneFile(prtDomain);
                }

                {
                    string prtDomain = new DnsQuestionRecord(IPAddress.IPv6Loopback, DnsClass.IN).Name;

                    _dnsServer.AuthoritativeZoneRoot.SetRecords(prtDomain, DnsResourceRecordType.SOA, 14400, new DnsResourceRecordData[] { new DnsSOARecord("localhost", "hostmaster.localhost", uint.Parse(DateTime.UtcNow.ToString("yyyyMMddHH")), 28800, 7200, 604800, 600) });
                    _dnsServer.AuthoritativeZoneRoot.SetRecords(prtDomain, DnsResourceRecordType.PTR, 3600, new DnsResourceRecordData[] { new DnsPTRRecord("localhost") });

                    SaveZoneFile(prtDomain);
                }
            }
            else
            {
                foreach (string zoneFile in zoneFiles)
                    LoadZoneFile(zoneFile);
            }
        }

        private void LoadZoneFile(string zoneFile)
        {
            using (FileStream fS = new FileStream(zoneFile, FileMode.Open, FileAccess.Read))
            {
                BinaryReader bR = new BinaryReader(fS);

                if (Encoding.ASCII.GetString(bR.ReadBytes(2)) != "DZ")
                    throw new InvalidDataException("DnsServer zone file format is invalid.");

                switch (bR.ReadByte())
                {
                    case 2:
                        int count = bR.ReadInt32();
                        DnsResourceRecord[] records = new DnsResourceRecord[count];

                        for (int i = 0; i < count; i++)
                            records[i] = new DnsResourceRecord(fS);

                        _dnsServer.AuthoritativeZoneRoot.SetRecords(records);
                        break;

                    default:
                        throw new InvalidDataException("Dns Zone file version not supported.");
                }
            }

            _log.Write("Loaded zone file: " + zoneFile);
        }

        private void SaveZoneFile(string domain)
        {
            domain = domain.ToLower();
            DnsResourceRecord[] records = _dnsServer.AuthoritativeZoneRoot.GetAllRecords(domain, DnsResourceRecordType.ANY, true, true);

            string authZone = records[0].Name.ToLower();

            using (FileStream fS = new FileStream(Path.Combine(_configFolder, authZone + ".zone"), FileMode.Create, FileAccess.Write))
            {
                BinaryWriter bW = new BinaryWriter(fS);

                bW.Write(Encoding.ASCII.GetBytes("DZ")); //format
                bW.Write((byte)2); //version

                bW.Write(records.Length);

                foreach (DnsResourceRecord record in records)
                    record.WriteTo(fS);
            }

            _log.Write("Saved zone file for domain: " + domain);
        }

        private void DeleteZoneFile(string domain)
        {
            domain = domain.ToLower();

            File.Delete(Path.Combine(_configFolder, domain + ".zone"));

            _log.Write("Deleted zone file for domain: " + domain);
        }

        private void LoadConfigFile()
        {
            string configFile = Path.Combine(_configFolder, "dns.config");

            try
            {
                using (FileStream fS = new FileStream(configFile, FileMode.Open, FileAccess.Read))
                {
                    BinaryReader bR = new BinaryReader(fS);

                    if (Encoding.ASCII.GetString(bR.ReadBytes(2)) != "DS") //format
                        throw new InvalidDataException("DnsServer config file format is invalid.");

                    byte version = bR.ReadByte();
                    switch (version)
                    {
                        case 2:
                        case 3:
                        case 4:
                            _serverDomain = bR.ReadShortString();
                            _webServicePort = bR.ReadInt32();

                            _dnsServer.PreferIPv6 = bR.ReadBoolean();

                            if (bR.ReadBoolean()) //logQueries
                                _dnsServer.QueryLogManager = _log;

                            _dnsServer.AllowRecursion = bR.ReadBoolean();

                            if (version >= 4)
                                _dnsServer.AllowRecursionOnlyForPrivateNetworks = bR.ReadBoolean();
                            else
                                _dnsServer.AllowRecursionOnlyForPrivateNetworks = true; //default true for security reasons

                            NetProxyType proxyType = (NetProxyType)bR.ReadByte();
                            if (proxyType != NetProxyType.None)
                            {
                                string address = bR.ReadShortString();
                                int port = bR.ReadInt32();
                                NetworkCredential credential = null;

                                if (bR.ReadBoolean()) //credential set
                                    credential = new NetworkCredential(bR.ReadShortString(), bR.ReadShortString());

                                _dnsServer.Proxy = new NetProxy(proxyType, address, port, credential);
                            }

                            {
                                int count = bR.ReadByte();
                                if (count > 0)
                                {
                                    NameServerAddress[] forwarders = new NameServerAddress[count];

                                    for (int i = 0; i < count; i++)
                                        forwarders[i] = new NameServerAddress(bR);

                                    _dnsServer.Forwarders = forwarders;
                                }
                            }

                            _dnsServer.ForwarderProtocol = (DnsClientProtocol)bR.ReadByte();

                            {
                                int count = bR.ReadByte();
                                if (count > 0)
                                {
                                    if (version > 2)
                                    {
                                        for (int i = 0; i < count; i++)
                                            LoadCredentials(bR.ReadShortString(), bR.ReadShortString());
                                    }
                                    else
                                    {
                                        for (int i = 0; i < count; i++)
                                            SetCredentials(bR.ReadShortString(), bR.ReadShortString());
                                    }
                                }
                            }

                            {
                                int count = bR.ReadInt32();
                                if (count > 0)
                                {
                                    for (int i = 0; i < count; i++)
                                        _dnsServer.AuthoritativeZoneRoot.DisableZone(bR.ReadShortString());
                                }
                            }
                            break;

                        default:
                            throw new InvalidDataException("DnsServer config version not supported.");
                    }
                }

                _log.Write("Dns Server config file was loaded: " + configFile);
            }
            catch (Exception ex)
            {
                _log.Write("Dns Server failed to load config file: " + configFile + "\r\n" + ex.ToString());
                _log.Write("Dns Server is restoring default config file.");

                _serverDomain = Environment.MachineName;
                _webServicePort = 5380;

                SetCredentials("admin", "admin");

                _dnsServer.AllowRecursion = true;
                _dnsServer.AllowRecursionOnlyForPrivateNetworks = true; //default true for security reasons

                SaveConfigFile();
            }
        }

        private void SaveConfigFile()
        {
            string configFile = Path.Combine(_configFolder, "dns.config");

            using (FileStream fS = new FileStream(configFile, FileMode.Create, FileAccess.Write))
            {
                BinaryWriter bW = new BinaryWriter(fS);

                bW.Write(Encoding.ASCII.GetBytes("DS")); //format
                bW.Write((byte)4); //version

                bW.WriteShortString(_serverDomain);
                bW.Write(_webServicePort);

                bW.Write(_dnsServer.PreferIPv6);
                bW.Write((_dnsServer.QueryLogManager != null)); //logQueries
                bW.Write(_dnsServer.AllowRecursion);
                bW.Write(_dnsServer.AllowRecursionOnlyForPrivateNetworks);

                if (_dnsServer.Proxy == null)
                {
                    bW.Write((byte)NetProxyType.None);
                }
                else
                {
                    bW.Write((byte)_dnsServer.Proxy.Type);
                    bW.WriteShortString(_dnsServer.Proxy.Address);
                    bW.Write(_dnsServer.Proxy.Port);

                    NetworkCredential credential = _dnsServer.Proxy.Credential;

                    if (credential == null)
                    {
                        bW.Write(false);
                    }
                    else
                    {
                        bW.Write(true);
                        bW.WriteShortString(credential.UserName);
                        bW.WriteShortString(credential.Password);
                    }
                }

                if (_dnsServer.Forwarders == null)
                {
                    bW.Write((byte)0);
                }
                else
                {
                    bW.Write(Convert.ToByte(_dnsServer.Forwarders.Length));

                    foreach (NameServerAddress forwarder in _dnsServer.Forwarders)
                        forwarder.WriteTo(bW);
                }

                bW.Write((byte)_dnsServer.ForwarderProtocol);

                {
                    bW.Write(Convert.ToByte(_credentials.Count));

                    foreach (KeyValuePair<string, string> credential in _credentials)
                    {
                        bW.WriteShortString(credential.Key);
                        bW.WriteShortString(credential.Value);
                    }
                }

                {
                    List<string> disabledZones = new List<string>();
                    Zone.ZoneInfo[] authoritativeZones = _dnsServer.AuthoritativeZoneRoot.ListAuthoritativeZones();

                    foreach (Zone.ZoneInfo zone in authoritativeZones)
                    {
                        if (zone.Disabled)
                            disabledZones.Add(zone.ZoneName);
                    }

                    bW.Write(disabledZones.Count);
                    foreach (string disabledZone in disabledZones)
                        bW.WriteShortString(disabledZone);
                }
            }

            _log.Write("Dns Server config file was saved: " + configFile);
        }

        #endregion

        #region public

        public void Start()
        {
            if (_state != ServiceState.Stopped)
                return;

            _dnsServer = new DnsServer();
            _dnsServer.LogManager = _log;

            LoadZoneFiles();
            LoadConfigFile();

            _dnsServer.Start();

            try
            {
                _webService = new HttpListener();
                _webService.Prefixes.Add("http://*:" + _webServicePort + "/");
                _webService.Start();
            }
            catch
            {
                _webService = new HttpListener();
                _webService.Prefixes.Add("http://localhost:" + _webServicePort + "/");
                _webService.Prefixes.Add("http://127.0.0.1:" + _webServicePort + "/");
                _webService.Start();
            }

            _webServiceThread = new Thread(AcceptWebRequestAsync);
            _webServiceThread.IsBackground = true;
            _webServiceThread.Start();

            _state = ServiceState.Running;

            _log.Write(new IPEndPoint(IPAddress.Loopback, _webServicePort), "Dns Web Service was started successfully.");
        }

        public void Stop()
        {
            if (_state != ServiceState.Running)
                return;

            _state = ServiceState.Stopping;

            _webService.Stop();
            _dnsServer.Stop();

            _state = ServiceState.Stopped;

            _log.Write(new IPEndPoint(IPAddress.Loopback, _webServicePort), "Dns Web Service was stopped successfully.");
            _log.Dispose();
        }

        #endregion

        #region properties

        public string ConfigFolder
        { get { return _configFolder; } }

        public string ServerDomain
        { get { return _serverDomain; } }

        public int WebServicePort
        { get { return _webServicePort; } }

        #endregion
    }

    public class UserSession
    {
        #region variables

        const int SESSION_TIMEOUT = 30 * 60 * 1000; //30 mins

        readonly string _username;
        DateTime _lastSeen;

        #endregion

        #region constructor

        public UserSession(string username)
        {
            _username = username;
            _lastSeen = DateTime.UtcNow;
        }

        #endregion

        #region public

        public void UpdateLastSeen()
        {
            _lastSeen = DateTime.UtcNow;
        }

        public bool HasExpired()
        {
            return _lastSeen.AddMilliseconds(SESSION_TIMEOUT) < DateTime.UtcNow;
        }

        #endregion

        #region properties

        public string Username
        { get { return _username; } }

        #endregion
    }

    public class DnsWebServiceException : Exception
    {
        #region constructors

        public DnsWebServiceException()
            : base()
        { }

        public DnsWebServiceException(string message)
            : base(message)
        { }

        public DnsWebServiceException(string message, Exception innerException)
            : base(message, innerException)
        { }

        protected DnsWebServiceException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context)
        { }

        #endregion
    }

    public class InvalidTokenDnsWebServiceException : Exception
    {
        #region constructors

        public InvalidTokenDnsWebServiceException()
            : base()
        { }

        public InvalidTokenDnsWebServiceException(string message)
            : base(message)
        { }

        public InvalidTokenDnsWebServiceException(string message, Exception innerException)
            : base(message, innerException)
        { }

        protected InvalidTokenDnsWebServiceException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context)
        { }

        #endregion
    }
}
