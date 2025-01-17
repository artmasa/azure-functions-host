// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Azure.WebJobs.Script.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Configuration
{
    public class ScriptJobHostOptionsSetupTests
    {
        [Fact]
        public void Configure_FileWatching()
        {
            var settings = new Dictionary<string, string>
            {
                { ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "fileWatchingEnabled"), "true" }
            };

            ScriptJobHostOptionsSetup setup = CreateSetupWithConfiguration(settings);

            var options = new ScriptJobHostOptions();

            // Validate default (this should be in another test - migrated here for now)
            Assert.True(options.FileWatchingEnabled);

            setup.Configure(options);

            Assert.True(options.FileWatchingEnabled);
            Assert.Equal(1, options.WatchDirectories.Count);
            Assert.Equal("node_modules", options.WatchDirectories.ElementAt(0));

            Assert.Equal(3, options.WatchFiles.Count);
            Assert.Contains("host.json", options.WatchFiles);
            Assert.Contains("function.json", options.WatchFiles);
            Assert.Contains("proxies.json", options.WatchFiles);

            // File watching disabled
            settings[ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "fileWatchingEnabled")] = bool.FalseString;

            setup = CreateSetupWithConfiguration(settings);

            options = new ScriptJobHostOptions();
            setup.Configure(options);

            Assert.False(options.FileWatchingEnabled);

            // File watching enabled, watch directories
            settings[ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "fileWatchingEnabled")] = bool.TrueString;
            settings[ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "watchDirectories", "0")] = "Shared";
            settings[ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "watchDirectories", "1")] = "Tools";
            settings[ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "watchFiles", "0")] = "myFirstFile.ext";
            settings[ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "watchFiles", "1")] = "mySecondFile.ext";

            setup = CreateSetupWithConfiguration(settings);

            options = new ScriptJobHostOptions();
            setup.Configure(options);

            Assert.True(options.FileWatchingEnabled);
            Assert.Equal(3, options.WatchDirectories.Count);
            Assert.Equal("node_modules", options.WatchDirectories.ElementAt(0));
            Assert.Equal("Shared", options.WatchDirectories.ElementAt(1));
            Assert.Equal("Tools", options.WatchDirectories.ElementAt(2));

            Assert.Equal(5, options.WatchFiles.Count);
            Assert.Contains("host.json", options.WatchFiles);
            Assert.Contains("function.json", options.WatchFiles);
            Assert.Contains("proxies.json", options.WatchFiles);
            Assert.Contains("myFirstFile.ext", options.WatchFiles);
            Assert.Contains("mySecondFile.ext", options.WatchFiles);
        }

        [Fact(Skip = "ApplyConfiguration no longer exists. Validate logic (moved to HostJsonFileConfigurationSource)")]
        public void Configure_AllowPartialHostStartup()
        {
            //var settings = new Dictionary<string, string>
            //{
            //    { ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "fileWatchingEnabled"), "true" }
            //};

            //var options = new ScriptHostOptions();

            //// Validate default (this should be in another test - migrated here for now)
            //Assert.True(options.FileWatchingEnabled);

            //Assert.True(options.HostConfig.AllowPartialHostStartup);

            //// explicit setting can override our default
            //scriptConfig = new ScriptHostConfiguration();
            //config["allowPartialHostStartup"] = new JValue(true);
            //ScriptHost.ApplyConfiguration(config, scriptConfig);
            //Assert.True(scriptConfig.HostConfig.AllowPartialHostStartup);

            //// explicit setting can override our default
            //scriptConfig = new ScriptHostConfiguration();
            //config["allowPartialHostStartup"] = new JValue(false);
            //ScriptHost.ApplyConfiguration(config, scriptConfig);
            //Assert.False(scriptConfig.HostConfig.AllowPartialHostStartup);
        }

        [Fact]
        public void Configure_AppliesDefaults_IfDynamic()
        {
            var settings = new Dictionary<string, string>();

            var environment = new TestEnvironment();
            environment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteSku, "Dynamic");

            var options = GetConfiguredOptions(settings, environment);

            Assert.Equal(ScriptJobHostOptionsSetup.DefaultFunctionTimeoutDynamic, options.FunctionTimeout);

            // When functionTimeout is set as null
            settings.Add(ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "functionTimeout"), string.Empty);

            options = GetConfiguredOptions(settings, environment);
            Assert.Equal(ScriptJobHostOptionsSetup.DefaultFunctionTimeoutDynamic, options.FunctionTimeout);

            // TODO: DI Need to ensure JobHostOptions is correctly configured
            //var timeoutConfig = options.HostOptions.FunctionTimeout;
            //Assert.NotNull(timeoutConfig);
            //Assert.True(timeoutConfig.ThrowOnTimeout);
            //Assert.Equal(scriptConfig.FunctionTimeout.Value, timeoutConfig.Timeout);
        }

        [Fact]
        public void Configure_AppliesTimeout()
        {
            var settings = new Dictionary<string, string>
            {
                { ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "functionTimeout"), "00:00:30" }
            };

            var options = GetConfiguredOptions(settings);

            Assert.Equal(TimeSpan.FromSeconds(30), options.FunctionTimeout);
        }

        [Fact]
        public void Configure_TimeoutDefaultsNull_IfNotDynamic()
        {
            var options = GetConfiguredOptions(new Dictionary<string, string>());
            Assert.Equal(ScriptJobHostOptionsSetup.DefaultFunctionTimeout, options.FunctionTimeout);

            // When functionTimeout is set as null
            var settings = new Dictionary<string, string>
            {
                { ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "functionTimeout"), string.Empty }
            };

            options = GetConfiguredOptions(settings);
            Assert.Equal(ScriptJobHostOptionsSetup.DefaultFunctionTimeout, options.FunctionTimeout);
        }

        [Fact]
        public void Configure_NoMaxTimeoutLimits_IfNotDynamic()
        {
            var timeout = ScriptJobHostOptionsSetup.MaxFunctionTimeoutDynamic + TimeSpan.FromMinutes(10);
            string configPath = ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "functionTimeout");
            var settings = new Dictionary<string, string>
            {
                { configPath, timeout.ToString() }
            };

            var options = GetConfiguredOptions(settings);
            Assert.Equal(timeout, options.FunctionTimeout);
        }

        [Fact]
        public void Configure_AppliesInfiniteTimeout_IfNotDynamic()
        {
            var settings = new Dictionary<string, string>
            {
                { ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "functionTimeout"), "-1" }
            };

            var options = GetConfiguredOptions(settings);
            Assert.Equal(null, options.FunctionTimeout);
        }

        [Fact]
        public void Configure_AppliesTimeoutLimits_IfDynamic()
        {
            string configPath = ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "functionTimeout");
            var settings = new Dictionary<string, string>
            {
                { configPath, (ScriptJobHostOptionsSetup.MaxFunctionTimeoutDynamic + TimeSpan.FromSeconds(1)).ToString() }
            };

            var environment = new TestEnvironment();
            environment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteSku, "Dynamic");

            var ex = Assert.Throws<ArgumentException>(() => GetConfiguredOptions(settings, environment));
            var expectedMessage = "FunctionTimeout must be greater than 00:00:01 and less than 00:10:00.";
            Assert.Equal(expectedMessage, ex.Message);

            settings[configPath] = (ScriptJobHostOptionsSetup.MinFunctionTimeout - TimeSpan.FromSeconds(1)).ToString();
            ex = Assert.Throws<ArgumentException>(() => GetConfiguredOptions(settings, environment));
            Assert.Equal(expectedMessage, ex.Message);

            settings[configPath] = "-1";
            ex = Assert.Throws<ArgumentException>(() => GetConfiguredOptions(settings, environment));
            Assert.Equal(expectedMessage, ex.Message);
        }

        [Fact]
        public void Configure_MinTimeoutLimit_IfNotDynamic()
        {
            string configPath = ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "functionTimeout");
            var settings = new Dictionary<string, string>
            {
                { configPath, (ScriptJobHostOptionsSetup.MinFunctionTimeout - TimeSpan.FromSeconds(1)).ToString() }
            };

            var ex = Assert.Throws<ArgumentException>(() => GetConfiguredOptions(settings));
            var expectedMessage = $"FunctionTimeout must be greater than 00:00:01 and less than {TimeSpan.MaxValue}.";
            Assert.Equal(expectedMessage, ex.Message);
        }

        [Fact]
        public void Configure_Default_AppliesFileLoggingMode()
        {
            var settings = new Dictionary<string, string>();
            var options = GetConfiguredOptions(settings);

            Assert.Equal(FileLoggingMode.DebugOnly, options.FileLoggingMode);
        }

        [Theory]
        [InlineData("never", FileLoggingMode.Never)]
        [InlineData("always", FileLoggingMode.Always)]
        [InlineData("debugOnly", FileLoggingMode.DebugOnly)]
        public void ConfigureW_WithConfiguration_AppliesFileLoggingMode(string setting, FileLoggingMode expectedMode)
        {
            var settings = new Dictionary<string, string>
            {
                { ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, ConfigurationSectionNames.Logging, "fileLoggingMode"), setting }
            };

            var options = GetConfiguredOptions(settings);

            Assert.Equal(expectedMode, options.FileLoggingMode);
        }

        private ScriptJobHostOptions GetConfiguredOptions(Dictionary<string, string> settings, IEnvironment environment = null)
        {
            ScriptJobHostOptionsSetup setup = CreateSetupWithConfiguration(settings, environment);

            var options = new ScriptJobHostOptions();

            setup.Configure(options);

            return options;
        }

        private ScriptJobHostOptionsSetup CreateSetupWithConfiguration(Dictionary<string, string> settings = null, IEnvironment environment = null)
        {
            var builder = new ConfigurationBuilder();
            environment = environment ?? SystemEnvironment.Instance;

            if (settings != null)
            {
                builder.AddInMemoryCollection(settings);
            }

            var configuration = builder.Build();

            return new ScriptJobHostOptionsSetup(configuration, environment, new OptionsWrapper<ScriptApplicationHostOptions>(new ScriptApplicationHostOptions()));
        }
    }
}
