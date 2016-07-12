﻿using Alexitech.Model;
using Alexitech.Scoping;
using HarmonyHub;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using System.Web.Script.Serialization;

namespace Alexitech.Controllers
{
    public class DoController : ValidatingController
    {
        private static object _lock = new object();

        protected override void OnActionExecuted(ActionExecutedContext filterContext)
        {
            Stream req = Request.InputStream;
            req.Seek(0, System.IO.SeekOrigin.Begin);
            string requestBody = new StreamReader(req).ReadToEnd();

            using (Manager m = new Manager())
            {
                var log = new RequestLog()
                {
                    UserID = RouteData.Values["userID"] as int?,
                    IntentName = RouteData.Values["intentName"] as string,
                    RequestBody = requestBody,
                    RequestDate = DateTime.Now
                };
                m.RequestLogs.Add(log);

                m.SaveChanges();
            }

            base.OnActionExecuted(filterContext);
        }

        public ActionResult Index(AlexaRoot root)
        {
            //var input = new JavaScriptSerializer().DeserializeObject(json);

            //root.Request = new AlexaRequest
            //{
            //    Intent = new AlexaIntent { Name = "VolumeUpIntent" },
            //    RequestID = "EdwRequestId.ef136b8d-88cf-4b6b-b2a5-1c3837a99a3a",
            //    Timestamp = "2016-07-10T00:51:09Z",
            //    Type = "IntentRequest"
            //};
            //root.Session = new AlexaSession
            //{
            //    New = true,
            //    SessionID = "SessionId.8afdd470-ef9b-4ce3-a21e-7e06ebb3472b",
            //    User = new AlexaUser { UserID = "amzn1.ask.account.AFP3ZWPOS2BGJR7OWJZ3DHPKMOMNWY4AY66FUR7ILBWANIHQN73QHDOKJGQAVFPBTDLXZA4WRO6FUYCI77PPCCLMNCGYTO77EPRIIKHHRTMDLQODCLF2VSNJ6UEGIA5YFZUCTEN5NFZ7THK2DUECPFNK5KSKHIOSA2KVZBFZBP4B5VCPGMIBTMWI22RQ25XOJWQCKJOYLBR5UIQ" }
            //};
            //root.Version = "1.0";

            bool success = false;
            string speech = null;
            bool inListen = false;
            object card = null;

            if (root == null || root.Request == null || (!IsTest && !IsTimestampValid(root.Request.Timestamp)))
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);

            User user = null;

            var session = root.Session;
            if (session != null)
            {
                if (session.Attributes != null)
                {
                    string v;
                    if (session.Attributes.TryGetValue("inListen", out v) && v == "true")
                        inListen = true;
                }

                if (session.User != null)// && session.User.AccessToken != null)
                {
                    using (var manager = new Manager())
                    {
                        user = manager.Users
                            .FirstOrDefault();//o => o.AlexaToken == session.User.AccessToken);

                        if (user != null)
                        {
                            RouteData.Values["userID"] = (int?)user.ID;

                            if (session.User.UserID != null && user.AlexaUserID != session.User.UserID)
                            {
                                user.AlexaUserID = session.User.UserID;

                                manager.SaveChanges();
                            }
                        }
                    }
                }
            }

            if (user == null)
            {
                var linkResponse = new
                {
                    version = "1.0",
                    sessionAttributes = new
                    {
                        inListen = (inListen ? "true" : "false")
                    },
                    response = new
                    {
                        outputSpeech = new
                        {
                            type = "PlainText",
                            text = "You need to link your Harmony account using the Alexa app"
                        },
                        card = new
                        {
                            type = "LinkAccount"
                        },
                        shouldEndSession = true
                    }
                };

                return Json(linkResponse, JsonRequestBehavior.AllowGet);
            }

            var requestType = root.Request.Type;
            if (requestType != null)
            {
                switch (requestType)
                {
                    case "LaunchRequest":
                        {
                            inListen = true;
                            success = true;
                            speech = "OK, I'm listening. A command list is available in the Alexa app. Say stop listening when you're done.";
                            card = new
                            {
                                type = "Standard",
                                title = "Command List",
                                text = "Start the [name of] activity\nPress the [name of] button\nPress the [name of] button on the [device name]\nStart listening\nStop listening\nRun the [name of] sequence\nTurn everything off\n"
                            };
                        }
                        break;

                    case "IntentRequest":
                        {
                            var intent = root.Request.Intent;
                            if (intent != null)
                            {
                                if (intent.Name != null)
                                {
                                    RouteData.Values["intentName"] = intent.Name;

                                    switch (intent.Name)
                                    {
                                        case "AMAZON.HelpIntent":
                                            success = true;
                                            speech = "Here are some sample phrases. Tell the remote to start the TV activity. Or. Tell the remote to pause. Or. Tell the remote to press the mute button.";
                                            break;

                                        case "ListenStartIntent":
                                            inListen = true;
                                            success = true;
                                            speech = "OK, I'm listening";
                                            break;

                                        case "ListenEndIntent":
                                        case "AMAZON.StopIntent":
                                        case "AMAZON.CancelIntent":
                                        case "AMAZON.NoIntent":
                                            inListen = false;
                                            success = true;
                                            speech = "OK, done listening";
                                            break;

                                        default:
                                            {
                                                var values = (intent.Slots ?? new Dictionary<string, AlexaSlot>())
                                                    .ToDictionary(o => o.Key, o => o.Value.Value);

                                                success = Command(user, intent.Name, values, out speech);
                                                if (inListen)
                                                    speech = success ? "OK" : "Hmm";
                                            }
                                            break;
                                    }
                                }
                                else
                                {
                                    speech = "Error. Missing intent name.";
                                }
                            }
                            else
                            {
                                speech = "Error. Missing intent.";
                            }
                        }
                        break;
                }
            }
            else
            {
                speech = "Error. Missing request type.";
            }

            var response = new
            {
                version = "1.0",
                sessionAttributes = new
                {
                    inListen = (inListen ? "true" : "false")
                },
                response = new
                {
                    outputSpeech = new
                    {
                        type = "PlainText",
                        text = speech ?? "OK"
                    },
                    card = card,
                    shouldEndSession = !inListen
                }
            };

            return Json(response, JsonRequestBehavior.AllowGet);
        }

        private bool IsTimestampValid(string timestamp)
        {
            if (string.IsNullOrEmpty(timestamp))
                return false;

            DateTime d;
            if (!DateTime.TryParse(timestamp, out d))
                return false;

            var diff = DateTime.Now - d;
            if (diff.TotalSeconds > 150.0)
                return false;

            return true;
        }

        public ActionResult Test(string name)
        {
            User user;
            using (Manager m = new Manager())
            {
                user = m.Users.FirstOrDefault();

                //var auth = new HarmonyAuthenticationClient(user.Hostname, 5222);

                //string sessionToken = auth.SwapAuthToken(user.HarmonyToken);
                //if (string.IsNullOrEmpty(sessionToken))
                //{
                //    throw new Exception("Could not swap token on Harmony Hub.");
                //}

                //var r = new HarmonyClient(user.Hostname, 5222, sessionToken);

                //r.GetConfig();

                //return Content("WEE");

                //var values = new Dictionary<string, string>();
                //values["Sequence"] = "hamburger";

                //string speech;
                //Command(user, "SequenceIntent", values, out speech);

                //return Content(speech);

                //var values = new Dictionary<string, string>();
                //values["Button"] = "volume down";

                //string speech;
                //Command(user, "ButtonIntent", values, out speech);
                //Command(user, "ButtonIntent", values, out speech);
                //Command(user, "ButtonIntent", values, out speech);
                //Command(user, "ButtonIntent", values, out speech);

                //return Content(speech);

                //var values = new Dictionary<string, string>();
                //values["Button"] = "mute";
                //values["Device"] = "sony TV";

                //string speech;
                //Command(user, "ButtonOnDeviceIntent", values, out speech);

                //return Content(speech);

                //var client = GetHarmonyClient(user);

                //var config = new JavaScriptSerializer().Deserialize<HarmonyConfigResult>(client.Config);

                //var sequence = config.sequence
                //    .OrderBy(o =>
                //    {
                //        var label = (o.name ?? "")
                //            .ToLower();

                //        return Distance(name, label);
                //    })
                //    .FirstOrDefault();

                //if (sequence != null)
                //{
                //    int id;
                //    if (int.TryParse(sequence.id, out id))
                //    {
                //        client.Sequence(id);
                //    }
                //}
            }

            if (user == null)
            {
                var linkResponse = new
                {
                    version = "1.0",
                    sessionAttributes = new
                    {
                        inListen = "false"
                    },
                    response = new
                    {
                        outputSpeech = new
                        {
                            type = "PlainText",
                            text = "Error while testing, unable to find account."
                        },
                        card = new
                        {
                            type = "LinkAccount"
                        },
                        shouldEndSession = true
                    }
                };

                return Json(linkResponse, JsonRequestBehavior.AllowGet);
            }

            var root = new AlexaRoot();
            root.Request = new AlexaRequest
            {
                Intent = new AlexaIntent
                {
                    Name = "ButtonIntent",
                    Slots = new Dictionary<string, AlexaSlot>
                    {
                        {
                            "Button",
                            new AlexaSlot
                            {
                                Name = "button", Value = "play"
                            }
                        }
                    }
                },
                RequestID = "EdwRequestId.ef136b8d-88cf-4b6b-b2a5-1c3837a99a3a",
                Timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ"), // "2016-07-10T00:51:09Z",
                Type = "IntentRequest"
            };
            root.Session = new AlexaSession
            {
                New = true,
                SessionID = "SessionId.8afdd470-ef9b-4ce3-a21e-7e06ebb3472b",
                User = new AlexaUser { UserID = "amzn1.ask.account.AFP3ZWPOS2BGJR7OWJZ3DHPKMOMNWY4AY66FUR7ILBWANIHQN73QHDOKJGQAVFPBTDLXZA4WRO6FUYCI77PPCCLMNCGYTO77EPRIIKHHRTMDLQODCLF2VSNJ6UEGIA5YFZUCTEN5NFZ7THK2DUECPFNK5KSKHIOSA2KVZBFZBP4B5VCPGMIBTMWI22RQ25XOJWQCKJOYLBR5UIQ" }
            };
            root.Version = "1.0";

            return Index(root);
        }

        private HarmonyClient GetHarmonyClient(User user)
        {
            return ScopingModule.Application.Ensure<HarmonyClient>(user.ID.ToString(), () =>
            {
                var auth = new HarmonyAuthenticationClient(user.Hostname, 5222);

                string sessionToken = auth.SwapAuthToken(user.HarmonyToken);
                if (string.IsNullOrEmpty(sessionToken))
                {
                    throw new Exception("Could not swap token on Harmony Hub.");
                }

                var r = new HarmonyClient(user.Hostname, 5222, sessionToken);

                r.GetConfig();

                return r;
            });
        }

        private bool Command(User user, string name, Dictionary<string, string> values, out string speech)
        {
            bool success = false;
            int repeats = 1;
            speech = null;

            lock (_lock)
            {
                var client = GetHarmonyClient(user);
                var config = client.Config;
                string s;

                // Shortcuts
                switch (name)
                {
                    case "PlayIntent":
                        name = "ButtonIntent";
                        values["Button"] = "play";
                        speech = "Playing";
                        break;

                    case "PauseIntent":
                        name = "ButtonIntent";
                        values["Button"] = "pause";
                        speech = "Pausing";
                        break;

                    case "VolumeUpIntent":
                        name = "ButtonIntent";
                        values["Button"] = "volume up";
                        speech = "Volume Up";
                        repeats = 8;
                        break;

                    case "VolumeDownIntent":
                        name = "ButtonIntent";
                        values["Button"] = "volume down";
                        speech = "Volume Down";
                        repeats = 8;
                        break;
                }

                // Commands
                switch (name)
                {
                    case "ButtonIntent":
                        {
                            string buttonName = (values.TryGetValue("Button", out s) ? s : "Pause").ToLower();

                            if (config != null && config.activity != null)
                            {
                                client.GetCurrentActivity();

                                if (client.CurrentActivity != null)
                                {
                                    var activity = config.activity
                                        .FirstOrDefault(o => o.id == client.CurrentActivity);

                                    if (activity != null)
                                    {
                                        var buttons = activity.controlGroup
                                            .Where(o => o.function != null)
                                            .SelectMany(o => o.function)
                                            .Where(o => o.action != null)
                                            .ToList();

                                        //var allbuttons = config.activity
                                        //    .Where(o => o.controlGroup != null)
                                        //    .SelectMany(o => o.controlGroup)
                                        //    .Where(o => o.function != null)
                                        //    .SelectMany(o => o.function)
                                        //    .Where(o => o.action != null)
                                        //    .Select(o => o.label)
                                        //    .Concat(config.device
                                        //        .SelectMany(o => o.controlGroup)
                                        //        .Where(o => o.function != null)
                                        //        .SelectMany(o => o.function)
                                        //        .Where(o => o.action != null)
                                        //        .Select(o => o.label)
                                        //    )
                                        //    .Distinct()
                                        //    .OrderBy(o => o);

                                        //var buttonList = string.Join("\r\n", allbuttons);

                                        var button = buttons
                                            .OrderBy(o => {
                                                var label = (o.label ?? "")
                                                    .ToLower()
                                                    .Replace("direction ", "");

                                                return Distance(buttonName, label);
                                            })
                                            .FirstOrDefault();

                                        if (button != null)
                                        {
                                            var action = new JavaScriptSerializer().Deserialize<HarmonyIRCommandAction>(button.action);
                                            if (action != null)
                                            {
                                                for (int i = 0; i < repeats; i++)
                                                {
                                                    client.PressButton(action.deviceId, action.command);
                                                }

                                                speech = speech ?? "Pressing " + button.label;
                                                success = true;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        break;

                    case "ButtonOnDeviceIntent":
                        {
                            string deviceName = (values.TryGetValue("Device", out s) ? s : "").ToLower();
                            string buttonName = (values.TryGetValue("Button", out s) ? s : "Pause").ToLower();

                            if (config != null && config.device != null)
                            {
                                var device = config.device
                                    .OrderBy(o => Distance(buttonName, (o.label ?? "").ToLower()))
                                    .FirstOrDefault();

                                if (device != null)
                                {
                                    var buttons = device.controlGroup
                                        .Where(o => o.function != null)
                                        .SelectMany(o => o.function)
                                        .Where(o => o.action != null)
                                        .ToList();

                                    var button = buttons
                                        .OrderBy(o =>
                                        {
                                            var label = (o.label ?? "")
                                                .ToLower()
                                                .Replace("direction ", "");

                                            return Distance(buttonName, label);
                                        })
                                        .FirstOrDefault();

                                    if (button != null)
                                    {
                                        var action = new JavaScriptSerializer().Deserialize<HarmonyIRCommandAction>(button.action);
                                        if (action != null)
                                        {
                                            client.PressButton(action.deviceId, action.command);
                                            speech = speech ?? "Pressing " + button.label + " on the " + device.label;
                                            success = true;
                                        }
                                    }
                                }
                            }
                        }
                        break;

                    case "ActivityIntent":
                        {
                            string activityName = (values.TryGetValue("Activity", out s) ? s : "TV").ToLower();

                            if (config != null && config.activity != null)
                            {
                                var activity = config.activity
                                    .Where(o => o.id != "-1")
                                    .OrderBy(o => Distance(activityName, (o.label ?? "").ToLower()))
                                    .FirstOrDefault();

                                if (activity != null)
                                {
                                    client.StartActivity(activity.id);
                                    speech = "Starting the " + activity.label + " activity";
                                    success = true;
                                }
                            }
                        }
                        break;

                    case "SequenceIntent":
                        {
                            string sequenceName = (values.TryGetValue("Sequence", out s) ? s : "").ToLower();

                            if (config != null && config.sequence != null)
                            {
                                var sequence = config.sequence
                                    .OrderBy(o => Distance(sequenceName, (o.name ?? "").ToLower()))
                                    .FirstOrDefault();

                                if (sequence != null)
                                {
                                    int sequenceId;
                                    if (int.TryParse(sequence.id, out sequenceId))
                                    {
                                        client.Sequence(sequenceId);
                                        speech = "Running the " + sequence.name + " sequence";
                                        success = true;
                                    }
                                }
                            }
                        }
                        break;

                    case "OffIntent":
                        client.TurnOff();
                        speech = "Powering off";
                        break;
                }
            }

            return success;
        }

        public static int Distance(string s, string t)
        {
            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            // Step 1
            if (n == 0)
            {
                return m;
            }

            if (m == 0)
            {
                return n;
            }

            // Step 2
            for (int i = 0; i <= n; d[i, 0] = i++)
            {
            }

            for (int j = 0; j <= m; d[0, j] = j++)
            {
            }

            // Step 3
            for (int i = 1; i <= n; i++)
            {
                //Step 4
                for (int j = 1; j <= m; j++)
                {
                    // Step 5
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;

                    // Step 6
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1 /* delete */, d[i, j - 1] + 1 /* insert */),
                        d[i - 1, j - 1] + cost /* substitute */);
                }
            }
            // Step 7
            return d[n, m];
        }

        public class AlexaRoot
        {
            public string Version { get; set; }
            public AlexaSession Session { get; set; }
            public AlexaRequest Request { get; set; }
        }

        public class AlexaSession
        {
            public bool New { get; set; }
            public string SessionID { get; set; }
            public AlexaUser User { get; set; }
            public Dictionary<string, string> Attributes { get; set; }
        }

        public class AlexaUser
        {
            public string UserID { get; set; }
            public string AccessToken { get; set; }
        }

        public class AlexaRequest
        {
            public string Type { get; set; }
            public string RequestID { get; set; }
            public string Timestamp { get; set; }
            public AlexaIntent Intent { get; set; }
        }

        public class AlexaIntent
        {
            public string Name { get; set; }
            public Dictionary<string, AlexaSlot> Slots { get; set; }
        }

        public class AlexaSlot
        {
            public string Name { get; set; }
            public string Value { get; set; }
        }
    }
}