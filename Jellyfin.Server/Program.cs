using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CommandLine;
using Emby.Server.Implementations;
using Jellyfin.Server.Extensions;
using Jellyfin.Server.Helpers;
using Jellyfin.Server.Implementations;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Extensions.Logging;
using static MediaBrowser.Controller.Extensions.ConfigurationExtensions;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Jellyfin.Server
{
    /// <summary>
    /// Class containing the entry point of the application.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// The name of logging configuration file containing application defaults.日志配置文件的默认名称.
        /// </summary>
        public const string LoggingConfigFileDefault = "logging.default.json";

        /// <summary>
        /// The name of the logging configuration file containing the system-specific override settings.
        /// </summary>
        public const string LoggingConfigFileSystem = "logging.json";

        private static readonly SerilogLoggerFactory _loggerFactory = new SerilogLoggerFactory();
        private static long _startTimestamp;
        private static ILogger _logger = NullLogger.Instance;
        private static bool _restartOnShutdown;

        /// <summary>
        /// The entry point of the application.
        /// </summary>
        /// <param name="args">The command line arguments passed.</param>
        /// <returns><see cref="Task" />.</returns>
        public static Task Main(string[] args)
        {
            // 内部定义了一个嵌套的 ErrorParsingArguments 方法，该方法用于处理解析命令行参数时发生的错误。如果有错误发生，将设置环境退出码为1，表示发生了错误，然后返回一个已完成的任务。
            static Task ErrorParsingArguments(IEnumerable<Error> errors)
            {
                Environment.ExitCode = 1;
                return Task.CompletedTask;
            }

            // Parse the command line arguments and either start the app or exit indicating error
            // 这里使用了 Parser.Default.ParseArguments 方法来解析命令行参数，期望的参数类型是 StartupOptions
            // MapResult 方法根据解析结果执行相应的操作。如果解析成功，则调用 StartApp 方法；如果解析失败，则调用上面定义的 ErrorParsingArguments 方法
            return Parser.Default.ParseArguments<StartupOptions>(args)
                .MapResult(StartApp, ErrorParsingArguments);
        }

        private static async Task StartApp(StartupOptions options)
        {
            // 记录了启动应用程序时的时间戳
            _startTimestamp = Stopwatch.GetTimestamp();
            // 创建应用程序的路径信息。这些路径可能包括日志目录、Web内容路径等。
            ServerApplicationPaths appPaths = StartupHelpers.CreateApplicationPaths(options);

            // 指定日志文件的目录路径
            // $JELLYFIN_LOG_DIR needs to be set for the logger configuration manager
            Environment.SetEnvironmentVariable("JELLYFIN_LOG_DIR", appPaths.LogDirectoryPath);

            // 设置了一些与硬件兼容性和特定硬件设置相关的环境变量
            // Enable cl-va P010 interop for tonemapping on Intel VAAPI
            Environment.SetEnvironmentVariable("NEOReadDebugKeys", "1");
            Environment.SetEnvironmentVariable("EnableExtendedVaFormats", "1");

            // 初始化了日志配置文件，如果不存在则复制一个嵌入资源
            await StartupHelpers.InitLoggingConfigFile(appPaths).ConfigureAwait(false);

            // 创建了一个应用程序配置实例，用于应用程序的启动
            // Create an instance of the application configuration to use for application startup
            IConfiguration startupConfig = CreateAppConfiguration(options, appPaths);

            StartupHelpers.InitializeLoggingFramework(startupConfig, appPaths);
            _logger = _loggerFactory.CreateLogger("Main");

            // Use the logging framework for uncaught exceptions instead of std error
            AppDomain.CurrentDomain.UnhandledException += (_, e)
                => _logger.LogCritical((Exception)e.ExceptionObject, "Unhandled Exception");

            _logger.LogInformation(
                "Jellyfin version: {Version}",
                Assembly.GetEntryAssembly()!.GetName().Version!.ToString(3));

            StartupHelpers.LogEnvironmentInfo(_logger, appPaths);

            // If hosting the web client, validate the client content path
            if (startupConfig.HostWebClient())
            {
                var webContentPath = appPaths.WebPath;
                if (!Directory.Exists(webContentPath) || !Directory.EnumerateFiles(webContentPath).Any())
                {
                    _logger.LogError(
                        "The server is expected to host the web client, but the provided content directory is either " +
                        "invalid or empty: {WebContentPath}. If you do not want to host the web client with the " +
                        "server, you may set the '--nowebclient' command line flag, or set" +
                        "'{ConfigKey}=false' in your config settings",
                        webContentPath,
                        HostWebClientKey);
                    Environment.ExitCode = 1;
                    return;
                }
            }

            StartupHelpers.PerformStaticInitialization();
            Migrations.MigrationRunner.RunPreStartup(appPaths, _loggerFactory);

            do
            {
                await StartServer(appPaths, options, startupConfig).ConfigureAwait(false);

                if (_restartOnShutdown)
                {
                    _startTimestamp = Stopwatch.GetTimestamp();
                }
            } while (_restartOnShutdown);
        }

        private static async Task StartServer(IServerApplicationPaths appPaths, StartupOptions options, IConfiguration startupConfig)
        {
            using var appHost = new CoreAppHost(
                appPaths,
                _loggerFactory,
                options,
                startupConfig);

            IHost? host = null;
            try
            {
                host = Host.CreateDefaultBuilder()
                    .UseConsoleLifetime()
                    .ConfigureServices(services => appHost.Init(services))
                    .ConfigureWebHostDefaults(webHostBuilder => webHostBuilder.ConfigureWebHostBuilder(appHost, startupConfig, appPaths, _logger))
                    .ConfigureAppConfiguration(config => config.ConfigureAppConfiguration(options, appPaths, startupConfig))
                    .UseSerilog()
                    .Build();

                // Re-use the host service provider in the app host since ASP.NET doesn't allow a custom service collection.
                appHost.ServiceProvider = host.Services;

                await appHost.InitializeServices().ConfigureAwait(false);
                Migrations.MigrationRunner.Run(appHost, _loggerFactory);

                try
                {
                    await host.StartAsync().ConfigureAwait(false);

                    if (!OperatingSystem.IsWindows() && startupConfig.UseUnixSocket())
                    {
                        var socketPath = StartupHelpers.GetUnixSocketPath(startupConfig, appPaths);

                        StartupHelpers.SetUnixSocketPermissions(startupConfig, socketPath, _logger);
                    }
                }
                catch (Exception)
                {
                    _logger.LogError("Kestrel failed to start! This is most likely due to an invalid address or port bind - correct your bind configuration in network.xml and try again");
                    throw;
                }

                await appHost.RunStartupTasksAsync().ConfigureAwait(false);

                _logger.LogInformation("Startup complete {Time:g}", Stopwatch.GetElapsedTime(_startTimestamp));

                await host.WaitForShutdownAsync().ConfigureAwait(false);
                _restartOnShutdown = appHost.ShouldRestart;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Error while starting server");
            }
            finally
            {
                // Don't throw additional exception if startup failed.
                if (appHost.ServiceProvider is not null)
                {
                    _logger.LogInformation("Running query planner optimizations in the database... This might take a while");
                    // Run before disposing the application
                    var context = await appHost.ServiceProvider.GetRequiredService<IDbContextFactory<JellyfinDbContext>>().CreateDbContextAsync().ConfigureAwait(false);
                    await using (context.ConfigureAwait(false))
                    {
                        if (context.Database.IsSqlite())
                        {
                            await context.Database.ExecuteSqlRawAsync("PRAGMA optimize").ConfigureAwait(false);
                        }
                    }
                }

                host?.Dispose();
            }
        }

        /// <summary>
        /// Create the application configuration.
        /// </summary>
        /// <param name="commandLineOpts">The command line options passed to the program.</param>
        /// <param name="appPaths">The application paths.</param>
        /// <returns>The application configuration.</returns>
        public static IConfiguration CreateAppConfiguration(StartupOptions commandLineOpts, IApplicationPaths appPaths)
        {
            return new ConfigurationBuilder()
                .ConfigureAppConfiguration(commandLineOpts, appPaths)
                .Build();
        }

        private static IConfigurationBuilder ConfigureAppConfiguration(
            this IConfigurationBuilder config,
            StartupOptions commandLineOpts,
            IApplicationPaths appPaths,
            IConfiguration? startupConfig = null)
        {
            // Use the swagger API page as the default redirect path if not hosting the web client
            var inMemoryDefaultConfig = ConfigurationOptions.DefaultConfiguration;
            if (startupConfig is not null && !startupConfig.HostWebClient())
            {
                inMemoryDefaultConfig[DefaultRedirectKey] = "api-docs/swagger";
            }

            return config
                .SetBasePath(appPaths.ConfigurationDirectoryPath)
                .AddInMemoryCollection(inMemoryDefaultConfig)
                .AddJsonFile(LoggingConfigFileDefault, optional: false, reloadOnChange: true)
                .AddJsonFile(LoggingConfigFileSystem, optional: true, reloadOnChange: true)
                .AddEnvironmentVariables("JELLYFIN_")
                .AddInMemoryCollection(commandLineOpts.ConvertToConfig());
        }
    }
}
