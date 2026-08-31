using System;
using System.Net;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.IO.IsolatedStorage;
using My.JDownloader.Api;
using System.Linq;
using My.JDownloader.Api.Models.Devices;
using My.JDownloader.Api.Models.LinkgrabberV2.Request;
using System.Collections.Generic;

namespace ClickNLoad2MyJD
{
    class Program
    {
        private static HttpListener Listener;
        private static DeviceHandler Jdownloader;
        private static bool processedLinks;
        static void Main(string[] args)
        {
            if (!InitializeJdownloaderApi())
            {
                Console.WriteLine("The application will only print the links in terminal window");
            }

            Listener = new HttpListener();
            Listener.Prefixes.Add("http://127.0.0.1:9666/");

            try
            {
                Listener.Start();
                Console.WriteLine("Listening for Click'N'Load requests...");

                Task.Run(() =>
                {
                    while (true)
                    {

                        IAsyncResult result = Listener.BeginGetContext(new AsyncCallback(WebRequestCallback), Listener);
                        while (true)
                        {
                            Thread.Sleep(400);
                            if (processedLinks) break;
                        }
                        processedLinks = false;

                    }
                });

                Console.WriteLine("Press any key to cancel!");
                Console.ReadLine();

                Listener.Close();
            }
            catch (HttpListenerException)
            {
                Console.WriteLine("Seems like another application already running the port 9666, please close this application first.");
            }

        }

        private static bool InitializeJdownloaderApi()
        {
            var credentials = Config.GetOrAskForMyJdownloaderCredentials();

            Console.WriteLine("Try connecting to your MyJDownloader account...");
            var jDownloaderHandler = new JDownloaderHandler(credentials.Mail, credentials.Password, "ClickNListen");
            if (!jDownloaderHandler.IsConnected)
            {
                Console.WriteLine("Connection to MyJDownloader API failed");
                Console.WriteLine("Do you want to reenter your MyJDownloader account credentials? Type 'yes' or 'no'");
                var input = AskForConsoleInputUntilValid(new string[] { "yes", "no" });
                if (input.Equals("yes"))
                {
                    Config.DeleteConfiguration();
                    return InitializeJdownloaderApi();
                }
                return false;
            }

            var devices = jDownloaderHandler.GetDevices().ToArray();
            if (devices.Length == 0)
            {
                Console.WriteLine("Found 0 devices connected to your MyJDownloader account");
                return false;
            }
            else if (devices.Length > 1)
            {
                Console.WriteLine("Found more than one devices connected to your MyJDownloader account. Please enter the number for the device you want to send links to.");
                var validInputs = new List<string>();

                for (int i = 1; i <= devices.Length; i++)
                {
                    Console.WriteLine($"{i} - {devices[i - 1]}");
                    validInputs.Add(i.ToString());
                }

                int.TryParse(AskForConsoleInputUntilValid(validInputs.ToArray()), out var deviceNumber);
                Jdownloader = jDownloaderHandler.GetDeviceHandler(devices[deviceNumber - 1]);
            }
            else
            {
                var device = devices.First();
                Console.WriteLine($"Found one device connected to your MyJDownloader account");
                Jdownloader = jDownloaderHandler.GetDeviceHandler(device);
            }
            Console.WriteLine($"Successfully connected to {Jdownloader.Jd.Device.Name}");
            return true;

        }

        private static string AskForConsoleInputUntilValid(string[] validInputs)
        {
            var input = Console.ReadLine();
            if (validInputs.Contains(input))
            {
                return input;
            }
            else
            {
                Console.WriteLine("Your input was not valid. Please try again.");
                return AskForConsoleInputUntilValid(validInputs);
            }
        }

        private static void WebRequestCallback(IAsyncResult result)
        {
            if (Listener.IsListening)
            {
                HttpListenerContext context = Listener.EndGetContext(result);
                Listener.BeginGetContext(new AsyncCallback(WebRequestCallback), Listener);
                ProcessRequest(context);
            }
        }

        private static void ProcessRequest(HttpListenerContext context)
        {
            // build response data
            HttpListenerResponse response = context.Response;
            string responseString = "";

            response.StatusCode = 200;
            response.Headers.Add("Content-Type: text/html");
            
            // CORS
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Methods"] =
                "GET, POST, PUT, DELETE, OPTIONS, PATCH, HEAD";

            response.Headers["Access-Control-Allow-Headers"] =
                "Content-Type, Authorization, X-Referer, X-Requested-With, Accept, Origin";

            // crossdomain.xml
            if (context.Request.RawUrl == "/crossdomain.xml")
            {
                responseString = "<?xml version=\"1.0\"?>"
                    + "<!DOCTYPE cross-domain-policy SYSTEM \"http://www.macromedia.com/xml/dtds/cross-domain-policy.dtd\">"
                    + "<cross-domain-policy>"
                    + "<allow-access-from domain=\"*\" />"
                    + "</cross-domain-policy>";

            }
            else if (context.Request.RawUrl == "/jdcheck.js")
            {
                responseString = "jdownloader=true; var version='18507';";

            }
            else if (context.Request.RawUrl.StartsWith("/flash"))
            {
                if (context.Request.RawUrl.Contains("add"))
                {
                    System.IO.Stream body = context.Request.InputStream;
                    System.IO.StreamReader reader = new System.IO.StreamReader(body, context.Request.ContentEncoding);

                    String requestBody = System.Web.HttpUtility.UrlDecode(reader.ReadToEnd());

                    string queryString = new System.Uri(context.Request.Url.AbsoluteUri + "?" + requestBody).Query;
                    var queryDictionary = System.Web.HttpUtility.ParseQueryString(queryString);

                    var links = string.Empty;

                    if (context.Request.RawUrl.Contains("addcrypted2"))
                    {
                        // get encrypted data
                        Regex rgxData = new Regex("crypted=(.*?)(&|$)");
                        String data = rgxData.Match(requestBody).Groups[1].ToString();

                        // get encrypted pass
                        Regex rgxPass = new Regex("jk=(.*?){(.*?)}(&|$)");
                        String pass = rgxPass.Match(requestBody).Groups[2].ToString();

                        var jsEngine = new Jurassic.ScriptEngine();
                        pass = jsEngine.Evaluate("(function (){" + pass + "})()").ToString();

                        // show decrypted links
                        links = DecryptLinks(pass, data);
                    }
                    else
                    {
                        links = queryDictionary.Get("urls");
                    }

                    var source = queryDictionary.Get("source");
                    Console.WriteLine($"{Environment.NewLine}");

                    if (Jdownloader != null)
                    {
                        if (links != string.Empty)
                        {
                            var addLinkRequest = new AddLinkRequest()
                            {
                                Links = links.Replace(Environment.NewLine, ";")
                            };
                            if (Jdownloader.LinkgrabberV2.AddLinks(addLinkRequest))
                                Console.WriteLine($"Links from {source} successfully send to {Jdownloader.Jd.Device.Name}");
                        }
                    }
                    if (links != string.Empty)
                    {
                        Console.WriteLine($"Extracted links from {source}:");
                        Console.Write(links);
                        processedLinks = true;
                    }

                    responseString = "success\r\n";
                }
                else
                {
                    responseString = "JDownloader";
                }
            }
            else
            {
                response.StatusCode = 400;
            }

            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;

            // output response
            System.IO.Stream output = response.OutputStream;
            output.Write(buffer, 0, buffer.Length);
            output.Close();
        }

        private static string DecryptLinks(String key, String data)
        {
            // decode key
            key = key.ToUpper();

            String decKey = "";
            for (int i = 0; i < key.Length; i += 2)
            {
                decKey += (char)Convert.ToUInt16(key.Substring(i, 2), 16);
            }

            // decode data
            byte[] dataByte = Convert.FromBase64String(data);

            // decrypt that shit!
            RijndaelManaged rDel = new RijndaelManaged();
            System.Text.ASCIIEncoding aEc = new System.Text.ASCIIEncoding();

            rDel.Key = aEc.GetBytes(decKey);
            rDel.IV = aEc.GetBytes(decKey);
            rDel.Mode = CipherMode.CBC;

            rDel.Padding = PaddingMode.None;
            ICryptoTransform cTransform = rDel.CreateDecryptor();
            byte[] resultArray = cTransform.TransformFinalBlock(dataByte, 0, dataByte.Length);

            String rawLinks = aEc.GetString(resultArray);

            // replace empty paddings
            Regex rgx = new Regex("\u0000+$");
            String cleanLinks = rgx.Replace(rawLinks, "");

            // replace newlines
            rgx = new Regex("\n+");
            cleanLinks = rgx.Replace(cleanLinks, "\r\n");

            return cleanLinks;
        }
    }
}
