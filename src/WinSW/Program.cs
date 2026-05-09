using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;
using log4net.Layout;
using Microsoft.Win32;
using WinSW.Logging;
using WinSW.Native;
using WinSW.Util;
using static WinSW.Native.ServiceApis;
using Process = System.Diagnostics.Process;
using TimeoutException = System.ServiceProcess.TimeoutException;

namespace WinSW
{
    // NOTE: Keep description strings in sync with docs.
    public static class Program
    {
        private const string NoPipe = "-";

        private static readonly ILog Log = LogManager.GetLogger(LoggerNames.Console);

        internal static Action<Exception>? TestExceptionHandler;
        internal static XmlServiceConfig? TestConfig;
        internal static string? TestExecutablePath;

        private static string ExecutablePath
        {
            get
            {
                if (TestExecutablePath != null)
                {
                    return TestExecutablePath;
                }

                using var current = Process.GetCurrentProcess();
                return current.MainModule!.FileName!;
            }
        }

        internal static int Main(string[] args)
        {
            bool elevated;
            if (args.Length > 0 && args[0] == "--elevated")
            {
                elevated = true;

                _ = ConsoleApis.FreeConsole();
                _ = ConsoleApis.AttachConsole(ConsoleApis.ATTACH_PARENT_PROCESS);

                string stdinName = args[1];
                if (stdinName != NoPipe)
                {
                    var stdin = new NamedPipeClientStream(".", stdinName, PipeDirection.In, PipeOptions.Asynchronous);
                    stdin.Connect();
                    Console.SetIn(new StreamReader(stdin));
                }

                string stdoutName = args[2];
                if (stdoutName != NoPipe)
                {
                    var stdout = new NamedPipeClientStream(".", stdoutName, PipeDirection.Out, PipeOptions.Asynchronous);
                    stdout.Connect();
                    Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
                }

                string stderrName = args[3];
                if (stderrName != NoPipe)
                {
                    var stderr = new NamedPipeClientStream(".", stderrName, PipeDirection.Out, PipeOptions.Asynchronous);
                    stderr.Connect();
                    Console.SetError(new StreamWriter(stderr) { AutoFlush = true });
                }

                args = args[4..];
            }
            else if (Environment.OSVersion.Version.Major == 5)
            {
                // Windows XP
                elevated = true;
            }
            else
            {
                elevated = IsProcessElevated();
            }

            var serviceConfig = new Argument<string?>("path-to-config")
            {
                Arity = ArgumentArity.ZeroOrOne,
                Hidden = true,
            };

            var root = new RootCommand("A wrapper binary that can be used to host executables as Windows services. https://github.com/winsw/winsw");

            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                if (principal.IsInRole(new SecurityIdentifier(WellKnownSidType.ServiceSid, null)) ||
                    principal.IsInRole(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)) ||
                    principal.IsInRole(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null)) ||
                    principal.IsInRole(new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null)))
                {
                    root.Add(serviceConfig);
                }
            }

            root.SetAction(parseResult =>
            {
                try { Run(parseResult.GetValue(serviceConfig)); return 0; }
                catch (Exception e) { TestExceptionHandler?.Invoke(e); return HandleException(e); }
            });

            var config = new Argument<string?>("path-to-config")
            {
                Description = "The path to the configuration file.",
                Arity = ArgumentArity.ZeroOrOne,
            };

            var noElevate = new Option<bool>("--no-elevate") { Description = "Doesn't automatically trigger a UAC prompt." };

            {
                var username = new Option<string?>("--username", "--user") { Description = "Specifies the user name of the service account." };
                var password = new Option<string?>("--password", "--pass") { Description = "Specifies the password of the service account." };

                var install = new Command("install", "Installs the service.")
                {
                    config,
                    noElevate,
                    username,
                    password,
                };
                install.SetAction(parseResult =>
                {
                    try { Install(parseResult.GetValue(config), parseResult.GetValue(noElevate), parseResult.GetValue(username), parseResult.GetValue(password)); return 0; }
                    catch (Exception e) { TestExceptionHandler?.Invoke(e); return HandleException(e); }
                });

                root.Add(install);
            }

            {
                var uninstall = new Command("uninstall", "Uninstalls the service.")
                {
                    config,
                    noElevate,
                };
                uninstall.SetAction(parseResult =>
                {
                    try { Uninstall(parseResult.GetValue(config), parseResult.GetValue(noElevate)); return 0; }
                    catch (Exception e) { TestExceptionHandler?.Invoke(e); return HandleException(e); }
                });

                root.Add(uninstall);
            }

            {
                var noWait = new Option<bool>("--no-wait") { Description = "Doesn't wait for the service to actually start." };

                var start = new Command("start", "Starts the service.")
                {
                    config,
                    noElevate,
                    noWait,
                };
                start.SetAction((parseResult, ct) =>
                {
                    try { Start(parseResult.GetValue(config), parseResult.GetValue(noElevate), parseResult.GetValue(noWait), ct); return Task.FromResult(0); }
                    catch (Exception e) { TestExceptionHandler?.Invoke(e); return Task.FromResult(HandleException(e)); }
                });

                root.Add(start);
            }

            {
                var noWait = new Option<bool>("--no-wait") { Description = "Doesn't wait for the service to actually stop." };
                var force = new Option<bool>("--force") { Description = "Stops the service even if it has started dependent services." };

                var stop = new Command("stop", "Stops the service.")
                {
                    config,
                    noElevate,
                    noWait,
                    force,
                };
                stop.SetAction((parseResult, ct) =>
                {
                    try { Stop(parseResult.GetValue(config), parseResult.GetValue(noElevate), parseResult.GetValue(noWait), parseResult.GetValue(force), ct); return Task.FromResult(0); }
                    catch (Exception e) { TestExceptionHandler?.Invoke(e); return Task.FromResult(HandleException(e)); }
                });

                root.Add(stop);
            }

            {
                var force = new Option<bool>("--force") { Description = "Restarts the service even if it has started dependent services." };

                var restart = new Command("restart", "Stops and then starts the service.")
                {
                    config,
                    noElevate,
                    force,
                };
                restart.SetAction((parseResult, ct) =>
                {
                    try { Restart(parseResult.GetValue(config), parseResult.GetValue(noElevate), parseResult.GetValue(force), ct); return Task.FromResult(0); }
                    catch (Exception e) { TestExceptionHandler?.Invoke(e); return Task.FromResult(HandleException(e)); }
                });

                root.Add(restart);
            }

            {
                var restartSelf = new Command("restart!", "self-restart (can be called from child processes)")
                {
                    config,
                };
                restartSelf.SetAction(parseResult =>
                {
                    try { RestartSelf(parseResult.GetValue(config)); return 0; }
                    catch (Exception e) { TestExceptionHandler?.Invoke(e); return HandleException(e); }
                });

                root.Add(restartSelf);
            }

            {
                var status = new Command("status", "Checks the status of the service.")
                {
                    config,
                };
                status.SetAction(parseResult =>
                {
                    try { return Status(parseResult.GetValue(config)); }
                    catch (Exception e) { TestExceptionHandler?.Invoke(e); return HandleException(e); }
                });

                root.Add(status);
            }

            {
                var refresh = new Command("refresh", "Refreshes the service properties without reinstallation.")
                {
                    config,
                    noElevate,
                };
                refresh.SetAction(parseResult =>
                {
                    try { Refresh(parseResult.GetValue(config), parseResult.GetValue(noElevate)); return 0; }
                    catch (Exception e) { TestExceptionHandler?.Invoke(e); return HandleException(e); }
                });

                root.Add(refresh);
            }

            {
                var output = new Option<string>("--output", "-o")
                {
                    Required = true,
                };

                var manufacturer = new Option<string>("--manufacturer")
                {
                    Required = true,
                };
                manufacturer.Validators.Add(result =>
                {
                    const int minLength = 12;
                    const int maxLength = 15;

                    string token = result.Tokens.Single().Value;
                    int length = token.Length;
                    if (length < minLength)
                        result.AddError($"The length of argument '{token}' must be greater than or equal to {minLength}.");
                    else if (length > maxLength)
                        result.AddError($"The length of argument '{token}' must be less than or equal to {maxLength}.");
                });

                var customize = new Command("customize", "Customizes the wrapper executable.")
                {
                    output,
                    manufacturer,
                };
                customize.SetAction(parseResult =>
                {
                    try { Customize(parseResult.GetValue(output)!, parseResult.GetValue(manufacturer)!); return 0; }
                    catch (Exception e) { TestExceptionHandler?.Invoke(e); return HandleException(e); }
                });

                root.Add(customize);
            }

            {
                var dev = new Command("dev", "Experimental commands.");

                root.Add(dev);

                {
                    var all = new Option<bool>("--all", "-a");

                    var ps = new Command("ps", "Draws the process tree associated with the service.")
                    {
                        config,
                        all,
                    };
                    ps.SetAction(parseResult =>
                    {
                        try { DevPs(parseResult.GetValue(config), parseResult.GetValue(all)); return 0; }
                        catch (Exception e) { TestExceptionHandler?.Invoke(e); return HandleException(e); }
                    });

                    dev.Add(ps);
                }

                {
                    var kill = new Command("kill", "Terminates the service if it has stopped responding.")
                    {
                        config,
                        noElevate,
                    };
                    kill.SetAction(parseResult =>
                    {
                        try { DevKill(parseResult.GetValue(config), parseResult.GetValue(noElevate)); return 0; }
                        catch (Exception e) { TestExceptionHandler?.Invoke(e); return HandleException(e); }
                    });

                    dev.Add(kill);
                }

                {
                    var list = new Command("list", "Lists services managed by the current executable.");
                    list.SetAction(parseResult =>
                    {
                        try { DevList(); return 0; }
                        catch (Exception e) { TestExceptionHandler?.Invoke(e); return HandleException(e); }
                    });

                    dev.Add(list);
                }
            }

            return root.Parse(args).Invoke(new InvocationConfiguration
            {
                ProcessTerminationTimeout = Timeout.InfiniteTimeSpan,
            });

            static int HandleException(Exception exception)
            {
                switch (exception)
                {
                    case InvalidDataException e:
                        Log.Fatal("The configuration file could not be loaded. " + e.Message, e);
                        return -1;

                    case OperationCanceledException e:
                        Log.Fatal(e.Message);
                        return -1;

                    case CommandException e:
                        Log.Fatal(e.Message);
                        return e.InnerException is Win32Exception ce ? ce.NativeErrorCode : -1;

                    case InvalidOperationException e when e.InnerException is Win32Exception inner:
                        Log.Fatal(e.Message);
                        return inner.NativeErrorCode;

                    case Win32Exception e:
                        Log.Fatal(e.Message, e);
                        return e.NativeErrorCode;

                    default:
                        Log.Fatal("Unhandled exception", exception);
                        return -1;
                }
            }

            static void Run(string? pathToConfig)
            {
                XmlServiceConfig config = null!;
                try
                {
                    config = LoadConfigAndInitLoggers(pathToConfig, false);
                }
                catch (FileNotFoundException)
                {
                    Throw.Command.Exception("The specified command or file was not found.");
                }

                Log.Debug("Starting WinSW in service mode.");

                AutoRefresh(config);

                using var service = new WrapperService(config);
                try
                {
                    ServiceBase.Run(service);
                }
                catch
                {
                    // handled in OnStart
                }
            }

            void Install(string? pathToConfig, bool noElevate, string? username, string? password)
            {
                var config = LoadConfigAndInitLoggers(pathToConfig, true);

                if (!elevated)
                {
                    Elevate(noElevate);
                    return;
                }

                Log.Info($"Installing service '{config.Format()}'...");

                using var scm = ServiceManager.Open(ServiceManagerAccess.CreateService);

                if (scm.ServiceExists(config.Name))
                {
                    Log.Error($"A service with ID '{config.Name}' already exists.");
                    Throw.Command.Win32Exception(Errors.ERROR_SERVICE_EXISTS, "Failed to install the service.");
                }

                bool saveCredential = false;
                if (config.HasServiceAccount())
                {
                    username = config.ServiceAccountUserName ?? username;
                    password = config.ServiceAccountPassword ?? password;

                    if (username is null || password is null && !Security.IsSpecialAccount(username))
                    {
                        switch (config.ServiceAccountPrompt)
                        {
                            case "dialog":
                                if (!Credentials.Load($"WinSW:{config.Name}", out username, out password))
                                {
                                    Credentials.PromptForCredentialsDialog(
                                        ref username,
                                        ref password,
                                        "Windows Service Wrapper",
                                        "Enter the service account credentials",
                                        ref saveCredential);
                                }

                                break;

                            case "console":
                                Credentials.PromptForCredentialsConsole(ref username, ref password);
                                break;
                        }
                    }
                }

                if (username != null && !Security.IsSpecialAccount(username))
                {
                    Security.AddServiceLogonRight(ref username);
                }

                using var sc = scm.CreateService(
                    config.Name,
                    config.DisplayName,
                    config.StartMode,
                    $"\"{config.ExecutablePath}\"" + (pathToConfig is null ? null : $" \"{Path.GetFullPath(pathToConfig)}\""),
                    config.ServiceDependencies,
                    username,
                    password);

                if (saveCredential)
                {
                    Credentials.Save($"WinSW:{config.Name}", username, password);
                }

                string description = config.Description;
                if (description.Length != 0)
                {
                    sc.SetDescription(description);
                }

                var actions = config.FailureActions;
                if (actions.Length > 0)
                {
                    sc.SetFailureActions(config.ResetFailureAfter, actions);
                }

                bool isDelayedAutoStart = config.StartMode == ServiceStartMode.Automatic && config.DelayedAutoStart;
                if (isDelayedAutoStart)
                {
                    sc.SetDelayedAutoStart(true);
                }

                if (config.PreshutdownTimeout is TimeSpan preshutdownTimeout)
                {
                    sc.SetPreshutdownTimeout(preshutdownTimeout);
                }

                string? securityDescriptor = config.SecurityDescriptor;
                if (securityDescriptor != null)
                {
                    // throws ArgumentException
                    sc.SetSecurityDescriptor(new RawSecurityDescriptor(securityDescriptor));
                }

                string eventLogSource = config.Name;
                if (!EventLog.SourceExists(eventLogSource))
                {
                    EventLog.CreateEventSource(eventLogSource, "Application");
                }

                Log.Info($"Service '{config.Format()}' was installed successfully.");
            }

            void Uninstall(string? pathToConfig, bool noElevate)
            {
                var config = LoadConfigAndInitLoggers(pathToConfig, true);

                if (!elevated)
                {
                    Elevate(noElevate);
                    return;
                }

                Log.Info($"Uninstalling service '{config.Format()}'...");

                using var scm = ServiceManager.Open(ServiceManagerAccess.Connect);
                try
                {
                    using var sc = scm.OpenService(config.Name);

                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        // We could fail the opeartion here, but it would be an incompatible change.
                        // So it is just a warning
                        Log.Warn($"Service '{config.Format()}' is started. It may be impossible to uninstall it.");
                    }

                    sc.Delete();

                    Log.Info($"Service '{config.Format()}' was uninstalled successfully.");
                }
                catch (CommandException e) when (e.InnerException is Win32Exception inner)
                {
                    switch (inner.NativeErrorCode)
                    {
                        case Errors.ERROR_SERVICE_DOES_NOT_EXIST:
                            Log.Warn($"Service '{config.Format()}' does not exist.");
                            break; // there's no such service, so consider it already uninstalled

                        case Errors.ERROR_SERVICE_MARKED_FOR_DELETE:
                            Log.Error(e.Message);

                            // TODO: change the default behavior to Error?
                            break; // it's already uninstalled, so consider it a success

                        default:
                            Throw.Command.Exception("Failed to uninstall the service.", inner);
                            break;
                    }
                }
            }

            void Start(string? pathToConfig, bool noElevate, bool noWait, CancellationToken ct)
            {
                var config = LoadConfigAndInitLoggers(pathToConfig, true);

                if (!elevated)
                {
                    Elevate(noElevate);
                    return;
                }

                AutoRefresh(config);

                using var svc = new ServiceController(config.Name);

                try
                {
                    Log.Info($"Starting service '{svc.Format()}'...");
                    svc.Start();

                    if (!noWait)
                    {
                        try
                        {
                            svc.WaitForStatus(ServiceControllerStatus.Running, ServiceControllerStatus.StartPending, ct);
                        }
                        catch (TimeoutException)
                        {
                            Throw.Command.Exception("Failed to start the service.");
                        }
                    }

                    Log.Info($"Service '{svc.Format()}' started successfully.");
                }
                catch (InvalidOperationException e)
                when (e.InnerException is Win32Exception inner && inner.NativeErrorCode == Errors.ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    Throw.Command.Exception(inner);
                }
                catch (InvalidOperationException e)
                when (e.InnerException is Win32Exception inner && inner.NativeErrorCode == Errors.ERROR_SERVICE_ALREADY_RUNNING)
                {
                    Log.Info($"Service '{svc.Format()}' has already started.");
                }
            }

            void Stop(string? pathToConfig, bool noElevate, bool noWait, bool force, CancellationToken ct)
            {
                var config = LoadConfigAndInitLoggers(pathToConfig, true);

                if (!elevated)
                {
                    Elevate(noElevate);
                    return;
                }

                AutoRefresh(config);

                using var svc = new ServiceController(config.Name);

                try
                {
                    if (!force)
                    {
                        if (svc.HasAnyStartedDependentService())
                        {
                            Throw.Command.Exception("Failed to stop the service because it has started dependent services. Specify '--force' to proceed.");
                        }
                    }

                    Log.Info($"Stopping service '{svc.Format()}'...");
                    svc.Stop();

                    if (!noWait)
                    {
                        try
                        {
                            svc.WaitForStatus(ServiceControllerStatus.Stopped, ServiceControllerStatus.StopPending, ct);
                        }
                        catch (TimeoutException)
                        {
                            Throw.Command.Exception("Failed to stop the service.");
                        }
                    }

                    Log.Info($"Service '{svc.Format()}' stopped successfully.");
                }
                catch (InvalidOperationException e)
                when (e.InnerException is Win32Exception inner && inner.NativeErrorCode == Errors.ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    Throw.Command.Exception(inner);
                }
                catch (InvalidOperationException e)
                when (e.InnerException is Win32Exception inner && inner.NativeErrorCode == Errors.ERROR_SERVICE_NOT_ACTIVE)
                {
                    Log.Info($"Service '{svc.Format()}' has already stopped.");
                }
            }

            void Restart(string? pathToConfig, bool noElevate, bool force, CancellationToken ct)
            {
                var config = LoadConfigAndInitLoggers(pathToConfig, true);

                if (!elevated)
                {
                    Elevate(noElevate);
                    return;
                }

                AutoRefresh(config);

                using var svc = new ServiceController(config.Name);

                List<ServiceController>? startedDependentServices = null;

                try
                {
                    if (svc.HasAnyStartedDependentService())
                    {
                        if (!force)
                        {
                            Throw.Command.Exception("Failed to restart the service because it has started dependent services. Specify '--force' to proceed.");
                        }

                        startedDependentServices = svc.DependentServices.Where(service => service.Status != ServiceControllerStatus.Stopped).ToList();
                    }

                    Log.Info($"Stopping service '{svc.Format()}'...");
                    svc.Stop();

                    try
                    {
                        svc.WaitForStatus(ServiceControllerStatus.Stopped, ServiceControllerStatus.StopPending, ct);
                    }
                    catch (TimeoutException)
                    {
                        Throw.Command.Exception("Failed to stop the service.");
                    }
                }
                catch (InvalidOperationException e)
                when (e.InnerException is Win32Exception inner && inner.NativeErrorCode == Errors.ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    Throw.Command.Exception(inner);
                }
                catch (InvalidOperationException e)
                when (e.InnerException is Win32Exception inner && inner.NativeErrorCode == Errors.ERROR_SERVICE_NOT_ACTIVE)
                {
                }

                Log.Info($"Starting service '{svc.Format()}'...");
                svc.Start();

                try
                {
                    svc.WaitForStatus(ServiceControllerStatus.Running, ServiceControllerStatus.StartPending, ct);
                }
                catch (TimeoutException)
                {
                    Throw.Command.Exception("Failed to start the service.");
                }

                if (startedDependentServices != null)
                {
                    foreach (var service in startedDependentServices)
                    {
                        if (service.Status == ServiceControllerStatus.Stopped)
                        {
                            Log.Info($"Starting service '{service.Format()}'...");
                            service.Start();
                        }
                    }
                }

                Log.Info($"Service '{svc.Format()}' restarted successfully.");
            }

            void RestartSelf(string? pathToConfig)
            {
                var config = LoadConfigAndInitLoggers(pathToConfig, true);

                if (!elevated)
                {
                    Throw.Command.Win32Exception(Errors.ERROR_ACCESS_DENIED);
                }

                AutoRefresh(config);

                // run restart from another process group. see README.md for why this is useful.
                if (!ProcessApis.CreateProcess(
                    null,
                    $"\"{config.ExecutablePath}\" restart" + (pathToConfig is null ? null : $" \"{pathToConfig}\""),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ProcessApis.CREATE_NEW_PROCESS_GROUP,
                    IntPtr.Zero,
                    null,
                    default,
                    out var processInfo))
                {
                    Throw.Command.Win32Exception("Failed to invoke restart.");
                }

                _ = HandleApis.CloseHandle(processInfo.ProcessHandle);
                _ = HandleApis.CloseHandle(processInfo.ThreadHandle);
            }

            static int Status(string? pathToConfig)
            {
                var config = LoadConfigAndInitLoggers(pathToConfig, true);

                using var svc = new ServiceController(config.Name);
                try
                {
                    Console.WriteLine(svc.Status switch
                    {
                        ServiceControllerStatus.StartPending => "Active (starting)",
                        ServiceControllerStatus.StopPending => "Active (stopping)",
                        ServiceControllerStatus.Running => "Active (running)",
                        ServiceControllerStatus.ContinuePending => "Active (continuing)",
                        ServiceControllerStatus.PausePending => "Active (pausing)",
                        ServiceControllerStatus.Paused => "Active (paused)",
                        _ => "Inactive (stopped)"
                    });

                    return svc.Status switch
                    {
                        ServiceControllerStatus.Stopped => 0,
                        ServiceControllerStatus.Running => 0,
                        _ => 1
                    };
                }
                catch (InvalidOperationException e)
                when (e.InnerException is Win32Exception inner && inner.NativeErrorCode == Errors.ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    Console.WriteLine("NonExistent");
                    return Errors.ERROR_SERVICE_DOES_NOT_EXIST;
                }
            }

            void Refresh(string? pathToConfig, bool noElevate)
            {
                var config = LoadConfigAndInitLoggers(pathToConfig, true);

                if (!elevated)
                {
                    Elevate(noElevate);
                    return;
                }

                DoRefresh(config);
            }

            static unsafe void DevPs(string? pathToConfig, bool all)
            {
                if (all)
                {
                    using var scm = ServiceManager.Open(ServiceManagerAccess.EnumerateService);
                    int prevProcessId = -1;
                    foreach (var status in scm.EnumerateServices())
                    {
                        using var sc = scm.OpenService(status->ServiceName, ServiceAccess.QueryConfig | ServiceAccess.QueryStatus);
                        if (sc.ExecutablePath.StartsWith($"\"{ExecutablePath}\""))
                        {
                            int processId = sc.ProcessId;
                            if (processId >= 0)
                            {
                                if (prevProcessId >= 0)
                                {
                                    using var process = Process.GetProcessById(prevProcessId);
                                    Draw(process, string.Empty, false);
                                }
                            }

                            prevProcessId = processId;
                        }
                    }

                    if (prevProcessId >= 0)
                    {
                        using var process = Process.GetProcessById(prevProcessId);
                        Draw(process, string.Empty, true);
                    }
                }
                else
                {
                    var config = LoadConfigAndInitLoggers(pathToConfig, true);

                    using var scm = ServiceManager.Open(ServiceManagerAccess.Connect);
                    using var sc = scm.OpenService(config.Name, ServiceAccess.QueryStatus);

                    int processId = sc.ProcessId;
                    if (processId >= 0)
                    {
                        using var process = Process.GetProcessById(processId);
                        Draw(process, string.Empty, true);
                    }
                }

                static void Draw(Process process, string indentation, bool isLastChild)
                {
                    const string Vertical = " \u2502 ";
                    const string Corner = " \u2514\u2500";
                    const string Cross = " \u251c\u2500";
                    const string Space = "   ";

                    Console.Write(indentation);

                    if (isLastChild)
                    {
                        Console.Write(Corner);
                        indentation += Space;
                    }
                    else
                    {
                        Console.Write(Cross);
                        indentation += Vertical;
                    }

                    Console.WriteLine(process.Format());

                    var children = process.GetChildren();
                    int count = children.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var child = children[i];
                        using (child.Process)
                        using (child.Handle)
                        {
                            Draw(child.Process, indentation, i == count - 1);
                        }
                    }
                }
            }

            void DevKill(string? pathToConfig, bool noElevate)
            {
                var config = LoadConfigAndInitLoggers(pathToConfig, true);

                if (!elevated)
                {
                    Elevate(noElevate);
                    return;
                }

                using var scm = ServiceManager.Open();
                using var sc = scm.OpenService(config.Name);

                int processId = sc.ProcessId;
                if (processId >= 0)
                {
                    using var process = Process.GetProcessById(processId);

                    process.StopDescendants(config.StopTimeoutInMs);
                }
            }

            static unsafe void DevList()
            {
                using var scm = ServiceManager.Open(ServiceManagerAccess.EnumerateService);
                foreach (var status in scm.EnumerateServices())
                {
                    using var sc = scm.OpenService(status->ServiceName, ServiceAccess.QueryConfig);
                    if (sc.ExecutablePath.StartsWith($"\"{ExecutablePath}\""))
                    {
                        Console.WriteLine(status->ToString());
                    }
                }
            }

            static void Customize(string output, string manufacturer)
            {
                if (Resources.UpdateCompanyName(ExecutablePath, output, manufacturer))
                {
                    Console.WriteLine("The operation succeeded.");
                }
                else
                {
                    Console.Error.WriteLine("The operation failed.");
                }
            }

            // [DoesNotReturn]
            static void Elevate(bool noElevate)
            {
                if (noElevate)
                {
                    Throw.Command.Win32Exception(Errors.ERROR_ACCESS_DENIED);
                }

                string? stdinName = Console.IsInputRedirected ? Guid.NewGuid().ToString() : null;
                string? stdoutName = Console.IsOutputRedirected ? Guid.NewGuid().ToString() : null;
                string? stderrName = Console.IsErrorRedirected ? Guid.NewGuid().ToString() : null;

                string exe = Environment.GetCommandLineArgs()[0];
                string commandLine = Environment.CommandLine;
                string arguments = "--elevated" +
                    " " + (stdinName ?? NoPipe) +
                    " " + (stdoutName ?? NoPipe) +
                    " " + (stderrName ?? NoPipe) +
                    commandLine.Remove(commandLine.IndexOf(exe), exe.Length).TrimStart('"');

                var startInfo = new ProcessStartInfo
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    FileName = ExecutablePath,
                    Arguments = arguments,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };

                try
                {
                    using var elevated = Process.Start(startInfo)!;

                    if (stdinName is not null)
                    {
                        var stdin = new NamedPipeServerStream(stdinName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        stdin.WaitForConnectionAsync().ContinueWith(_ => Console.OpenStandardInput().CopyToAsync(stdin));
                    }

                    if (stdoutName is not null)
                    {
                        var stdout = new NamedPipeServerStream(stdoutName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        stdout.WaitForConnectionAsync().ContinueWith(_ => stdout.CopyToAsync(Console.OpenStandardOutput()));
                    }

                    if (stderrName is not null)
                    {
                        var stderr = new NamedPipeServerStream(stderrName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        stderr.WaitForConnectionAsync().ContinueWith(_ => stderr.CopyToAsync(Console.OpenStandardError()));
                    }

                    elevated.WaitForExit();
                    Environment.Exit(elevated.ExitCode);
                }
                catch (Win32Exception e) when (e.NativeErrorCode == Errors.ERROR_CANCELLED)
                {
                    Log.Fatal(e.Message);
                    Environment.Exit(e.ErrorCode);
                }
            }

            static void AutoRefresh(XmlServiceConfig config)
            {
                if (!config.AutoRefresh)
                {
                    return;
                }

                var fileLastWriteTime = File.GetLastWriteTime(config.FullPath);

                using var registryKey = Registry.LocalMachine
                    .OpenSubKey("SYSTEM")?
                    .OpenSubKey("CurrentControlSet")?
                    .OpenSubKey("Services")?
                    .OpenSubKey(config.Name);

                if (registryKey is null)
                {
                    return;
                }

                var registryLastWriteTime = registryKey.GetLastWriteTime();

                if (fileLastWriteTime > registryLastWriteTime)
                {
                    DoRefresh(config);
                }
            }

            static void DoRefresh(XmlServiceConfig config)
            {
                using var scm = ServiceManager.Open(ServiceManagerAccess.Connect);
                try
                {
                    using var sc = scm.OpenService(config.Name);

                    sc.ChangeConfig(config.DisplayName, config.StartMode, config.ServiceDependencies);

                    sc.SetDescription(config.Description);

                    var actions = config.FailureActions;
                    if (actions.Length > 0)
                    {
                        sc.SetFailureActions(config.ResetFailureAfter, actions);
                    }

                    bool isDelayedAutoStart = config.StartMode == ServiceStartMode.Automatic && config.DelayedAutoStart;
                    if (isDelayedAutoStart)
                    {
                        sc.SetDelayedAutoStart(true);
                    }

                    if (config.PreshutdownTimeout is TimeSpan preshutdownTimeout)
                    {
                        sc.SetPreshutdownTimeout(preshutdownTimeout);
                    }

                    string? securityDescriptor = config.SecurityDescriptor;
                    if (securityDescriptor != null)
                    {
                        // throws ArgumentException
                        sc.SetSecurityDescriptor(new RawSecurityDescriptor(securityDescriptor));
                    }
                }
                catch (CommandException e)
                when (e.InnerException is Win32Exception inner && inner.NativeErrorCode == Errors.ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    Throw.Command.Exception(inner);
                }

                Log.Info($"Service '{config.Format()}' was refreshed successfully.");
            }
        }

        /// <exception cref="FileNotFoundException" />
        private static XmlServiceConfig LoadConfigAndInitLoggers(string? path, bool inConsoleMode)
        {
            if (TestConfig != null)
            {
                return TestConfig;
            }

            // TODO: Make logging levels configurable
            var fileLogLevel = Level.Debug;

            // TODO: Debug should not be printed to console by default. Otherwise commands like 'status' will be pollutted
            // This is a workaround till there is a better command line parsing, which will allow determining
            var consoleLogLevel = Level.Info;
            var eventLogLevel = Level.Warn;

            var repository = LogManager.GetRepository(Assembly.GetExecutingAssembly());

            if (inConsoleMode)
            {
                var consoleAppender = new WinSWConsoleAppender
                {
                    Name = "Wrapper console log",
                    Threshold = consoleLogLevel,
                    Layout = new PatternLayout("%message%newline"),
                };
                consoleAppender.ActivateOptions();

                BasicConfigurator.Configure(repository, consoleAppender);
            }
            else
            {
                var eventLogAppender = new ServiceEventLogAppender(WrapperService.EventLogProvider)
                {
                    Name = "Wrapper event log",
                    Threshold = eventLogLevel,
                };
                eventLogAppender.ActivateOptions();

                BasicConfigurator.Configure(repository, eventLogAppender);
            }

            XmlServiceConfig config;
            if (path != null)
            {
                config = new XmlServiceConfig(path);
            }
            else
            {
                path = Path.ChangeExtension(ExecutablePath, ".xml");
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("Unable to locate " + Path.GetFileNameWithoutExtension(path) + ".xml file within executable directory.");
                }

                config = new XmlServiceConfig(path);
            }

            // .wrapper.log
            string wrapperLogPath = Path.Combine(config.LogDirectory, config.BaseName + ".wrapper.log");
            var fileAppender = new FileAppender
            {
                AppendToFile = true,
                File = wrapperLogPath,
                ImmediateFlush = true,
                Name = "Wrapper file log",
                Threshold = fileLogLevel,
                LockingModel = new FileAppender.MinimalLock(),
                Layout = new PatternLayout("%date{yyyy-MM-ddTHH:mm:ss.fff} %-5level %logger - %message%newline"),
            };
            fileAppender.ActivateOptions();

            BasicConfigurator.Configure(repository, fileAppender);

            return config;
        }

        /// <exception cref="CommandException" />
        internal static bool IsProcessElevated()
        {
            var process = ProcessApis.GetCurrentProcess();
            if (!ProcessApis.OpenProcessToken(process, TokenAccessLevels.Read, out var token))
            {
                Throw.Command.Win32Exception("Failed to open process token.");
            }

            using (token)
            {
                unsafe
                {
                    if (!SecurityApis.GetTokenInformation(
                        token,
                        SecurityApis.TOKEN_INFORMATION_CLASS.TokenElevation,
                        out var elevation,
                        sizeof(SecurityApis.TOKEN_ELEVATION),
                        out _))
                    {
                        Throw.Command.Win32Exception("Failed to get token information.");
                    }

                    return elevation.TokenIsElevated != 0;
                }
            }
        }
    }
}
