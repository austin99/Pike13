using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using Topshelf;
using Pike13Zoom.Blueface;
using Pike13;

namespace Pike13Zoom
{
    // use this to generate credentials.json for sending via gmail https://developers.google.com/gmail/api/quickstart/dotnet
    public class Program
    {
        private List<ZoomData> _zoomClient = new List<ZoomData>();
        static private RestClient _pikeClient = new RestClient(Secret.PikeServer);
        static private dynamic _pikeSchedule;
        private const int _loopIntervalSeconds = 90000;
        static Dictionary<string, string> _scheduleWarnMsg = new Dictionary<string, string>();
        const string LogFile = "Pike13Zoom.log";
        const string InStudioEmailFile = "Pike13InStudioEmail.json";
        public const int _zoomMinsStartGroup = 5;
        public const int _zoomMinsStartAppt = 5;


        static void Main(string[] args)
        {
            try
            {
                HostFactory.Run(x =>
                {
                    x.Service<Program>(s =>
                    {
                        s.ConstructUsing(name => new Program());
                        s.WhenStarted(tc => tc.StartThread());
                        s.WhenStopped(tc => tc.Stop());
                    });
                    x.RunAsLocalSystem();
                    x.SetDescription("This service examines the Pike13 schedule and creates Zoom meetings and sends invites via notes and email beforehand");
                    x.SetDisplayName("Pike13 Zoom Connector");
                    x.SetServiceName("Pike13ZoomConnector");
                });
            }
            catch (Exception e)
            {
                ReportError(e.Message);
            }
        }

        static void GetPikeSchedule()
        {
            try
            {
                _pikeSchedule = null;

                //var meetingListRequest = new RestRequest("/desk/api/v3/reports/event_occurrences/queries", Method.POST);
                var today = DateTime.Now.AddDays(0).ToString("yyyy-MM-dd");
                var tomorrow = DateTime.Now.AddDays(2).ToString("yyyy-MM-dd");
                var query = $"api/v2/desk/event_occurrences?from={today}&to={tomorrow}&state=active&access_token={Secret.PikeToken}";
                var request = new RestRequest(query, Method.GET);

                var response = _pikeClient.Execute(request);
                if (response.ErrorException != null)
                    throw new ApplicationException($"Error querying Pike13 schedule [{request.Resource}]: {response.ErrorException.Message}");
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new ApplicationException($"Error querying Pike13 schedule [{request.Resource}]: {response.StatusCode}");

                _pikeSchedule = JsonConvert.DeserializeObject(response.Content);
            }
            catch (Exception e)
            {
                ReportError(e.Message);
            }
        }

        static private Dictionary<string, bool> _emailedInStudio = new Dictionary<string, bool>();

        static void ProcessInStudioClients()
        {
            // json file listing people who've emailed previously
            var json2 = File.ReadAllText(InStudioEmailFile);
            _emailedInStudio = JsonConvert.DeserializeObject<Dictionary<string, bool>>(json2);

            // make a note of how many people we've already emailed
            var emailCount = _emailedInStudio.Count;

            // loop through schedule
            foreach (var eventOccurance in _pikeSchedule.event_occurrences)
            {
                // look for in studio classes or appointments
                if (!eventOccurance.name.ToString().ToLower().Contains("in studio") && !eventOccurance.name.ToString().ToLower().Contains("in-studio"))
                    continue;

                // get the start time
                var start_at = Convert.ToDateTime(eventOccurance.start_at);

                // is this in the past?
                if (start_at.ToLocalTime() < DateTime.Now)
                {
                    // ignore
                    continue;
                }

                // only want events within 24 hours
                if ((start_at.ToLocalTime() - DateTime.Now).TotalHours > 24)
                {
                    continue;
                }

                // readable event name
                var eventName = $"{eventOccurance.name.ToString()} - {Convert.ToDateTime(eventOccurance.start_at).ToLocalTime().ToString("dd-MM-yyyy HH:mm")} - ";
                var staff = "<unassigned>";
                if (eventOccurance.staff_members != null && eventOccurance.staff_members.Count > 0)
                    staff = eventOccurance.staff_members[0].name.ToString();
                eventName += staff;

                Debug.WriteLine(eventName);

                // look for new signups
                foreach (var person in eventOccurance.people)
                {
                    // can't do anything without email
                    if (person.email == null || string.IsNullOrWhiteSpace(person.email.ToString()))
                    {
                        if (person.id.ToString() != Secret.FitForLifeClientNoEmail1)
                            ReportWarning($"No email for {person.id.ToString()} for {eventName}");

                        continue;
                    }

                    // can't do anything without email
                    if (person.email == null || string.IsNullOrWhiteSpace(person.email.ToString()))
                    {
                        if (person.id.ToString() != "446840")
                            ReportWarning($"No email for {person.id.ToString()} for {eventName}");
                        continue;
                    }

                    // have we emailed them before
                    if (_emailedInStudio.ContainsKey(person.email.ToString()))
                        continue;

                    // have to specifically query the event as we don't want to capture folk who've only reserved their spot
                    if (!QueryPersonStateForEvent(eventOccurance.id.ToString(), person.id.ToString()))
                        continue;

                    var clientEmail = person.email.ToString();

                    // capitalise first letter of first name
                    var name = person.name.ToString();
                    name = name.Split(' ')[0];
                    name = name.Substring(0,1).ToUpper() + name.Substring(1).ToLower();

                    // generate the email
                    var msg = Secret.GenerateCovidNote(name);

                    // send it
                    SendEmail(clientEmail, person.name.ToString(), Secret.EmailAdmin, "Before your first visit back to the studio", msg);

                    // add the client to the sent list
                    _emailedInStudio.Add(clientEmail, true);
                }

            }

            // did we email anyone?
            if (emailCount != _emailedInStudio.Count)
            {
                // update the file
                var json = JsonConvert.SerializeObject(_emailedInStudio, Formatting.Indented);
                File.WriteAllText(InStudioEmailFile, json);
            }
        }

        static bool QueryPersonStateForEvent(string eventId, string personId)
        {
            var query = $"api/v2/desk/event_occurrences/{eventId}?access_token={Secret.PikeToken}";
            var request = new RestRequest(query, Method.GET);

            var response = _pikeClient.Execute(request);
            if (response.ErrorException != null)
                throw new ApplicationException($"Error querying Pike13 event [{request.Resource}]: {response.ErrorException.Message}");
            if (response.StatusCode != HttpStatusCode.OK)
                throw new ApplicationException($"Error querying Pike13 event [{request.Resource}]: {response.StatusCode}");

            dynamic pikeEvent = JsonConvert.DeserializeObject(response.Content);
            if (pikeEvent.event_occurrences[0].id.ToString() != eventId)
            {
                ReportWarning("Expected event {eventId}, instead it is {pikeEvent.event_occurrences[0].id.ToString()}");
                return false;
            }
            if (pikeEvent.event_occurrences[0].state.ToString() != "active")
            {
                ReportWarning($"Expected event {eventId} state to be active, instead it is {pikeEvent.event_occurrences[0].state.ToString()}");
                return false;
            }

            // loop through the people for the event
            var found = false;
            foreach (var person in pikeEvent.event_occurrences[0].visits)
            {
                // do we have a match
                if (person.person_id.ToString() == personId)
                {
                    // are they actually registered/complete - skip if still in checkout/enrollment process
                    if ((person.state == "registered" && person.status == "enrolled")
                        || (person.state == "completed" && person.status == "complete")
                        || (person.state == "completed" && person.status == "unpaid"))
                    {
                        // success
                        return true;
                    }

                    // report a warning, this is generally reserved/reserved
                    ReportWarning($"Person {Secret.PikeServer}/people/{personId} for event {Secret.PikeServer}/e/{eventId} state is {person.state}/{person.status}"); 
                    found = true;
                    break;
                }
            }

            // report a not found warning, if that's what happened
            if (!found)
                ReportWarning($"Didn't find person {Secret.PikeServer}/people/{personId} for event {Secret.PikeServer}/e/{eventId}");

            // fail
            return false;
        }

        static void ReportMessage(string message, bool sendEmail = false)
        {
            Debug.WriteLine(DateTime.Now + " " + message);
            Console.WriteLine(DateTime.Now + " " + message);
            File.AppendAllText(LogFile, DateTime.Now + " -I- " + message + "\r\n");
            if (sendEmail)
                SendEmailImpl(Secret.EmailAdmin, Secret.EmailAdmin, null, "Zoom automation", message);
        }

        static Dictionary<string, DateTime> _lastMessages = new Dictionary<string, DateTime>();

        static void ReportWarning(string message, bool sendEmail = true)
        {
            Debug.WriteLine(DateTime.Now + " " + message);
            Console.WriteLine(DateTime.Now + " " + message);
            File.AppendAllText(LogFile, DateTime.Now + " -W- " + message + "\r\n");
            if (sendEmail)
            {
                if (!_lastMessages.ContainsKey(message) || (DateTime.Now - _lastMessages[message]).Hours > 1)
                    SendEmailImpl(Secret.EmailAdmin, Secret.EmailAdmin, null, "Zoom warning", message);

                if (!_lastMessages.ContainsKey(message))
                    _lastMessages.Add(message, DateTime.Now);
                else if ((DateTime.Now - _lastMessages[message]).Hours > 1)
                    _lastMessages[message] = DateTime.Now;
            }
        }

        static void ReportError(string message, bool sendEmail = true)
        {
            Debug.WriteLine(DateTime.Now + " " + message);
            Console.WriteLine(DateTime.Now + " " + message);
            File.AppendAllText(LogFile, DateTime.Now + " -E- " + message + "\r\n");
            if (sendEmail)
            {
                if (!_lastMessages.ContainsKey(message) || (DateTime.Now - _lastMessages[message]).Hours > 1)
                    SendEmailImpl(Secret.EmailAdmin, Secret.EmailAdmin, null, "Zoom error", message);

                if (!_lastMessages.ContainsKey(message))
                    _lastMessages.Add(message, DateTime.Now);
                else if ((DateTime.Now - _lastMessages[message]).Hours > 1)
                    _lastMessages[message] = DateTime.Now;
            }
        }

        static private void SendEmail(string destEmail, string destName, string subject, string message)
        {
            SendEmail(destEmail, destName, null, subject, message);
        }

        static private void SendEmail(string destEmail, string destName, string destBcc, string subject, string message)
        {
            SendEmailImpl(destEmail, destName, destBcc, subject, message);

            ReportMessage($"Email [{subject}] sent to [{destName}]");
        }

        static private void SendEmailImpl(string destEmail, string destName, string destBcc, string subject, string message)
        {
            try
            {
                string[] Scopes = {GmailService.Scope.GmailSend};
                string ApplicationName = "Gmail API .NET";

                UserCredential credential;
                // read the credentials file
                using (FileStream stream = new FileStream(AppDomain.CurrentDomain.BaseDirectory + "credentials.json", FileMode.Open, FileAccess.Read))
                {
                    string path = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                    path = Path.Combine(path, ".credentials/gmail-dotnet-quickstart.json");
                    credential = GoogleWebAuthorizationBroker.AuthorizeAsync(GoogleClientSecrets.Load(stream).Secrets, Scopes, "user", CancellationToken.None, new FileDataStore(path, true)).Result;
                }


                //call your gmail service
                var service = new GmailService(new BaseClientService.Initializer() {HttpClientInitializer = credential, ApplicationName = ApplicationName});
                var msg2 = new Google.Apis.Gmail.v1.Data.Message();

                var msg = new AE.Net.Mail.MailMessage
                {
                    Subject = subject,
                    Body = message,
                    From = new MailAddress(Secret.FromEmail, Secret.FromEmailName)
                };
                msg.ContentType = "text/html";
                msg.To.Add(new MailAddress(destEmail, destName));
                msg.ReplyTo.Add(msg.From); // Bounces without this!!
                if (destBcc != null)
                    msg.Bcc.Add(new MailAddress(destBcc));
                var msgStr = new StringWriter();
                msg.Save(msgStr);
                msg2.Raw = Base64UrlEncode(msgStr.ToString());

                service.Users.Messages.Send(msg2, "me").Execute();
            }
            catch (Exception e)
            {
                ReportError(e.Message, false);
            }
        }

        static private string Base64UrlEncode(string input)
        {
            var inputBytes = Encoding.UTF8.GetBytes(input);
            return Convert.ToBase64String(inputBytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .Replace("=", "");
        }

        static private void SendSms(string dest, string message)
        {
            try
            {
                // setup soap connection                                                                                                
                using (var soapProxy = new SMSAppSOAPProxySoapClient("SMSAppSOAPProxySoap"))
                {
                    var soapAuthHeader = new SoapAuthHeader();
                    soapAuthHeader.Username = Secret.BluefaceUser;
                    soapAuthHeader.Password = Secret.BluefacePwd;

                    if (!soapProxy.IsAliveSecure(soapAuthHeader))
                        throw new Exception("Failed to authenticate blueface");

                    var result = soapProxy.SendSMS(soapAuthHeader, dest, message);
                    if (result != "\"OK\"")
                        ReportWarning($"Blueface returned [{result}] while sending to {dest}");
                    else
                        ReportMessage($"Blueface successfully sent SMS to {dest}", true);
                }
            }
            catch (Exception e)
            {
                ReportError($"Blueface exception [{e.Message}] while sending to {dest}");
            }
        }

        public Program()
        {
            // get all the zoom users
            var zoomUsers = ZoomUser.GetZoomUsers();

            // create a zoomdata for each one
            foreach (var user in zoomUsers)
                _zoomClient.Add(new ZoomData(user));
        }

        void StartThread()
        {
            var thread = new Thread(Start);
            thread.Start();
        }

        void Start()
        {
            Thread.Sleep(5000);
            do
            {
                try
                {
                    var dtNow = DateTime.Now;
                    var nextDailyTestEmail = new DateTime(dtNow.Year, dtNow.Month, dtNow.Day, 23, 0, 0, DateTimeKind.Local);
                    if (nextDailyTestEmail < dtNow)
                        nextDailyTestEmail = nextDailyTestEmail.AddDays(1);

                    ReportMessage("Restarted", true);

                    do
                    {
                        GetPikeSchedule();

                        if (_pikeSchedule == null)
                        {
                            // if there was a fault getting the schedule then sleep and go from the top again - error will have already been reported
                            Thread.Sleep(30000);
                            continue;
                        }

                        ProcessInStudioClients();

                        // process zoom clients
                        foreach (var zoomData in _zoomClient)
                        {
                            zoomData.DoLoop();
                        }

                        // are we to perform the daily note cleanup task?
                        if (DateTime.Now > nextDailyTestEmail)
                        {
                            // delete old notes from pike13 - run this once a day
                            var deleted = Pike13DeleteOldNotes();

                            SendEmail(Secret.EmailAdmin, Secret.EmailAdmin, "Zoom Automation", $"Daily test email [{deleted} notes deleted]");
                            nextDailyTestEmail = nextDailyTestEmail.AddDays(1);
                        }
                    }
                    while (!_terminatedEvent.WaitOne(_loopIntervalSeconds));
                }
                catch (Exception e)
                {
                    ReportError(e.ToString());
                }
            }
            while (!_terminatedEvent.WaitOne(90000));
        }

        ManualResetEvent _terminatedEvent = new ManualResetEvent(false);

        void Stop()
        {
            ReportMessage("Terminating", true);
            _terminatedEvent.Set();
        }

        public class ZoomData
        {
            public void DoLoop()
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(_userId))
                        _userId = GetZoomUser();

                    ProcessPikeSchedule();
                    GetZoomSchedule();
                    CreateZoomSchedule();

                    // check if a class starts less than an hour away and hasn't been notified then notify
                    SendNote();
                }
                catch (Exception e)
                {
                    ReportError(e.Message);
                }
            }

            public ZoomData(ZoomUser zoomUser)
            {
                _zoomUser = zoomUser;
                _minsBeforeStart = _zoomUser._type == ZoomUser.EventType.Group ? _zoomMinsStartGroup : _zoomMinsStartAppt;
            }

            private RestClient _client = new RestClient("https://api.zoom.us/v2");
            private string _userId;
            ZoomUser _zoomUser;
            //private string _token;
            //private string _staff;
            private int _minsBeforeStart;
            //private EventType _type;
            private Dictionary<string, Pike13Zoom> _db = new Dictionary<string, Pike13Zoom>();

            string GetZoomUser()
            {
                var request = new RestRequest("users", Method.GET);
                request.AddHeader("Authorization", _zoomUser._token);

                var response = _client.Execute(request);
                if (response.ErrorException != null)
                    throw new ApplicationException($"Error getting Zoom user [{request.Resource}]: {response.ErrorException.Message}");
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new ApplicationException($"Error getting Zoom user [{request.Resource}]: {response.StatusCode}");

                dynamic jsonResponse = JsonConvert.DeserializeObject(response.Content);

                return jsonResponse.users[0].id;
            }

            void SendNote()
            {
                // loop through the schedule
                foreach (var eventOccurance in _db.Values)
                {
                    // do nothing if note is already post
                    if (eventOccurance._notePosted)
                        continue;

                    if (eventOccurance.p_start_at.ToLocalTime() < DateTime.Now.AddHours(1))
                    {
                        // check if note has already been posted - e.g. this program may have been restarted
                        if (GetNote(eventOccurance.p_id))
                        {
                            var staff = eventOccurance.p_staff;
                            if (string.IsNullOrWhiteSpace(staff))
                                staff = "<unassigned>";

                            eventOccurance._notePosted = true;
                            ReportWarning($"Note already posted - send an email indicating that some recipients may have been missed: {eventOccurance.p_name} with {staff.Split(' ')[0]} at {eventOccurance.p_start_at.ToLocalTime().ToString("dd-MM-yyyy HH:mm")}");
                            continue;
                        }

                        // send note
                        var noteText = GenerateNote(eventOccurance.p_id);
                        PostNote(eventOccurance.p_id, noteText);
                        eventOccurance._notePosted = true;

                        // several hotmail clients have issues with delayed emails from Pike13, so we also email them now rather than solely relying on Pike13
                        foreach (var person in eventOccurance.p_people.Values)
                        {
                            if (person._email.ToLower().Contains("hotmail")) 
                            {
                                SendZoomInviteEmailToClient(eventOccurance.p_id, person._email, person._name);
                            }
                        }

                        // special case handling for Fit for Life
                        if (eventOccurance.p_name.StartsWith("Fit for Life - Online"))
                        {
                            var smsInvite = GenerateNoteSms(eventOccurance.p_id);

                            if (eventOccurance.p_start_at.DayOfWeek == DayOfWeek.Monday || eventOccurance.p_start_at.DayOfWeek == DayOfWeek.Tuesday)
                            {
                                SendSms(Secret.FitForLifeClientSms1, smsInvite);
                                SendSms(Secret.FitForLifeClientSms2, smsInvite);
                            }

                            if (eventOccurance.p_start_at.DayOfWeek == DayOfWeek.Thursday || eventOccurance.p_start_at.DayOfWeek == DayOfWeek.Friday)
                            {
                                SendSms(Secret.FitForLifeClientSms3, smsInvite);
                            }
                        }
                    }
                }
            }

            void GetZoomSchedule()
            {
                // might have multiple pages of upcoming meetings (unlikely though)
                for (var page = 1;; page++)
                {
                    // zoom query
                    var request = new RestRequest($"users/{_userId}/meetings?page_size=100&page_number={Convert.ToInt32(page)}&type=upcoming", Method.GET);
                    request.AddHeader("Authorization", _zoomUser._token);

                    // execute
                    var response = _client.Execute(request);
                    if (response.ErrorException != null)
                        throw new ApplicationException($"Error getting Zoom schedule [{request.Resource}]: {response.ErrorException.Message}");
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new ApplicationException($"Error getting Zoom schedule [{request.Resource}]: {response.StatusCode}");

                    dynamic jsonResponse = JsonConvert.DeserializeObject(response.Content);

                    // loop through resultset
                    foreach (var meeting in jsonResponse.meetings)
                    {
                        // get the start time of the meeting and offset it so matches what should be in Pik13
                        var start_time = Convert.ToDateTime(meeting.start_time).AddMinutes(_minsBeforeStart);

                        // query our in-memory view of Pike13
                        var linq = _db.Where(p => p.Value.p_start_at == start_time).Select(p => p.Key);
                        if (linq.Count() > 1)
                        {
                            // seems to be multiple meetings for the same time...
                            var msg = $"Unable to process what appears to be a schedule clash for:";
                            foreach (var eventId in linq)
                                msg += $" {Secret.PikeServer}/e/{eventId}";
                            ReportError(msg);
                        }

                        // _db["118452705"].p_start_at == start_time
                        foreach (var eventId in linq)  //if (linq.Count() == 1)
                        {
                            // update in case details have changed
                            if (string.IsNullOrEmpty(_db[eventId].z_id))
                            {
                                _db[eventId].z_id = meeting.id;
                                _db[eventId].z_topic = meeting.topic;
                                _db[eventId].z_start_time = Convert.ToDateTime(meeting.start_time);
                                _db[eventId].z_timezone = meeting.timezone;
                                _db[eventId].z_join_url = meeting.join_url;
                                _db[eventId].z_password = _db[eventId].z_start_time.ToLocalTime().AddMinutes(_minsBeforeStart).ToString("ddMMHH");
                                //_db[eventId].z_uuid = meeting.uuid;
                            }
                            else
                            {
                                // check for a mismatch
                                if (_db[eventId].z_id != meeting.id.ToString()
                                    || _db[eventId].z_topic != meeting.topic.ToString()
                                    || _db[eventId].z_start_time != Convert.ToDateTime(meeting.start_time)
                                    || _db[eventId].z_timezone != meeting.timezone.ToString()
                                    || _db[eventId].z_join_url != meeting.join_url.ToString())
                                {
                                    ReportWarning($"Zoom meeting details don't match <br> {JsonConvert.SerializeObject(_db[eventId])} <br> {meeting.ToString()}");
                                }
                            }
                        }
                    }

                    // have we got a second page on the schedule
                    if (page >= (int) jsonResponse.page_count)
                        break;
                }
            }

            void ProcessPikeSchedule()
            {
                // flag all entries as stale
                foreach (var key in _db.Keys)
                    _db[key]._stale = true;

                // loop through the schedule
                foreach (var eventOccurance in _pikeSchedule.event_occurrences)
                {
                    var appointment = false;

                    // skip basi
                    if (eventOccurance.name.ToString() == "BASI Pilates Comprehensive Global Program" || eventOccurance.name.ToString() == "BASI Pilates Matwork Program")
                        continue;

                    if (eventOccurance.name.ToString() == "Studio Cleaning" || eventOccurance.name.ToString() == "Staff Sessions")
                        continue;

                    // skip "in studio" classes
                    if (eventOccurance.name.ToString().ToLower().Contains("in studio")
                        || eventOccurance.name.ToString().ToLower().Contains("in-studio")
                        || eventOccurance.name.ToString().ToLower().Contains("barre"))
                    {
                        continue;
                    }

                    // only interested in group classes & studio classes
                    if (eventOccurance.name.ToString().StartsWith("One to One")
                        || eventOccurance.name.ToString().StartsWith("Initial Assessment")
                        || eventOccurance.name.ToString().StartsWith("Appointment to Meet Instructor"))
                    {
                        // don't do anything if we are dealing with group
                        if (_zoomUser._type == ZoomUser.EventType.Group)
                            continue;

                        if (eventOccurance.staff_members == null || eventOccurance.staff_members.Count == 0)
                        {
                            ReportWarning($"{eventOccurance.name} has an unassigned staff member");
                            continue;
                        }

                        // is it for this staff member?
                        var staff = eventOccurance.staff_members[0].name.ToString();
                        if (_zoomUser._staff != staff)
                            continue;

                        appointment = true;
                    }
                    else if (!eventOccurance.name.ToString().EndsWith("(50 mins)") 
                        && !eventOccurance.name.ToString().EndsWith("(55 mins)")
                        && !eventOccurance.name.ToString().StartsWith("Studio Classes")
                        && !eventOccurance.name.ToString().StartsWith("Duet - Online"))
                    {
                        if (!_scheduleWarnMsg.ContainsKey(eventOccurance.id.ToString()))
                            _scheduleWarnMsg.Add(eventOccurance.id.ToString(), "");

                        var msg = "Unexpected class in Pike13 schedule: " + eventOccurance.name;

                        // problem
                        if (_scheduleWarnMsg[eventOccurance.id.ToString()] != msg)
                            ReportWarning(msg);

                        _scheduleWarnMsg[eventOccurance.id.ToString()] = msg;
                        continue;
                    }

                    if (_zoomUser._type == ZoomUser.EventType.Appointment && !appointment)
                        continue;

                    // get the end time
                    var end_at = Convert.ToDateTime(eventOccurance.end_at);

                    // is this past tense
                    if (end_at.ToLocalTime() < DateTime.Now)
                    {
                        // delete it if it exists in memory
                        RemoveFromSchedule(eventOccurance.id.ToString());

                        // ignore
                        continue;
                    }

                    // if we haven't come across this 
                    if (!_db.ContainsKey(eventOccurance.id.ToString()))
                    {
                        // add it
                        AddToSchedule(eventOccurance);
                    }
                    else
                    {
                        // check if the class/appointment has moved
                        if (_db[eventOccurance.id.ToString()].p_start_at != Convert.ToDateTime(eventOccurance.start_at) ||
                            _db[eventOccurance.id.ToString()].p_end_at != Convert.ToDateTime(eventOccurance.end_at))
                        {
                            // it has, so remove it and add it again
                            RemoveFromSchedule(eventOccurance.id.ToString());

                            AddToSchedule(eventOccurance);
                        }
                        else
                        {
                            _db[eventOccurance.id.ToString()]._stale = false;

                            // look for new signups
                            LookForNewSignups(eventOccurance);
                        }
                    }
                }

                // look for old entries - these are entries scheduled in pike13 and then changed - duplicate the array before iterating to avoid the collection has been modified exception
                foreach (var key in _db.Keys.ToArray())
                {
                    if (_db[key]._stale)
                        RemoveFromSchedule(key, true);
                }
            }

            void AddToSchedule(dynamic eventOccurance)
            {
                var zp = new Pike13Zoom();
                zp.p_id = eventOccurance.id;
                //zp.p_event_id = eventOccurance.event_id;
                //zp.p_service_id = eventOccurance.service_id;
                zp.p_name = eventOccurance.name;
                //zp.p_start_at = start_at.ToLocalTime().ToString();

                var start_at = Convert.ToDateTime(eventOccurance.start_at);
                zp.p_start_at = start_at;
                //zp.p_end_at = eventOccurance.end_at.ToString();
                zp.p_end_at = Convert.ToDateTime(eventOccurance.end_at);

                // loop through enrolled clients
                foreach (var person in eventOccurance.people)
                {
                    // have to specifically query the event as we don't want to capture folk who've only reserved their spot
                    if (!QueryPersonStateForEvent(eventOccurance.id.ToString(), person.id.ToString()))
                        continue;

                    zp.p_people.Add(person.id.ToString(), new Person(person.id.ToString(), person.name.ToString(), person.email.ToString()));
                }

                // extract staff details
                if (eventOccurance.staff_members != null && eventOccurance.staff_members.Count > 0)
                {
                    zp.p_staff = eventOccurance.staff_members[0].name.ToString();
                    zp.p_staffEmail = zp.p_staff.Split(' ')[0].ToLower() + Secret.EmailSuffix;
                }

                _db.Add(eventOccurance.id.ToString(), zp);
            }

            void RemoveFromSchedule(string id, bool staleEntry = false)
            {
                // delete it if it exists in memory
                if (_db.ContainsKey(id))
                {
                    var db = _db[id];

                    var staff = db.p_staff;
                    if (string.IsNullOrWhiteSpace(staff))
                        staff = "<unassigned>";

                    if (!staleEntry)
                        ReportMessage($"Removing old meeting: {db.p_name} with {staff.Split(' ')[0]} at {db.p_start_at.ToLocalTime().ToString("dd-MM-yyyy HH:mm")}");
                    else
                        ReportWarning($"Removing stale meeting: {db.p_name} with {staff.Split(' ')[0]} at {db.p_start_at.ToLocalTime().ToString("dd-MM-yyyy HH:mm")}");

                    // can only send email if we know staff member - and it's group and it's not stale
                    if (_zoomUser._type == ZoomUser.EventType.Group && !staleEntry && !string.IsNullOrWhiteSpace(db.p_staff))
                    {
                        // check who is doing the next class
                        var linq = _db.OrderBy(z => z.Value.p_start_at).Where(p => p.Value.p_start_at.ToLocalTime() > DateTime.Now).Select(p => p.Key);
                        if (linq.Count() > 0)
                        {
                            staff = _db[linq.First()].p_staff;
                            if (string.IsNullOrWhiteSpace(staff))
                                staff = "<unassigned>";
                            var nextUp = $"Next up: {_db[linq.First()].p_name} with {staff.Split(' ')[0]} at {_db[linq.First()].p_start_at.ToLocalTime().ToString("dd-MM-yyyy HH:mm")}";

                            // only fire this email if the staff member that is next up is different or if it's the same staff member but more than 45 mins away
                            if (staff != db.p_staff || _db[linq.First()].p_start_at.ToLocalTime() > DateTime.Now.AddMinutes(45))
                                SendEmail(db.p_staffEmail, db.p_staff, Secret.EmailAdmin, "Zoom sign out reminder", $"Please remember to sign out of Zoom once your class is over<br><br>{nextUp}");
                        }
                        else
                        {
                            SendEmail(db.p_staffEmail, db.p_staff, Secret.EmailAdmin, "Zoom sign out reminder", $"Please remember to sign out of Zoom once your class is over");
                        }
                    }

                    // remove
                    _db.Remove(id);
                }
            }

            void LookForNewSignups(dynamic eventOccurance)
            {
                // flag all clients as stale so we can spot ones that have been removed from the schedule
                foreach (var person in _db[eventOccurance.id.ToString()].p_people)
                    person.Value._stale = true;

                // look for new signups
                foreach (var person in eventOccurance.people)
                {
                    if (!_db[eventOccurance.id.ToString()].p_people.ContainsKey(person.id.ToString()))
                    {
                        // have we messaged this group already
                        if (_db[eventOccurance.id.ToString()]._notePosted)
                        {
                            // have to specifically query the event as we don't want to capture folk who've only reserved their spot
                            if (!QueryPersonStateForEvent(eventOccurance.id.ToString(), person.id.ToString()))
                                continue;

                            // new signup, send an email to the client
                            SendZoomInviteEmailToClient(eventOccurance.id.ToString(), person.email.ToString(), person.name.ToString());
                        }

                        // add the person to the list
                        _db[eventOccurance.id.ToString()].p_people.Add(person.id.ToString(), new Person(person.id.ToString(), person.name.ToString(), person.email.ToString()));
                    }
                    else
                    {
                        // clients still on schedule, flag them as not being stale
                        _db[eventOccurance.id.ToString()].p_people[person.id.ToString()]._stale = false;
                    }
                }

                // remove stale clients - duplicate the list before iterating to avoid the collection has been modified exception
                foreach (var person in new List<Person>(_db[eventOccurance.id.ToString()].p_people.Values))
                {
                    if (person._stale)
                    {
                        ReportWarning($"Removing stale person {Secret.PikeServer}/people/{person._id} from schedule {Secret.PikeServer}/e/{eventOccurance.id}");
                        _db[eventOccurance.id.ToString()].p_people.Remove(person._id);
                    }
                }
            }

            void SendZoomInviteEmailToClient(string eventOccuranceId, string email, string name)
            {
                // new signup, send an email
                var noteText = GenerateNote(eventOccuranceId);
                ReportMessage("Sending email to: " + email, true);

                // remove everything after first ' - '
                var topic = _db[eventOccuranceId].z_topic;
                if (topic.Contains(" - "))
                    topic = topic.Substring(0, topic.IndexOf(" - "));

                topic += " at " + _db[eventOccuranceId].p_start_at.ToLocalTime().ToString("HH:mm") + " today";

                // send an email to the _client
                SendEmail(email, name, topic, noteText);
            }

            void CreateZoomSchedule()
            {
                // loop through the classes
                foreach (var eventOccurance in _db.Values)
                {
                    // is this an old meeting
                    if (eventOccurance.p_end_at.ToLocalTime() < DateTime.Now) // .AddMinutes(-_minsAfterMeetingToDelete))
                    {
                        // old meeting, ignore it
                        continue;
                    }

                    // do we have an associated zoom meeting?
                    if (string.IsNullOrEmpty(eventOccurance.z_id))
                    {
                        // no zoom meeting scheduled - create one

                        var request = new RestRequest($"users/{_userId}/meetings", Method.POST);
                        request.AddHeader("Authorization", _zoomUser._token);

                        // remove crude from topic
                        var topic = eventOccurance.p_name;
                        if (topic.Contains(" - Online"))
                            topic = topic.Replace(" - Online", "");
                        if (topic.Contains(" (50 mins)"))
                            topic = topic.Replace(" (50 mins)", "");
                        if (topic.Contains(" (55 mins)"))
                            topic = topic.Replace(" (55 mins)", "");
                        if (!string.IsNullOrEmpty(eventOccurance.p_staff))
                            topic += " - " + eventOccurance.p_staff.Split(' ')[0];

                        var requestBody = new JObject();
                        requestBody.Add("topic", topic);
                        requestBody.Add("type", 2);
                        requestBody.Add("start_time", eventOccurance.p_start_at.ToLocalTime().AddMinutes(-_minsBeforeStart).ToString("yyyy-MM-ddTHH:mm:ss"));
                        requestBody.Add("duration", (eventOccurance.p_end_at - eventOccurance.p_start_at).TotalMinutes + _minsBeforeStart);
                        requestBody.Add("timezone", Secret.TimeZone);
                        requestBody.Add("password", eventOccurance.p_start_at.ToLocalTime().ToString("ddMMHH")); // _rand.Next(0, 999999).ToString("D6"));

                        var settings = new JObject();
                        settings.Add("host_video", 1);
                        settings.Add("participant_video", 1);
                        settings.Add("waiting_room", 0);
                        //settings.Add("enforce_login", 0);
                        requestBody.Add("settings", settings);

                        request.AddJsonBody(requestBody.ToString());

                        var response = _client.Execute(request);
                        if (response.ErrorException != null)
                            throw new ApplicationException($"Error creating Zoom meeting [{request.Resource}]: {response.ErrorException.Message}");
                        if (response.StatusCode != HttpStatusCode.Created)
                            throw new ApplicationException($"Error creating Zoom meeting [{request.Resource}]: {response.StatusCode}");

                        dynamic jsonResponse = JsonConvert.DeserializeObject(response.Content);

                        // store the zoom meeting details in memory
                        eventOccurance.z_id = jsonResponse.id;
                        eventOccurance.z_topic = jsonResponse.topic;
                        eventOccurance.z_start_time = Convert.ToDateTime(jsonResponse.start_time);
                        eventOccurance.z_timezone = jsonResponse.timezone;
                        eventOccurance.z_join_url = jsonResponse.join_url;
                        eventOccurance.z_password = jsonResponse.password;
                        //groupClass.z_uuid = jsonResponse.uuid;

                        var staff = eventOccurance.p_staff;
                        if (string.IsNullOrWhiteSpace(staff))
                            staff = "<unassigned>";

                        ReportMessage($"Created Zoom meeting for {eventOccurance.p_name} with {staff.Split(' ')[0]} at {eventOccurance.z_start_time.ToLocalTime().ToString("dd-MM-yyyy HH:mm")}");
                    }
                }
            }

            void DeleteFromZoomSchedule(string zoom_meeting_id)
            {
                var request = new RestRequest($"meetings/{zoom_meeting_id}", Method.DELETE);
                request.AddHeader("Authorization", _zoomUser._token);

                var response = _client.Execute(request);
                if (response.ErrorException != null)
                    throw new ApplicationException($"Error deleting Zoom meeting [{request.Resource}]: {response.ErrorException.Message}");
                if (response.StatusCode != HttpStatusCode.NoContent)
                    throw new ApplicationException($"Error deleting Zoom meeting [{request.Resource}]: {response.StatusCode}");

                dynamic jsonResponse = JsonConvert.DeserializeObject(response.Content);

                ReportMessage($"Deleted Zoom meeting id {zoom_meeting_id} for {_zoomUser._staff}");
            }

            bool GetNote(string id)
            {
                var query = $"api/v2/desk/event_occurrences/{id}/notes?access_token={Secret.PikeToken}";
                var request = new RestRequest(query, Method.GET);

                var response = _pikeClient.Execute(request);
                if (response.ErrorException != null)
                    throw new ApplicationException($"Error retrieving Pike13 notes [{request.Resource}]: {response.ErrorException.Message}");
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new ApplicationException($"Error retrieving Pike13 notes [{request.Resource}]: {response.StatusCode}");

                dynamic jsonResponse = JsonConvert.DeserializeObject(response.Content);

                foreach (var note in jsonResponse.notes)
                {
                    if (note.note.ToString().Contains(_db[id].z_join_url.ToString()))
                        return true;
                }

                return false;
            }

            string GenerateNote(string id)
            {
                // insert spaces into meeting id
                //var meetingId = _db[id].z_id.Substring(0, 3) + " " + _db[id].z_id.Substring(3, 3) + " " + _db[id].z_id.Substring(6, 3);
                var meetingId = _db[id].z_id;

                // remove everything after first ' - '
                var topic = _db[id].z_topic;
                if (topic.Contains(" - "))
                    topic = topic.Substring(0, topic.IndexOf(" - "));

                // add instructor name to topic
                if (!string.IsNullOrEmpty(_db[id].p_staff))
                    topic += " with " + _db[id].p_staff.Split(' ')[0];

                var note = new StringBuilder();
                note.AppendLine("Hi there,<br>");
                note.AppendLine("<br>");
                note.AppendLine($"{Secret.BusinessName} is inviting you to a scheduled Zoom meeting.<br>");
                note.AppendLine("<br>");
                note.AppendLine($"Topic: {topic}<br>");
                note.AppendLine($"Time: {_db[id].z_start_time.ToLocalTime().ToString("dd-MM-yyyy HH:mm")}<br>");
                note.AppendLine("<br>");
                note.AppendLine("Join Zoom Meeting<br>");
                note.AppendLine($"<a href=\"{_db[id].z_join_url}\">{_db[id].z_join_url}</a><br>");
                note.AppendLine("<br>");
                note.AppendLine($"Meeting ID: {meetingId}<br>");
                note.AppendLine($"Password: {_db[id].z_password}<br>");
                note.AppendLine("<br>");
                note.AppendLine("If you have difficulty with audio on your device, you can dial in for audio only on +353 1 536 9320.<br>");
                note.AppendLine("<br>");
                note.AppendLine($"We'll be online {_minsBeforeStart} minutes in advance to help ensure your camera is set up and ready to go for the class starts.<br>");
                note.AppendLine("<br>");
                note.AppendLine("Have your mat ready and your camera set up sideways to your mat so we can see you if you do want to use video, you are of course also welcome to not use video and just listen along to our cues.<br>");
                note.AppendLine("<br>");
                note.AppendLine("Here are two links to videos from Zoom, please watch them before class start time if you haven't already.<br>");
                note.AppendLine("<a href=\"https://www.youtube.com/watch?v=hIkCmbvAHQQ\">www.youtube.com/watch?v=hIkCmbvAHQQ</a><br>");
                note.AppendLine("<a href=\"https://www.youtube.com/watch?v=-s76QHshQnY\">www.youtube.com/watch?v=-s76QHshQnY</a><br>");
                note.AppendLine("<br>");
                note.AppendLine("If you have a Lenovo laptop, please also read this<br>");
                note.AppendLine("<a href=\"https://support.zoom.us/hc/en-us/articles/208362326-Video-Not-Working-on-Lenovo-Devices\">support.zoom.us/hc/en-us/articles/208362326-Video-Not-Working-on-Lenovo-Devices</a><br>");
                note.AppendLine("<br>");
                note.AppendLine($"Please note, your registration for these classes is your agreement and acknowledgement that you are choosing to do Pilates in your own chosen space and you are assuming responsibility and liability for being aware of your surroundings and any pre-existing limitations and absolve {Secret.BusinessName} and our teachers of any liability when using this service.<br>");
                note.AppendLine("<br>");
                note.AppendLine("See you for " + (_zoomUser._type == ZoomUser.EventType.Appointment ? "your appointment" : "class") + "!<br>");

                return note.ToString();
            }

            string GenerateNoteSms(string id)
            {
                // insert spaces into meeting id
                //var meetingId = _db[id].z_id.Substring(0, 3) + " " + _db[id].z_id.Substring(3, 3) + " " + _db[id].z_id.Substring(6, 3);
                var meetingId = _db[id].z_id;

                // remove everything after first ' - '
                var topic = _db[id].z_topic;
                if (topic.Contains(" - "))
                    topic = topic.Substring(0, topic.IndexOf(" - "));

                // add instructor name to topic
                if (!string.IsNullOrEmpty(_db[id].p_staff))
                    topic += " with " + _db[id].p_staff.Split(' ')[0];

                var note = new StringBuilder();
                note.AppendLine($"{Secret.BusinessName} is inviting you to a scheduled Zoom meeting.");
                note.AppendLine("");
                note.AppendLine($"Topic: {topic}");
                note.AppendLine($"Time: {_db[id].z_start_time.ToLocalTime().ToString("dd-MM-yyyy HH:mm")}");
                note.AppendLine("");
                note.AppendLine($"Join Zoom Meeting: {_db[id].z_join_url}");
                note.AppendLine("");
                note.AppendLine($"Meeting ID: {meetingId}");
                note.AppendLine($"Password: {_db[id].z_password}");

                return note.ToString();
            }

            void PostNote(string id, string noteText)
            {
                var query = $"api/v2/desk/event_occurrences/{id}/notes?access_token={Secret.PikeToken}";
                var request = new RestRequest(query, Method.POST);

                var requestBody = new JObject();
                var note = new JObject();
                note.Add("note", noteText);
#if TEST
                note.Add("public", "false"); // false = private note
#else
                note.Add("public", "true"); // public note
#endif
                requestBody.Add("note", note);

                request.AddJsonBody(requestBody.ToString());

                var response = _pikeClient.Execute(request);
                if (response.ErrorException != null)
                    throw new ApplicationException($"Error posting Pike13 note [{request.Resource}]: {response.ErrorException.Message}");
                if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Created)
                    throw new ApplicationException($"Error posting Pike13 note [{request.Resource}]: {response.StatusCode}");

                dynamic jsonResponse = JsonConvert.DeserializeObject(response.Content);

                _db[id]._notePosted = true;

                var staff = _db[id].p_staff;
                if (string.IsNullOrWhiteSpace(staff))
                    staff = "<unassigned>";

                if (_zoomUser._type == ZoomUser.EventType.Group)
                    ReportMessage($"Posted Zoom invite note for {_db[id].p_name} with {staff.Split(' ')[0]} at {_db[id].p_start_at.ToLocalTime().ToString("dd-MM-yyyy HH:mm")} - {_db[id].p_people.Count} clients", true);

                // appointment 
                if (_zoomUser._type == ZoomUser.EventType.Appointment)
                {
                    // extra client name
                    var clientName = "<no client>";
                    if (_db[id].p_people.Count > 0)
                        clientName = _db[id].p_people.First().Value._name;

                    // send staff member an email
                    if (!string.IsNullOrWhiteSpace(_db[id].p_staff))
                    {
                        SendEmail(_db[id].p_staffEmail, _db[id].p_staff, Secret.EmailAdmin, "Zoom invite sent",
                            $"Posted Zoom invite note for {_db[id].p_name} with {clientName} at {_db[id].p_start_at.ToLocalTime().ToString("dd-MM-yyyy HH:mm")}");
                    }
                    else
                    {
                        // no email for staff member
                        if (staff.Split(' ')[0] != "446840")
                            ReportWarning($"Posted Zoom invite note for {_db[id].p_name} with {staff.Split(' ')[0]} at {_db[id].p_start_at.ToLocalTime().ToString("dd-MM-yyyy HH:mm")} - {clientName} - no staff email");
                    }
                }
            }

            class Pike13Zoom
            {
                public bool _notePosted = false;
                public bool _stale = false;

                public string z_id;
                public string z_topic;
                public DateTime z_start_time;
                public string z_timezone;
                public string z_join_url;
                public string z_password;
                //public string z_uuid;

                public string p_id;
                //public string p_event_id;
                //public string p_service_id;
                public string p_name;
                public DateTime p_start_at;
                public DateTime p_end_at;
                public Dictionary<string, Person> p_people = new Dictionary<string, Person>();
                public string p_staff;
                public string p_staffEmail;
            }

            class Person
            {
                public Person(string id, string name, string email)
                {
                    _id = id;
                    _name = name;
                    _email = email;
                    _stale = false;
                }

                public string _id;
                public string _name;
                public string _email;
                public bool _stale;
            }
        }

        static int Pike13DeleteOldNotes()
        {
            var deleted = 0;
            try
            {
                var schedule = Del_GetPikeSchedule();
                foreach (var eventOccurance in schedule.event_occurrences)
                {
                    // only remove notes for classes that are Online and Studio Classes
                    if (!eventOccurance.name.ToString().ToLower().Contains("online") 
                        && !eventOccurance.name.ToString().ToLower().Contains("studio classes"))
                    {
                        Debug.WriteLine($"Skipping {eventOccurance.name.ToString()} - {Convert.ToDateTime(eventOccurance.start_at).ToLocalTime().ToString("yyyy-MM-dd HH:mm")}");
                        continue;
                    }

                    var noteId = GetEventNote(eventOccurance.id.ToString());

                    if (noteId != null)
                    {
                        Debug.WriteLine($"Deleting note for {eventOccurance.name.ToString()} - {Convert.ToDateTime(eventOccurance.start_at).ToLocalTime().ToString("yyyy-MM-dd HH:mm")}");
                        DeleteEventNote(eventOccurance.id.ToString(), noteId);
                        deleted++;
                    }
                    else
                    {
                        Debug.WriteLine($"No note found for {eventOccurance.name.ToString()} - {Convert.ToDateTime(eventOccurance.start_at).ToLocalTime().ToString("yyyy-MM-dd HH:mm")}");
                    }
                }
            }
            catch (Exception ex)
            {
                ReportError(ex.Message);
            }
            return deleted;
        }

        static dynamic Del_GetPikeSchedule()
        {
            var startDate = DateTime.Now.AddDays(-4).ToString("yyyy-MM-dd");
            var endDate = DateTime.Now.AddDays(-2).ToString("yyyy-MM-dd");
            var query = $"api/v2/desk/event_occurrences?from={startDate}&to={endDate}&state=active&access_token={Secret.PikeToken}";
            var request = new RestRequest(query, Method.GET);

            var response = _pikeClient.Execute(request);
            if (response.ErrorException != null)
                throw new ApplicationException($"Error querying Pike13 schedule 2 [{request.Resource}]: {response.ErrorException.Message}");
            if (response.StatusCode != HttpStatusCode.OK)
                throw new ApplicationException($"Error querying Pike13 schedule 2 [{request.Resource}]: {response.StatusCode}");

            return JsonConvert.DeserializeObject(response.Content);
        }

        static string GetEventNote(string id)
        {
            var query = $"api/v2/desk/event_occurrences/{id}/notes?access_token={Secret.PikeToken}";
            var request = new RestRequest(query, Method.GET);

            var response = _pikeClient.Execute(request);
            if (response.ErrorException != null)
                throw new ApplicationException($"Error retrieving Pike13 notes [{request.Resource}]: {response.ErrorException.Message}");
            if (response.StatusCode != HttpStatusCode.OK)
                throw new ApplicationException($"Error retrieving Pike13 notes [{request.Resource}]: {response.StatusCode}");

            dynamic jsonResponse = JsonConvert.DeserializeObject(response.Content);

            foreach (var note in jsonResponse.notes)
            {
                var msg = note.note.ToString();

                if (msg.Contains("inviting you to a scheduled Zoom meeting") && msg.Contains("Meeting ID:"))
                    return note.id.ToString();

                Debug.WriteLine("Non-matching note found for {Secret.PikeServer}/desk/e/{id}/notes");
            }

            return null;
        }

        static void DeleteEventNote(string eventId, string noteId)
        {
            var query = $"api/v2/desk/event_occurrences/{eventId}/notes/{noteId}?access_token={Secret.PikeToken}";
            var request = new RestRequest(query, Method.DELETE);

            var response = _pikeClient.Execute(request);
            if (response.ErrorException != null)
                throw new ApplicationException($"Error deleting Pike13 notes [{request.Resource}]: {response.ErrorException.Message}");
            if (response.StatusCode == HttpStatusCode.NoContent)
                return;

            if (response.StatusCode == HttpStatusCode.Forbidden)
                Debug.WriteLine("Forbidden");
            else
                throw new ApplicationException($"Error deleting Pike13 notes [{request.Resource}]: {response.StatusCode}");
        }
    }
}

