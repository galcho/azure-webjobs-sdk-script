﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.ServiceBus;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Script
{
    public class ScriptHost : JobHost
    {
        private const string HostAssemblyName = "ScriptHost";
        private const string HostConfigFileName = "host.json";
        internal const string FunctionConfigFileName = "function.json";
        private readonly AutoResetEvent _restartEvent = new AutoResetEvent(false);
        private Action<FileSystemEventArgs> _restart;
        private FileSystemWatcher _fileWatcher;
        private int _directoryCountSnapshot;
        
        protected ScriptHost(ScriptHostConfiguration scriptConfig) 
            : base(scriptConfig.HostConfig)
        {
            ScriptConfig = scriptConfig;

            if (scriptConfig.FileLoggingEnabled)
            {
                string hostLogFilePath = Path.Combine(scriptConfig.RootLogPath, "Host");
                TraceWriter = new FileTraceWriter(hostLogFilePath, TraceLevel.Verbose);
                scriptConfig.HostConfig.Tracing.Tracers.Add(TraceWriter);
            }
            else
            {
                TraceWriter = NullTraceWriter.Instance;
            }

            if (scriptConfig.TraceWriter != null)
            {
                scriptConfig.HostConfig.Tracing.Tracers.Add(scriptConfig.TraceWriter);
            }
            else
            {
                scriptConfig.TraceWriter = NullTraceWriter.Instance;
            }

            if (scriptConfig.FileWatchingEnabled)
            {
                _fileWatcher = new FileSystemWatcher(scriptConfig.RootScriptPath)
                {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };
                _fileWatcher.Changed += OnFileChanged;
                _fileWatcher.Created += OnFileChanged;
                _fileWatcher.Deleted += OnFileChanged;
                _fileWatcher.Renamed += OnFileChanged;
            }

            // If a file change should result in a restart, we debounce the event to
            // ensure that only a single restart is triggered within a specific time window.
            // This allows us to deal with a large set of file change events that might
            // result from a bulk copy/unzip operation. In such cases, we only want to
            // restart after ALL the operations are complete and there is a quiet period.
            _restart = (e) =>
            {
                TraceWriter.Verbose(string.Format(CultureInfo.InvariantCulture, "File change of type '{0}' detected for '{1}'", e.ChangeType, e.FullPath));
                TraceWriter.Verbose("Host configuration has changed. Signaling restart.");

                // signal host restart
                _restartEvent.Set();
            };
            _restart = _restart.Debounce(500);

            // take a snapshot so we can detect function additions/removals
            _directoryCountSnapshot = Directory.EnumerateDirectories(ScriptConfig.RootScriptPath).Count();
        }

        public TraceWriter TraceWriter { get; private set; }

        public ScriptHostConfiguration ScriptConfig { get; private set; }

        public Collection<FunctionDescriptor> Functions { get; private set; }

        public AutoResetEvent RestartEvent
        {
            get
            {
                return _restartEvent;
            }
        }

        public async Task CallAsync(string method, Dictionary<string, object> arguments, CancellationToken cancellationToken = default(CancellationToken))
        {
            // TODO: Don't hardcode Functions Type name
            // TODO: Validate inputs
            // TODO: Cache this lookup result
            string typeName = "Functions";
            method = method.ToLowerInvariant();
            Type type = ScriptConfig.HostConfig.TypeLocator.GetTypes().SingleOrDefault(p => p.Name == typeName);
            MethodInfo methodInfo = type.GetMethods().SingleOrDefault(p => p.Name.ToLowerInvariant() == method);

            await CallAsync(methodInfo, arguments, cancellationToken);
        }

        protected virtual void Initialize()
        {
            List<FunctionDescriptorProvider> descriptionProviders = new List<FunctionDescriptorProvider>()
            {
                new ScriptFunctionDescriptorProvider(this, ScriptConfig),
                new NodeFunctionDescriptorProvider(this, ScriptConfig)
            };

            if (ScriptConfig.HostConfig.IsDevelopment)
            {
                ScriptConfig.HostConfig.UseDevelopmentSettings();
            }

            // read host.json and apply to JobHostConfiguration
            string hostConfigFilePath = Path.Combine(ScriptConfig.RootScriptPath, HostConfigFileName);

            // If it doesn't exist, create an empty JSON file
            if (!File.Exists(hostConfigFilePath))
            {
                File.WriteAllText(hostConfigFilePath, "{}");
            }

            TraceWriter.Verbose(string.Format(CultureInfo.InvariantCulture, "Reading host configuration file '{0}'", hostConfigFilePath));
            string json = File.ReadAllText(hostConfigFilePath);
            JObject hostConfig = JObject.Parse(json);
            ApplyConfiguration(hostConfig, ScriptConfig);

            // read all script functions and apply to JobHostConfiguration
            Collection<FunctionDescriptor> functions = ReadFunctions(ScriptConfig, descriptionProviders);
            string defaultNamespace = "Host";
            string typeName = string.Format(CultureInfo.InvariantCulture, "{0}.{1}", defaultNamespace, "Functions");
            TraceWriter.Verbose(string.Format(CultureInfo.InvariantCulture, "Generating {0} job function(s)", functions.Count));
            Type type = FunctionGenerator.Generate(HostAssemblyName, typeName, functions);
            List<Type> types = new List<Type>();
            types.Add(type);

            ScriptConfig.HostConfig.TypeLocator = new TypeLocator(types);
            ScriptConfig.HostConfig.NameResolver = new NameResolver();

            Functions = functions;
        }


        public static ScriptHost Create(ScriptHostConfiguration scriptConfig = null)
        {
            if (scriptConfig == null)
            {
                scriptConfig = new ScriptHostConfiguration();
            }

            if (!Path.IsPathRooted(scriptConfig.RootScriptPath))
            {
                scriptConfig.RootScriptPath = Path.Combine(Environment.CurrentDirectory, scriptConfig.RootScriptPath);
            }

            ScriptHost scriptHost = new ScriptHost(scriptConfig);
            try
            {
                scriptHost.Initialize();
            }
            catch (Exception ex)
            {
                scriptHost.TraceWriter.Error("ScriptHost initialization failed", ex);
                throw;
            }

            return scriptHost;
        }

        private static bool TryParseFunctionMetadata(string functionName, JObject jObject, out FunctionMetadata functionMetadata)
        {
            functionMetadata = new FunctionMetadata
            {
                Name = functionName
            };

            JValue triggerDisabledValue = null;
            JObject bindingsObject = (JObject)jObject["bindings"];
            if (bindingsObject != null)
            {
                // parse input bindings
                JArray bindingArray = (JArray)bindingsObject["input"];
                if (bindingArray != null)
                {
                    foreach (JObject binding in bindingArray)
                    {
                        BindingMetadata bindingMetadata = null;
                        if (TryParseBindingMetadata(binding, out bindingMetadata))
                        {
                            functionMetadata.InputBindings.Add(bindingMetadata);
                            if (bindingMetadata.IsTrigger)
                            {
                                triggerDisabledValue = (JValue)binding["disabled"];
                            }
                        }
                    }
                }

                // parse output bindings
                bindingArray = (JArray)bindingsObject["output"];
                if (bindingArray != null)
                {
                    foreach (JObject binding in bindingArray)
                    {
                        BindingMetadata bindingMetadata = null;
                        if (TryParseBindingMetadata(binding, out bindingMetadata))
                        {
                            functionMetadata.OutputBindings.Add(bindingMetadata);
                        }
                    }
                }
            }

            // A function can be disabled at the trigger or function level
            if (IsDisabled(functionName, triggerDisabledValue) ||
                IsDisabled(functionName, (JValue)jObject["disabled"]))
            {
                functionMetadata.IsDisabled = true;
            }

            return true;
        }

        private static bool TryParseBindingMetadata(JObject binding, out BindingMetadata bindingMetadata)
        {
            bindingMetadata = null;
            string bindingTypeValue = (string)binding["type"];
            BindingType bindingType;
            if (!string.IsNullOrEmpty(bindingTypeValue) && Enum.TryParse<BindingType>(bindingTypeValue, true, out bindingType))
            {
                switch (bindingType)
                {
                    case BindingType.QueueTrigger:
                    case BindingType.Queue:
                        bindingMetadata = binding.ToObject<QueueBindingMetadata>();
                        break;
                    case BindingType.BlobTrigger:
                    case BindingType.Blob:
                        bindingMetadata = binding.ToObject<BlobBindingMetadata>();
                        break;
                    case BindingType.ServiceBusTrigger:
                    case BindingType.ServiceBus:
                        bindingMetadata = binding.ToObject<ServiceBusBindingMetadata>();
                        break;
                    case BindingType.HttpTrigger:
                    case BindingType.Http:
                        bindingMetadata = binding.ToObject<HttpBindingMetadata>();
                        break;
                    case BindingType.Table:
                        bindingMetadata = binding.ToObject<TableBindingMetadata>();
                        break;
                    case BindingType.ManualTrigger:
                        bindingMetadata = binding.ToObject<BindingMetadata>();
                        break;
                    case BindingType.TimerTrigger:
                        bindingMetadata = binding.ToObject<TimerBindingMetadata>();
                        break;
                };

                bindingMetadata.Type = bindingType;

                return true;
            }

            return false;
        }

        internal static Collection<FunctionDescriptor> ReadFunctions(ScriptHostConfiguration config, IEnumerable<FunctionDescriptorProvider> descriptionProviders)
        {
            string scriptRootPath = config.RootScriptPath;
            List<FunctionMetadata> metadatas = new List<FunctionMetadata>();
            foreach (var scriptDir in Directory.EnumerateDirectories(scriptRootPath))
            {
                // read the function config
                string functionConfigPath = Path.Combine(scriptDir, FunctionConfigFileName);
                if (!File.Exists(functionConfigPath))
                {
                    // not a function directory
                    continue;
                }

                string json = File.ReadAllText(functionConfigPath);
                JObject jObject = JObject.Parse(json);
                FunctionMetadata metadata = null;

                // unless the name is explicitly set in the config,
                // default it to the function folder name
                string name = (string)jObject["name"];
                if (string.IsNullOrEmpty(name))
                {
                    name = Path.GetFileNameWithoutExtension(scriptDir);
                }

                if (!TryParseFunctionMetadata(name, jObject, out metadata))
                {
                    // TODO: Handle error
                    continue;
                }

                // determine the primary script
                string[] functionFiles = Directory.EnumerateFiles(scriptDir).Where(p => Path.GetFileName(p).ToLowerInvariant() != FunctionConfigFileName).ToArray();
                if (functionFiles.Length == 0)
                {
                    continue;
                }
                else if (functionFiles.Length == 1)
                {
                    // if there is only a single file, that file is primary
                    metadata.Source = functionFiles[0];
                }
                else
                {
                    // if there is a "run" file, that file is primary
                    string functionPrimary = null;
                    functionPrimary = functionFiles.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).ToLowerInvariant() == "run");
                    if (string.IsNullOrEmpty(functionPrimary))
                    {
                        // for Node, any index.js file is primary
                        functionPrimary = functionFiles.FirstOrDefault(p => Path.GetFileName(p).ToLowerInvariant() == "index.js");
                        if (string.IsNullOrEmpty(functionPrimary))
                        {
                            // finally, if there is an explicit primary file indicated
                            // in config, use it
                            JToken token = jObject["source"];
                            if (token != null)
                            {
                                string sourceFileName = (string)token;
                                functionPrimary = Path.Combine(scriptDir, sourceFileName);
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(functionPrimary))
                    {
                        // TODO: should this be an error?
                        continue;
                    }
                    metadata.Source = functionPrimary;
                }

                metadatas.Add(metadata);
            }

            var functions = ReadFunctions(metadatas, descriptionProviders);
            return functions;
        }

        internal static Collection<FunctionDescriptor> ReadFunctions(List<FunctionMetadata> metadatas, IEnumerable<FunctionDescriptorProvider> descriptorProviders)
        {
            Collection<FunctionDescriptor> functionDescriptors = new Collection<FunctionDescriptor>();
            foreach (FunctionMetadata metadata in metadatas)
            {
                FunctionDescriptor descriptor = null;
                foreach (var provider in descriptorProviders)
                {
                    if (provider.TryCreate(metadata, out descriptor))
                    {
                        break;
                    }
                }

                if (descriptor != null)
                {
                    functionDescriptors.Add(descriptor);
                }
            }

            return functionDescriptors;
        }

        internal static void ApplyConfiguration(JObject config, ScriptHostConfiguration scriptConfig)
        {
            JobHostConfiguration hostConfig = scriptConfig.HostConfig;

            // We may already have a host id, but the one from the JSON takes precedence
            JToken hostId = (JToken)config["id"];
            if (hostId != null)
            {
                hostConfig.HostId = (string)hostId;
            }
            else if (hostConfig.HostId == null)
            {
                throw new InvalidOperationException("An 'id' must be specified in the host configuration.");
            }

            JToken watchFiles = (JToken)config["watchFiles"];
            if (watchFiles != null && watchFiles.Type == JTokenType.Boolean)
            {
                scriptConfig.FileWatchingEnabled = (bool)watchFiles;
            }

            // Apply Queues configuration
            JObject configSection = (JObject)config["queues"];
            JToken value = null;
            if (configSection != null)
            {
                if (configSection.TryGetValue("maxPollingInterval", out value))
                {
                    hostConfig.Queues.MaxPollingInterval = TimeSpan.FromMilliseconds((int)value);
                }
                if (configSection.TryGetValue("batchSize", out value))
                {
                    hostConfig.Queues.BatchSize = (int)value;
                }
                if (configSection.TryGetValue("maxDequeueCount", out value))
                {
                    hostConfig.Queues.MaxDequeueCount = (int)value;
                }
                if (configSection.TryGetValue("newBatchThreshold", out value))
                {
                    hostConfig.Queues.NewBatchThreshold = (int)value;
                }
            }

            // Apply Singleton configuration
            configSection = (JObject)config["singleton"];
            value = null;
            if (configSection != null)
            {
                if (configSection.TryGetValue("lockPeriod", out value))
                {
                    hostConfig.Singleton.LockPeriod = TimeSpan.Parse((string)value, CultureInfo.InvariantCulture);
                }
                if (configSection.TryGetValue("listenerLockPeriod", out value))
                {
                    hostConfig.Singleton.ListenerLockPeriod = TimeSpan.Parse((string)value, CultureInfo.InvariantCulture);
                }
                if (configSection.TryGetValue("listenerLockRecoveryPollingInterval", out value))
                {
                    hostConfig.Singleton.ListenerLockRecoveryPollingInterval = TimeSpan.Parse((string)value, CultureInfo.InvariantCulture);
                }
                if (configSection.TryGetValue("lockAcquisitionTimeout", out value))
                {
                    hostConfig.Singleton.LockAcquisitionTimeout = TimeSpan.Parse((string)value, CultureInfo.InvariantCulture);
                }
                if (configSection.TryGetValue("lockAcquisitionPollingInterval", out value))
                {
                    hostConfig.Singleton.LockAcquisitionPollingInterval = TimeSpan.Parse((string)value, CultureInfo.InvariantCulture);
                }
            }

            // Apply ServiceBus configuration
            ServiceBusConfiguration sbConfig = new ServiceBusConfiguration();
            configSection = (JObject)config["serviceBus"];
            value = null;
            if (configSection != null)
            {
                if (configSection.TryGetValue("maxConcurrentCalls", out value))
                {
                    sbConfig.MessageOptions.MaxConcurrentCalls = (int)value;
                }
            }
            hostConfig.UseServiceBus(sbConfig);

            // Apply Tracing configuration
            configSection = (JObject)config["tracing"];
            if (configSection != null && configSection.TryGetValue("consoleLevel", out value))
            {
                TraceLevel consoleLevel;
                if (Enum.TryParse<TraceLevel>((string)value, true, out consoleLevel))
                {
                    hostConfig.Tracing.ConsoleLevel = consoleLevel;
                }
            }

            hostConfig.UseTimers();
            hostConfig.UseCore();
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            string fileName = Path.GetFileName(e.Name);

            if (((string.Compare(fileName, HostConfigFileName, StringComparison.OrdinalIgnoreCase) == 0) || string.Compare(fileName, FunctionConfigFileName, StringComparison.OrdinalIgnoreCase) == 0) ||
                ((Directory.EnumerateDirectories(ScriptConfig.RootScriptPath).Count() != _directoryCountSnapshot)))
            {
                // a host level configuration change has been made which requires a
                // host restart
                _restart(e);
            }
        }

        private static bool IsDisabled(string functionName, JValue disabledValue)
        {
            if (disabledValue != null && IsDisabled(disabledValue))
            {
                // TODO: this needs to be written to the TraceWriter, not
                // Console
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Function '{0}' is disabled", functionName));
                return true;
            }

            return false;
        }

        private static bool IsDisabled(JToken isDisabledValue)
        {
            if (isDisabledValue != null)
            {
                if (isDisabledValue.Type == JTokenType.Boolean && (bool)isDisabledValue)
                {
                    return true;
                }
                else
                {
                    string settingName = (string)isDisabledValue;
                    string value = Environment.GetEnvironmentVariable(settingName);
                    if (!string.IsNullOrEmpty(value) &&
                        (string.Compare(value, "1", StringComparison.OrdinalIgnoreCase) == 0 ||
                         string.Compare(value, "true", StringComparison.OrdinalIgnoreCase) == 0))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                if (_fileWatcher != null)
                {
                    _fileWatcher.Dispose();
                }

                _restartEvent.Dispose();
            }
        }
    }
}
