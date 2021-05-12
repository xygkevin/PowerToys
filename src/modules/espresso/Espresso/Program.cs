﻿// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using Espresso.Shell.Core;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using NLog;

#pragma warning disable CS8602 // Dereference of a possibly null reference.
#pragma warning disable CS8603 // Possible null reference return.

namespace Espresso.Shell
{
    internal class Program
    {
        private static Mutex? _mutex = null;
        private const string AppName = "Espresso";
        private static FileSystemWatcher? _watcher = null;
        private static SettingsUtils? _settingsUtils = null;

        public static Mutex LockMutex { get => _mutex; set => _mutex = value; }

        private static Logger? _log;

        private static int Main(string[] args)
        {
            bool instantiated;
            LockMutex = new Mutex(true, AppName, out instantiated);

            if (!instantiated)
            {
                ForceExit(AppName + " is already running! Exiting the application.", 1);
            }

            _log = LogManager.GetCurrentClassLogger();
            _settingsUtils = new SettingsUtils();

            _log.Info("Launching Espresso...");
            _log.Info(FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion);
            _log.Debug($"OS: {Environment.OSVersion}");
            _log.Debug($"OS Build: {APIHelper.GetOperatingSystemBuild()}");

            var configOption = new Option<bool>(
                    aliases: new[] { "--use-pt-config", "-c" },
                    getDefaultValue: () => false,
                    description: "Specifies whether Espresso will be using the PowerToys configuration file for managing the state.")
            {
                Argument = new Argument<bool>(() => false)
                {
                    Arity = ArgumentArity.ZeroOrOne,
                },
            };

            configOption.Required = false;

            var displayOption = new Option<bool>(
                    aliases: new[] { "--display-on", "-d" },
                    getDefaultValue: () => true,
                    description: "Determines whether the display should be kept awake.")
            {
                Argument = new Argument<bool>(() => false)
                {
                    Arity = ArgumentArity.ZeroOrOne,
                },
            };

            displayOption.Required = false;

            var timeOption = new Option<long>(
                    aliases: new[] { "--time-limit", "-t" },
                    getDefaultValue: () => 0,
                    description: "Determines the interval, in seconds, during which the computer is kept awake.")
            {
                Argument = new Argument<long>(() => 0)
                {
                    Arity = ArgumentArity.ExactlyOne,
                },
            };

            timeOption.Required = false;

            var pidOption = new Option<int>(
                    aliases: new[] { "--pid", "-p" },
                    getDefaultValue: () => 0,
                    description: "Bind the execution of Espresso to another process.")
            {
                Argument = new Argument<int>(() => 0)
                {
                    Arity = ArgumentArity.ZeroOrOne,
                },
            };

            pidOption.Required = false;

            var rootCommand = new RootCommand
            {
                configOption,
                displayOption,
                timeOption,
                pidOption,
            };

            rootCommand.Description = AppName;

            rootCommand.Handler = CommandHandler.Create<bool, bool, long, int>(HandleCommandLineArguments);

            return rootCommand.InvokeAsync(args).Result;
        }

        private static void ForceExit(string message, int exitCode)
        {
            _log.Debug(message);
            _log.Info(message);
            Console.ReadKey();
            Environment.Exit(exitCode);
        }

        private static void HandleCommandLineArguments(bool usePtConfig, bool displayOn, long timeLimit, int pid)
        {
            if (pid == 0)
            {
                APIHelper.AllocateConsole();
            }

            _log.Info($"The value for --use-pt-config is: {usePtConfig}");
            _log.Info($"The value for --display-on is: {displayOn}");
            _log.Info($"The value for --time-limit is: {timeLimit}");
            _log.Info($"The value for --pid is: {pid}");

            if (usePtConfig)
            {
#pragma warning disable CS8604 // Possible null reference argument.
                TrayHelper.InitializeTray(AppName, new Icon(Application.GetResourceStream(new Uri("/Images/Espresso.ico", UriKind.Relative)).Stream));
#pragma warning restore CS8604 // Possible null reference argument.

                // Configuration file is used, therefore we disregard any other command-line parameter
                // and instead watch for changes in the file.
                try
                {
                    var settingsPath = _settingsUtils.GetSettingsFilePath(AppName);
                    _log.Info($"Reading configuration file: {settingsPath}");

                    _watcher = new FileSystemWatcher
                    {
                        Path = Path.GetDirectoryName(settingsPath),
                        EnableRaisingEvents = true,
                        NotifyFilter = NotifyFilters.LastWrite,
                        Filter = Path.GetFileName(settingsPath),
                    };

                    Observable.FromEventPattern<FileSystemEventHandler, FileSystemEventArgs>(
                            h => _watcher.Changed += h,
                            h => _watcher.Changed -= h)
                        .SubscribeOn(TaskPoolScheduler.Default)
                        .Select(e => e.EventArgs)
                        .Throttle(TimeSpan.FromMilliseconds(25))
                        .Subscribe(HandleEspressoConfigChange);

                    // Initially the file might not be updated, so we need to start processing
                    // settings right away.
                    ProcessSettings();
                }
                catch (Exception ex)
                {
                    var errorString = $"There was a problem with the configuration file. Make sure it exists.\n{ex.Message}";
                    _log.Info(errorString);
                    _log.Debug(errorString);
                }
            }
            else
            {
                var mode = timeLimit <= 0 ? EspressoMode.INDEFINITE : EspressoMode.TIMED;

                if (mode == EspressoMode.INDEFINITE)
                {
                    SetupIndefiniteKeepAwake(displayOn);
                }
                else
                {
                    SetupTimedKeepAwake(timeLimit, displayOn);
                }
            }

            var exitSignal = new ManualResetEvent(false);
            if (pid != 0)
            {
                RunnerHelper.WaitForPowerToysRunner(pid, () =>
                {
                    exitSignal.Set();
                    Environment.Exit(0);
                });
            }

            exitSignal.WaitOne();
        }

        private static void SetupIndefiniteKeepAwake(bool displayOn)
        {
            // Indefinite keep awake.
            APIHelper.SetIndefiniteKeepAwake(LogCompletedKeepAwakeThread, LogUnexpectedOrCancelledKeepAwakeThreadCompletion, displayOn);
        }

        private static void HandleEspressoConfigChange(FileSystemEventArgs fileEvent)
        {
            _log.Info("Detected a settings file change. Updating configuration...");
            _log.Info("Resetting keep-awake to normal state due to settings change.");
            ProcessSettings();
        }

        private static void ProcessSettings()
        {
            try
            {
                EspressoSettings settings = _settingsUtils.GetSettings<EspressoSettings>(AppName);

                if (settings != null)
                {
                    switch (settings.Properties.Mode)
                    {
                        case EspressoMode.INDEFINITE:
                            {
                                // Indefinite keep awake.
                                SetupIndefiniteKeepAwake(settings.Properties.KeepDisplayOn.Value);
                                break;
                            }

                        case EspressoMode.TIMED:
                            {
                                // Timed keep-awake.
                                long computedTime = (settings.Properties.Hours.Value * 60 * 60) + (settings.Properties.Minutes.Value * 60);
                                SetupTimedKeepAwake(computedTime, settings.Properties.KeepDisplayOn.Value);

                                break;
                            }

                        default:
                            {
                                var errorMessage = "Unknown mode of operation. Check config file.";
                                _log.Info(errorMessage);
                                _log.Debug(errorMessage);
                                break;
                            }
                    }

                    TrayHelper.SetTray(AppName, settings);
                }
                else
                {
                    var errorMessage = "Settings are null.";
                    _log.Info(errorMessage);
                    _log.Debug(errorMessage);
                }
            }
            catch (Exception ex)
            {
                var errorMessage = $"There was a problem reading the configuration file. Error: {ex.Message}";
                _log.Info(errorMessage);
                _log.Debug(errorMessage);
            }
        }

        private static void SetupTimedKeepAwake(long time, bool displayOn)
        {
            _log.Info($"Timed keep-awake. Expected runtime: {time} seconds with display on setting set to {displayOn}.");

            APIHelper.SetTimedKeepAwake(time, LogCompletedKeepAwakeThread, LogUnexpectedOrCancelledKeepAwakeThreadCompletion, displayOn);
        }

        private static void LogUnexpectedOrCancelledKeepAwakeThreadCompletion()
        {
            var errorMessage = "The keep-awake thread was terminated early.";
            _log.Info(errorMessage);
            _log.Debug(errorMessage);
        }

        private static void LogCompletedKeepAwakeThread(bool result)
        {
            _log.Info($"Exited keep-awake thread successfully: {result}");
        }
    }
}
