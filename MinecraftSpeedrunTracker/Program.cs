using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Timers;
using System.Management;
using Firebase.Database;
using Firebase.Database.Query;
using System.Net;

namespace MinecraftSpeedrunTracker
{
    internal class Program
    {
        private static System.Timers.Timer timer;

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowThreadProcessId(IntPtr hWnd, out uint ProcessId);

        const string InstanceNumberFileName = "instanceNumber.txt";
        const string GameDirFlag = "--gameDir ";
        const string NativesFlag = "-Djava.library.path=";

        public static string SavesPath { get; private set; } = string.Empty;
        public static string LogFile { get; private set; } = string.Empty;
        public static int Number { get; private set; } = -1;
        public static int LastActiveId { get; private set; } = -1;
        public static DateTime LastModifiedTime { get; private set; } = DateTime.MinValue;


        public static string authKey = "cnSBxG9CUlSl9TW9bgGJkVszAeroRSI89SBq8kMl";
        public static string firebaseUrl = "https://minecraft-speedrun-tracker-default-rtdb.firebaseio.com/";
        private static FirebaseClient? firebaseClient;

        private static string userConfigFilePath = "minecraft-speedrun-tracker-config.txt";
        private static string userKey = "";

        public static void Main(string[] args)
        {
            Console.WriteLine("Starting Minecraft Speedrun Tracker");
            Console.WriteLine("Checking for user key");

            if(!File.Exists(userConfigFilePath))
            {
                Console.WriteLine("No user key found. Creating a new user key.");
                var newGuid = Guid.NewGuid();
                File.WriteAllText(userConfigFilePath, newGuid.ToString());
                Console.WriteLine("====================================");
                Console.WriteLine("=========== NEW USER KEY ===========");
                Console.WriteLine($"{newGuid}");
                Console.WriteLine("========= COPY THIS KEY^^^ =========");
                Console.WriteLine("====================================");
                Console.WriteLine("===");
                Console.WriteLine("===");
                Console.WriteLine("UPDATE YOUR KEY IN EXTENSION CONFIGURATION");
            }

            string[] configLines = File.ReadAllLines(userConfigFilePath);
            userKey = configLines[0];
            if (!IsGuid(userKey))
            {
                Console.WriteLine($"Invalid User Key. Delete {userConfigFilePath} and try again.");
                Console.WriteLine("\nPress the Enter key to exit the application...\n");
                Console.ReadLine();
                Console.WriteLine("Terminating the application...");
                Environment.Exit(0);
            }

            Console.WriteLine($"User Key Found -> {userKey}");

            Console.WriteLine("");
            Console.WriteLine("Connecting to remote db");
            firebaseClient = new FirebaseClient(
                firebaseUrl,
                new FirebaseOptions
                {
                    AuthTokenAsyncFactory = () => Task.FromResult(authKey)
                });
            Console.WriteLine("Connection Successful!");

            SetTimer();

            Console.WriteLine("\nPress the Enter key to exit the application...\n");
            Console.WriteLine("The application started at {0:HH:mm:ss.fff}", DateTime.Now);

            Console.ReadLine();
            timer.Stop();
            timer.Dispose();

            Console.WriteLine("Terminating the application...");
        }

        private static async Task SetTimer()
        {
            timer = new System.Timers.Timer(5000);
            timer.Elapsed += CheckRecordFileAsync;
            timer.AutoReset = true;
            timer.Enabled = true;
        }

        private static async void CheckRecordFileAsync(Object source, ElapsedEventArgs e)
        {
            Console.WriteLine("Checking record {0:HH:mm:ss.fff}", e.SignalTime);

            if (!TryGetActive(out Process instance))
                return;

            //Instance changed
            if (instance.Id != LastActiveId)
            {
                string args = CommandLine(instance);

                SavesPath = TryParseDotMinecraft(args, out DirectoryInfo dotMinecraft)
                    ? Path.Combine(dotMinecraft.FullName, "saves")
                    : string.Empty;

                LastActiveId = instance.Id;
                LastModifiedTime = DateTime.MinValue;
            }

            var mostRecentWorld = new DirectoryInfo($"{SavesPath}")
                .GetDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .First();

            var recordPath = $"{mostRecentWorld.FullName}\\speedrunigt\\record.json";

            if (File.Exists(recordPath))
            {
                DateTime dt = File.GetLastWriteTime(recordPath);

                if(dt > LastModifiedTime)
                {
                    string jsonRecord = File.ReadAllText($"{mostRecentWorld.FullName}\\speedrunigt\\record.json");

                    Console.WriteLine("jsonRecord has changed");

                    if (firebaseClient != null)
                    {
                        Console.WriteLine("Updating Record for " + userKey);
                        await firebaseClient
                            .Child("records")
                            .Child(userKey)
                            .PutAsync(new { record = jsonRecord });

                        LastModifiedTime = dt;
                    }
                }               
            }
        }

        private static bool TryGetActive(out Process instance)
        {
            instance = null;
            try
            {
                IntPtr hWnd = GetForegroundWindow();
                GetWindowThreadProcessId(hWnd, out uint processId);
                var active = Process.GetProcessById((int)processId);

                //verify that process is an instance of minecraft
                if (active.ProcessName is "javaw" && active.MainWindowTitle.StartsWith("Minecraft"))
                    instance = active;
            }
            catch
            {
                //couldn't get active instance
            }
            return instance is not null;
        }

        private static bool TryParseDotMinecraft(string args, out DirectoryInfo folder)
        {
            folder = null;
            if (string.IsNullOrEmpty(args))
                return false;

            string path;
            try
            {
                if (args.Contains(GameDirFlag))
                {
                    //try parsing path
                    //flag specifies ".minecraft" directory
                    Match match = Regex.Match(args, @$"{GameDirFlag}(?:""(.+?)""|([^\s]+))");
                    path = args.Substring(match.Index + GameDirFlag.Length, match.Length - GameDirFlag.Length) + "\\";
                }
                else
                {
                    //try alternate method
                    //flag specifies "natives" directory which is adjacent to ".minecraft"
                    Match match = Regex.Match(args, @$"(?:{NativesFlag}(.+?) )|(?:\""{NativesFlag}(.+?)\"")");
                    int length = match.Length;
                    int index = match.Index;
                    if (args[match.Index + NativesFlag.Length] is '=')
                    {
                        length -= 1;
                        index += 1;
                    }
                    path = args.Substring(index + NativesFlag.Length, length - NativesFlag.Length - 8) + ".minecraft\\";
                    path = path.Replace("/", "\\");
                }
                folder = new DirectoryInfo(path);
            }
            catch
            {
                //unable to parse .minecraft path
            }
            return folder is not null;
        }

        private static string CommandLine(Process process)
        {
            string query = $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}";
            using (var searcher = new ManagementObjectSearcher(query))
            using (ManagementObjectCollection objects = searcher.Get())
            {
                return objects.Cast<ManagementBaseObject>().SingleOrDefault()?["CommandLine"]?.ToString();
            }
        }

        public static bool IsGuid(string value)
        {
            Guid x;
            return Guid.TryParse(value, out x);
        }
    }
}