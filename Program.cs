
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Threading;
using static System.Runtime.InteropServices.JavaScript.JSType;


namespace FolderTextMonitor
{
    class Program
    {
        // Simple in-memory cache to avoid reprocessing the same change bursts
        private static readonly ConcurrentDictionary<string, DateTime> _recentlyProcessed = new();
        private const int DebounceMs = 300; // Time window to coalesce rapid events
        private static AppSettings _settings = new();

        private static Dictionary<string, AcknowledgeFile> _acknowledgeFiles = new();

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            try
            {
                _settings = LoadSettings();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Unable to load appsettings.json: {ex.Message}");
                return 1;
            }

            // Parse args: [path] [searchTerm] [--ignoreCase] [--includeSubdirs]
            var (folderPath, searchTerm, filemask, ignoreCase, includeSubdirs) = ParseArgsOrPrompt(args);

            if (!Directory.Exists(folderPath))
            {
                Console.Error.WriteLine($"❌ Folder does not exist: {folderPath}");
                return 1;
            }

            Console.WriteLine("=======================================");
            Console.WriteLine("📂 Folder Text Monitor");
            Console.WriteLine("=======================================");
            Console.WriteLine($"📁 Watching: {folderPath}");
            Console.WriteLine($"🔎 Search term: \"{searchTerm}\"");
            Console.WriteLine($"📄 File Mask: \"{filemask}\"");
            Console.WriteLine($"🔠 Case {(ignoreCase ? "insensitive" : "sensitive")}");
            Console.WriteLine($"🗂️ Include subfolders: {includeSubdirs}");
            Console.WriteLine("=======================================");
            Console.WriteLine();
           
            // Scan existing files at startup
            ScanExistingFiles(folderPath, searchTerm, filemask, ignoreCase, includeSubdirs);

            // Set up the watcher
            using var watcher = new FileSystemWatcher(folderPath, $"*{filemask}")
            {
                IncludeSubdirectories = includeSubdirs,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
            };




            watcher.Created += (_, e) => HandleFileEvent("Created", e.FullPath, searchTerm, filemask, ignoreCase);
            watcher.Changed += (_, e) => HandleFileEvent("Changed", e.FullPath, searchTerm, filemask, ignoreCase);
            watcher.Renamed += (_, e) => HandleFileEvent("Renamed", e.FullPath, searchTerm, filemask, ignoreCase);
            watcher.Deleted += (_, e) => Console.WriteLine($"🗑️ Deleted: {e.FullPath}");

            watcher.Error += (_, e) =>
            {
                string errMsg = $"⚠️ Watcher error: {e.GetException()?.Message}";
                Console.Error.WriteLine(errMsg);
                SendEMail(errMsg);
            };

            watcher.EnableRaisingEvents = true;

            Console.WriteLine("👀 Monitoring started. Press Ctrl+C to exit.");
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n🛑 Stopping...");
                watcher.EnableRaisingEvents = false;
                Environment.Exit(0);
            };

            // Keep the app alive
            while (true)
            {
                Thread.Sleep(1000);
            }
        }

        private static (string folderPath, string searchTerm, string filemask, bool ignoreCase, bool includeSubdirs)
            ParseArgsOrPrompt(string[] args)
        {
            string folderPath = null!;
            string searchTerm = null!;
            string filemask = null!;
            bool ignoreCase = false;
            bool includeSubdirs = false;

            // Basic CLI parsing
            if (args.Length >= 2)
            {
                folderPath = args[0];
                searchTerm = args[1];
                filemask = args[2];
                ignoreCase = args.Any(a => a.Equals("--ignoreCase", StringComparison.OrdinalIgnoreCase));
                includeSubdirs = args.Any(a => a.Equals("--includeSubdirs", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Console.Write("Enter folder path to watch: ");
                folderPath = Console.ReadLine()?.Trim() ?? "";

                Console.Write("Enter string to search for: ");
                searchTerm = Console.ReadLine() ?? "";

                Console.Write("Enter string for message mask (eg .txt): ");
                filemask = Console.ReadLine() ?? "";

                Console.Write("Ignore case? (y/N): ");
                ignoreCase = (Console.ReadLine() ?? "").Trim().Equals("y", StringComparison.OrdinalIgnoreCase);

                Console.Write("Include subfolders? (y/N): ");
                includeSubdirs = (Console.ReadLine() ?? "").Trim().Equals("y", StringComparison.OrdinalIgnoreCase);
            }

            return (folderPath, searchTerm, filemask, ignoreCase, includeSubdirs);
        }

        private static AppSettings LoadSettings()
        {
            string settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            using FileStream stream = File.OpenRead(settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (settings == null)
            {
                throw new InvalidDataException("The configuration file is empty or invalid.");
            }

            settings.Teams.WebhookUrl = Environment.GetEnvironmentVariable("FILEMONITOR_TEAMS_WEBHOOK_URL")
                ?? settings.Teams.WebhookUrl;
            return settings;
        }

        private static void ScanExistingFiles(string folderPath, string searchTerm, string filemask, bool ignoreCase, bool includeSubdirs)
        {
            Console.WriteLine("🔄 Scanning existing files...");
            var searchOption = includeSubdirs ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            IEnumerable<string> files = Enumerable.Empty<string>();
            try
            {
                files = Directory.EnumerateFiles(folderPath, $"*{filemask}", searchOption);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"⚠️ Unable to enumerate files: {ex.Message}");
                return;
            }

            int totalFiles = 0, matchedFiles = 0;

            foreach (var file in files)
            {
                totalFiles++;
                int matches = ProcessFile(file, searchTerm, ignoreCase, "Initial Scan");
                if (matches > 0)
                {
                    AcknowledgeFile(file, searchTerm, matches, false);
                    matchedFiles++;
                }
            }

            Console.WriteLine($"{DateTime.Now.ToString()} ✅ Initial scan complete. Files scanned: {totalFiles}. Files with matches: {matchedFiles}.");
            Console.WriteLine();
        }

        private static void HandleFileEvent(string eventType, string fullPath, string searchTerm, string filemask, bool ignoreCase)
        {
            // Debounce duplicate events for the same file
            var last = _recentlyProcessed.GetOrAdd(fullPath, DateTime.MinValue);
            var now = DateTime.UtcNow;
            if ((now - last).TotalMilliseconds < DebounceMs)
            {
                return;
            }
            _recentlyProcessed[fullPath] = now;

            // Only process files based on a file mask (FileSystemWatcher already filters, but double-check)
            if (!fullPath.EndsWith(filemask, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Some events may fire before the file is fully written; retry a few times
            const int maxAttempts = 5;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    int Matches = ProcessFile(fullPath, searchTerm, ignoreCase, eventType);
                    if (Matches == 0)
                    {
                        Console.WriteLine($"{DateTime.Now.ToString()} {EmojiFor(eventType)} {eventType}: {fullPath} — no matches.");
                    }
                    else
                    {
                        AcknowledgeFile(fullPath, searchTerm, Matches, true);
                    }
                    break;
                }
                catch (IOException)
                {
                    // File may be locked; wait and retry
                    Thread.Sleep(100 * attempt);
                }
                catch (UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"🚫 Access denied: {fullPath}");
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"⚠️ Error processing {fullPath}: {ex.Message}");
                    break;
                }
            }
        }

        private static void AcknowledgeFile(string file, string searchString, int matches, bool SendMail)
        {
            var newfile = new AcknowledgeFile()
            {
                FileName = file,
                Occurences = matches,
                LastChecked = DateTime.Now,
            };

            //acknowledge file
            if (_acknowledgeFiles.TryAdd(file, newfile))
            {
                Console.WriteLine($"{DateTime.Now.ToString()} Acknowledge message: {file}");
                if ((matches > 0) && SendMail)
                {
                    SendEMail($"Dolphin Server Alert! '{searchString}' Occurred at {file}");
                    //Console.WriteLine($"Send Mail 0,{matches}");
                }

            }
            else
            {
                if (_acknowledgeFiles.TryGetValue(file, out var acknowledgedFile))
                {
                    //update
                    if (acknowledgedFile != null)
                    {
                        Console.WriteLine($"{DateTime.Now.ToString()} Already acknowledge message: {file}");
                        if (matches > acknowledgedFile.Occurences)
                        {
                            int oldval = acknowledgedFile.Occurences;
                            acknowledgedFile.Occurences = matches;

                            TimeSpan difference = DateTime.Now - acknowledgedFile.LastChecked;

                            if ((difference.TotalMinutes > _settings.PauseAlertMinutes) && SendMail) 
                            {
                                SendEMail($"Dolphin Server Alert! '{searchString}' Occurred at {file}");
                                //Console.WriteLine($"Send Mail {oldval},{matches}");
                            }



                            acknowledgedFile.LastChecked = DateTime.Now;
                        }
                        
                    }
                }              
            }

           

        }     

        static async Task SendToTeams(string message)
        {
            try
            {
                string webhookUrl = _settings.Teams.WebhookUrl;

                // Replace with the user's AAD Object ID or UPN (for visual mention)
                //string userId = "Maynard.Cuellar@interpublic.com"; // UPN
                //string userName = "Maynard Cuellar (Omnicom)";

                // Adaptive Card JSON with mention
                /*
                string jsonPayload = $@"
        {{
            ""type"": ""message"",
            ""attachments"": [
                {{
                    ""contentType"": ""application/vnd.microsoft.card.adaptive"",
                    ""content"": {{
                        ""$schema"": ""http://adaptivecards.io/schemas/adaptive-card.json"",
                        ""type"": ""AdaptiveCard"",
                        ""version"": ""1.4"",
                        ""body"": [
                            {{
                                ""type"": ""TextBlock"",
                                ""text"": ""<at>{userName}</at> Hello from webhook!"",
                                ""wrap"": true
                            }}
                        ],
                        ""msteams"": {{
                            ""entities"": [
                                {{
                                    ""type"": ""mention"",
                                    ""text"": ""<at>{userName}</at>"",
                                    ""mentioned"": {{
                                        ""id"": ""{userId}"",
                                        ""name"": ""{userName}""
                                    }}
                                }}
                            ]
                        }}
                    }}
                }}
            ]
        }}";
                */

                // Validate webhook URL
                if (string.IsNullOrWhiteSpace(webhookUrl) || !webhookUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Invalid Webhook URL. Please configure it correctly.");
                    return;
                }

                // Create the message payload
                var payload = new
                {
                    //text = $"Dolphin Server Alert! Error Occurred at {message}"
                    text = message
                };

                // Serialize payload to JSON
               string jsonPayload = JsonSerializer.Serialize(payload);

                using (var httpClient = new HttpClient())
                {
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                    // Send POST request
                    HttpResponseMessage response = await httpClient.PostAsync(webhookUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("Message sent successfully to Teams!");
                    }
                    else
                    {
                        Console.WriteLine($"Failed to send message. Status: {response.StatusCode}");
                        string errorDetails = await response.Content.ReadAsStringAsync();
                        Console.WriteLine($"Error details: {errorDetails}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception occurred: {ex.Message}");
            }
        }

        private static void SendEMail(string message)
        {
            if (!_settings.Email.Enabled)
            {
                Console.WriteLine($"{DateTime.Now.ToString()} ⚠️ Email sending is disabled in settings.");
                return;
            }
            
            var emailSettings = _settings.Email;
            string? smtpPassword = Environment.GetEnvironmentVariable("FILEMONITOR_SMTP_PASSWORD");

            try
            {
                using (MailMessage mail = new MailMessage())
                {
                    mail.From = new MailAddress(emailSettings.From);
                    mail.To.Add(emailSettings.To);
                    mail.Subject = emailSettings.Subject;
                    mail.Body = message;
                    mail.IsBodyHtml = false; // Set true if sending HTML content

                    using (SmtpClient smtp = new SmtpClient(emailSettings.SmtpServer, emailSettings.SmtpPort))
                    {
                        smtp.EnableSsl = emailSettings.EnableSsl;
                        if (!string.IsNullOrWhiteSpace(emailSettings.Username) && !string.IsNullOrEmpty(smtpPassword))
                        {
                            smtp.Credentials = new NetworkCredential(emailSettings.Username, smtpPassword);
                        }
                        smtp.Send(mail);
                        Console.WriteLine($"{DateTime.Now.ToString()} ✅ Email sent successfully!");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now.ToString()} ❌ Failed to send email: {ex.Message}");
            }
        }


        private static int ProcessFile(string path, string searchTerm, bool ignoreCase, string source)
        {
            if (!File.Exists(path))
            {
                // Renames or quick deletes can race
                return 0;
            }

            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            int matchCount = 0;
            int lineNumber = 0;

            // Read as text safely
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine() ?? string.Empty;
                lineNumber++;
                if (line.IndexOf(searchTerm, comparison) >= 0)
                {
                    matchCount++;
                    /*
                    Console.WriteLine($"🎯 Match ({source}) in {path}");
                    Console.WriteLine($"   • Line: {lineNumber}");
                    Console.WriteLine($"   • Snippet: {TruncateForDisplay(line)}");
                    */
                }
            }

            return matchCount;
        }

        private static string TruncateForDisplay(string text, int maxLen = 200)
        {
            if (text.Length <= maxLen) return text;
            return text.Substring(0, maxLen) + "…";
        }

        private static string EmojiFor(string eventType) => eventType switch
        {
            "Created" => "🆕",
            "Changed" => "✏️",
            "Renamed" => "🔁",
            "Initial Scan" => "🔄",
            _ => "📄"
        };
    }

    class AcknowledgeFile
    {
        public required string FileName { get; set; }
        public required int Occurences { get; set; }
        public required DateTime LastChecked { get; set; }

    }

    class AppSettings
    {
        public int PauseAlertMinutes { get; set; } = 30;
        public EmailSettings Email { get; set; } = new();
        public TeamsSettings Teams { get; set; } = new();
    }

    class EmailSettings
    {
        public bool Enabled { get; set; } = false;  
        public string SmtpServer { get; set; } = "";
        public int SmtpPort { get; set; }
        public string Username { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string Subject { get; set; } = "";
        public bool EnableSsl { get; set; }
    }

    class TeamsSettings
    {
        public string WebhookUrl { get; set; } = "";
    }
}
