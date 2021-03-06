﻿using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using StarryEyes.Annotations;
using StarryEyes.Casket;
using StarryEyes.Nightmare.Windows;
using StarryEyes.Settings;
using ThemeManager = StarryEyes.Settings.ThemeManager;

namespace StarryEyes
{
    /// <summary>
    /// App.xaml の相互作用ロジック
    /// </summary>
    public partial class App
    {
        private static readonly string DbVersion = "1.0";
        private static DateTime _startupTime;

        /// <summary>
        /// Application entry point
        /// </summary>
        private void AppStartup(object sender, StartupEventArgs e)
        {
            _startupTime = DateTime.Now;

            // create default theme xaml
            // File.WriteAllText("default.xaml", DefaultThemeProvider.GetDefaultAsXaml());
            // Environment.Exit(0);

            // call pre-initializer
            AppInitializer.PreInitialize(e);

            // set exception handlers
            Current.DispatcherUnhandledException += (sender2, e2) => HandleException(e2.Exception);
            AppDomain.CurrentDomain.UnhandledException += (sender2, e2) => HandleException(e2.ExceptionObject as Exception);

            // set exit handler
            Current.Exit += (_, __) => AppFinalize(true);

            // call main initializer
            AppInitializer.Initialize(e);

            // initialize database
            Database.Initialize(DatabaseFilePath);
            if (!this.CheckDatabase())
            {
                // db migration failed
                Current.Shutdown();
                Environment.Exit(0);
            }

            // Optimize database when user specified to do so in pre-execution dialog.
            AppInitializer.OptimizeDatabaseIfRequired();

            // Apply theme
            InitializeTheme();

            AppInitializer.PostInitialize();

            // initialization completed
            RaiseSystemReady();
        }

        #region Startup Subprocesses

        /// <summary>
        /// Check sqlite database
        /// </summary>
        /// <returns>when database is compatible, return true</returns>
        private bool CheckDatabase()
        {
            var ver = Database.ManagementCrud.DatabaseVersion;
            if (String.IsNullOrEmpty(ver))
            {
                Database.ManagementCrud.DatabaseVersion = DbVersion;
            }
            else if (ver != DbVersion)
            {
                // todo: update db
                return false;
            }
            return true;
        }

        #endregion

        #region Finalize/Error handling

        /// <summary>
        /// Finalize application
        /// </summary>
        /// <param name="shutdown">If true, application has shutdowned collectly.</param>
        void AppFinalize(bool shutdown)
        {
            if (shutdown)
            {
                Debug.WriteLine("App Normal Exit");
                RaiseApplicationExit();
            }
            Debug.WriteLine("App Finalize");
            RaiseApplicationFinalize();
            AppInitializer.ReleaseMutex();
        }

        /// <summary>
        /// Global unhandled exception handler method
        /// </summary>
        /// <param name="ex">thrown exception</param>
        private void HandleException(Exception ex)
        {
            try
            {
                var aex = ex as AggregateException;
                if (ex.Message.Contains("8007007e") &&
                    ex.Message.Contains("CLSID {E5B8E079-EE6D-4E33-A438-C87F2E959254}"))
                {
                    Setting.DisableGeoLocationService.Value = false;
                }

                // TODO:ロギング処理など
                Debug.WriteLine("##### SYSTEM CRASH! #####");
                Debug.WriteLine(ex.ToString());

                // Build stack trace report file
                var builder = new StringBuilder();
                builder.AppendLine("Krile STARRYEYES #" + FormattedVersion + " - " + DateTime.Now.ToString());
                builder.AppendLine(Environment.OSVersion + " " + (Environment.Is64BitProcess ? "x64" : "x86"));
                builder.AppendLine("execution mode: " + ExecutionMode.ToString() + ", " +
                    "multicore JIT: " + IsMulticoreJitEnabled.ToString() + ", " +
                    "hardware rendering: " + IsHardwareRenderingEnabled.ToString());
                var uptime = DateTime.Now - _startupTime;
                builder.AppendLine("application uptime: " + ((int)uptime.TotalHours).ToString("00") +
                                   uptime.ToString("\\:mm\\:ss"));
                builder.AppendLine();
                builder.AppendLine("exception stack trace:");
                builder.AppendLine(ex.ToString());
#if DEBUG
                var tpath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "StarryEyes_Dump_" + Path.GetRandomFileName() + ".txt");
                using (var sw = new StreamWriter(tpath))
                {
                    sw.WriteLine(builder.ToString());
                }
#else
                var tpath = Path.GetTempFileName() + ".crashlog";
                using (var sw = new StreamWriter(tpath))
                {
                    sw.WriteLine(builder.ToString());
                }
                var apppath = Path.GetDirectoryName(Environment.GetCommandLineArgs()[0]);
                var psi = new ProcessStartInfo
                {
                    Arguments = tpath,
                    UseShellExecute = true,
                    FileName = Path.Combine(apppath, App.FeedbackAppName)
                };
                try
                {
                    Process.Start(psi);
                }
                catch { }
#endif
            }
            finally
            {
                // Exit aplication
                AppFinalize(false);
            }

            Environment.Exit(-1);
        }

        #endregion

        #region Definitions

        public const string AppShortName = "Krile";

        public const string AppFullName = "Krile STARRYEYES";

        internal static readonly string ConsumerKey = "oReH15myW0cPq7pHNvAMGw";

        internal static readonly string ConsumerSecret = "0BkDivB4cqdkKaTmXc8j2urtH9C4xApJBKZFvlb9dec";

        public static bool IsOperatingSystemSupported
        {
            get
            {
                return Environment.OSVersion.Version.Major == 6;
            }
        }

        [NotNull]
        public static string ExeFilePath
        {
            get
            {
                return Process.GetCurrentProcess().MainModule.FileName;
            }
        }

        [NotNull]
        public static string ExeFileDir
        {
            get
            {
                return Path.GetDirectoryName(ExeFilePath) ?? ExeFilePath + "_";
            }
        }

        public static ExecutionMode ExecutionMode
        {
            get
            {
                switch (ConfigurationManager.AppSettings["ExecutionMode"].ToLower())
                {
                    case "standalone":
                    case "portable":
                        return ExecutionMode.Standalone;

                    case "roaming":
                        return ExecutionMode.Roaming;

                    default:
                        return ExecutionMode.Default;
                }
            }
        }

        public static bool DatabaseDirectoryUserSpecified
        {
            get
            {
                var path = ConfigurationManager.AppSettings["DatabaseDirectoryPath"];
                return !String.IsNullOrEmpty(path);
            }
        }

        public static string DatabaseDirectoryPath
        {
            get
            {
                try
                {
                    var path = ConfigurationManager.AppSettings["DatabaseDirectoryPath"];
                    if (String.IsNullOrEmpty(path))
                    {
                        // locate into configuration directory
                        return ConfigurationDirectoryPath;
                    }
                    // make sure to exist database directory
                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                    }
                    return path;
                }
                catch (Exception ex)
                {
                    TaskDialog.Show(new TaskDialogOptions
                    {
                        Title = StarryEyes.Properties.Resources.MsgStartupErrorTitle,
                        MainIcon = VistaTaskDialogIcon.Error,
                        MainInstruction = StarryEyes.Properties.Resources.MsgStartupErrorInst,
                        Content = StarryEyes.Properties.Resources.MsgDbPathInvalidContent,
                        ExpandedInfo = ex.ToString(),
                        CommonButtons = TaskDialogCommonButtons.Close,
                        FooterIcon = VistaTaskDialogIcon.Information,
                        FooterText = StarryEyes.Properties.Resources.MsgReInstallKrile,
                    });
                    throw;
                }
            }
        }

        public static bool IsMulticoreJitEnabled
        {
            get
            {
                if (ConfigurationManager.AppSettings["UseMulticoreJIT"].ToLower() == "none")
                    return false;
                return true;
            }
        }

        public static bool IsHardwareRenderingEnabled
        {
            get
            {
                if (ConfigurationManager.AppSettings["UseHardwareRendering"].ToLower() == "none")
                    return false;
                return true;
            }
        }

        [NotNull]
        public static string ConfigurationDirectoryPath
        {
            get
            {
                switch (ExecutionMode)
                {
                    case ExecutionMode.Standalone:
                        return ExeFileDir;
                    case ExecutionMode.Roaming:
                        return Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "Krile");
                    default:
                        // setting hold in "Local"
                        return Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "Krile");
                }
            }
        }

        [NotNull]
        public static string ConfigurationFilePath
        {
            get
            {
                return Path.Combine(ConfigurationDirectoryPath, ConfigurationFileName);
            }
        }

        [NotNull]
        public static string DatabaseFilePath
        {
            get { return Path.Combine(DatabaseDirectoryPath, DatabaseFileName); }
        }

        [NotNull]
        public static string LockFilePath
        {
            get { return Path.Combine(ConfigurationDirectoryPath, LockFileName); }
        }

        public static string LocalUpdateStorePath
        {
            get { return Path.Combine(ConfigurationDirectoryPath, LocalUpdateStoreDirName); }
        }

        public static string HashtagTempFilePath
        {
            get { return Path.Combine(ConfigurationDirectoryPath, HashtagCacheFileName); }
        }

        public static string ListUserTempFilePath
        {
            get { return Path.Combine(ConfigurationDirectoryPath, ListCacheFileName); }
        }

        private static Version _version;

        [NotNull]
        public static Version Version
        {
            get
            {
                return _version ??
                       (_version = Assembly.GetEntryAssembly().GetName().Version);
            }
        }

        [NotNull]
        public static string FormattedVersion
        {
            get
            {
                var basestr = Version.ToString(3);
                if (Version.Revision > 0)
                {
                    return basestr + " Rev." + Version.Revision;
                }
                return basestr;
            }
        }

        public static bool IsUnstableVersion
        {
            get { return Version.Revision != 0; }
        }

        public static readonly uint LeastDesktopHeapSize = 12 * 1024;

        public static readonly string DatabaseFileName = "krile.db";

        public static readonly string LockFileName = "krile.lock";

        public static readonly string KeyAssignProfilesDirectory = "assigns";

        public static readonly string ThemeProfilesDirectory = "themes";

        public static readonly string MediaDirectory = "media";

        public static readonly string PluginDirectory = "plugins";

        public static readonly string PluginSignatureFile = "auth.sig";

        public static readonly string ScriptDirectiory = "scripts";

        public static readonly string ConfigurationFileName = "krile.xml";

        public static readonly string HashtagCacheFileName = "tags.cache";

        public static readonly string ListCacheFileName = "lists.cache";

        public static readonly string ProfileFileName = "krile.profile";

        public static readonly string FeedbackAppName = "reporter.exe";

        public static readonly string UpdaterFileName = "kup.exe";

        public static readonly string PostUpdateFileName = "kup.completed";

        public static readonly string BehaviorLogFileName = "krile.log";

        public static readonly string DefaultStatusMessage = "完了";

        public static readonly string RemoteVersionXml = "http://krile.starwing.net/shared/update.xml";

        public static readonly string PublicKeyFile = "krile.pub";

        public static readonly string LocalUpdateStoreDirName = "update";

        public static readonly string MentionWavFile = "mention.wav";

        public static readonly string NewReceiveWavFile = "new.wav";

        public static readonly string DirectMessageWavFile = "message.wav";

        public static readonly string EventWavFile = "event.wav";

        public static readonly string OfficialUrl = "http://krile.starwing.net/";

        public static readonly string QueryReferenceUrl = "https://github.com/karno/StarryEyes/wiki";

        public static readonly string AuthorizeHelpUrl = "https://github.com/karno/StarryEyes/wiki/Help_Authorize";

        public static readonly string ReleaseNoteUrl = "https://github.com/karno/StarryEyes/wiki/ReleaseNote";

        public static readonly string DonationUrl = "http://krile.starwing.net/donation.html";

        public static readonly string ContributorsUrl = "http://krile.starwing.net/shared/contrib.xml";

        public static readonly string LicenseUrl = "https://raw.github.com/karno/StarryEyes/master/LICENSE.TXT";

        #endregion

        #region Triggers

        /// <summary>
        /// Call on kernel systems are ready<para />
        /// (But UI is not prepared)
        /// </summary>
        public static event Action SystemReady;
        internal static void RaiseSystemReady()
        {
            Debug.WriteLine("# System ready.");
            var osr = SystemReady;
            SystemReady = null;
            if (osr != null)
                osr();
        }

        /// <summary>
        /// Call on user interfaces are ready
        /// </summary>
        public static event Action UserInterfaceReady;
        internal static void RaiseUserInterfaceReady()
        {
            // this method called by background thread.
            Debug.WriteLine("# UI ready.");
            var usr = UserInterfaceReady;
            UserInterfaceReady = null;
            if (usr != null)
                usr();
        }

        /// <summary>
        /// Call on aplication is exit from user action<para />
        /// (On crash app, this handler won't call!)
        /// </summary>
        public static event Action ApplicationExit;
        internal static void RaiseApplicationExit()
        {
            Debug.WriteLine("# App exit.");
            // reset continual crash counter
            File.Delete(LockFilePath);
            var apx = ApplicationExit;
            ApplicationExit = null;
            if (apx != null)
                apx();
        }

        /// <summary>
        /// Call on application is exit from user action or crashed
        /// </summary>
        public static event Action ApplicationFinalize;
        internal static void RaiseApplicationFinalize()
        {
            Debug.WriteLine("# App finalize.");
            var apf = ApplicationFinalize;
            ApplicationFinalize = null;
            if (apf != null)
                apf();
        }

        #endregion

        #region Themes

        private ResourceDictionary _prevThemeDict;

        //#define THEME_TEST

        private void InitializeTheme()
        {
            ThemeManager.ThemeChanged += this.OnThemeChanged;
            ThemeManager.Initialize();

            // unload design-time default theme
            _prevThemeDict = Current.Resources.MergedDictionaries.FirstOrDefault(r => r.Source.ToString().EndsWith("DesignTimeDefault.xaml"));

            // initialization call
            OnThemeChanged();
            // TODO: below codes are reserved for debugging
#if THEME_TEST
                Observable.Timer(TimeSpan.FromSeconds(10))
                          .ObserveOnDispatcher()
                          .Subscribe(s =>
                          {
                              // apply new one
                              var currentTheme = ThemeManager.CurrentTheme;
                              currentTheme.TitleBarColor = new ThemeColors
                              {
                                  Foreground = Colors.White,
                                  Background = Color.FromRgb(0x11, 0x11, 0x11),
                              };
                              currentTheme.BaseColor = new ThemeColors
                              {
                                  Foreground = Colors.White,
                                  Background = Color.FromRgb(0x11, 0x11, 0x11),
                              };
                              this.ApplyThemeResource(currentTheme == null
                                  ? null
                                  : currentTheme.CreateResourceDictionary());
                          });
#endif

        }

        /// <summary>
        /// This method is called when theme has changed.
        /// </summary>
        void OnThemeChanged()
        {
            // apply new one
            var currentTheme = ThemeManager.CurrentTheme;
            this.ApplyThemeResource(currentTheme == null
                ? null
                : currentTheme.CreateResourceDictionary());
        }

        /// <summary>
        /// Apply theme resource dictionary
        /// </summary>
        /// <param name="dictionary">theme resource dictionary</param>
        void ApplyThemeResource(ResourceDictionary dictionary)
        {
            if (dictionary == null || dictionary == _prevThemeDict) return;

            // apply new dictionary
            Current.Resources.MergedDictionaries.Add(dictionary);

            // remove previous 
            Current.Resources.MergedDictionaries.Remove(_prevThemeDict);

            _prevThemeDict = dictionary;
        }

        #endregion

        #region Static helper constants/methods

        public static readonly DateTime StartupDateTime = DateTime.Now;

        public static T FindResource<T>(string name) where T : DispatcherObject
        {
            return App.Current.TryFindResource(name) as T;
        }

        #endregion
    }

    public enum ExecutionMode
    {
        /// <summary>
        /// Use Local folder for storing setting file
        /// </summary>
        Default,
        /// <summary>
        /// Use Roaming folder for storing setting file
        /// </summary>
        Roaming,
        /// <summary>
        /// Use application local folder for storing setting file
        /// </summary>
        Standalone,
    }
}
