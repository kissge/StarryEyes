﻿using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Livet;
using StarryEyes.Casket;
using StarryEyes.Models;
using StarryEyes.Models.Plugins;
using StarryEyes.Models.Receiving;
using StarryEyes.Models.Receiving.Handling;
using StarryEyes.Models.Stores;
using StarryEyes.Models.Subsystems;
using StarryEyes.Models.Subsystems.Notifications.UI;
using StarryEyes.Nightmare.Windows;
using StarryEyes.Plugins;
using StarryEyes.Settings;
using StarryEyes.Views.Dialogs;
using Application = System.Windows.Application;

namespace StarryEyes
{
    internal static class AppInitializer
    {
        private static Mutex _appMutex;

        #region pre-initialize application

        public static void PreInitialize(StartupEventArgs e)
        {
            var isMaintenanceMode = e.Args.Select(a => a.ToLower()).Contains("-maintenance");

            PrepareConfigurationDirectory();
            SetSystemParameters(App.IsMulticoreJitEnabled && !isMaintenanceMode,
                App.IsHardwareRenderingEnabled);

            if (!isMaintenanceMode && !CheckInitializeMutex())
            {
                FailMessages.LaunchDuplicated();
                Environment.Exit(-1);
            }
        }

        private static void PrepareConfigurationDirectory()
        {
            // create data-store directory
            try
            {
                Directory.CreateDirectory(App.ConfigurationDirectoryPath);
            }
            catch (Exception ex)
            {
                FailMessages.InitConfDirFailed(ex);
                Environment.Exit(-1);
            }
        }

        private static void SetSystemParameters(bool useProfiling, bool useHardwareRendering)
        {
            // enable multi-core JIT.
            // see reference: http://msdn.microsoft.com/en-us/library/system.runtime.profileoptimization.aspx
            if (useProfiling)
            {
                ProfileOptimization.SetProfileRoot(App.ConfigurationDirectoryPath);
                ProfileOptimization.StartProfile(App.ProfileFileName);
            }

            // initialize dispatcher helper
            DispatcherHelper.UIDispatcher = Application.Current.Dispatcher;
            DispatcherHolder.Initialize(Application.Current.Dispatcher);

            // set rendering mode
            if (!useHardwareRendering)
            {
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            }
        }

        #endregion

        #region initialize application

        public static void Initialize(StartupEventArgs e)
        {
            var isMaintenanceMode = e.Args.Select(a => a.ToLower()).Contains("-maintenance");
            var postUpdate = e.Args.Select(a => a.ToLower()).Contains("-postupdate");

            // determine mode and rescue/maintenance self
            if (!CheckRescueMaintenance(isMaintenanceMode))
            {
                Environment.Exit(-1);
            }

            if (postUpdate || AutoUpdateService.IsPostUpdateFileExisted())
            {
                // remove update binary
                AutoUpdateService.PostUpdate();
            }
            else if (AutoUpdateService.IsUpdateBinaryExisted())
            {
                // execute auto-update
                AutoUpdateService.StartUpdate(App.Version);
                Environment.Exit(0);
            }

            try
            {
                // create lock file
                using (File.Create(App.LockFilePath))
                {
                    // do nothing
                }
            }
            catch (Exception ex)
            {
                FailMessages.InitLockFileFailed(ex);
                Environment.Exit(-1);
            }

            // initialze web parameters
            InitializeWebConnectionParameters();

            // load subsystems, load settings
            InitializeSubsystemsBeforeSettingsLoaded();
            if (!Setting.LoadSettings())
            {
                // failed loading settings
                Environment.Exit(-1);
            }
            InitializeSubsystemsAfterSettingsLoaded();
        }

        /// <summary>
        /// Show rescue dialog or maintenance dialog
        /// </summary>
        /// <param name="isMaintenanceMode">flag of maintenance is explicitly designated</param>
        /// <returns>if false, aplication should be halted.</returns>
        private static bool CheckRescueMaintenance(bool isMaintenanceMode)
        {
            return isMaintenanceMode
                ? ShowMaintenanceDialog()
                : !File.Exists(App.LockFilePath) || ShowRescueDialog();
        }

        /// <summary>
        /// Show rescue option dialog (TaskDialog)
        /// </summary>
        /// <returns>when returning false, should abort execution</returns>
        private static bool ShowRescueDialog()
        {
#if DEBUG
            return true;
#else
            var resp = ConfirmationMassages.ConfirmRescue();
            if (!resp.CommandButtonResult.HasValue || resp.CommandButtonResult.Value == 2)
            {
                return false;
            }
            if (resp.CommandButtonResult.Value == 0)
            {
                return true;
            }
            return ShowMaintenanceDialog();
#endif
        }

        /// <summary>
        /// Show maintenance option dialog (TaskDialog)
        /// </summary>
        /// <returns>when returning false, should abort execution</returns>
        private static bool ShowMaintenanceDialog()
        {
            var resp = ConfirmationMassages.ConfirmMaintenance();
            if (!resp.CommandButtonResult.HasValue || resp.CommandButtonResult.Value == 5)
            {
                return false;
            }
            try
            {
                switch (resp.CommandButtonResult.Value)
                {
                    case 1:
                        // optimize database
                        ScheduleDatabaseOptimization();
                        break;
                    case 2:
                        // remove database
                        if (File.Exists(App.DatabaseFilePath))
                        {
                            File.Delete(App.DatabaseFilePath);
                        }
                        break;
                    case 3:
                    case 4:
                    case 5:
                        // remove all
                        if (App.ExecutionMode == ExecutionMode.Standalone)
                        {
                            // remove each
                            var files = new[]
                            {
                                App.DatabaseFilePath, App.DatabaseFilePath,
                                App.HashtagTempFilePath, App.ListUserTempFilePath,
                                Path.Combine(App.ConfigurationDirectoryPath, App.ProfileFileName)
                            };
                            var dirs = new[]
                            {
                                App.KeyAssignProfilesDirectory
                            };
                            files.Where(File.Exists).ForEach(File.Delete);
                            dirs.Where(Directory.Exists).ForEach(d => Directory.Delete(d, true));
                        }
                        else
                        {
                            // remove whole directory
                            if (Directory.Exists(App.ConfigurationDirectoryPath))
                            {
                                Directory.Delete(App.ConfigurationDirectoryPath, true);
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                FailMessages.GeneralProcessFailed(ex);
            }
            if (resp.CommandButtonResult.Value == 5)
            {
                // force update
                var w = new AwaitDownloadingUpdateWindow();
                w.ShowDialog();
            }
            return resp.CommandButtonResult.Value < 4;
        }

        /// <summary>
        /// Initialize service-point manager
        /// </summary>
        private static void InitializeWebConnectionParameters()
        {
            // initialize service points
            ServicePointManager.Expect100Continue = false; // disable expect 100 continue for User Streams connection.
            ServicePointManager.DefaultConnectionLimit = Int32.MaxValue; // Limit Break!

            // declare security protocol explicitly
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Ssl3;
        }

        private static void InitializeSubsystemsBeforeSettingsLoaded()
        {
            // initialize anomaly twitter library core system
            Anomaly.Core.Initialize();

            // initialize special image handlers
            SpecialImageResolvers.Initialize();

            // load plugins
            PluginManager.Load(Path.Combine(App.ExeFileDir, App.PluginDirectory));
        }

        private static void InitializeSubsystemsAfterSettingsLoaded()
        {
            // Logger subsystem initialize
            BehaviorLogger.Initialize();

            // set parameters for accessing twitter.
            Networking.Initialize();

            // load key assigns
            KeyAssignManager.Initialize();

            // load cache manager
            CacheStore.Initialize();
        }

        #endregion

        #region Database optimization

        private static bool _isDatabaseOptimizationRequired;

        private static void ScheduleDatabaseOptimization()
        {
            _isDatabaseOptimizationRequired = true;
        }

        public static void OptimizeDatabaseIfRequired()
        {
            if (!_isDatabaseOptimizationRequired) return;

            // change application shutdown mode for preventing 
            // auto-exit when optimization is completed.
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                // run database optimization
                var optDlg = new WorkingWindow(
                    "optimizing database...",
                    OptimizeDatabase);
                optDlg.ShowDialog();
            }
            finally
            {
                // restore shutdown mode
                Application.Current.ShutdownMode = ShutdownMode.OnLastWindowClose;
            }
        }

        private static async Task OptimizeDatabase()
        {
            try
            {
                await Database.VacuumTables();
            }
            catch (SQLiteException sqex)
            {
                TaskDialog.Show(new TaskDialogOptions
                {
                    MainIcon = VistaTaskDialogIcon.Error,
                    Title = "Krile Database Optimization",
                    MainInstruction = "データベースの最適化に失敗しました。",
                    Content = sqex.Message,
                    ExpandedInfo = sqex.ToString()
                });
            }
        }

        #endregion

        #region post-initialize application

        public static void PostInitialize()
        {
            // initialize subsystems
            StatisticsService.Initialize();
            PostLimitPredictionService.Initialize();
            MuteBlockManager.Initialize();
            StatusBroadcaster.Initialize();
            StatusInbox.Initialize();
            AutoUpdateService.StartSchedule();
            NotificationService.Initialize();
            UINotificationProxy.Initialize();

            // activate plugins
            PluginManager.LoadedPlugins.ForEach(p => p.Initialize());

            // activate scripts
            ScriptingManagerImpl.Initialize();

            // other core systems initialize
            ReceiveManager.Initialize();
            TwitterConfigurationService.Initialize();
            BackstageModel.Initialize();
        }

        #endregion

        #region Mutex control

        private static bool CheckInitializeMutex()
        {
            // if Krile started as maintenance mode, skip this check.
            string mutexStr = null;
            switch (App.ExecutionMode)
            {
                case ExecutionMode.Default:
                case ExecutionMode.Roaming:
                    mutexStr = App.ExecutionMode.ToString();
                    break;
                case ExecutionMode.Standalone:
                    mutexStr = "Standalone_" + App.ExeFilePath.Replace('\\', '*');
                    break;
            }

            // create mutex
            _appMutex = new Mutex(true, "Krile_StarryEyes_" + mutexStr);

            // check mutex
            return _appMutex.WaitOne(0, false);
        }

        public static void ReleaseMutex()
        {
            try
            {
                if (_appMutex == null) return;
                _appMutex.ReleaseMutex();
                _appMutex.Dispose();
                _appMutex = null;
            }
            catch (ObjectDisposedException) { }
        }

        #endregion

        #region Confirmation messages

        public static class ConfirmationMassages
        {

            public static TaskDialogResult ConfirmRescue()
            {
                return ShowMessage(new TaskDialogOptions
                 {
                     Title = "Krileの回復",
                     MainIcon = VistaTaskDialogIcon.Error,
                     MainInstruction = "Krileは正しく終了しませんでした。",
                     Content = "一時的な問題である場合は、このまま起動を継続できます。" + Environment.NewLine +
                     "問題が継続して発生する場合は、データベースファイルの削除などを行うと回復することがあります。" + Environment.NewLine +
                     "データベースや設定の削除、クリーンインストールなどを行うには「メンテナンスダイアログを表示」を選択してください。" + Environment.NewLine +
                     "バックアップなどを取得するためにこのままKrileを終了する場合は「起動せずにKrileを終了」を選択してください。",
                     CommandButtons = new[]
                {
                    /* 0 */ "このまま起動(&C)",
                    /* 1 */ "メンテナンスダイアログを表示(&M)",
                    /* 2 */ "起動せずにKrileを終了(&X)"
                },
                 });
            }

            public static TaskDialogResult ConfirmMaintenance()
            {
                return ShowMessage(new TaskDialogOptions
                {
                    Title = "Krileのメンテナンス",
                    MainIcon = VistaTaskDialogIcon.Warning,
                    MainInstruction = "Krileが保持するデータを管理できます。",
                    Content = "消去したデータはもとに戻せません。必要なデータは予めバックアップしてください。",
                    CommandButtons = new[]
                {
                    /* 0 */ "このまま起動(&C)",
                    /* 1 */ "データベースを最適化して起動(&O)",
                    /* 2 */ "データベースを消去して起動(&D)",
                    /* 3 */ "すべての設定・データベースを消去して起動(&R)",
                    /* 4 */ "すべての設定・データベースを消去して終了(&E)",
                    /* 5 */ "最新版をクリーンインストール(&U)",
                    /* 6 */ "キャンセル(&X)"
                },
                    FooterIcon = VistaTaskDialogIcon.Information,
                    FooterText = "クリーンインストールを行うと、全ての設定・データベースが消去されます。"
                });
            }

        }
        #endregion

        #region Fail messages

        /// <summary>
        /// Failed messages
        /// </summary>
        private static class FailMessages
        {

            public static void LaunchDuplicated()
            {
                ShowMessage(new TaskDialogOptions
                {
                    Title = "Krile StarryEyes",
                    MainIcon = VistaTaskDialogIcon.Error,
                    MainInstruction = "Krileはすでに起動しています。",
                    Content = "同じ設定を共有するKrileを多重起動することはできません。",
                    ExpandedInfo = "Krileを多重起動するためには、krile.exe.configを編集する必要があります。" + Environment.NewLine +
                                   "詳しくは公式ウェブサイト上のFAQを参照してください。",
                    CommonButtons = TaskDialogCommonButtons.Close
                });
            }

            public static void InitConfDirFailed(Exception ex)
            {
                ShowMessage(new TaskDialogOptions
                {
                    Title = "Krile StarryEyes",
                    MainIcon = VistaTaskDialogIcon.Error,
                    MainInstruction = "Krileの起動に失敗しました。",
                    Content = "設定を保持するディレクトリを作成できません。",
                    ExpandedInfo = ex.ToString(),
                    CommonButtons = TaskDialogCommonButtons.Close,
                    FooterIcon = VistaTaskDialogIcon.Information,
                    FooterText = "別の場所への配置を試みてください。"
                });
            }

            public static void InitLockFileFailed(Exception ex)
            {
                ShowMessage(new TaskDialogOptions
                {
                    Title = "Krile StarryEyes",
                    MainIcon = VistaTaskDialogIcon.Error,
                    MainInstruction = "Krileの起動に失敗しました。",
                    Content = "起動状態ファイルを作成できません。",
                    ExpandedInfo = ex.ToString(),
                    CommonButtons = TaskDialogCommonButtons.Close,
                    FooterIcon = VistaTaskDialogIcon.Information,
                    FooterText = "症状が改善しない場合は、Windowsを再起動してみてください。"
                });
            }

            public static void GeneralProcessFailed(Exception ex)
            {
                ShowMessage(new TaskDialogOptions
                {
                    Title = "Krile StarryEyes",
                    MainIcon = VistaTaskDialogIcon.Error,
                    MainInstruction = "処理が正常に完了しませんでした。",
                    Content = "Krileを再起動してもう一度試すか、コンピュータを再起動してみてください。",
                    ExpandedInfo = ex.ToString(),
                    CommonButtons = TaskDialogCommonButtons.Close,
                });
            }
        }

        #endregion

        private static TaskDialogResult ShowMessage(TaskDialogOptions option)
        {
            // Suppress auto shutdown
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                return TaskDialog.Show(option);
            }
            finally
            {
                Application.Current.ShutdownMode = ShutdownMode.OnLastWindowClose;
            }
        }
    }
}
