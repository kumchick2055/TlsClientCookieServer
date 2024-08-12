using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.SqlServer.Server;
using Newtonsoft.Json;

namespace TlsClientCookieServer
{
    public class CookieJson
    {
        public string domain { get; set; }
        public long expires { get; set; }
        public bool httpOnly { get; set; }
        public string name { get; set; }
        public string path { get; set; }
        public bool secure { get; set; }
        public string value { get; set; }
    }

    public class CookiesListJson
    {
        public IList<CookieJson> cookies { get; set; }
        public string sessionId { get; set; }
    }

    public class CookiesHeaderJson
    {
        public string cookies { get; set; }
        public string url { get; set; }
        public string sessionId { get; set; }

        public class CookieHeaderJson
        {
            public string cookie { get; set; }

            public class CookiesListByUrlJson
            {
                public string sessionId { get; set; }
                public string url { get; set; }
            }

            public class ClearCookieListBySession
            {
                public string sessionId { get; set; }
            }

            public class AllCookiesListJson
            {
                public IList<CookieJson> cookies { get; set; }
            }



            public class Program
            {
                private static Dictionary<Guid, CookieContainer> cookieJar = new Dictionary<Guid, CookieContainer>();

                public static DateTime UnixTimeStampToDateTime(double unixTimeStamp)
                {
                    DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
                    dateTime = dateTime.AddSeconds(unixTimeStamp).ToLocalTime();
                    return dateTime;
                }

                public static IEnumerable<Cookie> GetAllCookies(CookieContainer c)
                {
                    Hashtable k = (Hashtable)c.GetType().GetField("m_domainTable", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(c);
                    foreach (DictionaryEntry element in k)
                    {
                        SortedList l = (SortedList)element.Value.GetType().GetField("m_list", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(element.Value);
                        foreach (var e in l)
                        {
                            var cl = (CookieCollection)((DictionaryEntry)e).Value;
                            foreach (Cookie fc in cl)
                            {
                                yield return fc;
                            }
                        }
                    }
                }

                static async Task SendResponseJson(HttpListenerResponse resp, int code, string data)
                {
                    byte[] buffer = Encoding.UTF8.GetBytes(data);

                    resp.ContentType = "application/json";
                    resp.ContentEncoding = Encoding.UTF8;
                    resp.ContentLength64 = buffer.Length;
                    resp.StatusCode = code;

                    await resp.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                }


                static void Main(string[] args)
                {
                    var httpListener = new HttpListener();
                    httpListener.Prefixes.Add("http://localhost:2505/");
                    httpListener.Start();

                    Console.WriteLine("Cookie Server start on http://localhost:2505/");


                    Task listenTask = HandleIncomingConnections(httpListener);
                    listenTask.GetAwaiter().GetResult();

                    httpListener.Close();
                }


                static async Task HandleIncomingConnections(HttpListener listener)
                {
                    while (true)
                    {
                        // Ожидаем входящее соединение
                        HttpListenerContext ctx = await listener.GetContextAsync();

                        // Получаем запрос и ответ
                        HttpListenerRequest req = ctx.Request;
                        HttpListenerResponse resp = ctx.Response;

                        // Очистить куки
                        if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/api/v1/clear_all_cookies")
                        {
                            Console.WriteLine("Очистка кукисов...");
                            cookieJar.Clear();

                            // Формируем JSON ответ
                            string jsonResponse = "{\"code\": 200, \"detail\": \"success\"}";
                            await SendResponseJson(resp, 200, jsonResponse);
                        }
                        // Очистить куки в определенной сессии
                        else if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/api/v1/clear_cookies_by_sessionid")
                        {
                            try
                            {
                                using (StreamReader reader = new StreamReader(req.InputStream, req.ContentEncoding))
                                {
                                    string requestBody = await reader.ReadToEndAsync();

                                    CookiesListByUrlJson cookiesListJson = JsonConvert.DeserializeObject<CookiesListByUrlJson>(requestBody);
                                    Console.WriteLine(cookiesListJson.sessionId);

                                    Guid sessionId = Guid.Parse(cookiesListJson.sessionId);

                                    if (!cookieJar.ContainsKey(sessionId))
                                    {
                                        string jsonResponse = "{\"code\": 500, \"detail\": \"sessionId not found\"}";
                                        await SendResponseJson(resp, 500, jsonResponse);
                                    }
                                    else
                                    {
                                        cookieJar[sessionId] = new CookieContainer();

                                        string jsonResponse = "{\"status\": 200, \"detail\": \"success\"}";
                                        await SendResponseJson(resp, 200, jsonResponse);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                resp.StatusCode = 500;
                                Console.WriteLine($"{ex.Message}, {ex.StackTrace}");
                            }
                        }
                        // Установить куки в сессии
                        else if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/api/v1/set_cookies_list")
                        {
                            try
                            {
                                using (StreamReader reader = new StreamReader(req.InputStream, req.ContentEncoding))
                                {
                                    string requestBody = await reader.ReadToEndAsync();
                                    CookiesListJson cookiesListJson = JsonConvert.DeserializeObject<CookiesListJson>(requestBody);

                                    Guid sessionId = Guid.Parse(cookiesListJson.sessionId);

                                    if (!cookieJar.ContainsKey(sessionId))
                                    {
                                        cookieJar.Add(sessionId, new CookieContainer());
                                    }
                                    var currentCookieContainer = cookieJar[sessionId];

                                    foreach (var cookie in cookiesListJson.cookies)
                                    {
                                        var currentCookie = new Cookie();
                                        currentCookie.Name = cookie.name;
                                        currentCookie.Value = cookie.value;
                                        currentCookie.Domain = cookie.domain;
                                        currentCookie.Secure = cookie.secure;
                                        currentCookie.Path = cookie.path;
                                        currentCookie.HttpOnly = cookie.httpOnly;
                                        currentCookie.Expires = UnixTimeStampToDateTime(cookie.expires);

                                        Console.WriteLine(currentCookie.Expires);

                                        currentCookieContainer.Add(currentCookie);
                                    }

                                    // Формируем JSON ответ
                                    string jsonResponse = "{\"code\": 200, \"detail\": \"success\"}";
                                    await SendResponseJson(resp, 200, jsonResponse);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.ToString());
                                await SendResponseJson(resp, 500, "{\"error\": \"" + ex.ToString() + "\"}");
                                resp.StatusCode = 500;
                            }
                        }
                        // Установить куки через header
                        else if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/api/v1/set_cookies_by_header")
                        {
                            try
                            {
                                using (StreamReader reader = new StreamReader(req.InputStream, req.ContentEncoding))
                                {
                                    string requestBody = await reader.ReadToEndAsync();
                                    CookiesHeaderJson cookiesListJson = JsonConvert.DeserializeObject<CookiesHeaderJson>(requestBody);

                                    Guid sessionId = Guid.Parse(cookiesListJson.sessionId);

                                    if (!cookieJar.ContainsKey(sessionId))
                                    {
                                        cookieJar.Add(sessionId, new CookieContainer());
                                    }
                                    var currentCookieContainer = cookieJar[sessionId];

                                    Uri url = new Uri(cookiesListJson.url);
                                    currentCookieContainer.SetCookies(url, cookiesListJson.cookies);

                                    // Формируем JSON ответ
                                    string jsonResponse = "{\"code\": 200, \"detail\": \"success\"}";
                                    await SendResponseJson(resp, 200, jsonResponse);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine(ex.ToString());
                                await SendResponseJson(resp, 500, "{\"error\": \"" + ex.ToString() + "\"}");
                                resp.StatusCode = 500;
                            }
                        }
                        // Получить все куки из сессии по URL
                        else if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/api/v1/get_all_cookies_list")
                        {
                            try
                            {
                                using (StreamReader reader = new StreamReader(req.InputStream, req.ContentEncoding))
                                {
                                    string requestBody = await reader.ReadToEndAsync();

                                    CookiesListJson cookiesListJson = JsonConvert.DeserializeObject<CookiesListJson>(requestBody);
                                    Console.WriteLine(cookiesListJson.sessionId);

                                    Guid sessionId = Guid.Parse(cookiesListJson.sessionId);

                                    if (!cookieJar.ContainsKey(sessionId))
                                    {
                                        string jsonResponse = "{\"code\": 500, \"detail\": \"sessionId not found\"}";
                                        await SendResponseJson(resp, 500, jsonResponse);

                                    }
                                    else
                                    {

                                        var currentCookieContainer = cookieJar[sessionId];
                                        var cookieList = GetAllCookies(currentCookieContainer);
                                        var jsonCookieList = new AllCookiesListJson();
                                        jsonCookieList.cookies = new List<CookieJson>();

                                        foreach (var cookie in cookieList)
                                        {
                                            var cookieJson = new CookieJson();
                                            cookieJson.name = cookie.Name;
                                            cookieJson.value = cookie.Value;
                                            cookieJson.secure = cookie.Secure;
                                            cookieJson.path = cookie.Path;
                                            cookieJson.domain = cookie.Domain;
                                            cookieJson.httpOnly = cookie.HttpOnly;
                                            cookieJson.expires = ((DateTimeOffset)cookie.Expires).ToUnixTimeSeconds();

                                            jsonCookieList.cookies.Add(cookieJson);
                                        }

                                        string jsonResponse = JsonConvert.SerializeObject(jsonCookieList);
                                        await SendResponseJson(resp, 200, jsonResponse);

                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                resp.StatusCode = 500;
                                Console.WriteLine($"{ex.Message}, {ex.StackTrace}");
                            }
                        }
                        // Получить куки из сессии по URL
                        else if (req.HttpMethod == "POST" && req.Url.AbsolutePath == "/api/v1/get_cookies_list_by_url")
                        {
                            try
                            {
                                using (StreamReader reader = new StreamReader(req.InputStream, req.ContentEncoding))
                                {
                                    string requestBody = await reader.ReadToEndAsync();

                                    CookiesListByUrlJson cookiesListJson = JsonConvert.DeserializeObject<CookiesListByUrlJson>(requestBody);
                                    Console.WriteLine(cookiesListJson.sessionId);

                                    Guid sessionId = Guid.Parse(cookiesListJson.sessionId);

                                    if (!cookieJar.ContainsKey(sessionId))
                                    {
                                        string jsonResponse = "{\"code\": 500, \"detail\": \"sessionId not found\"}";
                                        await SendResponseJson(resp, 500, jsonResponse);
                                    }
                                    else
                                    {
                                        Uri currentUrl;
                                        try
                                        {
                                            currentUrl = new Uri(cookiesListJson.url);
                                            var currentCookieContainer = cookieJar[sessionId];
                                            var cookieList = currentCookieContainer.GetCookieHeader(currentUrl);

                                            var jsonCookieHeader = new CookieHeaderJson();
                                            jsonCookieHeader.cookie = cookieList;

                                            string jsonResponse = JsonConvert.SerializeObject(jsonCookieHeader);
                                            await SendResponseJson(resp, 200, jsonResponse);
                                        }
                                        catch (Exception ex)
                                        {
                                            string jsonResponse = "{\"status\": 500, \"detail\": \"url is not correct\"}";
                                            await SendResponseJson(resp, 500, jsonResponse);
                                        }

                                    }
                                }

                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"{ex.Message}, {ex.StackTrace}");

                                string jsonResponse = "{\"status\": 500, \"detail\": \"" + ex.Message + "\"}";
                                await SendResponseJson(resp, 500, jsonResponse);
                            }
                        }
                        else
                        {
                            //resp.StatusCode = 404;
                            string jsonResponse = "{\"status\": 404, \"detail\": \"not found\"}";
                            await SendResponseJson(resp, 404, jsonResponse);
                        }

                        // Закрываем поток
                        resp.Close();
                    }
                }
            }
        }
    }
}
